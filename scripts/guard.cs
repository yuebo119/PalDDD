// guard.cs — ITM-667：原子守卫命令（消除"改受影响项目漏跑守卫"的本地窗口）
// ═══════════════════════════════════════════════════════════════
// 用法：dotnet run scripts/guard.cs            # 串行跑全部守卫测试项目
//       dotnet run scripts/guard.cs -- --list # 只列守卫项目与过滤器（不跑）
// 退出码：0=全部守卫绿；1=任一守卫红；2=用法错误。
//
// 背景（v88，缺陷3→本地漏检窗口）：守卫测试分散在 3 个项目（Core.Tests/DI.Tests），
// 开发者"改哪跑哪"时跨项目守卫不触发——本命令一条跑全部，消除该窗口。
// CI 不需要本命令（Test 步骤已跑全部测试）——本工具定位是本地 pre-push 快检。

#pragma warning disable CA1303 // 守卫输出为 CI/终端协议关键字（PASS/FAIL/GREEN），固定英文非用户可配文案

using System.Diagnostics;
using System.Text;

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
};

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
