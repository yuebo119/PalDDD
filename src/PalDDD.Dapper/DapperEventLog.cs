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

        // 1. 乐观并发检查
        var currentVersion = await _connection.QuerySingleOrDefaultAsync<long?>(
            EventLogSql.MaxVersion,
            new { name = streamName }, _transaction).ConfigureAwait(false);
        expectedVersion.Matches(currentVersion ?? -1);

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
        for (int i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            var pos = await _connection.QuerySingleAsync<long>(sql, new
            {
                EventId = evt.EventId,
                EventName = evt.EventName,
                StreamName = streamName,
                StreamVersion = version++,
                SchemaVersion = evt.SchemaVersion,
                ContentType = evt.ContentType,
                Payload = evt.Payload.ToArray(),
                Metadata = evt.Metadata.ToArray(),
                RecordedAt = now,
                ActorId = (string?)null,
                Reason = (string?)null
            }, _transaction).ConfigureAwait(false);

            if (i == 0) firstGlobalPos = pos;
        }

        return new AppendEventsResult(
            streamName, firstVersion, version - 1, firstGlobalPos, firstGlobalPos + events.Count - 1);
    }

    public async IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName, long fromVersion = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 💡 RecordedEvent 的构造函数是 internal 且属性只读，Dapper 运行时无法直接物化。
        // 通过 EventLogRow DTO（public 无参构造 + public setters）读取，再映射到 RecordedEvent。
        var rows = await _connection.QueryAsync<EventLogRow>(
            new CommandDefinition(EventLogSql.ReadStream, new { name = streamName, from = fromVersion }, _transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var row in rows)
            yield return row.ToRecordedEvent();
    }

    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<EventLogRow>(
            new CommandDefinition(EventLogSql.ReadAll, new { from = fromPosition }, _transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var row in rows)
            yield return row.ToRecordedEvent();
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

        public RecordedEvent ToRecordedEvent()
            => RecordedEvent.RehydrateFromBytes(
                StreamName, StreamVersion, GlobalPosition, RecordedAt,
                EventId, EventName, SchemaVersion, ContentType,
                Payload, Metadata,
                string.IsNullOrEmpty(ActorId) && string.IsNullOrEmpty(Reason)
                    ? EventAuditMetadata.Empty
                    : new EventAuditMetadata(ActorId, Reason, null, null, null, null));
    }
}
