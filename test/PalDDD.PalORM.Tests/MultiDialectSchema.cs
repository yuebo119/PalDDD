using PalORM;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// 跨方言建表 DDL —— 按 PalORM SqlDialect 提供类型适配的建表脚本。
/// <para>
/// <b>关键差异</b>：
/// <list type="bullet">
/// <item>SQLite：全 TEXT/INTEGER（动态类型，无强类型校验）</item>
/// <item>PostgreSQL：TEXT→TEXT/uuid, INTEGER→BIGINT/INTEGER, BLOB→BYTEA, TEXT(payload)→TEXT(Base64)</item>
/// <item>MySQL：TEXT→VARCHAR(255), INTEGER→BIGINT, AUTOINCREMENT→AUTO_INCREMENT</item>
/// </list>
/// </para>
/// <para>枚举列 status 全部 INTEGER（v4 决策 2：统一 int）。</para>
/// </summary>
public static class MultiDialectSchema
{
    /// <summary>SQLite 建表 SQL（与 PalOrmStoreFixture 一致，分号分隔 —— SQLite 支持多语句）。</summary>
    public static readonly string[] Sqlite =
    [
        "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload TEXT NOT NULL, content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)",
        "CREATE TABLE inbox_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, consumer_name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, received_at TEXT NOT NULL, processing_started_at TEXT, processed_at TEXT, attempts INTEGER NOT NULL DEFAULT 1, last_error TEXT)",
        "CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)",
        "CREATE TABLE saga_states (saga_id TEXT PRIMARY KEY, current_state TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, completed_at TEXT, error TEXT, error_at TEXT, version INTEGER NOT NULL DEFAULT 0, saga_data TEXT, leased_by TEXT, leased_until TEXT)",
        "CREATE TABLE Events (GlobalPosition INTEGER PRIMARY KEY AUTOINCREMENT, EventId TEXT NOT NULL, EventName TEXT NOT NULL, StreamName TEXT NOT NULL, StreamVersion INTEGER NOT NULL, SchemaVersion INTEGER NOT NULL DEFAULT 1, ContentType TEXT NOT NULL DEFAULT 'application/json', Payload TEXT NOT NULL, Metadata TEXT, RecordedAt TEXT NOT NULL, ActorId TEXT, Reason TEXT)",
        "CREATE UNIQUE INDEX idx_events_stream ON Events(StreamName, StreamVersion)",
        "CREATE TABLE projection_checkpoints (projection_name TEXT NOT NULL, source_name TEXT NOT NULL, position TEXT NOT NULL, status INTEGER NOT NULL, updated_at TEXT NOT NULL, lease_until TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, error TEXT, PRIMARY KEY (projection_name, source_name, position))",
        "CREATE TABLE idempotency_records (operation_name TEXT NOT NULL, key TEXT NOT NULL, status INTEGER NOT NULL, locked_until TEXT NOT NULL, expires_at TEXT NOT NULL, updated_at TEXT NOT NULL, response_payload TEXT, error TEXT, PRIMARY KEY (operation_name, key))",
    ];

    /// <summary>PostgreSQL 建表 SQL（原生类型；payload 用 TEXT 存 Base64）。</summary>
    public static readonly string[] PostgreSql =
    [
        """CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload TEXT NOT NULL, content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, created_at TIMESTAMPTZ NOT NULL, processed_at TIMESTAMPTZ, next_attempt_at TIMESTAMPTZ, locked_by TEXT, locked_until TIMESTAMPTZ, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)""",
        """CREATE TABLE inbox_messages (id BIGSERIAL PRIMARY KEY, message_id TEXT NOT NULL, consumer_name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, received_at TIMESTAMPTZ NOT NULL, processing_started_at TIMESTAMPTZ, processed_at TIMESTAMPTZ, attempts INTEGER NOT NULL DEFAULT 1, last_error TEXT)""",
        "CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)",
        """CREATE TABLE saga_states (saga_id TEXT PRIMARY KEY, current_state TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, created_at TIMESTAMPTZ NOT NULL, completed_at TIMESTAMPTZ, error TEXT, error_at TIMESTAMPTZ, version INTEGER NOT NULL DEFAULT 0, saga_data TEXT, leased_by TEXT, leased_until TIMESTAMPTZ)""",
        """CREATE TABLE "Events" ("GlobalPosition" BIGSERIAL PRIMARY KEY, "EventId" TEXT NOT NULL, "EventName" TEXT NOT NULL, "StreamName" TEXT NOT NULL, "StreamVersion" BIGINT NOT NULL, "SchemaVersion" INTEGER NOT NULL DEFAULT 1, "ContentType" TEXT NOT NULL DEFAULT 'application/json', "Payload" TEXT NOT NULL, "Metadata" TEXT, "RecordedAt" TIMESTAMPTZ NOT NULL, "ActorId" TEXT, "Reason" TEXT)""",
        """CREATE UNIQUE INDEX idx_events_stream ON "Events"("StreamName", "StreamVersion")""",
        """CREATE TABLE projection_checkpoints (projection_name TEXT NOT NULL, source_name TEXT NOT NULL, position TEXT NOT NULL, status INTEGER NOT NULL, updated_at TIMESTAMPTZ NOT NULL, lease_until TIMESTAMPTZ NOT NULL, revision BIGINT NOT NULL DEFAULT 0, error TEXT, PRIMARY KEY (projection_name, source_name, position))""",
        """CREATE TABLE idempotency_records (operation_name TEXT NOT NULL, key TEXT NOT NULL, status INTEGER NOT NULL, locked_until TIMESTAMPTZ NOT NULL, expires_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, response_payload TEXT, error TEXT, PRIMARY KEY (operation_name, key))""",
    ];

