// ============================================================================
// sister-axis.cs——姊妹轴机械枚举（MIG-012-B1，2026-09-11）
// 由 .ai/scripts/sister-axis-scan.sh（115 行）等价迁移为 C#（file-based app）。
// 修复任何缺陷前运行，枚举全部同型位置——产出 = 修复范围（非首个命中），
// 防止"修一个漏 N-1 个"。
//
// 用法：在仓库根执行
//   dotnet run scripts/sister-axis.cs -- <修复类型> <关键标识符>
// 类型：guard | truncate | dispose | ct | dedup | comment | status | detach | null-guard
// 标识符：方法名/参数名/字段名等（用于精确定位同型位置；按字面子串匹配）
// 退出码：0=枚举完成；1=用法错误/未知类型。
//
// 等价迁移说明：
//   1) 原版 EXCLUDES="-not -path */obj/* -not -path */bin/*" 是 find 语法误传给
//      grep——九型首条 grep 全部报 "unknown option -- t" 崩溃（set -e 退出 1，
//      实跑复现），枚举功能从未生效。本版按其设计意图实现：路径含 /obj/ 或
//      /bin/ 段的文件排除（等价 find -not -path），属迁移清偿勘正。
//   2) 各段内 grep -v 的行级子串过滤（如 grep -v "obj\|///"）按输出行子串保真——
//      行内（含路径与内容）含该子串即排除。
//   3) grep 枚举顺序 = GNU grep fts（NTFS 字母混排 + 子目录即时递归），本版逐字节
//      复刻（见 EnumerateFilesByGrepOrder）；head -N 截断语义保持。
//   4) 输出行行尾 \r 剥离（MSYS2 GNU grep 管道输出 text mode：CRLF→LF）。
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本工具输出是修复枚举的固定
// 中文段头/结论行（与原 bash 版逐行一致），无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

Console.OutputEncoding = Encoding.UTF8;
// 重定向时 Console.WriteLine 默认 \r\n（Windows）——对齐 bash echo 的 \n 行尾
Console.Out.NewLine = "\n";

var type = args.Length > 0 ? args[0] : "";
var key = args.Length > 1 ? args[1] : "";

if (type.Length == 0 || key.Length == 0)
{
    Console.WriteLine("用法: dotnet run scripts/sister-axis.cs -- <guard|truncate|dispose|ct|dedup|comment|status|detach|null-guard> <关键标识符>");
    Console.WriteLine("示例: dotnet run scripts/sister-axis.cs -- guard leaseDuration");
    return 1;
}

Console.WriteLine("════════════════════════════════════════════════");
Console.WriteLine($" 姊妹轴枚举: 类型={type} 标识符={key}");
Console.WriteLine("════════════════════════════════════════════════");

// ── 段输出：标题 + 行（head 截断保真；行尾 \r 原样保留——GNU grep 逐字节一致）──
// grep -rn <rx> <roots> 多目录（实参序）+ 多扩展名 + 路径段排除；输出 file:line:text
List<string> Scan(string[] roots, string[] extensions, Regex rx) =>
    roots.Where(Directory.Exists)
        .SelectMany(root => EnumerateByGrepOrder(root, extensions))
        .Select(f => (Path: f.Replace('\\', '/'), Lines: Encoding.UTF8.GetString(File.ReadAllBytes(f)).Split('\n')))
        .SelectMany(x => x.Lines.Select((text, i) => (x.Path, Idx: i, Text: text))
            .Where(l => rx.IsMatch(l.Text))
            .Select(l => $"{l.Path}:{l.Idx + 1}:{l.Text}"))
        .ToList();

void Section(string title, IEnumerable<string> rows, int head)
{
    Console.WriteLine();
    Console.WriteLine(title);
    // 行尾 \r 剥离（MSYS2 GNU grep 管道输出为 text mode：CRLF→LF）
    foreach (var r in rows.Take(head))
        Console.WriteLine(r.EndsWith('\r') ? r[..^1] : r);
}

