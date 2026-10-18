// ============================================================================
// hook-pre-push.cs——pre-push 钩子判定层（2026-10-08，C# 化替换 42 行 bash）
// ① main 分支保护（merge-base --is-ancestor：dev 已合入 main 才允许从 main 推送）
// ② 同步提示（ITM-808：多会话积压的验证债可见性，信息性不阻断）
// ③ review 新鲜度提示（2026-10-18 增：metrics 最后一轮 >30 天且 src 有变更时告警，
//    信息性不阻断——review 系统是事件驱动，无法机械强制轮次，但可以让债务可见）
// ④ gate-lite CI 预览（快速门禁降级路径）
// .githooks/pre-push 退化为启动器；本文件持有全部判定。
//
// 用法：dotnet run scripts/hook-pre-push.cs（由 .githooks/pre-push 启动器 exec）
// 退出码：0=通过；1=拦截。
// ============================================================================

// Justification: CA1303——固定中文协议行（与原 bash 版一致），沿 verify-ai.cs 先例。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return RunSelftest();
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

// ── ① main 分支保护 ──
var branch = Git("rev-parse", "--abbrev-ref HEAD");
if (branch == "main")
{
    // merge-base --is-ancestor：dev 是否已合入 main（原 hook 的 ITM-671 修复——
    // 直接比 HEAD 不同会因 merge commit 恒真误拦）
    var merged = GitExitCode("merge-base", "--is-ancestor", "dev", "main");
    if (merged != 0)
    {
        Console.WriteLine("⛔ dev 未合入 main，请先合并：git checkout main && git merge dev");
        Console.WriteLine("   （如果不需要合并，说明你在 main 上直接做了修改——这是违规的）");
        return 1;
    }
}

// ── ② 同步提示（信息性不阻断）──
// 治「多批次并行开发后长时间不推送」的验证债：本地积压越多，CI（尤其 aot-verify
// 真发布）对积压代码的验证缺口越大（首席审计 O-1：曾实测积压 49 提交）。
var upstream = Git("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
if (upstream.Length > 0)
{
    var behind = ParseCount(Git("rev-list", "--count", $"HEAD..{upstream}"));
    var ahead = ParseCount(Git("rev-list", "--count", $"{upstream}..HEAD"));
    if (behind > 0)
        Console.WriteLine($"⚠️  pre-push：本地落后 {upstream} {behind} 个提交——推送可能被拒或需先 rebase（多会话并行写冲突风险，open-items O-2）。");
    if (ahead >= 10)
        Console.WriteLine($"⚠️  pre-push：本次推送承载 {ahead} 个积压提交——CI（含 aot-verify 真发布）将首次验证这批代码，失败排查面较大；建议日常小批推送。");
}

// ── ③ review 债阻断（2026-10-18 由提示升级为阻断）──
// metrics 停在 v91（2026-09-19）而 10 月有 v4.0.0/ADR-030/031 等 src 变更——review 系统
// 实际停摆，但此前只提示。现在：超过阈值且 src 有变更时阻断推送，除非
// .ai/review/review-exemption.json 有未到期豁免（owner/expires/reason）。豁免到期前
// 必须补正式 review 轮或续期，避免"提示被忽略"的历史重演。
var metricsPath = Path.Combine(root, ".ai", "review", "metrics.md");
if (File.Exists(metricsPath))
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var lastSrcDate = Git("log", "-1", "--format=%cs", "--", "src");
    var (reviewWarning, reviewDays) = ReviewFreshness(
        File.ReadAllLines(metricsPath), lastSrcDate, today, thresholdDays: 30);
    if (reviewWarning is not null)
    {
        var exemption = ReadReviewExemption(
            Path.Combine(root, ".ai", "review", "review-exemption.json"));
        if (exemption is not null && ExemptionAllows(exemption.Expires, today))
        {
            Console.WriteLine($"⚠️  pre-push：{reviewWarning}；有效豁免至 {exemption.Expires:yyyy-MM-dd}（{exemption.Owner}：{exemption.Reason}）");
        }
        else
        {
            Console.WriteLine($"⛔ pre-push：{reviewWarning}——无有效 review 豁免（最后一轮距今 {reviewDays} 天）");
            Console.WriteLine("   补正式 review 轮，或更新 .ai/review/review-exemption.json（owner/expires/reason）后重试");
            return 1;
        }
    }
}

// ── ④ gate-lite CI 预览 ──
Console.WriteLine("🔍 gate-lite（CI 降级门禁预览）…");
var psi = new ProcessStartInfo("dotnet", "run scripts/gate-lite.cs")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    WorkingDirectory = root,
};
using var p = Process.Start(psi)!;
Console.Out.Write(p.StandardOutput.ReadToEnd());
Console.Error.Write(p.StandardError.ReadToEnd());
p.WaitForExit();
if (p.ExitCode != 0)
{
    Console.WriteLine("⛔ gate-lite 失败");
    return 1;
}

Console.WriteLine("✅ pre-push 通过");
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

