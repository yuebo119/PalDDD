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
/// <para>
/// ⚠️ <b>倒挂-跳过风险（v29 P3 声明，镜像 EventLogPositionReserver 三十八轮 Hi/Lo 同款）</b>：
/// DB 自增使 GlobalPosition 的<b>分配序</b>（INSERT 时点）与<b>事务提交序</b>可倒挂——事务 B
/// 先 INSERT 拿到更高位置 110 并先提交、事务 A 后拿 100 后提交时无碍；但事务 A 先 INSERT 拿到
/// 100 尚未提交、事务 B 后拿 110 先提交时，按 <c>ReadAllAsync(fromPosition)</c> 检查点消费的
/// 消费方读到 110（A 未提交不可见）即推进检查点至 110，A 提交后其事件位置 100 已低于检查点，
/// 被永久跳过。检查点用法要求所有追加方提交延迟相近，或改用 <c>ReadStreamAsync</c>
/// （流内 StreamVersion 严格连续，不受自增分配倒挂影响）。
/// </para>
/// <para>
/// ⚠️ <b>ITM-075 分类局限（v34 P3 声明，与 Dapper 对齐故不改行为）</b>：EFCore 姊妹
/// （EventLogDbContext）在同场景用 eventIdExists 探测区分 EventId 撞库与流推进两因；
/// 本栈与 Dapper 对齐为单一并发判定（重查 <c>MAX(stream_version) &gt;= 失败事件版本</c> 即转
/// EventStreamConcurrencyException）——event_id 撞库 + 并发推进同窗叠加时误译为并发异常
/// （调用方重试经版本预检自然收敛），接受此权衡；v5.0 对齐窗口（原 v4.0 改期，2026-10-11 发布轮改指）。
/// </para>
/// </summary>
public class PalOrmEventLog<TProvider> : IEventLog
    where TProvider : IDbProvider
{
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需直接访问 Session。")]
    protected readonly DataSession<TProvider> Session;

    private readonly TimeProvider _clock;

    /// <summary>
    /// 读取时优先使用 event_id_bin（M4）。默认 false 回退文本列；存量行回填后置 true。
    /// </summary>
    public bool UseBinaryEventId { get; set; }

    /// <summary>构造 EventLog。</summary>
    public PalOrmEventLog(DataSession<TProvider> session, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(session); // v22 C-1
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

        // v36 P3：补写侧观测（对齐 EFCore EventLogDbContext / InMemoryEventLog 姊妹实现——
        // 三栈中本栈此前零命中）：方法守卫后创建 activity（镜像 EventLogDbContext.AppendAsync :53
        // 与 InMemoryEventLog.AppendAsync :42 的 StartEventLogAppend 形态），成功路径返回前
        // EventLogAppended 计数（镜像 EventLogDbContext :73/:83 两个成功分支的 return 前计数——
        // 写路径非迭代器、异常直接上抛，失败路径不计数与姊妹语义一致，无需 finally）。
        using var activity = PalActivitySource.StartEventLogAppend(streamName, events.Count);

        // 单事件 PG 快路径：INSERT ... SELECT MAX+1 ... RETURNING，一次往返完成
        // 版本判定与写入（守卫失匹零行，冲突路径再查）。批量与其它方言保持原路径。
        if (events.Count == 1 && TProvider.Dialect == SqlDialect.PostgreSql)
            return await AppendSinglePostgreSqlAsync(
                streamName, expectedVersion, events[0], _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

        // 步骤 1：乐观并发检查 —— 读当前最大 StreamVersion
        var currentMax = await Session.ScalarAsync<long?>(
            $"SELECT MAX(stream_version) FROM events WHERE stream_name = {streamName}",
            ct: cancellationToken).ConfigureAwait(false);
        var currentVersion = currentMax ?? -1;
        if (!expectedVersion.Matches(currentVersion))
            throw new EventStreamConcurrencyException(streamName, expectedVersion, currentVersion);

        // 步骤 2：事件 INSERT
        // A11（S1 轮，2026-10-15）：多行 VALUES 批量化——原逐事件 Session.InsertAsync 使
        // k 事件 append 付 k 次往返（加 MAX 预检共 k+1）。PG/SQLite
        // （TProvider.SupportsReturningClause）改多行 INSERT ... RETURNING 单语句（共 2
        // 往返）；PALORM005 告警注释声称「批量 INSERT 无法保证 GlobalPosition 单调有序 +
        // RETURNING 每行」——返回值只需首末位置，取 min/max 即可，行序映射不成立的要求。
        // MySQL 等无 RETURNING 方言维持逐行 InsertAsync（多行批化的位置回查需 2 次额外
        // 往返，仅 k≥4 净胜——登记延后；自增值连续性在 innodb_autoinc_lock_mode=2 下不
        // 保证，first+k-1 推导不成立）。
        var now = _clock.GetUtcNow();
        long firstGlobalPos;
        long lastGlobalPos;
        if (TProvider.SupportsReturningClause)
        {
            (firstGlobalPos, lastGlobalPos) = await InsertBatchWithReturningAsync(
                streamName, expectedVersion, currentVersion, events, now, cancellationToken).ConfigureAwait(false);
        }
        else if (events.Count <= MySqlBatchRowLimit)
        {
            // B11（EPSL 地毯审计 2026-10-16）：MySQL 多行 VALUES 批量化——A11 登记延后的
            // 两条理由实证后均不成立（同 Dapper B2）：① 无 RETURNING 可用「多行 INSERT +
            // 版本区间回查 MIN/MAX」三往返达成（MAX 预检 + INSERT + 回查，较逐行 k+1 在
            // k≥3 净胜）；② innodb_autoinc_lock_mode=2 下已知行数多行 VALUES 的自增块
            // 连续（wslc mysql84 8.4.11 实测：5 行 LAST_INSERT_ID=首行值、min/max=1/5
            // 无间隙），且本实现不依赖连续性（区间回查按版本区间）。行上限 4000（16 参/行
            // = 60000 < MySQL 65535 占位符上限），超出回落逐行（与 Dapper B2 同口径）。
            (firstGlobalPos, lastGlobalPos) = await InsertBatchMySqlAsync(
                streamName, expectedVersion, currentVersion, events, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            (firstGlobalPos, lastGlobalPos) = await InsertRowByRowAsync(
                streamName, expectedVersion, currentVersion, events, now, cancellationToken).ConfigureAwait(false);
        }

        var firstStreamVersion = currentVersion + 1;
        var lastStreamVersion = currentVersion + events.Count;
        // lastGlobalPos 已在循环内每次更新（不再用 firstGlobalPos + count - 1 推导）
        // v36 P3：写侧 metrics（对齐 EventLogDbContext/InMemoryEventLog 的 EventLogAppended）
        PalMetrics.EventLogAppended.Add(events.Count);
        return new AppendEventsResult(streamName, firstStreamVersion, lastStreamVersion, firstGlobalPos, lastGlobalPos);
    }

    /// <summary>
    /// PG 单事件原子追加：服务端 <c>COALESCE(MAX(stream_version),-1)+1</c> 加版本守卫，
    /// 单往返返回 global_position 与 stream_version。守卫失匹零行时重查实际版本转统一
    /// 并发异常；唯一冲突先探 EventId，重复 ID 原样上抛，其余转并发异常。
    /// </summary>
    private async ValueTask<AppendEventsResult> AppendSinglePostgreSqlAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        EventData e,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var guard = expectedVersion.Kind switch
        {
            ExpectedStreamVersionKind.NoStream =>
                " WHERE COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @p3), -1) = -1",
            ExpectedStreamVersionKind.StreamExists =>
                " WHERE COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @p3), -1) >= 0",
            ExpectedStreamVersionKind.Exact =>
                " WHERE COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @p3), -1) = @p15",
            _ => string.Empty,
        };
        var sql =
            "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) " +
            "SELECT @p0, @p1, @p2, @p3, COALESCE((SELECT MAX(stream_version) FROM events WHERE stream_name = @p3), -1) + 1, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14" +
            guard +
            " RETURNING global_position, stream_version";

        var eventIdText = e.EventId.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        await using var cmd = CreateRawCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@p0", eventIdText);
        AddParam(cmd, "@p1", e.EventId.ToByteArray());
        AddParam(cmd, "@p2", e.EventName);
        AddParam(cmd, "@p3", streamName);
        AddParam(cmd, "@p4", e.SchemaVersion);
        AddParam(cmd, "@p5", e.ContentType);
        AddParam(cmd, "@p6", e.PayloadArray);
        AddParam(cmd, "@p7", e.MetadataArray);
        AddParam(cmd, "@p8", now);
        AddParam(cmd, "@p9", e.Audit.ActorId ?? (object)DBNull.Value);
        AddParam(cmd, "@p10", e.Audit.Reason ?? (object)DBNull.Value);
        AddParam(cmd, "@p11", e.Audit.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        AddParam(cmd, "@p12", e.Audit.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        AddParam(cmd, "@p13", e.Audit.TraceParent ?? (object)DBNull.Value);
        AddParam(cmd, "@p14", e.Audit.TraceState ?? (object)DBNull.Value);
        if (expectedVersion.Kind == ExpectedStreamVersionKind.Exact)
            AddParam(cmd, "@p15", expectedVersion.Version);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var globalPosition = reader.GetInt64(0);
                var streamVersion = reader.GetInt64(1);
                PalMetrics.EventLogAppended.Add(1);
                return new AppendEventsResult(
                    streamName, streamVersion, streamVersion, globalPosition, globalPosition);
            }
        }
        catch (DbException ex) when (SqlErrorClassifier.IsUniqueKeyViolation(ex))
        {
            var idExists = await Session.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM events WHERE event_id = {eventIdText}",
                ct: ct).ConfigureAwait(false) > 0;
            if (idExists)
                throw; // EventId 重复：与两往返路径同语义，原样上抛
            var actual = await Session.ScalarAsync<long?>(
                $"SELECT MAX(stream_version) FROM events WHERE stream_name = {streamName}",
                ct: ct).ConfigureAwait(false);
            throw new EventStreamConcurrencyException(streamName, expectedVersion, actual ?? -1);
        }

        // 守卫失匹（零行插入）：重查实际版本转统一并发异常（对齐预查 Matches 失败）。
        var after = await Session.ScalarAsync<long?>(
            $"SELECT MAX(stream_version) FROM events WHERE stream_name = {streamName}",
            ct: ct).ConfigureAwait(false);
        throw new EventStreamConcurrencyException(streamName, expectedVersion, after ?? -1);
    }

    /// <summary>
    /// A11（S1 轮，2026-10-15）：多行 VALUES INSERT ... RETURNING（PG/SQLite）——k 事件由
    /// k 次往返降为 1 次（加 MAX 预检共 2）。RETURNING 全部列映射 EventLogRow，取
    /// GlobalPosition 的 min/max 作首末（返回值只需首末，行序映射非必需——PALORM005 的
    /// 「无法保证单调有序」顾虑对 min/max 口径不成立）。冲突分类（ITM-075）与逐行形态
    /// 同款：唯一索引冲突 → 重查 MAX 分类（并发写入转统一并发异常 / 否则原样上抛），
    /// 批粒度用本批首事件版本作门槛（INSERT 原子失败，无部分写入）。
    /// </summary>
    private async ValueTask<(long First, long Last)> InsertBatchWithReturningAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        long currentVersion,
        IReadOnlyList<EventData> events,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var (sqlBuilder, args) = BuildBatchInsert(streamName, currentVersion, events, now);
        // 2026-10-19 性能实施轮：调用方只取 min/max 全局位置，RETURNING 只回传位置列。
        // 原实现经 EventLogRow 回传全部 16 列（含 payload/metadata），且 PalORM 的
        // ColumnOrderValidator 要求全列映射；改走 raw reader 直接读 long，既免回传载荷，
        // 也免 DTO 物化。{N} 孔转换为 @pN 参数（一孔一参数，与原绑定形态一致）。
        var sql = sqlBuilder.ToString()
            .Replace("{", "@p", StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            + " RETURNING global_position";

        var positions = new List<long>(events.Count);
        try
        {
            await using var cmd = CreateRawCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < args.Count; i++)
                AddParam(cmd, "@p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), args[i] ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                positions.Add(reader.GetInt64(0));
        }
        catch (DbException ex) when (SqlErrorClassifier.IsUniqueKeyViolation(ex))
        {
            // ITM-075（批粒度）分类收口共享 helper（B11 提取，RETURNING/MySQL 两批路径同源）
            throw await ClassifyBatchConflictAsync(streamName, expectedVersion, currentVersion, ex, ct).ConfigureAwait(false);
        }

        if (positions.Count == 0)
            throw new InvalidOperationException(
                $"Batch INSERT ... RETURNING returned no rows for stream '{streamName}' ({events.Count} event(s) submitted).");

        var first = long.MaxValue;
        var last = long.MinValue;
        foreach (var position in positions)
        {
            if (position < first) first = position;
            if (position > last) last = position;
        }
        return (first, last);
    }

    /// <summary>
    /// B11（EPSL 地毯审计 2026-10-16）：MySQL 多行 VALUES 批量追加——MySQL 无 RETURNING，
    /// INSERT 后按本批版本区间 raw command 回查 MIN/MAX 全局位置（(stream_name,
    /// stream_version) 唯一索引served；区间由唯一索引 + 冲突分类保证只含本批行）。
    /// 往返：MAX 预检 + INSERT + 回查 = 3（逐行 k+1），k≥3 净胜——A11 登记按「位置回查
    /// 需 2 次额外往返」计的 k≥4 阈值因单语句 MIN/MAX 回查修正为 k≥3。冲突分类与
    /// RETURNING 批路径同款（ITM-075 批粒度）。
    /// </summary>
    private async ValueTask<(long First, long Last)> InsertBatchMySqlAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        long currentVersion,
        IReadOnlyList<EventData> events,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var (sql, args) = BuildBatchInsert(streamName, currentVersion, events, now);

        try
        {
            await Session.ExecuteAsync(
                FormattableStringFactory.Create(sql.ToString(), args.ToArray()), ct: ct).ConfigureAwait(false);
        }
        catch (DbException ex) when (SqlErrorClassifier.IsUniqueKeyViolation(ex))
        {
            throw await ClassifyBatchConflictAsync(streamName, expectedVersion, currentVersion, ex, ct).ConfigureAwait(false);
        }

        // 版本区间回查首末全局位置（raw command 单往返——MIN/MAX 同语句取回）
        await using var cmd = CreateRawCommand();  // ITM-243：挂接 IUnitOfWork 环境事务
        cmd.CommandText = "SELECT MIN(global_position), MAX(global_position) FROM events WHERE stream_name = @p0 AND stream_version BETWEEN @p1 AND @p2";
        AddParam(cmd, "@p0", streamName);
        AddParam(cmd, "@p1", currentVersion + 1);
        AddParam(cmd, "@p2", currentVersion + events.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Batch INSERT position readback returned no rows for stream '{streamName}' ({events.Count} event(s) submitted).");
        // DBNull 容错：区间无行时 MIN/MAX 为 NULL——与「零行」同罪，抛出不伪装成功
        if (reader.IsDBNull(0) || reader.IsDBNull(1))
            throw new InvalidOperationException(
                $"Batch INSERT position readback returned NULL positions for stream '{streamName}' ({events.Count} event(s) submitted).");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>B11：MySQL 批量化行上限——16 参/行 × 4000 = 64000 &lt; MySQL 65535 占位符上限
    ///（与 Dapper MySqlBatchRowLimit 同口径）；超出回落逐行路径。</summary>
    private const int MySqlBatchRowLimit = 4000;

    /// <summary>
    /// raw command 工厂（B11 位置回查用）——手动命令不自动 enlist（PalORM 仅 ExecuteAsync
    /// 路径自动挂 GetActiveTransaction()），MySQL 活动事务下未挂接抛
    /// InvalidOperationException（MySqlConnector 严格校验，探针实测）。形态对齐
    /// PalOrmIdempotencyStore/PalOrmSagaStateStore 同款私有工厂（ITM-243）。
    /// </summary>
    private DbCommand CreateRawCommand()
    {
        var cmd = Session.GetRawConnection().CreateCommand();
        var tx = PalOrmAmbientTransaction.TryGet(Session);
        if (tx is not null) cmd.Transaction = tx;
        return cmd;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// 多行 VALUES INSERT 构造（RETURNING 批与 MySQL 批共用单一来源，B11 提取）——
    /// 列序与 EventLogRow 映射一致（global_position 自增除外）；Ulid 列按
    /// UlidStringConverter 的 26 字符字符串存储形态直传字符串。{N} 索引孔经
    /// FormattableStringFactory.Create 编入（A11 先例：PalORM 每插值孔只绑一个
    /// DbParameter，SQL 片段禁入插值——PD18）。
    /// </summary>
    private static (System.Text.StringBuilder Sql, List<object?> Args) BuildBatchInsert(
        string streamName, long currentVersion, IReadOnlyList<EventData> events, DateTimeOffset now)
    {
        var sql = new System.Text.StringBuilder(
            "INSERT INTO events (event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state) VALUES ");
        var args = new List<object?>(events.Count * 15);
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (i > 0) sql.Append(", ");
            sql.Append('(');
            object?[] values =
            [
                e.EventId.ToString(null, System.Globalization.CultureInfo.InvariantCulture), e.EventId.ToByteArray(), e.EventName, streamName, currentVersion + 1 + i,
                e.SchemaVersion, e.ContentType, e.PayloadArray, e.MetadataArray,
                now, e.Audit.ActorId, e.Audit.Reason,
                e.Audit.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture), e.Audit.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                e.Audit.TraceParent, e.Audit.TraceState
            ];
            for (var c = 0; c < values.Length; c++)
            {
                if (c > 0) sql.Append(", ");
                sql.Append('{').Append(args.Count).Append('}');
                args.Add(values[c]);
            }
            sql.Append(')');
        }
        return (sql, args);
    }

    /// <summary>
    /// ITM-075（批粒度）：唯一索引冲突分类——重查实际版本，命中并发写入
    /// （&gt;= 本批首事件版本 currentVersion+1）转统一并发异常；否则（如批内 EventId
    /// 重复）原样上抛。RETURNING 批与 MySQL 批共用（B11 提取）。
    /// </summary>
    private async Task<Exception> ClassifyBatchConflictAsync(
        string streamName, ExpectedStreamVersion expectedVersion, long currentVersion, DbException original, CancellationToken ct)
    {
        long? actualVersion = null;
        var requerySucceeded = false;
        try
        {
            actualVersion = await Session.ScalarAsync<long?>(
                $"SELECT MAX(stream_version) FROM events WHERE stream_name = {streamName}",
                ct: ct).ConfigureAwait(false);
            requerySucceeded = true;
        }
        catch (DbException)
        {
            // 重查失败（如 PG aborted 事务）——放弃分类，走原始异常上抛
        }
        if (requerySucceeded && (actualVersion ?? -1) >= currentVersion + 1)
            return new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion ?? -1);
        return original;
    }

    /// <summary>
    /// 逐行 INSERT 回退路径（MySQL 等无 RETURNING 方言）——原循环形态原样保留：
    /// InsertAsync 对自增主键自动回填 GlobalPosition（MySQL 经 LAST_INSERT_ID），
    /// firstGlobalPos 记录首条返回、lastGlobalPos 每次更新（不用算术推导——并发下
    /// GlobalPosition 非连续）。冲突分类（ITM-075）与原形态逐字一致。
    /// </summary>
    private async ValueTask<(long First, long Last)> InsertRowByRowAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        long currentVersion,
        IReadOnlyList<EventData> events,
        DateTimeOffset now,
        CancellationToken ct)
    {
#pragma warning disable PALORM005 // 无 RETURNING 方言的逐行回退（批量位置回查需 2 次额外往返，仅 k≥4 净胜）
        long firstGlobalPos = 0;
        long lastGlobalPos = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var streamVersion = currentVersion + 1 + i;
            var row = new EventLogRow
            {
                EventId = e.EventId,
                EventIdBin = e.EventId.ToByteArray(),
                EventName = e.EventName,
                StreamName = streamName,
                StreamVersion = streamVersion,
                SchemaVersion = e.SchemaVersion,
                ContentType = e.ContentType,
                // v65 P3 零拷贝路径（对齐 StoredEvent.From 与 DapperEventLog 用法）：PayloadArray/
                // MetadataArray 是 EventData 构造期 ToArray 得到的同一数组实例，EventData 构造后
                // 不可变（公开 API 契约，EventData.cs:60-73 声明 internal 消费方只读不写），
                // InsertAsync 仅读取后立即发送——免去每事件 payload/metadata 各一次防御性拷贝。
                Payload = e.PayloadArray,
                Metadata = e.MetadataArray,
                RecordedAt = now,
                ActorId = e.Audit.ActorId,
                Reason = e.Audit.Reason,
                CorrelationId = e.Audit.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                CausationId = e.Audit.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                TraceParent = e.Audit.TraceParent,
                TraceState = e.Audit.TraceState,
            };
            EventLogRow inserted;
            try
            {
                inserted = await Session.InsertAsync(row, ct).ConfigureAwait(false);
            }
            catch (DbException ex) when (SqlErrorClassifier.IsUniqueKeyViolation(ex))
            {
                // ITM-075：与批量化路径同款的冲突分类（逐行粒度用本事件版本作门槛）
                long? actualVersion = null;
                var requerySucceeded = false;
                try
                {
                    actualVersion = await Session.ScalarAsync<long?>(
                        $"SELECT MAX(stream_version) FROM events WHERE stream_name = {streamName}",
                        ct: ct).ConfigureAwait(false);
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
            lastGlobalPos = inserted.GlobalPosition;
        }
#pragma warning restore PALORM005
        return (firstGlobalPos, lastGlobalPos);
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
            "SELECT global_position, event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE stream_name = {0} AND stream_version >= {1} ORDER BY stream_version"
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
            // v66 P3：ConfigureAwait(false) 补齐（对齐文件内其余 await）——库代码避免捕获
            // 调用方 SyncContext；对 IAsyncEnumerable 用 ConfiguredAsyncEnumerable 扩展。
            // 读副本标注（2026-09-25 特性审计裁决）：纯读流无 read-after-write 依赖——
            // 上游契约（6.6.0 XML 复核，2026-10-20 升位；6.4.x 系列前核）：readFromReplica=true 且未配置 ReadConnectionString 时
            // 逐位回落主库（未配置=行为不变）；配置后本读流可走副本。6.4.0 ARCH-001 读路由收口（READ-002 opt-in 教义 6.6.0 XML 明示不回归，与 6.4.x 逐字同文）
            // （2026-10-06）后 opt-in 教义明示不回归（CreateReadRoutedCommandAsync 分支③——本调用无事务无并行作用域）。
            await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(readStreamSql, readFromReplica: true, ct: cancellationToken).ConfigureAwait(false))
            {
                if (--maxCount < 0) yield break; // P3 修复：先减后判（maxCount=0 时零产出，对齐 EFCore ThrowIfLessThan(1)）
                checked { read++; }
                yield return ToRecorded(row, streamName, UseBinaryEventId);
            }
        }
        finally
        {
            // v25 P3 指标族（D5）：pal.eventlog.read_count SetTag 移除——读取数量随
            // maxCount 无界，高基数命中 ITM-229 清理标准（与 InMemory/EFCore 实现同步）；
            // 计数保留在 EventLogRead 指标
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
            "SELECT global_position, event_id, event_id_bin, event_name, stream_name, stream_version, schema_version, content_type, payload, metadata, recorded_at, actor_id, reason, correlation_id, causation_id, trace_parent, trace_state FROM events WHERE global_position >= {0} ORDER BY global_position"
                + (maxCount != int.MaxValue ? $" LIMIT {maxCount}" : string.Empty),
            fromPosition);
        using var activity = PalActivitySource.StartEventLogReadAll(fromPosition);
        var read = 0;
        // ITM-167 修复：补 metrics 并置入 finally + yield 前计数（同 ReadStreamAsync，
        // 对齐 EventLogDbContext/InMemoryEventLog）。
        try
        {
            // 读副本标注（同 ReadStreamAsync——纯读流无 read-after-write 依赖，未配置读串时回落主库）
            await foreach (var row in Session.QueryAsyncEnumerable<EventLogRow>(readAllSql, readFromReplica: true, ct: cancellationToken).ConfigureAwait(false))
            {
                if (--maxCount < 0) yield break; // P3 修复（八轮评审）：先减后判——对齐 ReadStreamAsync 结构
                checked { read++; }
                yield return ToRecorded(row, row.StreamName, UseBinaryEventId);
            }
        }
        finally
        {
            // v25 P3 指标族（D5）：pal.eventlog.read_count SetTag 移除——同 ReadStreamAsync
            PalMetrics.EventLogRead.Add(read);
        }
    }

    /// <summary>EventLogRow → RecordedEvent 领域类型（零拷贝路径，payload/metadata 直接引用 byte[]）。</summary>
    private static RecordedEvent ToRecorded(EventLogRow row, string streamName, bool useBinaryEventId)
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
        var eventId = useBinaryEventId && row.EventIdBin is { Length: 16 }
            ? Ulid.New(row.EventIdBin)
            : row.EventId;
        return RecordedEvent.RehydrateFromBytes(
            streamName, row.StreamVersion, row.GlobalPosition, row.RecordedAt,
            eventId, row.EventName, row.SchemaVersion, row.ContentType,
            row.Payload, row.Metadata, audit);
    }

    /// <summary>安全解析 Ulid 字符串 —— 脏数据返回 null 而非抛异常（与 OutboxMessageRow P0-6 同风格）。</summary>
    private static Ulid? ParseUlid(string? value)
        => value is not null && Ulid.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var ulid)
            ? ulid
            : (Ulid?)null;

}
