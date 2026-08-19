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
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Globalization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Dapper;

/// <summary>
/// 数据库原生批量导入 — 按数据库类型自动选择最优路径。<br/>
/// 支持 PostgreSQL COPY、MySQL MySqlBulkCopy、SQLite 事务批处理。
/// </summary>
public static class DapperBulkCopy
{
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
    /// P2 修复（八轮评审，配套批量追踪列）：元素类型放宽为可空——null 由各方言路径归一为 SQL NULL
    /// （SQLite/MySQL 显式 ?? DBNull.Value，PG COPY 走 NpgsqlDbType.Unknown）；Func 协变保证
    /// 既有 object[] 返回的 lambda 兼容。</param>
    /// <returns>成功插入的行数</returns>
    /// <exception cref="NotSupportedException">不支持的数据库类型</exception>
    public static async ValueTask<int> BulkInsertAsync<T>(
        DbConnection conn,
        DapperDbType dbType,
        string tableName,
        string[] columns,
        IReadOnlyList<T> items,
        Func<T, object?[]> valueExtractor,
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
        var probe = valueExtractor(items[0]);
        if (probe.Length != columns.Length)
            throw new ArgumentException(
                $"valueExtractor 返回 {probe.Length} 个值，但 columns 有 {columns.Length} 列。",
                nameof(valueExtractor));

        // 💡 switch 表达式按 DapperDbType 枚举分发 — 编译时已知值，零反射
        return dbType switch
        {
            DapperDbType.PostgreSql => await PgCopyAsync(conn, tableName, columns, items, valueExtractor, ct).ConfigureAwait(false),
            DapperDbType.MySql => await MySqlBulkAsync(conn, tableName, columns, items, valueExtractor, ct).ConfigureAwait(false),
            DapperDbType.Sqlite => await SqliteBatchAsync(conn, tableName, columns, items, valueExtractor, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"数据库类型 {dbType} 不支持批量导入。")
        };
    }

    // ─────────── PostgreSQL COPY（BinaryImport）───────────

    /// <summary>
    /// PostgreSQL COPY 协议批量导入。<br/>
    /// 💡 COPY FROM STDIN (FORMAT BINARY) 直接写入 Socket，绕过 SQL 解析器。<br/>
    /// ⚡ 比逐行 INSERT 快约 100 倍（取决于网络延迟和数据量）。
    /// </summary>
    private static async Task<int> PgCopyAsync<T>(
        DbConnection conn, string table, string[] cols,
        IReadOnlyList<T> items, Func<T, object?[]> extractor, CancellationToken ct)
    {
        var pgConn = (NpgsqlConnection)conn;
        var colList = string.Join(", ", cols);
        var copySql = $"COPY {table} ({colList}) FROM STDIN (FORMAT BINARY)";

        // BeginBinaryImportAsync — Npgsql 10.x 标准 COPY API
        await using var writer = await pgConn.BeginBinaryImportAsync(copySql, ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            await writer.StartRowAsync().ConfigureAwait(false);      // 开始新行
            foreach (var val in extractor(item))
            {
                // P1 修复（七轮评审）：Ulid/DateTimeOffset 无 Npgsql 原生映射——
                // Unknown 类型写入 raw 对象时抛类型解析异常。与 SQLite 路径对称做类型转换。
                var converted = ConvertForNpgsql(val);
                await writer.WriteAsync(converted.value, converted.type).ConfigureAwait(false);
            }
        }

        // CompleteAsync — 发送 COPY 结束标记，返回成功写入的行数
        var rowsWritten = await writer.CompleteAsync().ConfigureAwait(false);
        return (int)rowsWritten;
    }

    /// <summary>
    /// P1 修复：Ulid/DateTimeOffset 转 Npgsql 原生类型——raw 对象 + Unknown
    /// 在 COPY BINARY 下无法推断类型。转换策略与 Dapper TypeHandler 一致。
    /// </summary>
    private static (object? value, NpgsqlTypes.NpgsqlDbType type) ConvertForNpgsql(object? val)
    {
        return val switch
        {
            null => (null, NpgsqlTypes.NpgsqlDbType.Unknown),
            ByteAether.Ulid.Ulid ulid => (ulid.ToString(), NpgsqlTypes.NpgsqlDbType.Text),
            DateTimeOffset dto => (dto, NpgsqlTypes.NpgsqlDbType.TimestampTz),
            _ => (val, NpgsqlTypes.NpgsqlDbType.Unknown),
        };
    }

    // ─────────── MySQL MySqlBulkCopy ───────────

