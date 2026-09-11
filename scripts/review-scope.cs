// ============================================================================
// review-scope.cs——review 范围清单生成器（MIG-012-B1，2026-09-11）
// 由 .ai/scripts/review-scope.sh（115 行）等价迁移为 C#（file-based app）。
// 地毯式逐行的覆盖度账本基建（质量为先宗旨配套）。
//
// 用法：在仓库根执行
//   dotnet run scripts/review-scope.cs                    # 全量档：src/ 全部手写代码
//   dotnet run scripts/review-scope.cs -- --diff          # 标准档：本次 diff 触及文件
//   dotnet run scripts/review-scope.cs -- --all           # 全仓档：src+test+samples+docs+...
//   dotnet run scripts/review-scope.cs -- --all -- --partitions 8   # 指定分片数
//   （dotnet run 传参须用 -- 分隔；--partitions 默认 4，--all 建议 8）
// 产出：文件清单（路径+行数）+ 分片方案 + 可粘贴报告段 1 的覆盖度账本模板。
// 逐行覆盖是否完成由人工勾选账本，但"应读清单"由本工具机械生成——漏读文件在账本可见。
//
// 等价迁移说明：
//   1) --all 档以 git ls-files 全集为真源（ITM-232 修复语义保持），.ai 独立仓 tracked
//      文件也纳入（.ai/.git 为目录时前缀 .ai/）；排除项显式计数输出。
//   2) 行数统计：\n 字节计数，逐字节等价 bash 版 wc -l（ReadLines 对无尾换行的
//      末行会多计 1，实测存在此类文件）。
//   3) 排序：bash sort -rn = 数值降序 + 平局整行降序 → 本版 OrderByDescending(n)
//      + ThenByDescending(f, Ordinal)；sort -u = Distinct + Ordinal 升序。
//   4) 姊妹对照段调 scripts/sibling-map.cs（dotnet run，.cs 调 .cs）。
// ============================================================================

using System.Diagnostics;
using System.Text;

// Justification: CA1303 要求 UI 文案走资源表本地化；本工具输出是评审流程的固定
// 中文清单/账本行（与原 bash 版逐行一致），无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

Console.OutputEncoding = Encoding.UTF8;
// 重定向时 Console.WriteLine 默认 \r\n（Windows）——对齐 bash echo 的 \n 行尾
Console.Out.NewLine = "\n";

// 仓库根定位：自当前目录向上找 PalDDD.slnx（等价 bash cd "$(dirname $0)/../.."）
var dir = Environment.CurrentDirectory;
while (!File.Exists(Path.Combine(dir, "PalDDD.slnx"))
       && Path.GetFullPath(dir) != Path.GetPathRoot(Path.GetFullPath(dir)))
    dir = Path.GetDirectoryName(Path.GetFullPath(dir))!;
Environment.CurrentDirectory = dir;

// ── 参数解析（--diff / --all / --partitions N；未知参数 exit 1）──
var mode = "full";
var parts = 4;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--diff":
            mode = "diff";
            break;
        case "--all":
            mode = "all";
            break;
        case "--partitions":
            parts = int.Parse(args[++i]);
            break;
        default:
            Console.WriteLine($"未知参数: {args[i]}");
            return 1;
    }
}

// ── 各档文件集 ──
List<string> files;
if (mode == "diff")
{
    files = Run("git", "diff HEAD~1 --name-only -- src/**/*.cs")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimEnd('\r'))
        .Where(l => !l.Contains(".g.cs", StringComparison.Ordinal))
        .ToList();
    if (files.Count == 0)
    {
        Console.WriteLine("本次 diff 未触及 src/ 手写代码");
        return 0;
    }
}
else if (mode == "all")
{
    // ITM-232 修复（三十二轮）：--all 档以 git ls-files 全集为真源，非白名单。
    // 排除项显式列出；.ai 独立仓 tracked 文件也纳入。
    // 排除理由：
    //   docs/review/ — 评审过程产物（历史报告），不是被审代码
    //   *.g.cs — 源生成器产物
    //   .ai/brain-data/ — Cortex 运行时记忆（gitignored 但防御性排除）
    var tracked = Run("git", "ls-files")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimEnd('\r'))
        .ToList();
    if (Directory.Exists(".ai/.git"))
        tracked.AddRange(Run("git", "-C .ai ls-files")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => ".ai/" + l.TrimEnd('\r')));
    var excluded = tracked
        .Where(l => l.Contains(".g.cs", StringComparison.Ordinal)
                 || l.StartsWith("docs/review/", StringComparison.Ordinal)
                 || l.StartsWith(".ai/brain-data/", StringComparison.Ordinal))
        .Distinct()
        .Count();
    Console.WriteLine($"排除项：{excluded} 个（.g.cs 生成物 + docs/review/ 过程产物 + brain-data/）");
    files = tracked
        .Where(l => !l.Contains(".g.cs", StringComparison.Ordinal)
                 && !l.StartsWith("docs/review/", StringComparison.Ordinal)
                 && !l.StartsWith(".ai/brain-data/", StringComparison.Ordinal))
        .Distinct()
        .OrderBy(l => l, StringComparer.Ordinal)
        .ToList();
}
else
{
    files = Run("git", "ls-files src/**/*.cs")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimEnd('\r'))
        .Where(l => !l.Contains(".g.cs", StringComparison.Ordinal))
        .ToList();
}

