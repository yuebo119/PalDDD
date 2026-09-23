// ============================================================================
// ci-coverage.cs——CI 覆盖率 + 报告生成 + 全局阈值门禁（MIG-012-C，2026-09-11）
// 由仓库根 ci-coverage.sh（80 行 bash）等价迁移为 C#（dotnet file-based app）。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/ci-coverage.cs --            全链路：build → 逐项目
//       test --coverage → reportgenerator 合并 → line-rate 阈值门禁
//       → 单模块降幅门禁（baseline ±5pp，2026-09-14 增）
//   dotnet run scripts/ci-coverage.cs -- --selftest 自测：XML 解析 + 阈值路由
//       + 降幅判定（构造最小 Cobertura XML / 基线解析，不真跑 dotnet coverage）
//   dotnet run scripts/ci-coverage.cs -- --update-baseline
//       用当前 TestResults 产物重写 coverage-baseline.json（校准入口；改基线需评审）
//
// 环境变量：COVERAGE_THRESHOLD——全局行覆盖率阈值（默认 0.70，本地放宽/CI 收紧）
// 产物：TestResults/coverage.<项目名>.cobertura.xml + TestResults/coverage-report/
// 退出码：0=门禁通过；1=阈值未达/合并报告缺失/解析失败（fail-closed）；
//         子命令（build/test/restore/合并）失败时透传其退出码（set -e 语义）。
//
// 迁移说明：
//   1) 门禁历史（评审 P1-2）保持：test-coverage-baseline.md 曾声称 65% 门禁但从未
//      实现（守卫空转），本脚本实现全局行覆盖率阈值；单模块 5% 降幅规则仍靠
//      评审轮人工核对基线表；CI 集成为后续项（全量收集显著拉长流水线）。
//   2) MTP 手写协议保持：一次一个测试项目 + MTP 原生 --coverage；旧写法（slnx
//      批量 + --collect:"XPlat Code Coverage"）触发 VSTest 握手 exit 5
//      （2026-08-16 终验轮 B-2 实测复现）。PalDDD.Testing 为支持库非测试项目。
//   3) line-rate 提取由 grep 双段管道改 XDocument 解析根元素 line-rate 属性。
//      原脚本 `|| true` 兜底语义（ITM 修复史：无 pipefail 下 grep 无匹配使
//      set -e 提前退出、友好报错分支不可达）保持为：提取失败 → null →
//      显式 fail-closed 退出 1 并给出可读信息，控制流与原判定分支一致。
//   4) 阈值比较由 awk 浮点比较改 double 比较（InvariantCulture）；阈值非数字
//      时本版 fail-closed（awk 对非数字串会走字典序回退，行为怪异且从未被
//      依赖——文档化差异，更严格）。
//   5) 零 package 依赖——System.Xml.Linq 为框架内置，不写 #:package（NU1510
//      即错误）。
// ============================================================================

#pragma warning disable CA1303 // CI 门禁协议输出为固定控制台文案（中英文混排），无本地化需求——沿 flaky-parse.cs 先例

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

// Windows 控制台默认编码非 UTF-8，中文/箭头字符会乱码——对齐 bash UTF-8 输出；
// 重定向行尾默认 \r\n（bash 为 \n），统一为 \n 保证双跑逐字节可比
Console.OutputEncoding = Encoding.UTF8;
Console.Error.NewLine = "\n";   // 门禁失败/错误信息行尾对齐 bash（\n）
Console.Out.NewLine = "\n";

// ─── 参数路由：--selftest 自测模式（单元级验证，不真跑 dotnet coverage）───
if (args.Contains("--selftest"))
{
    return SelfTest();
}

// ─── 参数路由：--update-baseline（用当前产物重写基线；校准入口）───
if (args.Contains("--update-baseline"))
{
    return UpdateBaseline("coverage-baseline.json", "TestResults");
}

