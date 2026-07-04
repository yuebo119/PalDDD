// ─────────────────────────────────────────────────────────────
// 📊 PostgreSqlReportHelper — 报表/BI 流式查询（零反射，AOT 安全）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ params NpgsqlParameter[] — 显式传参，零反射，AOT 安全。
//   ✅ SearchValues<char> — 编译时优化字符查找（.NET 8+）。
//   ✅ NpgsqlDataReader 流式读取 — O(1) 内存。
//
// 使用方式：
//   await PostgreSqlReportHelper.ExportCsvAsync(
//       dataSource,
//       "SELECT * FROM events WHERE recorded_at > @since",
//       [new("since", DateTime.UtcNow.AddDays(-7))],
//       "report.csv");
// ─────────────────────────────────────────────────────────────

using Npgsql;
using System.Buffers;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>PostgreSQL 报表/BI 流式查询工具（零反射，AOT 就绪）</summary>
public static class PostgreSqlReportHelper
{
    // 预编译 SearchValues — CSV 转义检测零分配
    private static readonly SearchValues<char> s_csvSpecial = SearchValues.Create(",\"\n\r");

    // ── CSV 导出 ──

    /// <summary>流式导出 CSV（支持百万级行，O(1) 内存）</summary>
    public static async Task<long> ExportCsvAsync(
        NpgsqlDataSource dataSource,
        string sql,
        NpgsqlParameter[] parameters,
        string outputPath,
        CancellationToken ct = default)
    {
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, ct);
        await using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);

        // 写入 CSV 头
        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);
        await writer.WriteLineAsync(string.Join(',', columns.Select(EscapeCsvSpan))).ConfigureAwait(false);

        // 流式写入数据行
        long rowCount = 0;
        var values = new object?[columns.Length];
        while (await reader.ReadAsync(ct))
        {
            reader.GetValues(values);
            for (int i = 0; i < values.Length; i++)
                values[i] = values[i] is DBNull ? null : values[i];

            var line = string.Join(',',
                values.Select(v => v is null ? "" : EscapeCsvSpan(Convert.ToString(v, CultureInfo.InvariantCulture).AsSpan())));
            await writer.WriteLineAsync(line).ConfigureAwait(false);

            if (++rowCount % 100_000 == 0)
                await writer.FlushAsync().ConfigureAwait(false);
        }

        await writer.FlushAsync().ConfigureAwait(false);
        return rowCount;
    }

    // ── JSON Lines 导出 ──

    /// <summary>流式导出 JSON Lines（每行一个 JSON 对象）</summary>
    public static async Task<long> ExportJsonLinesAsync(
        NpgsqlDataSource dataSource,
        string sql,
        NpgsqlParameter[] parameters,
        string outputPath,
        CancellationToken ct = default)
    {
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, ct);
        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        await using var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);

        long rowCount = 0;
        var values = new object?[columns.Length];
        while (await reader.ReadAsync(ct))
        {
            reader.GetValues(values);

            jsonWriter.WriteStartObject();
            for (int i = 0; i < columns.Length; i++)
            {
                var val = values[i];
                if (val is DBNull or null) continue;

                jsonWriter.WritePropertyName(columns[i]);
                WriteJsonValue(jsonWriter, val);
            }
            jsonWriter.WriteEndObject();

            if (++rowCount % 100_000 == 0)
                await jsonWriter.FlushAsync(ct).ConfigureAwait(false);
        }

        await jsonWriter.FlushAsync(ct).ConfigureAwait(false);
        return rowCount;
    }

    // ── 流式聚合 ──

    /// <summary>流式读取并逐行处理（任意自定义逻辑，O(1) 内存）</summary>
    public static async Task<long> StreamProcessAsync(
        NpgsqlDataSource dataSource,
        string sql,
        NpgsqlParameter[] parameters,
        Func<NpgsqlDataReader, CancellationToken, ValueTask<bool>> rowHandler,
        CancellationToken ct = default)
    {
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, ct);

        long rowCount = 0;
        while (await reader.ReadAsync(ct))
        {
            if (!await rowHandler(reader, ct))
                break;
            rowCount++;
        }

        return rowCount;
    }

    // ── PostgreSQL COPY 导出（最快，二进制格式）──

    /// <summary>使用 COPY TO STDOUT 导出 CSV（最快方式）</summary>
    public static async Task<long> CopyToCsvAsync(
        NpgsqlDataSource dataSource,
        string tableOrQuery,
        string outputPath,
        CancellationToken ct = default)
    {
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);
        await using var reader = await conn.BeginTextExportAsync(
            $"COPY ({tableOrQuery}) TO STDOUT WITH (FORMAT CSV, HEADER)", ct).ConfigureAwait(false);

        // Npgsql 10.x: 使用流式复制替代 CopyToAsync
        var buffer = new char[8192];
        int charsRead;
        while ((charsRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            await writer.WriteAsync(buffer, 0, charsRead).ConfigureAwait(false);

        return 0; // COPY TO 不返回行数
    }

    // ── 辅助方法 ──

    private static void WriteJsonValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string s: writer.WriteStringValue(s); break;
            case long l: writer.WriteNumberValue(l); break;
            case int i: writer.WriteNumberValue(i); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal m: writer.WriteNumberValue(m); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case DateTime dt: writer.WriteStringValue(dt.ToString("O")); break;
            case DateTimeOffset dto: writer.WriteStringValue(dto.ToString("O")); break;
            case Guid g: writer.WriteStringValue(g.ToString("D")); break;
            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
        }
    }

    /// <summary>CSV 转义 — SearchValues 零分配检测</summary>
    private static string EscapeCsvSpan(ReadOnlySpan<char> value)
    {
        if (value.ContainsAny(s_csvSpecial))
            return $"\"{value.ToString().Replace("\"", "\"\"")}\"";
        return value.ToString();
    }

    /// <summary>CSV 转义（字符串重载）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string EscapeCsvSpan(string value)
        => EscapeCsvSpan(value.AsSpan());
}
