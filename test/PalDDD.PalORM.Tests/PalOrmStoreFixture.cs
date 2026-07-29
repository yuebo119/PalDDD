using PalORM;
using PalORM.Sqlite;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// PalORM 测试共享 Fixture —— 替代 DapperStoreTests 的 [Before(Class)] 全局静态状态。
/// <para>
/// <b>关键差异（vs DapperStoreTests）</b>：
/// <list type="bullet">
/// <item>无 [Before(Class)] 注册 TypeHandler / 设 MatchNamesWithUnderscores（PalORM 无全局静态状态）。</item>
/// <item>每测试用独立 SQLite :memory: DataSession（天然隔离，无交叉污染）。</item>
/// <item>建表 DDL 与 DapperStoreTests.cs:92-178 一致（表结构契约保留）。</item>
/// </list>
/// </para>
/// </summary>
public static class PalOrmStoreFixture
{
    /// <summary>建表 DDL —— 与 DapperStoreTests.cs:92-178 一致（表结构契约）。</summary>
    /// <remarks>
    /// 注意：列名 snake_case（与 Dapper 兼容），EventLog 表例外 PascalCase（双实现历史一致）。
    /// 枚举列 status 全部 INTEGER（v4 决策 2：统一 int）。
    /// </remarks>
    public const string CreateSchemaSql = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE outbox_messages (
            id              TEXT PRIMARY KEY,
            type            TEXT NOT NULL,
            payload         TEXT NOT NULL,
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

        CREATE TABLE Events (
            GlobalPosition INTEGER PRIMARY KEY AUTOINCREMENT,
            EventId        TEXT NOT NULL,
            EventName      TEXT NOT NULL,
            StreamName     TEXT NOT NULL,
            StreamVersion  INTEGER NOT NULL,
            SchemaVersion  INTEGER NOT NULL DEFAULT 1,
            ContentType    TEXT NOT NULL DEFAULT 'application/json',
            Payload        TEXT NOT NULL,
            Metadata       TEXT,
            RecordedAt     TEXT NOT NULL,
            ActorId        TEXT,
            Reason         TEXT
        );
        CREATE UNIQUE INDEX idx_events_stream ON Events(StreamName, StreamVersion);

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
        CREATE INDEX idx_projection_checkpoints_status ON projection_checkpoints(projection_name, source_name, status);

        CREATE TABLE idempotency_records (
            operation_name   TEXT NOT NULL,
            idempotency_key  TEXT NOT NULL,
            status           INTEGER NOT NULL,
            locked_until     TEXT NOT NULL,
            expires_at       TEXT NOT NULL,
            updated_at       TEXT NOT NULL,
            response_payload TEXT,
            error            TEXT,
            PRIMARY KEY (operation_name, idempotency_key)
        );
        CREATE INDEX idx_idempotency_expires ON idempotency_records(expires_at);
        """;

    /// <summary>创建并初始化测试用 DataSession（:memory: SQLite + 建表）。</summary>
    public static async Task<DataSession<SqliteProvider>> CreateAsync(CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct);
        await session.ExecuteAsync($"PRAGMA journal_mode=WAL", ct);
        // 注：SQLite 不支持单 ExecuteAsync 执行多条分号分隔 SQL —— 逐条执行
        await session.ExecuteAsync($"CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload TEXT NOT NULL, content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)", ct);
        await session.ExecuteAsync($"CREATE INDEX idx_outbox_status ON outbox_messages(status, next_attempt_at, locked_until)", ct);
        await session.ExecuteAsync($"CREATE TABLE inbox_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, consumer_name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, received_at TEXT NOT NULL, processing_started_at TEXT, processed_at TEXT, attempts INTEGER NOT NULL DEFAULT 1, last_error TEXT)", ct);
        await session.ExecuteAsync($"CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)", ct);
        await session.ExecuteAsync($"CREATE TABLE saga_states (saga_id TEXT PRIMARY KEY, current_state TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, completed_at TEXT, error TEXT, error_at TEXT, version INTEGER NOT NULL DEFAULT 0, saga_data TEXT, leased_by TEXT, leased_until TEXT)", ct);
        await session.ExecuteAsync($"CREATE TABLE events (global_position INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL, event_name TEXT NOT NULL, stream_name TEXT NOT NULL, stream_version INTEGER NOT NULL, schema_version INTEGER NOT NULL DEFAULT 1, content_type TEXT NOT NULL DEFAULT 'application/json', payload TEXT NOT NULL, metadata TEXT, recorded_at TEXT NOT NULL, actor_id TEXT, reason TEXT)", ct);
        await session.ExecuteAsync($"CREATE UNIQUE INDEX idx_events_stream ON events(stream_name, stream_version)", ct);
        await session.ExecuteAsync($"CREATE TABLE projection_checkpoints (projection_name TEXT NOT NULL, source_name TEXT NOT NULL, position TEXT NOT NULL, status INTEGER NOT NULL, updated_at TEXT NOT NULL, lease_until TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, error TEXT, PRIMARY KEY (projection_name, source_name, position))", ct);
        await session.ExecuteAsync($"CREATE TABLE idempotency_records (operation_name TEXT NOT NULL, idempotency_key TEXT NOT NULL, status INTEGER NOT NULL, locked_until TEXT NOT NULL, expires_at TEXT NOT NULL, updated_at TEXT NOT NULL, response_payload TEXT, error TEXT, PRIMARY KEY (operation_name, idempotency_key))", ct);
        return session;
    }
}
