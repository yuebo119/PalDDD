// guard.cs — ITM-667：原子守卫命令（消除"改受影响项目漏跑守卫"的本地窗口）
// ═══════════════════════════════════════════════════════════════
// 用法：dotnet run scripts/guard.cs                # 串行跑全部守卫测试项目 + 注册完整性核查
//       dotnet run scripts/guard.cs -- --list      # 只列守卫项目与过滤器（不跑）
//       dotnet run scripts/guard.cs -- --selftest  # 自测（判定逻辑单元验证，不跑守卫测试）
// 退出码：0=全部守卫绿且注册完整；1=任一守卫红或存在未登记的守卫类；2=用法错误。
//
// 背景（v88，缺陷3→本地漏检窗口）：守卫测试分散在 3 个项目（Core.Tests/DI.Tests），
// 开发者"改哪跑哪"时跨项目守卫不触发——本命令一条跑全部，消除该窗口。
// 触发点：`.githooks/pre-commit`（`.cs` 入暂存集时）。CI 不需要本命令——Test 步骤
// 已跑全部测试；本工具的定位是**提交时**消除「改哪跑哪」的本地漏检窗口。
// ⚠️ 2026-09-13 勘正：原文写「本工具定位是本地 pre-push 快检」，与事实不符——
// `.githooks/pre-push` 调用的是 gate-lite.cs（G1-G3 快速门禁），不含本命令的 8 道守卫。
// ── 是否把本命令也挂到 pre-push：2026-09-13 裁决为**不挂** ──
// 理由：① 8 道守卫套件**本身就在 CI 的 Test 步骤内**（`dotnet test` 逐项目跑全量，
// 含 Core.Tests/DI.Tests/Compression.Tests），故绕过提交门禁的改动仍会被 CI 拦住
// ——pre-push 加挂只把检测提前，省一次 CI 往返，不改变"最终会被发现"；
// ② 代价是每次 push 多 ~22s（8 套串行），而唯一的收益场景是「有人用 --no-verify
// 提交」这一已被文档判为不推荐的用法；
// ③ 若将来要挂，最省的做法不是全量串行，而是按变更面选跑相关子集（pre-push
// 已知 diff 范围），但那是另一项工作，不在本次范围。
// 复核触发条件：若出现「CI 未拦住而 pre-push 本可拦住」的实例，重开此裁决。
//
// 2026-09-13 补两点（均由 gate-audit 的 UNVERIFIED 清单引出）：
//   1) 注册完整性核查：扫描 test/ 下「守卫命名形态」的测试类（*GateTests / *GuardTests /
//      ArchitectureBoundaryTests），与本清单比对。**起因（实测）**：CompressionGuardTests
//      （解压炸弹防护：输入上限/损坏输入/输出上限，16 测试）自 v2.1.0 起就存在，但本命令
//      创建时（v88）未纳入 → 本地 pre-commit 不跑该安全守卫，正是本命令要消除的漏检窗口。
//      有意只在 CI 跑者须登记进 ExemptGuardClasses 并写明理由（显式豁免，非静默跳过）。
//   2) --selftest：本命令判定逻辑此前无自证能力（gate-audit 矩阵标 UNVERIFIED）。

#pragma warning disable CA1303 // 守卫输出为 CI/终端协议关键字（PASS/FAIL/GREEN），固定英文非用户可配文案

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

// 守卫清单：项目 → treenode-filter
var guards = new (string Project, string Filter, string Name)[]
{
    ("test/PalDDD.Core.Tests/PalDDD.Core.Tests.csproj",
     "/*/*/SourceCodeGuardTests/*", "SourceCodeGuard（反射/OCE/阻塞/ConfigureAwait/异常sealed/时钟）"),
    ("test/PalDDD.DependencyInjection.Tests/PalDDD.DependencyInjection.Tests.csproj",
     "/*/*/ArchitectureBoundaryTests/*", "ArchitectureBoundary（分层/命名/csproj/AOT/命名规范）"),
    ("test/PalDDD.DependencyInjection.Tests/PalDDD.DependencyInjection.Tests.csproj",
     "/*/*/DocConsistencyGateTests/*", "DocConsistency（文档口径/计数锚/XML doc 覆盖）"),
    ("test/PalDDD.DependencyInjection.Tests/PalDDD.DependencyInjection.Tests.csproj",
     "/*/*/AssertionStrengthGateTests/*", "AssertionStrength（弱断言棘轮）"),
    ("test/PalDDD.DependencyInjection.Tests/PalDDD.DependencyInjection.Tests.csproj",
     "/*/*/TechDebtGuardTests/*", "TechDebtGuard（SuppressMessage/方言守卫对称/乐观锁对称）"),
    ("test/PalDDD.DependencyInjection.Tests/PalDDD.DependencyInjection.Tests.csproj",
     "/*/*/TestGateGuardTests/*", "TestGateGuard（DROP TABLE 账本/bench 配置/环境变量分离/post-fix）"),
    ("test/PalDDD.Core.Tests/PalDDD.Core.Tests.csproj",
     "/*/*/DiagnosticCoverageGateTests/*", "DiagnosticCoverage（38 条诊断断言级覆盖）"),
    ("test/PalDDD.Compression.Tests/PalDDD.Compression.Tests.csproj",
     "/*/*/CompressionGuardTests/*", "CompressionGuard（解压炸弹防护：输入上限/损坏输入/输出上限）"),
};

