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
        "INSERT INTO events (event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState) RETURNING global_position";

    /// <summary>MySQL INSERT ... SELECT LAST_INSERT_ID() 语法</summary>
    public const string InsertMySql =
        "INSERT INTO events (event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState); SELECT LAST_INSERT_ID();";

    /// <summary>SQLite INSERT ... SELECT last_insert_rowid() 语法</summary>
    public const string InsertSqlite =
        "INSERT INTO events (event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES (@EventId, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState); SELECT last_insert_rowid();";

    /// <summary>按流名和版本读取事件。<br/>
    /// 💡 LIMIT @max 由调用方传入 maxCount（ITM-063：此前参数被静默忽略）。
    /// </summary>
    public const string ReadStream =
        "SELECT * FROM events WHERE stream_name = @name AND stream_version >= @from ORDER BY stream_version LIMIT @max";

    /// <summary>
    /// I（2026-10-10 优化轮，仅 PG 启用）：单事件 Any 版本原子追加——INSERT..SELECT 子查询
    /// 在服务端原子计算 COALESCE(MAX(stream_version),-1)+1，读+插单往返（原路径 MAX 预查 +
    /// INSERT 两次往返）。Any 语义无守卫谓词、永不空插；并发竞争由 (stream_name, stream_version)
    /// 唯一索引兜底。别名显式 PascalCase，不依赖 MatchNamesWithUnderscores 全局开关。
    /// MySQL/SQLite 同语义模板经 2026-10-10 实测不启用（MySQL -5.3% E6.8% 噪声带、
    /// SQLite +44% 反向劣化——本地往返近零时子查询复杂度反超省往返收益），未保留。
    /// </summary>
    public const string AppendSingleAnyPG =
        "INSERT INTO events (event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) " +
        "SELECT @EventId, @EventName, @StreamName, COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) + 1, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState " +
        "RETURNING global_position AS \"GlobalPosition\", stream_version AS \"StreamVersion\"";

    /// <summary>EventId 存在性探查（快路径唯一约束冲突的分类依据：存在 = EventId 重复原样上抛，不存在 = 版本位竞争）。</summary>
    public const string EventIdExists =
        "SELECT COUNT(*) FROM events WHERE event_id = @id";

    /// <summary>
    /// 按全局位置读取所有事件。<br/>
    /// 💡 LIMIT @max 由调用方传入 maxCount（ITM-063：此前参数被静默忽略）。
    /// </summary>
    public const string ReadAll =
        "SELECT * FROM events WHERE global_position >= @from ORDER BY global_position LIMIT @max";
}