    /// <summary>
    /// MySQL BulkCopy 批量导入。<br/>
    /// 💡 MySqlBulkCopy 使用 MySQL 原生的 LOAD DATA INFILE 协议，比逐行 INSERT 快约 10 倍。<br/>
    /// ⚡ 需要连接字符串包含 <c>AllowLoadLocalInfile=True</c>。<br/>
    /// 🛡️ 检查 <see cref="MySqlBulkCopyResult.Warnings"/> 防止静默数据截断。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Aot", "IL2062:RequiresDynamicallyAccessedMembers",
        Justification = "Dapper 适配层为非 AOT（IsAotCompatible=false）；DataTable 列类型按运行时值推断是 MySqlBulkCopy 唯一数据源格式。")]
    private static async Task<int> MySqlBulkAsync<T>(
        DbConnection conn, string table, string[] cols,
        IReadOnlyList<T> items, Func<T, object?[]> extractor, CancellationToken ct)
    {
        var myConn = (MySqlConnection)conn;

        // P3 修复（二十一轮）：AllowLoadLocalInfile 前提从注释升级为运行时检测——
        // MySqlBulkCopy 走 LOAD DATA LOCAL INFILE 协议，MySqlConnector 连接串缺
        // AllowLoadLocalInfile=true 时 WriteToServerAsync 抛晦涩的协议异常（或静默零行）。
        // 不自动开启（该选项涉及服务端 local_infile 权限面，须由调用方显式决策）。
        // 检测放在 MySQL 批量路径入口而非 AddPalMySqlDataSource DI 入口——前提仅批量路径
        // 需要，DI 入口抛异常会误伤不使用 bulk copy 的应用启动。
        if (!new MySqlConnectionStringBuilder(myConn.ConnectionString).AllowLoadLocalInfile)
            throw new InvalidOperationException(
                "MySQL 批量导入要求连接字符串包含 'AllowLoadLocalInfile=True'（MySqlBulkCopy 走 LOAD DATA LOCAL INFILE 协议）。" +
                "请在构造 MySqlConnection/MySqlDataSource 的连接串中显式添加该选项，并确认 MySQL 服务端 local_infile=1。" +
                "本库不自动开启该选项——它扩大服务端可访问的文件面，须由调用方显式决策。");

        // 构建 DataTable — MySqlBulkCopy 的唯一数据源格式
        // 注意：DataTable 在现代 .NET（net6.0+）中已 AOT 兼容
        // ITM-083 修复：DataTable 用 using 声明（成功/异常路径都释放）。
        // MySqlBulkCopy 经查证（MySqlConnector 2.6.x XML 文档）不实现 IDisposable——无 Dispose 可调，
        // 其内部连接生命周期由 myConn 持有者管理；此处仅 DataTable 需要释放。
        // ITM-214 修复（三十二轮）：按首行非空值推断列类型——默认 string 列把 byte[]
        // 静默 ToString() 为 "System.Byte[]"，二进制负载损坏。
        var columnTypes = new Type?[cols.Length];
        foreach (var item in items)
        {
            var sampleValues = extractor(item);
            for (int i = 0; i < cols.Length; i++)
            {
                if (columnTypes[i] is null && sampleValues[i] is not null)
                {
                    var converted = ConvertForMySql(sampleValues[i]);
                    columnTypes[i] = converted?.GetType();
                }
            }
            if (Array.TrueForAll(columnTypes, t => t is not null)) break;
        }

        using var dt = new DataTable();
        for (int i = 0; i < cols.Length; i++)
            dt.Columns.Add(cols[i], columnTypes[i] ?? typeof(object));

        foreach (var item in items)
        {
            var row = dt.NewRow();
            var values = extractor(item);
            // P2 修复（八轮评审）：套用 ConvertForMySql——DataTable 对未知类型（Ulid/DateTimeOffset）
            // 静默 ToString() 是区域性依赖的静默损坏（本地化时间分隔符/Ulid 表示漂移），显式转换消除。
            for (int i = 0; i < cols.Length; i++)
                row[i] = ConvertForMySql(values[i]) ?? DBNull.Value;   // null 值转为 DBNull（SQL NULL）
            dt.Rows.Add(row);
        }

        // MySqlBulkCopy 不实现 IDisposable（MySqlConnector 2.6.x）——无 using，见上方 ITM-083 注释
        var bulkCopy = new MySqlBulkCopy(myConn)
        {
            DestinationTableName = table
        };

        // 映射 DataTable 列 → 数据库列（按索引匹配）
        foreach (var col in cols)
            bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(Array.IndexOf(cols, col), col));

        // WriteToServerAsync — 真正执行批量导入
        var result = await bulkCopy.WriteToServerAsync(dt, ct).ConfigureAwait(false);

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
    /// P2 修复（八轮评审）：Ulid/DateTimeOffset 转 MySQL 原生可映射类型——
    /// DataTable 对未知类型静默 ToString() 是区域性依赖的静默损坏（本地化时间分隔符/
    /// DateTimeOffset 表示漂移），与 ConvertForNpgsql 对称显式转换：
    /// Ulid→string（char(36) 文本列），DateTimeOffset→UtcDateTime（DATETIME(6) 原生支持，
    /// 统一 UTC 语义与 DapperAotInitializer.ToMySqlParameter 一致）。
    /// </summary>
    private static object? ConvertForMySql(object? val)
        => val switch
        {
            ByteAether.Ulid.Ulid ulid => ulid.ToString(),
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
        IReadOnlyList<T> items, Func<T, object?[]> extractor, CancellationToken ct)
    {
        // 构建参数化 INSERT SQL：INSERT INTO t (c1,c2) VALUES (@c1,@c2)
        var placeholders = string.Join(", ", cols.Select(c => $"@{c}"));
        var colList = string.Join(", ", cols);
        var sql = $"INSERT INTO {table} ({colList}) VALUES ({placeholders})";

        // 📦 开启事务 — SQLite 默认每条 INSERT 都会 fsync，事务中只 fsync 一次
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;

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

        await tx.CommitAsync().ConfigureAwait(false);
        return count;
    }

    private static void ValidateColumns(string[] columns)
    {
        ArgumentOutOfRangeException.ThrowIfZero(columns.Length, nameof(columns));
        foreach (var column in columns)
            ValidateIdentifier(column, nameof(columns), allowDot: false);
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

    /// <summary>确保连接已打开（幂等操作）</summary>
    /// <remarks>P3 修复（二十一轮）：EnsureOpen 同步版改异步——OpenAsync(ct) 传导取消令牌，
    /// 避免同步 Open 阻塞与取消延迟生效。</remarks>
    private static async Task EnsureOpenAsync(DbConnection conn, CancellationToken ct)
    {
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);
    }
}
