// ============================================================================
// fix-completeness.cs——修复完整性验证（MIG-012-B1，2026-09-11）
// 由 .ai/scripts/fix-completeness-check.sh（109 行）等价迁移为 C#（file-based app）。
// 修复提交前运行，机械验证姊妹轴覆盖——确认"修复范围 = 枚举结果全集"而非
// "修了第一个就提交"。在 sister-axis.cs 枚举 → 修复 → 本工具验证 的流程中作最终关。
//
// 用法：在仓库根执行
//   dotnet run scripts/fix-completeness.cs -- guard <关键标识符>
//   dotnet run scripts/fix-completeness.cs -- truncate|status|detach <任意占位>
// 类型：guard | truncate | status | detach（未知类型 exit 1）
// 退出码：0=通过；1=验证失败/用法错误。
//
// 等价迁移说明（原版 bash 病态的处置，均经当前仓库实跑基线确认）：
//   1) ITM-619 修复语义保持：guard 档 KEY 零文件命中 → ✗ + exit 1（防"验证了个寂寞"）。
//   2) 原版 guard/truncate 档残留 `grep -c ... || echo 0` 多行病态（ITM-619 只修了
//      status 档）：计数值 0 时变量为 "0\n0" → [[ ]] 算术报错（stderr 噪音 + 条件
//      判假）。本版整数计数天然消除，stdout 与退出码与原版正常路径一致。
//      该病态导致 guard 档"有公开成员但零守卫"的 ✗ 行实际不可达（假绿）——
//      本版勘正为可达（这是勘正，非漂移：✗ 行是脚本明确意图）。
//   3) 原版 `((MISSING++))` 首次自增返回非零 → set -e 提前退出：失败档输出首个 ✗
//      行后立即终止（exit 1，无"完整性验证失败"总结行）。本版保真：首个 ✗ 即
//      return 1——原版的失败总结行因该病态不可达，不迁移。
//   4) grep -v obj / grep -v test 为输出行子串过滤（非路径段匹配），保真：
//      路径含 "obj"/"test" 子串的行被排除。
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本工具输出是修复验证的固定
// 中文结论行（与原 bash 版逐行一致），无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

Console.OutputEncoding = Encoding.UTF8;
// 重定向时 Console.WriteLine 默认 \r\n（Windows）——对齐 bash echo 的 \n 行尾
Console.Out.NewLine = "\n";

const string Green = "\x1b[0;32m", Red = "\x1b[0;31m", Nc = "\x1b[0m";

var type = args.Length > 0 ? args[0] : "";
var key = args.Length > 1 ? args[1] : "";

if (type.Length == 0 || key.Length == 0)
{
    Console.WriteLine("用法: dotnet run scripts/fix-completeness.cs -- <修复类型> <关键标识符>");
    return 1;
}

Console.WriteLine("════════════════════════════════════════════════");
Console.WriteLine($" 修复完整性验证: 类型={type} 标识符={key}");
Console.WriteLine("════════════════════════════════════════════════");

// ── src/ 全量 .cs 枚举（含 obj/ 下生成物——grep -r 同样递归，排除靠输出行过滤）──
var srcFiles = EnumerateCsFiles("src").Select(f => f.Replace('\\', '/')).ToList();

