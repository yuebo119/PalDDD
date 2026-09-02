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
            .OrderBy(s => s.SagaId) // v23 C1：EF SQLite 不支持 DateTimeOffset ORDER BY（ITM-261 姊妹）——改按 Id（ULID 字典序=创建序）
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
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
        // v25 P3 守卫族：leaseDuration 边界守卫——对照 Outbox 四方言 LeasePendingMessagesAsync
        //（MySql :77-85 / PG :56-62 / Sqlite :82-89，ITM-167/216 对齐系列）同型漏网——
        // leaseDuration 非正时租约即刻过期/永不过期语义错乱；TotalSeconds 超过 int.MaxValue 时
        // leasedUntil = now.Add(leaseDuration) 的秒数语义溢出。Options 层已校验正数，
        // 此处是 Store 直调路径的防御性 fail-fast（与 Options 层校验各自覆盖
        // DI 启动期与运行时直调两类入口）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LeasedUntil value.");

        var now = GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);
        // v25 P2-1：翻页循环防饥饿（镜像 SqliteOutboxDbContext.QueryEligibleAsync 三十九轮形态）——
        // v23 C1 把 LeasedUntil 过滤移到内存后，头部 batchSize 条全为他实例活跃租约时
        // 单页 Take 每 tick 租 0 条（SagaId 序=创建序，老 Saga 恒占头部）；循环翻页
        // 直至填满 batchSize 或耗尽候选。
        var states = new List<TState>(batchSize);
        var skip = 0;
        while (states.Count < batchSize)
        {
            var page = await SagaStates
            // 三十四轮（中断态超时兜底）：扫描集扩 AwaitingHumanDecision——中断态 Saga
            // 配置了步骤 Timeout 且超期时由 SagaTimeoutProcessor.IsTimedOut 门控补偿；
            // v44 勘正（对齐 InMemorySagaStateStore v43 勘正）：IsTimedOut 判据已含
            // "AWD 态已成功步骤"排除——残留时间戳不再触发中断态兜底补偿
            // v23 C1：EF SQLite 不支持 DateTimeOffset 有序比较/排序（ITM-261 姊妹）——
            // 等值比较（Status）可翻译，LeasedUntil <= now 改物化后内存过滤，OrderBy 改 SagaId
            .Where(s => s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision)
            .OrderBy(s => s.SagaId)
            .Skip(skip).Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
            if (page.Count == 0) break;
            // v27 P3 修复：翻页循环内对未选中实体 Detach——page 为 tracked 物化（LeasedUntil
            // 过滤在内存做），未被租约选中的实体（Unchanged）滞留 ChangeTracker 逐 tick 累积
            // （长驻 SagaProcessor 的 DbContext 内存膨胀）；选中项保持跟踪，供下方
            // LeasedBy/LeasedUntil 变异 + SaveChanges 持久化。单次遍历分支替代
            // AddRange + Contains 回查（O(n) 而非 O(n²)，语义等价）
            foreach (var s in page)
            {
                if (s.LeasedUntil is null || s.LeasedUntil <= now) states.Add(s);
                else Entry(s).State = EntityState.Detached;
            }
            skip += batchSize;
        }
        // v28 P3 修复：超额裁剪前先 Detach 被裁实体——RemoveRange 只从列表移除不清跟踪
        // 状态，被裁实体仍为 tracked（Unchanged）滞留 ChangeTracker，长驻 SagaProcessor 下
        // 逐 tick 累积（同方法 v27 P3 对循环内未选中实体的 Detach，此处是循环结束后
        // 超出 batchSize 的尾部；随后 foreach 只变异保留项，被裁项再无释放机会）
        if (states.Count > batchSize)
        {
            for (var i = batchSize; i < states.Count; i++)
                Entry(states[i]).State = EntityState.Detached;
            states.RemoveRange(batchSize, states.Count - batchSize);
        }

        foreach (var state in states)
        {
            state.LeasedBy = owner;
            state.LeasedUntil = leasedUntil;
        }

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
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
        catch (DbUpdateException)
        {
            // v26 P2 修复：非并发瞬时故障（连接闪断/超时）上抛前全批 Detach——states 已被
            // 变异为 Modified（LeasedBy/LeasedUntil + BumpVersion），滞留 ChangeTracker 会被
            // 下次无关 SaveChanges 提交为从未成功获取的幽灵租约（WHERE Version=orig 匹配
            // DB 真值必成功），无主锁死这批 Saga 至 LeaseDuration 过期——镜像
            // IdempotencyDbContext 三十八轮 P2 全修样板
            foreach (var state in states)
                Entry(state).State = EntityState.Detached;
            throw;
        }
        return states;
    }

    /// <inheritdoc/>
    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
        => await SagaStates.SingleOrDefaultAsync(s => s.SagaId == sagaId, ct).ConfigureAwait(false);

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
        // v29 P3（S10，镜像 v28 DapperSagaStateStore / v29 PalOrmSagaStateStore 的 Q1 形态）：
        // 存储层截断兜底——Error 列 HasMaxLength(2048)（本类 OnModelCreating），超长 ex.Message
        //（含大 payload 的序列化错误）会让终态保存本身抛 DbUpdateException 掩盖原始异常
        //（ITM-167/175 截断族）；截断到 2040 对齐跨栈（Dapper/PalORM 同款 2040，2048 列上限
        // 的安全余量）。EF Core 是变更跟踪写入（Error 赋值在调用方），入口截断 state.Error
        // 后本次 SaveChanges 随即生效。经 Core.FailureReason.Truncate：仅截断不归一
        //（Error=null 是"未出错"语义，Normalize 会归一为 "(no message)" 破坏之），
        // 含 UTF-16 代理对守卫（末位高代理回退一位）。
        state.Error = Core.FailureReason.Truncate(state.Error, 2040);
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            return 1;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 乐观锁冲突（他实例已写同一 Saga）——契约要求返回 0 而非上抛，
            // 调用方（SagaProcessor）据此判定内存快照作废并记 Warning。
            // 验证轮返工：仅当 state 已跟踪时才 detach——对未跟踪实体调 Entry(state)
            // 读取状态不附加（EF Core：仅 State 赋值才附加；R41 ITM-271 勘正原"静默挂为
            // Unchanged"的失实描述），且并发冲突可能来自其他被跟踪实体；未跟踪时无需清理。
            if (ChangeTracker.Entries<TState>().Any(e => ReferenceEquals(e.Entity, state)))
                Entry(state).State = EntityState.Detached;
            return 0;
        }
        catch (DbUpdateException)
        {
            // v27 P2 修复：非并发瞬时故障（连接闪断/超时）上抛前 Detach——state 可能已被
            // 调用方变异为 Modified（Version 已被 BumpVersion 递增），滞留 ChangeTracker 会被
            // 同 scope 下一条 Saga 的 SaveChangesAsync 幽灵提交或抛并发异常污染后续批处理
            // （SagaProcessor foreach 吞异常继续）——镜像 v26 Lease 路径与 IdempotencyDbContext
            // 三十八轮全修样板
            if (ChangeTracker.Entries<TState>().Any(e => ReferenceEquals(e.Entity, state)))
                Entry(state).State = EntityState.Detached;
            throw;
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
