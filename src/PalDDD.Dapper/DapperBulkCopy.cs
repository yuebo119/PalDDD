// ─────────────────────────────────────────────────────────────
// 📦 DapperBulkCopy — 数据库原生批量导入（零反射，AOT 安全）
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要这个？
//   ｜ 逐条 INSERT 在大批量场景下非常慢（每条都是一个网络往返）。
//   ｜ 各数据库都提供了原生批量导入机制：
//   ｜   - PostgreSQL COPY 协议 → 比逐行 INSERT 快 ~100 倍
//   ｜   - MySQL MySqlBulkCopy  → 比逐行 INSERT 快 ~10 倍
//   ｜   - SQLite 事务批处理    → 批量 INSERT 在一个事务中
//   ｜
//   ｜ DapperBulkCopy 封装了这三种机制，按 DapperDbType 枚举自动分发。
//   ｜ 调用者不需要知道底层数据库——只需传入列名和值提取函数。
//
// ✅ AOT 安全性：
//   ✅ Func<T, object?[]> 委托模式 — 值提取由调用者 lambda 完成，零反射
//   ✅ switch/Compiler 类型分发 — C# 编译时类型匹配，零 MakeGenericType
//   ✅ 列名数组 + 函数指针 — 零 PropertyInfo.GetValue()
//
// ⚡ 性能：
//   ✅ PostgreSQL COPY — BinaryImport 直接写入 Socket，零 SQL 解析
//   ✅ MySQL BulkCopy — 原生 LOAD DATA INFILE 协议
//   ✅ SQLite — 事务 + 参数化批量 INSERT，复用 Command 和 Parameters
//   ✅ ConfigureAwait(false) — 所有异步调用零 SynchronizationContext 捕获
//
// 使用示例：
//   await DapperBulkCopy.BulkInsertAsync(conn, DapperDbType.PostgreSql, "outbox_messages",
//       ["id", "type", "payload"],
//       messages,
//       m => [m.Id, m.Type, m.Payload]);
//
// 📐 DDD 位置：基础设施层 — 数据库批量操作是纯技术关注点。
// ─────────────────────────────────────────────────────────────

using MySqlConnector;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Dapper;

/// <summary>
/// 数据库原生批量导入 — 按数据库类型自动选择最优路径。<br/>
/// 支持 PostgreSQL COPY、MySQL MySqlBulkCopy、SQLite 事务批处理。
/// </summary>
public static class DapperBulkCopy
{
    // C-15（EPSL 地毯审计 2026-10-11）：AllowLoadLocalInfile 判定按连接实例缓存
    //（ConditionalWeakTable——连接串含凭据不以字符串驻留静态表；长连接复用免每次
    // 全量解析连接串只为读一个布尔）。见 MySqlBulkAsync 入口检测的使用点。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbConnection, System.Runtime.CompilerServices.StrongBox<bool>>
        s_allowLoadLocalInfileByConnection = new();