switch (type)
{
    case "guard":
        Section("── 同参数守卫点 ──",
            Scan(["src"], [".cs"], new Regex(Regex.Escape(key)))
                .Where(l => !l.Contains("///")), 30);
        Section("── 全部守卫方法（跨文件对照） ──",
            Scan(["src"], [".cs"], new Regex("ThrowIfNegativeOrZero|ThrowIfNullOrWhiteSpace|ThrowIfNull|ThrowIfLessThan|ThrowIfNegative"))
                .Where(l => !l.Contains("obj")), 30);
        Section("── 缺守卫的姊妹方法（同名方法无守卫） ──",
            Scan(["src"], [".cs"], new Regex(Regex.Escape(key)))
                .Where(l => !l.Contains("ThrowIf") && !l.Contains("ArgumentNullException") && !l.Contains("ArgumentOutOfRange")), 15);
        break;

    case "truncate":
        Section("── 字符串截断点（裸切片 + Truncate 调用） ──",
            Scan(["src"], [".cs"], new Regex(@"\[\.\.(2000|2040|256|2000)\]|Truncate\(|Normalize\(")), 30);
        Section("── FailureReason 调用点 ──",
            Scan(["src"], [".cs"], new Regex(@"FailureReason\.(Truncate|Normalize)")), 20);
        break;

    case "dispose":
        Section("── Dispose/Close/清理方法 ──",
            Scan(["src"], [".cs"], new Regex(@"Dispose|\.Close|\.CloseAsync"))
                .Where(l => !l.Contains("obj") && !l.Contains("///") && !l.Contains("// "))
                .Where(l => new Regex("override|async|void|Task").IsMatch(l)), 20);
        Section("── try-finally 配对检查 ──",
            Scan(["src"], [".cs"], new Regex("finally")), 15);
        break;

    case "ct":
        Section("── CancellationToken 参数/传导 ──",
            Scan(["src"], [".cs"], new Regex("CancellationToken"))
                .Where(l => !l.Contains("obj") && !l.Contains("///") && !l.Contains("using"))
                .Where(l => new Regex("public|internal|protected").IsMatch(l)), 20);
        Section("── 缺 ct 传导的 async 调用（无 ct 参数的 await） ──",
            Scan(["src"], [".cs"], new Regex("await.*Async\\(\\)"))
                .Where(l => !l.Contains("ct") && !l.Contains("CancellationToken")
                         && !l.Contains("tokenSnapshot") && !l.Contains("None")), 10);
        break;

    case "dedup":
        Section("── 查重/去重逻辑 ──",
            Scan(["src"], [".cs"], new Regex(@"HashSet|\.Add\(|ContainsKey|seenHosts|seenServers"))
                .Where(l => !l.Contains("obj") && !l.Contains("///")), 20);
        break;

    case "comment":
        Section("── 同族注释/声明 ──",
            Scan(["src", "docs"], [".cs", ".md"], new Regex(Regex.Escape(key))), 30);
        break;

    case "status":
        Section("── 状态枚举守卫/SQL 字面量 ──",
            Scan(["src"], [".cs"], new Regex("Status|status"))
                .Where(l => new Regex("= 0|= 1|= 2|= 3|== 0|== 1|== 2|== 3|Pending|Processing|Completed|Failed|Dead").IsMatch(l))
                .Where(l => !l.Contains("obj") && !l.Contains("///")), 30);
        break;

    case "detach":
        Section("── Detach 覆盖矩阵 ──",
            Scan(["src"], [".cs"], new Regex("EntityState.Detached|DetachAddedEvents|Detach"))
                .Where(l => !l.Contains("obj")), 30);
        Section("── 全部 SaveChanges 调用点（核对 Detach 覆盖） ──",
            Scan(["src"], [".cs"], new Regex(@"SaveChangesAsync|SaveChanges\(\)"))
                .Where(l => !l.Contains("obj") && !l.Contains("///")), 20);
        break;

    case "null-guard":
        Section("── null 守卫覆盖矩阵 ──",
            Scan(["src"], [".cs"], new Regex(@"ThrowIfNull|is null.*throw|is not null"))
                .Where(l => !l.Contains("obj") && !l.Contains("///")), 30);
        break;

    default:
        Console.WriteLine($"未知类型: {type}");
        Console.WriteLine("支持: guard | truncate | dispose | ct | dedup | comment | status | detach | null-guard");
        return 1;
}

Console.WriteLine();
Console.WriteLine("════════════════════════════════════════════════");
Console.WriteLine(" 枚举完成 — 以上为修复范围的全部同型位置");
Console.WriteLine(" 修复清单 = 枚举结果全集（非首个命中）");
Console.WriteLine("════════════════════════════════════════════════");
return 0;

// ─── 工具函数 ───

// GNU grep fts 枚举序：目录条目 NTFS 字母混排（大小写不敏感），文件即时处理、
// 子目录立即递归（EnumerateFiles(AllDirectories) 子目录延后导致顺序漂移，不用）。
// 路径段排除 /obj/、/bin/（原版 find -not -path 设计意图）。
static IEnumerable<string> EnumerateByGrepOrder(string root, string[] extensions)
{
    var entries = Directory.EnumerateFileSystemEntries(root)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    foreach (var e in entries)
    {
        var posix = e.Replace('\\', '/');
        if (posix.Contains("/obj/", StringComparison.Ordinal)
            || posix.Contains("/bin/", StringComparison.Ordinal))
            continue;
        if (Directory.Exists(e))
        {
            foreach (var f in EnumerateByGrepOrder(e, extensions)) yield return f;
        }
        else if (extensions.Any(ext => e.EndsWith(ext, StringComparison.Ordinal)))
        {
            yield return e;
        }
    }
}

#pragma warning restore CA1303
