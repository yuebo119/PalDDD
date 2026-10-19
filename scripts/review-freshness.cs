// ============================================================================
// review-freshness.cs——review 债双层新鲜度与豁免判定（2026-10-18 审计 P1 抽取）
//
// 用法：
//   dotnet run scripts/review-freshness.cs
//   dotnet run scripts/review-freshness.cs -- --today 2026-10-18 --src-date 2026-10-01
//   dotnet run scripts/review-freshness.cs -- --selftest
// 退出码：0=新鲜/有有效豁免/无 .ai（跳过）；1=review 债且无有效豁免；2=参数/仓库根/自测失败。
//
// 为什么抽成独立 TOOL：原逻辑内嵌在 hook-pre-push.cs，只有纯函数 selftest，
// 没有隔离探针能证明“过期 metrics + 无豁免 → 真的阻断”。本文件由 pre-push 调用，
// gate-audit 用 --today/--src-date 构造 stale/fresh 夹具做正反例。
// ============================================================================

// Justification: CA1303——固定中文协议行（人读/grep 消费），沿 verify-ai.cs 先例。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal)) return RunSelftest();

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var metricsPath = Path.GetFullPath(ArgValue(args, "--metrics") ?? ".ai/review/metrics.md");
if (!File.Exists(metricsPath))
{
    Console.WriteLine("SKIP review 新鲜度：无 metrics（.ai 未安装）");
    return 0;
}

var todayText = ArgValue(args, "--today");
var today = todayText is null
    ? DateOnly.FromDateTime(DateTime.UtcNow)
    : (DateOnly.TryParse(todayText, out var parsedToday) ? parsedToday : DateOnly.MinValue);
if (today == DateOnly.MinValue)
{
    Console.WriteLine($"FAIL --today 非法：{todayText}");
    return 2;
}

var thresholdText = ArgValue(args, "--threshold");
var threshold = thresholdText is null
    ? 30
    : (int.TryParse(thresholdText, out var parsedThreshold) ? parsedThreshold : -1);
if (threshold < 0)
{
    Console.WriteLine($"FAIL --threshold 非法：{thresholdText}");
    return 2;
}

var fullThresholdText = ArgValue(args, "--full-threshold");
var fullThreshold = fullThresholdText is null
    ? 90
    : (int.TryParse(fullThresholdText, out var parsedFullThreshold) ? parsedFullThreshold : -1);
if (fullThreshold < 0)
{
    Console.WriteLine($"FAIL --full-threshold 非法：{fullThresholdText}");
    return 2;
}

var srcDateText = ArgValue(args, "--src-date") ?? Git("log", "-1", "--format=%cs", "--", "src");
var (warning, anyDays, lastAny, lastFull, fullDays) = ReviewFreshness(
    File.ReadAllLines(metricsPath), srcDateText, today, threshold, fullThreshold);
if (warning is null)
{
    var anyText = lastAny is null ? "无轮次" : $"{lastAny:yyyy-MM-dd}（{anyDays} 天）";
    var fullText = lastFull is null ? "无全量记录" : $"{lastFull:yyyy-MM-dd}（{fullDays} 天）";
    Console.WriteLine($"PASS review 新鲜度：最近轮次 {anyText}；最近全量 {fullText}");
    return 0;
}

var exemptionPath = Path.GetFullPath(ArgValue(args, "--exemption") ?? ".ai/review/review-exemption.json");
var exemption = ReadReviewExemption(exemptionPath);
if (exemption is not null && ExemptionAllows(exemption.Expires, today))
{
    Console.WriteLine($"WARN {warning}；有效豁免至 {exemption.Expires:yyyy-MM-dd}（{exemption.Owner}：{exemption.Reason}）");
    return 0;
}

Console.WriteLine($"FAIL {warning}——无有效 review 豁免");
Console.WriteLine("   补正式 review 轮（维护轮只重置 30 天任一轮时钟；全量/里程碑 90 天时钟需全量轮），或更新 .ai/review/review-exemption.json（owner/expires/reason）后重试");
return 1;

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

static string? ArgValue(string[] arguments, string name)
{
    var i = Array.IndexOf(arguments, name);
    return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : null;
}

static string Git(params string[] arguments)
{
    var psi = new ProcessStartInfo("git", string.Join(' ', arguments))
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd().Trim();
    p.WaitForExit();
    return p.ExitCode == 0 ? stdout : "";
}

