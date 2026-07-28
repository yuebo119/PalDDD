// ─────────────────────────────────────────────────────────────
// 📜 EventLog SQL 常量 — DapperEventLog 专用
// ─────────────────────────────────────────────────────────────
// 💡 独立于 PalDDD.Dapper 统一 SQL 模板，保持 EventLog 领域边界。
//    EventLog 与 Transactions 是平级领域，不应相互依赖。
// ⚠️ 列名统一 snake_case（与 docs/sql/ schema + PalORM 适配层对齐）。
//    Dapper MatchNamesWithUnderscores=true 自动映射到 PascalCase 属性。

namespace PalDDD.Dapper;

/// <summary>EventLog SQL 模板（DapperEventLog 专用）</summary>
internal static class EventLogSql
{
    /// <summary>查询流最大版本号（乐观并发检查）</summary>
    public const string MaxVersion =
        "SELECT MAX(stream_version) FROM events WHERE stream_name = @name";

    /// <summary>
    /// PostgreSQL INSERT ... RETURNING global_position 语法。<br/>
    /// 💡 单条语句完成插入 + 返回全局位置，零额外往返。
    /// </summary>
    public const string InsertPG =
        "INSERT INTO events (event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason) RETURNING global_position";

    /// <summary>MySQL INSERT ... SELECT LAST_INSERT_ID() 语法</summary>
    public const string InsertMySql =
        "INSERT INTO events (event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason); SELECT LAST_INSERT_ID();";

    /// <summary>SQLite INSERT ... SELECT last_insert_rowid() 语法</summary>
    public const string InsertSqlite =
        "INSERT INTO events (event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason); SELECT last_insert_rowid();";

    /// <summary>按流名和版本读取事件</summary>
    public const string ReadStream =
        "SELECT * FROM events WHERE stream_name = @name AND stream_version >= @from ORDER BY stream_version";

    /// <summary>按全局位置读取所有事件</summary>
    public const string ReadAll =
        "SELECT * FROM events WHERE global_position >= @from ORDER BY global_position";
}