// 有意不纳入本命令的守卫命名类（须逐条写明理由——豁免是显式的，不是静默跳过）
var exemptGuardClasses = Array.Empty<string>();

if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return SelfTest(guards, exemptGuardClasses);
}

if (args.Contains("--list", StringComparer.Ordinal))
{
    Console.WriteLine("守卫项目清单（ITM-667）：");
    foreach (var (proj, filter, name) in guards)
        Console.WriteLine($"  {name}\n    → {proj} --treenode-filter \"{filter}\"");
    return 0;
}

Console.WriteLine($"═══════ guard.cs：{guards.Length} 道守卫串行 ═══════");
Console.WriteLine($"仓库根：{root}");
Console.WriteLine();

var failedGuards = new List<string>();
var sw = System.Diagnostics.Stopwatch.StartNew();

foreach (var (proj, filter, name) in guards)
{
    Console.Write($"  ▶ {name} … ");
    Console.Out.Flush();
    var psi = new ProcessStartInfo("dotnet", $"test \"{proj}\" --no-build -c Release --verbosity quiet -- --treenode-filter \"{filter}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
    };
    using var p = new Process { StartInfo = psi };
    p.Start();
    // 双流并行读防死锁（MIG-012 教训：单流串行读在缓冲满时挂起）
    var outTask = p.StandardOutput.ReadToEndAsync();
    var errTask = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    var stdout = outTask.Result;
    var errText = errTask.Result;

    // 判定仅看退出码即可，无需断言「测试数 > 0」——已实测（2026-09-13）：
    // MTP 对「过滤器匹配到 0 个测试」返回 exit 8，而非 0，故测试类被改名后本门禁会
    // 报 RED 而非假 GREEN（对照组：真实过滤器 总计 15 / exit 0）。
    // ⚠️ 该保护依赖 MTP 的退出码语义——若将来换回 VSTest 或改测试运行器，必须重测此行为。
    if (p.ExitCode == 0)
    {
        Console.WriteLine("GREEN");
    }
    else
    {
        Console.WriteLine("RED");
        failedGuards.Add(name);
        // 输出失败详情（截取最后 15 行——TUnit 失败摘要在尾部）
        var tail = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[..Math.Min(15, stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)];
        foreach (var l in tail) Console.WriteLine($"    {l.Trim()}");
        if (errText.Length > 0) Console.WriteLine($"    [stderr] {errText.Trim()[..Math.Min(200, errText.Trim().Length)]}");
    }
}

// ─── 注册完整性核查：发现未登记的守卫命名类（防本清单随时间漏登）───
// 实测起因：CompressionGuardTests 存在多时却未登记，本地 pre-commit 一直不跑它。
var unregistered = UnregisteredGuardClasses(root, guards, exemptGuardClasses);
if (unregistered.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  ⚠ 发现「守卫命名形态」但未登记的测试类（本地 pre-commit 不会跑它们）：");
    foreach (var c in unregistered) Console.WriteLine($"      {c}");
    Console.WriteLine("    → 二选一：登记进 guards 清单；或若有意只在 CI 跑，登记进 exemptGuardClasses 并写明理由。");
    foreach (var c in unregistered) failedGuards.Add($"未登记守卫类 {c}");
}

sw.Stop();
Console.WriteLine();
Console.WriteLine($"═══════ {(failedGuards.Count == 0 ? "全部守卫 GREEN" : $"{failedGuards.Count} 道守卫 RED")}（{sw.ElapsedMilliseconds}ms）═══════");
foreach (var f in failedGuards) Console.WriteLine($"  FAIL {f}");
if (failedGuards.Count > 0)
{
    Console.WriteLine("\n修复指引：dotnet run scripts/fix-orchestrator.cs（④段同构模式核查 + 回归清单）");
    return 1;
}
return 0;

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

// ══════════════ 注册完整性判定（纯函数，供 --selftest 覆盖）══════════════

// 守卫命名形态：与 guards 清单口径一致（*GateTests / *GuardTests / ArchitectureBoundaryTests）
static Regex GuardClassNamePattern() =>
    new(@"class\s+([A-Za-z0-9_]*(?:GateTests|GuardTests|ArchitectureBoundaryTests))\b");

