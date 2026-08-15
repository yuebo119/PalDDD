// ─────────────────────────────────────────────────────────────
// 📜 DapperEventLog — Dapper 事件日志（乐观并发 + 流式读取）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ⚠️ 读取路径使用 Dapper 运行时反射物化（QueryAsync<EventLogRow>），
//      非 Dapper.AOT SG 拦截器。EventLogRow DTO 通过 public 无参构造 + setters
//      供 Dapper 反射赋值，再映射到 RecordedEvent。此项目 IsAotCompatible=true
//      但实际读取路径依赖 Dapper 运行时 IL 生成，AOT 发布时需验证可用性。
//   ✅ 手写 SQL — 所有 SQL 在编译时确定，零动态构建。
//
// 💡 什么是事件日志（EventLog）？
//   ｜ 事件溯源（Event Sourcing）的核心存储模式——只追加（Append-Only），不修改。
//   ｜ 每个事件按顺序记录到 Events 表，通过 StreamName + StreamVersion 唯一定位。
//   ｜ 聚合的当前状态 = 从第一个事件开始重放到最新的结果。
//   ｜ 好处：完整审计历史、时间旅行调试、事件回放重建投影。
//
// 💡 乐观并发控制（Optimistic Concurrency）：
//   ｜ AppendAsync 先查询 MAX(StreamVersion)，再用 ExpectedStreamVersion 校验。
//   ｜ 如果版本不匹配 → 抛出 EventStreamConcurrencyException。
//   ｜ 这避免了悲观锁，同时保证了事件流的顺序一致性。
//
// 💡 跨数据库差异：
//   ｜ PostgreSQL → RETURNING GlobalPosition（一条语句拿到自增ID）
//   ｜ MySQL → SELECT LAST_INSERT_ID()
//   ｜ SQLite → SELECT last_insert_rowid()
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using PalUlid = ByteAether.Ulid.Ulid;

using PalDDD.EventLog;
namespace PalDDD.Dapper;

