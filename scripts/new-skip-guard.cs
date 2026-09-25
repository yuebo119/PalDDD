// ============================================================================
// new-skip-guard.cs——新增 Skip 标注守卫（Pal 会话审计 2026-09-25 立法）
//
// 动机：把失败测试改成 Skip 来修绿，是「改测试修绿」的隐性形态，test-change-guard
// 抓不到（它只拦「只改测试不改 src」的形态，正常 TDD 节奏下的 Skip 新增完全绕行）。
// 会话审计实证三案同源：Skip 16 个被报成 9 个（完成判定松散）；45 个 Testcontainers
// 测试长期不在「12 项目面板全绿」口径外未声明；方言测试本地 Skip 使全绿口径漂移。
// 数量棘轮需要运行时基线且本地/CI 跳过构成不同（Testcontainers 环境性跳过是运行时
// 判定，不是源码标注）——本守卫改抓「源码里新增 Skip 标注」这个静态动作：零基线、
// 误伤面天然为零（运行时跳过不进源码）、pre-commit 与 CI 均可判定。
//
// 判定（净增口径）：
//   拦截：暂存集 test/**.cs 的 unified diff 中，含 Skip 标注的新增行数 > 删除行数。
//         Skip 形态四种：[Skip("…")]（TUnit attribute）· Skip = "…" ·
//         .Skip("…")（Assert.Skip）· Skip.If( —— 注意 LINQ 的 events.Skip(1)
//         是序列分片不是测试跳过，形态 3 要求引号实参故零误伤。
//   放行：① 净增 ≤ 0（修改既有 Skip 的 reason / 删除 Skip 均放行——修绿的反向是好事）；
//         ② 显式豁免 ALLOW_NEW_SKIP=1（flaky 排查等正当场景，commit message 须写
//            理由与到期日——对齐 TechDebtGuardTests T-24 观察态纪律：无到期日的临时
//            Skip 就是会话审计里 Skip 9→16 的积累路径）。
//   已知不覆盖（如实声明，T-34）：字符串字面量内的 Skip 文本（如验证本门禁的元测试）
//   会误报，由豁免路径兜底；行内 // 之后文本被剥除可能漏报（漏报方向安全）。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/new-skip-guard.cs              按当前暂存集判定
//   dotnet run scripts/new-skip-guard.cs -- --selftest  自测（判定逻辑单元验证）
//
// 退出码：0=放行；1=拦截；2=仓库根定位失败。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 pre-commit 的
// 固定协议行（BLOCK/ALLOW 被人读与 grep 消费），固定中文非用户可配文案——
// 沿 test-change-guard.cs / secret-scan.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest"))
{
    return SelfTest();
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var patch = GitOutput("diff --cached -U0 -- test/");
var (netAdded, marks) = SkipLogic.Tally(patch);
var allowOverride = Environment.GetEnvironmentVariable("ALLOW_NEW_SKIP") == "1";
var (block, reason) = SkipLogic.Decide(netAdded, marks, allowOverride);

if (!block)
{
    Console.WriteLine($"PASS {reason}");
    return 0;
}

Console.WriteLine($"FAIL 检测到 test/** 新增 Skip 标注（净增 {netAdded} 行）——疑似以 Skip 修绿");
foreach (var m in marks.Take(10))
    Console.WriteLine($"  {m.File}:{m.LineNo}  {m.Text.Trim()}");
if (marks.Count > 10) Console.WriteLine($"  …另有 {marks.Count - 10} 处");
Console.WriteLine();
Console.WriteLine("运行时跳过（Testcontainers 守卫等）不受影响——本守卫只看源码标注。");
Console.WriteLine("若属正当场景（flaky 排查、平台特定），显式豁免并在提交信息写理由与到期日：");
Console.WriteLine("  ALLOW_NEW_SKIP=1 git commit …");
return 1;

// ══════════════ 判定（纯函数，供自测覆盖）══════════════

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

    const string sample =
        "diff --git a/test/Fake.Tests/T.cs b/test/Fake.Tests/T.cs\n" +
        "index 111..222 100644\n" +
        "--- a/test/Fake.Tests/T.cs\n" +
        "+++ b/test/Fake.Tests/T.cs\n" +
        "@@ -1,3 +1,4 @@\n" +
        "+[Skip(\"flaky 排查\")]\n" +
        " public class T { }\n" +
        "+// [Skip(\"注释行不算\")]\n" +
        "+var url = \"https://x.io/a\"; // 见 [Skip 文档]\n" +
        "+Assert.Skip(\"损坏帧\");\n";

    // 拦截路径
    var (net, marks) = SkipLogic.Tally(sample);
    Case("拦截：净增 Skip 标注", SkipLogic.Decide(net, marks, false).Block);
    Case("计数：attribute 与 Assert.Skip 计入、注释行与 // 后文本不计入", net == 2 && marks.Count == 2);
    Case("报告：含文件与真实行号（@@ 头解析，两条命中为第 1/4 行）",
        marks.Count == 2 && marks[0].LineNo == 1 && marks[1].LineNo == 4);

    // 放行路径
    Case("放行：无 Skip 净增", !SkipLogic.Decide(0, [], false).Block);
    Case("放行：净删除（修绿反向）", !SkipLogic.Decide(-1, [], false).Block);
    Case("放行：显式豁免", !SkipLogic.Decide(2, marks, true).Block);

    // 对称判定：改 reason（增 1 删 1）净 0 放行
    const string modify =
        "diff --git a/test/Fake.Tests/T.cs b/test/Fake.Tests/T.cs\n" +
        "+++ b/test/Fake.Tests/T.cs\n" +
        "@@ -1,1 +1,1 @@\n" +
        "-[Skip(\"旧原因\")]\n" +
        "+[Skip(\"新原因\")]\n";
    var (netM, _) = SkipLogic.Tally(modify);
    Case("放行：修改既有 Skip 的 reason（净 0）", netM == 0 && !SkipLogic.Decide(netM, [], false).Block);

    // 对称判定：删 1 加 3 净 +2 拦截（搬掩护不洗白）
    const string expand =
        "diff --git a/test/Fake.Tests/T.cs b/test/Fake.Tests/T.cs\n" +
        "+++ b/test/Fake.Tests/T.cs\n" +
        "@@ -1,1 +1,3 @@\n" +
        "-[Skip(\"a\")]\n" +
        "+[Skip(\"b\")]\n" +
        "+[Skip(\"c\")]\n" +
        "+Skip.If(true, \"d\");\n";
    var (netE, _) = SkipLogic.Tally(expand);
    Case("拦截：删 1 加 3 净增 2", netE == 2 && SkipLogic.Decide(netE, [], false).Block);

    // 形态精度：LINQ .Skip(1) 与 SkipIf 变量实参不误伤
    Case("形态：LINQ .Skip(1) 不算 Skip 标注", !SkipLogic.HasSkipMark("foreach (var e in events.Skip(1))"));
    Case("形态：Skip.If( 拦截", SkipLogic.HasSkipMark("Skip.If(!hasDocker, \"需要容器\");"));
    Case("形态：Skip = \"...\" 拦截", SkipLogic.HasSkipMark("Skip = \"实验性方言\";"));
    Case("形态：整行注释忽略", !SkipLogic.HasSkipMark("    // [Skip(\"历史\")](已移除)"));
    var mdTally = SkipLogic.Tally("diff --git a/docs/x.md b/docs/x.md\n+++ b/docs/x.md\n+[Skip(\"...\")]\n");
    Case("路径：非 .cs 文件忽略（docs 中的 Skip 文本）", mdTally.NetAdded == 0 && mdTally.AddedMarks.Count == 0);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}