// 从 filter 抽类名：`/*/*/SourceCodeGuardTests/*` → `SourceCodeGuardTests`
// （用正则而非 Split——`/*/` 作为分隔符在 `/*/*/X/*` 上只匹配开头一处，
//   经自测发现会得到 `*/X/*`，见本文件 --selftest 的「filter 提取类名」用例）
static List<string> RegisteredGuardClasses((string Project, string Filter, string Name)[] guards) =>
    guards.Select(g =>
        {
            var m = Regex.Match(g.Filter, @"/([A-Za-z0-9_]+)/\*$");
            return m.Success ? m.Groups[1].Value : "";
        })
        .Where(s => s.Length > 0)
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

// 差集：发现的守卫类中，既未登记也未豁免者（纯函数，便于自测）
static List<string> DiffGuardClasses(IEnumerable<string> discovered, IEnumerable<string> accounted) =>
    discovered.Where(c => !accounted.Contains(c, StringComparer.Ordinal))
              .OrderBy(c => c, StringComparer.Ordinal)
              .ToList();

// 从源码文本采集守卫类名；**跳过行注释**——注释里提到旧类名（如「原 XGuardTests 已删」）
// 不是声明，纳入会造成误报（低精度门禁比无门禁更坏）。
static List<string> GuardClassesInText(string text)
{
    var pattern = GuardClassNamePattern();
    var found = new List<string>();
    foreach (var line in text.Split('\n'))
    {
        if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
        foreach (Match m in pattern.Matches(line)) found.Add(m.Groups[1].Value);
    }
    return found;
}

// 扫描 test/ 下所有 .cs，收集守卫命名类，与「已登记 + 已豁免」比对
static List<string> UnregisteredGuardClasses(
    string root,
    (string Project, string Filter, string Name)[] guards,
    string[] exempt)
{
    var discovered = new SortedSet<string>(StringComparer.Ordinal);
    var testDir = Path.Combine(root, "test");
    if (Directory.Exists(testDir))
    {
        foreach (var file in Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories))
        {
            var posix = file.Replace('\\', '/');
            if (posix.Contains("/obj/") || posix.Contains("/bin/")) continue;
            foreach (var name in GuardClassesInText(File.ReadAllText(file, new UTF8Encoding(false, false))))
                discovered.Add(name);
        }
    }

    var accounted = new List<string>(RegisteredGuardClasses(guards));
    accounted.AddRange(exempt);
    return DiffGuardClasses(discovered, accounted);
}

// ══════════════ 自测 ══════════════

static int SelfTest(
    (string Project, string Filter, string Name)[] guards,
    string[] exempt)
{
    var passed = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    // filter → 类名提取
    var registered = RegisteredGuardClasses(guards);
    Case("filter 提取类名正确（SourceCodeGuardTests）", registered.Contains("SourceCodeGuardTests"));
    Case("filter 提取类名正确（ArchitectureBoundaryTests）", registered.Contains("ArchitectureBoundaryTests"));
    Case("提取结果不含 filter 残片", !registered.Any(s => s.Contains("/*")));

    // 本清单必须含 CompressionGuardTests——该条是本次修复自身的回归守卫：
    // 若将来有人把它从清单移除，本自测红（而门禁本身不会红，因为它只是少跑一项）
    Case("清单含 CompressionGuardTests（本次补漏的回归守卫）", registered.Contains("CompressionGuardTests"));
    Case("清单规模为 8 道", registered.Count == 8);

    // 差集判定
    Case("差集：未登记的守卫类被报出",
        DiffGuardClasses(["CompressionGuardTests", "NewGuardTests"], ["CompressionGuardTests"])
            is ["NewGuardTests"]);
    Case("差集：全部已登记时为空",
        DiffGuardClasses(["A_GuardTests"], ["A_GuardTests"]).Count == 0);
    var oneGuard = new[] { "A_GuardTests" };
    Case("差集：豁免项不计入",
        DiffGuardClasses(["A_GuardTests"], oneGuard.Concat(exempt)).Count == 0);
    Case("差集：输出按序稳定",
        DiffGuardClasses(["Z_GuardTests", "A_GuardTests"], Array.Empty<string>()) is ["A_GuardTests", "Z_GuardTests"]);

    // 类名形态识别（正/反例）
    Case("识别 *GuardTests 声明", GuardClassesInText("public sealed class CompressionGuardTests").Contains("CompressionGuardTests"));
    Case("识别 ArchitectureBoundaryTests 声明", GuardClassesInText("internal class ArchitectureBoundaryTests").Contains("ArchitectureBoundaryTests"));
    Case("不误报普通测试类", GuardClassesInText("public sealed class CompressionTests").Count == 0);
    Case("不采集行注释里提到的旧类名（防误报）", GuardClassesInText("// 原 OldGuardTests 已随 MIG 删除").Count == 0);
    Case("采集块内声明行", GuardClassesInText("{\n    public sealed class A_GuardTests\n}").Contains("A_GuardTests"));

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
