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
        // P2/P3 修复（十七轮）：补三重入参守卫——镜像 EFCore 版 EventLogDbContext 与
        // Dapper 版 DapperEventLog.AppendAsync 同款（三方一致），空流名/空事件列表
        // 此前会静默产出空结果或污染 events 表（空流名写入无主事件行）
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) throw new ArgumentException("至少需要一个事件。", nameof(events));

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
        long lastGlobalPos = 0;  // P1 修复：循环内每次更新，不用推导
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
                // P2 定案：审计 4 字段补齐持久化（此前仅 EFCore 版持久化，PalORM 版导出时追踪链断）
                CorrelationId = e.Audit.CorrelationId?.ToString(),
                CausationId = e.Audit.CausationId?.ToString(),
                TraceParent = e.Audit.TraceParent,
                TraceState = e.Audit.TraceState,
            };
            // InsertAsync 对自增主键自动回填 GlobalPosition（PG/SQLite 经 RETURNING，MySQL 经 LAST_INSERT_ID）
            var inserted = await Session.InsertAsync(row, cancellationToken);
            if (i == 0) firstGlobalPos = inserted.GlobalPosition;
            lastGlobalPos = inserted.GlobalPosition;  // P1 修复：不用算术推导（并发下 GlobalPosition 非连续）
        }
#pragma warning restore PALORM005

        var firstStreamVersion = currentVersion + 1;
        var lastStreamVersion = currentVersion + events.Count;
        // lastGlobalPos 已在循环内每次更新（不再用 firstGlobalPos + count - 1 推导）
        return new AppendEventsResult(streamName, firstStreamVersion, lastStreamVersion, firstGlobalPos, lastGlobalPos);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName, long fromVersion = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // P3 修复（八轮评审）：补参数守卫，严格对齐 EFCore 版（EventLogDbContext ThrowIfLessThan(maxCount, 1)）
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        // 注意：FormattableString 无法插值 maxCount=int.MaxValue（会被插值为参数），SQL 层面 LIMIT 兼容
        // 使用流式 QueryAsyncEnumerable —— 恒定内存读取（重要：超长事件流场景）
        await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(
            $"SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE stream_name = {streamName} AND stream_version >= {fromVersion} ORDER BY stream_version",
            cancellationToken))
        {
            if (--maxCount < 0) yield break; // P3 修复：先减后判（maxCount=0 时零产出，对齐 EFCore ThrowIfLessThan(1)）
            yield return ToRecorded(row, streamName);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // P3 修复（八轮评审）：补参数守卫（对齐 EFCore 版）+ 先判后产出——原"产出 1 条后再判"
        // 在 maxCount=0 时会多产出 1 条；结构与 ReadStreamAsync 统一。
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(
            $"SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE global_position >= {fromPosition} ORDER BY global_position",
            cancellationToken))
        {
            if (--maxCount < 0) yield break; // P3 修复（八轮评审）：先减后判——对齐 ReadStreamAsync 结构
            yield return ToRecorded(row, row.StreamName);
        }
    }

    /// <summary>EventLogRow → RecordedEvent 领域类型（零拷贝路径，payload/metadata 直接引用 byte[]）。</summary>
    private static RecordedEvent ToRecorded(EventLogRow row, string streamName)
    {
        // P2 定案：审计 6 字段全量还原（此前 4 个追踪字段回填 null，备份/迁移往返后追踪链断）
        var audit = string.IsNullOrEmpty(row.ActorId) && string.IsNullOrEmpty(row.Reason)
            && string.IsNullOrEmpty(row.CorrelationId) && string.IsNullOrEmpty(row.CausationId)
            && string.IsNullOrEmpty(row.TraceParent) && string.IsNullOrEmpty(row.TraceState)
            ? EventAuditMetadata.Empty
            : new EventAuditMetadata(row.ActorId, row.Reason,
                ParseUlid(row.CorrelationId), ParseUlid(row.CausationId),
                row.TraceParent, row.TraceState);

        // P2 修复（零拷贝真化）：Rehydrate 会 ToArray 复制 payload/metadata；
        // InternalsVisibleTo 补 PalORM 后走 RehydrateFromBytes（真零拷贝，与 EFCore/StoredEvent 同路径）
        return RecordedEvent.RehydrateFromBytes(
            streamName, row.StreamVersion, row.GlobalPosition, row.RecordedAt,
            row.EventId, row.EventName, row.SchemaVersion, row.ContentType,
            row.Payload, row.Metadata, audit);
    }

    /// <summary>安全解析 Ulid 字符串 —— 脏数据返回 null 而非抛异常（与 OutboxMessageRow P0-6 同风格）。</summary>
    private static Ulid? ParseUlid(string? value)
        => value is not null && Ulid.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var ulid)
            ? ulid
            : (Ulid?)null;
}
