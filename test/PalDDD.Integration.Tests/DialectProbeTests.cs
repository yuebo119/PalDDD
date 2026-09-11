using System.Data.Common;
using System.Runtime.ExceptionServices;
using ByteAether.Ulid;
using MySqlConnector;
using Npgsql;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Projections;
using PalDDD.Testing;
using PalDDD.Transactions;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧪 MIG-001 · 方言探针转正 — dialect-probe.sh 双副本的 TUnit 形态
// ═══════════════════════════════════════════════════════════════
// 源：scripts/dialect-probe.sh + .ai/scripts/dialect-probe.sh（双副本已删除）。
// 探针 C# 本体逐条迁入（42 项断言 = 每方言 21 项，按族拆为独立测试）：
//   Outbox   Add→GetPending→Lease→MarkProcessed + 追踪 4 列（5 项）
//   EventLog Append→Read→陈旧版本冲突分类（3 项）
//   Saga     Insert→Lease（3 项）
//   Inbox    Insert→Lease→超时接管（5 项）
//   Checkpoint TryStart→Complete→Fail（4 项）
//   AmbientTx ambient 事务挂接断链探针（1 项）
//
// 安全守卫等价迁移（原探针 P0 #1 红线，逐条对照，见 DialectProbeSession 头注释）：
//   ①库名白名单 palddd_probe_* 前缀 + [a-z0-9_] 字符集 + 随机后缀
//   ②管理库限定（CREATE/DROP DATABASE 的连接目标白名单）
//   ③ownership marker 写入 + 清理前回读校验归属；未确认但本进程创建 → 补偿清理
//   ④连接串与凭据绝不进入日志/异常消息
//   ⑤生成库名格式校验（TestEnvironment.IsStrictGeneratedDatabaseName，同源逻辑）
//   ⑥RunDialectGuarded 单方言降级 → PG/MySQL 拆独立测试，一方言失败不影响另一方言
//
// 执行环境：Testcontainers 一次性容器——比原探针"外部实例建临时库 + DROP"更严
// （容器即沙箱，清理失败容器销毁兜底）；外部连接串配置 / Docker 不可达 → Skip
// （对齐 InboxTimestampTokenPrecisionProbeTests 的 Skip.Test 环境守卫模式）。
// DDL 真源：docs/sql/{postgresql,mysql}/000_schema.sql——保留原探针
// "验证 DDL 真源 + Dapper Store 组合"的价值（DDL 漂移必红）。
// SQLite 不适用：原探针只做 PG/MySQL；SQLite 路径由 DapperStoreTests 覆盖。
//
// 与原探针的语义差异（迁移声明）：探针 Check() 收集全部失败一次报告；
// 测试形态下族内断言首败即停（TUnit 标准语义），跨族/跨方言独立性由测试拆分保证。
// ═══════════════════════════════════════════════════════════════

