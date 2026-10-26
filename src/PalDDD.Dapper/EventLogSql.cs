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
        "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES (@EventId, @EventIdBin, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState) RETURNING global_position";

    /// <summary>MySQL INSERT ... SELECT LAST_INSERT_ID() 语法</summary>
    public const string InsertMySql =
        "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES (@EventId, @EventIdBin, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState); SELECT LAST_INSERT_ID();";

    /// <summary>SQLite INSERT ... SELECT last_insert_rowid() 语法</summary>
    public const string InsertSqlite =
        "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES (@EventId, @EventIdBin, @EventName, @StreamName, @StreamVersion, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState); SELECT last_insert_rowid();";

    /// <summary>按流名和版本读取事件。<br/>
    /// 💡 LIMIT @max 由调用方传入 maxCount（ITM-063：此前参数被静默忽略）。
    /// </summary>
    public const string ReadStream =
        "SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE stream_name = @name AND stream_version >= @from ORDER BY stream_version LIMIT @max";

    /// <summary>二进制模式按流读取：不传输文本 event_id，只取 event_id_bin。</summary>
    public const string ReadStreamBinary =
        "SELECT global_position, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE stream_name = @name AND stream_version >= @from ORDER BY stream_version LIMIT @max";

    /// <summary>
    /// I（2026-10-10 优化轮，仅 PG 启用）：单事件 Any 版本原子追加——INSERT..SELECT 子查询
    /// 在服务端原子计算 COALESCE(MAX(stream_version),-1)+1，读+插单往返（原路径 MAX 预查 +
    /// INSERT 两次往返）。Any 语义无守卫谓词、永不空插；并发竞争由 (stream_name, stream_version)
    /// 唯一索引兜底。别名显式 PascalCase，不依赖 MatchNamesWithUnderscores 全局开关。
    /// MySQL/SQLite 同语义模板经 2026-10-10 实测不启用（MySQL -5.3% E6.8% 噪声带、
    /// SQLite +44% 反向劣化——本地往返近零时子查询复杂度反超省往返收益），未保留。
    /// </summary>
    public const string AppendSingleAnyPG =
        "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) " +
        "SELECT @EventId, @EventIdBin, @EventName, @StreamName, COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) + 1, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState " +
        "RETURNING global_position AS \"GlobalPosition\", stream_version AS \"StreamVersion\"";

    /// <summary>
    /// B4（EPSL 地毯审计 2026-10-16）：PG 单事件 Exact(n) 原子追加——<see cref="AppendSingleAnyPG"/>
    /// + 版本守卫谓词（服务端原子判定当前 COALESCE(MAX,-1) = @ExpectedVersion——Exact(n) 语义为
    /// 「当前流版本为 n」，新事件落 n+1）。守卫失匹零行插入（无异常），调用方重查实际版本转统一
    /// 并发异常——语义与「MAX 预查 + Matches 失败」逐字一致，但省预查往返并消除该路径 TOCTOU
    /// 窗口（Any 先例同款收益形态，-15% 锚）。真库探针（wslc pg186）store 级验证四形态在案。
    /// </summary>
    public const string AppendSingleExactPG =
        "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) " +
        "SELECT @EventId, @EventIdBin, @EventName, @StreamName, COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) + 1, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState " +
        "WHERE COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) = @ExpectedVersion " +
        "RETURNING global_position AS \"GlobalPosition\", stream_version AS \"StreamVersion\"";

    /// <summary>B4：PG 单事件 NoStream 原子追加——守卫 = 当前版本为 -1（空流，InsertSqlite 同语义）。</summary>
    public const string AppendSingleNoStreamPG =
        "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) " +
        "SELECT @EventId, @EventIdBin, @EventName, @StreamName, COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) + 1, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState " +
        "WHERE COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) = -1 " +
        "RETURNING global_position AS \"GlobalPosition\", stream_version AS \"StreamVersion\"";

    /// <summary>B4：PG 单事件 StreamExists 原子追加——守卫 = 当前版本 ≥ 0（流已存在）。</summary>
    public const string AppendSingleStreamExistsPG =
        "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) " +
        "SELECT @EventId, @EventIdBin, @EventName, @StreamName, COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) + 1, @SchemaVersion, @ContentType, @Payload, @Metadata, @RecordedAt, @ActorId, @Reason, @CorrelationId, @CausationId, @TraceParent, @TraceState " +
        "WHERE COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @StreamName), -1) >= 0 " +
        "RETURNING global_position AS \"GlobalPosition\", stream_version AS \"StreamVersion\"";

    /// <summary>EventId 存在性探查（快路径唯一约束冲突的分类依据：存在 = EventId 重复原样上抛，不存在 = 版本位竞争）。</summary>
    public const string EventIdExists =
        "SELECT COUNT(*) FROM events WHERE event_id = @id";

    /// <summary>
    /// B2（EPSL 地毯审计 2026-10-16）：MySQL 批量化后的全局位置范围回查——MySQL 无
    /// RETURNING，多行 VALUES INSERT 后按本批版本区间取 MIN/MAX（(stream_name,
    /// stream_version) 唯一索引served；区间由 (stream_name, stream_version) 唯一索引
    /// 与冲突分类保证只含本批行——并发写入者落在区间外）。较「LAST_INSERT_ID + 连续
    /// 算术」无自增连续性假设（wslc mysql84 8.4.11 mode 2 实测已知行数多行 VALUES 确为
    /// 连续块，但范围回查不依赖该语义）。
    /// </summary>
    public const string PositionRangeMySql =
        "SELECT MIN(global_position) AS Min, MAX(global_position) AS Max FROM events WHERE stream_name = @name AND stream_version BETWEEN @from AND @to";

    /// <summary>
    /// 按全局位置读取所有事件。<br/>
    /// 💡 LIMIT @max 由调用方传入 maxCount（ITM-063：此前参数被静默忽略）。
    /// </summary>
    public const string ReadAll =
        "SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE global_position >= @from ORDER BY global_position LIMIT @max";

    /// <summary>二进制模式按全局位置读取：不传输文本 event_id，只取 event_id_bin。</summary>
    public const string ReadAllBinary =
        "SELECT global_position, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE global_position >= @from ORDER BY global_position LIMIT @max";
}
