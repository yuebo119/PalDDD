// ============================================================================
// declaration-guard.cs——代码内决策声明注释的删除守卫（2026-11-25 增）
//
// 动机（项目 AGENTS.md §3 两条实证，机械层原标注「为待办」）：
//   ① 2026-09-17 审计把 SagaState.CloneForLease 的 P3 勘正声明判为「风险低」并实施，
//      实施时删掉了声明而非回应声明，失败模式被静默翻转（并发写可抛 → 漏补偿无兜底），
//      且 init→set 的公共 API 破坏对快照不可见；2026-09-19 回滚。
//      教训：审计条目命中代码内声明注释时，必须先落决策文档回应声明，禁止删声明通过。
//   ② 2026-09-25 会话审计同族：SQL 单行字符串里的勘正注释使租约 fencing 双条件守卫
//      退化为单条件，P3 回归 P1（该形态由 sql-comment-guard.cs 负责，本脚本只管代码注释）。
//
// 判定（净删除口径）：
//   拦截：暂存集 src/**/*.cs 的 unified diff 中，「删除行含强声明字样且位于注释内」的
//         条数减去「新增行同形态」的条数 > 0，且暂存集不含决策文档
//         （docs/decisions/*.md 或 docs/review/decision-*.md）。
//   放行：① 净删除 ≤ 0（改写措辞、移动注释、删后在新位置写回，增删相抵）；
//         ② 暂存集含决策文档（同提交回应声明，结构由 verify-conventions V11 管）；
//         ③ 显式豁免 ALLOW_DECLARATION_REMOVAL=1（提交信息须写理由）。
//
//   强声明字样：P定案 / 勘正 / 取舍 / 联动约束 / 登记延后 / 暂不优化 / 禁止删除。
//   收窄声明（与 AGENTS.md §3 人工层原文的差异，如实登记）：人工层还要求 grep「ADR」，
//   机械层不把 ADR 编号引用计入——ADR 引用是索引（删索引不改决策承载），逐条计入会让
//   任何含「见 ADR-026」的代码删除都要求决策文档，误伤面盖过收益。人工层的 ADR 检查
//   职责不变。
//
//   已知不覆盖（T-34，声明覆盖形态即声明不覆盖什么）：
//   - raw string（含 """ 的行直接跳过）：声明字样跨 raw string 行时漏报，方向安全。
//   - 字符串字面量内的声明字样：本仓 src 无此形态，漏报方向安全。
//   - 整文件删除：diff 全是删除行，文件含声明字样即命中（有意行为）。
//   - docs/ 与 test/ 内的声明文字增删（本门禁只管 src 代码注释）。
//
// 用法（仓库根执行）：
//   dotnet run scripts/declaration-guard.cs
//   dotnet run scripts/declaration-guard.cs -- --selftest
//
// 退出码：0=放行；1=拦截；2=仓库根定位失败。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表；本脚本输出是 pre-commit 协议固定行
//（PASS/FAIL 前缀被 grep 与人读消费），沿 new-skip-guard.cs / test-change-guard.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Globalization;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return SelfTest();
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var patch = GitOutput("diff --cached -U0 -- src");
var (netRemoved, hits) = DeclarationLogic.Tally(patch);
var decisionDocs = GitOutput("diff --cached --name-only --diff-filter=ACMR")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(DeclarationLogic.IsDecisionDoc)
    .ToList();
var allowOverride = Environment.GetEnvironmentVariable("ALLOW_DECLARATION_REMOVAL") == "1";
var (block, reason) = DeclarationLogic.Decide(netRemoved, decisionDocs.Count, allowOverride);

if (!block)
{
    Console.WriteLine($"PASS {reason}");
    return 0;
}

Console.WriteLine($"FAIL 检测到 src/** 决策声明注释被删除（净删除 {netRemoved} 行）——禁止删声明通过");
foreach (var h in hits.Take(10))
    Console.WriteLine($"  {h.File}:{h.LineNo}  {h.Text.Trim()}");