switch (type)
{
    case "guard":
    {
        Console.WriteLine();
        Console.WriteLine("── 守卫覆盖完整性 ──");
        // 找出所有含 KEY 的方法所在文件，检查是否都有对应守卫
        var filesWithKey = srcFiles
            .Where(f => !f.Contains("obj", StringComparison.Ordinal) && FileContains(f, key))
            .ToList();
        // ITM-619 修复：命中 0 文件视为 KEY 误用（拼写错误），直接 fail 阻断
        if (filesWithKey.Count == 0)
        {
            Console.WriteLine($"  {Red}✗{Nc} {key} 未匹配任何文件——请检查拼写（防误用）");
            return 1;
        }
        foreach (var f in filesWithKey)
        {
            var lines = ReadLines(f);
            var methods = lines.Count(l => l.Contains("public", StringComparison.Ordinal)
                                         || l.Contains("internal", StringComparison.Ordinal));
            var guards = lines.Count(l => l.Contains("ThrowIf", StringComparison.Ordinal)
                                       || l.Contains("ArgumentNull", StringComparison.Ordinal)
                                       || l.Contains("ArgumentOutOfRange", StringComparison.Ordinal));
            if (methods > 0 && guards == 0)
            {
                Console.WriteLine($"  {Red}✗{Nc} {f} 有 {methods} 个公开成员但零守卫");
                return 1;   // 原版 ((MISSING++)) 触发 set -e 提前退出——保真
            }
        }
        Console.WriteLine($"  {Green}✓{Nc} 全部含 {key} 的文件均有守卫覆盖");
        break;
    }

    case "truncate":
    {
        Console.WriteLine();
        Console.WriteLine("── 截断/归一化覆盖完整性 ──");
        // 确认零裸切片残留（ERE：[..2000] / [..2040] / [..256]）
        var bareRx = new Regex(@"\[\.\.(2000|2040|256)\]");
        var hits = srcFiles.SelectMany(f => ReadLines(f)
                .Select((text, idx) => (Path: f, Line: idx + 1, Text: text))
                .Where(x => bareRx.IsMatch(x.Text))
                .Select(x => $"{x.Path}:{x.Line}:{x.Text}"))
            .Where(l => !l.Contains("obj", StringComparison.Ordinal)
                     && !l.Contains("FailureReason", StringComparison.Ordinal))
            .ToList();
        // BARE 计数额外排除注释勘正行（列出链不排——原版两条管道不一致，保真）
        var bare = hits.Count(l => !l.Contains("勘正") && !l.Contains("引用"));
        if (bare > 0)
        {
            Console.WriteLine($"  {Red}✗{Nc} {bare} 处裸切片残留");
            hits.ForEach(h => Console.WriteLine(h.TrimEnd('\r')));
            return 1;   // 原版 ((MISSING++)) 触发 set -e 提前退出——保真
        }
        Console.WriteLine($"  {Green}✓{Nc} 零裸切片残留");
        break;
    }

    case "status":
    {
        Console.WriteLine();
        Console.WriteLine("── Status 守卫覆盖 ──");
        // 确认全部终态写（MarkProcessed/MarkDead/MarkFailed）都有 Status 守卫
        // （枚举序：先含任一 Mark* 的文件，再按路径子串排除 obj/test——等价原管道）
        var guardRx = new Regex("Status.*Pending|status.*[Pp]ending|Status ==.*Completed|status.*<>");
        foreach (var f in srcFiles
                     .Where(f => FileContains(f, "MarkProcessed")
                              || FileContains(f, "MarkDead")
                              || FileContains(f, "MarkFailed"))
                     .Where(f => !f.Contains("obj", StringComparison.Ordinal)
                              && !f.Contains("test", StringComparison.Ordinal)))
        {
            if (ReadLines(f).Any(l => guardRx.IsMatch(l))) continue;
            Console.WriteLine($"  {Red}✗{Nc} {f} 含 Mark* 方法但零 Status 守卫");
            return 1;   // 原版 ((MISSING++)) 触发 set -e 提前退出——保真
        }
        break;
    }

    case "detach":
    {
        Console.WriteLine();
        Console.WriteLine("── Detach 族完整性 ──");
        // 全部 DbUpdateException catch 都应有对应 Detach 或明确声明
        var totalCatch = srcFiles
            .SelectMany(f => ReadLines(f).Select(l => $"{f}:{l}"))
            .Count(l => l.Contains("catch (DbUpdateException", StringComparison.Ordinal)
                     && !l.Contains("obj", StringComparison.Ordinal));
        var detached = srcFiles
            .SelectMany(f => ReadLines(f).Select(l => $"{f}:{l}"))
            .Count(l => l.Contains("EntityState.Detached", StringComparison.Ordinal)
                     && !l.Contains("obj", StringComparison.Ordinal));
        Console.WriteLine($"  DbUpdateException catch: {totalCatch} / Detach 引用: {detached}");
        Console.WriteLine("  （人工核对：每个 catch 块应有 Detach 或注释声明为何不需要）");
        break;
    }

    default:
        Console.WriteLine("支持: guard | truncate | status | detach");
        return 1;
}

Console.WriteLine();
Console.WriteLine($"{Green} 完整性验证通过{Nc} — 修复范围覆盖全部姊妹轴");
return 0;

// ─── 工具函数 ───

// 递归枚举 .cs（NTFS 字母混排 + 子目录即时递归——模拟 GNU grep fts 顺序）
static IEnumerable<string> EnumerateCsFiles(string root)
{
    if (!Directory.Exists(root)) yield break;
    var entries = Directory.EnumerateFileSystemEntries(root)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    foreach (var e in entries)
    {
        if (Directory.Exists(e))
        {
            foreach (var f in EnumerateCsFiles(e)) yield return f;
        }
        else if (e.EndsWith(".cs", StringComparison.Ordinal))
        {
            yield return e;
        }
    }
}

// 读文件行（\n 切分，行尾 \r 保留——计数/匹配不受影响，含 \r 的行等价 GNU grep）
static string[] ReadLines(string path) =>
    Encoding.UTF8.GetString(File.ReadAllBytes(path)).Split('\n');

// 文件是否含子串（等价 grep -l，标识符场景 BRE 字面 = 子串匹配）
static bool FileContains(string path, string needle) =>
    Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(needle, StringComparison.Ordinal);

#pragma warning restore CA1303
