// ============================================================================
// sql-comment-guard.cs——SQL 字符串内嵌 `--` 注释的自噬扫描（2026-11-25 增）
//
// 动机（项目 AGENTS.md §3 实证，机械层原标注「为待办门禁」）：
//   2026-09-25 会话审计：勘正注释 `-- v72：删…谓词` 被写进单行 SQL 字符串字面量内，
//   `--` 到语句尾全部被数据库当注释，`AND locked_until` 双条件守卫退化为单条件，
//   租约 fencing 丢失，P3 回归 P1。与 XML 注释含 `--` 致 MSB4025 属同族（形态不同）。
//   代码评审靠肉眼核对谓词完整性，在长 SQL 拼接里不可靠，故改机械扫描。
//
// 判定（扫全树 src/**/*.cs，不只暂存集：存量漏洞与新增同等对待）：
//   命中：字符串字面量内含 `--`，且满足任一
//         ① `--` 之后紧跟空白（`-- ` 是 SQL 行注释的标准形态）；
//         ② 该字面量同时含 SQL 语句关键字（词边界：SELECT/INSERT/UPDATE/DELETE/
//            WHERE/FROM/JOIN/VALUES/INTO/SET/LIMIT/ORDER BY/GROUP BY）。
//   放行：`--` 后紧跟非空白且字面量无 SQL 关键字（`a--b`、URL、命令行前缀等）。
//   提示（WARN，不阻断）：字面量含 `--` 但两个条件都不满足，输出供人核。
//   放行：字面量 trim 后长度 ≤ 2（纯 `--` 分隔符）；C# 注释内的 `--`（不在字面量内）；
//         字符字面量 `'-'`；自减表达式 `count--`（不在字面量内）。
//
//   覆盖面：普通字符串 `"…"`、逐字字符串 `@"…"`、插值字符串 `$"…"`、
//   多行原始字符串 `"""…"""`（文件级状态机跟踪，跨行内容按行判定）。
//
//   已知不覆盖（T-34）：
//   - 字符串拼接后的语义（`sql + "-- x"` 的跨表达式组合）——逐字面量判定，不做常量折叠。
//   - .sql 资源文件与 docs 内的 SQL 示例（本门禁只管 src 代码里的字符串字面量；
//     .sql 文件由 semgrep 与人工审查承担）。
//   - 其他数据库的注释分隔符（`#`、`/* */` 在 C# 字符串里不构成自噬面）。
//
// 用法（仓库根执行）：
//   dotnet run scripts/sql-comment-guard.cs
//   dotnet run scripts/sql-comment-guard.cs -- --selftest
//
// 退出码：0=通过；1=命中；2=仓库根定位失败。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表；本脚本输出是 pre-commit 协议固定行
//（PASS/FAIL/WARN 前缀被 grep 与人读消费），沿 new-skip-guard.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return SelfTest();
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var files = GitOutput("ls-files src")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(p => p.EndsWith(".cs", StringComparison.Ordinal))
    .ToList();

var failures = new List<SqlCommentFinding>();
var warnings = new List<SqlCommentFinding>();

foreach (var file in files)
{
    string[] lines;
    try
    {
        lines = File.ReadAllLines(file, Encoding.UTF8);
    }
    catch (IOException ex)
    {
        Console.WriteLine($"WARN 读取失败跳过：{file}（{ex.GetType().Name}）");
        continue;
    }

    foreach (var (lineNo, literal) in SqlScanLogic.Literals(lines))
    {
        var (fail, warn) = SqlScanLogic.JudgeLiteral(literal);
        if (fail) failures.Add(new SqlCommentFinding(file, lineNo, literal));
        else if (warn) warnings.Add(new SqlCommentFinding(file, lineNo, literal));
    }
}

foreach (var w in warnings.Take(5))
    Console.WriteLine($"WARN 可疑但未阻断：{w.File}:{w.LineNo}  {Clip(w.Literal)}");
if (warnings.Count > 5) Console.WriteLine($"WARN …另有 {warnings.Count - 5} 处");

if (failures.Count == 0)
{
    Console.WriteLine($"PASS 扫描 {files.Count} 个 src 源文件，字符串字面量内无 SQL 注释自噬形态（WARN {warnings.Count} 处）");
    return 0;
}

Console.WriteLine($"FAIL 检测到 {failures.Count} 处 SQL 字符串内嵌 `--` 注释——数据库会把 `--` 到语句尾当注释");
foreach (var f in failures.Take(10))
    Console.WriteLine($"  {f.File}:{f.LineNo}  {Clip(f.Literal)}");
if (failures.Count > 10) Console.WriteLine($"  …另有 {failures.Count - 10} 处");
Console.WriteLine();
Console.WriteLine("改法：把注释移出 SQL 字面量（C# // 注释或独立说明），不要删掉守卫谓词。");
return 1;

