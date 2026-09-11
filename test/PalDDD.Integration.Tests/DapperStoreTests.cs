// ─────────────────────────────────────────────────────────────
// 🧪 Dapper 适配器集成测试 — SQLite 内存数据库
// ─────────────────────────────────────────────────────────────
// 覆盖 DapperOutboxStore / DapperInboxStore / DapperSagaStateStore / DapperEventLog
// 使用 SQLite :memory: 模式 — 零外部依赖，毫秒级执行
//
// 💡 测试隔离：
//   Dapper 的 DefaultTypeMap.MatchNamesWithUnderscores 和 TypeHandler 注册是全局静态状态。
//   本 fixture 在 InitializeAsync 保存旧值，DisposeAsync 恢复，避免污染其他测试类。
//   [Collection("Dapper")] 确保不与其他使用 Dapper 全局状态的测试并行。
//
// 💡 Dapper.AOT 与 SQLite 类型映射：
//   生产 Store 未启用 Dapper.AOT 拦截（[module:DapperAot] 为注释禁用态），走运行时经典 Dapper 路径。
//   SQLite TEXT 列需要运行时 TypeHandler 转换 Guid/DateTimeOffset（见 InitializeAsync 注册）。
//   Dapper.AOT 编译时拦截器不适用于这些 Store，TypeHandler 是必需的。
// ─────────────────────────────────────────────────────────────

using Dapper;
using Microsoft.Data.Sqlite;
using PalDDD.EventLog;
using PalDDD.Projections;
using PalDDD.Transactions;
using PalDDD.Dapper;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

