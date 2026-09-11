// ============================================================================
// refine-scan.cs——Pal.DDD 精炼扫描 v7.1（MIG-012-B2 双镜像合一，2026-09-11）
// 由 scripts/refine-scan.sh 与 .ai/scripts/refine-scan.sh 等价迁移
// （两份 55 行 bash 逐字节一致、仅脚本位置不同——合一后单一 .cs 维护）。
//
// 用法：dotnet run scripts/refine-scan.cs
// 输出格式：ID [信噪比] 名称: 命中数  — 提示（15 个 grep 计数 + 信噪比标注）
//
// ⚠️ 命中数 ≠ 可改数。采纳任何精炼前，逐条核实语义，并遵循诊断三步骤
//    （改前基线 → 单项验证 → build/test 反向验证）。
// 信噪比标注（基于 2026-07 实测校准）：
//   🟢 命中数 ≈ 可改数（扫描可信）；🟡 命中数 > 可改数（需人工核实）；
//   🔴 命中数 ≫ 可改数（高假阳性，多数不可改，仅作线索）。
// 实测校准基线（commit 4459e23，供偏差参考）：
//   M1 报 15 → 实际可改 5（Array.Empty 命中 ReadOnlyMemory 返回类型不可改）
//   M6 报 19 → 实际可改 0（18 惯用 ?? throw + 1 在 netstandard2.0）
//   M3 报 87 → 实际可改 0（全为 ORM 映射类，required 不适用）
//   O1 报 5  → 实际可改 0（全为 ToFrozenDictionary 构建器/外部 API 传入）
//
// 计数口径（与 bash 版逐条对照，含 grep 管道过滤细节）：
//   count()：模式匹配内容行，整行 = "posix路径:行号:内容" 上剔除含 /obj/ 与
//     /bin/ 的行（带斜杠过滤——路径与内容同口径）；
//   各指标差异化的后续过滤（public 正向、set|init|static|=> 负向、///、Suppress、
//     ?? throw、InMemory、Test 等）均作用于整行（与 bash 管道一致）；
//   A1/A2/A3/A5/M6/O3 的 find/grep 差异过滤（仅 /obj/ 不含 /bin/）逐条保留；
//   A3 行数 = wc -l 口径（统计 '\n' 个数，无尾换行的末行不计）；
//   BRE → .NET 正则转换点：\| → |、{} () + ? 等在 BRE 中为字面量处加转义。
//
// 与 bash 版的口径差异（仅一处，仓库内运行无影响）：仓库根发现锚点为当前目录
// 向上查找 PalDDD.slnx（file-based app 运行时取不到 .cs 源路径，同系列件同款说明）。
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表；本工具输出是固定中文扫描报告行
// （信噪比 emoji + 提示文案），无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文/emoji 输出乱码——对齐 bash UTF-8
Console.OutputEncoding = Encoding.UTF8;

// 仓库根发现：向上找含 PalDDD.slnx 的目录（等价 bash _ai_root_find，锚点为 cwd）
var root = FindRepoRoot();
Directory.SetCurrentDirectory(root);
const string src = "src";

// ─── 数据装载：src/ 全部 .cs（含 obj/bin——bash grep -rn 同样递归进去，靠行过滤剔除）───
// 行内容按 '\n' 切分且保留 '\r'（GNU grep 语义）；文本另存供 A3 的 wc -l 口径计数
var files = new List<(string Path, string Text, string[] Lines)>();
if (Directory.Exists(src))
{
    foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
    {
        var posix = f.Replace('\\', '/');
        // UTF-8 容错解码（无效字节替换 U+FFFD——不影响 ASCII 模式匹配）
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(f));
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0 && text.EndsWith('\n'))
            lines = lines[..^1]; // 尾换行切出的空尾元素不是 grep 意义上的行
        files.Add((posix, text, lines));
    }
    files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path)); // 确定性（grep 顺序不进计数）
}

// 整行（grep -rn 输出形态）：路径:行号:内容
var allLines = files
    .SelectMany(f => f.Lines.Select((line, i) => (Path: f.Path, No: i + 1, Content: line)))
    .ToList();

// ─── count()：内容匹配 + 整行剔除 /obj/ 与 /bin/（等价 bash count 辅助函数）───
int Count(string pattern)
{
    var re = new Regex(pattern);
    return allLines.Count(l => re.IsMatch(l.Content) && !Full(l).Contains("/obj/") && !Full(l).Contains("/bin/"));
}
// 整行 = "路径:行号:内容"（posix 路径与行号均不含冒号，拼接无歧义）
static string Full((string Path, int No, string Content) l) => $"{l.Path}:{l.No}:{l.Content}";

