// ============================================================================
// test-gate.cs——Pal.DDD 测试规范门禁（MIG-012-A1，2026-09-11）
// 由 .ai/scripts/test-gate.sh 等价迁移为 C#（dotnet file-based app）。
// 检查项：T9（bench 存在性哨兵）/ T12（VersionPrefix）/ T-DEF-1 / T-DEF-4 +
// OSC 翻转检测段（Process 调 scripts/osc-check.cs，MIG-011c 已迁的 file-based app）。
// T4/T6/T8/T11 及 post-fix-check 三项已下沉 TestGateGuardTests（编号保留不重排）。
//
// 用法：dotnet run scripts/test-gate.cs -- [--oscillation-selftest]
//   --oscillation-selftest：透传 --selftest 给 osc-check.cs 并在 OSC 段后提前退出
// 退出码：0=通过；1=有违规（OSC 段子进程非零退出计入 FAIL，fail-loud 无静默 no-op）。
//
// ⚠️ 口径迁移说明（T-DEF-1，2026-09-11 MIG-012-A1）：
//   原版检查全部 shell 脚本头有 set -uo pipefail（bash 防御性头守卫）。MIG-012
//   门禁薄壳 .sh 全删后该检查对象消失（glob 空集恒 PASS = no-op 门，与 T9 退役
//   病根同族）。迁移口径：改为检查 scripts/ 下 5 个门禁薄壳 C# 脚本存在性
//   （gate/tech-debt/test-gate/doc-consistency/osc-check）——C# 脚本由编译器保证
//   类型/空安全，无需 set -uo 等价物；守卫的对象从"脚本防御头"迁移为
//   "迁移后薄壳全集在位"（防迁移遗漏/误删，与 T9 bench 哨兵同模式）。
//
// 等价迁移说明（其余）：
//   1) OSC 段：bash 的 $(cd ROOT && dotnet run scripts/osc-check.cs -- ...) 改为
//      Process 调用（工作目录=仓库根）；bash 引号包裹 glob 参数是为防 MSYS 路径
//      转换，C# Process 无 MSYS 层，ArgumentList 原样传参即可。
//   2) 时间行格式差异：bash $(date) 为 Git Bash locale 格式，C# 用固定格式——
//      留档 diff 时归一时间行。
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的固定
// 协议行（PASS/FAIL 关键字 + 中文口径说明），无本地化需求——沿 osc-check.cs 先例
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文输出对齐 bash UTF-8（沿 osc-check.cs 先例）
Console.OutputEncoding = Encoding.UTF8;

// 仓库根发现：从本 cs 源文件位置（编译期 CallerFilePath）向上找 PalDDD.slnx
//（等价 bash ROOT_DIR 推导，与调用方 cwd 无关）
var ROOT = FindRepoRoot();
var bar = new string('═', 63);   // 与 bash 版头尾分隔线等长（63 个 ═）
bool selftest = args.Contains("--oscillation-selftest");
int failCount = 0;

