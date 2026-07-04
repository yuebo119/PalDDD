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
//   生产 Store 标注 [DapperAot(false)]，走运行时 Dapper 路径。
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

/// <summary>Dapper 测试集合 — 序列化执行避免全局静态状态竞态</summary>
public sealed class DapperStoreTests
{
    private DbConnection _conn = new SqliteConnection("Data Source=:memory:");
    private const DapperDbType _dbType = DapperDbType.Sqlite;
    private static bool s_previousUnderscoreSetting;

    [Before(Class)]
    public static void ClassInitialize()
    {
        // 保存全局状态旧值，ClassCleanup 恢复
        s_previousUnderscoreSetting = global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores;

        // Dapper snake_case → PascalCase 映射（全局静态）
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        // 注册运行时 TypeHandler — SQLite TEXT 列需要转换为 Guid/DateTimeOffset
        // 生产 Store 已标注 [DapperAot(false)]，走运行时 Dapper 路径，TypeHandler 生效
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
                status          TEXT NOT NULL DEFAULT 'Pending',
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
                status                TEXT NOT NULL DEFAULT 'Processing',
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
                GlobalPosition INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId        TEXT NOT NULL,
                EventName      TEXT NOT NULL,
                StreamName     TEXT NOT NULL,
                StreamVersion  INTEGER NOT NULL,
                SchemaVersion  INTEGER NOT NULL DEFAULT 1,
                ContentType    TEXT NOT NULL DEFAULT 'application/json',
                Payload        BLOB NOT NULL,
                Metadata       BLOB,
                RecordedAt     TEXT NOT NULL,
                ActorId        TEXT,
                Reason         TEXT
            );
            CREATE UNIQUE INDEX idx_events_stream ON events(StreamName, StreamVersion);

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

    [Test]
    public async Task Outbox_AddMessage_UsesInjectedTimeProvider(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Parse("2026-06-27T10:00:00Z", CultureInfo.InvariantCulture);
        var clock = new FixedTimeProvider(now);
        var store = new DapperOutboxStore(_conn, _dbType, timeProvider: clock);
        var msg = CreateOutboxMessage("test.event.v1");

        store.AddMessage(msg);

        var createdAt = await ReadScalarAsync<DateTimeOffset>(
            "SELECT created_at FROM outbox_messages WHERE id=$id",
            ("$id", msg.Id));
        await Assert.That(createdAt).IsEqualTo(now);
    }

    [Test]
    public async Task Outbox_AddMessagesAsync_UsesInjectedTimeProvider(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Parse("2026-06-27T10:01:00Z", CultureInfo.InvariantCulture);
        var clock = new FixedTimeProvider(now);
        var store = new DapperOutboxStore(_conn, _dbType, timeProvider: clock);
        var messages = Enumerable.Range(0, 2)
            .Select(i => CreateOutboxMessage($"test.event.v{i}"))
            .ToList();

        await store.AddMessagesAsync(messages);

        var createdTimes = await ReadScalarsAsync<DateTimeOffset>(
            "SELECT created_at FROM outbox_messages ORDER BY type");
        foreach (var createdAt in createdTimes)
            await Assert.That(createdAt).IsEqualTo(now);
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

        var status = await ReadScalarAsync<string>(
            "SELECT status FROM inbox_messages WHERE id=$id",
            ("$id", first.Id));
        await Assert.That(status).IsEqualTo(InboxStatus.Processed.ToString());
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
        await Assert.That(loaded).IsNotNull();
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
            "SELECT RecordedAt FROM events WHERE StreamName=$stream",
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

    private static OutboxMessage CreateOutboxMessage(string type) => new()
    {
        Type = type,
        Payload = "test-payload"u8.ToArray(),
        ContentType = "application/json",
        SchemaVersion = 1,
        Status = OutboxStatus.Pending
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
}

[JsonSerializable(typeof(DapperStoreTests.TestSagaState))]
internal sealed partial class DapperStoreJsonContext : JsonSerializerContext;

// ═══════════════════════════════════════════════════════════════
// Dapper 测试集合定义 — 序列化执行避免全局静态状态竞态
// ═══════════════════════════════════════════════════════════════


// ═══════════════════════════════════════════════════════════════
// Dapper SQLite 类型处理器（已移除）
// ─────────────────────────────────────────────────────────────────
// Dapper.AOT 编译时拦截器直接调用 SqliteDataReader.GetDateTimeOffset() 等强类型方法，
// 不查阅运行时 TypeHandler。Microsoft.Data.Sqlite 原生支持 DateTimeOffset↔TEXT 和 Guid↔TEXT
// （连接字符串加 BinaryGuid=False），无需运行时 TypeHandler。
// Saga/Inbox 通过的测试是此结论的铁证。
