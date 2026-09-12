// ---------------------------------------------------------------------------
// CI 失败自诊断（unified v2.0，2026-08-20；MIG-011a 自 Python 迁移，逻辑逐通道等价）
// 三通道把失败原因以 GitHub `::error` 控制台注解输出——GitHub 将其转为 check-run
// 注解，注解 API 公开可读，无需认证下载日志。
//
// 通道：① TUnit JSON 报告中的失败测试名；② 失败项目输出日志尾（关键字上下文 +
//           最后 ~30 行）；③ Verify 快照对（*.received.txt vs *.verified.txt）首个差异。
//
// 用法：dotnet run scripts/ci-failed-tests.cs <项目名词干> [日志文件路径]
// 退出码：0=未发现可诊断信息；1=发射过诊断（仅作标记，调用方用 || true 兜底）；
//         2=参数缺失。
//
// 说明：file-based app（dotnet run 直接运行），零 package 依赖——System.Text.Json
// 为框架内置，不写 #:package（NU1510 警告即错误）。
// ---------------------------------------------------------------------------
using System.Text;
using System.Text.Json;

// Windows 控制台默认编码非 UTF-8，中文与特殊字符注解会被改写——显式对齐
// Python 3 默认 UTF-8 输出，保证跨实现字节级可比
Console.OutputEncoding = Encoding.UTF8;

// 参数计数偏移：Python sys.argv[0] 是脚本名（无参数 = len<2、有日志 = len>2），
// C# args 不含程序名，对应映射为 <1 与 >1
if (args.Length < 1)
{
    Console.Error.WriteLine("用法: ci-failed-tests.cs <proj_stem> [log]");
    return 2;
}

int found = FromReports(args[0]);
if (args.Length > 1)
{
    found += FromLogTail(args[1]);
}
found += FromVerifySnapshots();
return found == 0 ? 0 : 1;

// ---------------------------------------------------------------- 通道 ① --

// 发射 GitHub ::error 注解：单条上限 250 字符，截断后把换行压平为 " | "。
// 与 Python 版等价：先截断（message[:250]）再替换换行（chr(10) -> " | "），\r 不替换。
static void Emit(string message)
{
    var clipped = message.Length <= 250 ? message : message[..250];
    Console.WriteLine($"::error ::{clipped.Replace("\n", " | ")}");
}

// 枚举 JSON 对象上指定名字的数组属性；缺失或非数组时返回空（等价 Python dict.get(name, [])）。
static IEnumerable<JsonElement> ArrayOrEmpty(JsonElement parent, string name) =>
    parent.ValueKind == JsonValueKind.Object
    && parent.TryGetProperty(name, out var value)
    && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray()
        : [];

// 读取字符串属性，缺失或非字符串时回退默认值（等价 Python dict.get(name, fallback)）。
static string StrOr(JsonElement parent, string name, string fallback) =>
    parent.TryGetProperty(name, out var value)
    && value.ValueKind == JsonValueKind.String
    && value.GetString() is { } text
        ? text
        : fallback;

// 取失败测试的 exception.message 前 180 字符；exception/message 缺失或为 null 时回退空串
// （等价 Python (exception.get("message") or "")[:180]）。
static string ExceptionMessage(JsonElement test)
{
    if (test.TryGetProperty("exception", out var exception)
        && exception.ValueKind == JsonValueKind.Object
        && exception.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.String)
    {
        var text = message.GetString() ?? "";
        return text.Length <= 180 ? text : text[..180];
    }
    return "";
}

// 通道①：遍历 TestResults/*.tunit-report.json，提取文件名含 stem 的报告中
// status=="failed" 的测试名与异常消息。返回发现数。
static int FromReports(string stem)
{
    int found = 0;
    if (!Directory.Exists("TestResults"))
    {
        return 0; // 目录不存在：glob 返回空，与 Python 一致
    }

    foreach (var report in Directory.EnumerateFiles("TestResults", "*.tunit-report.json"))
    {
        // 文件名子串匹配（区分大小写，等价 Python `stem not in report` 的 Ordinal 语义）
        if (!report.Contains(stem, StringComparison.Ordinal))
        {
            continue;
        }

        JsonDocument document;
        try
        {
            using var stream = File.OpenRead(report);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            continue; // 解析失败/不可读：跳过该报告（等价 Python except (OSError, ValueError)）
        }

        using (document)
        {
            foreach (var group in ArrayOrEmpty(document.RootElement, "groups"))
            foreach (var test in ArrayOrEmpty(group, "tests"))
            {
                if (StrOr(test, "status", "") != "failed")
                {
                    continue;
                }

                var name = $"{StrOr(test, "className", "?")}.{StrOr(test, "methodName", "?")}";
                Emit($"FAILED {name} — {ExceptionMessage(test)}");
                found++;
            }
        }
    }

    return found;
}