/// <summary>Dapper 栈 PG/MySQL 方言探针（MIG-001，迁自 dialect-probe.sh）。</summary>
/// <remarks>
/// [NotInParallel("dapper-global")] 与 DapperStoreTests / ITM-650 探针同组序列化：
/// Dapper 的 TypeHandler 与 MatchNamesWithUnderscores 是全局静态状态（PalDDD.Dapper 的
/// ModuleInitializer 自足注册，本类无需手动注册），但 DapperStoreTests 的 ClassCleanup
/// 会 ResetTypeHandlers——交错执行会产生 SQLite TEXT→DateTimeOffset 映射竞态，同组串行防御。
/// </remarks>
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class DialectProbeTests
{
    // ─── PostgreSQL 六族 ────────────────────────────────────────

    /// <summary>Outbox 族（探针断言 #1-#5）：Add→GetPending→追踪 4 列→Lease→MarkProcessed。</summary>
    [Test]
    public async Task Outbox_PostgreSql_Add_GetPending_Lease_MarkProcessed()
    {
        EnsureTestcontainersEnabled(isPostgreSql: true);
        await using var probe = await DialectProbeSession.CreatePostgreSqlAsync();
        await OutboxSmokeAsync(probe.Connection, DapperDbType.PostgreSql);
    }

    /// <summary>EventLog 族（探针断言 #6-#8）：Append→Read→审计列→陈旧版本冲突分类。</summary>
    [Test]
    public async Task EventLog_PostgreSql_Append_Read_ConcurrencyConflict()
    {
        EnsureTestcontainersEnabled(isPostgreSql: true);
        await using var probe = await DialectProbeSession.CreatePostgreSqlAsync();
        await EventLogSmokeAsync(probe.Connection, DapperDbType.PostgreSql);
    }

    /// <summary>Saga 族（探针断言 #9-#11）：INSERT 1 行→Lease 归属。</summary>
    [Test]
    public async Task Saga_PostgreSql_Insert_Lease()
    {
        EnsureTestcontainersEnabled(isPostgreSql: true);
        await using var probe = await DialectProbeSession.CreatePostgreSqlAsync();
        await SagaSmokeAsync(probe.Connection, DapperDbType.PostgreSql);
    }

    /// <summary>Inbox 族（探针断言 #12-#16）：TryStart→重复拒绝→他消费者→MarkProcessed→超时接管。</summary>
    [Test]
    public async Task Inbox_PostgreSql_TryStart_MarkProcessed_TimeoutTakeover()
    {
        EnsureTestcontainersEnabled(isPostgreSql: true);
        await using var probe = await DialectProbeSession.CreatePostgreSqlAsync();
        await InboxSmokeAsync(probe.Connection, DapperDbType.PostgreSql);
    }

    /// <summary>Checkpoint 族（探针断言 #17-#20）：TryStart→Completed 跳过→新位置重入→MarkFailed。</summary>
    [Test]
    public async Task Checkpoint_PostgreSql_TryStart_Complete_Fail()
    {
        EnsureTestcontainersEnabled(isPostgreSql: true);
        await using var probe = await DialectProbeSession.CreatePostgreSqlAsync();
        await CheckpointSmokeAsync(probe.Connection, DapperDbType.PostgreSql);
    }

    /// <summary>AmbientTx 族（探针断言 #21）：DI 形态 Store 挂接 UoW 事务，回滚后无泄漏。</summary>
    [Test]
    public async Task AmbientTx_PostgreSql_Rollback_NoLeak()
    {
        EnsureTestcontainersEnabled(isPostgreSql: true);
        await using var probe = await DialectProbeSession.CreatePostgreSqlAsync();
        await AmbientTxDapperSmokeAsync(probe.Connection, DapperDbType.PostgreSql);
    }

    // ─── MySQL 六族 ─────────────────────────────────────────────

    /// <summary>Outbox 族（探针断言 #1-#5）：Add→GetPending→追踪 4 列→Lease→MarkProcessed。</summary>
    [Test]
    public async Task Outbox_MySql_Add_GetPending_Lease_MarkProcessed()
    {
        EnsureTestcontainersEnabled(isPostgreSql: false);
        await using var probe = await DialectProbeSession.CreateMySqlAsync();
        await OutboxSmokeAsync(probe.Connection, DapperDbType.MySql);
    }

    /// <summary>EventLog 族（探针断言 #6-#8）：Append→Read→审计列→陈旧版本冲突分类。</summary>
    [Test]
    public async Task EventLog_MySql_Append_Read_ConcurrencyConflict()
    {
        EnsureTestcontainersEnabled(isPostgreSql: false);
        await using var probe = await DialectProbeSession.CreateMySqlAsync();
        await EventLogSmokeAsync(probe.Connection, DapperDbType.MySql);
    }

    /// <summary>Saga 族（探针断言 #9-#11）：INSERT 1 行→Lease 归属。</summary>
    [Test]
    public async Task Saga_MySql_Insert_Lease()
    {
        EnsureTestcontainersEnabled(isPostgreSql: false);
        await using var probe = await DialectProbeSession.CreateMySqlAsync();
        await SagaSmokeAsync(probe.Connection, DapperDbType.MySql);
    }

    /// <summary>Inbox 族（探针断言 #12-#16）：TryStart→重复拒绝→他消费者→MarkProcessed→超时接管。</summary>
    [Test]
    public async Task Inbox_MySql_TryStart_MarkProcessed_TimeoutTakeover()
    {
        EnsureTestcontainersEnabled(isPostgreSql: false);
        await using var probe = await DialectProbeSession.CreateMySqlAsync();
        await InboxSmokeAsync(probe.Connection, DapperDbType.MySql);
    }

    /// <summary>Checkpoint 族（探针断言 #17-#20）：TryStart→Completed 跳过→新位置重入→MarkFailed。</summary>
    [Test]
    public async Task Checkpoint_MySql_TryStart_Complete_Fail()
    {
        EnsureTestcontainersEnabled(isPostgreSql: false);
        await using var probe = await DialectProbeSession.CreateMySqlAsync();
        await CheckpointSmokeAsync(probe.Connection, DapperDbType.MySql);
    }

    /// <summary>AmbientTx 族（探针断言 #21）：DI 形态 Store 挂接 UoW 事务，回滚后无泄漏。</summary>
    [Test]
    public async Task AmbientTx_MySql_Rollback_NoLeak()
    {
        EnsureTestcontainersEnabled(isPostgreSql: false);
        await using var probe = await DialectProbeSession.CreateMySqlAsync();
        await AmbientTxDapperSmokeAsync(probe.Connection, DapperDbType.MySql);
    }

    // ─── 环境守卫（原探针 probe_tcp 不可达跳过的等价物 + 禁外部库裁决）───

    private static void EnsureTestcontainersEnabled(bool isPostgreSql)
    {
        // 对齐 MultiDialectFixture "禁止连接或清理外部数据库"裁决：配置指向外部连接串时
        // Skip 而非硬拒（探针的 PG 不可达跳过语义），Testcontainers 路径才是本探针的执行面。
        if (isPostgreSql && !TestEnvironment.UsePostgreSqlTestcontainers)
            Skip.Test("PG 方言探针仅走 Testcontainers（对齐 MultiDialectFixture 禁外部库裁决）；当前配置指向外部连接串，跳过。");
        if (!isPostgreSql && !TestEnvironment.UseMySqlTestcontainers)
            Skip.Test("MySQL 方言探针仅走 Testcontainers（对齐 MultiDialectFixture 禁外部库裁决）；当前配置指向外部连接串，跳过。");
    }

    // ─── 探针断言本体（逐条对照原探针 Smoke 方法，断言编号 = 原探针 Check 顺序）───

    /// <summary>原探针 OutboxSmoke——5 项断言。</summary>
    private static async Task OutboxSmokeAsync(DbConnection conn, DapperDbType dbType)
    {
        var clock = TimeProvider.System;
        var store = new DapperOutboxStore(conn, dbType, timeProvider: clock);
        var msg = new OutboxMessage
        {
            Type = "probe.event.v1", Payload = [1, 2, 3], ContentType = "application/json", SchemaVersion = 1,
            CorrelationId = Ulid.New(), CausationId = Ulid.New(), TraceParent = "00-abc-def-01", TraceState = "probe=1",
        };
        store.AddMessage(msg);

        // #1「Outbox GetPending 返回 1 条」
        var pending = await store.GetPendingMessagesAsync(10, 10, default);
        await Assert.That(pending.Count).IsEqualTo(1);

        // #2「Outbox 追踪 4 列回读」——CorrelationId/CausationId/TraceParent/TraceState
        await Assert.That(pending[0].CorrelationId).IsEqualTo(msg.CorrelationId);
        await Assert.That(pending[0].CausationId).IsEqualTo(msg.CausationId);
        await Assert.That(pending[0].TraceParent).IsEqualTo("00-abc-def-01");
        await Assert.That(pending[0].TraceState).IsEqualTo("probe=1");

        // #3「Outbox Lease 返回 1 条」
        var leased = await store.LeasePendingMessagesAsync(10, "probe-owner", TimeSpan.FromMinutes(2), 10, default);
        await Assert.That(leased.Count).IsEqualTo(1);

        // #4「Outbox Lease 归属」
        await Assert.That(leased[0].LockedBy).IsEqualTo("probe-owner");

        // #5「Outbox MarkProcessed 后无 pending」
        store.MarkProcessed(leased[0], clock.GetUtcNow());
        await Assert.That(await store.GetPendingMessagesAsync(10, 10, default)).IsEmpty();
    }

    /// <summary>原探针 EventLogSmoke——3 项断言（冲突分类含异常类型鉴别）。</summary>
    private static async Task EventLogSmokeAsync(DbConnection conn, DapperDbType dbType)
    {
        var log = new DapperEventLog(conn, dbType: dbType);
        var audit = new EventAuditMetadata("probe-actor", "probe-reason", Ulid.New(), Ulid.New(), "00-abc-def-01", "probe=1");
        var events = new List<EventData>
        {
            new(Ulid.New(), "probe.event.v1", 1, "application/json", new ReadOnlyMemory<byte>([1]), new ReadOnlyMemory<byte>([1]), audit),
            new(Ulid.New(), "probe.event.v1", 1, "application/json", new ReadOnlyMemory<byte>([2]), new ReadOnlyMemory<byte>([2]), audit),
        };
        await log.AppendAsync("probe-stream", ExpectedStreamVersion.NoStream, events, default);

        // #6「EventLog Append→Read 2 条」
        var read = new List<RecordedEvent>();
        await foreach (var e in log.ReadStreamAsync("probe-stream", 0, 100, default)) read.Add(e);
        await Assert.That(read.Count).IsEqualTo(2);

        // #7「EventLog 审计列回读」
        await Assert.That(read[0].Audit.CorrelationId).IsEqualTo(audit.CorrelationId);

        // #8「EventLog 陈旧版本抛并发异常」——原探针语义：未抛 = 失败；抛 EventStreamConcurrencyException
        // = 通过；抛其他异常 = 失败（含异常类型鉴别）。ThrowsAsync 泛型精确匹配该分类。
        await Assert.ThrowsAsync<EventStreamConcurrencyException>(async () =>
            await log.AppendAsync("probe-stream", ExpectedStreamVersion.Exact(0), events, default));
    }

    /// <summary>原探针 SagaSmoke——3 项断言。</summary>
    private static async Task SagaSmokeAsync(DbConnection conn, DapperDbType dbType)
    {
        var store = new DapperSagaStateStore<ProbeSagaState>(conn, dbType: dbType);
        var state = new ProbeSagaState { CurrentState = "Waiting" };

        // #9「Saga INSERT 1 行」
        await Assert.That(await store.SaveChangesAsync(state, default)).IsEqualTo(1);

        // #10「Saga Lease 返回 1 条」
        var leased = await store.LeaseActiveSagasAsync("probe-owner", TimeSpan.FromMinutes(2), 10, default);
        await Assert.That(leased.Count).IsEqualTo(1);

        // #11「Saga Lease 归属」
        await Assert.That(leased[0].LeasedBy).IsEqualTo("probe-owner");
    }

    /// <summary>原探针 InboxSmoke——5 项断言。</summary>
    private static async Task InboxSmokeAsync(DbConnection conn, DapperDbType dbType)
    {
        var store = new DapperInboxStore(conn, dbType);
        var clock = DateTimeOffset.UtcNow;

        // #12「Inbox TryStart 首次返回记录」
        var first = await store.TryStartProcessingAsync("probe-consumer", "probe-msg-1", clock, TimeSpan.FromMinutes(5), default);
        await Assert.That(first).IsNotNull();

        // #13「Inbox TryStart 重复返回 null」
        await Assert.That(
            await store.TryStartProcessingAsync("probe-consumer", "probe-msg-1", clock, TimeSpan.FromMinutes(5), default))
            .IsNull();

        // #14「Inbox 其他消费者可处理」
        await Assert.That(
            await store.TryStartProcessingAsync("other-consumer", "probe-msg-1", clock, TimeSpan.FromMinutes(5), default))
            .IsNotNull();

        // #15「Inbox MarkProcessed 后不再重投」
        await store.MarkProcessedAsync(first!, clock.AddSeconds(1), default);
        await Assert.That(
            await store.TryStartProcessingAsync("probe-consumer", "probe-msg-1", clock.AddSeconds(2), TimeSpan.FromMinutes(5), default))
            .IsNull();

        // #16「Inbox 超时接管（timeout=0）可重入」
        await Assert.That(
            await store.TryStartProcessingAsync("probe-consumer", "probe-msg-stale", clock, TimeSpan.Zero, default))
            .IsNotNull();
    }

    /// <summary>原探针 CheckpointSmoke——4 项断言。</summary>
    private static async Task CheckpointSmokeAsync(DbConnection conn, DapperDbType dbType)
    {
        var store = new DapperProjectionCheckpointStore(conn, dbType);
        var clock = DateTimeOffset.UtcNow;

        // #17「Checkpoint TryStart 首次返回」
        var cp = await store.TryStartAsync("probe-projection", "probe-source", "pos-1", clock, default);
        await Assert.That(cp).IsNotNull();

        // #18「Checkpoint Completed 同位置跳过（replay 保护）」
        await store.MarkCompletedAsync(cp!, clock.AddSeconds(1), default);
        await Assert.That(
            await store.TryStartAsync("probe-projection", "probe-source", "pos-1", clock.AddSeconds(2), default))
            .IsNull();

        // #19「Checkpoint 新位置可重入」
        var nextPos = await store.TryStartAsync("probe-projection", "probe-source", "pos-2", clock.AddSeconds(2), default);
        await Assert.That(nextPos).IsNotNull();

        // #20「Checkpoint MarkFailed 不抛」
        await store.MarkFailedAsync(nextPos!, "probe-failure", clock.AddSeconds(3), default);
    }

    /// <summary>原探针 AmbientTxDapperSmoke——1 项断言（二轮评审 T5 断链探针）。</summary>
    /// <remarks>
    /// DI 形态 store（transaction: null）+ DapperUnitOfWork.Begin → 写入必须进入活动事务
    /// （回滚即丢弃）。挂接缺失时两种失败形态都会被捕获：MySQL/PG 严格校验（未挂 tx 的
    /// 命令直接抛）或写入自动提交（回滚后仍可查到）。
    /// </remarks>
    private static async Task AmbientTxDapperSmokeAsync(DbConnection conn, DapperDbType dbType)
    {
        var store = new DapperOutboxStore(conn, dbType);
        await using var uow = new DapperUnitOfWork(conn);
        await uow.BeginTransactionAsync(default);
        store.AddMessage(new OutboxMessage
        {
            Type = "probe.ambienttx.v1", Payload = [9], ContentType = "application/json", SchemaVersion = 1,
            CorrelationId = Ulid.New(), CausationId = Ulid.New(),
        });
        await uow.RollbackAsync(default);

        // #21「AmbientTx 回滚后无泄漏」
        var leaked = await store.GetPendingMessagesAsync(10, 10, default);
        await Assert.That(leaked.Count).IsEqualTo(0)
            .Because($"泄漏 {leaked.Count} 条（store 未挂接 UoW 事务）");
    }

    /// <summary>探针 Saga 测试状态（原探针 ProbeSagaState 同款：无快照序列化，仅标量列）。</summary>
    private sealed class ProbeSagaState : SagaState;
}

