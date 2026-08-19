// ─────────────────────────────────────────────────────────────
// 💾 SagaStateDbContext — EF Core Saga 状态存储（PrimitiveCollection + 并发令牌）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// EF Core Saga 状态持久化
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core Saga 状态存储基础上下文。</summary>
/// <typeparam name="TState">Saga 状态类型</typeparam>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("Trimming", "IL2091:Target generic argument does not satisfy member access requirements",
    Justification = "EF Core model construction requires broad member access for saga state entities in the optional EFCore adapter package.")]
public abstract class SagaStateDbContext<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.NonPublicConstructors
        | DynamicallyAccessedMemberTypes.PublicFields
        | DynamicallyAccessedMemberTypes.NonPublicFields
        | DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.NonPublicProperties
        | DynamicallyAccessedMemberTypes.Interfaces)]
TState>(DbContextOptions options) : DbContext(options), ISagaStateStore<TState>
    where TState : SagaState
{
    /// <summary>Saga 状态表</summary>
    public DbSet<TState> SagaStates => Set<TState>();

    /// <summary>
    /// 当前 UTC 时间（P3 修复：时钟双轨清零）——虚方法模式与 OutboxDbContext.GetUtcNow 对齐，
    /// 测试子类可覆写注入 FakeTimeProvider。
    /// </summary>
    protected virtual DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // 优化（二十五轮 API 扫描 EF-6）：AsNoTracking——只读契约（SagaProcessor 走
        // LeaseActiveSagasAsync 独立租约路径，本查询仅观测，保证不进 Mark*+SaveChanges）；
        // 违反契约的突变将静默丢失（非跟踪实体不经 SaveChangesAsync 持久化）。
        return await SagaStates
            .AsNoTracking()
            // 三十四轮（中断态超时兜底）：观测查询与 Lease 同步纳入 AwaitingHumanDecision
            .Where(s => s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision)
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var now = GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);
        var states = await SagaStates
            // 三十四轮（中断态超时兜底）：扫描集扩 AwaitingHumanDecision——中断态 Saga
            // 配置了步骤 Timeout 且超期时由 SagaTimeoutProcessor.IsTimedOut 门控补偿；
            // 未配置 Timeout 则 IsTimedOut 恒 false（显式无限等待契约）
            .Where(s => (s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision)
                && (s.LeasedUntil == null || s.LeasedUntil <= now))
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var state in states)
        {
            state.LeasedBy = owner;
            state.LeasedUntil = leasedUntil;
        }

        try
        {
            await SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // ITM-066：多实例同批租约互撞（Version 令牌）。SaveChanges 是原子操作，
            // 抛出即本轮无任何写入——视为"未获取租约"返回空，下轮重试，
            // 避免整轮 tick 失败退化为撞租约轮盘。跨方言 FOR UPDATE SKIP LOCKED
            // 单语句原子租约改造（SQLite 不支持）需后续 ADR。
            foreach (var state in states)
                Entry(state).State = EntityState.Detached;
            return [];
        }
        return states;
    }

    /// <inheritdoc/>
    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
        => await SagaStates.SingleOrDefaultAsync(s => s.SagaId == sagaId, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// EF Core 版：DbContext 变更跟踪自动检测修改，state 参数可选。
    /// ITM-072：对齐 <see cref="ISagaStateStore{TState}.SaveChangesAsync"/> 契约——
    /// 原实现直接透传 <c>await SaveChangesAsync(ct)</c> 返回"实际写入实体数"：
    /// 无变更保存返回 0（被调用方误判为乐观锁冲突）、多实体保存返回 N&gt;1（契约只定义 0/1）。
    /// 现改为：保存成功且无并发冲突 → 1（写入生效）；DbUpdateConcurrencyException → 分离实体返回 0
    /// （对齐 Dapper/PalORM 的受影响行数语义——冲突时返回 0 而非抛异常）。
    /// </remarks>
    async ValueTask<int> ISagaStateStore<TState>.SaveChangesAsync(TState state, CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
            return 1;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 乐观锁冲突（他实例已写同一 Saga）——契约要求返回 0 而非上抛，
            // 调用方（SagaProcessor）据此判定内存快照作废并记 Warning。
            // 验证轮返工：仅当 state 已跟踪时才 detach——Entry() 对未跟踪实体
            // 会静默把它挂为 Unchanged（污染 ChangeTracker），且并发冲突可能来自
            // 其他被跟踪实体；未跟踪时无需任何清理。
            if (ChangeTracker.Entries<TState>().Any(e => ReferenceEquals(e.Entity, state)))
                Entry(state).State = EntityState.Detached;
            return 0;
        }
    }

    /// <summary>
    /// P2 修复（八轮评审）：<see cref="SagaState.Version"/> 并发令牌此前从不递增——
    /// EF 用 original value 生成 <c>WHERE Version=orig</c>，而 orig 永不前进 → 恒匹配，
    /// <see cref="DbUpdateConcurrencyException"/> 保护不可达（乐观锁失效）。
    /// 此处对 Modified 状态的 Saga 实体在提交前递增 current value：EF 把新值写入 SET、
    /// 原值留在 WHERE，成功后内存与 DB 同步 +1——对齐 DapperSagaStateStore（SQL 内
    /// version=version+1）与 PalOrmSagaStateStore（UPDATE 后 state.Version++）。
    /// ⚠️ 递增必须在提交前：提交后递增会使 SET 不含 Version（DB 不前进）而内存前进，
    /// 下一次保存 WHERE 永不匹配。租约路径（只改 LeasedBy/LeasedUntil）同样经此
    /// 递增受益——并发租约互撞现在会真实抛并发异常，由 LeaseActiveSagasAsync 捕获降级。
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpVersionOnModifiedSagaStates();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc cref="SaveChanges(bool)"/>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpVersionOnModifiedSagaStates();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>提交前递增所有 Modified 状态 Saga 实体的 Version（见 <see cref="SaveChanges(bool)"/> 注释）。</summary>
    private void BumpVersionOnModifiedSagaStates()
    {
        foreach (var entry in ChangeTracker.Entries<TState>())
        {
            if (entry.State == EntityState.Modified)
                entry.Property(static s => s.Version).CurrentValue++;
        }
    }

    /// <summary>配置 Saga 状态实体规则</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TState>(e =>
        {
            e.HasKey(x => x.SagaId);
            // P2 修复（八轮评审·集群 T 新发现）：Ulid 主键需显式转换——关系型 provider 无
            // Ulid 原生映射，缺转换时 SaveChanges 抛"无法映射类型"（对齐 OutboxDbContext.Id 模式）
            e.Property(x => x.SagaId).HasConversion(v => v.ToString(), v => PalUlid.Parse(v));
            e.HasIndex(x => new { x.Status, x.CurrentState });
            e.HasIndex(x => new { x.Status, x.LeasedUntil, x.CreatedAt });
            e.Property(x => x.CurrentState).HasMaxLength(256);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Error).HasMaxLength(2048);
            e.Property(x => x.LeasedBy).HasMaxLength(256);

            // Dictionary<K,V> → JSON column（EF Core 不支持 PrimitiveCollection for Dictionary）
            e.Property(x => x.StepStartedAt)
                .HasConversion(
                    static value => JsonSerializer.Serialize(value, SagaStateJsonContext.Default.DictionaryStringDateTimeOffset),
                    static value => JsonSerializer.Deserialize(value, SagaStateJsonContext.Default.DictionaryStringDateTimeOffset)
                        ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal))
                .Metadata.SetValueComparer(StepStartedAtComparer);

            // Collection<string> → PrimitiveCollection（EF Core 11 原生 JSON 列支持）
            // 替代了手写 HasConversion + ValueComparer，由 EF Core 自动管理变更追踪
            e.PrimitiveCollection(x => x.ExecutedStepKeys).ElementType().HasMaxLength(256);
        });
    }

    // ─────────────────────────────────────────────────────────────
    // StepStartedAt 的 Dictionary ValueComparer（EF Core 无原生支持）
    // ─────────────────────────────────────────────────────────────

    private static readonly ValueComparer<Dictionary<string, DateTimeOffset>> StepStartedAtComparer = new(
        static (left, right) => DictionaryEquals(left, right),
        static value => DictionaryHashCode(value),
        static value => CloneDictionary(value));

    private static bool DictionaryEquals(
        Dictionary<string, DateTimeOffset>? left,
        Dictionary<string, DateTimeOffset>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) || other != value) return false;
        return true;
    }

    private static int DictionaryHashCode(Dictionary<string, DateTimeOffset>? value)
    {
        var hash = new HashCode();
        if (value is not null)
            foreach (var item in value.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                hash.Add(item.Key, StringComparer.Ordinal);
                hash.Add(item.Value);
            }
        return hash.ToHashCode();
    }

    private static Dictionary<string, DateTimeOffset> CloneDictionary(Dictionary<string, DateTimeOffset>? value)
        => value is null
            ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(value, StringComparer.Ordinal);
}

[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
internal sealed partial class SagaStateJsonContext : JsonSerializerContext;
