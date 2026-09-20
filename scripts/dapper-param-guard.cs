// ============================================================================
// dapper-param-guard.cs——Dapper 参数枚举直传静态守卫（2026-09-14 新增）
//
// 动机（CI #94 实证）：Dapper.AOT 生成拦截器把枚举参数**直传驱动**，PG 抛
// InvalidCastException（Writing values of 'ProjectionCheckpointStatus' is not
// supported）；经典路径下驱动容忍 → 本地 SQLite 全绿与 CI PG 崩溃分叉。
// 本地盲区双重：① DialectProbeTests 本地 Skip（禁外部库裁决）② AOT 探针当时
// 未覆盖 Checkpoint 组件。修复（ddd9f28）为显式 (int) 化后，本守卫把该类
// 模式固化为**提交期静态检查**——下一处枚举直传在 pre-commit 即红，不再等 CI。
//
// 判定：扫描 src/PalDDD.Dapper*/ 下 .cs 源码中的**匿名对象参数**（`new { ... }`
// 块，经花括号深度配对），块内成员赋值形如 `= XxxStatus.Member`（未经 (int)
// 转换）即违规。命名域名用 `*Status` 后缀（现状 6 枚举：Outbox/Inbox/Saga/
// ProjectionCheckpoint/IdempotencyRecord/Checkpoint 全命中；扩展点在 Pattern）。
//
// 为什么正则天然排除已修形态：模式 `=\s*[A-Z]\w*Status\.` 要求 `=` 后紧跟
// 字母开头的类型名；`= (int)XxxStatus.Y` 的 `(` 使其不匹配——无需负向前瞻。
// 领域对象构造（`new InboxMessage { Status = InboxStatus.Processing }`）经
// "new 后必须紧邻 {"（匿名）判定天然排除——只有匿名对象才是 Dapper 参数。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/dapper-param-guard.cs             扫描 src/PalDDD.Dapper*/
//   dotnet run scripts/dapper-param-guard.cs -- --selftest  自测（判定单元验证）
//
// 退出码：0=无违规；1=发现枚举直传；2=仓库根定位失败。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是门禁固定协议行
// （PASS/FAIL 被人读与 grep 消费），固定中文非用户可配文案——沿 xml-guard.cs
// / secret-scan.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文输出会乱码——对齐 xml-guard.cs
Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest"))
{
    return SelfTest();
}

// ─── 仓库根定位（CWD 向上找 PalDDD.slnx；BaseDirectory 兜底）───
var root = FindRepoRoot();
Environment.CurrentDirectory = root;

Console.WriteLine("═══ Dapper 参数枚举守卫 ═══");

var violations = new List<string>();
var scannedDirs = 0;
var scannedFiles = 0;
foreach (var dir in new[] { "src/PalDDD.Dapper", "src/PalDDD.Dapper.PostgreSql", "src/PalDDD.Dapper.MySql", "src/PalDDD.Dapper.Sqlite" })
{
    if (!Directory.Exists(dir)) continue;
    scannedDirs++;
    foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
        scannedFiles++;
        var text = File.ReadAllText(file);
        foreach (var (line, snippet) in FindEnumParams(text))
            violations.Add($"{ToPosix(file)}:{line}  {snippet}");
    }
}

// T-1 同类空转守卫（2026-09-19 系统优化）：四个 Dapper 目录全部不存在或零 .cs 文件
// → 扫描范围意外为空（目录改名/结构重构）——exit 1 而非报 PASS（「跑过东西的绿」才是绿）
if (scannedDirs == 0 || scannedFiles == 0)
{
    Console.WriteLine($"FAIL 扫描范围为空（命中目录 {scannedDirs}/4，扫描文件 {scannedFiles}）——Dapper 目录结构可能已变，守卫失去扫描对象（空转假绿防护，v2 审计 T-1 同类）");
    return 1;
}

if (violations.Count > 0)
{
    Console.WriteLine("FAIL 匿名参数含枚举直传（Dapper.AOT 拦截器直传驱动，PG/MySQL 拒绝；改 (int) 强转）：");
    foreach (var v in violations) Console.WriteLine(v);
    Console.WriteLine($"═══ 结果：{violations.Count} 违规 ═══");
    return 1;
}
Console.WriteLine("PASS Dapper 参数无枚举直传（全 *Status 枚举已 (int) 化）");
Console.WriteLine("═══ 结果：0 违规 ═══");
return 0;

// ══════════════ static 局部函数 ══════════════

