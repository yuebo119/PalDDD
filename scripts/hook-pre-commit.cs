// ============================================================================
// hook-pre-commit.cs——pre-commit 钩子判定层（2026-10-08，C# 化替换 182 行 bash）
// 原 .githooks/pre-commit 的全部逻辑：暂存集快照 → 触发条件判定 → 门禁序列编排
// → FAIL 聚合。.githooks/pre-commit 退化为 3 行启动器（git hook 机制要求可执行
// 文件由 shell 引导，dotnet 无法直接充当 hook；判定实现 100% 在本文件）。
//
// 用法：dotnet run scripts/hook-pre-commit.cs（由 .githooks/pre-commit 启动器 exec）
// 退出码：0=通过；1=任一门禁失败。
// 门禁序列与触发条件逐条对应原 bash 版（ITM-671 建立、历次增补的 10 段编排）；
// 输出格式保持一致（emoji 段头 + ⛔ 失败行——grep 消费方稳定）。
//
// 原版第 14-18 行的 SIGPIPE 教训在此形态下结构性消解：C# 一次性快照暂存集 +
// 内存字符串匹配，无管道、无 SIGPIPE 面、无子 shell。
//
// --selftest：触发条件判定纯函数红绿矩阵（正负双向），不触真实仓库。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表；本脚本输出是钩子协议的固定中文行
//（与原 bash 版逐行一致，grep/人读消费），沿 verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return RunSelftest();
}

// ─── 仓库根定位（hook CWD 为 toplevel，slnx 探测双保险）───
var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var FAIL = 0;

// ─── 暂存集快照（触发条件判定用；一次性取全量/MDR/ACMR 三视图）───
var ALL = GitLines("--cached", "--name-only");
var MDR = GitLines("--cached", "--name-only", "--diff-filter=MDR");
var ACMR = GitLines("--cached", "--name-only", "--diff-filter=ACMR");

// ─── 1. secret-scan（凭据入库拦截，~2s）───
Console.WriteLine("🔒 secret-scan …");
FAIL += RunGate("scripts/secret-scan.cs", [], "⛔ secret-scan 失败——受跟踪文件发现疑似硬编码凭据");

// ─── 2. encoding-gate（编码一致性，~2s）───
Console.WriteLine("📐 encoding-gate …");
FAIL += RunGate("scripts/encoding-gate.cs", [], "⛔ encoding-gate 失败——CRLF/BOM/mojibake");

// ─── 2b. verify-ai（V1-V29 系统自检，~2s；仅 .ai 存在时跑——T-35：.ai 独立库
//         永不进 CI，本钩子是它唯一的自动执行面；clone 主仓无此目录故加守卫）───
if (Directory.Exists(".ai"))
{
    Console.WriteLine("🧭 verify-ai（V1-V29，检测到 .ai）…");
    FAIL += RunGate("scripts/verify-ai.cs", [], "⛔ verify-ai 失败——.ai 系统一致性校验未通过");
}

// ─── 3. guard.cs（8 道守卫 ~22s，仅 .cs 在暂存集时触发——纯文档提交不耗时）───
if (ALL.Any(p => p.EndsWith(".cs", StringComparison.Ordinal)))
{
    Console.WriteLine("🛡 guard.cs（.cs 在暂存集）…");
    FAIL += RunGate("scripts/guard.cs", [], "⛔ guard 失败——守卫测试红");
}

// ─── 4. test-change-guard（只改测试不改 src = 改测试修绿签名；test/ 的修改/删除
//         在暂存集时触发。豁免：ALLOW_TEST_ONLY_CHANGE=1）───
if (MDR.Any(p => p.StartsWith("test/", StringComparison.Ordinal)))
{
    Console.WriteLine("🧪 test-change-guard（test/ 有改动）…");
    FAIL += RunGate("scripts/test-change-guard.cs", [], "⛔ test-change-guard 拦截——见上方豁免说明");
}

// ─── 4b. new-skip-guard（test/**.cs 新增 Skip 标注 = 以 Skip 修绿的隐性形态；
//         净增口径。豁免：ALLOW_NEW_SKIP=1 + 提交信息写理由与到期日）───
if (ACMR.Any(p => p.StartsWith("test/", StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal)))
{
    Console.WriteLine("🚫 new-skip-guard（test/ .cs 有改动）…");
    FAIL += RunGate("scripts/new-skip-guard.cs", [], "⛔ new-skip-guard 拦截——新增强 [Skip] 须豁免并写到期日，或改为修 src");
}

