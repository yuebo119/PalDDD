// ============================================================================
// review-coverage-report.cs——沉寂面报告（Pal 会话审计 2026-09-25 立法 · TOOL）
//
// 动机：差分评审（以 diff 为锚的质量轮）对「长期未被变更的文件」结构性失明——
// 审计实证：28 处门禁存量假绿全部集中在 MIG-012 后基本未动的文件，IUnitOfWork 的
// 缺陷引入侧最后改动 2026-07-05（两个月无人碰）才被一次无锚点全量普查暴露。
// 教训：差分门禁防回归，普查防潜伏，缺一不可。但日历式全量普查已被证伪（G060：
// 22 次同命令驱动 60+ 轮循环，产出退化为修复副产品）——最优解不是「定期跑全量」，
// 而是让盲区变成一个可测的数字：沉寂时长。本报告把「哪些文件可能没人看过」量化，
// 供发布前抽查与评审轮定向导航。
//
// 口径（单一真源，避免第二次 count-audit 式漂移）：
//   硬信号 = git 沉寂天数：`git log --name-only` 全量一次取回（单进程，非逐文件
//   spawn），每文件首次出现的提交时间 = 最后实质变更时间。排除 bin/obj/dotnet/
//   .git 与本报告自身。
//   软信号（评审覆盖）暂不做：grep 评审报告点名文件会有双向误差（报告引用代码位置
//   文件名≠真读过；未点名≠没读过）——宁缺毋滥，硬信号足够导航。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/review-coverage-report.cs                 默认 Top 25 · ≥14 天
//   dotnet run scripts/review-coverage-report.cs -- --top 40 --min-days 30
//
// 退出码：0=报告生成；2=仓库根定位失败/git 不可用（报告工具对内容恒 0——它没有
// 「失败」语义，只有「数字」语义；数字异常由读报告的人裁决）。
// ============================================================================

// Justification: CA1303 同 scripts/ 既有门禁先例（固定中文报告文案，非可配 UI）。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

int top = 25;
int minDays = 14;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--top" && int.TryParse(args[i + 1], out var t)) top = t;
    if (args[i] == "--min-days" && int.TryParse(args[i + 1], out var d)) minDays = d;
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

// 一次 git log 拿全仓（git magic pathspec 的 :(exclude) 经 Process 直启报
// "outside repository"——改为无路径限定全量取回，产物目录在 C# 侧过滤）：
// 时间倒序，每文件首次出现 = 最后变更。
var psi = new ProcessStartInfo("git", "log --format=%ct --name-only -n 100000")
{
    RedirectStandardOutput = true,
    StandardOutputEncoding = Encoding.UTF8,
    UseShellExecute = false,
};
string logText;
try
{
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("git 进程启动失败");
    logText = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine("错误：git log 失败（浅克隆/无历史？）——沉寂面无法计算");
        return 2;
    }
}
catch (System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine("错误：git 不可用");
    return 2;
}

var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var lastTouch = new Dictionary<string, long>(StringComparer.Ordinal);
string? pendingFile = null;
        foreach (var raw in logText.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0) continue; // %ct 与文件段之间的空行——不清 pendingFile
            if (raw.All(char.IsDigit))
            {
                pendingFile = raw; // 本次提交时间，作用于其后的文件名段
                continue;
            }
    if (pendingFile is null) continue;
    // 产物/旁路目录过滤（bin/obj 构建产物、dotnet/ runfile 产物、.ai 独立仓库、data 本地数据）
    if (raw.StartsWith("bin/", StringComparison.Ordinal) || raw.Contains("/bin/", StringComparison.Ordinal)
        || raw.StartsWith("obj/", StringComparison.Ordinal) || raw.Contains("/obj/", StringComparison.Ordinal)
        || raw.StartsWith("dotnet/", StringComparison.Ordinal) || raw.StartsWith(".ai/", StringComparison.Ordinal)
        || raw.StartsWith("data/", StringComparison.Ordinal))
        continue;
    if (!lastTouch.ContainsKey(raw))
        lastTouch[raw] = long.Parse(pendingFile, System.Globalization.CultureInfo.InvariantCulture);
}

if (lastTouch.Count == 0)
{
    Console.Error.WriteLine("错误：git log 无文件记录——仓库历史为空？");
    return 2;
}

// 按目录聚合：文件数、中位沉寂、最沉寂文件
var byDir = lastTouch
    .GroupBy(kv => GetDir(kv.Key), StringComparer.Ordinal)
    .Select(g =>
    {
        var days = g.Select(kv => (now - kv.Value) / 86400.0).OrderBy(x => x).ToList();
        var median = days[days.Count / 2];
        var oldest = g.OrderByDescending(kv => now - kv.Value).First();
        return new DirRow(g.Key, days.Count, median, oldest.Key, (now - oldest.Value) / 86400.0);
    })
    .OrderByDescending(r => r.MedianDays)
    .ToList();

Console.WriteLine($"═══ 沉寂面报告（基准 {DateTime.Now:yyyy-MM-dd} · 沉寂 = 距最后一次 git 变更）═══");
Console.WriteLine();
Console.WriteLine($"{"目录",-44} {"文件数",5} {"中位沉寂天",9}  最沉寂文件（天）");
Console.WriteLine(new string('-', 104));
foreach (var r in byDir.Take(15))
    Console.WriteLine($"{r.Dir,-44} {r.Files,5} {r.MedianDays,9:F0}  {r.OldestFile}（{r.OldestDays:F0}）");

var stale = lastTouch
    .Where(kv => (now - kv.Value) >= minDays * 86400)
    .OrderByDescending(kv => now - kv.Value)
    .Take(top)
    .ToList();
Console.WriteLine();
Console.WriteLine($"── Top {stale.Count} 沉寂文件（≥{minDays} 天；定向抽查池：发布前 checklist / 评审轮导航）──");
foreach (var (f, t) in stale)
    Console.WriteLine($"  {(now - t) / 86400.0,6:F0} 天  {f}");
Console.WriteLine();
Console.WriteLine($"口径：单次 git log 推导（最后变更 = 该文件首次出现于时间倒序流）；评审覆盖软信号未纳入（宁缺毋滥）。");
Console.WriteLine($"用法：发布 tag 前 / 大迁移收尾后，对 Top 清单做无锚点抽查——不必全量普查。");
return 0;

static string GetDir(string path)
{
    var i = path.LastIndexOf('/');
    return i < 0 ? "（根目录）" : path[..i];
}

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

internal sealed record DirRow(string Dir, int Files, double MedianDays, string OldestFile, double OldestDays);
