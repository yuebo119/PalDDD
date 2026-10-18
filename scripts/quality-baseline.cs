// ============================================================================
// quality-baseline.cs——第三方分析器警告基线棘轮（2026-10-18）
//
// 用法：
//   dotnet run scripts/quality-baseline.cs              # 对比基线（CI）
//   dotnet run scripts/quality-baseline.cs -- --update  # 重测并写基线
//   dotnet run scripts/quality-baseline.cs -- --selftest
// 退出码：0=不高于基线；1=高于基线/构建失败/基线缺失（fail-closed）；2=自测失败。
//
// 为什么默认关闭分析器却还要基线：Pal.DDD 的 TreatWarningsAsErrors=true，直接常开
// 会让 642 条新规则警告变成构建错误。评估后选择 opt-in（-p:EnableQualityAnalyzers=true）
// + 规则子集（.editorconfig）+ 基线棘轮：默认构建零警告契约不变，CI 单独 job 防新增债务。
// 本脚本只数警告、不改规则；规则收敛走 .editorconfig 与评估文档。
// ============================================================================

// Justification: CA1303——固定中文协议行（人读/grep 消费），沿 verify-ai.cs 先例。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal)) return RunSelftest();

var root = FindRepoRoot();
Environment.CurrentDirectory = root;
var baselinePath = Path.Combine(root, "scripts", "quality-analyzer-baseline.txt");

Console.WriteLine("🔬 构建（EnableQualityAnalyzers=true, TreatWarningsAsErrors=false）…");
var psi = new ProcessStartInfo("dotnet",
    "build PalDDD.slnx -c Release -t:Rebuild -p:EnableQualityAnalyzers=true -p:TreatWarningsAsErrors=false --nologo")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    WorkingDirectory = root,
};
using var p = Process.Start(psi)!;
var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
p.WaitForExit();
if (p.ExitCode != 0)
{
    Console.WriteLine($"FAIL 分析器构建失败（exit={p.ExitCode}）——基线无法测量");
    Console.WriteLine(Tail(output, 1200));
    return 1;
}

var lines = output.Split('\n');
var count = CountWarnings(lines);
Console.WriteLine($"分析器警告：{count} 条（去重后）");

if (args.Contains("--update", StringComparer.Ordinal))
{
    File.WriteAllText(baselinePath,
        "# Pal.DDD 第三方分析器警告基线（scripts/quality-baseline.cs）\n" +
        "# 更新：dotnet run scripts/quality-baseline.cs -- --update\n" +
        $"baseline={count}\n" +
        $"measured={DateTime.UtcNow:yyyy-MM-dd}\n");
    Console.WriteLine($"PASS 基线已写入 {Path.GetRelativePath(root, baselinePath)}（{count}）");
    return 0;
}

var baseline = ReadBaseline(baselinePath);
if (baseline is null)
{
    Console.WriteLine($"FAIL 基线文件缺失/不可解析：{baselinePath}——先跑 --update 建立基线（fail-closed）");
    return 1;
}
if (!WithinBaseline(count, baseline.Value))
{
    Console.WriteLine($"FAIL 分析器警告 {count} > 基线 {baseline.Value}（新增 {count - baseline.Value}）——修警告或经评审后 --update");
    foreach (var family in TopFamilies(lines, 8)) Console.WriteLine($"  {family}");
    return 1;
}
Console.WriteLine(count < baseline.Value
    ? $"PASS 分析器警告 {count} < 基线 {baseline.Value}（可 --update 下调）"
    : $"PASS 分析器警告 {count} == 基线 {baseline.Value}");
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

// 只数诊断行（`: warning CODE:`），按整行去重——同一警告在汇总段可能重复输出。
static int CountWarnings(IEnumerable<string> lines) =>
    lines.Where(l => Regex.IsMatch(l, @": warning [A-Za-z]+[0-9]+"))
        .Distinct(StringComparer.Ordinal)
        .Count();

static int? ReadBaseline(string path)
{
    if (!File.Exists(path)) return null;
    return ParseBaselineText(File.ReadAllText(path));
}

static int? ParseBaselineText(string? text)
{
    if (text is null) return null;
    var m = Regex.Match(text, @"(?m)^baseline=(\d+)\s*$");
    return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
}

static bool WithinBaseline(int count, int baseline) => count <= baseline;

static IEnumerable<string> TopFamilies(IEnumerable<string> lines, int top)
{
    var codes = lines
        .Select(l => Regex.Match(l, @": warning ([A-Za-z]+[0-9]+)"))
        .Where(m => m.Success)
        .Select(m => m.Groups[1].Value)
        .GroupBy(c => c, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .Take(top)
        .Select(g => $"{g.Key}: {g.Count()}");
    return codes;
}

static string Tail(string text, int max)
{
    var trimmed = text.Replace("\r", "").TrimEnd();
    return trimmed.Length <= max ? trimmed : "…" + trimmed[^max..];
}

// ─── selftest：判定逻辑纯函数（不触发构建）───
static int RunSelftest()
{
    var failures = new List<string>();
    void Case(string name, bool ok)
    {
        if (ok) Console.WriteLine($"PASS ST-QB {name}");
        else failures.Add(name);
    }

    string[] sample =
    [
        "a.cs(1,1): warning MA0001: x",
        "a.cs(1,1): warning MA0001: x",
        "b.cs(2,2): warning S2077: y",
        "Build succeeded.",
    ];
    Case("CountWarnings 去重且只数诊断行 → 2", CountWarnings(sample) == 2);
    Case("CountWarnings 空输入 → 0", CountWarnings([]) == 0);
    Case("WithinBaseline 等于基线放行", WithinBaseline(10, 10));
    Case("WithinBaseline 低于基线放行", WithinBaseline(9, 10));
    Case("WithinBaseline 高于基线拦截", !WithinBaseline(11, 10));
    Case("ParseBaselineText 正常解析", ParseBaselineText("baseline=642\n") == 642);
    Case("ParseBaselineText 缺失 → null", ParseBaselineText(null) is null);
    Case("ParseBaselineText 不可解析 → null", ParseBaselineText("no baseline") is null);

    Console.WriteLine($"═══════ QUALITY-BASELINE SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var f in failures) Console.WriteLine("  FAIL " + f);
    return failures.Count == 0 ? 0 : 2;
}