// git 快速调用：输出首行（异常/非零返回空串——提示类信息失败不阻断推送）
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

static int GitExitCode(params string[] arguments)
{
    var psi = new ProcessStartInfo("git", string.Join(' ', arguments))
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };
    using var p = Process.Start(psi)!;
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode;
}

static int ParseCount(string s) =>
    int.TryParse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

// review 新鲜度纯函数（2026-10-18 增）：metrics 最后一轮日期 vs 最近 src 提交日期。
// 返回 (warning, days)；不满足告警条件时 warning=null。语义：
//   · 无 metrics / 无 src 提交 → 不告警（无法判断，不给假信号）
//   · 最后一轮在阈值内 → 不告警
//   · 超过阈值但最后一轮之后 src 无变更 → 不告警（没有待评审的 src 变化）
//   · 超过阈值且最后一轮之后 src 有变更 → 告警（review 债可见）
static (string? Warning, int Days) ReviewFreshness(
    IEnumerable<string> metricsLines, string lastSrcDate, DateOnly today, int thresholdDays)
{
    DateOnly? lastRound = null;
    foreach (var line in metricsLines)
    {
        var m = Regex.Match(line, @"^\|\s*v\d+[^|]*\|\s*(\d{4}-\d{2}-\d{2})\s*\|");
        if (m.Success && DateOnly.TryParse(m.Groups[1].Value, out var d)
            && (lastRound is null || d > lastRound.Value))
            lastRound = d;
    }

    if (lastRound is null || !DateOnly.TryParse(lastSrcDate, out var srcDate)) return (null, 0);
    var days = today.DayNumber - lastRound.Value.DayNumber;
    if (days <= thresholdDays || srcDate <= lastRound.Value) return (null, days);
    return ($"review 债：metrics 最后一轮 {lastRound:yyyy-MM-dd}（{days} 天前），src 最近提交 {srcDate:yyyy-MM-dd}", days);
}

// review 豁免解析（2026-10-18 增）：owner/expires/reason 三字段；解析失败按无豁免处理。
static ReviewExemption? ReadReviewExemption(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("expires", out var expiresElement)
            || !DateOnly.TryParse(expiresElement.GetString(), out var expires))
            return null;
        var owner = root.TryGetProperty("owner", out var ownerElement) ? ownerElement.GetString() ?? "" : "";
        var reason = root.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() ?? "" : "";
        return new ReviewExemption(expires, owner, reason);
    }
    catch (JsonException) { return null; }
    catch (IOException) { return null; }
    catch (UnauthorizedAccessException) { return null; }
}

static bool ExemptionAllows(DateOnly expires, DateOnly today) => expires >= today;

// ─── selftest：计数解析纯函数正负例（gate-audit SELFTEST 判定面）───
static int RunSelftest()
{
    var failures = new List<string>();
    void Case(bool actual, bool expected, string name)
    {
        if (actual == expected) Console.WriteLine($"PASS ST-HPP {name}");
        else failures.Add(name);
    }

    Case(ParseCount("7") == 7, true, "计数解析数字");
    Case(ParseCount(" 12 ") == 12, true, "计数解析含空白");
    Case(ParseCount("") == 0, true, "空串兜底 0（git 失败不阻断提示）");
    Case(ParseCount("abc") == 0, true, "非数字兜底 0");

    // review 新鲜度（2026-10-18 增）
    var today = new DateOnly(2026, 10, 18);
    string[] metrics = ["| v91 | 2026-09-19 | 全量 |", "| v90 | 2026-09-19 | 全量 |"];
    Case(ReviewFreshness(metrics, "2026-10-01", today, 30).Warning is null,
        true, "review 新鲜度：最后一轮在阈值内不告警");
    var stale = ReviewFreshness(["| v91 | 2026-09-01 | 全量 |"], "2026-10-01", today, 30);
    Case(stale.Warning is not null && stale.Days == 47, true, "review 新鲜度：超 30 天且 src 有变更 → 告警");
    Case(ReviewFreshness(["| v91 | 2026-09-01 | 全量 |"], "2026-08-01", today, 30).Warning is null,
        true, "review 新鲜度：超期但 src 无新变更 → 不告警");
    Case(ReviewFreshness([], "2026-10-01", today, 30).Warning is null,
        true, "review 新鲜度：无 metrics → 不告警（不给假信号）");

    // review 豁免（2026-10-18 增）
    Case(ExemptionAllows(new DateOnly(2026, 11, 15), today), true, "review 豁免：未到期放行");
    Case(ExemptionAllows(new DateOnly(2026, 9, 1), today), false, "review 豁免：已到期阻断");

    Console.WriteLine($"═══════ HOOK-PRE-PUSH SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var f in failures) Console.WriteLine("  FAIL " + f);
    return failures.Count == 0 ? 0 : 1;
}

// 顶层类型声明（top-level program 要求类型声明位于所有语句之后）
internal sealed record ReviewExemption(DateOnly Expires, string Owner, string Reason);
