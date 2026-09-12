// ============================================================================
// ci-coverage.cs——CI 覆盖率 + 报告生成 + 全局阈值门禁（MIG-012-C，2026-09-11）
// 由仓库根 ci-coverage.sh（80 行 bash）等价迁移为 C#（dotnet file-based app）。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/ci-coverage.cs --            全链路：build → 逐项目
//       test --coverage → reportgenerator 合并 → line-rate 阈值门禁
//   dotnet run scripts/ci-coverage.cs -- --selftest 自测：XML 解析 + 阈值路由
//       单元验证（构造最小 Cobertura XML，不真跑 dotnet coverage）
//
// 环境变量：COVERAGE_THRESHOLD——全局行覆盖率阈值（默认 0.65，本地放宽/CI 收紧）
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

// 阈值可被环境变量覆盖（本地放宽/CI 收紧）——与原脚本 ${COVERAGE_THRESHOLD:-0.65} 一致
var (thresholdRaw, threshold) = ReadThreshold();
if (threshold is null)
{
    Console.Error.WriteLine($"ERROR: COVERAGE_THRESHOLD 非数字: {thresholdRaw}");
    return 1;
}

Console.WriteLine("=== Pal.DDD CI Coverage ===");

// 1. 构建（输出直接透传终端——原脚本未捕获；非零退出码透传 = set -e 语义）
Console.WriteLine(">> Building...");
var buildExit = RunInherit("dotnet", "build PalDDD.slnx --nologo -v q");
if (buildExit != 0) return buildExit;

// 2. 测试 + 覆盖率收集（MTP 手写协议——见头注释迁移说明 2）
Console.WriteLine(">> Running tests with coverage...");
Directory.CreateDirectory("TestResults");
foreach (var csproj in FindTestProjects())
{
    var name = Path.GetFileNameWithoutExtension(csproj);
    Console.WriteLine($">> {name}");
    var testExit = RunInherit("dotnet",
        $"test {csproj} --nologo --no-build -v q --coverage" +
        $" --coverage-output TestResults/coverage.{name}.cobertura.xml" +
        " --coverage-output-format cobertura");
    if (testExit != 0) return testExit;
}

// 3. 恢复本地工具清单（固定 ReportGenerator 版本，见 .config/dotnet-tools.json）
Console.WriteLine(">> Restoring local tools...");
var restoreExit = RunInherit("dotnet", "tool restore");
if (restoreExit != 0) return restoreExit;

// 4. 合并报告（Cobertura 供第 5 步门禁解析，Html 供人工审阅；glob 由 reportgenerator 自行展开）
Console.WriteLine(">> Merging coverage reports...");
var mergeExit = RunInherit("dotnet",
    "tool run reportgenerator" +
    " -reports:TestResults/coverage.*.cobertura.xml" +
    " -targetdir:TestResults/coverage-report" +
    " -reporttypes:Html;Cobertura");
if (mergeExit != 0) return mergeExit;

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

Console.WriteLine("=== Coverage complete (gate PASSED) ===");
Console.WriteLine("Report: TestResults/coverage-report/index.html");
return 0;

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

// ─── 测试项目枚举：对齐 find test -name '*.csproj' ! -name 'PalDDD.Testing.csproj' | sort ───
// 路径均为 ASCII，Ordinal 与 bash sort（字节序）等价
static List<string> FindTestProjects() =>
    Directory.EnumerateFiles("test", "*.csproj", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != "PalDDD.Testing.csproj")
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();

// ─── 阈值读取：环境变量覆盖 + 默认 0.65；非数字返回 null（fail-closed）───
static (string Raw, double? Value) ReadThreshold()
{
    var raw = Environment.GetEnvironmentVariable("COVERAGE_THRESHOLD");
    if (string.IsNullOrEmpty(raw)) raw = "0.65";
    return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
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
        if (attr is not null
            && double.TryParse(attr, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
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

        // 用例 6：阈值判定语义（awk a<b 的等价 double 比较：低于才 FAIL，等于通过）
        var rate = double.Parse("0.72", NumberStyles.Float, CultureInfo.InvariantCulture);
        Case("0.72 >= 0.65 判定通过", !(rate < 0.65));
        Case("0.50 < 0.65 判定失败", double.Parse("0.50", NumberStyles.Float, CultureInfo.InvariantCulture) < 0.65);
        Case("0.65 == 0.65 边界通过", !(0.65 < 0.65));

        // 用例 7：阈值环境变量路由
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", "0.9");
        var (raw1, v1) = ReadThreshold();
        Case("环境变量覆盖阈值 0.9", raw1 == "0.9" && v1 == 0.9);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", null);
        var (raw2, v2) = ReadThreshold();
        Case("缺省阈值 0.65", raw2 == "0.65" && v2 == 0.65);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", "abc");
        var (raw3, v3) = ReadThreshold();
        Case("非法阈值 fail-closed", raw3 == "abc" && v3 is null);
        Environment.SetEnvironmentVariable("COVERAGE_THRESHOLD", null);
    }
    finally
    {
        File.Delete(tmp);
    }

    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