Console.WriteLine(bar);
Console.WriteLine(" Pal.DDD 测试规范门禁（T9/T12 + T-DEF-1/T-DEF-4）");
Console.WriteLine($" 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine(bar);

// ─── T9：BenchmarkCategory 同义词检查（规则退役，2026-09-11）───
// DDD 项目不使用 BenchmarkCategory（按文件分类），同义词规则无可承接判定。
// 保留编号占位，改为 bench 工程存在性哨兵：目录消失 = 基准面整体缺失（CI 不跑
// bench，无其他防线可见），须人工介入。
Console.WriteLine();
Console.WriteLine("─── T9: BenchmarkCategory 同义词（规则已退役·bench 存在性哨兵） ───");
if (Directory.Exists(Path.Combine(ROOT, "bench", "PalDDD.Benchmarks")))
{
    Console.WriteLine("PASS  T9  基准工程在位（同义词规则退役：DDD 按文件分类，bench 零 BenchmarkCategory）");
}
else
{
    Console.WriteLine("FAIL  T9  bench/PalDDD.Benchmarks 不存在——基准工程缺失，须人工确认");
    failCount++;
}

// ─── T-DEF-1：门禁薄壳 C# 脚本在位（口径迁移，见头注释 ⚠️）─────
Console.WriteLine();
Console.WriteLine("─── T-DEF-1: 门禁薄壳 C# 脚本在位（口径迁移：原 set -uo 检查随 .sh 全删退役） ───");
var gateShells = (string[])["gate.cs", "tech-debt.cs", "test-gate.cs", "doc-consistency.cs", "osc-check.cs"];
var missingShells = gateShells.Where(f => !File.Exists(Path.Combine(ROOT, "scripts", f))).ToList();
foreach (var f in missingShells)
    Console.WriteLine($"FAIL  T-DEF-1  scripts/{f} 缺失——门禁薄壳迁移遗漏，须人工确认");
if (missingShells.Count == 0)
{
    Console.WriteLine("PASS  T-DEF-1  门禁薄壳 C# 脚本全集在位（gate/tech-debt/test-gate/doc-consistency/osc-check）");
}
else
{
    failCount++;
}

// ─── CI timeout-minutes 检查（T-DEF-4）─────────────
// 锚 runs-on: 行计 job 数（每个 job 恰一条；原正则匹配 4 空格缩进数到的是
// steps:/services: 的元审计脚本#4 勘误口径）
Console.WriteLine();
Console.WriteLine("─── T-DEF-4: CI job timeout-minutes ───");
var runsOnRx = new Regex(@"^\s+runs-on:");
var ciFile = Path.Combine(ROOT, ".github", "workflows", "ci.yml");
var totalJobs = 0;
var timeouts = 0;
if (File.Exists(ciFile))
{
    foreach (var line in File.ReadLines(ciFile))
    {
        if (runsOnRx.IsMatch(line)) totalJobs++;
        if (line.Contains("timeout-minutes")) timeouts++;
    }
}
if (timeouts < totalJobs)
{
    Console.WriteLine($"FAIL  T-DEF-4  CI 有 {totalJobs} 个 job，仅 {timeouts} 个有 timeout-minutes");
    failCount++;
}
else
{
    Console.WriteLine($"PASS  T-DEF-4  所有 CI job 均有 timeout-minutes（{totalJobs} 个）");
}

// ─── T12: 版本管理一致性（DDD 用 Directory.Build.props 统一管）──────────────
Console.WriteLine();
Console.WriteLine("─── T12: 版本管理一致性（Directory.Build.props） ───");
var dbpFile = Path.Combine(ROOT, "Directory.Build.props");
var t12Ok = true;
if (File.Exists(dbpFile))
{
    if (!File.ReadAllText(dbpFile).Contains("VersionPrefix"))
    {
        Console.WriteLine("FAIL  T12  Directory.Build.props 缺少 VersionPrefix");
        failCount++;
        t12Ok = false;
    }
}
else
{
    Console.WriteLine("FAIL  T12  Directory.Build.props 不存在");
    failCount++;
    t12Ok = false;
}
if (t12Ok)
{
    var ver = new Regex(@"<VersionPrefix>[^<]+").Match(File.ReadAllText(dbpFile)).Value["<VersionPrefix>".Length..];
    Console.WriteLine($"PASS  T12  版本统一管理（Directory.Build.props VersionPrefix={ver}）");
}

// ─── OSC: 翻转检测器（unified v2.0 Phase 1c；MIG-011c 迁 scripts/osc-check.cs）───
// 数据源 TestResults/*.tunit-report.json；状态文件 .ai/gate/oscillation-state.json
//（gitignore）。同一测试连续 3 次观测呈 ABA → OSCILLATION 计入 FAIL。
// 调用形态：cd 仓库根 + 相对路径（C# Process 无 MSYS glob 转换问题，裸传即可）。
Console.WriteLine();
Console.WriteLine("─── OSC: 翻转检测器（同测试状态翻转两次） ───");
var oscPsi = new ProcessStartInfo("dotnet")
{
    WorkingDirectory = ROOT,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
oscPsi.ArgumentList.Add("run");
oscPsi.ArgumentList.Add("scripts/osc-check.cs");
oscPsi.ArgumentList.Add("--");
oscPsi.ArgumentList.Add(".ai/gate/oscillation-state.json");
oscPsi.ArgumentList.Add("TestResults/*.tunit-report.json");
if (selftest) oscPsi.ArgumentList.Add("--selftest");

var oscOut = "";
var oscRc = -1;
try
{
    using var oscProc = Process.Start(oscPsi)!;
    // stderr 异步读防管道死锁（bash $() 只捕获 stdout，stderr 直通终端——转发等价）
    var errTask = oscProc.StandardError.ReadToEndAsync();
    oscOut = oscProc.StandardOutput.ReadToEnd();
    oscProc.WaitForExit();
    Console.Error.Write(errTask.Result);
    oscRc = oscProc.ExitCode;
}
catch (Exception e) when (e is Win32Exception or InvalidOperationException)
{
    // dotnet 不可达 → 非零退出语义（fail-loud，无静默 no-op 路径——与 bash 版同口径）
    oscRc = -1;
}
// bash printf '%s\n' "$OSC_OUT" 语义：$() 去尾换行后补一个——等价 TrimEnd+WriteLine
Console.WriteLine(oscOut.TrimEnd('\r', '\n'));
if (selftest)
{
    // 自测模式提前退出（合成 ABA/AAA/AEA 三序列，ABA 必须唯一检出）
    return oscRc == 0 ? 0 : 1;
}
if (oscRc != 0)
{
    failCount++;
}

// ─── 总结 ────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine(bar);
Console.WriteLine($" 结果: {failCount} 失败");
if (failCount > 0)
{
    Console.WriteLine(" ❌ 门禁未通过——修复 FAIL 项后重试");
    return 1;
}
Console.WriteLine(" ✅ 测试规范门禁通过");
return 0;

// ─── 局部函数 ───

// 仓库根发现：从本 cs 源文件位置向上找含 PalDDD.slnx 的目录
static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string src = "")
{
    var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(src)
        ? Environment.CurrentDirectory
        : Path.GetDirectoryName(src)!);
    while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("未找到仓库根（PalDDD.slnx）——请从仓库内运行");
}