/// <summary>
/// 方言探针会话 — 一次性 Testcontainers 容器 + palddd_probe_* 临时库的完整安全生命周期
/// （MIG-001 从 dialect-probe.sh 的 RunPg/RunMySql + 安全守卫族迁移）。
/// <para>
/// <b>安全守卫对照清单（原探针 → 本类实现）</b>：
/// <list type="number">
/// <item>库名白名单（validate_generated_database / EnsureOwnedDatabase）→
/// <see cref="EnsureProbeDatabaseName"/>：前缀 + 长度 + [a-z0-9_] 字符集，随机后缀 Guid N 格式；
/// 清理前再用 <see cref="TestEnvironment.IsStrictGeneratedDatabaseName"/> 复核（同源逻辑）。</item>
/// <item>管理库限定（validate_admin_database）→ <see cref="EnsureAdminTargetAllowed"/>：
/// CREATE/DROP DATABASE 的连接目标限定 postgres/mysql/palddd_test_admin/palddd_probe_admin/
/// palddd_test_* 白名单；数据库别名必须唯一（TryGetUniqueDatabaseName，对齐原 connection_value
/// 的重复别名拒绝语义）。</item>
/// <item>ownership marker 校验后才清理 → 建库后写入 palddd_probe_ownership 表；Dispose 时
/// 回读校验一致才 DROP；marker 未确认但库由本进程创建 → 补偿清理（否则孤儿库）；
/// 非本进程创建 → 拒绝清理。</item>
/// <item>连接串与凭据绝不进入日志/异常消息 → 本类不打印连接串；补偿清理失败输出仅含
/// 库名与异常类型名（连驱动异常 Message 也不输出，比原探针更保守）。</item>
/// <item>补偿清理兜底 → 即使 DROP 失败，一次性容器销毁也回收全部库（比原探针的外部实例更强）。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class DialectProbeSession : IAsyncDisposable
{
    private const string PgProbePrefix = "palddd_probe_pg_";
    private const string MySqlProbePrefix = "palddd_probe_mysql_";
    private const string OwnershipTableName = "palddd_probe_ownership";

    /// <summary>指向 probe 库的已打开连接（探针断言共用）。</summary>
    public DbConnection Connection { get; }

    private readonly IAsyncDisposable _container;
    private readonly string _adminConnectionString;
    private readonly string _probeDatabase;
    private readonly string _probeConnectionString;
    private readonly string _dropDatabaseSql;
    private readonly string _probePrefix;
    private readonly bool _databaseCreated;
    private readonly bool _isPostgreSql;

    private DialectProbeSession(
        DbConnection connection,
        IAsyncDisposable container,
        string adminConnectionString,
        string probeDatabase,
        string probeConnectionString,
        string probePrefix,
        bool databaseCreated,
        bool isPostgreSql)
    {
        Connection = connection;
        _container = container;
        _adminConnectionString = adminConnectionString;
        _probeDatabase = probeDatabase;
        _probeConnectionString = probeConnectionString;
        _probePrefix = probePrefix;
        _databaseCreated = databaseCreated;
        _isPostgreSql = isPostgreSql;
        // PG DROP WITH (FORCE) 对齐原探针；MySQL 普通 DROP（容器内无并发会话）
        _dropDatabaseSql = isPostgreSql
            ? $"DROP DATABASE {probeDatabase} WITH (FORCE)"
            : $"DROP DATABASE {probeDatabase}";
    }

    /// <summary>创建 PG 探针会话：一次性容器 → palddd_probe_pg_* 库 → marker → docs/sql DDL。</summary>
    public static async Task<DialectProbeSession> CreatePostgreSqlAsync(CancellationToken ct = default)
    {
        var container = new PostgreSqlBuilder(TestEnvironment.PostgreSqlImage)
            .WithDatabase($"palddd_test_{Guid.NewGuid():N}")
            .Build();
        return await StartContainerAndBuildSessionAsync(
            container, isPostgreSql: true, probePrefix: PgProbePrefix, adminDatabase: "postgres", ct).ConfigureAwait(false);
    }

    /// <summary>创建 MySQL 探针会话：一次性容器 → palddd_probe_mysql_* 库 → marker → docs/sql DDL。</summary>
    public static async Task<DialectProbeSession> CreateMySqlAsync(CancellationToken ct = default)
    {
        var container = new MySqlBuilder(TestEnvironment.MySqlImage)
            .WithDatabase($"palddd_test_{Guid.NewGuid():N}")
            .Build();
        // MySQL 管理连接直接复用容器默认库（palddd_test_* 命中白名单；CREATE/DROP DATABASE 不受当前库影响）
        return await StartContainerAndBuildSessionAsync(
            container, isPostgreSql: false, probePrefix: MySqlProbePrefix, adminDatabase: null, ct).ConfigureAwait(false);
    }

    private static async Task<DialectProbeSession> StartContainerAndBuildSessionAsync(
        IAsyncDisposable container,
        Func<CancellationToken, Task> startAsync,
        Func<string> getConnectionString,
        bool isPostgreSql,
        string probePrefix,
        string? adminDatabase,
        CancellationToken ct)
    {
        try
        {
            await startAsync(ct);
        }
#pragma warning disable CA1031 // 意图性宽捕获：探测 Docker 可达性（对齐 ITM-650 / BrokerIntegrationTests 环境守卫模式）
        catch (Exception)
#pragma warning restore CA1031
        {
            await container.DisposeAsync();
            Skip.Test($"Docker/Testcontainers 不可达——{(isPostgreSql ? "PostgreSQL" : "MySQL")} 方言探针跳过（待 CI 执行）。");
        }

        try
        {
            return await BuildSessionAsync(container, getConnectionString(), isPostgreSql, probePrefix, adminDatabase, ct);
        }
        catch
        {
            // 容器已启动但建库/建 marker/应用 DDL 失败：回收容器（半成品库由 BuildSessionAsync 的
            // 补偿清理路径回收，失败再由容器销毁兜底）
            await container.DisposeAsync();
            throw;
        }
    }

    // 重载桥接：上述泛型参数化工厂需要每方言传入 start/GetConnectionString 委托——
    // PostgreSqlContainer/MySqlContainer 无公共基类暴露这两个成员，用两个薄包装保持调用点简洁。
    private static Task<DialectProbeSession> StartContainerAndBuildSessionAsync(
        PostgreSqlContainer container, bool isPostgreSql, string probePrefix, string? adminDatabase, CancellationToken ct)
        => StartContainerAndBuildSessionAsync(
            container, c => container.StartAsync(c), container.GetConnectionString, isPostgreSql, probePrefix, adminDatabase, ct);

    private static Task<DialectProbeSession> StartContainerAndBuildSessionAsync(
        MySqlContainer container, bool isPostgreSql, string probePrefix, string? adminDatabase, CancellationToken ct)
        => StartContainerAndBuildSessionAsync(
            container, c => container.StartAsync(c), container.GetConnectionString, isPostgreSql, probePrefix, adminDatabase, ct);

    /// <summary>建库 → marker → DDL（原探针 RunPg/RunMySql 主体；任一步失败走补偿清理）。</summary>
    private static async Task<DialectProbeSession> BuildSessionAsync(
        IAsyncDisposable container,
        string containerConnectionString,
        bool isPostgreSql,
        string probePrefix,
        string? adminDatabase,
        CancellationToken ct)
    {
        // 守卫①：库名白名单（原探针 EnsureOwnedDatabase；库名来自 Guid，无用户输入）
        var probeDatabase = probePrefix + Guid.NewGuid().ToString("N");
        EnsureProbeDatabaseName(probeDatabase, probePrefix);

        // 守卫②：管理库限定。adminDatabase=null 表示复用容器连接串原目标（MySQL palddd_test_* 路径）
        var adminConnectionString = adminDatabase is null
            ? containerConnectionString
            : WithDatabase(containerConnectionString, adminDatabase);
        EnsureAdminTargetAllowed(adminConnectionString, isPostgreSql);

        await ExecuteNonQueryAsync(adminConnectionString, $"CREATE DATABASE {probeDatabase}", isPostgreSql, ct);
        var databaseCreated = true;
        var marked = false;

        var probeConnectionString = WithDatabase(containerConnectionString, probeDatabase);
        DbConnection? connection = null;
        try
        {
            connection = CreateConnection(probeConnectionString, isPostgreSql);
            await connection.OpenAsync(ct);

            // 守卫③：ownership marker 写入 + 回读确认
            await CreateOwnershipMarkerAsync(connection, probeDatabase, ct);
            marked = await TryReadOwnershipMarkerAsync(probeConnectionString, isPostgreSql, ct) == probeDatabase;
            if (!marked)
                throw new InvalidOperationException($"{(isPostgreSql ? "PostgreSQL" : "MySQL")} ownership marker 未确认（方言探针）。");

            // DDL 真源：docs/sql/{postgresql,mysql}/000_schema.sql（原探针同源路径）
            var ddlPath = FindSchemaFile(isPostgreSql ? "docs/sql/postgresql/000_schema.sql" : "docs/sql/mysql/000_schema.sql");
            await ExecuteBatchAsync(connection, await File.ReadAllTextAsync(ddlPath, ct), ct);

            return new DialectProbeSession(connection, container, adminConnectionString, probeDatabase,
                probeConnectionString, probePrefix, databaseCreated, isPostgreSql);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            // 补偿清理（原探针 finally 补偿路径）：严格前缀库由本进程创建，marker 未确认也必须回收；
            // DROP 失败由容器销毁兜底，不掩盖原始异常
            try { await ExecuteNonQueryAsync(adminConnectionString, BuildDropSql(probeDatabase, isPostgreSql), isPostgreSql, CancellationToken.None); }
#pragma warning disable CA1031 // 补偿清理失败不掩盖创建失败的根因（容器销毁兜底回收）
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            throw;
        }
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1031",
        Justification = "释放路径收集全部失败聚合抛出（对齐 AsyncResourceDisposer 模式），不吞异常")]
    public async ValueTask DisposeAsync()
    {
        var exceptions = new List<Exception>(2);
        try
        {
            await Connection.DisposeAsync();

            // 守卫③清理侧（原探针 finally）：marker 回读校验通过才 DROP；未确认但本进程创建 → 补偿清理
            var markerConfirmed = false;
            try
            {
                markerConfirmed = await TryReadOwnershipMarkerAsync(_probeConnectionString, _isPostgreSql, CancellationToken.None)
                    == _probeDatabase;
            }
#pragma warning disable CA1031 // marker 回读失败按未确认处理（走补偿清理分支）
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            if (markerConfirmed && TestEnvironment.IsStrictGeneratedDatabaseName(_probeDatabase, _probePrefix, _databaseCreated))
            {
                await ExecuteNonQueryAsync(_adminConnectionString, _dropDatabaseSql, _isPostgreSql, CancellationToken.None);
            }
            else if (_databaseCreated)
            {
                // 补偿清理：本进程创建了该严格前缀库，marker 未建立也必须回收，否则留孤儿库
                try
                {
                    await ExecuteNonQueryAsync(_adminConnectionString, _dropDatabaseSql, _isPostgreSql, CancellationToken.None);
                    Console.Error.WriteLine($"方言探针补偿清理：已回收孤儿探针库 {_probeDatabase}");
                }
                catch (Exception cleanupEx)
                {
                    // 守卫④：不输出异常 Message（驱动消息可能含连接信息），仅类型名 + 库名
                    Console.Error.WriteLine($"方言探针孤儿库需手动清理（容器销毁兜底）：{_probeDatabase} — {cleanupEx.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
        finally
        {
            try { await _container.DisposeAsync(); }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        if (exceptions.Count == 1) ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        if (exceptions.Count > 1) throw new AggregateException("方言探针会话释放失败。", exceptions);
    }

    // ─── 安全守卫实现 ───────────────────────────────────────────

    /// <summary>守卫①：生成库名格式校验（原探针 EnsureOwnedDatabase——前缀 + 长度 + [a-z0-9_]）。</summary>
    private static void EnsureProbeDatabaseName(string database, string prefix)
    {
        if (string.IsNullOrWhiteSpace(database) || !database.StartsWith(prefix, StringComparison.Ordinal)
            || database.Length <= prefix.Length
            || database.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
            throw new InvalidOperationException("拒绝非专用测试数据库：库名不符合方言探针白名单格式。");
    }

    /// <summary>守卫②：管理连接目标白名单（原探针 validate_admin_database 的 PG/MySQL case 表）。</summary>
    private static void EnsureAdminTargetAllowed(string adminConnectionString, bool isPostgreSql)
    {
        if (!TestEnvironment.TryGetUniqueDatabaseName(adminConnectionString, out var database))
            throw new InvalidOperationException("管理连接串包含重复、冲突或无效数据库别名（方言探针守卫）。");

        var allowed = string.IsNullOrEmpty(database)
            || database is "palddd_probe_admin" or "palddd_test_admin"
            || database.StartsWith("palddd_test_", StringComparison.Ordinal)
            || (isPostgreSql && database == "postgres")
            || (!isPostgreSql && database == "mysql");
        if (!allowed)
            throw new InvalidOperationException($"拒绝非专用管理数据库目标（方言探针守卫，{(isPostgreSql ? "PG" : "MySQL")}）。");
    }

    /// <summary>ownership marker 写入（参数化插入，原探针 OwnershipMarkerSql）。</summary>
    private static async Task CreateOwnershipMarkerAsync(DbConnection connection, string database, CancellationToken ct)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {OwnershipTableName} (marker VARCHAR(128) PRIMARY KEY)";
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO {OwnershipTableName}(marker) VALUES (@marker)";
        var parameter = insert.CreateParameter();
        parameter.ParameterName = "@marker";
        parameter.Value = database;
        insert.Parameters.Add(parameter);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>ownership marker 回读（原探针 HasOwnershipMarker——每次新开连接）。</summary>
    private static async Task<string?> TryReadOwnershipMarkerAsync(
        string probeConnectionString, bool isPostgreSql, CancellationToken ct)
    {
        await using var connection = CreateConnection(probeConnectionString, isPostgreSql);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT marker FROM {OwnershipTableName}";
        return Convert.ToString(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>DDL 真源定位：从测试输出目录向上找仓库内 docs/sql 文件（找不到即 fail——基建损坏非环境缺失）。</summary>
    private static string FindSchemaFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"未找到方言探针 DDL 真源 {relativePath}（需在仓库内执行测试）。");
    }

    // ─── 连接/执行辅助 ──────────────────────────────────────────

    private static DbConnection CreateConnection(string connectionString, bool isPostgreSql) =>
        isPostgreSql ? new NpgsqlConnection(connectionString) : new MySqlConnection(connectionString);

    private static string WithDatabase(string connectionString, string database)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Database"] = database;
        return builder.ConnectionString;
    }

    private static string BuildDropSql(string database, bool isPostgreSql) =>
        isPostgreSql ? $"DROP DATABASE {database} WITH (FORCE)" : $"DROP DATABASE {database}";

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql, bool isPostgreSql, CancellationToken ct)
    {
        await using var connection = CreateConnection(connectionString, isPostgreSql);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>整文件 DDL 单批执行（原探针 Exec 同款：Npgsql/MySqlConnector 均支持多语句批）。</summary>
    private static async Task ExecuteBatchAsync(DbConnection connection, string ddl, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ddl;
        await command.ExecuteNonQueryAsync(ct);
    }
}