// ─── 5. xml-guard（XML 良构性；xml 系扩展名入暂存集时触发。
//         实证：21549d3 在 .csproj 注释写 `--` 致全仓构建失败 MSB4025）───
if (ACMR.Any(IsXmlPath))
{
    Console.WriteLine("📄 xml-guard（xml 系文件有改动）…");
    FAIL += RunGate("scripts/xml-guard.cs", [], "⛔ xml-guard 拦截——XML 非良构，构建会失败");
}

// ─── 6. verify-conventions --quick（V5/V8/V9/V11；.md 入暂存集时触发 ~3s。
//         V9 来源：MIG 迁移后文档命令断链两次回归，改机械拦截）───
if (ACMR.Any(p => p.EndsWith(".md", StringComparison.Ordinal)))
{
    Console.WriteLine("📘 verify-conventions --quick（.md 在暂存集）…");
    FAIL += RunGate("scripts/verify-conventions.cs", ["--quick"], "⛔ verify-conventions 静态检查未通过");
}

// ─── 7. dapper-param-guard（Dapper 参数枚举直传；src/PalDDD.Dapper* 入暂存集时
//         触发。来源：CI #94——Dapper.AOT 枚举直传 PG 拒绝，本地 SQLite 盲区）───
if (ALL.Any(p => p.StartsWith("src/PalDDD.Dapper", StringComparison.Ordinal)))
{
    Console.WriteLine("🔧 dapper-param-guard（Dapper 源码在暂存集）…");
    FAIL += RunGate("scripts/dapper-param-guard.cs", [], "⛔ dapper-param-guard 失败——匿名参数含枚举直传（改 (int) 强转）");
}

// ─── 8. config-policy（dependabot 词表/结构 + Directory.Build Exec 副作用；
//         触发用 ALL 含删除：策略文件被删也触发，门禁 fail-closed 报"须显式裁决"）───
if (ALL.Any(p => p.EndsWith(".github/dependabot.yml", StringComparison.Ordinal)
    || p.EndsWith("Directory.Build.props", StringComparison.Ordinal)
    || p.EndsWith("Directory.Build.targets", StringComparison.Ordinal)))
{
    Console.WriteLine("⚙️ config-policy（dependabot/Directory.Build 有改动）…");
    FAIL += RunGate("scripts/config-policy.cs", [], "⛔ config-policy 拦截——dependabot 词表/结构或 Directory.Build Exec 副作用违规");
}

// ─── 9. license-policy（依赖许可；包清单入暂存时触发——许可由 nuspec 决定，
//         nuspec 只随包清单/版本变化。2026-09-25 裁决：只用开源许可）───
if (ALL.Any(p => p.EndsWith("Directory.Packages.props", StringComparison.Ordinal)
    || p.EndsWith(".csproj", StringComparison.Ordinal)))
{
    Console.WriteLine("📜 license-policy（包清单有改动）…");
    FAIL += RunGate("scripts/license-policy.cs", [], "⛔ license-policy 拦截——非白名单许可 / 禁令命中 / gap 到期 / 无法判定（见上方明细）");
}

// ─── 10. count-audit（文档计数声明与源码推导一致；README*/docs 入暂存时触发。
//          触发用 ACMR：删除声明文件无声明可失实，不触发；src 侧漂移由 CI 兜底）───
if (ACMR.Any(p => p.EndsWith("README.md", StringComparison.Ordinal)
    || p.EndsWith("README.en.md", StringComparison.Ordinal)
    || p.StartsWith("docs/", StringComparison.Ordinal)))
{
    Console.WriteLine("🔢 count-audit（文档计数声明核对）…");
    FAIL += RunGate("scripts/count-audit.cs", [], "⛔ count-audit 拦截——计数声明与推导真值不一致（或推导失败 fail-closed）");
}

if (FAIL != 0)
{
    Console.WriteLine();
    Console.WriteLine("⛔ pre-commit 拦截——跳过检查：git commit --no-verify（不推荐）");
    return 1;
}
Console.WriteLine("✅ pre-commit 通过");
return 0;

// ══════════════ static 局部函数 ══════════════

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
    Console.Error.WriteLine("错误：未定位到仓库根（无 PalDDD.slnx）");
    Environment.Exit(2);
    return "";
}

