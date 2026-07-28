using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ByteAether.Ulid;
using PalORM;
using PalDDD.EventLog;
using PalDDD.PalORM.Models;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// EventLog Store 的 PalORM 实现 —— 双泛型核心基类。
/// <para>
/// <b>EventLog 表名+列名</b>：统一 snake_case（与其他表一致；原 Dapper/EFCore 用 PascalCase，
/// 但 PalORM 手写 SQL 的 FormattableString 不加引号——PG 折叠无引号标识符为小写，导致不匹配。
/// v4 实施修正：统一改为 snake_case）。
/// </para>
/// <para>
/// <b>GlobalPosition 分配</b>：<c>[Key(AutoIncrement=true)]</c> —— Dapper 风格（DB 自增 + RETURNING），
/// 替代 EFCore 的 Hi/Lo 应用层预分配（避免引入 EventLogPositionReserver 依赖）。
/// </para>
/// <para>
/// <b>乐观并发</b>：<see cref="IEventLog.AppendAsync"/> 在 INSERT 前预检查 <c>SELECT MAX(StreamVersion)</c>，
/// 与 <c>ExpectedStreamVersion.Matches</c> 配合实现流版本乐观控制；事件流的唯一索引 (StreamName, StreamVersion) 是兜底。
/// </para>
/// </summary>
public class PalOrmEventLog<TProvider> : IEventLog
    where TProvider : IDbProvider
{
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需直接访问 Session。")]
    protected readonly DataSession<TProvider> Session;

    private readonly TimeProvider _clock;

    /// <summary>构造 EventLog。</summary>
    public PalOrmEventLog(DataSession<TProvider> session, TimeProvider? clock = null)
    {
        Session = session;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<AppendEventsResult> AppendAsync(
        string streamName, ExpectedStreamVersion expectedVersion,
        IReadOnlyList<EventData> events, CancellationToken cancellationToken = default)
    {
        // 步骤 1：乐观并发检查 —— 读当前最大 StreamVersion
        var currentMax = await Session.ScalarAsync<long?>(
            $"SELECT MAX(stream_version) FROM events WHERE stream_name = {streamName}",
            cancellationToken);
        var currentVersion = currentMax ?? -1;
        if (!expectedVersion.Matches(currentVersion))
            throw new EventStreamConcurrencyException(streamName, expectedVersion, currentVersion);

        // 步骤 2：循环 INSERT —— 每个事件一条 SQL（与 Dapper 实现一致；非批量）
        // 注：GlobalPosition 为 DB 自增，InsertAsync 自动回填；firstGlobalPos 记录首条返回的全局位置
        // PALORM005 循环告警 —— EventLog 语义要求每事件独立 GlobalPosition + StreamVersion 递增，
        // 无法用 BulkInsert（批量 INSERT 无法保证 GlobalPosition 单调有序 + RETURNING 每行）。
#pragma warning disable PALORM005 // 事件溯源语义要求循环 INSERT，非 N+1 反模式
        var now = _clock.GetUtcNow();
        long firstGlobalPos = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var streamVersion = currentVersion + 1 + i;
            var row = new EventLogRow
            {
                EventId = e.EventId,
                EventName = e.EventName,
                StreamName = streamName,
                StreamVersion = streamVersion,
                SchemaVersion = e.SchemaVersion,
                ContentType = e.ContentType,
                Payload = e.Payload.ToArray(),
                Metadata = e.Metadata.ToArray(),
                RecordedAt = now,
                ActorId = e.Audit.ActorId,
                Reason = e.Audit.Reason,
            };
            // InsertAsync 对自增主键自动回填 GlobalPosition（PG/SQLite 经 RETURNING，MySQL 经 LAST_INSERT_ID）
            var inserted = await Session.InsertAsync(row, cancellationToken);
            if (i == 0) firstGlobalPos = inserted.GlobalPosition;
        }
#pragma warning restore PALORM005

        var firstStreamVersion = currentVersion + 1;
        var lastStreamVersion = currentVersion + events.Count;
        var lastGlobalPos = firstGlobalPos + events.Count - 1;
        return new AppendEventsResult(streamName, firstStreamVersion, lastStreamVersion, firstGlobalPos, lastGlobalPos);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName, long fromVersion = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 注意：FormattableString 无法插值 maxCount=int.MaxValue（会被插值为参数），SQL 层面 LIMIT 兼容
        // 使用流式 QueryAsyncEnumerable —— 恒定内存读取（重要：超长事件流场景）
        await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(
            $"SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason FROM events WHERE stream_name = {streamName} AND stream_version >= {fromVersion} ORDER BY stream_version",
            cancellationToken))
        {
            yield return ToRecorded(row, streamName);
            if (--maxCount <= 0) yield break;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(
            $"SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason FROM events WHERE global_position >= {fromPosition} ORDER BY global_position",
            cancellationToken))
        {
            yield return ToRecorded(row, row.StreamName);
            if (--maxCount <= 0) yield break;
        }
    }

    /// <summary>EventLogRow → RecordedEvent 领域类型（零拷贝路径，payload/metadata 直接引用 byte[]）。</summary>
    private static RecordedEvent ToRecorded(EventLogRow row, string streamName)
    {
        // EventAuditMetadata：ActorId+Reason 都空 → Empty；否则构造（其他 4 字段当前未持久化，传 null）
        var audit = string.IsNullOrEmpty(row.ActorId) && string.IsNullOrEmpty(row.Reason)
            ? EventAuditMetadata.Empty
            : new EventAuditMetadata(row.ActorId, row.Reason, null, null, null, null);

        return RecordedEvent.Rehydrate(
            streamName, row.StreamVersion, row.GlobalPosition, row.RecordedAt,
            row.EventId, row.EventName, row.SchemaVersion, row.ContentType,
            row.Payload, row.Metadata, audit);
    }
}