    /// <summary>MySQL 建表 SQL（VARCHAR/BIGINT，AUTO_INCREMENT）。</summary>
    public static readonly string[] MySql =
    [
        "CREATE TABLE outbox_messages (id VARCHAR(32) PRIMARY KEY, type VARCHAR(255) NOT NULL, payload TEXT NOT NULL, content_type VARCHAR(64) NOT NULL DEFAULT 'application/json', schema_version INT NOT NULL DEFAULT 1, status INT NOT NULL DEFAULT 0, retry_count INT NOT NULL DEFAULT 0, error TEXT, created_at DATETIME(6) NOT NULL, processed_at DATETIME(6) NULL, next_attempt_at DATETIME(6) NULL, locked_by VARCHAR(64) NULL, locked_until DATETIME(6) NULL, correlation_id VARCHAR(32) NULL, causation_id VARCHAR(32) NULL, trace_parent VARCHAR(128) NULL, trace_state TEXT NULL)",
        "CREATE TABLE inbox_messages (id BIGINT AUTO_INCREMENT PRIMARY KEY, message_id VARCHAR(255) NOT NULL, consumer_name VARCHAR(128) NOT NULL, status INT NOT NULL DEFAULT 0, received_at DATETIME(6) NOT NULL, processing_started_at DATETIME(6) NULL, processed_at DATETIME(6) NULL, attempts INT NOT NULL DEFAULT 1, last_error TEXT NULL)",
        "CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)",
        "CREATE TABLE saga_states (saga_id VARCHAR(32) PRIMARY KEY, current_state VARCHAR(128) NOT NULL, status INT NOT NULL DEFAULT 0, created_at DATETIME(6) NOT NULL, completed_at DATETIME(6) NULL, error TEXT NULL, error_at DATETIME(6) NULL, version INT NOT NULL DEFAULT 0, saga_data TEXT NULL, leased_by VARCHAR(64) NULL, leased_until DATETIME(6) NULL)",
        "CREATE TABLE Events (GlobalPosition BIGINT AUTO_INCREMENT PRIMARY KEY, EventId VARCHAR(32) NOT NULL, EventName VARCHAR(255) NOT NULL, StreamName VARCHAR(255) NOT NULL, StreamVersion BIGINT NOT NULL, SchemaVersion INT NOT NULL DEFAULT 1, ContentType VARCHAR(64) NOT NULL DEFAULT 'application/json', Payload TEXT NOT NULL, Metadata TEXT NULL, RecordedAt DATETIME(6) NOT NULL, ActorId VARCHAR(128) NULL, Reason VARCHAR(255) NULL)",
        "CREATE UNIQUE INDEX idx_events_stream ON Events(StreamName, StreamVersion)",
        "CREATE TABLE projection_checkpoints (projection_name VARCHAR(128) NOT NULL, source_name VARCHAR(128) NOT NULL, position VARCHAR(255) NOT NULL, status INT NOT NULL, updated_at DATETIME(6) NOT NULL, lease_until DATETIME(6) NOT NULL, revision BIGINT NOT NULL DEFAULT 0, error TEXT NULL, PRIMARY KEY (projection_name, source_name, position))",
        "CREATE TABLE idempotency_records (operation_name VARCHAR(128) NOT NULL, `key` VARCHAR(255) NOT NULL, status INT NOT NULL, locked_until DATETIME(6) NOT NULL, expires_at DATETIME(6) NOT NULL, updated_at DATETIME(6) NOT NULL, response_payload TEXT NULL, error TEXT NULL, PRIMARY KEY (operation_name, `key`))",
    ];

    /// <summary>按 PalORM SqlDialect 枚举获取对应 DDL。</summary>
    public static string[] For(SqlDialect dialect) => dialect switch
    {
        SqlDialect.PostgreSql => PostgreSql,
        SqlDialect.MySql => MySql,
        _ => Sqlite,
    };
}