// ---------------------------------------------------------------- 通道 ② --

// 通道②：失败项目日志。2a=全日志关键字上下文（最后 15 个命中行及其后继行）；
// 2b=尾部窗口（最后 120 行中非空白的最后 30 行）。返回尾部行数。
static int FromLogTail(string logPath)
{
    string[] lines;
    try
    {
        // ReadAllLines 按 \r\n / \n / \r 分行且不产生尾部空行——与 Python
        // read().splitlines() 对真实 dotnet 输出的行为一致
        lines = File.ReadAllLines(logPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return 0; // 读不了：静默放弃（等价 Python except OSError: return 0）
    }

    // 通道 2a：全日志关键字上下文（异常/失败/超时/退出码——不受尾部窗口限制）
    string[] keys = ["exception", "failed", "error(s)", "timeout", "timed out", "killed", "exit code", "fatal"];
    var hits = new List<(int Index, string Line)>();
    for (var i = 0; i < lines.Length; i++)
    {
        if (keys.Any(key => lines[i].Contains(key, StringComparison.OrdinalIgnoreCase)))
        {
            hits.Add((i, lines[i]));
        }
    }

    foreach (var (index, line) in hits.Skip(Math.Max(0, hits.Count - 15))) // 只取最后 15 个命中
    {
        Emit($"CTX| {line}");
        if (index + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[index + 1]))
        {
            Emit($"CTX+1| {lines[index + 1]}");
        }
    }

    // 通道 2b：尾部窗口——最后 120 行里取非空白的最后 30 行
    var tail = lines.Skip(Math.Max(0, lines.Length - 120))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(30)
                    .ToList();
    foreach (var line in tail)
    {
        Emit($"LOG| {line}");
    }

    return tail.Count;
}

// ---------------------------------------------------------------- 通道 ③ --

// 通道③：Verify 快照对（test/**/*.received.txt vs *.verified.txt）的首个差异行
// （含行号与两侧前 100 字符），或行数不等提示。返回发现数。
static int FromVerifySnapshots()
{
    int found = 0;
    if (!Directory.Exists("test"))
    {
        return 0;
    }

    foreach (var received in Directory.EnumerateFiles("test", "*.received.txt", SearchOption.AllDirectories))
    {
        var verified = received.Replace(".received.txt", ".verified.txt");

        string[] got, want;
        try
        {
            got = File.ReadAllLines(received);
            want = File.ReadAllLines(verified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            continue; // received/verified 任一不可读（含 verified 不存在）：跳过
        }

        // zip 语义：只比较到较短一方的行数；首个差异即发射并跳出（等价 Python for...break）
        var mismatchFound = false;
        var zipLength = Math.Min(got.Length, want.Length);
        for (var i = 0; i < zipLength; i++)
        {
            if (got[i] != want[i])
            {
                Emit($"SNAPSHOT {received}:{i + 1} received={PyRepr(Truncate(got[i]))} verified={PyRepr(Truncate(want[i]))}");
                found++;
                mismatchFound = true;
                break;
            }
        }

        // for-else 语义：zip 内无差异但行数不等 → 行数提示
        if (!mismatchFound && got.Length != want.Length)
        {
            Emit($"SNAPSHOT {received} 行数 received={got.Length} verified={want.Length}");
            found++;
        }
    }

    return found;
}

// 截断到 100 字符（等价 Python 切片 g[:100]——按字符计数，不足取全部）。
static string Truncate(string text) => text.Length <= 100 ? text : text[..100];

// 模拟 Python repr(str) 输出（SNAPSHOT 行与原实现逐字段一致的关键）：
// 包裹符默认单引号；字符串含 ' 且不含 " 时改用双引号；反斜杠与包裹符本身转义；
// \n \r \t 用命名转义，其余控制字符（<0x20、0x7f）用 \xNN 小写十六进制；
// 非 ASCII 字符原样保留。
static string PyRepr(string text)
{
    var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
    var builder = new StringBuilder(text.Length + 2);
    builder.Append(quote);
    foreach (var ch in text)
    {
        switch (ch)
        {
            case '\\':
                builder.Append("\\\\");
                break;
            case '\n':
                builder.Append("\\n");
                break;
            case '\r':
                builder.Append("\\r");
                break;
            case '\t':
                builder.Append("\\t");
                break;
            default:
                if (ch < 0x20 || ch == 0x7f)
                {
                    builder.Append("\\x").Append(((int)ch).ToString("x2"));
                }
                else if (ch == quote)
                {
                    builder.Append('\\').Append(quote); // 仅转义包裹符本身（与 Python 一致）
                }
                else
                {
                    builder.Append(ch);
                }

                break;
        }
    }

    builder.Append(quote);
    return builder.ToString();
}