/// <summary>Dapper 测试集合 — 序列化执行避免全局静态状态竞态（ClassCleanup 调 ResetTypeHandlers 清理全局状态）。
/// 注：TUnit 无 xUnit 的 [Collection] 概念，全局状态隔离靠 ClassInitialize/ClassCleanup 的静态生命周期保证；
/// [NotInParallel("dapper-global")]（ITM-253/F12）使本类测试互不并行执行，与类注释声称的"序列化执行"对齐。</summary>
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class DapperStoreTests
{
    private DbConnection _conn = null!;
    // 三十八轮 P2 备注（方言测试盲区）：原硬编码 Sqlite 使全部 Dapper 测试只跑 SQLite。
    // ⚠️ 当前仅支持 Sqlite——MySQL/PG 分支需配套连接工厂与方言 Schema（CreateSchemaAsync
    // 亦为 SQLite 专用），完整实现属后续任务；在此之前其他值显式失败而非静默错配。
    // MySQL/PG 路径的自动化验证由 DialectProbeTests 承载（42 断言，CI dialect-probe job）。
    private static readonly DapperDbType _dbType = ResolveDbType();

    private static DapperDbType ResolveDbType()
    {
        var raw = Environment.GetEnvironmentVariable("PALDDD_TEST_DAPPER_DB") ?? "Sqlite";
        if (Enum.TryParse<DapperDbType>(raw, ignoreCase: true, out var parsed) && parsed == DapperDbType.Sqlite)
            return parsed;
        throw new NotSupportedException(
            $"PALDDD_TEST_DAPPER_DB={raw}：DapperStoreTests 当前仅实现 Sqlite 分支（连接工厂/Schema 为 SQLite 专用）。"
            + "MySQL/PG 路径由 DialectProbeTests 覆盖（CI Testcontainers）。");
    }

    private static bool s_previousUnderscoreSetting;

    [Before(Class)]
    public static void ClassInitialize()
    {
        // 保存全局状态旧值，ClassCleanup 恢复
        s_previousUnderscoreSetting = global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores;

        // Dapper snake_case → PascalCase 映射（全局静态）
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        // 注册运行时 TypeHandler — SQLite TEXT 列需要转换为 Guid/DateTimeOffset
        // 生产 Store 未启用 Dapper.AOT 拦截（注释禁用态），走运行时经典 Dapper 路径，TypeHandler 生效
        var dtoHandler = new SqliteDateTimeOffsetTypeHandler();
        SqlMapper.AddTypeHandler(dtoHandler);
        SqlMapper.AddTypeHandler(typeof(DateTimeOffset), dtoHandler);
        SqlMapper.AddTypeHandler(typeof(DateTimeOffset?), dtoHandler);

        var guidHandler = new SqliteGuidTypeHandler();
        SqlMapper.AddTypeHandler(guidHandler);
        SqlMapper.AddTypeHandler(typeof(Guid), guidHandler);

        var ulidHandler = new SqliteUlidTypeHandler();
        SqlMapper.AddTypeHandler(ulidHandler);
        SqlMapper.AddTypeHandler(typeof(PalUlid), ulidHandler);
    }

    [After(Class)]
    public static void ClassCleanup()
    {
        // 恢复全局状态，避免污染其他测试类
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = s_previousUnderscoreSetting;
        SqlMapper.ResetTypeHandlers();
    }

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        await _conn.OpenAsync();

        // 建表
        await CreateSchemaAsync(_conn);
    }

    [After(Test)]
    public async Task CleanupAsync()
    {
        if (_conn is not null)
        {
            await _conn.CloseAsync();
            await _conn.DisposeAsync();
        }
    }

    // TST-205 平行副本声明：Dapper 栈与 PalORM 栈 schema 刻意分离（栈内演化独立），
    // 对齐需双改——见 MultiDialectSchema（PalORM.Tests）。
    private static async Task CreateSchemaAsync(DbConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE outbox_messages (
                id              TEXT PRIMARY KEY,
                type            TEXT NOT NULL,
                payload         BLOB NOT NULL,
                content_type    TEXT NOT NULL DEFAULT 'application/json',
                schema_version  INTEGER NOT NULL DEFAULT 1,
                status          INTEGER NOT NULL DEFAULT 0,
                retry_count     INTEGER NOT NULL DEFAULT 0,
                error           TEXT,
                created_at      TEXT NOT NULL,
                processed_at    TEXT,
                next_attempt_at TEXT,
                locked_by       TEXT,
                locked_until    TEXT,
                correlation_id  TEXT,
                causation_id    TEXT,
                trace_parent    TEXT,
                trace_state     TEXT
            );
            CREATE INDEX idx_outbox_status ON outbox_messages(status, next_attempt_at, locked_until);

            CREATE TABLE inbox_messages (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id            TEXT NOT NULL,
                consumer_name         TEXT NOT NULL,
                status                INTEGER NOT NULL DEFAULT 0,
                received_at           TEXT NOT NULL,
                processing_started_at TEXT,
                processed_at          TEXT,
                attempts              INTEGER NOT NULL DEFAULT 1,
                last_error            TEXT
            );
            CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id);

            CREATE TABLE saga_states (
                saga_id       TEXT PRIMARY KEY,
                current_state TEXT NOT NULL,
                status        INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT NOT NULL,
                completed_at  TEXT,
                error         TEXT,
                error_at      TEXT,
                version       INTEGER NOT NULL DEFAULT 0,
                saga_data     TEXT,
                leased_by     TEXT,
                leased_until  TEXT
            );

            CREATE TABLE events (
                global_position INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id        TEXT NOT NULL,
                event_name      TEXT NOT NULL,
                stream_name     TEXT NOT NULL,
                stream_version  INTEGER NOT NULL,
                schema_version  INTEGER NOT NULL DEFAULT 1,
                content_type    TEXT NOT NULL DEFAULT 'application/json',
                payload         BLOB NOT NULL,
                metadata        BLOB,
                recorded_at     TEXT NOT NULL,
                actor_id        TEXT,
                reason          TEXT,
                correlation_id  TEXT,
                causation_id    TEXT,
                trace_parent    TEXT,
                trace_state     TEXT
            );
            CREATE UNIQUE INDEX idx_events_stream ON events(stream_name, stream_version);

            CREATE TABLE projection_checkpoints (
                projection_name TEXT NOT NULL,
                source_name     TEXT NOT NULL,
                position        TEXT NOT NULL,
                status          INTEGER NOT NULL,
                updated_at      TEXT NOT NULL,
                lease_until     TEXT NOT NULL,
                revision        INTEGER NOT NULL DEFAULT 0,
                error           TEXT,
                PRIMARY KEY (projection_name, source_name, position)
            );
            CREATE INDEX idx_projection_checkpoints_status
                ON projection_checkpoints(projection_name, source_name, status);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    // DapperOutboxStore 测试
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Outbox_AddMessage_ThenGetPending(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("test.event.v1");

        store.AddMessage(msg);

        var pending = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Type).IsEqualTo("test.event.v1");
        await Assert.That(pending[0].Status).IsEqualTo(OutboxStatus.Pending);
    }

    // ─────────────────────────────────────────────────────────────
    // 二轮评审 T5：DapperUnitOfWork ambient 事务贯通行为回归。
    // ⚠️ 能力边界（S3 反向验证实证 + 对齐 PalOrmAmbientTransaction 声明）：SQLite 引擎级
    // 事务使同连接命令自动参与活动事务——本测试对"ambient 挂接缺失"不可观测（禁用
    // DapperAmbientTransaction.Set 后测试仍过，已实测）。它验证的是行为语义正确性
    // （回滚丢弃/提交持久化/边界清理），真正的断链探测器在 DialectProbeTests
    // 的 AmbientTx 族（MySQL/PG 严格校验，CI dialect-probe job 承载）。
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task UnitOfWorkRollback_DIAppliedStoreWrite_IsDiscarded(CancellationToken cancellationToken)
    {
        // DI 解析形态：与 DapperServiceCollectionExtensions 的容器构造完全一致（无事务参数）
        var store = new DapperOutboxStore(_conn, _dbType);
        await using var uow = new DapperUnitOfWork(_conn);

        await uow.BeginTransactionAsync(cancellationToken);
        store.AddMessage(CreateOutboxMessage("tx.rollback.v1"));
        await uow.RollbackAsync(cancellationToken);

        // 回滚后消息不可见——若 store 未挂接 UoW 事务（断链），写入已自动提交、此处查到 1 条
        var pending = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task UnitOfWorkCommit_DIAppliedStoreWrite_Persists(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        await using var uow = new DapperUnitOfWork(_conn);

        await uow.BeginTransactionAsync(cancellationToken);
        store.AddMessage(CreateOutboxMessage("tx.commit.v1"));
        await uow.CommitAsync(cancellationToken);

        // 提交后持久化且 ambient 通道已清空（后续写入不再误挂已终结事务）
        store.AddMessage(CreateOutboxMessage("tx.after-commit.v1"));
        var pending = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(pending.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Outbox_AddMessage_PersistsDomainCreatedAt(CancellationToken cancellationToken)
    {
        // ITM-634：created_at 持久化领域赋值 OutboxMessage.CreatedAt（对齐 PalORM/EFCore/InMemory
        // 三栈）。Store 时钟显式设为与领域值不同的时刻，断言落库值取领域 CreatedAt——原用例
        // Outbox_AddMessage_UsesInjectedTimeProvider 锁定的恰是被修复的"Store 时钟覆盖"分叉语义。
        var createdAt = DateTimeOffset.Parse("2026-06-27T10:00:00Z", CultureInfo.InvariantCulture);
        var storeClock = new FixedTimeProvider(createdAt.AddHours(1));
        var store = new DapperOutboxStore(_conn, _dbType, timeProvider: storeClock);
        var msg = CreateOutboxMessage("test.event.v1", createdAt);

        store.AddMessage(msg);

        var persisted = await ReadScalarAsync<DateTimeOffset>(
            "SELECT created_at FROM outbox_messages WHERE id=$id",
            ("$id", msg.Id));
        await Assert.That(persisted).IsEqualTo(createdAt);
    }

    [Test]
    public async Task Outbox_AddMessagesAsync_PersistsEachDomainCreatedAt(CancellationToken cancellationToken)
    {
        // ITM-634：批量路径持久化各消息领域 CreatedAt（对齐三栈）——原实现整批用批次起始
        // Store 时钟覆盖，同批各行时间被抹平为同刻且与领域值分叉。
        var t0 = DateTimeOffset.Parse("2026-06-27T10:01:00Z", CultureInfo.InvariantCulture);
        var store = new DapperOutboxStore(_conn, _dbType,
            timeProvider: new FixedTimeProvider(t0.AddHours(1)));
        var messages = new List<OutboxMessage>
        {
            CreateOutboxMessage("test.event.v0", t0),
            CreateOutboxMessage("test.event.v1", t0.AddMinutes(5)),
        };

        await store.AddMessagesAsync(messages);

        var createdTimes = await ReadScalarsAsync<DateTimeOffset>(
            "SELECT created_at FROM outbox_messages ORDER BY type");
        await Assert.That(createdTimes[0]).IsEqualTo(t0);
        await Assert.That(createdTimes[1]).IsEqualTo(t0.AddMinutes(5));
    }

    [Test]
    public async Task Outbox_AddMessagesAsync_BulkInsert(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var messages = Enumerable.Range(0, 5)
            .Select(i => CreateOutboxMessage($"test.event.v{i}"))
            .ToList();

        var count = await store.AddMessagesAsync(messages);

        await Assert.That(count).IsEqualTo(5);
        var pending = await store.GetPendingMessagesAsync(20, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(pending.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Outbox_AddMessagesAsync_EmptyList(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var count = await store.AddMessagesAsync([]);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task Outbox_LeasePendingMessages_AtomicAcquisition(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        store.AddMessage(CreateOutboxMessage("a.v1"));

        var leased = await store.LeasePendingMessagesAsync(
            10, "test-owner", TimeSpan.FromMinutes(5), new OutboxOptions().MaxRetryCount, cancellationToken);

        await Assert.That(leased).Count().IsEqualTo(1);
        await Assert.That(leased[0].LockedBy).IsEqualTo("test-owner");
        await Assert.That(leased[0].LockedUntil).IsNotNull();
    }

    [Test]
    public async Task Outbox_LeasePendingMessages_SkipLocked(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);

        // 第一次租约获取
        await store.LeasePendingMessagesAsync(
            10, "owner-1", TimeSpan.FromMinutes(5), new OutboxOptions().MaxRetryCount, cancellationToken);

        // 第二次 — 应该获取不到（已被锁定）
        var leased2 = await store.LeasePendingMessagesAsync(
            10, "owner-2", TimeSpan.FromMinutes(5), new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(leased2).IsEmpty();
    }

    [Test]
    public async Task Outbox_MarkProcessed(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);
        var pending = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        var target = pending[0];

        var processedAt = TimeProvider.System.GetUtcNow();
        store.MarkProcessed(target, processedAt);
        await store.SaveChangesAsync(cancellationToken);

        var after = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(after).IsEmpty();
    }

    [Test]
    public async Task Outbox_MarkProcessed_LeaseTakenOver_StaleWriteRejected(CancellationToken cancellationToken)
    {
        // 三十四轮 ITM-210 租约 token 回归：租约被重租（locked_until 变化 = 新 token）后，
        // 旧 worker 终态写必须影响 0 行——原裸 WHERE id=@id 对重租/同 owner 复用零防护
        var store = new DapperOutboxStore(_conn, _dbType);
        store.AddMessage(CreateOutboxMessage("fence.v1"));
        var leased = await store.LeasePendingMessagesAsync(
            10, "stale-worker", TimeSpan.FromMinutes(5), new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(leased).Count().IsEqualTo(1);
        var staleMsg = leased[0];

        // 模拟租约被重租（token 变化：locked_until 更晚、持有者更换）——
        // 用与生产路径一致的 SQLite TEXT 编码（Ulid 字符串 / "O" 时间串），ADO.NET 辅助方法避开 DAP005
        var idParam = staleMsg.Id.ToString();
        await ExecuteNonQueryAsync(
            "UPDATE outbox_messages SET locked_by=$owner, locked_until=$until WHERE id=$id",
            ("$owner", "new-worker"),
            ("$until", DateTimeOffset.UtcNow.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture)),
            ("$id", idParam));

        store.MarkProcessed(staleMsg, TimeProvider.System.GetUtcNow());

        // 三十八轮统一（状态列 int 化）：断言改读数值——OutboxStatus.Processed=1
        var status = await ReadScalarAsync<long>(
            "SELECT status FROM outbox_messages WHERE id=$id", ("$id", idParam));
        await Assert.That(status).IsNotEqualTo(1L);
        var currentOwner = await ReadScalarAsync<string>(
            "SELECT locked_by FROM outbox_messages WHERE id=$id", ("$id", idParam));
        await Assert.That(currentOwner).IsEqualTo("new-worker"); // 新租约未被旧写清除
    }

    [Test]
    public async Task Outbox_MarkDead(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);
        var pending = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);

        var deadAt = TimeProvider.System.GetUtcNow();
        store.MarkDead(pending[0], "permanent failure", deadAt);

        var after = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(after).IsEmpty();
    }

    [Test]
    public async Task Outbox_MarkProcessed_ClearsErrorRetryAndLeaseState(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);
        var leasedList = await store.LeasePendingMessagesAsync(
            10, "owner-1", TimeSpan.FromMinutes(5), new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(leasedList).Count().IsEqualTo(1);
        var leased = leasedList[0];
        await ExecuteNonQueryAsync(
            "UPDATE outbox_messages SET error='old', next_attempt_at=$next WHERE id=$id",
            ("$next", TimeProvider.System.GetUtcNow().AddMinutes(1)),
            ("$id", msg.Id));
        store.MarkProcessed(leased, TimeProvider.System.GetUtcNow());

        var row = await ReadOutboxCleanupStateAsync(msg.Id);
        await Assert.That(row.Error).IsNull();
        await Assert.That(row.NextAttemptAt).IsNull();
        await Assert.That(row.LockedBy).IsNull();
        await Assert.That(row.LockedUntil).IsNull();
    }

    [Test]
    public async Task Outbox_MarkDead_ClearsRetryAndLeaseState(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);
        var leasedList = await store.LeasePendingMessagesAsync(
            10, "owner-1", TimeSpan.FromMinutes(5), new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(leasedList).Count().IsEqualTo(1);
        var leased = leasedList[0];
        await ExecuteNonQueryAsync(
            "UPDATE outbox_messages SET next_attempt_at=$next WHERE id=$id",
            ("$next", TimeProvider.System.GetUtcNow().AddMinutes(1)),
            ("$id", msg.Id));
        store.MarkDead(leased, "permanent", TimeProvider.System.GetUtcNow());

        var row = await ReadOutboxCleanupStateAsync(msg.Id);
        await Assert.That(row.NextAttemptAt).IsNull();
        await Assert.That(row.LockedBy).IsNull();
        await Assert.That(row.LockedUntil).IsNull();
    }

    [Test]
    public async Task Outbox_ReleaseForRetry(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);
        var pending = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);

        var nextAttempt = TimeProvider.System.GetUtcNow().AddSeconds(30);
        store.ReleaseForRetry(pending[0], "transient failure", nextAttempt);

        // 由于 next_attempt_at 在未来，GetPendingMessagesAsync 不应返回
        var after = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(after).IsEmpty();
    }

    [Test]
    public async Task Outbox_DeadLetterFilter_Rh10(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);

        // 重试 10 次，每次 ReleaseForRetry 在 SQL 中原子递增 RetryCount
        // 默认 MaxRetryCount=10，达到上限后不再返回待处理消息
        for (int i = 0; i < 10; i++)
        {
            var pending = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
            if (pending.Count == 0) break;
            var target = pending[0];

            store.ReleaseForRetry(target, $"retry {i + 1}",
                TimeProvider.System.GetUtcNow().AddMilliseconds(-1));
        }

        // TST-116/201：Dapper 版 ReleaseForRetry 在 SQL 原子递增 retry_count（内存对象不回写），
        // 从 DB 读验证递增确实发生 10 次——对齐 PalOrmOutboxStoreTests 同名测试的 RetryCount==10 断言
        var retryCount = await ReadScalarAsync<long>(
            "SELECT retry_count FROM outbox_messages WHERE id=$id", ("$id", msg.Id));
        await Assert.That(retryCount).IsEqualTo(10L);

        var final = await store.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(final).IsEmpty();
    }

    [Test]
    public async Task Outbox_GetPendingMessages_UsesConfiguredMaxRetryCount(CancellationToken cancellationToken)
    {
        var store = new DapperOutboxStore(_conn, _dbType);
        var msg = CreateOutboxMessage("a.v1");
        store.AddMessage(msg);

        var pending = await store.GetPendingMessagesAsync(10, 1, cancellationToken);
        await Assert.That(pending).Count().IsEqualTo(1);
        var target = pending[0];
        store.ReleaseForRetry(target, "retry 1", TimeProvider.System.GetUtcNow().AddMilliseconds(-1));

        var final = await store.GetPendingMessagesAsync(10, 1, cancellationToken);
        await Assert.That(final).IsEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Inbox_TryStartProcessing_FirstAttempt(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();

        var result = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.ConsumerName).IsEqualTo("test-consumer");
        await Assert.That(result.MessageId).IsEqualTo("msg-001");
        await Assert.That(result.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(result.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Inbox_TryStartProcessing_Duplicate(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();

        var first = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(first).IsNotNull();

        await store.MarkProcessedAsync(first, now, cancellationToken);

        var second = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Inbox_TryStartProcessing_DuplicateInsertDoesNotThrow(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();

        var first = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(first).IsNotNull();

        var duplicate = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(duplicate).IsNull();
        var count = await ReadScalarAsync<long>(
            "SELECT COUNT(*) FROM inbox_messages WHERE consumer_name=$consumer AND message_id=$message",
            ("$consumer", "test-consumer"),
            ("$message", "msg-001"));
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Inbox_TryStartProcessing_StillProcessing(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();

        var first = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(first).IsNotNull();

        var second = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Inbox_MarkProcessedAsync(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();

        var result = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(result).IsNotNull();

        await store.MarkProcessedAsync(result, now, cancellationToken);

        var after = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(after).IsNull();
    }

    [Test]
    public async Task Inbox_MarkFailedAsync(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();

        var result = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(result).IsNotNull();

        await store.MarkFailedAsync(result, "simulated crash", cancellationToken);

        var retry = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now.AddSeconds(1), TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(retry).IsNotNull();
        await Assert.That(retry.Attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Inbox_StaleFailed_DoesNotOverwriteProcessed(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();
        var first = await store.TryStartProcessingAsync(
            "test-consumer", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(first).IsNotNull();

        await store.MarkProcessedAsync(first, now, cancellationToken);
        await store.MarkFailedAsync(first, "stale handler failed", cancellationToken);

        // 三十八轮统一（状态列 int 化）：断言改读数值——InboxStatus.Processed=2
        var status = await ReadScalarAsync<long>(
            "SELECT status FROM inbox_messages WHERE id=$id",
            ("$id", first.Id));
        await Assert.That(status).IsEqualTo((long)InboxStatus.Processed);
    }

    [Test]
    public async Task Inbox_DifferentConsumers_Independent(CancellationToken cancellationToken)
    {
        var store = new DapperInboxStore(_conn, _dbType);
        var now = TimeProvider.System.GetUtcNow();

        var a = await store.TryStartProcessingAsync(
            "consumer-a", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(a).IsNotNull();
        await store.MarkProcessedAsync(a, now, cancellationToken);

        var b = await store.TryStartProcessingAsync(
            "consumer-b", "msg-001", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(b).IsNotNull();
        await Assert.That(b.ConsumerName).IsEqualTo("consumer-b");
    }

    // ═══════════════════════════════════════════════════════════════
    // DapperSagaStateStore 测试
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Saga_SaveChangesAsync_InsertNew(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn);
        var state = new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Initial",
            Status = SagaStatus.Active,
            CreatedAt = TimeProvider.System.GetUtcNow()
        };

        var rows = await store.SaveChangesAsync(state, cancellationToken);
        await Assert.That(rows).IsEqualTo(1);

        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.CurrentState).IsEqualTo("Initial");
        await Assert.That(loaded.Status).IsEqualTo(SagaStatus.Active);
    }

    [Test]
    public async Task Saga_SaveChangesAsync_Upsert(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn);
        var sagaId = PalUlid.New();
        var state = new TestSagaState
        {
            SagaId = sagaId,
            CurrentState = "Initial",
            Status = SagaStatus.Active,
            CreatedAt = TimeProvider.System.GetUtcNow()
        };

        await store.SaveChangesAsync(state, cancellationToken);

        state.CurrentState = "Processing";
        state.Status = SagaStatus.Completed;
        state.CompletedAt = TimeProvider.System.GetUtcNow();
        state.Version = 0;

        var rows = await store.SaveChangesAsync(state, cancellationToken);
        await Assert.That(rows).IsEqualTo(1);

        var loaded = await store.GetByIdAsync(sagaId, cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.CurrentState).IsEqualTo("Processing");
        await Assert.That(loaded.Status).IsEqualTo(SagaStatus.Completed);
    }

    [Test]
    public async Task Saga_GetActiveSagas_ReturnsOnlyActiveStates(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn);
        var now = TimeProvider.System.GetUtcNow();
        await store.SaveChangesAsync(new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Active",
            Status = SagaStatus.Active,
            CreatedAt = now
        }, cancellationToken);
        await store.SaveChangesAsync(new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Completed",
            Status = SagaStatus.Completed,
            CreatedAt = now.AddMinutes(1)
        }, cancellationToken);
        await store.SaveChangesAsync(new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Compensated",
            Status = SagaStatus.Compensated,
            CreatedAt = now.AddMinutes(2)
        }, cancellationToken);
        await store.SaveChangesAsync(new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "CompensationFailed",
            Status = SagaStatus.CompensationFailed,
            CreatedAt = now.AddMinutes(3)
        }, cancellationToken);
        await store.SaveChangesAsync(new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "DeadLettered",
            Status = SagaStatus.DeadLettered,
            CreatedAt = now.AddMinutes(4)
        }, cancellationToken);

        var active = await store.GetActiveSagasAsync(10, cancellationToken);

        await Assert.That(active).Count().IsEqualTo(1);
        var single = active[0];
        await Assert.That(single.CurrentState).IsEqualTo("Active");
    }

    [Test]
    public async Task Saga_SaveChangesAsync_OverlongEmojiError_TruncatesWithoutSplittingSurrogatePair(CancellationToken cancellationToken)
    {
        // v29 P3（S12）：存储层截断的 UTF-16 代理对守卫端到端验证——超长含 emoji 的 Error
        // 在 [..2040] 切片点恰落在高代理上时（'a'*2039 + 🎉 长度 2041），守卫回退一位
        // 防孤立高代理入库（镜像 FailureReasonTests.Normalize/Truncate 同名用例形态；
        // INSERT 与 UPDATE 共用方法开头收口，测 INSERT 路径即可覆盖）
        var store = new DapperSagaStateStore<TestSagaState>(_conn);
        var state = new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Initial",
            Status = SagaStatus.CompensationFailed,
            CreatedAt = TimeProvider.System.GetUtcNow(),
            Error = new string('a', 2039) + "🎉",
            ErrorAt = TimeProvider.System.GetUtcNow()
        };

        await store.SaveChangesAsync(state, cancellationToken);

        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);
        await Assert.That(loaded!.Error).IsNotNull();
        // 末字符必须是完整 'a'（高代理回退一位），长度 2039 而非 2040
        await Assert.That(loaded.Error!.Length).IsEqualTo(2039);
        await Assert.That(char.IsHighSurrogate(loaded.Error[^1])).IsFalse();
    }

    [Test]
    public async Task Saga_SaveChangesAsync_PersistsFullStateSnapshot(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn, jsonTypeInfo: DapperStoreJsonContext.Default.TestSagaState);
        var state = new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "PaymentReserved",
            Status = SagaStatus.Active,
            CreatedAt = TimeProvider.System.GetUtcNow(),
            CustomerId = "customer-001",
            ErrorAt = TimeProvider.System.GetUtcNow().AddSeconds(3)
        };
        state.StepStartedAt["ReservePayment"] = state.CreatedAt.AddSeconds(1);
        state.ExecutedStepKeys.Add("ReservePayment");

        await store.SaveChangesAsync(state, cancellationToken);

        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);
        await Assert.That(loaded!.CustomerId).IsEqualTo("customer-001");
        await Assert.That(loaded.StepStartedAt["ReservePayment"]).IsEqualTo(state.CreatedAt.AddSeconds(1));
        await Assert.That(loaded.ExecutedStepKeys).Count().IsEqualTo(1);
        await Assert.That(loaded.ExecutedStepKeys.First()).IsEqualTo("ReservePayment");
        await Assert.That(loaded.ErrorAt).IsEqualTo(state.ErrorAt);
    }

    [Test]
    public async Task Saga_SaveChangesAsync_SuccessfulUpdateIncrementsVersionInMemory(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn, jsonTypeInfo: DapperStoreJsonContext.Default.TestSagaState);
        var state = new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Initial",
            Status = SagaStatus.Active,
            CreatedAt = TimeProvider.System.GetUtcNow()
        };
        await store.SaveChangesAsync(state, cancellationToken);

        state.CurrentState = "Completed";
        state.Status = SagaStatus.Completed;
        var rows = await store.SaveChangesAsync(state, cancellationToken);

        await Assert.That(rows).IsEqualTo(1);
        await Assert.That(state.Version).IsEqualTo(1);
    }

    [Test]
    public async Task Saga_SaveChangesAsync_StaleVersionDoesNotOverwrite(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn, jsonTypeInfo: DapperStoreJsonContext.Default.TestSagaState);
        var state = new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Initial",
            Status = SagaStatus.Active,
            CreatedAt = TimeProvider.System.GetUtcNow()
        };
        await store.SaveChangesAsync(state, cancellationToken);
        state.CurrentState = "Winner";
        await store.SaveChangesAsync(state, cancellationToken);

        state.Version = 0;
        state.CurrentState = "Stale";
        var rows = await store.SaveChangesAsync(state, cancellationToken);

        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);
        await Assert.That(rows).IsEqualTo(0);
        await Assert.That(loaded!.CurrentState).IsEqualTo("Winner");
    }

    [Test]
    public async Task Saga_LeaseActiveSagas_OnlyOneOwnerGetsActiveSaga(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn, jsonTypeInfo: DapperStoreJsonContext.Default.TestSagaState);
        var state = new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Active",
            Status = SagaStatus.Active,
            CreatedAt = TimeProvider.System.GetUtcNow()
        };
        await store.SaveChangesAsync(state, cancellationToken);

        var first = await store.LeaseActiveSagasAsync("owner-1", TimeSpan.FromMinutes(5), 10, cancellationToken);
        var second = await store.LeaseActiveSagasAsync("owner-2", TimeSpan.FromMinutes(5), 10, cancellationToken);

        await Assert.That(first).Count().IsEqualTo(1);
        var leased = first[0];
        await Assert.That(leased.LeasedBy).IsEqualTo("owner-1");
        await Assert.That(leased.LeasedUntil).IsNotNull();
        await Assert.That(second).IsEmpty();
    }

    // v53 P2：租约即换代——租约 UPDATE 递增 version（对齐 EFCore BumpVersion/InMemory v25 B1）。
    // 修复前租约不动 version：worker A 的内存快照（version=N）在租约被 B 抢走后仍与 DB 真值
    // 相等，A 的保存穿透覆盖 B 的活跃租约（fencing 失效）；修复后重租即换代，A 保存必 0 行
    [Test]
    public async Task Saga_LeaseTakeover_BumpsVersion_StaleHolderSaveRejected(CancellationToken cancellationToken)
    {
        var store = new DapperSagaStateStore<TestSagaState>(_conn, jsonTypeInfo: DapperStoreJsonContext.Default.TestSagaState);
        var state = new TestSagaState
        {
            SagaId = PalUlid.New(),
            CurrentState = "Active",
            Status = SagaStatus.Active,
            CreatedAt = TimeProvider.System.GetUtcNow()
        };
        await store.SaveChangesAsync(state, cancellationToken);
        // worker A 租约（version 0→1），回读快照 A 内存 version=1
        var leasedByA = await store.LeaseActiveSagasAsync("owner-A", TimeSpan.FromMinutes(5), 10, cancellationToken);
        await Assert.That(leasedByA).Count().IsEqualTo(1);
        var snapshotA = leasedByA[0];

        // 租约过期（直接清空模拟超时）后 worker B 重租（version 1→2）
        //（原生 DbCommand 直改——绕开 Dapper.AOT 的 DAP005 拦截，与 CreateSchemaAsync 同模式）
        var expireCmd = _conn.CreateCommand();
        expireCmd.CommandText = "UPDATE saga_states SET leased_until = @past WHERE saga_id = @id";
        var pastParam = expireCmd.CreateParameter();
        pastParam.ParameterName = "@past";
        pastParam.Value = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        expireCmd.Parameters.Add(pastParam);
        var idParam = expireCmd.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = state.SagaId.ToString();
        expireCmd.Parameters.Add(idParam);
        await expireCmd.ExecuteNonQueryAsync(cancellationToken);
        var leasedByB = await store.LeaseActiveSagasAsync("owner-B", TimeSpan.FromMinutes(5), 10, cancellationToken);
        await Assert.That(leasedByB).Count().IsEqualTo(1);
        await Assert.That(leasedByB[0].Version).IsEqualTo(snapshotA.Version + 1);

        // A 用过期快照保存：修复前 version 匹配 DB 真值（租约没动 version）保存成功覆盖 B；
        // 修复后 A 的 version 落后，保存 0 行被拒
        snapshotA.CurrentState = "Stale-A";
        var rows = await store.SaveChangesAsync(snapshotA, cancellationToken);
        await Assert.That(rows).IsEqualTo(0);

        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);
        await Assert.That(loaded!.LeasedBy).IsEqualTo("owner-B");
    }

    // ═══════════════════════════════════════════════════════════════
    // DapperProjectionCheckpointStore 测试
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task ProjectionCheckpoint_TryStartAsync_CreatesProcessingCheckpoint(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, _dbType);
        var now = DateTimeOffset.Parse("2026-06-27T11:00:00Z", CultureInfo.InvariantCulture);

        var checkpoint = await store.TryStartAsync(
            "ordering.order-summary", "orders", "0", now, TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(checkpoint).IsNotNull();
        await Assert.That(checkpoint.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
        await Assert.That(checkpoint.LeaseUntil).IsEqualTo(now.AddMinutes(5));
    }

    [Test]
    public async Task ProjectionCheckpoint_TryStartAsync_SkipsActiveLease(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, _dbType);
        var now = DateTimeOffset.Parse("2026-06-27T11:01:00Z", CultureInfo.InvariantCulture);

        await store.TryStartAsync("ordering.order-summary", "orders", "1", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        var second = await store.TryStartAsync("ordering.order-summary", "orders", "1", now.AddMinutes(1), TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task ProjectionCheckpoint_TryStartAsync_ReclaimsExpiredLease(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, _dbType);
        var now = DateTimeOffset.Parse("2026-06-27T11:02:00Z", CultureInfo.InvariantCulture);

        await store.TryStartAsync("ordering.order-summary", "orders", "2", now, TimeSpan.FromMinutes(1),
            cancellationToken);
        var reclaimed = await store.TryStartAsync("ordering.order-summary", "orders", "2", now.AddMinutes(2), TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(reclaimed).IsNotNull();
        await Assert.That(reclaimed.LeaseUntil).IsEqualTo(now.AddMinutes(7));
        await Assert.That(reclaimed.Revision).IsEqualTo(2);
    }

    [Test]
    public async Task ProjectionCheckpoint_MarkCompleted_PreventsReprocessing(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, _dbType);
        var now = DateTimeOffset.Parse("2026-06-27T11:03:00Z", CultureInfo.InvariantCulture);
        var checkpoint = await store.TryStartAsync("ordering.order-summary", "orders", "3", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(checkpoint).IsNotNull();

        await store.MarkCompletedAsync(checkpoint, now.AddSeconds(1), cancellationToken);
        var next = await store.TryStartAsync("ordering.order-summary", "orders", "3", now.AddMinutes(10), TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(next).IsNull();
        var saved = await store.GetAsync("ordering.order-summary", "orders", "3", cancellationToken);
        await Assert.That(saved).IsNotNull();
        await Assert.That(saved.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);
    }

    // v69 守卫锁定（v62 MarkCompleted 的 status<>1——既有 PreventsReprocessing 测试只锁
    // TryStart 对 Completed 的拒绝，不锁直调 Failed 快照的 MarkCompleted 不翻转）
    [Test]
    public async Task ProjectionCheckpoint_MarkCompleted_FailedSnapshot_DoesNotFlip(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, _dbType);
        var now = DateTimeOffset.Parse("2026-06-27T11:05:00Z", CultureInfo.InvariantCulture);
        var checkpoint = await store.TryStartAsync("ordering.failed-flip", "orders", "1", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(checkpoint).IsNotNull();
        await store.MarkFailedAsync(checkpoint, "boom", now.AddSeconds(1), cancellationToken);

        // Failed 快照直调 MarkCompleted——status<>1 守卫使 SQL 0 行，DB 不翻转
        await store.MarkCompletedAsync(checkpoint, now.AddMinutes(2), cancellationToken);

        var saved = await store.GetAsync("ordering.failed-flip", "orders", "1", cancellationToken);
        await Assert.That(saved!.Status).IsEqualTo(ProjectionCheckpointStatus.Failed);
    }

    [Test]
    public async Task ProjectionCheckpoint_Reset_RemovesProjectionSourceRows(CancellationToken cancellationToken)
    {
        var store = new DapperProjectionCheckpointStore(_conn, _dbType);
        var now = DateTimeOffset.Parse("2026-06-27T11:04:00Z", CultureInfo.InvariantCulture);
        await store.TryStartAsync("ordering.order-summary", "orders", "4", now, TimeSpan.FromMinutes(5),
            cancellationToken);
        await store.TryStartAsync("ordering.order-summary", "invoices", "4", now, TimeSpan.FromMinutes(5),
            cancellationToken);

        await store.ResetAsync("ordering.order-summary", "orders", cancellationToken);

        await Assert.That(await store.GetAsync("ordering.order-summary", "orders", "4", cancellationToken)).IsNull();
        await Assert.That(await store.GetAsync("ordering.order-summary", "invoices", "4", cancellationToken)).IsNotNull();
    }

    // ═══════════════════════════════════════════════════════════════
    // DapperEventLog 测试
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task EventLog_AppendAsync_UsesInjectedTimeProvider(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Parse("2026-06-27T10:02:00Z", CultureInfo.InvariantCulture);
        var clock = new FixedTimeProvider(now);
        var log = new DapperEventLog(_conn, null, _dbType, clock);
        var @event = new EventData(
            PalUlid.New(), "test.event.v1", 1, "application/json",
            "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty);

        await log.AppendAsync("test-stream", ExpectedStreamVersion.NoStream, [@event],
            cancellationToken);

        var recordedAt = await ReadScalarAsync<DateTimeOffset>(
            "SELECT recorded_at FROM events WHERE stream_name=$stream",
            ("$stream", "test-stream"));
        await Assert.That(recordedAt).IsEqualTo(now);
    }

    [Test]
    public async Task EventLog_AppendAsync_NoStream(CancellationToken cancellationToken)
    {
        var log = new DapperEventLog(_conn, null, _dbType);
        var events = new List<EventData>
        {
            new(
                PalUlid.New(),
                "test.event.v1",
                schemaVersion: 1,
                contentType: "application/json",
                payload: "{}"u8.ToArray(),
                metadata: "{}"u8.ToArray(),
                audit: EventAuditMetadata.Empty)
        };

        var result = await log.AppendAsync(
            "test-stream", ExpectedStreamVersion.NoStream, events,
            cancellationToken);

        await Assert.That(EventsWritten(result)).IsEqualTo(1);
        await Assert.That(result.FirstStreamVersion).IsEqualTo(0);
    }

    [Test]
    public async Task EventLog_AppendAsync_ConcurrencyCheck(CancellationToken cancellationToken)
    {
        var log = new DapperEventLog(_conn, null, _dbType);
        var event1 = new EventData(
            PalUlid.New(), "test.event.v1", 1, "application/json",
            "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty);

        var r1 = await log.AppendAsync(
            "test-stream", ExpectedStreamVersion.NoStream, [event1],
            cancellationToken);
        await Assert.That(EventsWritten(r1)).IsEqualTo(1);

        var event2 = new EventData(
            PalUlid.New(), "test.event.v2", 1, "application/json",
            "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty);

        var r2 = await log.AppendAsync(
            "test-stream", ExpectedStreamVersion.Exact(0), [event2],
            cancellationToken);
        await Assert.That(EventsWritten(r2)).IsEqualTo(1);
        await Assert.That(r2.FirstStreamVersion).IsEqualTo(1);
    }

    [Test]
    public async Task EventLog_AppendAsync_BatchDuplicateEventId_ThrowsOriginalNotConcurrency(CancellationToken cancellationToken)
    {
        // P3 回归（九轮）：批内两条事件共享 EventId 时，冲突重查的分类基线是"本批已写入的
        // 最高版本"（version-2）——误用 expectedVersion 会把批内重复误报为并发冲突。
        // 前置声明（TST-202）：生产默认 schema 无 event_id 唯一索引时该路径不触发
        // （默认 DDL 只约束 stream+version，生产可加此强化）；本测试自建索引验证分类逻辑
        // （ITM-247 传感器形态）。
        await using (var indexCommand = _conn.CreateCommand())
        {
            indexCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_events_event_id ON events(event_id)";
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var log = new DapperEventLog(_conn, null, _dbType);
        var duplicated = PalUlid.New();
        var events = new List<EventData>
        {
            new(duplicated, "test.event.v1", 1, "application/json",
                "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty),
            new(duplicated, "test.event.v1", 1, "application/json",
                "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty)
        };

        // 断言：抛原始唯一约束 DbException，而非 EventStreamConcurrencyException
        await Assert.That(async () => await log.AppendAsync(
            "dup-stream", ExpectedStreamVersion.NoStream, events, cancellationToken))
            .Throws<System.Data.Common.DbException>();
    }

    [Test]
    public async Task EventLog_ReadStreamAsync(CancellationToken cancellationToken)
    {
        var log = new DapperEventLog(_conn, null, _dbType);
        var event1 = new EventData(
            PalUlid.New(), "test.event.v1", 1, "application/json",
            "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty);

        await log.AppendAsync("test-stream", ExpectedStreamVersion.NoStream, [event1],
            cancellationToken);

        var events = new List<RecordedEvent>();
        await foreach (var e in log.ReadStreamAsync("test-stream"))
            events.Add(e);

        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].EventName).IsEqualTo("test.event.v1");
        await Assert.That(events[0].StreamVersion).IsEqualTo(0);
    }

    [Test]
    public async Task EventLog_ReadAllAsync(CancellationToken cancellationToken)
    {
        var log = new DapperEventLog(_conn, null, _dbType);
        var event1 = new EventData(
            PalUlid.New(), "test.event.v1", 1, "application/json",
            "{}"u8.ToArray(), "{}"u8.ToArray(), EventAuditMetadata.Empty);

        await log.AppendAsync("stream-a", ExpectedStreamVersion.NoStream, [event1],
            cancellationToken);
        await log.AppendAsync("stream-b", ExpectedStreamVersion.NoStream, [event1],
            cancellationToken);

        var events = new List<RecordedEvent>();
        await foreach (var e in log.ReadAllAsync())
            events.Add(e);

        await Assert.That(events.Count).IsEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════════
    // 测试辅助
    // ═══════════════════════════════════════════════════════════════

    private static OutboxMessage CreateOutboxMessage(string type, DateTimeOffset? createdAt = null) => new()
    {
        Type = type,
        Payload = "test-payload"u8.ToArray(),
        ContentType = "application/json",
        SchemaVersion = 1,
        Status = OutboxStatus.Pending,
        CreatedAt = createdAt ?? TimeProvider.System.GetUtcNow()
    };

    public sealed class TestSagaState : SagaState
    {
        public string CustomerId { get; set; } = string.Empty;
    }

    private static long EventsWritten(AppendEventsResult result)
        => result.LastStreamVersion - result.FirstStreamVersion + 1;

    private static object ConvertParameterValue(object? value)
    {
        if (value is PalUlid ulid)
            return ulid.ToString();
        return value ?? DBNull.Value;
    }

    private async Task<T> ReadScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = _conn.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = ConvertParameterValue(value);
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        if (typeof(T) == typeof(DateTimeOffset) && result is string text)
            return (T)(object)DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        return (T)result!;
    }

    private async Task ExecuteNonQueryAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = _conn.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = ConvertParameterValue(value);
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<List<T>> ReadScalarsAsync<T>(string sql)
    {
        await using var command = _conn.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var values = new List<T>();
        while (await reader.ReadAsync(CancellationToken.None))
            values.Add(await reader.GetFieldValueAsync<T>(0, CancellationToken.None));
        return values;
    }

    private async Task<(string? Error, DateTimeOffset? NextAttemptAt, string? LockedBy, DateTimeOffset? LockedUntil)> ReadOutboxCleanupStateAsync(PalUlid id)
    {
        await using var command = _conn.CreateCommand();
        command.CommandText = "SELECT error, next_attempt_at, locked_by, locked_until FROM outbox_messages WHERE id=$id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = ConvertParameterValue(id);
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        await Assert.That(await reader.ReadAsync(CancellationToken.None)).IsTrue();
        return (
            await reader.IsDBNullAsync(0, CancellationToken.None) ? null : reader.GetString(0),
            ReadNullableDateTimeOffset(reader, 1),
            await reader.IsDBNullAsync(2, CancellationToken.None) ? null : reader.GetString(2),
            ReadNullableDateTimeOffset(reader, 3));
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value is DateTimeOffset dateTimeOffset
            ? dateTimeOffset
            : DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    // ─────────────────────────────────────────────────────────────
    // v9 P1-1 契约测试：接口赋值可编译性——行内注释吞掉 ": IEventLog" 的接口声明丢失
    // 曾在 build 0/0 + 全量测试 + API 快照三重缺口下静默（快照不覆盖 Dapper 族/测试全用
    // var 具体类型/方法结构仍满足接口）。本测试以接口类型接收构造物，声明丢失即编译失败。
    // ─────────────────────────────────────────────────────────────
    [Test]
    public async Task DapperEventLog_ImplementsIEventLog_InterfaceAssignmentCompiles()
    {
        // 接口赋值——若 DapperEventLog 丢失 : IEventLog 声明，本行编译失败（CS0029；v10 勘正——泛型约束码 CS0311 不适用）
        PalDDD.EventLog.IEventLog log = new DapperEventLog(_conn, dbType: _dbType);
        await Assert.That(log).IsNotNull();
    }
}

[JsonSerializable(typeof(DapperStoreTests.TestSagaState))]
internal sealed partial class DapperStoreJsonContext : JsonSerializerContext;

// ═══════════════════════════════════════════════════════════════
// Dapper 测试集合定义 — 序列化执行避免全局静态状态竞态
// ═══════════════════════════════════════════════════════════════


// ═══════════════════════════════════════════════════════════════
// Dapper SQLite 类型处理器（运行时路径必需）
// ─────────────────────────────────────────────────────────────────
// 生产 Store 未启用 Dapper.AOT 拦截（[module:DapperAot] 为注释禁用态），走运行时经典 Dapper 路径。
// SQLite TEXT 列需要运行时 TypeHandler 转换 DateTimeOffset/Guid/Ulid（见 ClassInitialize 注册）。
// ClassCleanup 调用 SqlMapper.ResetTypeHandlers() 清理全局状态。