if (hits.Count > 10) Console.WriteLine($"  …另有 {hits.Count - 10} 处");
Console.WriteLine();
Console.WriteLine("声明的决策理由需要被回应，而不是被删除。二选一：");
Console.WriteLine("  1) 同提交落决策文档（docs/review/decision-*.md 或 docs/decisions/*.md）说明改判理由；");
Console.WriteLine("  2) 属误判时显式豁免并在提交信息写理由：ALLOW_DECLARATION_REMOVAL=1 git commit …");
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

    const string removeDeclaration =
        "diff --git a/src/A/B.cs b/src/A/B.cs\n" +
        "index 111..222 100644\n" +
        "--- a/src/A/B.cs\n" +
        "+++ b/src/A/B.cs\n" +
        "@@ -10,2 +10,0 @@\n" +
        "-    // P定案：浅拷贝共享是有意取舍\n" +
        "-    public void M() { }\n";

    var (net, hits) = DeclarationLogic.Tally(removeDeclaration);
    Case("拦截：删除含 P定案 的注释行", net == 1);
    Case("报告：命中数 1 且行号取 - 侧（第 10 行）", hits.Count == 1 && hits[0].LineNo == 10);
    Case("判定：净删除 1 行 + 无决策文档 → 拦截", DeclarationLogic.Decide(net, 0, false).Block);
    Case("判定：净删除 1 行 + 有决策文档 → 放行", !DeclarationLogic.Decide(net, 1, false).Block);
    Case("判定：净删除 1 行 + 显式豁免 → 放行", !DeclarationLogic.Decide(net, 0, true).Block);

    const string moveDeclaration =
        "diff --git a/src/A/B.cs b/src/A/B.cs\n" +
        "--- a/src/A/B.cs\n" +
        "+++ b/src/A/B.cs\n" +
        "@@ -3,1 +3,1 @@\n" +
        "-    // 勘正：旧措辞\n" +
        "+    // 勘正：新措辞（措辞澄清，理由不变）\n";
    var (netMove, _) = DeclarationLogic.Tally(moveDeclaration);
    Case("放行：改写声明措辞（增删相抵净 0）", netMove == 0 && !DeclarationLogic.Decide(netMove, 0, false).Block);

    const string codeOnly =
        "diff --git a/src/A/B.cs b/src/A/B.cs\n" +
        "--- a/src/A/B.cs\n" +
        "+++ b/src/A/B.cs\n" +
        "@@ -1,1 +1,0 @@\n" +
        "-var message = \"无需勘正\";\n";
    var (netCode, _) = DeclarationLogic.Tally(codeOnly);
    Case("放行：字符串字面量内的字样不计入（非注释）", netCode == 0);

    const string tailComment =
        "diff --git a/src/A/B.cs b/src/A/B.cs\n" +
        "--- a/src/A/B.cs\n" +
        "+++ b/src/A/B.cs\n" +
        "@@ -5,1 +5,0 @@\n" +
        "-var x = 1; // 取舍：性能优先\n";
    var (netTail, _) = DeclarationLogic.Tally(tailComment);
    Case("拦截形态：行尾 // 注释计入", netTail == 1);

    const string nonSrc =
        "diff --git a/test/A/B.cs b/test/A/B.cs\n" +
        "--- a/test/A/B.cs\n" +
        "+++ b/test/A/B.cs\n" +
        "@@ -1,1 +1,0 @@\n" +
        "-// 勘正：测试说明\n";
    var (netNonSrc, _) = DeclarationLogic.Tally(nonSrc);
    Case("放行：非 src/ 路径不计入", netNonSrc == 0);

    const string rawString =
        "diff --git a/src/A/B.cs b/src/A/B.cs\n" +
        "--- a/src/A/B.cs\n" +
        "+++ b/src/A/B.cs\n" +
        "@@ -1,1 +1,0 @@\n" +
        "-        // 勘正：{{模板}} 行\n" +
        "-\"\"\";\n";
    var (netRaw, _) = DeclarationLogic.Tally(rawString);
    Case("形态：raw string 结束行跳过，但声明行仍计入", netRaw == 1);

    Case("路径识别：docs/decisions/026-x.md 是决策文档", DeclarationLogic.IsDecisionDoc("docs/decisions/026-x.md"));
    Case("路径识别：docs/review/decision-2026-09-19-x.md 是决策文档", DeclarationLogic.IsDecisionDoc("docs/review/decision-2026-09-19-x.md"));
    Case("路径识别：docs/review/review-2026-09-19-x.md 不是决策文档", !DeclarationLogic.IsDecisionDoc("docs/review/review-2026-09-19-x.md"));
    Case("路径识别：src/A/B.cs 不是决策文档", !DeclarationLogic.IsDecisionDoc("src/A/B.cs"));
    Case("路径识别：docs/decisionsx/x.md 不是决策文档", !DeclarationLogic.IsDecisionDoc("docs/decisionsx/x.md"));

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}