// ─── 参数路由：--enforce-only（仅跑第 5/6 步门禁，跳过 build/test/merge）───
// 用途：① 隔离式变异探针（gate-audit 的 ci-coverage 探针预置合并报告后直达阈值判定，
// 否则前四步在空仓必然失败，探针永远到不了被测逻辑——等于没探）；② 本地复跑门禁
// 判定而不用重跑整个测试套件。合并报告不存在时 fail-closed（与第 5 步同口径）。
// 阈值可被环境变量覆盖（本地放宽/CI 收紧）——与原脚本 ${COVERAGE_THRESHOLD:-0.70} 一致
var (thresholdRaw, threshold) = ReadThreshold();
if (threshold is null)
{
    Console.Error.WriteLine($"ERROR: COVERAGE_THRESHOLD 非数字: {thresholdRaw}");
    return 1;
}

// ─── 参数路由：--enforce-only（仅跑第 5/6 步门禁，跳过 build/test/merge）───
// 用途：① 隔离式变异探针（gate-audit 的 ci-coverage 探针预置合并报告后直达阈值判定，
// 否则前四步在空仓必然失败，探针永远到不了被测逻辑——等于没探）；② 本地复跑门禁
// 判定而不用重跑整个测试套件。合并报告不存在时 fail-closed（与第 5 步同口径）。
if (args.Contains("--enforce-only"))
{
    Console.WriteLine(">> --enforce-only: skipping build/test/merge (using existing merged report)");
}
else
{

Console.WriteLine("=== Pal.DDD CI Coverage ===");

// 1. 构建（输出直接透传终端——原脚本未捕获；非零退出码透传 = set -e 语义）
Console.WriteLine(">> Building...");
var buildExit = RunInherit("dotnet", "build PalDDD.slnx --nologo -v q");
if (buildExit != 0) return buildExit;

// 2. 测试 + 覆盖率收集（MTP 手写协议——见头注释迁移说明 2）
// ⚡ 并行化（2026-09-23）：本步骤实测 591s，是整轮 CI 的**唯一关键路径**（coverage 647s >
// build-and-test 505s）——墙钟提速的最后一个杠杆（build-and-test 侧的 Test 循环已并行化）。
// 并行度保守起步 = 2，理由同 ci.yml 的 Test 步骤：仓库有墙钟敏感的超时护栏
// （OutboxProcessorTests 的 WhenAny(stopTask, Delay(3s))，见 T-21 注记），争用加剧可能触发
// 误报；稳定若干轮后可上调此常量。
// 语义保留与变化：① 任一项目失败 → 整体返回其退出码（**不再 fail-fast**——并发下 fail-fast
// 会掩盖其余失败，改为跑完全部再汇总，与 ci.yml 的 Test 步骤同款）；② 每项目 cobertura
// 文件名按项目名唯一 ⇒ 并行写互不冲突；③ 输出改为**逐项目捕获后按序打印**——并发下继承
// stdout 会交错，失败日志无法归属到项目。
Console.WriteLine(">> Running tests with coverage...");
Directory.CreateDirectory("TestResults");
var testProjects = FindTestProjects().ToList();
const int coverageParallelism = 2;
var covResults = new (string Name, int Exit, string Output)[testProjects.Count];
Parallel.For(0, testProjects.Count,
    new ParallelOptions { MaxDegreeOfParallelism = coverageParallelism },
    i =>
    {
        var name = Path.GetFileNameWithoutExtension(testProjects[i]);
        covResults[i] = RunOne(testProjects[i], name);
    });
foreach (var r in covResults)
{
    Console.WriteLine($">> {r.Name}: exit={r.Exit}");
    if (r.Output.Length > 0) Console.WriteLine(r.Output);
}
var failedTest = covResults.FirstOrDefault(r => r.Exit != 0);
if (failedTest.Name is not null) return failedTest.Exit;

static (string Name, int Exit, string Output) RunOne(string csproj, string name)
{
    var (exit, output) = RunCapture("dotnet",
        $"test {csproj} --nologo --no-build -v q --coverage" +
        $" --coverage-output TestResults/coverage.{name}.cobertura.xml" +
        " --coverage-output-format cobertura");
    return (name, exit, output);
}

// 3. 恢复本地工具清单（固定 ReportGenerator 版本，见 .config/dotnet-tools.json）
Console.WriteLine(">> Restoring local tools...");
var restoreExit = RunInherit("dotnet", "tool restore");
if (restoreExit != 0) return restoreExit;

// 4. 合并报告（Cobertura 供第 5 步门禁解析，Html 供人工审阅；glob 由 reportgenerator 自行展开）
Console.WriteLine(">> Merging coverage reports...");
// ⚠️ 路径缺陷修复(2026-09-14 实测实证):MTP 的 --coverage-output 相对路径基准是
// 测试结果根(TestResults/),故第 2 步传入 "TestResults/coverage.X.xml" 实际落在
// TestResults/TestResults/coverage.X.xml——单层 glob "TestResults/coverage.*" 实测
// "found no matching files"(reportgenerator 退出非零,fail-closed 卡在合并步)。
// 递归 glob 兼容双层落点(实测合并成功);若未来 MTP 变更落点基准仍兼容。
var mergeExit = RunInherit("dotnet",
    "tool run reportgenerator" +
    " -reports:TestResults/**/coverage.*.cobertura.xml" +
    " -targetdir:TestResults/coverage-report" +
    " -reporttypes:Html;Cobertura");
if (mergeExit != 0) return mergeExit;
}

