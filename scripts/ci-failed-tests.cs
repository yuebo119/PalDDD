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

// 2026-09-13 增：本脚本此前无自证能力（gate-audit 矩阵标 UNVERIFIED）。三通道都是
// 「从日志/报告里挑出该报的行」，失效形态是**该报的失败没报**（诊断静默丢失，
// 而调用方一律 `|| true` 兜底，故不会以非零退出暴露）。通道 ②的两个窗口判定
// （关键字取最后 15、尾部 120→非空→最后 30）是纯文本处理，抽为纯函数并覆盖。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

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
    foreach (var ctx in LogKeywordContexts(lines))
    {
        Emit(ctx);
    }

    // 通道 2b：尾部窗口——最后 120 行里取非空白的最后 30 行
    var tail = LogTailWindow(lines);
    foreach (var line in tail)
    {
        Emit($"LOG| {line}");
    }

    return tail.Count;
}

// 通道 2a 判定（纯函数，供 --selftest 覆盖）：取关键字命中的**最后 15 个**，
// 每个附其下一行（下一行为空则略）。返回待发射的 "CTX| …" / "CTX+1| …" 行。
static List<string> LogKeywordContexts(string[] lines)
{
    string[] keys = ["exception", "failed", "error(s)", "timeout", "timed out", "killed", "exit code", "fatal"];
    var hits = new List<(int Index, string Line)>();
    for (var i = 0; i < lines.Length; i++)
    {
        if (keys.Any(key => lines[i].Contains(key, StringComparison.OrdinalIgnoreCase)))
        {
            hits.Add((i, lines[i]));
        }
    }

    var outLines = new List<string>();
    foreach (var (index, line) in hits.Skip(Math.Max(0, hits.Count - 15))) // 只取最后 15 个命中
    {
        outLines.Add($"CTX| {line}");
        if (index + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[index + 1]))
        {
            outLines.Add($"CTX+1| {lines[index + 1]}");
        }
    }
    return outLines;
}

// 通道 2b 判定（纯函数）：末 120 行 → 去空白 → 取最后 30 行。
// 两级窗口的先后顺序影响结果（先截 120 再取非空最后 30），故单独抽出钉住。
static List<string> LogTailWindow(string[] lines) =>
    lines.Skip(Math.Max(0, lines.Length - 120))
         .Where(line => !string.IsNullOrWhiteSpace(line))
         .TakeLast(30)
         .ToList();

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

// ══════════════ 自测（纯文本判定，不依赖仓库/文件系统）══════════════

static int SelfTest()
{
    var passed = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    // ── 通道 2a：关键字上下文（最后 15 个命中 + 附下一行）──
    Case("无关键字 → 无 CTX 行", LogKeywordContexts(["all good", "still fine"]).Count == 0);
    Case("单命中 → 仅 CTX 行（下一行为空）",
        LogKeywordContexts(["boom Exception here", ""]) is ["CTX| boom Exception here"]);
    Case("命中且下一行非空 → CTX 与 CTX+1 两行",
        LogKeywordContexts(["an Exception", "detail line"]) is ["CTX| an Exception", "CTX+1| detail line"]);
    Case("命中在末行（无下一行）→ 仅 CTX 行（边界）",
        LogKeywordContexts(["tail fatal"]) is ["CTX| tail fatal"]);
    Case("关键字大小写不敏感", LogKeywordContexts(["FAILED"]).Count == 1);
    // keys 覆盖逐项独立性（防某条被摘掉）
    Case("关键字覆盖 timeout", LogKeywordContexts(["timeout"]).Count == 1);
    Case("关键字覆盖 timed out", LogKeywordContexts(["timed out"]).Count == 1);
    Case("关键字覆盖 killed", LogKeywordContexts(["killed"]).Count == 1);
    Case("关键字覆盖 exit code", LogKeywordContexts(["exit code 1"]).Count == 1);
    Case("关键字覆盖 fatal", LogKeywordContexts(["fatal"]).Count == 1);
    Case("关键字覆盖 error(s)", LogKeywordContexts(["0 error(s)"]).Count == 1);
    Case("普通行不误报", LogKeywordContexts(["building project", "publishing"]).Count == 0);

    // 15 个命中窗口：26 个命中 → 只保留最后 15 个（每条无下一行上下文时 = 15 行）
    var many = Enumerable.Range(0, 26).Select(i => $"failed {i}").ToArray();
    var manyCtx = LogKeywordContexts(many);
    // 注意：此处输入行互为「下一行非空」，故每条命中另附一行 CTX+1——断言须数 CTX| 行数
    Case("命中数超 15 时只保留最后 15 个（数 CTX| 行）",
        manyCtx.Count(l => l.StartsWith("CTX| ", StringComparison.Ordinal)) == 15);
    Case("保留的是最后 15 个（非最前）", manyCtx[0] == "CTX| failed 11" && manyCtx[^1] == "CTX| failed 25");

    // ── 通道 2b：尾部两级窗口 ──
    Case("空输入 → 空窗口", LogTailWindow([]).Count == 0);
    Case("空白行被剔除", LogTailWindow(["a", "", "  ", "b"]) is ["a", "b"]);
    Case("非空行不足 30 → 全取",
        LogTailWindow(Enumerable.Range(0, 5).Select(i => $"L{i}").ToArray()).Count == 5);
    // 两级窗口顺序（先截末 120 行、再取非空最后 30 行）：200 行中前 80 行不得出现
    var big = Enumerable.Range(0, 200).Select(i => i < 80 ? $"EARLY{i}" : $"LATE{i}").ToArray();
    var window = LogTailWindow(big);
    Case("两级窗口：先截末 120 行再取最后 30", window.Count == 30);
    Case("两级窗口：起点为第 170 行", window[0] == "LATE170" && window[^1] == "LATE199");
    Case("两级窗口：窗口外的早期行不出现", !window.Any(l => l.StartsWith("EARLY", StringComparison.Ordinal)));

    // ── Truncate（快照差异行截断，边界含 100）──
    Case("Truncate 边界：100 字符原样", Truncate(new string('x', 100)) == new string('x', 100));
    Case("Truncate 边界：101 字符截到 100", Truncate(new string('x', 101)).Length == 100);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
