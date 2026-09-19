// ─────────────────────────────────────────────────────────────
// 📜 DapperEventLog — Dapper 事件日志（乐观并发 + 缓冲读取（LIMIT 分页可控，非真流式——三十七轮口径勘正））
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

using System.Data;
using Dapper;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using PalUlid = ByteAether.Ulid.Ulid;
using static PalDDD.Dapper.DapperSqlErrorClassifier;

using PalDDD.Core.Diagnostics;
using PalDDD.EventLog;
namespace PalDDD.Dapper;

/// <summary>Dapper 事件日志 — 实现 IEventLog 接口</summary>
/// <remarks>v8 声明：唯一不显式 EnsureOpen 的 Store——依赖 Dapper CommandDefinition
/// auto-open/close 兜底（事务场景连接必已 open）。
/// v9 修复 P1-1：本注释原以行内 // 形式加在类声明行，把 ": IEventLog" 吞进注释行尾——
/// 接口声明静默丢失（build/测试/快照三重缺口均未拦，验证轮 v9 抓出）；现移入 XML doc 并恢复声明。</remarks>
public sealed class DapperEventLog : IEventLog
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    /// <summary>生效事务（二轮评审 T5）：显式构造参数优先，否则查同连接 DapperUnitOfWork
    /// 的 ambient 活动事务——DI 解析的 Store（构造时无事务）也能参与 UoW 事务边界。</summary>
    private DbTransaction? Tx => _transaction ?? DapperAmbientTransaction.TryGet(_connection);

    private readonly DapperDbType _dbType;
    private readonly TimeProvider _timeProvider;

    /// <param name="dbType">数据库类型（用于选择 INSERT ... RETURNING / LAST_INSERT_ID / last_insert_rowid 语法）。
    /// v33 P3 声明：默认 Sqlite 仅为既有直连调用方兼容；非 SQLite 用户必须显式传 dbType
    ///（PG 报类型错误可见，MySQL 静默时差混存）。</param>
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

        // v37 P3：写侧观测（镜像 v36 PalOrmEventLog 修复形态，对齐 EventLogDbContext 与
        // InMemoryEventLog 的 StartEventLogAppend 形态）——Dapper 路径此前完全无 Activity/
        // metrics，追加耗时与吞吐不可见；成功路径 return 前 EventLogAppended 计数
        //（并发冲突等异常路径不计数，与 EFCore/PalORM 姊妹一致）
        using var activity = PalActivitySource.StartEventLogAppend(streamName, events.Count);

        // 📐 事务契约（P2 定案声明）：批量追加的原子性由调用方事务保证——传入
        // Tx 则整批可回滚；未传时中途失败会留下前半批（部分写入）。
        // EFCore 版在内部事务中自动回滚，Dapper 版依赖外部 UoW（两版契约差异是
        // Dapper 连接由调用方持有的设计结果——与 PalORM 版一致）。
        // 1. 乐观并发检查（P0-2 修复：原 expectedVersion.Matches 返回值被丢弃）
        var currentVersion = await _connection.QueryFirstOrDefaultAsync<long?>(
            EventLogSql.MaxVersion,
                new { name = streamName }, Tx).ConfigureAwait(false);
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
                // 🔬 AOT 实验：参数走 DynamicParameters——1.1.0 List expansion 把匿名对象里的
                //    byte[]（Payload/Metadata）误判为 IN 列表做 PackListParameters 展开（SQL 变
                //    行值，SQLite "row value misused"）；byte[] 用显式 DbType.Binary。
                var dp = new DynamicParameters();
                dp.Add("EventId", DapperAotInitializer.ToSqliteParameter(evt.EventId));
                dp.Add("EventName", evt.EventName);
                dp.Add("StreamName", streamName);
                dp.Add("StreamVersion", version++);
                dp.Add("SchemaVersion", evt.SchemaVersion);
                dp.Add("ContentType", evt.ContentType);
                dp.Add("Payload", evt.Payload.ToArray(), DbType.Binary);
                dp.Add("Metadata", evt.Metadata.ToArray(), DbType.Binary);
                dp.Add("RecordedAt", ToTimeParam(now));
                // 修复覆盖残留：此前硬编码 null——actor/reason 也从未真正持久化过；
                // 现按 EventData.Audit 全量映射 6 字段（对齐 PalORM/EFCore）
                dp.Add("ActorId", evt.Audit.ActorId);
                dp.Add("Reason", evt.Audit.Reason);
                dp.Add("CorrelationId", evt.Audit.CorrelationId?.ToString());
                dp.Add("CausationId", evt.Audit.CausationId?.ToString());
                dp.Add("TraceParent", evt.Audit.TraceParent);
                dp.Add("TraceState", evt.Audit.TraceState);
                pos = await _connection.QuerySingleAsync<long>(sql, dp, Tx).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
            {
                // P2 修复：TOCTOU 窗口（预检查后并发写入）由唯一索引兜底，转换为统一并发异常。
                // EventId 冲突误译防护（对齐 EFCore 版）：版本仍满足期望说明是 EventId 唯一
                // 索引撞（重复事件 ID），原样上抛而非转并发异常
                // P2 修复（stale version）：冲突后重查实际版本再分类——预检查快照可能已陈旧
                // P1 修复（八轮评审）：重查必须挂接 Tx——Microsoft.Data.Sqlite 要求
                // 命令挂接连接的活动事务（传 null 在 UoW 事务内抛 InvalidOperationException，
                // 吞掉本应转换的并发异常）。PG 事务 aborted（25P02）下同事务重查自身会抛——
                // 无法分类时保守上抛原始冲突异常（外层事务将回滚，不会产生误判）。
                long? actualVersion = null;
                var requerySucceeded = false;
                try
                {
                    actualVersion = await _connection.QueryFirstOrDefaultAsync<long?>(
                        EventLogSql.MaxVersion, new { name = streamName }, Tx).ConfigureAwait(false);
                    requerySucceeded = true;
                }
                catch (System.Data.Common.DbException)
                {
                    // 重查失败（如 PG aborted 事务）——放弃分类，走原始异常上抛
                }
                // P3 修复（九轮）：批内前序 INSERT 已推进 MaxVersion（同事务可见），失败事件
                // 的赋值版本（version-1）未落库——无外部写入者的基线是 version-2；外部并发
                // 写入者只会落在我们的失败点位或更高（>= version-1）。据此分类：命中基线说明
                // 是批内 EventId 重复，原样上抛原始异常；超出基线说明存在并发写入者，转统一
                // 并发异常。
                if (requerySucceeded && (actualVersion ?? -1) >= version - 1)
                    throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion ?? -1);
                throw;
            }

            if (i == 0) firstGlobalPos = pos;
            lastGlobalPos = pos; // P1 修复（四轮评审，PD17）：循环内每次更新，不用算术推导（并发下 GlobalPosition 非连续）——对齐 PalORM 版同方法
        }

        // v37 P3：写侧 metrics（对齐 EventLogDbContext/InMemoryEventLog/PalOrmEventLog 的
        // EventLogAppended——成功追加的事件数，异常路径不计数）
        PalMetrics.EventLogAppended.Add(events.Count);
        return new AppendEventsResult(
            streamName, firstVersion, version - 1, firstGlobalPos, lastGlobalPos);
    }

    public async IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName, long fromVersion = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // ITM-163 修复：补 streamName 空白守卫（对齐 EventLogDbContext/InMemoryEventLog 同款）
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        // P3 修复（二十一轮）：补 fromVersion 非负守卫（对齐 EFCore/PalORM 版 ThrowIfLessThan(fromVersion, 0)）
        ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 0);
        // ITM-080 修复：补 maxCount 守卫（对齐 EFCore/PalORM/InMemory 版 ThrowIfLessThan(maxCount, 1)）——
        // maxCount=0 时 SQLite `LIMIT 0` 返回空结果（契约要求抛 ArgumentOutOfRangeException）；
        // maxCount<0 时 SQLite `LIMIT -1` = 无限制（危险——负数意图不明时全量拉取，绕过调用方上限）
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        // 💡 RecordedEvent 的构造函数是 internal 且属性只读，Dapper 运行时无法直接物化。
        // 通过 EventLogRow DTO（public 无参构造 + public setters）读取，再映射到 RecordedEvent。
        // v38 P3：读侧观测补齐（v37 补了写侧，读侧 PD24 残留——镜像 EventLogDbContext:97/138
        // 与 PalOrmEventLog:199/239 的 StartEventLogReadStream/StartEventLogReadAll +
        // EventLogRead 计数形态）——读取耗时与吞吐此前不可见。
        using var activity = PalActivitySource.StartEventLogReadStream(streamName, fromVersion);

        var rows = await _connection.QueryAsync<EventLogRow>(
            EventLogSql.ReadStream, new { name = streamName, from = fromVersion, max = maxCount }, Tx).ConfigureAwait(false);

        var read = 0;
        // ITM-167 同款：计数与 metrics 置入 finally——迭代器被消费方提前 Dispose（await
        // foreach 中 break/抛异常）时循环后语句不执行，finally 在任何退出路径都记录已产出
        // 计数；计数在 yield 前（对齐 EventLogDbContext/PalOrmEventLog/InMemoryEventLog）。
        try
        {
            foreach (var row in rows)
            {
                checked { read++; }
                yield return row.ToRecordedEvent();
            }
        }
        finally
        {
            // v25 P3 指标族（D5）口径：EventLogRead 计数保留，无 SetTag
            PalMetrics.EventLogRead.Add(read);
        }
    }

    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0, int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // P3 修复（二十一轮）：补 fromPosition 非负守卫（对齐 EFCore/PalORM 版 ThrowIfLessThan(fromPosition, 0)）
        ArgumentOutOfRangeException.ThrowIfLessThan(fromPosition, 0);
        // ITM-080 修复：补 maxCount 守卫（同 ReadStreamAsync——SQLite `LIMIT -1` = 无限制的方言陷阱）
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        // v38 P3：读侧观测补齐（同 ReadStreamAsync——镜像 EventLogDbContext:138 /
        // PalOrmEventLog:239 的 StartEventLogReadAll + EventLogRead 计数形态）。
        using var activity = PalActivitySource.StartEventLogReadAll(fromPosition);

        var rows = await _connection.QueryAsync<EventLogRow>(
            EventLogSql.ReadAll, new { from = fromPosition, max = maxCount }, Tx).ConfigureAwait(false);

        var read = 0;
        // ITM-167 同款：计数与 metrics 置入 finally + yield 前计数（同 ReadStreamAsync）。
        try
        {
            foreach (var row in rows)
            {
                checked { read++; }
                yield return row.ToRecordedEvent();
            }
        }
        finally
        {
            // v25 P3 指标族（D5）口径：EventLogRead 计数保留，无 SetTag
            PalMetrics.EventLogRead.Add(read);
        }
    }

    /// <summary>
    /// P2 修复（七轮评审）：按方言选择时间参数格式——对齐 Outbox/Inbox/Checkpoint 三 Store。
    /// <para>
    /// P2/P3 修复（十七轮）：返回 <c>object</c>（DateTimeOffset 装箱一次）是刻意的收口防线——
    /// 强类型返回会诱导调用方绕过本方法自行格式化，方言错配（PG text OID / MySQL session tz）
    /// 将重新进入；五 Store 同款声明（Outbox/Inbox/Saga/EventLog/Checkpoint）。装箱开销相对 SQL 执行成本可忽略。
    /// </para>
    /// </summary>
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

    // ITM-796（2026-09-19）：IsUniqueConstraintViolation 私有副本（DbException 签名）
    // 收口至 DapperSqlErrorClassifier（码集并集）——IL2075 抑制随类迁移；
    // 调用点经 using static 解析至类成员

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