// ══════════════ git 交互 ══════════════

static string GitOutput(string arguments)
{
    var psi = new ProcessStartInfo("git", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false,
    };
    try
    {
        using var p = Process.Start(psi);
        if (p is null) return "";
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : "";
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return ""; // git 不可用——守卫让行，不阻塞提交
    }
}

static string FindRepoRoot()
{
    foreach (var start in (string?[])[Environment.CurrentDirectory, AppContext.BaseDirectory])
    {
        var dir = start;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "PalDDD.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
    Console.Error.WriteLine("错误：未定位到仓库根（无 PalDDD.slnx）——请在仓库内执行");
    Environment.Exit(2);
    return ""; // 不可达
}

static string Clip(string text)
{
    var t = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    return t.Length <= 90 ? t : t[..90] + "…";
}

// ══════════════ 自测 ══════════════

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

    // 判定：SQL 关键字 + `--` 空格形态必红
    Case("拦截：SELECT 字面量含 `-- ` 注释",
        SqlScanLogic.JudgeLiteral("SELECT * FROM t WHERE a=1 -- 备注").Fail);
    Case("拦截：UPDATE 字面量含 `-- v72：删谓词`",
        SqlScanLogic.JudgeLiteral("UPDATE t SET a=1 -- v72：删谓词").Fail);
    Case("拦截：无 SQL 关键字但 `--` 后跟空白",
        SqlScanLogic.JudgeLiteral("-- 单独一行说明").Fail);
    Case("拦截：字面量含 SQL 关键字且 `--` 后无空白（`WHERE a=1--x`）",
        SqlScanLogic.JudgeLiteral("SELECT 1 WHERE a=1--x").Fail);

    // 放行：非注释形态
    Case("放行：URL 中的 `--`（无空白无 SQL）", !SqlScanLogic.JudgeLiteral("http://x/a--b").Fail);
    Case("放行：纯 `--` 分隔符（长度 ≤ 2）", !SqlScanLogic.JudgeLiteral("--").Fail);
    Case("放行：普通文本 `a--b`", !SqlScanLogic.JudgeLiteral("prefix--suffix").Fail);
    Case("放行：`--selftest` 命令行前缀形态", !SqlScanLogic.JudgeLiteral("--selftest").Fail);

    // WARN 面
    var (fail1, warn1) = SqlScanLogic.JudgeLiteral("a--b");
    Case("提示：`a--b` 记 WARN 不阻断", !fail1 && warn1);

    // 行内提取
    var lits = SqlScanLogic.Literals(["var sql = \"SELECT * FROM t -- x\"; var n = 0;"]);
    Case("提取：普通字符串字面量", lits.Count == 1 && lits[0].Literal.Contains("SELECT", StringComparison.Ordinal));

    // 自减不在字面量内
    var dec = SqlScanLogic.Literals(["for (var i = n; i > 0; i--) { }"]);
    Case("提取：自减表达式不产生字面量", dec.Count == 0);

    // C# 注释里的 `--` 不算
    var comment = SqlScanLogic.Literals(["// SELECT * FROM t -- 注释", "/// 文档 -- 说明"]);
    Case("提取：C# 行注释不产生字面量", comment.Count == 0);

    // verbatim 字符串
    var verbatim = SqlScanLogic.Literals([@"var sql = @""UPDATE t SET a=1 -- 备注"";"]);
    Case("提取：逐字字符串", verbatim.Count == 1 && verbatim[0].Literal.Contains("UPDATE", StringComparison.Ordinal));

    // 多行 raw string（文件级状态）
    var rawLines = new[]
    {
        "var sql = \"\"\"",
        "    SELECT * FROM t",
        "    WHERE a = 1 -- v72：删谓词",
        "    AND b = 2",
        "    \"\"\";",
        "var x = 1;",
    };
    var rawLits = SqlScanLogic.Literals(rawLines);
    var rawHit = rawLits.Where(l => l.Literal.Contains("v72", StringComparison.Ordinal)).ToList();
    Case("提取：多行 raw string 的中间行按字面量处理", rawHit.Count == 1 && rawHit[0].LineNo == 3);
    Case("提取：raw string 外的代码行不误并入", rawLits.All(l => !l.Literal.Contains("var x", StringComparison.Ordinal)));

    // 插值字符串
    var interp = SqlScanLogic.Literals(["var sql = $\"SELECT * FROM {table} -- 备注\";"]);
    Case("提取：插值字符串", interp.Count == 1 && interp[0].Literal.Contains("SELECT", StringComparison.Ordinal));

    // 字符字面量
    var chr = SqlScanLogic.Literals(["var c = '-';"]);
    Case("提取：字符字面量不产生字符串字面量", chr.Count == 0);

    // 转义引号
    var esc = SqlScanLogic.Literals(["var s = \"a \\\" b -- c\";"]);
    Case("提取：转义引号不截断字面量", esc.Count == 1 && esc[0].Literal.Contains("-- c", StringComparison.Ordinal));

    // 关键字边界：SET 出现在非 SQL 语境时不误判（仅当同时含 -- 时才进入判定）
    Case("放行：`reset--full` 无 SQL 关键字", !SqlScanLogic.JudgeLiteral("reset--full").Fail);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}

