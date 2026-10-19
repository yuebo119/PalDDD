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
using System.Text;
using Dapper;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using PalUlid = ByteAether.Ulid.Ulid;
using static PalDDD.Dapper.DapperSqlErrorClassifier;

using PalDDD.Core.Diagnostics;
using PalDDD.EventLog;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// 读取时优先使用 event_id_bin（M4）。默认 false 回退文本列；存量行回填后置 true。
    /// </summary>
    public bool UseBinaryEventId { get; set; }

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

        // I（2026-10-10 优化轮，r3 实证后收窄为 PG 门控）：单事件原子追加快路径——
        // INSERT..SELECT 子查询在服务端原子计算 MAX(stream_version)+1，读+插单往返（原路径
        // MAX 预查 + INSERT 两次往返），同时消除该子路径的 TOCTOU 窗口。**方言门控依据
        //（2026-10-10 三方言实测）**：PG -15%（968→820μs，E2.6% ✓，网络往返主导收益成立）；
        // MySQL -5.3% 但 E6.8%（噪声带内，证据不足，不启用）；SQLite +44% 反向劣化
        //（本地往返近零，省往返收益被子查询复杂度反超）——故仅 PG 走本路径，MySQL/SQLite
        // 维持原路径不变。
        // B4（EPSL 地毯审计 2026-10-16）：快路径从 Any 单形态扩展为四版本语义全覆盖——
        // Exact/NoStream/StreamExists 加守卫谓词（服务端原子判定，失匹零行插入），语义与
        // 预查 + Matches 失败逐字一致（重查实际版本转并发异常），省预查往返。k>1 走批路径。
        if (events.Count == 1 && _dbType == DapperDbType.PostgreSql)
        {
            var evt = events[0];
            var nowFast = _timeProvider.GetUtcNow();
            // 参数形态镜像下方慢路径（byte[] 显式 DbType.Binary 的 AOT 实验结论同源），
            // 唯一差异：StreamVersion 不传（服务端原子计算）
            var dpFast = new DynamicParameters();
            dpFast.Add("EventId", DapperAotInitializer.ToSqliteParameter(evt.EventId));
            dpFast.Add("EventIdBin", evt.EventId.ToByteArray(), DbType.Binary);
            dpFast.Add("EventName", evt.EventName);
            dpFast.Add("StreamName", streamName);
            dpFast.Add("SchemaVersion", evt.SchemaVersion);
            dpFast.Add("ContentType", evt.ContentType);
            dpFast.Add("Payload", evt.PayloadArray, DbType.Binary);
            dpFast.Add("Metadata", evt.MetadataArray, DbType.Binary);
            dpFast.Add("RecordedAt", ToTimeParam(nowFast));
            dpFast.Add("ActorId", evt.Audit.ActorId);
            dpFast.Add("Reason", evt.Audit.Reason);
            dpFast.Add("CorrelationId", evt.Audit.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
            dpFast.Add("CausationId", evt.Audit.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
            dpFast.Add("TraceParent", evt.Audit.TraceParent);
            dpFast.Add("TraceState", evt.Audit.TraceState);
            var sqlFast = expectedVersion.Kind switch
            {
                ExpectedStreamVersionKind.NoStream => EventLogSql.AppendSingleNoStreamPG,
                ExpectedStreamVersionKind.StreamExists => EventLogSql.AppendSingleStreamExistsPG,
                ExpectedStreamVersionKind.Exact => EventLogSql.AppendSingleExactPG,
                _ => EventLogSql.AppendSingleAnyPG,
            };
            if (expectedVersion.Kind == ExpectedStreamVersionKind.Exact)
                dpFast.Add("ExpectedVersion", expectedVersion.Version);

            List<AppendSingleRow> rows;
            try
            {
                rows = (await _connection.QueryAsync<AppendSingleRow>(sqlFast, dpFast, Tx).ConfigureAwait(false)).ToList();
            }
            catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
            {
                var idExists = false;
                long? actualVersion = null;
                var classifySucceeded = false;
                try
                {
                    idExists = await _connection.QuerySingleAsync<int>(
                        EventLogSql.EventIdExists, new { id = evt.EventId }, Tx).ConfigureAwait(false) > 0;
                    actualVersion = await _connection.QueryFirstOrDefaultAsync<long?>(
                        EventLogSql.MaxVersion, new { name = streamName }, Tx).ConfigureAwait(false);
                    classifySucceeded = true;
                }
                catch (System.Data.Common.DbException)
                {
                    // 分类查询自败（如 PG aborted 事务 25P02）——放弃分类，原始冲突异常上抛
                }
                if (classifySucceeded)
                {
                    if (idExists)
                        throw;
                    throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion ?? -1);
                }
                throw;
            }

            // B4：守卫形态（非 Any）失匹 = 零行插入（无异常）——重查实际版本转统一并发
            // 异常（与预查路径 Matches 失败同款分类）；aborted 事务下重查自败，actual=-1
            // 保守上抛（外层事务回滚，不产生误判）。Any 无守卫谓词恒 1 行，本分支不可达。
            if (rows.Count == 0)
            {
                long? guardActual = null;
                try
                {
                    guardActual = await _connection.QueryFirstOrDefaultAsync<long?>(
                        EventLogSql.MaxVersion, new { name = streamName }, Tx).ConfigureAwait(false);
                }
                catch (System.Data.Common.DbException)
                {
                    // 重查自败——保守上抛并发异常（actual=-1）
                }
                throw new EventStreamConcurrencyException(streamName, expectedVersion, guardActual ?? -1);
            }

            var row = rows[0];
            PalMetrics.EventLogAppended.Add(1);
            return new AppendEventsResult(
                streamName, row.StreamVersion, row.StreamVersion, row.GlobalPosition, row.GlobalPosition);
        }

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

        // B-4（EPSL 地毯审计 2026-10-11）：PG k>1 多行 VALUES + RETURNING 单往返——原 k 次
        // INSERT..RETURNING 各一往返（批化锚对照 Outbox AddBatch5）。冲突分类等价：多行
        // INSERT 单语句原子（任一行违反唯一索引整批不入库），冲突后重查 MaxVersion——高于
        // 预检查快照即外部并发写入者（转统一并发异常），等于快照即批内/存量 EventId 重复
        //（原样上抛，与逐条路径的 P3 九轮分类同构）。
        // A12（S1 轮，2026-10-15）：门控扩展 SQLite——B-4 原注释「MySQL/SQLite 自增无多行
        // RETURNING 语义」对 SQLite 不成立：多行 VALUES + RETURNING 是 SQL 标准形态
        //（3.35+），Microsoft.Data.Sqlite 支持（仓内 InsertSqlite 单行 RETURNING 与 PalORM
        // SQLite 租约 UPDATE..RETURNING 均在用）；其引用的「三方言实测定案」是
        // INSERT..SELECT 子查询形态（Any 单语句快路径，SQLite +44% 劣化不启用），与多行
        // VALUES 形态不同，不构成对本形态的否决。
        // B2（EPSL 地毯审计 2026-10-16）：门控扩展 MySQL——原「登记延后」的两条理由逐一
        // 实证后均不成立：① 无 RETURNING 属实但可用「多行 VALUES INSERT + 版本范围回查
        // MIN/MAX」两往返达成（无自增连续性假设，见 EventLogSql.PositionRangeMySql）；
        // ②「自增连续性在 innodb_autoinc_lock_mode=2 下不保证」对已知行数的多行 VALUES
        // 不成立——wslc mysql84 8.4.11 mode 2 实测：5 行多行 INSERT 的
        // LAST_INSERT_ID()=首行值且 min/max=1/5 连续无间隙（mode 2 的间隙仅作用于
        // 行数不确定的 bulk insert）。且本实现不依赖连续性（范围回查按版本区间）。
        // 收益：k 次 INSERT..SELECT LAST_INSERT_ID 往返 → 2 往返（INSERT + 范围回查），
        // k≥3 净胜（k=5 典型形态 5→2）。MySQL 行上限 4000（14 参/行 = 56000 < PG/MySQL
        // 65535 占位符上限；SQL 文本 ~1MB < max_allowed_packet 默认 64MB）。
        var batchRowLimit = BatchRowLimit(_dbType);
        if (events.Count > 1 && batchRowLimit > 0)
        {
            if (events.Count <= batchRowLimit)
            {
                if (_dbType == DapperDbType.MySql)
                    return await AppendEventBatchMySqlAsync(streamName, expectedVersion, currentVersion, version, now, events).ConfigureAwait(false);
                return await AppendEventBatchAsync(streamName, expectedVersion, currentVersion, version, now, events).ConfigureAwait(false);
            }

            // SQLite 维持逐条：仓库已有实测（1000 行逐条 4ms vs 多行 VALUES 14ms，慢 3.5×），
            // 大语句构造与解析成本超过省下的命令执行；PG/MySQL 超过行上限原会退化逐条，
            // 这里改为按上限分块，每块一条多行语句，语句数从 O(k) 降到 O(k/limit)。
            if (_dbType != DapperDbType.Sqlite)
            {
                var firstGlobalPosChunked = 0L;
                var lastGlobalPosChunked = 0L;
                for (var offset = 0; offset < events.Count; offset += batchRowLimit)
                {
                    var count = Math.Min(batchRowLimit, events.Count - offset);
                    var chunk = SliceEvents(events, offset, count);
                    var chunkCurrentVersion = currentVersion + offset;
                    var chunkFirstVersion = version + offset;
                    var result = _dbType == DapperDbType.MySql
                        ? await AppendEventBatchMySqlAsync(streamName, expectedVersion, chunkCurrentVersion, chunkFirstVersion, now, chunk).ConfigureAwait(false)
                        : await AppendEventBatchAsync(streamName, expectedVersion, chunkCurrentVersion, chunkFirstVersion, now, chunk).ConfigureAwait(false);
                    if (offset == 0)
                        firstGlobalPosChunked = result.FirstGlobalPosition;
                    lastGlobalPosChunked = result.LastGlobalPosition;
                }

                return new AppendEventsResult(
                    streamName, firstVersion, firstVersion + events.Count - 1,
                    firstGlobalPosChunked, lastGlobalPosChunked);
            }
        }

        long firstGlobalPos = 0;
        long lastGlobalPos = 0; // P1 修复（四轮评审）：循环内跟踪，替代算术推导
        // F7 声明（第五十四轮片4，2026-09-19）：events 含 null 元素时循环内 NRE（evt.EventId
        // 解引用）——与 PalOrmEventLog.cs:74-76 同分叉同声明（省一次遍历，失败点相同仅异常
        // 类型不同）；EF 栈是逐元素 ThrowIfNull 的 ArgumentException。行为 2:1 已知，三方
        // 对齐属独立变更（若做，对齐方向为入口统一 ThrowIfNull）
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
                dp.Add("EventIdBin", evt.EventId.ToByteArray(), DbType.Binary);
                dp.Add("EventName", evt.EventName);
                dp.Add("StreamName", streamName);
                dp.Add("StreamVersion", version++);
                dp.Add("SchemaVersion", evt.SchemaVersion);
                dp.Add("ContentType", evt.ContentType);
                // v65 P3 零拷贝路径（对齐 StoredEvent.From 用法）：PayloadArray/MetadataArray
                // 是 EventData 构造期 ToArray 得到的同一数组实例，EventData 构造后不可变（公开
                // API 契约，EventData.cs:60-73 声明 internal 消费方只读不写），Dapper 仅读取
                // 后立即发送——免去每事件 payload/metadata 各一次防御性拷贝。
                dp.Add("Payload", evt.PayloadArray, DbType.Binary);
                dp.Add("Metadata", evt.MetadataArray, DbType.Binary);
                dp.Add("RecordedAt", ToTimeParam(now));
                // 修复覆盖残留：此前硬编码 null——actor/reason 也从未真正持久化过；
                // 现按 EventData.Audit 全量映射 6 字段（对齐 PalORM/EFCore）
                dp.Add("ActorId", evt.Audit.ActorId);
                dp.Add("Reason", evt.Audit.Reason);
                dp.Add("CorrelationId", evt.Audit.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
                dp.Add("CausationId", evt.Audit.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
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

    /// <summary>PG 多行批插入行数上限——13 个每行参数 × 行数 + 共享参数须低于 PG 65535 占位符上限（4000 行 = 52002 参数）。</summary>
    private const int PostgreSqlBatchRowLimit = 4000;

    /// <summary>B2（EPSL 地毯审计 2026-10-16）：MySQL 多行批插入行数上限——与 PG 同口径
    /// （14 参/行 = 56000 < MySQL 65535 占位符上限；SQL 文本 ~1MB < max_allowed_packet
    /// 默认 64MB）。MySQL 无 RETURNING，位置经版本范围回查（PositionRangeMySql）。</summary>
    private const int MySqlBatchRowLimit = 4000;

    /// <summary>A12（S1 轮，2026-10-15）：SQLite 批量化行上限——多行 VALUES + RETURNING
    /// 为 SQL 标准形态（3.35+，Microsoft.Data.Sqlite 支持；仓内 InsertSqlite 单行
    /// RETURNING 与 PalORM SQLite 租约 UPDATE..RETURNING 均在用）。行数按 SQLite 变量数
    /// 999 防御：每行 13 参 + 2 共享参（StreamName/RecordedAt），64 行 = 834 &lt; 999。</summary>
    private const int SqliteBatchRowLimit = 64;

    /// <summary>A12（S1 轮，2026-10-15）：批量化行上限按方言取——PG 4000（驱动/服务端批能力），
    /// SQLite 64（变量数 999 防御），MySQL 4000（B2：14 参/行 = 56000 < 65535 占位符上限，
    /// SQL 文本 ~1MB < max_allowed_packet 默认 64MB），其他 0（不批量化，走逐条路径）。</summary>
    private static int BatchRowLimit(DapperDbType dbType) => dbType switch
    {
        DapperDbType.PostgreSql => PostgreSqlBatchRowLimit,
        DapperDbType.MySql => MySqlBatchRowLimit,
        DapperDbType.Sqlite => SqliteBatchRowLimit,
        _ => 0,
    };

    /// <summary>按行上限切片（2026-10-19）：避免 Skip/Take 迭代器，单次预分配。</summary>
    private static List<EventData> SliceEvents(IReadOnlyList<EventData> events, int offset, int count)
    {
        var chunk = new List<EventData>(count);
        for (var i = 0; i < count; i++)
            chunk.Add(events[offset + i]);
        return chunk;
    }

    /// <summary>
    /// B-4（EPSL 地毯审计 2026-10-11）：PG k&gt;1 多行 VALUES + RETURNING 批量追加——
    /// k 次 INSERT..RETURNING 往返收为单往返；RETURNING 行序与 VALUES 序的关系 PG 未作
    /// 保证，first/last 全局位置取返回集合的 Min/Max（序无关，与逐条路径的「first=首行、
    /// last=末行」在单语句内等价——同语句内序列值单调）。
    /// </summary>
    private async Task<AppendEventsResult> AppendEventBatchAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        long? currentVersion,
        long firstVersion,
        DateTimeOffset now,
        IReadOnlyList<EventData> events)
    {
        // F7 同款声明：events 含 null 元素时此处 NRE（evt.EventId 解引用），失败点与逐条路径相同
        var (sql, dp) = BuildBatchInsert(streamName, firstVersion, ToTimeParam(now), events, returningGlobalPosition: true);

        List<long> positions;
        try
        {
            positions = (await _connection.QueryAsync<long>(sql, dp, Tx).ConfigureAwait(false)).ToList();
        }
        catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 单语句原子（整批不入库）：重查 MaxVersion 分类——高于预检查快照 = 外部并发
            // 写入者；等于快照 = 批内/存量 EventId 重复（原样上抛）。PG aborted 事务
            //（25P02）下分类查询自败——保守上抛原始冲突异常（与逐条路径同款）。
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
                // 重查失败：保留原异常路径（诊断查询尽力而为，失败不掩盖主因）
            }
            if (requerySucceeded && (actualVersion ?? -1) > (currentVersion ?? -1))
                throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion ?? -1);
            throw;
        }

        PalMetrics.EventLogAppended.Add(events.Count);
        return new AppendEventsResult(
            streamName, firstVersion, firstVersion + events.Count - 1, positions.Min(), positions.Max());
    }

    /// <summary>
    /// B2（EPSL 地毯审计 2026-10-16）：MySQL k&gt;1 多行 VALUES 批量追加——MySQL 无
    /// RETURNING，INSERT 后按本批版本区间回查 MIN/MAX 全局位置（PositionRangeMySql，
    /// (stream_name, stream_version) 唯一索引served，无自增连续性假设）。k 次
    /// INSERT..SELECT LAST_INSERT_ID 往返 → 2 往返，k≥3 净胜。冲突分类与 PG 批路径
    /// 同构（单语句原子 + MaxVersion 重查）。
    /// </summary>
    private async Task<AppendEventsResult> AppendEventBatchMySqlAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        long? currentVersion,
        long firstVersion,
        DateTimeOffset now,
        IReadOnlyList<EventData> events)
    {
        var (sql, dp) = BuildBatchInsert(streamName, firstVersion, ToTimeParam(now), events, returningGlobalPosition: false);

        try
        {
            await _connection.ExecuteAsync(sql, dp, Tx).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 与 PG 批路径同款分类：高于预检查快照 = 外部并发写入者；等于快照 = 批内/存量
            // EventId 重复（原样上抛）。MySQL aborted 事务下重查自败——保守上抛原始异常。
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
                // 重查失败：保留原异常路径（诊断查询尽力而为，失败不掩盖主因）
            }
            if (requerySucceeded && (actualVersion ?? -1) > (currentVersion ?? -1))
                throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion ?? -1);
            throw;
        }

        // 版本区间回查首末全局位置（区间由唯一索引 + 冲突分类保证只含本批行）
        var range = await _connection.QuerySingleAsync<PositionRange>(
            EventLogSql.PositionRangeMySql,
            new { name = streamName, from = firstVersion, to = firstVersion + events.Count - 1 },
            Tx).ConfigureAwait(false);

        PalMetrics.EventLogAppended.Add(events.Count);
        return new AppendEventsResult(
            streamName, firstVersion, firstVersion + events.Count - 1, range.Min, range.Max);
    }

    /// <summary>B2：MySQL 批量化位置范围回查结果（列别名 Min/Max 按名映射）。</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PositionRange(long Min, long Max);

    /// <summary>
    /// B2：多行 VALUES INSERT 构造（PG RETURNING 与 MySQL 范围回查两形态共用单一来源）——
    /// 每行 14 参数 + StreamName/RecordedAt 两共享参数；逐条路径同款零拷贝口径
    /// （PayloadArray/MetadataArray 只读直传，DbType.Binary 显式标注）。
    /// </summary>
    private static (string Sql, DynamicParameters Parameters) BuildBatchInsert(
        string streamName, long firstVersion, object recordedAt, IReadOnlyList<EventData> events, bool returningGlobalPosition)
    {
        var dp = new DynamicParameters();
        dp.Add("StreamName", streamName);
        dp.Add("RecordedAt", recordedAt);

        var sb = new StringBuilder(320 + events.Count * 236)
            .Append("INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES ");
        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            var p = $"b{i}_";
            if (i > 0)
                sb.Append(", ");
            sb.Append("(@").Append(p).Append("eid, @").Append(p).Append("eidbin, @").Append(p).Append("en, @StreamName, @").Append(p)
              .Append("sv, @").Append(p).Append("scv, @").Append(p).Append("ct, @").Append(p)
              .Append("pl, @").Append(p).Append("md, @RecordedAt, @").Append(p).Append("aid, @").Append(p)
              .Append("rs, @").Append(p).Append("cid, @").Append(p).Append("cau, @").Append(p)
              .Append("tp, @").Append(p).Append("ts)");

            dp.Add(p + "eid", DapperAotInitializer.ToSqliteParameter(evt.EventId));
            dp.Add(p + "eidbin", evt.EventId.ToByteArray(), DbType.Binary);
            dp.Add(p + "en", evt.EventName);
            dp.Add(p + "sv", firstVersion + i);
            dp.Add(p + "scv", evt.SchemaVersion);
            dp.Add(p + "ct", evt.ContentType);
            // v65 P3 零拷贝口径同逐条路径：PayloadArray/MetadataArray 为 EventData 构造期数组，只读直传
            dp.Add(p + "pl", evt.PayloadArray, DbType.Binary);
            dp.Add(p + "md", evt.MetadataArray, DbType.Binary);
            dp.Add(p + "aid", evt.Audit.ActorId);
            dp.Add(p + "rs", evt.Audit.Reason);
            dp.Add(p + "cid", evt.Audit.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
            dp.Add(p + "cau", evt.Audit.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
            dp.Add(p + "tp", evt.Audit.TraceParent);
            dp.Add(p + "ts", evt.Audit.TraceState);
        }
        if (returningGlobalPosition)
            sb.Append(" RETURNING global_position");
        return (sb.ToString(), dp);
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

        var read = 0;
        // ITM-167 同款：计数与 metrics 置入 finally——迭代器被消费方提前 Dispose（await
        // foreach 中 break/抛异常）时循环后语句不执行，finally 在任何退出路径都记录已产出
        // 计数；计数在 yield 前（对齐 EventLogDbContext/PalOrmEventLog/InMemoryEventLog）。
        try
        {
            // 2026-10-19 性能实施轮：改为 DbDataReader 真流式（SequentialAccess）。
            // 原 QueryAsync<EventLogRow> 会把全部行先物化进 List 再迭代，1M 事件可占数 GB；
            // 现按行读取并立即映射，峰值内存只与本行载荷相关。
            await foreach (var evt in ReadEventsCoreAsync(
                EventLogSql.ReadStream, streamName, fromVersion, maxCount, cancellationToken).ConfigureAwait(false))
            {
                checked { read++; }
                yield return evt;
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

        var read = 0;
        // ITM-167 同款：计数与 metrics 置入 finally + yield 前计数（同 ReadStreamAsync）。
        try
        {
            await foreach (var evt in ReadEventsCoreAsync(
                EventLogSql.ReadAll, streamName: null, fromPosition, maxCount, cancellationToken).ConfigureAwait(false))
            {
                checked { read++; }
                yield return evt;
            }
        }
        finally
        {
            // v25 P3 指标族（D5）口径：EventLogRead 计数保留，无 SetTag
            PalMetrics.EventLogRead.Add(read);
        }
    }

    /// <summary>
    /// 真流式读取（2026-10-19 性能实施轮）：DbDataReader + SequentialAccess 逐行产出，
    /// 不再经 Dapper QueryAsync 先物化全部行。连接所有权：仅当本方法打开连接时才在
    /// 枚举结束时关闭；调用方已打开的连接/事务保持原状。
    /// </summary>
    private async IAsyncEnumerable<RecordedEvent> ReadEventsCoreAsync(
        string sql,
        string? streamName,
        long from,
        int max,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var openedHere = false;
        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(ct).ConfigureAwait(false);
            openedHere = true;
        }

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = Tx;
        if (streamName is not null)
            AddReadParameter(cmd, "@name", streamName);
        AddReadParameter(cmd, "@from", from);
        AddReadParameter(cmd, "@max", max);

        DbDataReader? reader = null;
        try
        {
            reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            var ordinals = ReadOrdinals.Create(reader);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                yield return ReadRecordedEvent(reader, ordinals, UseBinaryEventId);
        }
        finally
        {
            if (reader is not null)
                await reader.DisposeAsync().ConfigureAwait(false);
            if (openedHere)
                await _connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>按列名取一次 ordinal，逐行按 ordinal 读取（避免每行字符串查找）。</summary>
    private readonly record struct ReadOrdinals(
        int GlobalPosition,
        int EventId,
        int EventIdBin,
        int EventName,
        int StreamName,
        int StreamVersion,
        int SchemaVersion,
        int ContentType,
        int Payload,
        int Metadata,
        int RecordedAt,
        int ActorId,
        int Reason,
        int CorrelationId,
        int CausationId,
        int TraceParent,
        int TraceState)
    {
        public static ReadOrdinals Create(DbDataReader reader)
            => new(
                reader.GetOrdinal("global_position"),
                reader.GetOrdinal("event_id"),
                reader.GetOrdinal("event_id_bin"),
                reader.GetOrdinal("event_name"),
                reader.GetOrdinal("stream_name"),
                reader.GetOrdinal("stream_version"),
                reader.GetOrdinal("schema_version"),
                reader.GetOrdinal("content_type"),
                reader.GetOrdinal("payload"),
                reader.GetOrdinal("metadata"),
                reader.GetOrdinal("recorded_at"),
                reader.GetOrdinal("actor_id"),
                reader.GetOrdinal("reason"),
                reader.GetOrdinal("correlation_id"),
                reader.GetOrdinal("causation_id"),
                reader.GetOrdinal("trace_parent"),
                reader.GetOrdinal("trace_state"));
    }

    /// <summary>
    /// 单行映射（零拷贝载荷引用直传）。审计字段全空时复用 <see cref="EventAuditMetadata.Empty"/>，
    /// 与 EF/PalORM/JSONL 导入同款。
    /// </summary>
    private static RecordedEvent ReadRecordedEvent(DbDataReader reader, in ReadOrdinals o, bool useBinaryEventId)
    {
        var actorId = reader.IsDBNull(o.ActorId) ? null : reader.GetString(o.ActorId);
        var reason = reader.IsDBNull(o.Reason) ? null : reader.GetString(o.Reason);
        PalUlid? correlationId = null;
        if (!reader.IsDBNull(o.CorrelationId))
            correlationId = PalUlid.Parse(reader.GetString(o.CorrelationId));
        PalUlid? causationId = null;
        if (!reader.IsDBNull(o.CausationId))
            causationId = PalUlid.Parse(reader.GetString(o.CausationId));
        var traceParent = reader.IsDBNull(o.TraceParent) ? null : reader.GetString(o.TraceParent);
        var traceState = reader.IsDBNull(o.TraceState) ? null : reader.GetString(o.TraceState);

        var audit = actorId is null && reason is null && correlationId is null && causationId is null
            && traceParent is null && traceState is null
                ? EventAuditMetadata.Empty
                : new EventAuditMetadata(actorId, reason, correlationId, causationId, traceParent, traceState);

        var eventId = useBinaryEventId && !reader.IsDBNull(o.EventIdBin)
            ? PalUlid.New(reader.GetFieldValue<byte[]>(o.EventIdBin))
            : PalUlid.Parse(reader.GetString(o.EventId));

        return RecordedEvent.RehydrateFromBytes(
            reader.GetString(o.StreamName),
            reader.GetInt64(o.StreamVersion),
            reader.GetInt64(o.GlobalPosition),
            ReadDateTimeOffset(reader, o.RecordedAt),
            eventId,
            reader.GetString(o.EventName),
            reader.GetInt32(o.SchemaVersion),
            reader.GetString(o.ContentType),
            reader.GetFieldValue<byte[]>(o.Payload),
            reader.GetFieldValue<byte[]>(o.Metadata),
            audit);
    }

    /// <summary>三方言 recorded_at 取值（SQLite TEXT / MySQL DATETIME / PG timestamptz）。</summary>
    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
        => reader.GetValue(ordinal) switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string text => DateTimeOffset.Parse(
                text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            var other => throw new InvalidOperationException(
                $"Unexpected recorded_at column type '{other.GetType().Name}'.")
        };

    private static void AddReadParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
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
        public byte[]? EventIdBin { get; set; }
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

    /// <summary>
    /// I（2026-10-10 优化轮）：单事件 Any 快路径的回读行——三方言模板以显式 PascalCase
    /// 别名对齐（PG RETURNING 别名 / MySQL/SQLite 聚合单行 SELECT 别名），不依赖
    /// MatchNamesWithUnderscores 全局开关。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Dapper QuerySingleAsync<T> 运行时反射物化（与同文件 EventRow 同路径），分析器不可见")]
    private sealed class AppendSingleRow
    {
#pragma warning disable S1144 // Dapper 运行时反射物化使用 init 访问器
        public long GlobalPosition { get; init; }
        public long StreamVersion { get; init; }
#pragma warning restore S1144
    }
}