// ══════════════ 类型声明（C# 顶级语句要求：全部语句之后）══════════════

internal sealed record SkipMark(string File, int LineNo, string Text);

internal static partial class SkipLogic
{
    // Skip 标注四形态。LinQ 的 .Skip(1) 被形态 3 的引号实参要求排除。
    // GeneratedRegex（源生成）而非运行时反射构造——AOT/启动成本双重友好。
    [GeneratedRegex(@"\[Skip\b|Skip\s*=\s*[""']|\.Skip\s*\(\s*[""']|\bSkip\.If\s*\(")]
    public static partial Regex SkipMarkRegex();

    /// <summary>一行代码是否含 Skip 标注。剥除行内 // 之后文本（URL 中的 // 会提前截断，
    /// 漏报方向安全）；以 // 或 * 开头的整行注释直接忽略。</summary>
    public static bool HasSkipMark(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*'))
            return false;
        var idx = t.IndexOf("//", StringComparison.Ordinal);
        if (idx >= 0) t = t[..idx];
        return SkipMarkRegex().IsMatch(t);
    }

    /// <summary>解析 unified diff（git diff -U0 输出），按文件统计含 Skip 标注的新增/删除行。
    /// 行号取自 @@ +l,c @@ 头的 + 侧真实行号——报告失实等于门禁失明。</summary>
    public static (int NetAdded, List<SkipMark> AddedMarks) Tally(string diffText)
    {
        var file = "";
        var lineNo = 0;
        int added = 0, removed = 0;
        var marks = new List<SkipMark>();

        foreach (var raw in diffText.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("diff --git", StringComparison.Ordinal))
            {
                file = "";
                continue;
            }
            if (raw.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                file = raw[6..];
                continue;
            }
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                // @@ -a,b +c,d @@ → + 侧起始行 c
                var m = HunkHeadRegex().Match(raw);
                lineNo = m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                continue;
            }
            if (raw.StartsWith("--- ", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal)
                || raw.StartsWith('\\'))
                continue;
            if (file.Length == 0 || !file.EndsWith(".cs", StringComparison.Ordinal))
                continue;
            if (raw.StartsWith('+'))
            {
                if (HasSkipMark(raw[1..])) { added++; marks.Add(new SkipMark(file, lineNo, raw[1..])); }
                lineNo++;
            }
            else if (raw.StartsWith('-'))
            {
                if (HasSkipMark(raw[1..])) removed++;
            }
        }
        return (added - removed, marks);
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)")]
    private static partial Regex HunkHeadRegex();

    public static (bool Block, string Reason) Decide(
        int netAdded, IReadOnlyList<SkipMark> marks, bool allowOverride)
    {
        if (netAdded <= 0)
            return (false, netAdded == 0
                ? "暂存集无 Skip 标注净增（增删相抵或无 Skip）"
                : "Skip 标注净删除——方向正确");
        if (allowOverride)
            return (false, $"ALLOW_NEW_SKIP=1 显式豁免（净增 {netAdded} 行 Skip 标注，提交信息须含理由与到期日）");
        return (true, $"test/** 新增 Skip 标注净增 {netAdded} 行");
    }
}
