using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ByteAether.Ulid;
using PalORM;
using PalDDD.Core.Diagnostics;
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
        // P3 修复（二十一轮）：补取消前置检查（对齐 EFCore 版 AppendAsync 的
        // ThrowIfCancellationRequested）——乐观并发 SELECT MAX 前先响应已取消令牌。
        // 契约差异声明：EFCore 版另有 foreach 元素级 ThrowIfNull(@event)，本版不加——
        // null 元素在循环内解引用（e.EventId）即抛 NullReferenceException，失败点相同
        // 仅异常类型不同；逐元素预检与解引用失败对契约影响无差，省一次遍历。
        cancellationToken.ThrowIfCancellationRequested();

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
        // ITM-075 修复：TOCTOU 窗口（步骤 1 预检后并发写入撞 (stream_name, stream_version)
        // 唯一索引）此前抛裸 provider 异常——Dapper（DapperEventLog）与 EFCore（EventLogDbContext）
        // 均翻译为 EventStreamConcurrencyException，调用方按该异常重试的契约在本实现失效。
        // 现补同型翻译：唯一约束冲突 → 重查实际版本再分类（对齐 Dapper 八轮修复逻辑——
        // 批内 EventId 重复原样上抛，并发写入转统一并发异常）；非唯一约束异常原样上抛。
        // 事务契约声明（对齐 DapperEventLog "P2 定案"）：批量追加的原子性由调用方事务保证；
        // 中途失败会留下前半批（部分写入），与 Dapper 版一致。
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
            EventLogRow inserted;
            try
            {
                inserted = await Session.InsertAsync(row, cancellationToken);
            }
            catch (DbException ex) when (IsUniqueConstraintViolation(ex))
            {
                // ITM-075：预检后并发写入兜底——唯一索引冲突转为统一并发异常（对齐 Dapper/EFCore）。
                // 分类逻辑镜像 Dapper 八轮修复：批内前序 INSERT 已推进 MaxVersion（同事务可见），
                // 失败事件的赋值版本（streamVersion）未落库——重查实际版本，命中并发写入则转
                // EventStreamConcurrencyException，否则（如批内 EventId 重复）原样上抛。
                long? actualVersion = null;
                var requerySucceeded = false;
                try
                {
                    actualVersion = await Session.ScalarAsync<long?>(
                        $"SELECT MAX(stream_version) FROM events WHERE stream_name = {streamName}",
                        cancellationToken);
                    requerySucceeded = true;
                }
                catch (DbException)
                {
                    // 重查失败（如 PG aborted 事务）——放弃分类，走原始异常上抛
                }
                if (requerySucceeded && (actualVersion ?? -1) >= streamVersion)
                    throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion ?? -1);
                throw;
            }
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
        // ITM-163 修复：补 streamName 空白守卫（对齐 EventLogDbContext/InMemoryEventLog 同款）
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        // P3 修复（八轮评审）：补参数守卫，严格对齐 EFCore 版（EventLogDbContext ThrowIfLessThan(maxCount, 1)）
        // P3 修复（二十一轮）：补 fromVersion 非负守卫（对齐 EFCore 版 ThrowIfLessThan(fromVersion, 0)）
        ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        // ITM-079 修复：SQL LIMIT 下推（对齐 Dapper EventLogSql.ReadStream `LIMIT @max` / EFCore
        // EventLogDbContext `.Take` / InMemory 服务端上限）——原实现只做客户端计数截断，DB 仍全量
        // 返回（超长事件流下网络/内存空转）。maxCount != int.MaxValue 时拼接 `LIMIT {maxCount}`
        // 字面量（maxCount 是 int，非注入面；绕开 PalORM FormattableString 把插值参数化为
        // `LIMIT @p` 的 provider 兼容差异——SQLite/PG/MySQL 虽均接受 LIMIT 参数，但 int.MaxValue
        // 参数化在 PG 有边界问题）；等于 int.MaxValue（默认"不设上限"）时不加 LIMIT，由下方
        // 客户端计数兜底。字面量经 FormattableStringFactory.Create 编入 format 文本而非参数。
        var readStreamSql = FormattableStringFactory.Create(
            "SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE stream_name = {0} AND stream_version >= {1} ORDER BY stream_version"
                + (maxCount != int.MaxValue ? $" LIMIT {maxCount}" : string.Empty),
            streamName, fromVersion);
        using var activity = PalActivitySource.StartEventLogReadStream(streamName, fromVersion);
        var read = 0;
        // ITM-167 修复：补 metrics（对齐 EventLogDbContext/InMemoryEventLog）并置入 finally——
        // 迭代器被消费方提前 Dispose（await foreach 中 break/抛异常）时尾部语句不执行，
        // finally 在任何退出路径都会记录已产出计数（对齐 InMemoryEventLog 二十一轮）；
        // 计数在 yield 前（对齐 InMemoryEventLog ITM-120）。
        try
        {
            // 使用流式 QueryAsyncEnumerable —— 恒定内存读取（重要：超长事件流场景）
            await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(readStreamSql, cancellationToken))
            {
                if (--maxCount < 0) yield break; // P3 修复：先减后判（maxCount=0 时零产出，对齐 EFCore ThrowIfLessThan(1)）
                checked { read++; }
                yield return ToRecorded(row, streamName);
            }
        }
        finally
        {
            activity?.SetTag("pal.eventlog.read_count", read);
            PalMetrics.EventLogRead.Add(read);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // P3 修复（八轮评审）：补参数守卫（对齐 EFCore 版）+ 先判后产出——原"产出 1 条后再判"
        // 在 maxCount=0 时会多产出 1 条；结构与 ReadStreamAsync 统一。
        // P3 修复（二十一轮）：补 fromPosition 非负守卫（对齐 EFCore 版 ThrowIfLessThan(fromPosition, 0)）
        ArgumentOutOfRangeException.ThrowIfLessThan(fromPosition, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        // ITM-079 修复：SQL LIMIT 下推（同 ReadStreamAsync——语义与字面量拼接方式一致）
        var readAllSql = FormattableStringFactory.Create(
            "SELECT global_position, event_id, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE global_position >= {0} ORDER BY global_position"
                + (maxCount != int.MaxValue ? $" LIMIT {maxCount}" : string.Empty),
            fromPosition);
        using var activity = PalActivitySource.StartEventLogReadAll(fromPosition);
        var read = 0;
        // ITM-167 修复：补 metrics 并置入 finally + yield 前计数（同 ReadStreamAsync，
        // 对齐 EventLogDbContext/InMemoryEventLog）。
        try
        {
            await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(readAllSql, cancellationToken))
            {
                if (--maxCount < 0) yield break; // P3 修复（八轮评审）：先减后判——对齐 ReadStreamAsync 结构
                checked { read++; }
                yield return ToRecorded(row, row.StreamName);
            }
        }
        finally
        {
            activity?.SetTag("pal.eventlog.read_count", read);
            PalMetrics.EventLogRead.Add(read);
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

    /// <summary>
    /// 判定 DbException 是否为唯一约束冲突（ITM-075：跨 provider 鸭子类型，与
    /// PalOrmSagaStateStore.IsUniqueConstraintViolation / DapperEventLog / EFCore 侧同型）。
    /// </summary>
    /// <remarks>
    /// ITM-167 裁剪降级声明：本方法通过反射鸭子类型读取 provider 异常属性
    /// （PostgresException.SqlState / MySqlException.Number / SqlException.Number）。在裁剪
    /// （trimmed/AOT）发布下，GetProperty 可能因元数据被裁而返回 null——判定安全降级为 false，
    /// 原始 provider 异常原样上抛（并发冲突仅失去统一 EventStreamConcurrencyException 翻译，
    /// 不会崩溃、不会误判；调用方重试契约退化为检查原始异常）。
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃）。")]
    private static bool IsUniqueConstraintViolation(DbException exception)
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

            // SQLite: Microsoft.Data.Sqlite.SqliteException 消息包含 "UNIQUE constraint"
            // 类型限定：裸消息匹配会把文案恰好含该词组的非唯一约束异常误判（镜像 InboxDbContext 十七轮修复）
            var message = inner.Message;
            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
