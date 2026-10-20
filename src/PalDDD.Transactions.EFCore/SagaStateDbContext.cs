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
TState>(DbContextOptions options) : DbContext(options), ISagaStateStore<TState>, ISagaLeaseBatchStore<TState>, ISagaScanCursorStore<TState>, IDueTimeSagaStore<TState>, ISagaBatchStateStore<TState>
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
        // 不能翻译 DateTimeOffset 有序比较），而手写 SQL 不受此限（decision-2026-09-17
        // shape 2 已在 SqliteOutboxDbContext 落地同族单语句租约，本案是其 Saga 面延续）。
        // 2026-10-20 性能轮：PG/MySQL 同样下沉为方言原生语句。PG 两条（子查询 UPDATE +
        // 等值回读），MySQL 两条（JOIN-UPDATE + 等值回读），与 Dapper/PalORM 同形态。
        // 只有无法识别 provider 或非关系型 provider 才回落跟踪分页。
        if (UsesSingleStatementSqliteLease())
            return await LeaseViaSingleStatementSqliteAsync(owner, now, leasedUntil, batchSize, dueBefore, ct).ConfigureAwait(false);
        if (IsPostgreSqlProvider())
            return await LeaseViaPostgreSqlAsync(owner, leasedUntil, batchSize, dueBefore, now, ct).ConfigureAwait(false);
        if (IsMySqlProvider())
            return await LeaseViaMySqlAsync(owner, leasedUntil, batchSize, dueBefore, now, ct).ConfigureAwait(false);
        if (IsSqlServerProvider())
            return await LeaseViaSqlServerAsync(owner, leasedUntil, batchSize, dueBefore, now, ct).ConfigureAwait(false);

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
    /// 单语句租约启用判定（R7）：SQLite provider——按 <c>Database.ProviderName</c> 精确匹配
    ///（Microsoft.EntityFrameworkCore.Sqlite 注册的 provider 名），不引具体 provider 包
    ///（本适配包 csproj 零 provider 依赖）。SQL 形态（UPDATE…WHERE IN(SELECT…ORDER BY LIMIT)）
    /// 为 SQLite 语法。
    /// </summary>
    private bool UsesSingleStatementSqliteLease()
        => Database.IsRelational()
            && string.Equals(Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);

    /// <summary>PG provider 判定（Npgsql.EntityFrameworkCore.PostgreSQL）。</summary>
    private bool IsPostgreSqlProvider()
        => Database.IsRelational()
            && Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

    /// <summary>MySQL provider 判定（Pomelo 与 Oracle 两族 provider 名均含 MySql）。</summary>
    private bool IsMySqlProvider()
        => Database.IsRelational()
            && Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>SQL Server provider 判定（Microsoft.EntityFrameworkCore.SqlServer）。</summary>
    private bool IsSqlServerProvider()
        => Database.IsRelational()
            && Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// PG 单语句租约（2026-10-20 性能轮）：<c>UPDATE ... WHERE saga_id IN (SELECT ... LIMIT ...
    /// FOR UPDATE SKIP LOCKED)</c> + 按精确租约标识回读，两步与批次大小无关。语义与 SQLite
    /// 单语句路径逐项对齐：资格谓词在子查询内、租约即换代（version + 1）、到期过滤、批次上限。
    /// 回读走普通 LINQ SELECT（不依赖 provider 对 UPDATE..RETURNING 的组合翻译）。
    /// </summary>
    private async ValueTask<IReadOnlyList<TState>> LeaseViaPostgreSqlAsync(
        string owner,
        DateTimeOffset untilValue,
        int batchSize,
        DateTimeOffset? dueBefore,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var (sql, args) = BuildPostgreSqlLeaseSql(owner, untilValue, batchSize, dueBefore, now);
        await Database.ExecuteSqlRawAsync(sql, args, ct).ConfigureAwait(false);

        // 保持跟踪：SagaTimeoutProcessor 的补偿路径经 ISagaStateStore.SaveChangesAsync(state)
        // 依赖 ChangeTracker；SQL 已换代，物化值即 DB 真值（Unchanged）。
        return await SagaStates
            .Where(s => s.LeasedBy == owner
                && s.LeasedUntil == untilValue
                && (s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision))
            .OrderBy(s => s.SagaId)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// MySQL 单语句租约（2026-10-20 性能轮）：JOIN 派生表 UPDATE + 按精确租约标识回读，两步
    /// 与批次大小无关。SKIP LOCKED 按服务端版本启用（ADR-025 同一判定，旧版/MariaDB 回退
    /// 无锁形态，行为与既有 EF MySQL Outbox 路径一致）。
    /// </summary>
    private async ValueTask<IReadOnlyList<TState>> LeaseViaMySqlAsync(
        string owner,
        DateTimeOffset untilValue,
        int batchSize,
        DateTimeOffset? dueBefore,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var skipLocked = await MySqlSupportsSkipLockedAsync(ct).ConfigureAwait(false);
        var (sql, args) = BuildMySqlLeaseSql(owner, untilValue, batchSize, dueBefore, now, skipLocked);
        await Database.ExecuteSqlRawAsync(sql, args, ct).ConfigureAwait(false);

        return await SagaStates
            .Where(s => s.LeasedBy == owner
                && s.LeasedUntil == untilValue
                && (s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision))
            .OrderBy(s => s.SagaId)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>MySQL SKIP LOCKED 判定：连接已打开时 ServerVersion 来自握手包，零往返。</summary>
    private async ValueTask<bool> MySqlSupportsSkipLockedAsync(CancellationToken ct)
    {
        var connection = Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        return MySqlLeasePolicy.UseSkipLocked(connection.ServerVersion);
    }

    /// <summary>
    /// SQL Server 单语句租约（2026-10-20 性能轮）：<c>WITH candidates AS (SELECT TOP (@n)
    /// ... WITH (UPDLOCK, READPAST, ROWLOCK)) UPDATE ... INNER JOIN candidates</c> + 按精确
    /// 租约标识回读。原跟踪分页回落路径的 <c>Skip/Take</c> 深偏移与全页物化在 SQL Server
    /// 大表上随历史行数退化。T-SQL 与 SqlServerOutboxDbContext 的租约形态同族。
    /// </summary>
    private async ValueTask<IReadOnlyList<TState>> LeaseViaSqlServerAsync(
        string owner,
        DateTimeOffset untilValue,
        int batchSize,
        DateTimeOffset? dueBefore,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var (sql, args) = BuildSqlServerLeaseSql(owner, untilValue, batchSize, dueBefore, now);
        await Database.ExecuteSqlRawAsync(sql, args, ct).ConfigureAwait(false);

        return await SagaStates
            .Where(s => s.LeasedBy == owner
                && s.LeasedUntil == untilValue
                && (s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision))
            .OrderBy(s => s.SagaId)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>SQL Server 租约 SQL 构造：候选集 TOP + UPDLOCK/READPAST，列名取自 EF 模型映射。</summary>
    private (string Sql, object[] Args) BuildSqlServerLeaseSql(
        string owner,
        DateTimeOffset untilValue,
        int batchSize,
        DateTimeOffset? dueBefore,
        DateTimeOffset now)
    {
        var ids = ResolveLeaseIdentifiers(mySql: false, sqlServer: true);
        var dueClause = dueBefore is null
            ? string.Empty
            : $"AND ({ids.NextCheckAt} IS NOT NULL AND {ids.NextCheckAt} <= {{4}})";
        var orderBy = dueBefore is null
            ? $"CASE WHEN {ids.LeasedUntil} IS NULL THEN 0 ELSE 1 END, {ids.LeasedUntil}, {ids.SagaId}"
            : $"{ids.NextCheckAt}, {ids.SagaId}";
        var ownerIndex = dueBefore is null ? 4 : 5;
        var untilIndex = dueBefore is null ? 5 : 6;

        var sql = $@";WITH candidates AS (
    SELECT TOP ({{0}}) {ids.SagaId}
    FROM {ids.Table} WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE {ids.Status} IN ({{1}}, {{2}})
      AND ({ids.LeasedUntil} IS NULL OR {ids.LeasedUntil} <= {{3}})
      {dueClause}
    ORDER BY {orderBy}
)
UPDATE t
SET t.{ids.LeasedBy} = {{{ownerIndex}}}, t.{ids.LeasedUntil} = {{{untilIndex}}}, t.{ids.Version} = t.{ids.Version} + 1
FROM {ids.Table} AS t
INNER JOIN candidates AS c ON t.{ids.SagaId} = c.{ids.SagaId}";

        var args = dueBefore is null
            ? new object[] { batchSize, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, owner, untilValue }
            : new object[] { batchSize, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, dueBefore.Value, owner, untilValue };
        return (sql, args);
    }

    /// <summary>PG 租约 SQL 构造：标识符取自 EF 模型映射，值全部走 ExecuteSqlRaw 参数占位符。</summary>
    private (string Sql, object[] Args) BuildPostgreSqlLeaseSql(
        string owner,
        DateTimeOffset untilValue,
        int batchSize,
        DateTimeOffset? dueBefore,
        DateTimeOffset now)
    {
        var ids = ResolveLeaseIdentifiers(mySql: false);
        var dueClause = dueBefore is null
            ? string.Empty
            : $"AND ({ids.NextCheckAt} IS NOT NULL AND {ids.NextCheckAt} <= {{6}})";
        var orderBy = dueBefore is null
            ? $"{ids.LeasedUntil} NULLS FIRST, {ids.SagaId}"
            : $"{ids.NextCheckAt}, {ids.SagaId}";

        var sql = $@"UPDATE {ids.Table}
SET {ids.LeasedBy} = {{0}}, {ids.LeasedUntil} = {{1}}, {ids.Version} = {ids.Version} + 1
WHERE {ids.SagaId} IN (
    SELECT {ids.SagaId} FROM {ids.Table}
    WHERE {ids.Status} IN ({{2}}, {{3}})
      AND ({ids.LeasedUntil} IS NULL OR {ids.LeasedUntil} <= {{4}})
      {dueClause}
    ORDER BY {orderBy}
    LIMIT {{5}}
    FOR UPDATE SKIP LOCKED)";

        return dueBefore is { } due
            ? (sql, [owner, untilValue, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, batchSize, due])
            : (sql, [owner, untilValue, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, batchSize]);
    }

    /// <summary>MySQL 租约 SQL 构造：JOIN 派生表 UPDATE，派生表内 SELECT 只取主键。</summary>
    private (string Sql, object[] Args) BuildMySqlLeaseSql(
        string owner,
        DateTimeOffset untilValue,
        int batchSize,
        DateTimeOffset? dueBefore,
        DateTimeOffset now,
        bool skipLocked)
    {
        var ids = ResolveLeaseIdentifiers(mySql: true);
        var dueClause = dueBefore is null
            ? string.Empty
            : $"AND ({ids.NextCheckAt} IS NOT NULL AND {ids.NextCheckAt} <= {{6}})";
        var orderBy = dueBefore is null
            ? $"({ids.LeasedUntil} IS NULL) DESC, {ids.LeasedUntil}, {ids.SagaId}"
            : $"{ids.NextCheckAt}, {ids.SagaId}";
        var lockClause = skipLocked ? " FOR UPDATE SKIP LOCKED" : string.Empty;

        var sql = $@"UPDATE {ids.Table} t
JOIN (
    SELECT {ids.SagaId} FROM {ids.Table}
    WHERE {ids.Status} IN ({{2}}, {{3}})
      AND ({ids.LeasedUntil} IS NULL OR {ids.LeasedUntil} <= {{4}})
      {dueClause}
    ORDER BY {orderBy}
    LIMIT {{5}}{lockClause}
) AS sub ON t.{ids.SagaId} = sub.{ids.SagaId}
SET t.{ids.LeasedBy} = {{0}}, t.{ids.LeasedUntil} = {{1}}, t.{ids.Version} = t.{ids.Version} + 1";

        return dueBefore is { } due
            ? (sql, [owner, untilValue, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, batchSize, due])
            : (sql, [owner, untilValue, (int)SagaStatus.Active, (int)SagaStatus.AwaitingHumanDecision, now, batchSize]);
    }

    /// <summary>租约 SQL 标识符解析（EF 模型映射；MySQL 用反引号，PG/SQLite 用双引号）。</summary>
    private (string Table, string SagaId, string Status, string LeasedBy, string LeasedUntil, string Version, string NextCheckAt)
        ResolveLeaseIdentifiers(bool mySql, bool sqlServer = false)
    {
        var entityType = Model.FindEntityType(typeof(TState))
            ?? throw new InvalidOperationException($"Entity type '{typeof(TState).FullName}' is not mapped in this model.");
        var quote = mySql
            ? (Func<string, string>)QuoteMySqlIdentifier
            : sqlServer ? QuoteSqlServerIdentifier : QuoteSqliteIdentifier;

        var table = quote(entityType.GetTableName()
            ?? throw new InvalidOperationException($"Entity type '{typeof(TState).FullName}' has no table mapping."));
        if (entityType.GetSchema() is { } schema)
            table = quote(schema) + "." + table;

        return (
            table,
            quote(ColumnName(entityType, nameof(SagaState.SagaId))),
            quote(ColumnName(entityType, nameof(SagaState.Status))),
            quote(ColumnName(entityType, nameof(SagaState.LeasedBy))),
            quote(ColumnName(entityType, nameof(SagaState.LeasedUntil))),
            quote(ColumnName(entityType, nameof(SagaState.Version))),
            quote(ColumnName(entityType, nameof(SagaState.NextCheckAt))));
    }

    /// <summary>MySQL 标识符加引号（反引号 + 反引号转义）。</summary>
    private static string QuoteMySqlIdentifier(string identifier)
        => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>SQL Server 标识符加引号（方括号 + 右方括号转义）。</summary>
    private static string QuoteSqlServerIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

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
        await Database.ExecuteSqlAsync(
            BuildSingleStatementLeaseCommand(owner, now, leasedUntil, batchSize, dueBefore), ct).ConfigureAwait(false);

        // 等值守卫回读（不重跑资格子查询）：按精确租约标识 (owner, until) 取回本批。
        // 保持跟踪：SagaTimeoutProcessor 的补偿路径经 SaveChangesAsync(state) 依赖 ChangeTracker。
        return await SagaStates
            .Where(s => s.LeasedBy == owner
                && s.LeasedUntil == leasedUntil
                && (s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision))
            .OrderBy(s => s.SagaId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>构造单语句租约命令（R7，SQLite 形态）</summary>
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
        // 到期过滤按最早到期取批，避免老创建但晚到期的行占满批次；无到期过滤保持 SagaId 序。
        var orderBy = dueBefore is null
            ? $"({leasedUntilColumn} IS NULL) DESC, {leasedUntilColumn}, {sagaIdColumn}"
            : $"{nextCheckAtColumn}, {sagaIdColumn}";

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
                ORDER BY {{orderBy}}
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

    /// <summary>SQL 标识符加引号（双引号 + 双引号转义——SQLite/PG 标准形态；MySQL 反引号、SQL Server 方括号为独立形态）。</summary>
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
    public ValueTask<int> ReleaseLeasesAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
        => ReleaseLeasesCoreAsync(owner, sagas, checkedAt: null, ct);

    /// <inheritdoc/>
    /// <remarks>扫描轮转：leased_until 写成本次扫描时刻，legacy 租约按 NULLS FIRST 排序。</remarks>
    public ValueTask<int> ReleaseLeasesAsync(
        string owner,
        IReadOnlyList<TState> sagas,
        DateTimeOffset checkedAt,
        CancellationToken ct)
        => ReleaseLeasesCoreAsync(owner, sagas, checkedAt, ct);

    private async ValueTask<int> ReleaseLeasesCoreAsync(
        string owner,
        IReadOnlyList<TState> sagas,
        DateTimeOffset? checkedAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        var ids = sagas.Select(static s => s.SagaId).ToList();
        var query = SagaStates.Where(s => s.LeasedBy == owner && ids.Contains(s.SagaId));
        var affected = checkedAt is { } at
            ? await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.LeasedBy, (string?)null)
                .SetProperty(s => s.LeasedUntil, at)
                .SetProperty(s => s.Version, s => s.Version + 1), ct).ConfigureAwait(false)
            : await query.ExecuteUpdateAsync(setters => setters
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

    /// <inheritdoc/>
    /// <remarks>
    /// 2026-10-20 性能轮：整批一次 <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>，
    /// <see cref="BumpVersionOnModifiedSagaStates"/> 只扫描一次 ChangeTracker（原逐 Saga 保存
    /// 在 256 批下叠加成 O(n²) 扫描）。只写入调用方已变异的 NextCheckAt；Version 并发令牌由
    /// 提交前递增承担。失败整体按 0 返回，租约由 LeaseDuration 兜底。
    /// </remarks>
    public async ValueTask<int> AdvanceNextCheckAtAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        var eligible = 0;
        foreach (var saga in sagas)
        {
            if (saga is null) continue;
            var entry = Entry(saga);
            if (entry.State == EntityState.Detached)
                continue;
            if (!string.Equals(saga.LeasedBy, owner, StringComparison.Ordinal))
            {
                entry.State = EntityState.Detached;
                continue;
            }
            eligible++;
        }
        if (eligible == 0) return 0;

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            return eligible;
        }
        catch (DbUpdateConcurrencyException)
        {
            DetachAll(sagas);
            return 0;
        }
        catch
        {
            DetachAll(sagas);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<int>> SaveManyAsync(IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return [];

        var results = new int[sagas.Count];
        for (var i = 0; i < sagas.Count; i++)
        {
            var saga = sagas[i];
            if (saga is null) continue;
            var entry = Entry(saga);
            if (entry.State == EntityState.Detached) continue;
            saga.Error = Core.FailureReason.Truncate(saga.Error, 2040);
            results[i] = 1;
        }

        var eligible = 0;
        foreach (var result in results)
            eligible += result;
        if (eligible == 0) return results;

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            return results;
        }
        catch (DbUpdateConcurrencyException)
        {
            DetachAll(sagas);
            return new int[sagas.Count];
        }
        catch
        {
            DetachAll(sagas);
            throw;
        }
    }

    private void DetachAll(IReadOnlyList<TState> sagas)
    {
        foreach (var saga in sagas)
        {
            if (saga is null) continue;
            var entry = Entry(saga);
            if (entry.State != EntityState.Detached)
                entry.State = EntityState.Detached;
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
            e.Property(x => x.SagaId).HasConversion(v => v.ToString(null, System.Globalization.CultureInfo.InvariantCulture), v => PalUlid.Parse(v));
            e.HasIndex(x => new { x.Status, x.LeasedUntil, x.CreatedAt });
            // 2026-10-21 审计 P1：PG/MySQL/SQLite/SQL Server 的租约回读按
            // (leased_by, leased_until) 等值匹配；缺此索引时回读退化为全表扫描。
            e.HasIndex(x => new { x.LeasedBy, x.LeasedUntil });
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

            // List<string> → PrimitiveCollection（EF Core 11 原生 JSON 列支持；ADR-035：
            // Npgsql EF 物化返回 List<string>，属性类型同步为 List<string>）
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