// git diff 快照：无暂存/异常返回空数组（与 bash `|| true` 兜底一致——快照失败不阻断提交，
// 门禁自身的失败才阻断）
static string[] GitLines(params string[] diffArgs)
{
    var args = new List<string> { "diff" };
    args.AddRange(diffArgs);
    var psi = new ProcessStartInfo("git", string.Join(' ', args))
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode == 0
        ? stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];
}

// 门禁执行：stdout/stderr 透传，非零退出返回 1 并打失败行（与 bash `if ! dotnet run …` 一致）
static int RunGate(string script, string[] extraArgs, string failLine)
{
    var arguments = $"run {script}" + (extraArgs.Length > 0 ? " -- " + string.Join(' ', extraArgs) : "");
    var psi = new ProcessStartInfo("dotnet", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };
    using var p = Process.Start(psi)!;
    Console.Out.Write(p.StandardOutput.ReadToEnd());
    Console.Error.Write(p.StandardError.ReadToEnd());
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.WriteLine(failLine);
        return 1;
    }
    return 0;
}

// xml 系扩展名判定（原 bash 逐 EndsWidth 的 or 链）
static bool IsXmlPath(string path) =>
    path.EndsWith(".csproj", StringComparison.Ordinal)
    || path.EndsWith(".props", StringComparison.Ordinal)
    || path.EndsWith(".slnx", StringComparison.Ordinal)
    || path.EndsWith(".targets", StringComparison.Ordinal)
    || path.EndsWith(".xml", StringComparison.Ordinal);

// ─── selftest：触发条件判定正负例（纯函数面，不触真实仓库）───
static int RunSelftest()
{
    var failures = new List<string>();

    void Case(bool actual, bool expected, string name)
    {
        if (actual == expected) Console.WriteLine($"PASS ST-HPC {name}");
        else failures.Add(name);
    }

    // xml 系判定：五扩展名正例 + 反例
    Case(IsXmlPath("a/B.csproj"), true, "csproj 触发");
    Case(IsXmlPath("a/b.targets"), true, "targets 触发");
    Case(IsXmlPath("a/b.slnx"), true, "slnx 触发");
    Case(IsXmlPath("a/b.props"), true, "props 触发");
    Case(IsXmlPath("a/b.xml"), true, "xml 触发");
    Case(IsXmlPath("a/b.cs"), false, "cs 不触发 xml");
    Case(IsXmlPath("a/b.csprojx"), false, "扩展名前缀不误触");

    // 触发条件组合判定（原 bash 各段条件的语义等价抽查）
    var sampleAll = new[] { "src/PalDDD.Dapper/foo.cs", "docs/x.md" };
    Case(sampleAll.Any(p => p.StartsWith("src/PalDDD.Dapper", StringComparison.Ordinal)), true, "Dapper 前缀触发（ALL）");
    Case(sampleAll.Any(p => p.EndsWith(".cs", StringComparison.Ordinal)), true, ".cs 存在触发 guard");
    var sampleMdr = new[] { "test/OldTests.cs" };
    Case(sampleMdr.Any(p => p.StartsWith("test/", StringComparison.Ordinal)), true, "test/ 删除触发 test-change-guard");
    var sampleAcmr = new[] { "docs/readme.md", "scripts/x.py" };
    Case(sampleAcmr.Any(p => p.EndsWith(".md", StringComparison.Ordinal)), true, ".md 触发 verify-conventions");
    Case(sampleAcmr.Any(p => p.EndsWith("Directory.Packages.props", StringComparison.Ordinal)), false, "非包清单不触发 license-policy");
    var packagesProps = new[] { "Directory.Packages.props" };
    Case(packagesProps.Any(p => p.EndsWith("Directory.Packages.props", StringComparison.Ordinal)), true, "包清单触发 license-policy");
    var testCs = new[] { "test/A.cs" };
    Case(testCs.Any(p => p.StartsWith("test/", StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal)), true, "test/**.cs 触发 new-skip-guard");
    var testMd = new[] { "test/A.md" };
    Case(testMd.Any(p => p.StartsWith("test/", StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal)), false, "test/**.md 不触发 new-skip-guard");

    Console.WriteLine($"═══════ HOOK-PRE-COMMIT SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var f in failures) Console.WriteLine("  FAIL " + f);
    return failures.Count == 0 ? 0 : 1;
}