/// <summary>Dapper 事件日志 — 实现 IEventLog 接口</summary>
public sealed class DapperEventLog : IEventLog
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private readonly DapperDbType _dbType;
    private readonly TimeProvider _timeProvider;

    /// <param name="dbType">数据库类型（用于选择 INSERT ... RETURNING / LAST_INSERT_ID / last_insert_rowid 语法）</param>
    public DapperEventLog(
        DbConnection connection,
        DbTransaction? transaction = null,
        DapperDbType dbType = DapperDbType.Sqlite,
        TimeProvider? timeProvider = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction;
        _dbType = dbType;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<AppendEventsResult> AppendAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) throw new ArgumentException("至少需要一个事件。", nameof(events));

        // 📐 事务契约（P2 定案声明）：批量追加的原子性由调用方事务保证——传入
        // _transaction 则整批可回滚；未传时中途失败会留下前半批（部分写入）。
        // EFCore 版在内部事务中自动回滚，Dapper 版依赖外部 UoW（两版契约差异是
        // Dapper 连接由调用方持有的设计结果——与 PalORM 版一致）。
        // 1. 乐观并发检查（P0-2 修复：原 expectedVersion.Matches 返回值被丢弃）
        var currentVersion = await _connection.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(EventLogSql.MaxVersion,
                new { name = streamName }, _transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!expectedVersion.Matches(currentVersion ?? -1))
            throw new EventStreamConcurrencyException(streamName, expectedVersion, currentVersion ?? -1);

        // 2. 批量插入事件 — 根据数据库类型选择返回 ID 语法
        var version = (currentVersion ?? -1) + 1;
        var now = _timeProvider.GetUtcNow();
        var firstVersion = version;

        var sql = _dbType switch
        {
            DapperDbType.PostgreSql => EventLogSql.InsertPG,
            DapperDbType.MySql => EventLogSql.InsertMySql,
            _ => EventLogSql.InsertSqlite
        };

        long firstGlobalPos = 0;
        long lastGlobalPos = 0; // P1 修复（四轮评审）：循环内跟踪，替代算术推导
        for (int i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            long pos;
            try
            {
                pos = await _connection.QuerySingleAsync<long>(new CommandDefinition(sql, new
                {
                    EventId = DapperAotInitializer.ToSqliteParameter(evt.EventId),
                    EventName = evt.EventName,
                    StreamName = streamName,
                    StreamVersion = version++,
                    SchemaVersion = evt.SchemaVersion,
                    ContentType = evt.ContentType,
                    Payload = evt.Payload.ToArray(),
                    Metadata = evt.Metadata.ToArray(),
                    RecordedAt = ToTimeParam(now),
                    // 修复覆盖残留：此前硬编码 null——actor/reason 也从未真正持久化过；
                    // 现按 EventData.Audit 全量映射 6 字段（对齐 PalORM/EFCore）
                    ActorId = evt.Audit.ActorId,
                    Reason = evt.Audit.Reason,
                    CorrelationId = evt.Audit.CorrelationId?.ToString(),
                    CausationId = evt.Audit.CausationId?.ToString(),
                    TraceParent = evt.Audit.TraceParent,
                    TraceState = evt.Audit.TraceState
                }, _transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
            {
                // P2 修复：TOCTOU 窗口（预检查后并发写入）由唯一索引兜底，转换为统一并发异常。
                // EventId 冲突误译防护（对齐 EFCore 版）：版本仍满足期望说明是 EventId 唯一
                // 索引撞（重复事件 ID），原样上抛而非转并发异常
                // P2 修复（stale version）：冲突后重查实际版本再分类——预检查快照可能已陈旧
                // P1 修复（八轮评审）：重查必须挂接 _transaction——Microsoft.Data.Sqlite 要求
                // 命令挂接连接的活动事务（传 null 在 UoW 事务内抛 InvalidOperationException，
                // 吞掉本应转换的并发异常）。PG 事务 aborted（25P02）下同事务重查自身会抛——
                // 无法分类时保守上抛原始冲突异常（外层事务将回滚，不会产生误判）。
                long? actualVersion = null;
                var requerySucceeded = false;
                try
                {
                    actualVersion = await _connection.QuerySingleOrDefaultAsync<long?>(
                        new CommandDefinition(EventLogSql.MaxVersion, new { name = streamName }, _transaction,
                            cancellationToken: cancellationToken)).ConfigureAwait(false);
                    requerySucceeded = true;
                }
                catch (System.Data.Common.DbException)
                {
                    // 重查失败（如 PG aborted 事务）——放弃分类，走原始异常上抛
                }
                if (requerySucceeded && !expectedVersion.Matches(actualVersion ?? -1))
                    throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion ?? -1);
                throw;
            }

            if (i == 0) firstGlobalPos = pos;
            lastGlobalPos = pos; // P1 修复（四轮评审，PD17）：循环内每次更新，不用算术推导（并发下 GlobalPosition 非连续）——对齐 PalORM 版同方法
        }

        return new AppendEventsResult(
            streamName, firstVersion, version - 1, firstGlobalPos, lastGlobalPos);
    }

    public async IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName, long fromVersion = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 💡 RecordedEvent 的构造函数是 internal 且属性只读，Dapper 运行时无法直接物化。
        // 通过 EventLogRow DTO（public 无参构造 + public setters）读取，再映射到 RecordedEvent。
        var rows = await _connection.QueryAsync<EventLogRow>(
            new CommandDefinition(EventLogSql.ReadStream, new { name = streamName, from = fromVersion, max = maxCount }, _transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var row in rows)
            yield return row.ToRecordedEvent();
    }

    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<EventLogRow>(
            new CommandDefinition(EventLogSql.ReadAll, new { from = fromPosition, max = maxCount }, _transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var row in rows)
            yield return row.ToRecordedEvent();
    }

    /// <summary>P2 修复（七轮评审）：按方言选择时间参数格式——对齐 Outbox/Inbox/Checkpoint 三 Store。</summary>
    private object ToTimeParam(DateTimeOffset value)
        => _dbType switch
        {
            DapperDbType.MySql => DapperAotInitializer.ToMySqlParameter(value),
            // P1 修复（八轮评审）：PG 传原生 DateTimeOffset——Npgsql 映射 timestamptz；
            // "O" 格式 string 按 text OID 发送，PG 8.3+ 的 timestamptz <= text 无比较
            // 运算符（隐式转换已移除），WHERE 比较上下文必炸 42883
            DapperDbType.PostgreSql => value,
            _ => DapperAotInitializer.ToSqliteParameter(value)
        };

    /// <summary>
    /// 判定 DbException 是否为唯一约束冲突（跨 provider 鸭子类型，P2 修复引入）。
    /// <para>与 EFCore 侧 EventLogDbContext 的实现对齐（ITM-003 同型，作用于原生 DbException）。</para>
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定（与 EFCore 侧同型）。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃），并发冲突仅失去统一异常类型。")]
    private static bool IsUniqueConstraintViolation(System.Data.Common.DbException exception)
    {
        for (var inner = (Exception)exception; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            var typeName = type.Name;

            // PostgreSQL: Npgsql.PostgresException.SqlState == "23505"
            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(inner) is string sqlState
                && sqlState == "23505")
            {
                return true;
            }

            // MySQL: MySqlException.Number == 1062（ER_DUP_ENTRY）或 1586
            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int mysqlNumber
                && (mysqlNumber == 1062 || mysqlNumber == 1586))
            {
                return true;
            }

            // SQL Server: SqlException.Number == 2601 或 2627
            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
            {
                return true;
            }

            // SQLite: 消息包含 "UNIQUE constraint"
            var message = inner.Message;
            if (!string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Dapper 读取 DTO — 桥接 PascalCase 列名到 RecordedEvent 的 internal 构造路径。<br/>
    /// 保持 RecordedEvent 的领域封装不变（internal 构造 + 只读属性）。
    /// </summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Dapper 运行时通过反射实例化此 DTO 用于 QueryAsync<EventLogRow> 物化。")]
    internal sealed class EventLogRow
    {
        public long GlobalPosition { get; set; }
        public PalUlid EventId { get; set; }
        public string EventName { get; set; } = "";
        public string StreamName { get; set; } = "";
        public long StreamVersion { get; set; }
        public int SchemaVersion { get; set; }
        public string ContentType { get; set; } = "";
        public byte[] Payload { get; set; } = [];
        public byte[] Metadata { get; set; } = [];
        public DateTimeOffset RecordedAt { get; set; }
        public string? ActorId { get; set; }
        public string? Reason { get; set; }
        // 修复覆盖残留（对齐 PalORM/EFCore 审计 6 字段）：此前 Dapper 路径只持久化 2 字段，
        // 追踪链（correlation/causation/trace）在 Dapper 存储上断，导出/备份往返丢字段
        public string? CorrelationId { get; set; }
        public string? CausationId { get; set; }
        public string? TraceParent { get; set; }
        public string? TraceState { get; set; }

        public RecordedEvent ToRecordedEvent()
            => RecordedEvent.RehydrateFromBytes(
                StreamName, StreamVersion, GlobalPosition, RecordedAt,
                EventId, EventName, SchemaVersion, ContentType,
                Payload, Metadata,
                string.IsNullOrEmpty(ActorId) && string.IsNullOrEmpty(Reason)
                    && string.IsNullOrEmpty(CorrelationId) && string.IsNullOrEmpty(CausationId)
                    && string.IsNullOrEmpty(TraceParent) && string.IsNullOrEmpty(TraceState)
                    ? EventAuditMetadata.Empty
                    : new EventAuditMetadata(ActorId, Reason,
                        ParseUlid(CorrelationId), ParseUlid(CausationId),
                        TraceParent, TraceState));

        /// <summary>安全解析 Ulid 字符串 —— 脏数据返回 null（与 PalORM 版 P0-6 同风格）。</summary>
        private static PalUlid? ParseUlid(string? value)
            => value is not null && PalUlid.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var ulid)
                ? ulid
                : (PalUlid?)null;
    }
}