    /// <summary>
    /// 批量插入实体（按数据库类型自动分发到最优实现）。<br/>
    /// 💡 泛型参数 <typeparamref name="T"/> 不产生运行时类型检查——所有类型信息由调用者 lambda 提供。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    /// <param name="conn">数据库连接（必须已 Open）</param>
    /// <param name="dbType">数据库类型枚举（用于选择最优批量路径）</param>
    /// <param name="tableName">目标表名</param>
    /// <param name="columns">列名列表（顺序必须与值提取函数一致）</param>
    /// <param name="items">实体列表</param>
    /// <param name="valueExtractor">每行值提取函数：item → object?[]，调用者 lambda 完成，零反射。
    /// 调用次数契约（ITM-251）：三方言均每行恰调用一次；首行因入口长度校验额外一次
    /// （首行值可能被提取两次）。MySQL 路径类型推断复用缓存值，不二次提取。
    /// P2 修复（八轮评审，配套批量追踪列）：元素类型放宽为可空——null 由各方言路径归一为 SQL NULL
    /// （SQLite/MySQL 显式 ?? DBNull.Value，PG COPY 走 NpgsqlDbType.Unknown）；Func 协变保证
    /// 既有 object[] 返回的 lambda 兼容。</param>
    /// <param name="transaction">可选外部事务（UnitOfWork 模式）。三十八轮 P2 修复：三方言行为统一声明——
    /// PG COPY 自动加入连接上的活动局部事务（本参数仅作契约显式化）；MySQL 显式挂接传入事务
    /// （原实现未挂接，连接有活动事务时 WriteToServerAsync 直接抛 InvalidOperationException）；
    /// SQLite 传入时命令挂接该事务且不 Commit/Dispose（所有权归调用方），未传时自建本地事务。</param>
    /// <returns>成功插入的行数</returns>
    /// <exception cref="NotSupportedException">不支持的数据库类型</exception>
    public static async ValueTask<int> BulkInsertAsync<T>(
        DbConnection conn,
        DapperDbType dbType,
        string tableName,
        string[] columns,
        IReadOnlyList<T> items,
        Func<T, object?[]> valueExtractor,
        DbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(valueExtractor);
        ValidateIdentifier(tableName, nameof(tableName), allowDot: true);
        ValidateColumns(columns);

        if (items.Count == 0) return 0;

        // P3 修复（二十一轮）：EnsureOpen → EnsureOpenAsync——同步 Open 在 UI 线程/受限
        // 同步上下文下阻塞，且 CancellationToken 无法传导（取消要等 Open 完成后才生效）。
        await EnsureOpenAsync(conn, ct).ConfigureAwait(false);

        // ITM-195 修复（三十轮）：首行校验值提取长度与列数一致——lambda 返回数组短于
        // 列数时原实现抛晦涩 IndexOutOfRange/MysqlDataTruncation；长于列数时多余值
        // 静默丢弃。入口一次校验（列数恒定，首行代表性）给出可定位的 ArgumentException。
        // 契约声明（ITM-207 三十一轮）：此校验使 extractor 对首行额外调用一次——
        // valueExtractor 必须是无副作用的纯提取函数（首行值可能被提取两次）。
        // ITM-251 修复（F9）：MySQL 路径原对每行调用 extractor 两次（类型推断循环 +
        // 填充循环），与本契约失实；现已物化缓存，每行恰一次（加本探针后首行共两次）。
        // 契约自此对三方言一致成立：每行一次 + 首行额外一次。
        var probe = valueExtractor(items[0]);
        if (probe.Length != columns.Length)
            throw new ArgumentException(
                $"valueExtractor 返回 {probe.Length} 个值，但 columns 有 {columns.Length} 列。",
                nameof(valueExtractor));

        // 💡 switch 表达式按 DapperDbType 枚举分发 — 编译时已知值，零反射
        return dbType switch
        {
            DapperDbType.PostgreSql => await PgCopyAsync(conn, tableName, columns, items, valueExtractor, transaction, ct).ConfigureAwait(false),
            DapperDbType.MySql => await MySqlBulkAsync(conn, tableName, columns, items, valueExtractor, transaction, ct).ConfigureAwait(false),
            DapperDbType.Sqlite => await SqliteBatchAsync(conn, tableName, columns, items, valueExtractor, transaction, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"数据库类型 {dbType} 不支持批量导入。")
        };
    }

