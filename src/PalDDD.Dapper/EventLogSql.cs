// ─────────────────────────────────────────────────────────────
// 📜 EventLog SQL 常量 — DapperEventLog 专用
// ─────────────────────────────────────────────────────────────
// 💡 独立于 PalDDD.Dapper 统一 SQL 模板，保持 EventLog 领域边界。
//    EventLog 与 Transactions 是平级领域，不应相互依赖。

namespace PalDDD.Dapper;

/// <summary>EventLog SQL 模板（DapperEventLog 专用）</summary>
internal static class EventLogSql
{
    /// <summary>查询流最大版本号（乐观并发检查）</summary>
    public const string MaxVersion =
        "SELECT MAX(StreamVersion) FROM Events WHERE StreamName = @name";

    /// <summary>
    /// PostgreSQL INSERT ... RETURNING GlobalPosition 语法。<br/>
    /// 💡 单条语句完成插入 + 返回全局位置，零额外往返。
    /// </summary>
    public const string InsertPG =
        "INSERT INTO Events (EventId, EventName, StreamName, StreamVersion, SchemaVersion, ContentType, Payload, Metadata, RecordedAt, ActorId, Reason) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason) RETURNING GlobalPosition";

    /// <summary>MySQL INSERT ... SELECT LAST_INSERT_ID() 语法</summary>
    public const string InsertMySql =
        "INSERT INTO Events (EventId, EventName, StreamName, StreamVersion, SchemaVersion, ContentType, Payload, Metadata, RecordedAt, ActorId, Reason) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason); SELECT LAST_INSERT_ID();";

    /// <summary>SQLite INSERT ... SELECT last_insert_rowid() 语法</summary>
    public const string InsertSqlite =
        "INSERT INTO Events (EventId, EventName, StreamName, StreamVersion, SchemaVersion, ContentType, Payload, Metadata, RecordedAt, ActorId, Reason) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason); SELECT last_insert_rowid();";

    /// <summary>按流名和版本读取事件</summary>
    public const string ReadStream =
        "SELECT * FROM Events WHERE StreamName = @name AND StreamVersion >= @from ORDER BY StreamVersion";

    /// <summary>按全局位置读取所有事件</summary>
    public const string ReadAll =
        "SELECT * FROM Events WHERE GlobalPosition >= @from ORDER BY GlobalPosition";
}
