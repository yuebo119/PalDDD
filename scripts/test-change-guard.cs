// ============================================================================
// test-change-guard.cs——测试文件改动守卫（ITM：2026-09-13 gate-audit 缺口 #5）
//
// 动机：AI 编码的结构性偏差之一是「改测试来修绿」——测试红了就改断言、改期望、
// 重录快照，而不是修生产代码。本仓库有 16 个测试项目 + 覆盖率门禁，但提交环节
// 没有任何守卫识别这一形态（.githooks 仅 secret-scan / encoding-gate / guard）。
// 本脚本补该缺口，作为 pre-commit 的第 4 道守卫。
//
// 判定（高精度优先——低精度门禁会被噪声淹没后失效，沿 secret-scan 设计纪律）：
//   拦截：暂存集含 test/** 的「修改/删除/重命名」，且不含任何 src/** 变更。
//         这是「只动测试不动生产代码」的签名，正常 TDD 必然同时动 src。
//   放行：① 同时有 src 变更（正常 TDD：改码 + 改测）；
//         ② 仅新增测试文件（为新代码补测属正常，不算改答案）；
//         ③ 显式豁免 ALLOW_TEST_ONLY_CHANGE=1（纯测试重构/修 flaky 等正当场景）。
//   只改文档/配置不触发（本守卫只看 src|test 两侧）。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/test-change-guard.cs              按当前暂存集判定
//   dotnet run scripts/test-change-guard.cs -- --selftest  自测（判定逻辑单元验证）
//
// 退出码：0=放行；1=拦截；2=仓库根定位失败。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 pre-commit 的
// 固定协议行（BLOCK/ALLOW 被人读与 grep 消费），固定中文非用户可配文案——
// 沿 secret-scan.cs / verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest"))
{
    return SelfTest();
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

// 暂存集中 test/** 的既有文件改动（M=修改 D=删除 R=重命名；不含 A=新增）
var riskyTestChanges = GitNameOnly("diff --cached --name-only --diff-filter=MDR -- test/")
    .Where(IsUnderTestDir)
    .ToList();

if (riskyTestChanges.Count == 0)
{
    Console.WriteLine("PASS 暂存集无 test/** 的修改或删除");
    return 0;
}

// 暂存集中的 src 变更（A/C/M/R 均计——新增生产代码同样说明改动是成对的）
var srcChanges = GitNameOnly("diff --cached --name-only --diff-filter=ACMR -- src/")
    .Where(IsUnderSrcDir)
    .ToList();

var allowOverride = Environment.GetEnvironmentVariable("ALLOW_TEST_ONLY_CHANGE") == "1";
var (block, reason) = Decide(riskyTestChanges, srcChanges, allowOverride);

if (!block)
{
    Console.WriteLine($"PASS {reason}");
    return 0;
}

Console.Error.WriteLine("FAIL 检测到「只改测试不改生产代码」——疑似改测试修绿");
foreach (var f in riskyTestChanges.Take(10)) Console.Error.WriteLine($"  {f}");
if (riskyTestChanges.Count > 10) Console.Error.WriteLine($"  …另有 {riskyTestChanges.Count - 10} 个");
Console.Error.WriteLine();
Console.Error.WriteLine("若这是正当改动（纯测试重构、修 flaky、补断言），显式豁免：");
Console.Error.WriteLine("  ALLOW_TEST_ONLY_CHANGE=1 git commit …");
Console.Error.WriteLine("若是为了让测试变绿而改测试，请改为修 src/——测试是证明，不是累赘。");
return 1;

// ══════════════ 判定（纯函数，供自测覆盖）══════════════

static (bool Block, string Reason) Decide(
    IReadOnlyList<string> modifiedOrDeletedTests,
    IReadOnlyList<string> srcChanges,
    bool allowOverride)
{
    if (modifiedOrDeletedTests.Count == 0)
        return (false, "暂存集无 test/** 的修改或删除");
    if (allowOverride)
        return (false, $"ALLOW_TEST_ONLY_CHANGE=1 显式豁免（{modifiedOrDeletedTests.Count} 个测试文件改动）");
    if (srcChanges.Count > 0)
        return (false, $"src/ 同步变更 {srcChanges.Count} 个——改动成对，非改测试修绿");
    return (true, $"test/** 改动 {modifiedOrDeletedTests.Count} 个且无 src/ 变更");
}

// ══════════════ git 交互 ══════════════

static List<string> GitNameOnly(string arguments)
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
        if (p is null) return [];
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0
            ? output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList()
            : [];
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return []; // git 不可用——守卫让行，不阻塞提交
    }
}

// 路径判定统一走 posix 形式（git 输出恒为 a/b 形式；本地 File 比较用不到）
static bool IsUnderTestDir(string path) =>
    path.StartsWith("test/", StringComparison.Ordinal);

static bool IsUnderSrcDir(string path) =>
    path.StartsWith("src/", StringComparison.Ordinal);

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

    // 拦截路径：只改测试，无 src
    Case("拦截：改测试且无 src 变更",
        Decide(["test/A.Tests/X.cs"], [], false).Block);

    // 放行路径
    Case("放行：有 src 同步变更",
        !Decide(["test/A.Tests/X.cs"], ["src/A/X.cs"], false).Block);
    Case("放行：无测试改动",
        !Decide([], [], false).Block);
    Case("放行：显式豁免",
        !Decide(["test/A.Tests/X.cs"], [], true).Block);

    // 边界：豁免不得掩盖真正的拦截语义（有 src 时豁免与否都应放行）
    Case("放行：有 src 时豁免状态不影响结论",
        !Decide(["test/A.Tests/X.cs"], ["src/A/X.cs"], true).Block);

    // 路径判定口径：只认 test/ 与 src/ 前缀，不误伤 docs/scripts
    Case("路径判定不误伤 docs/",
        !IsUnderTestDir("docs/test-coverage-baseline.md") && !IsUnderTestDir("scripts/test-gate.cs"));
    Case("路径判定命中 test/ 与 src/",
        IsUnderTestDir("test/PalDDD.Core.Tests/X.cs") && IsUnderSrcDir("src/PalDDD.Core/X.cs"));

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