// ─── 15 个指标（先计数后输出，避免插值孔内嵌字面量的转义咬合）───
var a1 = files.Count(f => Path.GetFileName(f.Path) == "AssemblyInfo.cs" && !f.Path.Contains("/obj/"));
var a2 = files.Count(f => Path.GetFileName(f.Path) == "GlobalUsings.cs" && !f.Path.Contains("/obj/"));
var a3 = files.Count(f => !f.Path.Contains("/obj/") && f.Text.Count(c => c == '\n') is > 0 and <= 10);
var a5 = allLines.Count(l => l.Content.StartsWith("using ", StringComparison.Ordinal) && !Full(l).Contains("/obj/"));
var m1a = Count(@"Array\.Empty<");
var m1b = Count(@"new List<[^>]*>\(\)|new Dictionary<[^>]*>\(\)");
var m2 = Count(@"private readonly.*\?\? throw");
var m3 = allLines.Count(l => Regex.IsMatch(l.Content, @"\{ get; \}")
    && !Full(l).Contains("/obj/") && Full(l).Contains("public")
    && !Full(l).Contains("set") && !Full(l).Contains("init")
    && !Full(l).Contains("static") && !Full(l).Contains("=>"));
var m5 = Count(@"string\.Format|String\.Format");
var m6Raw = Count("throw new ArgumentNullException");
var m6 = allLines.Count(l => l.Content.Contains("throw new ArgumentNullException")
    && !Full(l).Contains("/obj/")
    && !Full(l).Contains("///") && !Full(l).Contains("Suppress")
    && !Full(l).Contains("?? throw") && !Full(l).Contains("??throw"));
var o1 = Count("new Dictionary<");
var o2 = m1b; // O2 与 M1b 同模式（bash 版即如此——同一 count 表达式复用）
var o3 = allLines.Count(l => Regex.IsMatch(l.Content, @"\.ToArray\(\)|\.ToList\(\)")
    && !Full(l).Contains("/obj/")
    && !Full(l).Contains("InMemory") && !Full(l).Contains("Test"));
var o6 = Count(@"\+= .*""");

Console.WriteLine("═══════ 一类:减法 ═══════");
Console.WriteLine($"A1 🟢 AssemblyInfo: {a1}  — 删文件→csproj InternalsVisibleTo");
Console.WriteLine($"A2 🟢 GlobalUsings: {a2}  — 删文件→csproj Using 项");
Console.WriteLine($"A3 🟡 标记接口/常量(≤10行): {a3}  — 需核实是否独立语义(enum+record 不合并)");
Console.WriteLine($"A5 🟢 using 行密度: {a5} 行  — dotnet format IDE0005 清冗余");

Console.WriteLine();
Console.WriteLine("═══════ 二类:现代化 ═══════");
Console.WriteLine($"M1a 🟡 Array.Empty<T>(): {m1a}  — ⚠️ 返回类型 byte[]/T[] 可改[]；ReadOnlyMemory/Span/Memory 不可改(CS9174)");
Console.WriteLine($"M1b 🟢 List/Dict 空构造: {m1b}  — new T<>()→[](无容量/comparer 时)");
Console.WriteLine($"M2 🔴 ?? throw 字段初始化: {m2}  — 高假阳性：主构造函数在继承链/ORM 类风险高，多不可下沉");
Console.WriteLine($"M3 🔴 public {{get;}}: {m3}  — 高假阳性：ORM 映射类不能用 required(需无参构造)");
Console.WriteLine($"M5 🟢 string.Format: {m5}  — →$\"{{x}}\" 插值");
Console.WriteLine($"M6 🟡 独立 throw ArgumentNullException: {m6} (总 {m6Raw}, 排除 ?? throw 惯用法)  — →ThrowIfNull；⚠️ netstandard2.0 项目(Analyzers/SourceGen)不支持此 API");

Console.WriteLine();
Console.WriteLine("═══════ 三类:优化 ═══════");
Console.WriteLine($"O1 🔴 new Dictionary<>: {o1}  — 高假阳性：ToFrozenDictionary 构建器/外部 API(comparer/headers)传入，多不可改");
Console.WriteLine($"O2 🟢 List/Dict 无预分配: {o2}  — new(N) 预分配(已知容量时)");
Console.WriteLine($"O3 🟡 ToArray/ToList: {o3}  — 热路径→Span<T> 零分配(非热路径不改)");
Console.WriteLine($"O6 🟡 string +=: {o6}  — 循环内→StringBuilder/插值(单次拼接不改)");

Console.WriteLine();
Console.WriteLine("═══ 扫描完成 · 命中数≠可改数，逐条核实后按诊断三步骤采纳 ═══");
return 0;

static string FindRepoRoot()
{
    var d = new DirectoryInfo(Environment.CurrentDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "PalDDD.slnx")))
        d = d.Parent!;
    if (d is null)
    {
        Console.Error.WriteLine("错误：未找到仓库根（向上未发现 PalDDD.slnx）——请在仓库内运行");
        Environment.Exit(2);
    }
    return d.FullName;
}