    /// <summary>
    /// Outbox 专用 PG COPY（2026-10-21）：强类型逐列写入。通用泛型路径每行构造
    /// <c>object?[]</c> 并对每个值做一次 <see cref="ConvertForNpgsql"/> 类型分派，
    /// 值类型全部装箱；本路径按列类型直接调用 <see cref="NpgsqlBinaryImporter"/> 写入，
    /// 每行零数组分配、零装箱。列集与 <c>DapperOutboxStore.AddMessagesAsync</c> 通用路径一致。
    /// </summary>
    internal static async Task<int> BulkInsertOutboxAsync(
        DbConnection conn,
        IReadOnlyList<OutboxMessage> messages,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;
        if (transaction is not null and not NpgsqlTransaction)
            throw new ArgumentException(
                $"PG COPY 要求 NpgsqlTransaction，收到 {transaction.GetType().Name}。", nameof(transaction));

        await EnsureOpenAsync(conn, ct).ConfigureAwait(false);
        var pgConn = (NpgsqlConnection)conn;
        await using var writer = await pgConn.BeginBinaryImportAsync(
            "COPY outbox_messages (id,type,payload,content_type,schema_version,status,created_at,next_attempt_at,correlation_id,causation_id,trace_parent,trace_state) FROM STDIN (FORMAT BINARY)",
            ct).ConfigureAwait(false);

        foreach (var message in messages)
        {
            await writer.StartRowAsync(ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.Id.ToString(null, CultureInfo.InvariantCulture),
                NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.Type, NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.Payload, NpgsqlTypes.NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.ContentType, NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.SchemaVersion, NpgsqlTypes.NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                (int)OutboxStatus.Pending, NpgsqlTypes.NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.CreatedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.NextAttemptAt ?? message.CreatedAt,
                NpgsqlTypes.NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.CorrelationId?.ToString(null, CultureInfo.InvariantCulture),
                NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.CausationId?.ToString(null, CultureInfo.InvariantCulture),
                NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.TraceParent, NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(
                message.TraceState, NpgsqlTypes.NpgsqlDbType.Text, ct).ConfigureAwait(false);
        }

        var rowsWritten = await writer.CompleteAsync(ct).ConfigureAwait(false);
        return checked((int)rowsWritten);
    }

    // ─────────── PostgreSQL COPY（BinaryImport）───────────

    /// <summary>
    /// PostgreSQL COPY 协议批量导入。<br/>
    /// 💡 COPY FROM STDIN (FORMAT BINARY) 直接写入 Socket，绕过 SQL 解析器。<br/>
    /// ⚡ 比逐行 INSERT 快约 100 倍（取决于网络延迟和数据量）。
    /// </summary>
    private static async Task<int> PgCopyAsync<T>(
        DbConnection conn, string table, string[] cols,
        IReadOnlyList<T> items, Func<T, object?[]> extractor, DbTransaction? transaction, CancellationToken ct)
    {
        // 三十八轮 P2：Npgsql COPY 自动加入连接上的活动局部事务（Npgsql 语义），
        // 本参数仅作契约显式化——无需额外挂接动作。
        // v66 P3：对称 fail-fast（对齐 MySQL :275/SQLite :360 姊妹——传非 NpgsqlTransaction
        // 的 DbTransaction 子类立即暴露契约错误，而非静默忽略 COPY 挂接活动事务的语义）
        if (transaction is not null and not NpgsqlTransaction)
            throw new ArgumentException(
                $"PG COPY 要求 NpgsqlTransaction，收到 {transaction.GetType().Name}。", nameof(transaction));
        var pgConn = (NpgsqlConnection)conn;
        var colList = string.Join(", ", cols);
        var copySql = $"COPY {table} ({colList}) FROM STDIN (FORMAT BINARY)";

        // BeginBinaryImportAsync — Npgsql 10.x 标准 COPY API
        await using var writer = await pgConn.BeginBinaryImportAsync(copySql, ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            // v25 P3 生成器族/指标族（D7）：三处 await 补传 ct——Npgsql 10.x 的
            // NpgsqlBinaryImporter 三个方法均有 CancellationToken 重载（本地 NuGet XML
            // 证实：StartRowAsync(ct)/WriteAsync(T,NpgsqlDbType,ct)/CompleteAsync(ct)），
            // 原无参调用使取消要等当前行写完/整批 Complete 后才生效，与入口
            // BeginBinaryImportAsync(ct) 的取消传导不一致
            await writer.StartRowAsync(ct).ConfigureAwait(false);      // 开始新行
            foreach (var val in extractor(item))
            {
                // P1 修复（七轮评审）：Ulid/DateTimeOffset 无 Npgsql 原生映射——
                // Unknown 类型写入 raw 对象时抛类型解析异常。与 SQLite 路径对称做类型转换。
                var converted = ConvertForNpgsql(val);
                await writer.WriteAsync(converted.value, converted.type, ct).ConfigureAwait(false);
            }
        }

        // CompleteAsync — 发送 COPY 结束标记，返回成功写入的行数
        var rowsWritten = await writer.CompleteAsync(ct).ConfigureAwait(false);
        return (int)rowsWritten;
    }

    /// <summary>
    /// P1 修复 + 三十七轮 P2-3：Ulid/DateTimeOffset 转 Npgsql 原生类型；主流基元类型补显式映射。
    /// 原 default 分支对 byte[]/int/string/long/bool/Guid 等全走 Unknown——COPY BINARY 下依赖运行时推断，
    /// 可能抛晦涩类型异常或类型误写。补显式映射后 default 仅覆盖罕见类型（decimal/TimeSpan 等），
    /// Unknown 作为最后的运行时推断回退保留。
    /// </summary>
    private static (object? value, NpgsqlTypes.NpgsqlDbType type) ConvertForNpgsql(object? val)
    {
        return val switch
        {
            null => (null, NpgsqlTypes.NpgsqlDbType.Unknown),
            ByteAether.Ulid.Ulid ulid => (ulid.ToString(null, System.Globalization.CultureInfo.InvariantCulture), NpgsqlTypes.NpgsqlDbType.Text),
            DateTimeOffset dto => (dto, NpgsqlTypes.NpgsqlDbType.TimestampTz),
            // 三十七轮 P2-3：主流基元类型显式映射（消除 Unknown 覆盖面）
            byte[] bytes => (bytes, NpgsqlTypes.NpgsqlDbType.Bytea),
            int i => (i, NpgsqlTypes.NpgsqlDbType.Integer),
            long l => (l, NpgsqlTypes.NpgsqlDbType.Bigint),
            string s => (s, NpgsqlTypes.NpgsqlDbType.Text),
            bool b => (b, NpgsqlTypes.NpgsqlDbType.Boolean),
            Guid g => (g, NpgsqlTypes.NpgsqlDbType.Uuid),
            double d => (d, NpgsqlTypes.NpgsqlDbType.Double),
            float f => (f, NpgsqlTypes.NpgsqlDbType.Real),
            short sh => (sh, NpgsqlTypes.NpgsqlDbType.Smallint),
            // v25 P3 生成器族/指标族（D6）：DateTime 按 Kind 分派——Npgsql 6+ 明确拒绝
            // Kind=Utc 的 DateTime 写入 timestamp without time zone（InvalidCastException），
            // 原恒定 Timestamp 映射对 Utc 输入必抛；Utc 走 TimestampTz（timestamptz 恰好
            // 只接受 Utc），Unspecified/Local 走 Timestamp（Npgsql 对二者接受）。
            // 边界：Kind=Local 写 timestamp 按本地字面值写入（Npgsql 不做时区换算）——
            // 与 Npgsql 官方 converter 行为一致，调用方负责语义正确性。
            DateTime dt => (dt, dt.Kind == DateTimeKind.Utc
                ? NpgsqlTypes.NpgsqlDbType.TimestampTz
                : NpgsqlTypes.NpgsqlDbType.Timestamp),
            _ => (val, NpgsqlTypes.NpgsqlDbType.Unknown),  // 罕见类型回退（decimal/TimeSpan/自定义类型等）
        };
    }

    // ─────────── MySQL MySqlBulkCopy ───────────

    /// <summary>
    /// MySQL BulkCopy 批量导入。<br/>
    /// 💡 MySqlBulkCopy 使用 MySQL 原生的 LOAD DATA INFILE 协议，比逐行 INSERT 快约 10 倍。<br/>
    /// ⚡ 大批量（&gt; <see cref="MySqlMultiRowInsertRowThreshold"/> 行）优先 LOAD DATA；
    /// 连接未声明 <c>AllowLoadLocalInfile=True</c> 时自动回退参数化多行 VALUES（按参数预算分块、
    /// 本地事务），不再因 17 行以上直接失败。小批量始终走多行 VALUES（C2'，2026-10-09）。<br/>
    /// 🛡️ 检查 <see cref="MySqlBulkCopyResult.Warnings"/> 防止静默数据截断。<br/>
    /// ⚠️ v35 P3：列类型按首个非空值采样（ITM-214 跨行首个非空值口径）——跨行同列异型
    ///（如首行 int、后续 long）时读取器列定型为首个非空值的类型，不兼容值延迟到
    /// WriteToServerAsync 阶段晦涩失败。真实调用方（Store/源码生成器）的
    /// extractor 同列同型；外部自定义 extractor 需自保每列类型一致。
    /// </summary>
    private static async Task<int> MySqlBulkAsync<T>(
        DbConnection conn, string table, string[] cols,
        IReadOnlyList<T> items, Func<T, object?[]> extractor, DbTransaction? transaction, CancellationToken ct)
    {
        var myConn = (MySqlConnection)conn;

        // C2'（2026-10-09 优化轮）：小批量路由到参数化多行 VALUES——单往返且无
        // AllowLoadLocalInfile 部署要求（LOAD DATA 的安全门由调用方显式决策，小批量
        // 不应被迫继承该要求）；阈值覆盖领域保存多事件典型形态（k=1~5）。大批量在
        // 允许 local_infile 时走 MySqlBulkCopy，否则回退多行 VALUES（见下方分支）。
        if (items.Count <= MySqlMultiRowInsertRowThreshold)
            return await MySqlMultiRowValuesAsync(conn, table, cols, items, extractor, transaction, ct).ConfigureAwait(false);

        // P3 修复（二十一轮）：AllowLoadLocalInfile 前提从注释升级为运行时检测——
        // MySqlBulkCopy 走 LOAD DATA LOCAL INFILE 协议，MySqlConnector 连接串缺
        // AllowLoadLocalInfile=true 时 WriteToServerAsync 抛晦涩的协议异常（或静默零行）。
        // 不自动开启（该选项涉及服务端 local_infile 权限面，须由调用方显式决策）。
        // 检测放在 MySQL 批量路径入口而非 AddPalMySqlDataSource DI 入口——前提仅批量路径
        // 需要，DI 入口抛异常会误伤不使用 bulk copy 的应用启动。
        // C2' 注：本检查现仅约束大批量（LOAD DATA）路径——小批量已改走 VALUES，无此要求
        // C-15：判定经上方类级 CWT 缓存（首查解析，长连接复用免重复解析）
        var allowLoadLocalInfile = s_allowLoadLocalInfileByConnection.GetValue(
            myConn,
            static c => new System.Runtime.CompilerServices.StrongBox<bool>(new MySqlConnectionStringBuilder(c.ConnectionString).AllowLoadLocalInfile)).Value;
        if (!allowLoadLocalInfile)
        {
            // 无 local_infile 时不再直接拒绝：按参数预算回退参数化多行 VALUES。
            // 17 行等常见批量不再被迫扩大服务端文件访问面；超过单语句参数预算时
            // 在本地事务内分块，保持全成全败。
            var maxRows = Math.Max(1, 60_000 / Math.Max(1, cols.Length));
            if (items.Count <= maxRows)
                return await MySqlMultiRowValuesAsync(conn, table, cols, items, extractor, transaction, ct).ConfigureAwait(false);

            DbTransaction? localTx = null;
            var effectiveTx = transaction;
            if (effectiveTx is null)
            {
                localTx = await myConn.BeginTransactionAsync(ct).ConfigureAwait(false);
                effectiveTx = localTx;
            }

            try
            {
                var total = 0;
                for (var start = 0; start < items.Count; start += maxRows)
                {
                    var count = Math.Min(maxRows, items.Count - start);
                    var chunk = new T[count];
                    for (var i = 0; i < count; i++)
                        chunk[i] = items[start + i];
                    total += await MySqlMultiRowValuesAsync(
                        conn, table, cols, chunk, extractor, effectiveTx, ct).ConfigureAwait(false);
                }
                if (localTx is not null)
                    await localTx.CommitAsync(ct).ConfigureAwait(false);
                return total;
            }
            finally
            {
                if (localTx is not null)
                {
                    try { await localTx.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (ex is InvalidOperationException or DbException) { /* 事务已失效/已释放 */ }
                }
            }
        }

        // 构建 DataTable — MySqlBulkCopy 的唯一数据源格式
        // 注意：DataTable 在现代 .NET（net6.0+）中已 AOT 兼容
        // ITM-083 修复：DataTable 用 using 声明（成功/异常路径都释放）。
        // MySqlBulkCopy 经查证（MySqlConnector 2.6.x XML 文档）不实现 IDisposable——无 Dispose 可调，
        // 其内部连接生命周期由 myConn 持有者管理；此处仅 DataTable 需要释放。
        // ITM-214 修复（三十二轮）：按首行非空值推断列类型（ITM-251 后演进为跨行首个非空值，见下）——
        // 默认 string 列把 byte[] 静默 ToString() 为 "System.Byte[]"，二进制负载损坏。
        // ITM-251 修复（F9）：提取值一次性物化缓存——原推断循环 + 填充循环各调一次
        // extractor（首行含 null 列时推断循环扫多行，每行被提取两次），与入口契约
        // "首行值可能被提取两次"失实。现每行恰提取一次，推断/填充共用缓存；
        // 值已全部在手，类型推断退化为内存数组扫描（无需提前 break）。
        var cachedValues = new object?[items.Count][];
        for (var i = 0; i < items.Count; i++)
            cachedValues[i] = extractor(items[i]);
        var columnTypes = new Type?[cols.Length];
        foreach (var sampleValues in cachedValues)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                if (columnTypes[i] is null && sampleValues[i] is not null)
                {
                    var converted = ConvertForMySql(sampleValues[i]);
                    columnTypes[i] = converted?.GetType();
                }
            }
        }

        // 2026-10-21：以轻量 DbDataReader 替代 DataTable。原始行数组直接交给读取器，
        // 逐值转换在 GetValue 时执行（与 DataTable 填充循环逐一对应），不再构造列元数据、
        // DataRow 对象与整表行集合。MySqlBulkCopy 的 IDataReader 重载按 GetFieldType/GetValue 取值。
        var finalColumnTypes = new Type[cols.Length];
        for (int i = 0; i < cols.Length; i++)
            finalColumnTypes[i] = columnTypes[i] ?? typeof(object);

        using var reader = new BulkValueReader(cachedValues, cols, finalColumnTypes, ConvertForMySql);

        // MySqlBulkCopy 不实现 IDisposable（MySqlConnector 2.6.x）——无 using，见上方 ITM-083 注释
        // 三十八轮 P2 修复：显式挂接传入事务——MySqlConnector 要求连接有活动事务时
        // 命令必须挂接同一事务，原实现未挂接导致 UnitOfWork 内调用直接抛 InvalidOperationException
        var mysqlTx = transaction as MySqlTransaction
            ?? (transaction is null
                ? null
                : throw new ArgumentException(
                    $"MySQL 批量导入要求事务为 MySqlTransaction，实际为 {transaction.GetType().Name}。",
                    nameof(transaction)));
        var bulkCopy = new MySqlBulkCopy(myConn, mysqlTx)
        {
            DestinationTableName = table
        };

        // 映射读取器列 → 数据库列（按索引匹配）
        // P3-SRC-209 修复（R44 ITM-280 勘正动机）：列映射改按索引直配——原 Array.IndexOf 逐列
        // 扫描 O(n²)；索引直配 O(n) 且语义显式。2026-10-21 勘正：DataTable 已被 BulkValueReader
        // 替换，重复列名不再由 DataTable.Columns.Add 拦截——现由 ValidateColumns 显式拒绝。
        for (int i = 0; i < cols.Length; i++)
            bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, cols[i]));

        // WriteToServerAsync — 真正执行批量导入
        var result = await bulkCopy.WriteToServerAsync(reader, ct).ConfigureAwait(false);

        // 🛡️ 检查 Warnings — MySQL BulkCopy 可能因类型不兼容而静默截断数据
        // 例如：字符串超过列长度被截断、数值溢出被强制转换
        if (result.Warnings.Count > 0)
        {
            var warnings = string.Join("; ", result.Warnings.Select(w => $"{w.Message} (level={w.Level})"));
            throw new InvalidOperationException(
                $"MySqlBulkCopy 完成但有 {result.Warnings.Count} 条警告（可能有数据截断）: {warnings}");
        }

        // ITM-197 修复（三十轮）：RowsInserted 与 items 数不符（部分写入/被忽略行）显式暴露——
        // 仅靠 Warnings 间接覆盖不足（无 warning 的部分写入不报，调用方误以为全量成功）。
        if (result.RowsInserted != items.Count)
            throw new InvalidOperationException(
                $"MySqlBulkCopy 仅插入 {result.RowsInserted}/{items.Count} 行（非全量，可能含被忽略/重复键行）。");

        return result.RowsInserted;
    }

    /// <summary>
    /// C2'（2026-10-09 优化轮）：MySQL 小批量路由阈值——≤ 此行数走参数化多行 VALUES
    /// （单往返、无 AllowLoadLocalInfile 要求），超过走 MySqlBulkCopy（LOAD DATA）。
    /// 取值依据：领域保存多事件典型形态 k=1~5 + 小批量种子；16×11 列=176 参数，
    /// 远离 MySQL 单语句 65535 参数上界。
    /// </summary>
    private const int MySqlMultiRowInsertRowThreshold = 16;

    /// <summary>
    /// C2'（2026-10-09 优化轮）：MySQL 参数化多行 VALUES 插入——单语句单往返。
    /// 动机：原批量路径统一走 MySqlBulkCopy（LOAD DATA），小批量被迫继承其
    /// AllowLoadLocalInfile + local_infile=1 部署要求，而多行 VALUES 是标准 SQL、
    /// 无额外权限面。参数走 ADO 原生 CreateParameter（不经 Dapper 拦截器，
    /// dapper-param-guard 的匿名参数枚举直传形态在本路径不出现）。
    /// </summary>
    private static async Task<int> MySqlMultiRowValuesAsync<T>(
        DbConnection conn, string table, string[] cols,
        IReadOnlyList<T> items, Func<T, object?[]> extractor, DbTransaction? transaction, CancellationToken ct)
    {
        // 外部事务类型校验（镜像 MySqlBulkAsync 的 MySqlTransaction fail-fast）；
        // 单语句自原子，未传事务时无需自建（autocommit 单语句即一次提交）
        var mysqlTx = transaction as MySqlTransaction
            ?? (transaction is null
                ? null
                : throw new ArgumentException(
                    $"MySQL 多行 VALUES 插入要求事务为 MySqlTransaction，实际为 {transaction.GetType().Name}。",
                    nameof(transaction)));

        var colList = string.Join(", ", cols);
        var rows = new string[items.Count];
        for (var r = 0; r < items.Count; r++)
        {
            var placeholders = new string[cols.Length];
            for (var c = 0; c < cols.Length; c++)
                placeholders[c] = $"@r{r}c{c}";
            rows[r] = "(" + string.Join(", ", placeholders) + ")";
        }
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table/colList 经 ValidateIdentifier/ValidateColumns 入口校验，值侧全参数化
        var sql = $"INSERT INTO {table} ({colList}) VALUES {string.Join(", ", rows)}"; // NOSONAR: S2077 标识符白名单 + 值参数化

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = mysqlTx;
#pragma warning disable S2077 // 标识符白名单 + 值参数化
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- sql 由已验证标识符 + 参数占位符构造
        cmd.CommandText = sql;
#pragma warning restore S2077
        for (var r = 0; r < items.Count; r++)
        {
            var values = extractor(items[r]);
            for (var c = 0; c < cols.Length; c++)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = $"@r{r}c{c}";
                // P2（八轮评审）同款显式转换：Ulid→string / DateTimeOffset→UtcDateTime /
                // decimal→Invariant 字符串——镜像 MySqlBulkAsync 的 DataTable 填充口径
                param.Value = ConvertForMySql(values[c]) ?? DBNull.Value;
                cmd.Parameters.Add(param);
            }
        }

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // 行数守卫（镜像 ITM-197 语义）：非全量写入显式暴露而非静默成功
        if (affected != items.Count)
            throw new InvalidOperationException(
                $"MySQL 多行 VALUES 仅插入 {affected}/{items.Count} 行（非全量，可能含被忽略/重复键行）。");
        return affected;
    }

    /// <summary>
    /// P2 修复（八轮评审）：Ulid/DateTimeOffset 转 MySQL 原生可映射类型——
    /// DataTable 对未知类型静默 ToString() 是区域性依赖的静默损坏（本地化时间分隔符/
    /// DateTimeOffset 表示漂移），与 ConvertForNpgsql 对称显式转换：
    /// Ulid→string（CHAR(26) 文本列，26 字符 Ulid；v53 勘正：原写 char(36) 是 GUID 长度），DateTimeOffset→UtcDateTime（DATETIME(6) 原生支持，
    /// 统一 UTC 语义与 DapperAotInitializer.ToMySqlParameter 一致）。
    /// </summary>
    private static object? ConvertForMySql(object? val)
        => val switch
        {
            ByteAether.Ulid.Ulid ulid => ulid.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.UtcDateTime,
            // ITM-166 修复：decimal 显式转 InvariantCulture 字符串——DataTable 列全 string 时，
            // 直接赋 decimal 由 DataTable 按当前区域设置 ToString（如 de-DE 产出 "1,5"），
            // MySqlBulkCopy 把字符串按原样写入 DECIMAL 列时静默损坏或报错。Invariant 字符串
            // （"1.5"）由 MySQL 服务端按数值解析，与区域设置无关。
            decimal dec => dec.ToString(CultureInfo.InvariantCulture),
            _ => val,
        };

    // ─────────── SQLite 事务批量 INSERT ───────────

    /// <summary>
    /// SQLite 事务批量 INSERT。<br/>
    /// 💡 SQLite 不支持 COPY 或 BulkCopy，但可以通过"事务 + 参数复用"大幅提速。<br/>
    /// ⚡ 关键优化：
    ///   <br/>1. 所有 INSERT 在一个事务中——避免每次 fsync
    ///   <br/>2. 复用 DbCommand 和 DbParameter——避免重复创建对象
    /// </summary>
    private static async Task<int> SqliteBatchAsync<T>(
        DbConnection conn, string table, string[] cols,
        IReadOnlyList<T> items, Func<T, object?[]> extractor, DbTransaction? transaction, CancellationToken ct)
    {
        // 构建参数化 INSERT SQL：INSERT INTO t (c1,c2) VALUES (@c1,@c2)
        var placeholders = string.Join(", ", cols.Select(c => $"@{c}"));
        var colList = string.Join(", ", cols);
        var sql = $"INSERT INTO {table} ({colList}) VALUES ({placeholders})";

        // 📦 开启事务 — SQLite 默认每条 INSERT 都会 fsync，事务中只 fsync 一次
        // 三十八轮 P2 修复：传入外部事务时挂接该事务且不 Commit/Dispose（所有权归调用方）；
        // 未传时保持自建本地事务（原行为）。嵌套场景 Microsoft.Data.Sqlite 6+ 转 SAVEPOINT。
        // v35 P3：外部事务类型校验（镜像 MySQL 分支 MySqlBulkAsync 的 MySqlTransaction
        // fail-fast 形态）——传入非 SqliteTransaction 的外部事务对象（如 Npgsql/MySql 事务）
        // 原实现直接挂接 DbCommand.Transaction，执行时才在 SQLite 驱动深处报晦涩错误；
        // 提前 fail-fast 给出明确类型要求。自建事务路径（transaction is null）不受影响。
        if (transaction is not null and not SqliteTransaction)
            throw new ArgumentException(
                $"SQLite 批量导入要求事务为 SqliteTransaction，实际为 {transaction.GetType().Name}。",
                nameof(transaction));
        DbTransaction? localTx = null;
        if (transaction is null)
            localTx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // 标识符白名单 + 值参数化
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- sql 由已验证标识符 + 参数占位符构造
            cmd.CommandText = sql;
#pragma warning restore S2077
            cmd.Transaction = transaction ?? localTx;

            // 🔄 复用参数 — 每次循环只改 Value，不重新 CreateParameter
            var parameters = cols.Select(c =>
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@{c}";
                return p;
            }).ToArray();
            foreach (var p in parameters) cmd.Parameters.Add(p);

            int count = 0;
            foreach (var item in items)
            {
                var values = extractor(item);
                for (int i = 0; i < cols.Length; i++)
                    parameters[i].Value = values[i] switch
                    {
                        PalUlid ulid => DapperAotInitializer.ToSqliteParameter(ulid),
                        Guid guid => DapperAotInitializer.ToSqliteParameter(guid),
                        DateTimeOffset dto => DapperAotInitializer.ToSqliteParameter(dto),
                        _ => values[i] ?? DBNull.Value
                    };
                count += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 仅自建事务需要本地 Commit——外部事务所有权归调用方
            if (localTx is not null)
                await localTx.CommitAsync(ct).ConfigureAwait(false);
            return count;
        }
        finally
        {
            // 自建事务的释放归本方法；外部事务不由本方法 Dispose
            if (localTx is not null)
                await localTx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateColumns(string[] columns)
    {
        ArgumentOutOfRangeException.ThrowIfZero(columns.Length, nameof(columns));
        // 2026-10-21 评审（F15 姊妹）：原 DataTable 在 Columns.Add 处替我们拦重复列名，
        // 换成 BulkValueReader 后该保护消失——["id","id"] 会延迟到服务端才失败。
        var seen = new HashSet<string>(columns.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            ValidateIdentifier(column, nameof(columns), allowDot: false);
            if (!seen.Add(column))
                throw new ArgumentException(
                    $"columns 含重复列名 '{column}'：批量插入的列映射必须一一对应。", nameof(columns));
        }
    }

    private static void ValidateIdentifier(string identifier, string paramName, bool allowDot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier, paramName);

        var start = 0;
        while (start < identifier.Length)
        {
            var end = allowDot ? identifier.IndexOf('.', start) : -1;
            if (end < 0) end = identifier.Length;
            ValidateIdentifierPart(identifier.AsSpan(start, end - start), paramName);
            start = end + 1;
        }

        if (identifier[^1] == '.')
            throw new ArgumentException("SQL identifier cannot end with a dot.", paramName);
    }

    private static void ValidateIdentifierPart(ReadOnlySpan<char> part, string paramName)
    {
        if (part.IsEmpty || !IsIdentifierStart(part[0]))
            throw new ArgumentException("SQL identifier must start with a letter or underscore.", paramName);

        for (var i = 1; i < part.Length; i++)
            if (!IsIdentifierPart(part[i]))
                throw new ArgumentException("SQL identifier can only contain letters, digits, or underscores.", paramName);
    }

    private static bool IsIdentifierStart(char c)
        => c is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char c)
        => IsIdentifierStart(c) || c is >= '0' and <= '9';

    /// <summary>
    /// MySQL BulkCopy 的轻量行读取器（2026-10-21）：替代 DataTable。原始行数组与列类型在
    /// 入口一次性准备，逐值方言转换在 <see cref="GetValue"/> 执行；不再构造 DataTable 的
    /// 列元数据、DataRow 对象与整表行集合。MySqlBulkCopy 的 IDataReader 重载按
    /// GetFieldType/GetValue 取值。
    /// </summary>
    private sealed class BulkValueReader(
        IReadOnlyList<object?[]> rows,
        string[] columns,
        Type[] columnTypes,
        Func<object?, object?> convert) : DbDataReader
    {
        private int _rowIndex = -1;

        public override int FieldCount => columns.Length;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;

        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read() => ++_rowIndex < rows.Count;

        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Read());

        public override bool NextResult() => false;

        public override string GetName(int ordinal) => columns[ordinal];

        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (string.Equals(columns[i], name, StringComparison.Ordinal))
                    return i;
            }
            throw new ArgumentException($"Column '{name}' not found.", nameof(name));
        }

        [return: DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
        [UnconditionalSuppressMessage("Trimming", "IL2063",
            Justification = "列类型由运行时值推断（与 DataTable 路径同源），仅用于 MySqlBulkCopy 的 provider 类型映射；类型已物化，不需要裁剪保留成员。")]
        public override Type GetFieldType(int ordinal) => columnTypes[ordinal];
        public override string GetDataTypeName(int ordinal) => columnTypes[ordinal].Name;

        public override object GetValue(int ordinal)
            => convert(rows[_rowIndex][ordinal]) ?? DBNull.Value;

        public override int GetValues(object[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++)
                values[i] = GetValue(i);
            return count;
        }

        public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
        public override T GetFieldValue<T>(int ordinal) => (T)GetValue(ordinal);

        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            var bytes = (byte[])GetValue(ordinal);
            if (buffer is null)
                return bytes.Length;
            var offset = checked((int)dataOffset);
            var count = Math.Min(length, bytes.Length - offset);
            Array.Copy(bytes, offset, buffer, bufferOffset, count);
            return count;
        }

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            var chars = ((string)GetValue(ordinal)).ToCharArray();
            if (buffer is null)
                return chars.Length;
            var offset = checked((int)dataOffset);
            var count = Math.Min(length, chars.Length - offset);
            Array.Copy(chars, offset, buffer, bufferOffset, count);
            return count;
        }

        public override Stream GetStream(int ordinal)
            => new MemoryStream((byte[])GetValue(ordinal), writable: false);

        public override TextReader GetTextReader(int ordinal)
            => new StringReader((string)GetValue(ordinal));

        public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

        public override void Close()
        {
        }
    }

    /// <summary>确保连接已打开（幂等操作）</summary>
    /// <remarks>P3 修复（二十一轮）：EnsureOpen 同步版改异步——OpenAsync(ct) 传导取消令牌，
    /// 避免同步 Open 阻塞与取消延迟生效。</remarks>
    private static async Task EnsureOpenAsync(DbConnection conn, CancellationToken ct)
    {
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);
    }
}