// 5. 全局行覆盖率门禁（fail-closed，评审 P1-2）
//    解析合并后 Cobertura 顶层 <coverage line-rate="0.xxx">——该值是
//    ReportGenerator 对全部模块行覆盖的加权汇总，与基线口径一致。
Console.WriteLine($">> Enforcing global line coverage threshold: {thresholdRaw}");
var mergedCobertura = "TestResults/coverage-report/Cobertura.xml";
if (!File.Exists(mergedCobertura))
{
    Console.Error.WriteLine($"ERROR: merged cobertura report not found at {mergedCobertura}");
    return 1;
}
var lineRateRaw = ExtractLineRate(mergedCobertura);
if (lineRateRaw is null)
{
    Console.Error.WriteLine($"ERROR: could not parse line-rate from {mergedCobertura} (report format drift?)");
    return 1;
}
Console.WriteLine($">> Global line coverage: {lineRateRaw} (threshold {thresholdRaw})");
if (double.Parse(lineRateRaw, NumberStyles.Float, CultureInfo.InvariantCulture) < threshold)
{
    Console.Error.WriteLine($"FAIL: global line coverage {lineRateRaw} is below threshold {thresholdRaw}");
    return 1;
}

// 6. 单模块覆盖率降幅门禁（账本 §四「单模块降幅 ≤5%」；2026-09-14 增）
//    同项目跨时间可比（同一测试集插桩同一装配集）；项目间不可比（lines-valid 各异）
Console.WriteLine(">> Enforcing per-module coverage drop limit (baseline ±5pp)...");
var dropExit = EnforceModuleDropLimit("coverage-baseline.json", "TestResults");
if (dropExit != 0) return dropExit;

Console.WriteLine("=== Coverage complete (gate PASSED) ===");
Console.WriteLine("Report: TestResults/coverage-report/index.html");
return 0;
// ─── 子进程执行：stdout/stderr 捕获返回（并行循环专用）───
// 与 RunInherit 的区别：并发下继承终端会让各项目输出交错，失败日志无法归属到项目——
// 故改为逐项目捕获、由调用方按序打印。
// stderr 必须**并发排空**：RedirectStandardError 是管道，不排空时子进程写满缓冲即与父进程的
// stdout ReadToEnd 互等（沿 fix-orchestrator.cs 的同款教训——dotnet 在 stderr 上输出很吵）。
static (int ExitCode, string Output) RunCapture(string fileName, string arguments)
{
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    using var process = Process.Start(psi)!;
    var stderr = new StringBuilder();
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
    process.BeginErrorReadLine();
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    process.WaitForExit();   // 双调用：确保异步缓冲 flush（沿 check-all.cs 先例）
    return (process.ExitCode, stdout + stderr);
}

// ─── 子进程执行：stdout/stderr 继承终端（对齐原脚本未捕获的 dotnet 调用）───
// ITM-663：stdout/stderr 均继承终端——coverage 工具输出即结果，控制台直出可观察（刻意取舍）
static int RunInherit(string fileName, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;
    process.WaitForExit();
    return process.ExitCode;
}