// ══════════════ 类型声明（C# 顶级语句要求：全部语句之后）══════════════

internal sealed record DeclarationHit(string File, int LineNo, string Text);

internal static partial class DeclarationLogic
{
    /// <summary>强声明字样：承载决策理由、删除即静默翻转失败模式的注释标记。</summary>
    private static readonly string[] StrongMarkers =
        ["P定案", "勘正", "取舍", "联动约束", "登记延后", "暂不优化", "禁止删除"];

    public static bool IsDecisionDoc(string path) =>
        (path.StartsWith("docs/decisions/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal))
        || (path.StartsWith("docs/review/decision-", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal));

    /// <summary>行内注释起点（-1 表示无）。字符串/字符字面量内的 // 不算；
    /// 含 """ 的 raw string 行返回 -2（调用方跳过该行）。</summary>
    public static int FindCommentStart(string s)
    {
        if (s.Contains("\"\"\"", StringComparison.Ordinal)) return -2;
        bool inStr = false, inChar = false, verbatim = false;
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
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }
            if (c == '"')
            {
                inStr = true;
                verbatim = i > 0 && s[i - 1] == '@';
                continue;
            }
            if (c == '\'') { inChar = true; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') return i;
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') return i;
        }
        return -1;
    }

    /// <summary>一行代码的注释部分是否含强声明字样。整行注释（/// 与 * 开头）直接判定；
    /// 行尾注释取 // 之后文本。raw string 行跳过（不覆盖，方向安全）。</summary>
    public static bool HasDeclaration(string line)
    {
        var start = FindCommentStart(line);
        if (start == -2) return false;
        if (start < 0)
        {
            // 无 // 形态的块注释续行（以 * 开头）
            var t = line.TrimStart();
            return t.StartsWith('*') && ContainsMarker(t);
        }
        return ContainsMarker(line[(start + 2)..]);
    }

    private static bool ContainsMarker(string text)
    {
        foreach (var m in StrongMarkers)
            if (text.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>解析 unified diff（-U0），按行统计 src/**/*.cs 内声明注释的净删除数。
    /// 行号取 @@ 头的 - 侧真实行号（报告失实等于门禁失明）。</summary>
    public static (int NetRemoved, List<DeclarationHit> Hits) Tally(string diffText)
    {
        var file = "";
        var lineNo = 0;
        int removed = 0, added = 0;
        var hits = new List<DeclarationHit>();

        foreach (var raw in diffText.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("diff --git", StringComparison.Ordinal)) { file = ""; continue; }
            if (raw.StartsWith("+++ b/", StringComparison.Ordinal)) { file = raw[6..]; continue; }
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkHeadRegex().Match(raw);
                lineNo = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                continue;
            }
            if (raw.StartsWith("--- ", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal)
                || raw.StartsWith('\\'))
                continue;
            if (file.Length == 0 || !file.StartsWith("src/", StringComparison.Ordinal)
                || !file.EndsWith(".cs", StringComparison.Ordinal))
                continue;

            if (raw.StartsWith('-'))
            {
                if (HasDeclaration(raw[1..]))
                {
                    removed++;
                    hits.Add(new DeclarationHit(file, lineNo, raw[1..]));
                }
                lineNo++;
            }
            else if (raw.StartsWith('+'))
            {
                if (HasDeclaration(raw[1..])) added++;
            }
        }
        return (removed - added, hits);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^@@ -(\d+)")]
    private static partial System.Text.RegularExpressions.Regex HunkHeadRegex();

    public static (bool Block, string Reason) Decide(
        int netRemoved, int decisionDocCount, bool allowOverride)
    {
        if (netRemoved <= 0)
            return (false, netRemoved == 0
                ? "暂存集无声明注释净删除（增删相抵或无声明）"
                : "声明注释净删除为负（新增多于删除）");
        if (decisionDocCount > 0)
            return (false, $"暂存集含 {decisionDocCount} 份决策文档——按「回应声明」处置");
        if (allowOverride)
            return (false, $"ALLOW_DECLARATION_REMOVAL=1 显式豁免（净删除 {netRemoved} 行，提交信息须写理由）");
        return (true, $"src/** 声明注释净删除 {netRemoved} 行且无决策文档");
    }
}
