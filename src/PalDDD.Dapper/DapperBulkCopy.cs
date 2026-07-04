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
//   ✅ Func<T, object[]> 委托模式 — 值提取由调用者 lambda 完成，零反射
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
    /// <param name="valueExtractor">每行值提取函数：item → object[]，调用者 lambda 完成，零反射</param>
    /// <returns>成功插入的行数</returns>
    /// <exception cref="NotSupportedException">不支持的数据库类型</exception>
    public static async ValueTask<int> BulkInsertAsync<T>(
        DbConnection conn,
        DapperDbType dbType,
        string tableName,
        string[] columns,
        IReadOnlyList<T> items,
        Func<T, object[]> valueExtractor)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(valueExtractor);
        ValidateIdentifier(tableName, nameof(tableName), allowDot: true);
        ValidateColumns(columns);

        if (items.Count == 0) return 0;

        EnsureOpen(conn);

        // 💡 switch 表达式按 DapperDbType 枚举分发 — 编译时已知值，零反射
        return dbType switch
        {
            DapperDbType.PostgreSql => await PgCopyAsync(conn, tableName, columns, items, valueExtractor).ConfigureAwait(false),
            DapperDbType.MySql => await MySqlBulkAsync(conn, tableName, columns, items, valueExtractor).ConfigureAwait(false),
            DapperDbType.Sqlite => await SqliteBatchAsync(conn, tableName, columns, items, valueExtractor).ConfigureAwait(false),
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
        IReadOnlyList<T> items, Func<T, object[]> extractor)
    {
        var pgConn = (NpgsqlConnection)conn;
        var colList = string.Join(", ", cols);
        var copySql = $"COPY {table} ({colList}) FROM STDIN (FORMAT BINARY)";

        // BeginBinaryImportAsync — Npgsql 10.x 标准 COPY API
        await using var writer = await pgConn.BeginBinaryImportAsync(copySql).ConfigureAwait(false);

        foreach (var item in items)
        {
            await writer.StartRowAsync().ConfigureAwait(false);      // 开始新行
            foreach (var val in extractor(item))
                await writer.WriteAsync(val, NpgsqlTypes.NpgsqlDbType.Unknown).ConfigureAwait(false); // 写入每个列值
        }

        // CompleteAsync — 发送 COPY 结束标记，返回成功写入的行数
        var rowsWritten = await writer.CompleteAsync().ConfigureAwait(false);
        return (int)rowsWritten;
    }

    // ─────────── MySQL MySqlBulkCopy ───────────

    /// <summary>
    /// MySQL BulkCopy 批量导入。<br/>
    /// 💡 MySqlBulkCopy 使用 MySQL 原生的 LOAD DATA INFILE 协议，比逐行 INSERT 快约 10 倍。<br/>
    /// ⚡ 需要连接字符串包含 <c>AllowLoadLocalInfile=True</c>。<br/>
    /// 🛡️ 检查 <see cref="MySqlBulkCopyResult.Warnings"/> 防止静默数据截断。
    /// </summary>
    private static async Task<int> MySqlBulkAsync<T>(
        DbConnection conn, string table, string[] cols,
        IReadOnlyList<T> items, Func<T, object[]> extractor)
    {
        var myConn = (MySqlConnection)conn;

        // 构建 DataTable — MySqlBulkCopy 的唯一数据源格式
        // 注意：DataTable 在现代 .NET（net6.0+）中已 AOT 兼容
        var dt = new DataTable();
        foreach (var col in cols) dt.Columns.Add(col);

        foreach (var item in items)
        {
            var row = dt.NewRow();
            var values = extractor(item);
            for (int i = 0; i < cols.Length; i++)
                row[i] = values[i] ?? DBNull.Value;   // null 值转为 DBNull（SQL NULL）
            dt.Rows.Add(row);
        }

        var bulkCopy = new MySqlBulkCopy(myConn)
        {
            DestinationTableName = table
        };

        // 映射 DataTable 列 → 数据库列（按索引匹配）
        foreach (var col in cols)
            bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(Array.IndexOf(cols, col), col));

        // WriteToServerAsync — 真正执行批量导入
        var result = await bulkCopy.WriteToServerAsync(dt).ConfigureAwait(false);

        // 🛡️ 检查 Warnings — MySQL BulkCopy 可能因类型不兼容而静默截断数据
        // 例如：字符串超过列长度被截断、数值溢出被强制转换
        if (result.Warnings.Count > 0)
        {
            var warnings = string.Join("; ", result.Warnings.Select(w => $"{w.Message} (level={w.Level})"));
            throw new InvalidOperationException(
                $"MySqlBulkCopy 完成但有 {result.Warnings.Count} 条警告（可能有数据截断）: {warnings}");
        }

        return result.RowsInserted;
    }

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
        IReadOnlyList<T> items, Func<T, object[]> extractor)
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
                parameters[i].Value = values[i] is PalUlid ulid ? ulid.ToString() : values[i] ?? DBNull.Value;
            count += await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
    private static void EnsureOpen(DbConnection conn)
    {
        if (conn.State != ConnectionState.Open)
            conn.Open();
    }
}
