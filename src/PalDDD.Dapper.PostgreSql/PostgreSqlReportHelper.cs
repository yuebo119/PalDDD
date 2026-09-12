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
/// <remarks>
/// ⚠️ <b>P3 声明（同步文件句柄边界，不动行为）</b>：三个导出方法（ExportCsvAsync/
/// ExportJsonLinesAsync/CopyToCsvAsync）的输出句柄均为<b>同步构造</b>（StreamWriter/
/// FileStream 直构，无 useAsync: true）——读取流（Npgsql 流式）为真异步，但落盘写路径
/// 走同步句柄的异步包装（线程池绑定），高并发导出下存在句柄线程占用；报表/BI 为低频
/// 运维路径（非热路径），按现有形态保留。ExportJsonLinesAsync 的逐行 jsonWriter.Flush()
/// 为同步 flush（ITM-215 要求逐行 Reset 的伴生约束），百万行导出有同步落盘等待。
/// </remarks>
public static class PostgreSqlReportHelper
{
    // 预编译 SearchValues — CSV 转义检测零分配
    private static readonly SearchValues<char> s_csvSpecial = SearchValues.Create(",\"\n\r");

    // v27 P3（B 片 N6）：JSONL 行分隔符缓存——原每行 "\n"u8.ToArray() 分配一个新 byte[1]，
    // 百万行导出即百万次短命分配（GC gen0 压力线性放大）；静态只读字段一次分配全程复用
    private static readonly byte[] s_jsonLinesNewline = "\n"u8.ToArray();

    // ── CSV 导出 ──