// 静默捕获 stdout（新鲜度检查用——git 日期查询，失败返回 null 由调用方跳过）
static string? RunGitQuiet(string arguments)
{
    using var process = Process.Start(new ProcessStartInfo("git", arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? output : null;
}

// ─── 测试项目枚举：对齐 find test -name '*.csproj' ! -name 'PalDDD.Testing.csproj' | sort ───
// 路径均为 ASCII，Ordinal 与 bash sort（字节序）等价
static List<string> FindTestProjects() =>
    Directory.EnumerateFiles("test", "*.csproj", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != "PalDDD.Testing.csproj")
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();

// ─── 阈值读取：环境变量覆盖 + 默认 0.70；非数字返回 null（fail-closed）───
// 2026-09-14 由 0.65 上调至 0.70：阈值口径沿用项目原始设计原则「基线 − 3pp 缓冲」，
// 而原值 0.65 锚定的是 2026-07-30 基线 67.9%（67.9 − 3 ≈ 65）。2026-09-14 实测
// 16/16 测试项目并集行覆盖率 **72.98%**（13146/18013，Debug 插桩；该值为**下界**——
// 其中 PalORM.Tests 因本机无 Docker 有 46 项未跑完，CI 上会更高），按同一原则取 0.70。
// 原 0.65 在新基线下留有约 8pp 余量，等于要一次掉 8 个百分点才触发，已失去早期预警意义。
// ⚠️ 复校准触发：首次 CI 运行会给出含 Docker 的 16 项目完整值，届时按其值重校准
// （预期 ≥ 0.73），并同步 docs/test-coverage-baseline.md §门禁阈值。
static (string Raw, double? Value) ReadThreshold()
{
    var raw = Environment.GetEnvironmentVariable("COVERAGE_THRESHOLD");
    if (string.IsNullOrEmpty(raw)) raw = "0.70";
    // 全仓扫描修复（fail-open）：NumberStyles.Float 自 .NET Core 3.0 起接受 "NaN"/"Infinity"，
    // 而 NaN 使 `lineRate < NaN` 恒假 → 阈值门禁静默放行（Infinity 则恒不可通过）。
    // 非有限值/越界一律走 null 路径（调用方 fail-closed），与「非数字 fail-closed」同口径。
    return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
        && value > 0
        && value <= 1
        ? (raw, value)
        : (raw, null);
}

// ─── line-rate 提取：XDocument 解析根元素属性（任务指定，框架内置）───
// 等价原脚本 grep 链：首个 <coverage 开标签内的 line-rate="数字.数字"，
// 取其属性值原串（保留 "0.6500" 等原始形式用于输出）。
// 无法提取（文件不可读/XML 损坏/根无 coverage line-rate 数字属性）→ null，
// 调用方显式 fail-closed（对齐 `|| true` 兜底 + 空串判定的控制流）。
static string? ExtractLineRate(string coberturaPath)
{
    try
    {
        var root = XDocument.Load(coberturaPath).Root;
        // 对齐原 grep '<coverage[^>]*line-rate='：仅根元素名为 coverage 时提取
        var attr = root?.Name.LocalName == "coverage" ? root.Attribute("line-rate")?.Value : null;
        // 全仓扫描修复（fail-open）：line-rate="NaN" 能被 TryParse 接受，随后
        // CheckModuleDrop 的 `baseline - NaN > 0.05` 恒假 → 模块降幅门禁放行。
        // 非有限值视同解析失败 → 调用方 ERROR + fail-closed。
        if (attr is not null
            && double.TryParse(attr, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
            && double.IsFinite(rate))
        {
            return attr;
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
    {
        // 读不了/坏 XML → 走 null 路径（原脚本 grep 无匹配 → 空串路径）
    }
    return null;
}

// ─── 单模块降幅门禁：基线 JSON vs 当前逐项目 cobertura（容差 5pp 绝对降幅）───
// 基线缺失/解析空 → fail-closed（基线是仓库文件，缺失=仓库异常，不得静默跳过）；
// 基线有、当前无数据 → WARN（CI 全项目有数据；缺数据不阻断但必须可见）。
static int EnforceModuleDropLimit(string baselinePath, string resultsRoot)
{
    if (!File.Exists(baselinePath))
    {
        Console.Error.WriteLine($"ERROR: baseline {baselinePath} not found (fail-closed)");
        return 1;
    }
    // v2 审计 T-3（主题 4「证据新鲜度即门禁」）：基线年龄检查——旧基线上的降幅门禁
    // 不构成证据（v2 实证：7 周陈旧基线 + 最低覆盖区恰是生产 SQL 栈）。读基线文件的
    // git 最后提交日期（JSON 内无字段时也覆盖旧格式）；>30 天 → ::warning 提示重测
    //（不 fail：CI 无法自动重测会死锁，重测走 --update-baseline 校准入口）
    try
    {
        var gitDate = RunGitQuiet($"log -1 --format=%as -- {baselinePath}");
        if (DateTime.TryParse(gitDate?.Trim(), out var generated) &&
            (DateTime.UtcNow.Date - generated).TotalDays > 30)
            Console.WriteLine($"::warning::coverage 基线已 {(DateTime.UtcNow.Date - generated).Days} 天未更新（{generated:yyyy-MM-dd}）——降幅门禁的比对数据可能失真，请跑 dotnet run scripts/ci-coverage.cs -- --update-baseline 重测（v2 审计 T-3）");
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    { /* git 不可用/启动失败时跳过新鲜度检查，不阻塞门禁本体（窄化 catch 免 CA1031） */ }
    var baselines = ReadModuleBaselines(baselinePath);
    if (baselines.Count == 0)
    {
        Console.Error.WriteLine($"ERROR: baseline {baselinePath} parsed empty (fail-closed)");
        return 1;
    }
    var failed = 0;
    foreach (var (proj, baseline) in baselines)
    {
        var currentFile = FindCurrentCobertura(resultsRoot, proj);
        if (currentFile is null)
        {
            Console.WriteLine($"  WARN {proj}: 当前无 cobertura（跳过比对）");
            continue;
        }
        var rateRaw = ExtractLineRate(currentFile);
        if (rateRaw is null)
        {
            Console.Error.WriteLine($"ERROR: {proj} cobertura 解析失败: {currentFile} (fail-closed)");
            failed++;
            continue;
        }
        var current = double.Parse(rateRaw, NumberStyles.Float, CultureInfo.InvariantCulture);
        var verdict = CheckModuleDrop(baseline, current);
        Console.WriteLine($"  {(verdict == 0 ? "PASS" : "FAIL")} {proj}: baseline {baseline.ToString("P2", CultureInfo.InvariantCulture)} → {current.ToString("P2", CultureInfo.InvariantCulture)}（降幅 {((baseline - current) * 100).ToString("F2", CultureInfo.InvariantCulture)}pp）");
        if (verdict != 0) failed++;
    }
    if (failed > 0)
    {
        Console.Error.WriteLine($"FAIL: {failed} 个模块覆盖率降幅超过 5pp 容差");
        return 1;
    }
    return 0;
}

// 纯判定：降幅超过容差（5pp 绝对）即违规——抽纯函数供 --selftest 与变异验证。
// epsilon（1e-9）吸收浮点噪声：0.80−0.75 在 double 下为 0.050000000000000044，
// 无 epsilon 时「恰好等于容差」被误判违规（本判定边界语义：等于容差放行——自测用例锁定）。
static int CheckModuleDrop(double baseline, double current)
    => !double.IsFinite(baseline) || !double.IsFinite(current)
        ? 1   // 纵深防御：非有限值判违规（NaN 会让比较恒假而静默放行）
        : baseline - current > 0.05 + 1e-9 ? 1 : 0;

// 基线读取：flat JSON 逐行 "proj": rate（手写解析规避 AOT 反射序列化禁用；"//" 注释行跳过）
static Dictionary<string, double> ReadModuleBaselines(string path)
{
    var result = new Dictionary<string, double>(StringComparer.Ordinal);
    foreach (var line in File.ReadAllLines(path))
    {
        var m = Regex.Match(line, "^\\s*\"([^\"]+)\"\\s*:\\s*([0-9.]+)\\s*,?\\s*$");
        if (m.Success && !m.Groups[1].Value.StartsWith("//", StringComparison.Ordinal)
            && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            result[m.Groups[1].Value] = rate;
        }
    }
    return result;
}

// 当前项目 cobertura 查找（兼容 MTP 双层落点——277bc34 教训：递归搜索）
static string? FindCurrentCobertura(string resultsRoot, string project)
{
    var name = $"coverage.{project}.cobertura.xml";
    foreach (var f in Directory.EnumerateFiles(resultsRoot, name, SearchOption.AllDirectories))
        return f;
    return null;
}

// 基线更新入口（--update-baseline）：用当前产物重写基线文件（校准步骤，需评审后提交）
static int UpdateBaseline(string baselinePath, string resultsRoot)
{
    var rates = new SortedDictionary<string, double>(StringComparer.Ordinal);
    foreach (var f in Directory.EnumerateFiles(resultsRoot, "coverage.*.cobertura.xml", SearchOption.AllDirectories))
    {
        var name = Path.GetFileName(f);
        var proj = name["coverage.".Length..^".cobertura.xml".Length];
        var rateRaw = ExtractLineRate(f);
        if (rateRaw is null)
        {
            Console.Error.WriteLine($"ERROR: {f} 解析失败，拒绝生成不完整基线 (fail-closed)");
            return 1;
        }
        rates[proj] = Math.Round(double.Parse(rateRaw, NumberStyles.Float, CultureInfo.InvariantCulture), 4);
    }
    if (rates.Count == 0)
    {
        Console.Error.WriteLine($"ERROR: {resultsRoot} 下无 cobertura 产物，无法生成基线 (fail-closed)");
        return 1;
    }
    var sb = new StringBuilder();
    sb.AppendLine("{");
    sb.AppendLine("  \"//\": \"单模块覆盖率基线(A6 降幅门禁,容差 5pp)——键=测试项目名,值=line-rate;由 ci-coverage --update-baseline 更新(校准需评审)\",");
    var i = 0;
    foreach (var (proj, rate) in rates)
        sb.AppendLine($"  \"{proj}\": {rate.ToString(CultureInfo.InvariantCulture)}{(++i < rates.Count ? "," : "")}");
    sb.Append('}');
    File.WriteAllText(baselinePath, sb.ToString());
    Console.WriteLine($"基线已更新：{rates.Count} 项目 → {baselinePath}");
    Console.WriteLine($"  _generatedAt = {DateTime.UtcNow:yyyy-MM-dd}（新鲜度锚，v2 审计 T-3 主题——比对步骤据此警示过期基线）");
    foreach (var (proj, rate) in rates)
        Console.WriteLine($"  {proj}: {rate.ToString("P2", CultureInfo.InvariantCulture)}");
    return 0;
}

// ─── 自测：构造最小 Cobertura XML 验证解析 + 阈值路由单元 ───
// 覆盖：常规提取 / 无属性 / 根非 coverage / 整数形式 / 边界等于阈值 /
//       低于阈值判定 / 环境变量路由（覆盖值·默认值·非法值 fail-closed）
static int SelfTest()
{
    var passed = 0;
    var total = 0;
    var tmp = Path.Combine(Path.GetTempPath(), "ci-coverage-selftest-" + Guid.NewGuid().ToString("N") + ".xml");

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    try
    {
        // 用例 1：常规 line-rate 提取（属性原串保留）
        File.WriteAllText(tmp, "<?xml version=\"1.0\"?><coverage line-rate=\"0.72\" branch-rate=\"0.5\"></coverage>");
        Case("line-rate 提取 0.72", ExtractLineRate(tmp) == "0.72");

        // 用例 2：整数形式（[0-9.]* 允许无小数点）
        File.WriteAllText(tmp, "<?xml version=\"1.0\"?><coverage line-rate=\"1\"></coverage>");
        Case("line-rate 整数形式提取 1", ExtractLineRate(tmp) == "1");

        // 用例 3：无 line-rate 属性 → null（fail-closed 路径）
        File.WriteAllText(tmp, "<?xml version=\"1.0\"?><coverage branch-rate=\"0.5\"></coverage>");
        Case("无 line-rate 属性返回 null", ExtractLineRate(tmp) is null);

        // 用例 4：根元素非 coverage → null（grep 版同样找不到 <coverage 开标签）
        File.WriteAllText(tmp, "<?xml version=\"1.0\"?><report line-rate=\"0.9\"></report>");
        Case("根非 coverage 返回 null", ExtractLineRate(tmp) is null);

        // 用例 5：坏 XML → null
        File.WriteAllText(tmp, "<coverage line-rate=\"0.5\"");
        Case("坏 XML 返回 null", ExtractLineRate(tmp) is null);

        // 用例 5b：非有限 line-rate → null（全仓扫描修复：NaN 曾被接受，
        // 继而在 CheckModuleDrop 里让 `baseline - NaN > 0.05` 恒假 → 门禁放行）
        File.WriteAllText(tmp, "<?xml version=\"1.0\"?><coverage line-rate=\"NaN\"></coverage>");
        Case("line-rate=NaN 返回 null(fail-closed)", ExtractLineRate(tmp) is null);
        File.WriteAllText(tmp, "<?xml version=\"1.0\"?><coverage line-rate=\"Infinity\"></coverage>");
        Case("line-rate=Infinity 返回 null(fail-closed)", ExtractLineRate(tmp) is null);

        // 用例 6：阈值判定语义（awk a<b 的等价 double 比较：低于才 FAIL，等于通过）
        var rate = double.Parse("0.72", NumberStyles.Float, CultureInfo.InvariantCulture);
        Case("0.72 >= 0.70 判定通过", !(rate < 0.70));
        Case("0.50 < 0.70 判定失败", double.Parse("0.50", NumberStyles.Float, CultureInfo.InvariantCulture) < 0.70);
        Case("0.70 == 0.70 边界通过", !(0.70 < 0.70));

        // 用例 7：阈值环境变量路由
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", "0.9");
        var (raw1, v1) = ReadThreshold();
        Case("环境变量覆盖阈值 0.9", raw1 == "0.9" && v1 == 0.9);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", null);
        var (raw2, v2) = ReadThreshold();
        Case("缺省阈值 0.70", raw2 == "0.70" && v2 == 0.70);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", "abc");
        var (raw3, v3) = ReadThreshold();
        Case("非法阈值 fail-closed", raw3 == "abc" && v3 is null);
        // 全仓扫描修复：NaN/Infinity 曾被 TryParse 接受 → `< NaN` 恒假 → 门禁静默放行
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", "NaN");
        var (rawNaN, vNaN) = ReadThreshold();
        Case("NaN 阈值 fail-closed", rawNaN == "NaN" && vNaN is null);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", "Infinity");
        var (rawInf, vInf) = ReadThreshold();
        Case("Infinity 阈值 fail-closed", rawInf == "Infinity" && vInf is null);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", "1.5");
        var (rawBig, vBig) = ReadThreshold();
        Case("越界阈值 1.5 fail-closed", rawBig == "1.5" && vBig is null);

        // 用例：单模块降幅判定（CheckModuleDrop——容差 5pp 绝对）
        Case("降幅 2pp 通过", CheckModuleDrop(0.80, 0.78) == 0);
        Case("降幅 5pp 边界通过(等于容差)", CheckModuleDrop(0.80, 0.75) == 0);
        Case("降幅 5.1pp 失败", CheckModuleDrop(0.80, 0.749) == 1);
        Case("覆盖率上升通过(负向对照)", CheckModuleDrop(0.80, 0.85) == 0);
        Case("零基线(新项目)通过", CheckModuleDrop(0.0, 0.30) == 0);
        Case("NaN 当前值判违规(fail-closed)", CheckModuleDrop(0.80, double.NaN) == 1);
        Case("NaN 基线判违规(fail-closed)", CheckModuleDrop(double.NaN, 0.80) == 1);

        // 用例：基线 flat JSON 解析（含注释行跳过 / 尾逗号 / 多项目）
        var baselineFile = Path.Combine(Path.GetTempPath(), "ci-coverage-selftest-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(baselineFile,
            "{\n  \"//\": \"comment\",\n  \"PalDDD.Core.Tests\": 0.3090,\n  \"PalDDD.CQRS.Tests\": 0.3098\n}");
        var parsed = ReadModuleBaselines(baselineFile);
        Case("基线解析：注释行跳过 + 2 项目", parsed.Count == 2 && parsed.ContainsKey("PalDDD.Core.Tests"));
        Case("基线解析：值正确", Math.Abs(parsed["PalDDD.CQRS.Tests"] - 0.3098) < 1e-9);
        File.Delete(baselineFile);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", null);
    }
    finally
    {
        File.Delete(tmp);
    }

    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
