// ─────────────────────────────────────────────────────────────
// 💾 SagaStateDbContext — EF Core Saga 状态存储（PrimitiveCollection + 并发令牌）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
TState>(DbContextOptions options) : DbContext(options), ISagaStateStore<TState>, ISagaLeaseBatchStore<TState>, IDueTimeSagaStore<TState>
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
    public ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
        => LeaseSagasAsync(owner, leaseDuration, batchSize, dueBefore: null, GetUtcNow(), ct);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TState>> LeaseDueSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        DateTimeOffset now,
        CancellationToken ct)
        => LeaseSagasAsync(owner, leaseDuration, batchSize, dueBefore: now, now, ct);

    private async ValueTask<IReadOnlyList<TState>> LeaseSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        DateTimeOffset? dueBefore,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // v25 P3 守卫族：leaseDuration 边界守卫——对照 Outbox 四方言 LeasePendingMessagesAsync
        //（MySql :77-85 / PG :56-62 / Sqlite :82-89，ITM-167/216 对齐系列）同型漏网——
        // leaseDuration 非正时租约即刻过期/永不过期语义错乱；TotalSeconds 超过 int.MaxValue
        // 时 leasedUntil = now.Add(leaseDuration) 的秒数语义溢出。Options 层已校验正数，
        // 此处是 Store 直调路径的防御性 fail-fast（与 Options 层校验各自覆盖
        // DI 启动期与运行时直调两类入口）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LeasedUntil value.");

        var leasedUntil = now.Add(leaseDuration);

        // R7（EPSL 地毯审计 2026-10-11 P0-4，2026-10-15 性能实施轮）：SQLite 单语句租约下推——
        // v23 C1 起的"翻页物化 + 内存过滤 + 逐行 UPDATE"退化根因是 ITM-261（EF SQLite LINQ
        // 不能翻译 DateTimeOffset 有序比较），而手写 SQL 不受其限（decision-2026-09-17
        // shape 2 已在 SqliteOutboxDbContext 落地同族单语句租约，本案是其 Saga 面延续）。
        // 本路径固定 2 条语句（集合式 UPDATE + 等值守卫回读）与批次大小无关（旧形态：一页
        // 查询 + N 条逐行 UPDATE，且头部被活跃租约占满时叠加 O(跳过页) 次翻页往返）。
        // 非 SQLite provider（含 InMemory）保底走下方跟踪分页路径；PG/MySQL/SQL Server 的
        // 等价下推待 EF 真库车道（上游 provider 未发布 rc 线版本——既有登记，见
        // docs/review/decision-2026-10-15-saga-lease-sqlite-single-statement.md）。
        if (UsesSingleStatementSqliteLease())
            return await LeaseViaSingleStatementSqliteAsync(owner, now, leasedUntil, batchSize, dueBefore, ct).ConfigureAwait(false);

        // v25 P2-1：翻页循环防饥饿（镜像 SqliteOutboxDbContext.QueryEligibleAsync 三十九轮形态）——
        // v23 C1 把 LeasedUntil 过滤移到内存后，头部 batchSize 条全为他实例活跃租约时
        // 单页 Take 每 tick 租 0 条（SagaId 序=创建序，老 Saga 恒占头部）；循环翻页
        // 直至填满 batchSize 或耗尽候选。
        var states = new List<TState>(batchSize);
        var skip = 0;
        while (states.Count < batchSize)
        {
            var query = SagaStates
            // 三十四轮（中断态超时兜底）：扫描集扩 AwaitingHumanDecision——中断态 Saga
            // 配置了步骤 Timeout 且超期时由 SagaTimeoutProcessor.IsTimedOut 门控补偿；
            // v44 勘正（对齐 InMemorySagaStateStore v43 勘正）：IsTimedOut 判据已含
            // "AWD 态已成功步骤"排除——残留时间戳不再触发中断态兜底补偿
            // v23 C1：EF SQLite 不支持 DateTimeOffset 有序比较/排序（ITM-261 姊妹）——
            // 等值比较（Status）可翻译，LeasedUntil <= now 改物化后内存过滤，OrderBy 改 SagaId
            .Where(s => s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision);
            if (dueBefore is { } due)
            {
                query = query
                    .Where(s => s.NextCheckAt != null && s.NextCheckAt <= due)
                    .OrderBy(s => s.NextCheckAt)
                    .ThenBy(s => s.SagaId);
            }
            else
            {
                query = query.OrderBy(s => s.SagaId);
            }

            var page = await query
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
            // 避免整轮 tick 失败退化为撞租约轮盘。单语句原子租约改造沿革：SQLite 已由
            // R7（2026-10-15）落地（见 UsesSingleStatementSqliteLease 分支）；本兜底
            // 服务于保底路径（PG/MySQL/SQL Server 待真库车道，InMemory 无并发语义）。
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
        catch
        {
            // ITM-632：保存失败（含 OCE 与非 DbUpdate 异常）逃逸前全批 Detach——states 已被
            // 变异为 Modified（LeasedBy/LeasedUntil + BumpVersion），异常逃逸后滞留
            // ChangeTracker 会被下次无关 SaveChanges 提交为从未成功获取的幽灵租约
            //（WHERE Version=orig 匹配 DB 真值必成功），无主锁死这批 Saga 至 LeaseDuration
            // 过期。与上方 DbUpdateException 分支同款 Detach 手法；异常（含 OCE）原样上抛，
            // 不吞取消、不转成功语义。
            foreach (var state in states)
                Entry(state).State = EntityState.Detached;
            throw;
        }
        return states;
    }

    /// <summary>
    /// 单语句租约启用判定（R7）：仅 SQLite provider——按 <c>Database.ProviderName</c> 精确匹配
    ///（Microsoft.EntityFrameworkCore.Sqlite 注册的 provider 名），不引具体 provider 包
    ///（本适配包 csproj 零 provider 依赖）。SQL 形态（UPDATE…WHERE IN(SELECT…ORDER BY LIMIT)）
    /// 为 SQLite 语法；其它方言的等价形态（PG 同族 / MySQL JOIN 两步）待真库车道开线后加。
    /// </summary>
    private bool UsesSingleStatementSqliteLease()
        => Database.IsRelational()
            && string.Equals(Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);

    /// <summary>
    /// SQLite 单语句租约（R7）：集合式 UPDATE + 等值守卫回读。语义与跟踪分页路径逐项对齐——
    /// 资格（Status ∈ {Active, AwaitingHumanDecision} 且 LeasedUntil 空/到期）、租约即换代
    ///（Version 同语句 +1，对齐 v53 P2 四栈契约）、批次上限、SagaId 序；差异：原子性由单条
    /// SQL 承担（无部分写入，无需 ITM-066/632 的 Detach 兜底——本路径不经 ChangeTracker）。
    /// </summary>
    private async ValueTask<IReadOnlyList<TState>> LeaseViaSingleStatementSqliteAsync(
        string owner,
        DateTimeOffset now,
        DateTimeOffset leasedUntil,
        int batchSize,
        DateTimeOffset? dueBefore,
        CancellationToken ct)
    {
        // 资格谓词在子查询内评估（与 Dapper/PalORM/Outbox 三栈同契约形态），ORDER BY + LIMIT
        // 作用于过滤后集合——v25 P2-1 的"头部活跃租约占满窗口致饥饿"在谓词下推下结构性不成立
        await Database.ExecuteSqlAsync(
            BuildSingleStatementLeaseCommand(owner, now, leasedUntil, batchSize, dueBefore), ct).ConfigureAwait(false);

        // 等值守卫回读（不重跑资格子查询）：按精确租约标识 (owner, until) 取回本批——同一 owner
        // 同 tick 两次租约混批属姊妹栈既有已知限制（PalORM/EF-MySql 同款声明在案）。
        // ⚠️ 保持跟踪（非 AsNoTracking）：租约返回实体的消费方（SagaTimeoutProcessor 补偿路径）
        // 经 ISagaStateStore.SaveChangesAsync(state) 直存依赖 ChangeTracker（Detached→0 契约，
        // 见本类显式实现）；Version 已在 SQL 内换代，物化值即 DB 真值（Unchanged），后续变更
        // 保存的 WHERE Version=orig 与新行值匹配
        return await SagaStates
            .Where(s => s.LeasedBy == owner
                && s.LeasedUntil == leasedUntil
                && (s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision))
            .OrderBy(s => s.SagaId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>构造单语句租约命令（R7，SQLite 形态）——标识符取自 EF 模型映射（用户级
    /// HasColumnName/ToTable 重映射同样生效；缺映射即异常而非静默回落属性名），值全部经
    /// FormattableStringFactory 参数化（无字面量拼接注入面）。SQL 形态对齐
    /// SqliteOutboxDbContext.LeasePendingMessagesAsync（decision-2026-09-17 shape 2 族）
    /// 与 Dapper/PalORM 的 SagaLeaseActive。</summary>
    private FormattableString BuildSingleStatementLeaseCommand(
        string owner,
        DateTimeOffset now,
        DateTimeOffset leasedUntil,
        int batchSize,
        DateTimeOffset? dueBefore)
    {
        var entityType = Model.FindEntityType(typeof(TState))
            ?? throw new InvalidOperationException($"Entity type '{typeof(TState).FullName}' is not mapped in this model.");
        var table = QuoteSqliteIdentifier(entityType.GetTableName()
            ?? throw new InvalidOperationException($"Entity type '{typeof(TState).FullName}' has no table mapping."));
        if (entityType.GetSchema() is { } schema)
            table = QuoteSqliteIdentifier(schema) + "." + table;

        var sagaIdColumn = QuoteSqliteIdentifier(ColumnName(entityType, nameof(SagaState.SagaId)));
        var statusColumn = QuoteSqliteIdentifier(ColumnName(entityType, nameof(SagaState.Status)));
        var leasedByColumn = QuoteSqliteIdentifier(ColumnName(entityType, nameof(SagaState.LeasedBy)));
        var leasedUntilColumn = QuoteSqliteIdentifier(ColumnName(entityType, nameof(SagaState.LeasedUntil)));
        var versionColumn = QuoteSqliteIdentifier(ColumnName(entityType, nameof(SagaState.Version)));
        var nextCheckAtColumn = QuoteSqliteIdentifier(ColumnName(entityType, nameof(SagaState.NextCheckAt)));

        // 到期过滤（M1）仅在 LeaseDueSagasAsync 路径追加；空串保持旧 SQL 形状。
        var dueClause = dueBefore is null
            ? string.Empty
            : $"AND ({nextCheckAtColumn} IS NOT NULL AND {nextCheckAtColumn} <= {{6}})";

        // {{…}} 是标识符插值（表/列名为受信标识符，已加引号）；{N} 是 FormattableStringFactory
        // 占位符（值全部参数化）。DateTimeOffset 参数直传原生形态。
        var format = $$"""
            UPDATE {{table}}
            SET {{leasedByColumn}} = {0}, {{leasedUntilColumn}} = {1}, {{versionColumn}} = {{versionColumn}} + 1
            WHERE {{sagaIdColumn}} IN (
                SELECT {{sagaIdColumn}} FROM {{table}}
                WHERE {{statusColumn}} IN ({2}, {3})
                  AND ({{leasedUntilColumn}} IS NULL OR {{leasedUntilColumn}} <= {4})
                  {{dueClause}}
                ORDER BY {{sagaIdColumn}}
                LIMIT {5})
            """;
        return dueBefore is { } due
            ? FormattableStringFactory.Create(
                format, owner, leasedUntil, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, batchSize, due)
            : FormattableStringFactory.Create(
                format, owner, leasedUntil, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, batchSize);
    }

    /// <summary>列名解析（EF 模型映射）——缺映射即异常而非静默回落属性名（重映射下会生成错误 SQL）。</summary>
    private static string ColumnName(IEntityType entityType, string propertyName)
        => entityType.FindProperty(propertyName)?.GetColumnName()
            ?? throw new InvalidOperationException($"Property '{propertyName}' has no column mapping in the model.");

    /// <summary>SQL 标识符加引号（双引号 + 双引号转义——SQLite/PG 标准形态；MySQL 反引号与
    /// SQL Server 方括号待对应车道开线日加入）。</summary>
    private static string QuoteSqliteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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
        // F2 勘正（第五十四轮片4，2026-09-19）：补 ITM-163 null state 守卫——原实现
        // null 时抛 NRE（首行即 state.Error 解引用），Dapper/PalORM 均抛 ArgumentNull；
        // PalOrmSagaStateStore.cs:183 的三方对齐注释声称含本类，实为失实声明
        ArgumentNullException.ThrowIfNull(state);

        // v29 P3（S10，镜像 v28 DapperSagaStateStore / v29 PalOrmSagaStateStore 的 Q1 形态）：
        // 存储层截断兜底——Error 列 HasMaxLength(2048)（本类 OnModelCreating），超长 ex.Message
        //（含大 payload 的序列化错误）会让终态保存本身抛 DbUpdateException 掩盖原始异常
        //（ITM-167/175 截断族）；截断到 2040 对齐跨栈（Dapper/PalORM 同款 2040，2048 列上限
        // 的安全余量）。EF Core 是变更跟踪写入（Error 赋值在调用方），入口截断 state.Error
        // 后本次 SaveChanges 随即生效。经 Core.FailureReason.Truncate：仅截断不归一
        //（Error=null 是"未出错"语义，Normalize 会归一为 "(no message)" 破坏之），
        // 含 UTF-16 代理对守卫（末位高代理回退一位）。
        state.Error = Core.FailureReason.Truncate(state.Error, 2040);

        // F3 修复（第五十四轮片4，2026-09-19）：未跟踪的新实体（调用方未 Add/Attach）在
        // ChangeTracker 中无变更 → SaveChanges 零写入却返回 1，谎报「写入生效」（违反
        // ISagaStateStore :44-46 契约「1=写入生效；0=目标行不存在」）。对齐契约：Detached
        // 状态直接返回 0（Dapper/PalORM 的自查 UPSERT 对新行真 INSERT，等价语义是
        // 「未见跟踪中的行 = 未写入」——正常管线 SagaProcessor 只保存已加载实体不受影响）
        if (Entry(state).State == EntityState.Detached)
            return 0;

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
        catch
        {
            // ITM-632：保存失败（含 OCE 与非 DbUpdate 异常）逃逸前 Detach——state 已被调用方
            // 变异为 Modified（含 Version 递增），异常逃逸后滞留 ChangeTracker 会被同 scope
            // 后续 SaveChanges 幽灵提交为未生效的终态；与上方 DbUpdateException 分支同款
            // 受跟踪判定手法；异常（含 OCE）原样上抛，不吞取消、不改语义。
            if (ChangeTracker.Entries<TState>().Any(e => ReferenceEquals(e.Entity, state)))
                Entry(state).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// EPSL 地毯审计 2026-10-11 B-1：ExecuteUpdateAsync 单语句批量释放——绕过变更跟踪器
    ///（免逐实体 DetectChanges 的 O(跟踪数) 扫描与 B 次往返）。⚠️ 与跟踪器的交互陷阱
    ///（同文件 v27/v28 Detach 纪律的批量版）：ExecuteUpdate 直写 DB 后，租约期被跟踪的
    /// 本批实体仍滞留 ChangeTracker 且为 Modified（租约路径已变异 LeasedBy/LeasedUntil +
    /// BumpVersion 递增）——不 Detach 则：① DB version 已被本语句再 +1，跟踪实体的
    /// original Version 落后，同 scope 后续任意 SaveChanges 触发
    /// <see cref="DbUpdateConcurrencyException"/>；② 滞留变异会被后续保存幽灵提交。
    /// 故成功后全批 Detach（含 0 行路径——DB 已换代，跟踪快照同样作废）。
    /// </remarks>
    public async ValueTask<int> ReleaseLeasesAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        var ids = sagas.Select(static s => s.SagaId).ToList();
        var affected = await SagaStates
            .Where(s => s.LeasedBy == owner && ids.Contains(s.SagaId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.LeasedBy, (string?)null)
                .SetProperty(s => s.LeasedUntil, (DateTimeOffset?)null)
                .SetProperty(s => s.Version, s => s.Version + 1), ct).ConfigureAwait(false);

        foreach (var saga in sagas)
        {
            var entry = Entry(saga);
            if (entry.State != EntityState.Detached)
                entry.State = EntityState.Detached;
        }
        return affected;
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
            e.Property(x => x.SagaId).HasConversion(v => v.ToString(null, System.Globalization.CultureInfo.InvariantCulture), v => PalUlid.Parse(v));
            e.HasIndex(x => new { x.Status, x.CurrentState });
            e.HasIndex(x => new { x.Status, x.LeasedUntil, x.CreatedAt });
            // M1：到期扫描索引；NextCheckAt 由 Saga 在状态变更时维护，NULL 表示无待检查超时。
            e.HasIndex(x => new { x.Status, x.NextCheckAt });
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
        // R1（B-F9，2026-10-14 性能实施轮）：无排序聚合——原 OrderBy 每次 DetectChanges 对每个
        // 被跟踪 Saga 的字典快照分配排序枚举器（O(n log n) CPU + 迭代器分配）；per-entry
        // 哈希的加法聚合与顺序无关，分布略逊于排序序列哈希但相等性语义不变
        //（DictionaryEquals 与本 hash 同为顺序无关，相等字典必得相等和）
        if (value is null) return 0;
        var hash = 0;
        foreach (var item in value)
        {
            var entry = new HashCode();
            entry.Add(item.Key, StringComparer.Ordinal);
            entry.Add(item.Value);
            hash += entry.ToHashCode();
        }
        return hash;
    }

    private static Dictionary<string, DateTimeOffset> CloneDictionary(Dictionary<string, DateTimeOffset>? value)
        => value is null
            ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(value, StringComparer.Ordinal);
}

[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
internal sealed partial class SagaStateJsonContext : JsonSerializerContext;