    /// <summary>流式导出 CSV（支持百万级行，O(1) 内存）</summary>
    public static async Task<long> ExportCsvAsync(
        NpgsqlDataSource dataSource,
        string sql,
        NpgsqlParameter[] parameters,
        string outputPath,
        CancellationToken ct = default)
    {
        // ITM-167 修复：补 null/空白守卫——缺守卫时 dataSource.CreateConnection()
        // 或 new NpgsqlCommand(null) 的失败点远离本入口，参数错误被 provider 异常掩盖。
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.Default, ct).ConfigureAwait(false);
        await using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);

        // 写入 CSV 头
        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);
        await writer.WriteLineAsync(string.Join(',', columns.Select(EscapeCsvSpan))).ConfigureAwait(false);

        // 流式写入数据行
        long rowCount = 0;
        var values = new object?[columns.Length];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            reader.GetValues(values);
            // v66 P4 声明（DBNull 分叉，与 JSONL 路径契约不同）：CSV 把 DBNull 归一为 null
            // 后经下方 `v is null ? ""` 输出空单元格——DB NULL 与零长字符串在 CSV 输出中
            // 不可区分（CSV 无 null 字面量）；JSONL 侧（ExportJsonLinesAsync）DBNull 是
            // 整字段缺失（非 JSON null），消费端按"字段不存在"处理。
            for (int i = 0; i < values.Length; i++)
                values[i] = values[i] is DBNull ? null : values[i];

            // ITM-114 修复：按类型原生格式化（FormatCsvValue）——原 Convert.ToString 把
            // byte[] 降级为 "System.Byte[]"、DateTime 丢时区/亚秒精度、float 精度失真
            var line = string.Join(',',
                values.Select(v => v is null ? "" : EscapeCsvSpan(FormatCsvValue(v).AsSpan())));
            await writer.WriteLineAsync(line).ConfigureAwait(false);

            // v35 P3：FlushAsync 补传 ct（镜像同文件 ExportJsonLinesAsync 收尾 FlushAsync(ct)
            // 形态）——原无参调用使取消信号无法传导到刷盘等待，长导出中段/收尾取消要等
            // 当前 Flush 完成才响应
            if (++rowCount % 100_000 == 0)
                await writer.FlushAsync(ct).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
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
        // ITM-167 修复：补 null/空白守卫（同 ExportCsvAsync）。
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.Default, ct).ConfigureAwait(false);
        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        await using var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);

        long rowCount = 0;
        var values = new object?[columns.Length];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            reader.GetValues(values);

            // ITM-215 修复：JSONL 每行一个根对象——同一 writer 连续 WriteStartObject 第二次必抛
            // InvalidOperationException。Flush + 写换行 + Reset 允许下一行作为新根对象。
            jsonWriter.WriteStartObject();
            for (int i = 0; i < columns.Length; i++)
            {
                var val = values[i];
                // v66 P4 声明（DBNull 分叉，与 CSV 路径契约不同）：NULL 列整字段跳过
                //（不写 JSON null）——消费端按"字段缺失"而非显式 null 解析；如需
                // 显式 null 语义应改 WriteNullValue（行为变更，需下游协商）。CSV 侧
                //（ExportCsvAsync）NULL 输出为空单元格。
                if (val is DBNull or null) continue;

                jsonWriter.WritePropertyName(columns[i]);
                WriteJsonValue(jsonWriter, val);
            }
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            await stream.WriteAsync(s_jsonLinesNewline, ct).ConfigureAwait(false); // v21 B-3 勘误放弃：u8 是 ReadOnlySpan，WriteAsync 收 ReadOnlyMemory 无隐式转换；v27 P3（B 片 N6）经静态缓存字段消除每行分配
            jsonWriter.Reset();
            rowCount++;
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
        // ITM-167 修复：补 null/空白守卫（同 ExportCsvAsync；rowHandler 为调用方委托）。
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(rowHandler);

        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.Default, ct).ConfigureAwait(false);

        long rowCount = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!await rowHandler(reader, ct).ConfigureAwait(false))
                break;
            rowCount++;
        }

        return rowCount;
    }

    // ── PostgreSQL COPY 导出（最快，二进制格式）──

    /// <summary>使用 COPY TO STDOUT 导出 CSV（最快方式）</summary>
    /// <param name="tableOrQuery">表名或 SELECT 查询。⚠️ 直接插入 COPY 语句——必须为编译期常量或受信任来源，禁止传入用户输入（COPY 语法要求完整 SQL，无法参数化）。</param>
    /// <returns>恒返回 0——COPY TO 协议只回传字节流不回传行数（与 <see cref="ExportCsvAsync"/>/
    /// <see cref="ExportJsonLinesAsync"/> 逐行计数的 long 返回契约不同）；需要行数的场景
    /// 改用 ExportCsvAsync 或先 <c>SELECT count(*)</c>。</returns>
    /// <remarks>
    /// ⚠️ <b>CSV 公式注入无防护（ITM-212 声明·三十二轮）</b>：本方法走服务器端
    /// <c>COPY (...) TO STDOUT</c> 原样转储，<b>不经过</b> <see cref="EscapeCsvSpan(System.ReadOnlySpan{char})"/> 的
    /// OWASP 公式前缀防护（= + - @ \t，见 ITM-201）——服务器流式输出无法在传输中改写单元格。
    /// 导出含操作者可控字段（outbox error / audit reason 等）时，输出在 Excel 打开可触发
    /// 公式执行；此类场景<b>必须改用</b> <see cref="ExportCsvAsync"/>（客户端逐单元格转义）。
    /// 仅导出受信任/系统字段（时间戳、计数、固定枚举）时可安全使用本方法。
    /// </remarks>
    public static async Task<long> CopyToCsvAsync(
        NpgsqlDataSource dataSource,
        string tableOrQuery,
        string outputPath,
        CancellationToken ct = default)
    {
        // ITM-167 修复：补 null/空白守卫（同 ExportCsvAsync）。
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableOrQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // ITM-167 声明（COPY 受信边界）：tableOrQuery 直接插入 COPY 语句，无法参数化——
        // 必须为编译期常量或受信任来源；用户输入必须先经白名单校验，禁止传入本方法。
        await using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);
        await using var reader = await conn.BeginTextExportAsync(
            $"COPY ({tableOrQuery}) TO STDOUT WITH (FORMAT CSV, HEADER)", ct).ConfigureAwait(false);

        // Npgsql 10.x: 使用流式复制替代 CopyToAsync
        var buffer = new char[8192];
        int charsRead;
        // v26 P3 H6：补 ct 传导（同文件其余方法均传 ct）——TextReader/StreamWriter 的
        // (char[],int,int) 重载不带 CancellationToken，改用 Memory 重载透传 ct，
        // 取消延迟从"整个拷贝完成"收敛到"当前块 IO 完成"。
        while ((charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            await writer.WriteAsync(buffer.AsMemory(0, charsRead), ct).ConfigureAwait(false);

        // ITM-167 修复：StreamWriter 改 await using + 显式异步 Flush——
        // 原同步 using/Dispose 在释放时同步 flush 阻塞线程（同步释放路径）。
        // v36 P3：收尾 FlushAsync 补传 ct——v35 FlushAsync 修复的姊妹漏网（同文件
        // ExportCsvAsync :89/:92 与 ExportJsonLinesAsync :151 均已传 ct），原无参调用
        // 使取消信号无法传导到收尾刷盘等待
        await writer.FlushAsync(ct).ConfigureAwait(false);
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
            // ITM-114 修复：byte[] → Base64（原 default 分支输出 "System.Byte[]"）；
            // float 单独处理（float 装箱不匹配 double 分支，原落入 default 变字符串）
            case byte[] bytes: writer.WriteStringValue(Convert.ToBase64String(bytes)); break;
            case float f: writer.WriteNumberValue(f); break;
            // ITM-189 修复（二十九轮）：其余整数族补数值分支——uint/ulong/short/ushort/
            // byte/sbyte 装箱不匹配 long/int（值类型装箱类型敏感），原落 default 被
            // Convert.ToString 写成 JSON 字符串，消费端按 number 反序列化失败/失真。
            case uint ui: writer.WriteNumberValue(ui); break;
            case ulong ul: writer.WriteNumberValue(ul); break;
            case short sh: writer.WriteNumberValue(sh); break;
            case ushort us: writer.WriteNumberValue(us); break;
            case byte by: writer.WriteNumberValue(by); break;
            case sbyte sb: writer.WriteNumberValue(sb); break;
            // ITM-641 修复：DateOnly/TimeOnly 未覆盖时落入 default 的 Convert.ToString——
            // 区域性相关输出（非 ISO），消费端解析失真。PG date/time 的 Npgsql 默认映射即
            // DateOnly/TimeOnly，显式走 InvariantCulture 定长格式（对齐 DateTime "O" 分支）。
            case DateOnly dateOnly: writer.WriteStringValue(dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case TimeOnly timeOnly: writer.WriteStringValue(timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)); break;
            // ITM-641 修复：数组（PG text[] 等，Npgsql 映射为 CLR 数组/集合）原落 default 输出
            // "System.String[]"；此处写 JSON 数组，元素递归走本方法（string/byte[] 已在前面
            // 分支优先匹配，故 IEnumerable 分支仅命中真正的集合类型）。
            case System.Collections.IEnumerable seq:
                writer.WriteStartArray();
                foreach (var item in seq)
                {
                    if (item is null or DBNull)
                        writer.WriteNullValue();
                    else
                        WriteJsonValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
        }
    }

    /// <summary>
    /// ITM-114 修复：按 CLR 类型原生格式化 CSV 单元格——原 Convert.ToString 把 byte[]
    /// 降级为 "System.Byte[]"、DateTime 丢时区/亚秒精度、float/double 精度失真。
    /// 与 WriteJsonValue 的映射规则对齐（byte[] → Base64；时间 "O"；Guid "D"）。
    /// </summary>
    private static string FormatCsvValue(object value)
    {
        return value switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D", CultureInfo.InvariantCulture),
            // ITM-641 修复：DateOnly/TimeOnly 原落 default 的 Convert.ToString——区域性相关；
            // PG date/time 的 Npgsql 默认映射即此二者，显式 InvariantCulture 定长格式。
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            // ITM-641 修复：数组（PG text[] 等）原落 default 输出 "System.String[]"；
            // 逐元素递归格式化后以逗号连接（含逗号的单元格会被 EscapeCsvSpan 整格引用，
            // 列边界不破）。string 同为 IEnumerable 必须排除，否则被逐字符拆分；
            // byte[] 已在前面分支优先匹配。
            System.Collections.IEnumerable seq when value is not string => string.Join(',',
                seq.Cast<object?>().Select(e => e is null or DBNull ? "" : FormatCsvValue(e))),
            float f => f.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }

    /// <summary>CSV 转义 — SearchValues 零分配检测 + CSV 公式注入防护</summary>
    internal static string EscapeCsvSpan(ReadOnlySpan<char> value)
    {
        // ITM-201 修复（三十一轮）：CSV 公式注入防护（OWASP CSV Injection）——以
        // = + - @ \t 开头的单元格在 Excel 打开时会被当作公式执行；前置单引号
        // 使 Excel 按文本解释。reason/actor_id 等操作者输入字段经报表导出可达此路径。
        // ITM-215 修复（三十二轮）：公式前缀的单引号必须放在 CSV 双引号内部——
        // 原实现放在双引号外部会破坏列边界（'=1,=HYPERLINK 被解析为两列）。
        if (!value.IsEmpty && IsCsvFormulaPrefix(value[0]))
        {
            var text = value.ToString();
            return value.ContainsAny(s_csvSpecial)
                ? "\"'" + text.Replace("\"", "\"\"") + "\""
                : "'" + text;
        }

        if (value.ContainsAny(s_csvSpecial))
            return $"\"{value.ToString().Replace("\"", "\"\"")}\"";
        return value.ToString();
    }

    /// <summary>CSV 公式注入前缀集合（OWASP：= + - @ Tab）</summary>
    private static bool IsCsvFormulaPrefix(char c)
        => c is '=' or '+' or '-' or '@' or '\t';

    /// <summary>CSV 转义（字符串重载）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string EscapeCsvSpan(string value)
        => EscapeCsvSpan(value.AsSpan());
}