// 判定核心：扫描文本中的匿名对象块，返回 (行号, 成员片段) 违规列表。
// 匿名识别：`new` 后跳过空白**必须紧邻** `{`——`new Type {`（领域对象）不匹配。
static List<(int Line, string Snippet)> FindEnumParams(string text)
{
    var result = new List<(int, string)>();
    // 匹配 = XxxStatus.Member（= 后紧跟字母开头的类型名；= (int)X 的 ( 天然不匹配）
    // 全仓扫描修复（假阳性）：原模式 `=\s*[A-Z]\w*Status\.` 未锚定 `=` 左侧，故
    // `== XxxStatus.Y` / `!= XxxStatus.Y` / `>= XxxStatus.Y` 会从运算符里那个 `=` 起
    // 匹配——比较运算被判成参数直传。加负向后视排除 `= ! < >` 前缀（`=>`/`+=` 因
    // `>`/`+` 后不接 [A-Z] 本就不匹配，无需额外处理）。
    var pattern = new Regex(@"(?<![=!<>])=\s*(?<t>[A-Z]\w*Status\.\w+)", RegexOptions.Compiled);
    int i = 0;
    while (i < text.Length)
    {
        var newIdx = text.IndexOf("new", i, StringComparison.Ordinal);
        if (newIdx < 0) break;
        // new 前须为边界（非标识符字符），防匹配到 "renew" 等
        if (newIdx > 0 && (char.IsLetterOrDigit(text[newIdx - 1]) || text[newIdx - 1] == '_'))
        {
            i = newIdx + 3;
            continue;
        }
        var j = newIdx + 3;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        if (j >= text.Length || text[j] != '{')
        {
            i = newIdx + 3;
            continue; // new Type ... —— 非匿名对象
        }
        var close = MatchBrace(text, j);
        if (close < 0) { i = newIdx + 3; continue; }
        var block = text[j..close];
        var lineBase = CountLines(text, 0, j) + 1; // 1-based：{ 前的换行数 + 1
        foreach (Match m in pattern.Matches(block))
        {
            var line = lineBase + CountLines(block, 0, m.Groups["t"].Index);
            result.Add((line, m.Value.TrimStart('=', ' ')));
        }
        i = close + 1;
    }
    return result;
}

// 花括号深度配对：open 指向 '{'，返回配对 '}' 的下标；不配对返回 -1
static int MatchBrace(string text, int open)
{
    int depth = 0;
    for (int k = open; k < text.Length; k++)
    {
        if (text[k] == '{') depth++;
        else if (text[k] == '}')
        {
            depth--;
            if (depth == 0) return k;
        }
    }
    return -1;
}

// 统计 [start, end) 区间的换行数（用于把块内偏移折算为文件行号）
static int CountLines(string text, int start, int end)
{
    int count = 0;
    for (int k = start; k < end && k < text.Length; k++)
        if (text[k] == '\n') count++;
    return count;
}

static string ToPosix(string path) => path.Replace('\\', '/');

// 仓库根定位：CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底（file-based app
// 的 BaseDirectory 实测指向 %TEMP%\dotnet\runfile\...，向上不可达仓库根）
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
    return "";
}

// ══════════════ 自测（--selftest）══════════════

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

    // 正例：枚举直传（CI #94 的真实形态）→ 命中
    Case("枚举直传检出（CI #94 真实形态）",
        FindEnumParams("var p = new { status = ProjectionCheckpointStatus.Processing };").Count == 1);

    // 负例：已 (int) 化 → 放行
    Case("(int) 化放行（负向对照）",
        FindEnumParams("var p = new { status = (int)ProjectionCheckpointStatus.Processing };").Count == 0);

    // 负例：领域对象构造（new Type { ... }）→ 非 Dapper 参数，放行
    Case("领域对象构造放行（new InboxMessage { Status = InboxStatus.X }）",
        FindEnumParams("var m = new InboxMessage { Status = InboxStatus.Processing };").Count == 0);

    // 多行匿名块 + 混合成员：仅枚举项命中
    var multi = "var p = new\n{\n    a = 1,\n    st = SagaStatus.Active,\n    at = ToTimeParam(now)\n};";
    Case("多行匿名块混合成员仅枚举命中", FindEnumParams(multi).Count == 1);

    // 非 Status 后缀类型放行（命名域边界——防误报）
    Case("非 *Status 命名域放行", FindEnumParams("var p = new { t = ContentTypes.Json };").Count == 0);

    // 行号折算：违规在第 3 行
    var numbered = "line1\nline2\nvar p = new { s = OutboxStatus.Pending };\n";
    var found = FindEnumParams(numbered);
    Case("行号折算正确（第 3 行）", found.Count == 1 && found[0].Line == 3);

    // 假阳性回归（全仓扫描修复）：比较运算符被当成参数直传
    Case("比较运算 == 放行（假阳性回归）",
        FindEnumParams("var a = new { ok = p.Status == InboxStatus.Processing };").Count == 0);
    Case("不等 != 放行", FindEnumParams("var a = new { ok = p.Status != InboxStatus.Processing };").Count == 0);
    Case("大于等于 >= 放行", FindEnumParams("var a = new { ok = p.Status >= SagaStatus.Active };").Count == 0);
    // 正例对照：同形状的真赋值仍必须命中（防修假阳性时把检出也修掉）
    Case("真赋值仍命中（对照）", FindEnumParams("var a = new { st = InboxStatus.Processing };").Count == 1);

    // 无自指豁免的说明（不设"本文件零命中"用例）：本文件的样本字符串含
    // `new { ... = XxxStatus.Y }` 形态，若对自身文本运行判定会命中——这是
    // 刻意的（样本即正例）。守卫扫描面限定 src/PalDDD.Dapper*/，scripts/ 不在
    // 扫描面内，故无需自指豁免；⚠️ 若未来扩大扫描面到 scripts/，必须补排除
    // （OPS-7 自指污染先例：gate-audit 探针与 encoding-gate 均遇过同型）。

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