// ══════════════ 类型声明（C# 顶级语句要求：全部语句之后）══════════════

internal sealed record SqlCommentFinding(string File, int LineNo, string Literal);

internal static partial class SqlScanLogic
{
    /// <summary>SQL 语句关键字（词边界，忽略大小写）。仅作 `--` 判定的佐证条件之一，
    /// 因此 SET/OR 这类常见英文词的误配面被 `--` 前置条件压到极小。</summary>
    [GeneratedRegex(@"\b(SELECT|INSERT|UPDATE|DELETE|WHERE|FROM|JOIN|VALUES|INTO|SET|LIMIT)\b|ORDER\s+BY|GROUP\s+BY",
        RegexOptions.IgnoreCase)]
    public static partial Regex SqlKeywordRegex();

    /// <summary>字面量判定：返回 (是否阻断, 是否提示)。</summary>
    public static (bool Fail, bool Warn) JudgeLiteral(string literal)
    {
        if (literal.Trim().Length <= 2) return (false, false); // 纯 `--` 分隔符
        var hasSql = SqlKeywordRegex().IsMatch(literal);
        var idx = literal.IndexOf("--", StringComparison.Ordinal);
        if (idx < 0) return (false, false);

        var sawSuspicious = false;
        while (idx >= 0)
        {
            var afterIsWhitespace = idx + 2 >= literal.Length || char.IsWhiteSpace(literal[idx + 2]);
            if (afterIsWhitespace || hasSql) return (true, false);
            sawSuspicious = true;
            idx = literal.IndexOf("--", idx + 2, StringComparison.Ordinal);
        }
        return (false, sawSuspicious);
    }

    /// <summary>从源码行提取字符串字面量内容（普通/逐字/插值/多行 raw）。
    /// 文件级状态机跟踪 raw string 的跨行区间，C# 注释与字符字面量被排除。</summary>
    public static List<(int LineNo, string Literal)> Literals(IReadOnlyList<string> lines)
    {
        var result = new List<(int, string)>();
        var inRaw = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;

            if (inRaw)
            {
                var end = line.IndexOf("\"\"\"", StringComparison.Ordinal);
                if (end >= 0)
                {
                    result.Add((lineNo, line[..end]));
                    inRaw = false;
                    // 收尾行 `"""` 之后可能还有代码（如 `;`），不再提取——罕见形态，方向安全
                }
                else
                {
                    result.Add((lineNo, line));
                }
                continue;
            }

            var start = FindRawStart(line);
            if (start >= 0)
            {
                var before = line[..start];
                ExtractNormalLiterals(before, lineNo, result);
                var after = line[(start + 3)..];
                var end = after.IndexOf("\"\"\"", StringComparison.Ordinal);
                if (end >= 0)
                {
                    result.Add((lineNo, after[..end]));
                }
                else
                {
                    result.Add((lineNo, after));
                    inRaw = true;
                }
                continue;
            }

            ExtractNormalLiterals(line, lineNo, result);
        }
        return result;
    }

    /// <summary>行内 raw string 起始位置（不在普通/逐字字面量内的第一个 """）；-1 表示无。</summary>
    private static int FindRawStart(string s)
    {
        bool inStr = false, verbatim = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (verbatim)
                {
                    if (c != '"') continue;
                    if (i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }
                    inStr = false;
                    verbatim = false;
                }
                else
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                }
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') return -1;
            if (c == '"')
            {
                if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"') return i;
                inStr = true;
                verbatim = i > 0 && s[i - 1] == '@';
            }
        }
        return -1;
    }

    /// <summary>提取一行内普通/逐字/插值字符串的内容；遇到 // 停止。</summary>
    private static void ExtractNormalLiterals(string s, int lineNo, List<(int, string)> sink)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') return;
            if (c == '\'')
            {
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '\'') { i++; break; }
                    i++;
                }
                continue;
            }
            if (c == '"')
            {
                var verbatim = i > 0 && s[i - 1] == '@';
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    if (verbatim)
                    {
                        if (s[i] == '"')
                        {
                            if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(s[i]);
                        i++;
                    }
                    else
                    {
                        if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                        if (s[i] == '"') { i++; break; }
                        sb.Append(s[i]);
                        i++;
                    }
                }
                sink.Add((lineNo, sb.ToString()));
                continue;
            }
            i++;
        }
    }
}