// 最后一轮日期取 metrics 表 `| vN | YYYY-MM-DD |` 的最大值；交叉索引的 bullet 行不匹配。
// 双层新鲜度（2026-10-18 审计）：任一轮 30 天 + 全量/里程碑 90 天。
// 维护轮只重置“任一轮”时钟，不能洗白全量评审债；全量行由 type 列关键词识别
//（全量/全仓/里程碑/full）。src 变更日期须晚于对应轮次才告警。
static (string? Warning, int AnyDays, DateOnly? LastAny, DateOnly? LastFull, int FullDays) ReviewFreshness(
    IEnumerable<string> metricsLines, string lastSrcDate, DateOnly today, int anyThresholdDays, int fullThresholdDays)
{
    DateOnly? lastAny = null;
    DateOnly? lastFull = null;
    foreach (var line in metricsLines)
    {
        var m = Regex.Match(line, @"^\|\s*v\d+\s*\|\s*(\d{4}-\d{2}-\d{2})\s*\|\s*([^|]*)");
        if (!m.Success || !DateOnly.TryParse(m.Groups[1].Value, out var d)) continue;
        if (lastAny is null || d > lastAny.Value) lastAny = d;

        var type = m.Groups[2].Value;
        if ((type.Contains("全量", StringComparison.Ordinal)
                || type.Contains("全仓", StringComparison.Ordinal)
                || type.Contains("里程碑", StringComparison.Ordinal)
                || type.Contains("full", StringComparison.OrdinalIgnoreCase))
            && (lastFull is null || d > lastFull.Value))
            lastFull = d;
    }

    if (!DateOnly.TryParse(lastSrcDate, out var srcDate))
        return (null, 0, lastAny, lastFull, 0);

    var anyDays = lastAny is null ? 0 : today.DayNumber - lastAny.Value.DayNumber;
    var fullDays = lastFull is null ? 0 : today.DayNumber - lastFull.Value.DayNumber;
    var parts = new List<string>();
    if (lastAny is not null && anyDays > anyThresholdDays && srcDate > lastAny.Value)
        parts.Add($"最近轮次 {lastAny:yyyy-MM-dd} 已 {anyDays} 天");
    if (lastFull is not null && fullDays > fullThresholdDays && srcDate > lastFull.Value)
        parts.Add($"最近全量 {lastFull:yyyy-MM-dd} 已 {fullDays} 天");

    return parts.Count == 0
        ? (null, anyDays, lastAny, lastFull, fullDays)
        : ($"review 债：{string.Join("；", parts)}（src 最近 {srcDate:yyyy-MM-dd}）", anyDays, lastAny, lastFull, fullDays);
}

static ReviewExemption? ReadReviewExemption(string path)
{
    try
    {
        return File.Exists(path) ? ParseExemptionText(File.ReadAllText(path)) : null;
    }
    catch (IOException) { return null; }
    catch (UnauthorizedAccessException) { return null; }
}

static ReviewExemption? ParseExemptionText(string? text)
{
    if (text is null) return null;
    try
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("expires", out var expiresElement)
            || !DateOnly.TryParse(expiresElement.GetString(), out var expires))
            return null;
        var owner = root.TryGetProperty("owner", out var ownerElement) ? ownerElement.GetString() ?? "" : "";
        var reason = root.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() ?? "" : "";
        return new ReviewExemption(expires, owner, reason);
    }
    catch (JsonException) { return null; }
}

static bool ExemptionAllows(DateOnly expires, DateOnly today) => expires >= today;

// ─── selftest：纯函数正反例（不触达真实仓库）───
static int RunSelftest()
{
    var failures = new List<string>();
    void Case(string name, bool ok)
    {
        if (ok) Console.WriteLine($"PASS ST-RF {name}");
        else failures.Add(name);
    }

    var today = new DateOnly(2026, 10, 18);
    string[] fresh = ["| v92 | 2026-10-18 | 质量系统维护轮 |"];
    string[] stale = ["| v91 | 2026-09-01 | 全量 |"];
    Case("ReviewFreshness 新鲜维护轮不告警", ReviewFreshness(fresh, "2026-10-01", today, 30, 90).Warning is null);
    var staleResult = ReviewFreshness(stale, "2026-10-01", today, 30, 90);
    Case("ReviewFreshness 超期 + src 变更告警", staleResult.Warning is not null && staleResult.AnyDays == 47);
    Case("ReviewFreshness 超期但 src 无新变更不告警", ReviewFreshness(stale, "2026-08-01", today, 30, 90).Warning is null);
    string[] fullStale = ["| v92 | 2026-10-18 | 质量系统维护轮 |", "| v91 | 2026-05-01 | 全仓运行 |"];
    Case("ReviewFreshness 维护轮新鲜但全量超 90 天告警", ReviewFreshness(fullStale, "2026-10-01", today, 30, 90).Warning is not null);
    string[] anyStale = ["| v92 | 2026-09-01 | 质量系统维护轮 |", "| v91 | 2026-08-01 | 全仓运行 |"];
    Case("ReviewFreshness 全量新鲜但任一轮超 30 天告警", ReviewFreshness(anyStale, "2026-10-01", today, 30, 90).Warning is not null);
    Case("ReviewFreshness 无轮次不告警", ReviewFreshness([], "2026-10-01", today, 30, 90).Warning is null);
    Case("ExemptionAllows 未到期放行", ExemptionAllows(new DateOnly(2026, 11, 15), today));
    Case("ExemptionAllows 已到期阻断", !ExemptionAllows(new DateOnly(2026, 9, 1), today));
    Case("ParseExemptionText 正常解析", ParseExemptionText("""{"owner":"a","expires":"2026-11-15","reason":"r"}""") is { Owner: "a" });
    Case("ParseExemptionText 缺 expires → null", ParseExemptionText("""{"owner":"a"}""") is null);
    Case("ParseExemptionText 非法 JSON → null", ParseExemptionText("not-json") is null);

    Console.WriteLine($"═══════ REVIEW-FRESHNESS SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var f in failures) Console.WriteLine("  FAIL " + f);
    return failures.Count == 0 ? 0 : 2;
}

// 顶层类型声明（top-level program 要求类型声明位于所有语句之后）
internal sealed record ReviewExemption(DateOnly Expires, string Owner, string Reason);