Console.WriteLine($"═══════ review 范围清单（{mode} 档）═══════");
Console.WriteLine($"生成: {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 基线: {Run("git", "rev-parse --short HEAD").Trim()}");
Console.WriteLine();

// ── 应读清单（文件存在才计行；行数 = \n 字节计数，逐字节等价 wc -l：
//    无尾换行的末行 ReadLines 会多计 1——实测 .ai/*.md 等 LF 文件大量无尾换行）──
var manifest = new List<(int Lines, string File)>();
foreach (var f in files)
{
    if (!File.Exists(f)) continue;
    manifest.Add((CountNewlines(f), f));
}

var total = manifest.Sum(x => x.Lines);
Console.WriteLine($"─── 应读清单（{manifest.Count} 文件 · {total} 行）───");
// sort -rn：数值降序 + 平局按整行（即文件名）降序
var sorted = manifest
    .OrderByDescending(x => x.Lines)
    .ThenByDescending(x => x.File, StringComparer.Ordinal)
    .ToList();
foreach (var (n, f) in sorted)
    Console.WriteLine($"  {n,5} 行  {f}");

// ── 分片方案（LPT 贪心：按序分给当前负载最小的片，平局取最小片号）──
Console.WriteLine();
Console.WriteLine($"─── 分片方案（{parts} 片按行数均衡——供并行子代理各领一片地毯）───");
var load = new int[parts + 1];
var partOfFile = new int[sorted.Count + 1];
for (var i = 0; i < sorted.Count; i++)
{
    var min = 1;
    for (var p = 2; p <= parts; p++)
        if (load[p] < load[min]) min = p;
    partOfFile[i] = min;
    load[min] += sorted[i].Lines;
}
for (var p = 1; p <= parts; p++)
{
    var members = sorted.Where((_, i) => partOfFile[i] == p).Select(x => x.File).ToList();
    Console.WriteLine($"  片 {p}（{load[p]} 行）: {string.Join(" ", members)}");
}

// ── 覆盖度账本模板 ──
Console.WriteLine();
Console.WriteLine("─── 覆盖度账本模板（粘贴报告段 1，逐文件勾销）───");
foreach (var (n, f) in sorted)
    Console.WriteLine($"- [ ] {f} ({n} 行)");
Console.WriteLine();
Console.WriteLine("账本规则: 全部勾销 = 地毯完成;未勾销文件出现在报告 = 报告视为草稿。");

// ── 姊妹对照清单（unified v2.0 Phase 1a，2026-08-20）──
// 范围内文件命中的「接口→多实现族」机械列出，主线程横向比对逐对留痕（engine.md
// 并行地毯协议第 4 条）。漏点定位：姊妹类缺陷的漏点在修复任务分解处（修一处漏
// 姊妹），review 端此清单是兜底第二道网。
var sibPath = "scripts/sibling-map.cs";
if (File.Exists(sibPath))
{
    var sibOut = Run("dotnet", $"run {sibPath}");
    Console.WriteLine();
    Console.WriteLine("─── 姊妹对照清单（范围内文件命中的多实现族——逐对核对留痕）───");
    var sibRows = sibOut.Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("| I", StringComparison.Ordinal))
        .ToList();
    var sibHits = 0;
    foreach (var f in files)
    {
        if (!File.Exists(f)) continue;
        var hits = sibRows.Where(r => r.Contains(f, StringComparison.Ordinal))
            .Select(r => $"  {r.Split('|')[1].Replace(" ", "")} ← {f}")
            .ToList();
        if (hits.Count <= 0) continue;
        hits.ForEach(Console.WriteLine);
        sibHits++;
    }
    if (sibHits == 0)
        Console.WriteLine("  （范围内无多实现族命中——管线孪生轴仍须对照 .ai/review/sibling-map.md 种子表）");
    Console.WriteLine("规则: 每个命中的族，横向并列全部实现对照守卫/异常转换/参数消费三类对称性（engine.md PD17/PD24）；核对结果记入报告。");
}
else
{
    Console.WriteLine();
    Console.WriteLine("⚠ sibling-map.cs 缺失——姊妹对照退化为人工 checklist（engine.md 存量条款）");
}
return 0;

// ─── 工具函数 ───

// 执行外部命令取 stdout（UTF-8 读——git/dotnet 输出均 UTF-8；stderr 丢弃）
static string Run(string fileName, string arguments)
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
    using var p = Process.Start(psi)!;
    var text = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return text;
}

// 数文件中的 \n 字节数（等价 wc -l；流式分块读，不整载大文件）
static int CountNewlines(string path)
{
    using var fs = File.OpenRead(path);
    var buf = new byte[1 << 16];
    int count = 0, read;
    while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        for (var i = 0; i < read; i++)
            if (buf[i] == (byte)'\n') count++;
    return count;
}

#pragma warning restore CA1303
