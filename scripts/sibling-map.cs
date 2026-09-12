// ============================================================================
// sibling-map.cs——姊妹族清单生成器·轴 A 机械枚举（MIG-011d，2026-09-11）
// 由 .ai/scripts/sibling-map.sh 内嵌 68 行 python 等价迁移为 C#（file-based app）。
//
// 用法：dotnet run scripts/sibling-map.cs [-- 接口名过滤词]
//   过滤词大小写不敏感，对接口名做子串匹配（如 Outbox、Idempotency）；缺省输出全部族。
//
// MIG-012-B2（2026-09-11）：补齐 .ai/scripts/sibling-map.sh 包装件的仓库根定位——
//   从当前目录向上找 PalDDD.slnx 并切换后枚举（file-based app 编译进用户临时缓存，
//   运行时取不到自身 .cs 路径，锚点为 cwd；仓库内任意子目录运行与 bash 包装件一致；
//   从仓库根运行行为不变）。FILTER 过滤词透传（args[0]）原已支持，包装件仅原样透传。
//
// 逻辑（与原 python 版逐项对应）：
//   1) 类型声明正则解析：class|record|struct|interface + 大写开头的类型名（可带 <T>
//      与主构造参数表），基类/基接口列表取到 {、= 或换行前；
//      kind 用捕获组（关键词在匹配起点，回看窗口取不到——原版初版 bug 已勘正的写法）。
//   2) partial 合并：同名类型后见声明只追加基列表（注意：原 python 追加时不做 strip，
//      带空格的基名在 stripGeneric 处匹配失败被忽略——本版保持同样行为，不改"缺陷"，
//      避免族集合与迁移前漂移）。
//   3) 传递闭包：沿基类/基接口链 BFS，收集可达的「项目内声明的接口」
//      （仅 src/ 中 kind==interface 的类型；BCL 接口天然排除）。
//   4) 族分组：接口 → {(文件, 实现类)}，输出 2+ 实现的族（markdown 表）。
//
// 输出格式与原 python 版逐行一致（族集合/排序/过滤语义均保持）：
//   行序 = 实现数降序、接口名升序（code point 序）；组内实现按 (文件, 类) 升序；
//   <br> 连接。轴 B（管线孪生）不在本工具范围（见 .ai/review/sibling-map.md 种子表）。
//
// 退出码：0（信息工具，非门禁）；src/ 缺失输出空表同样 0。
// 文件遍历排除：/obj/、/bin/、*.g.cs（生成代码）；文本按 UTF-8 容错解码
// （等价 python errors='replace'）。
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文输出会乱码——对齐 python UTF-8
Console.OutputEncoding = Encoding.UTF8;

// MIG-012-B2：仓库根定位（等价 sibling-map.sh 包装件 _ai_root_find + cd）——
// src/ 枚举锚定仓库根，仓库内任意子目录可运行；找不到根时报错退出 2
//（此前依赖调用方 cd 到仓库根，子目录运行会输出空表）
{
    var d = new DirectoryInfo(Environment.CurrentDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "PalDDD.slnx")))
        d = d.Parent!;
    if (d is null)
    {
        Console.Error.WriteLine("错误：未找到仓库根（向上未发现 PalDDD.slnx）——请在仓库内运行");
        return 2;
    }
    Directory.SetCurrentDirectory(d.FullName);
}

var filt = args.Length > 0 ? args[0] : "";

// 类型声明正则（与原 python decl 逐字对应；python re 与 .NET Regex 均为 Unicode 模式）
var decl = new Regex(
    @"\b(class|record|struct|interface)\s+([A-Z]\w*)"
    + @"(?:<[^<>]*>)?(?:\([^)]*\))?\s*(?::\s*([^{=\n]+))?");

// 类型名 → 声明信息（首见胜出——partial 后见只追加基列表）
// 元组：(Kind, File, Bases)；Bases 为 List 引用，partial 合并时原地追加
var types = new Dictionary<string, (string Kind, string File, List<string> Bases)>();

// src/ 递归收集 .cs（排除 obj/bin/.g.cs），按 posix 相对路径排序保证确定性
// （python pathlib.rglob 顺序未定义；排序后首见归属稳定，族集合对账见迁移记录）
var files = new List<string>();
if (Directory.Exists("src"))
{
    foreach (var f in Directory.EnumerateFiles("src", "*.cs", SearchOption.AllDirectories))
    {
        var posix = f.Replace('\\', '/');
        if (posix.Contains("/obj/") || posix.Contains("/bin/")
            || posix.EndsWith(".g.cs", StringComparison.Ordinal))
            continue;
        files.Add(posix);
    }
}
files.Sort(StringComparer.Ordinal);

foreach (var path in files)
{
    // UTF-8 容错解码（等价 python read_text(errors='replace')：无效字节替换 U+FFFD）
    var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
    foreach (Match m in decl.Matches(text))
    {
        var kind = m.Groups[1].Value;
        var name = m.Groups[2].Value;
        var bases = m.Groups[3].Value;
        if (types.TryGetValue(name, out var info))
        {
            // partial 多声明：合并基列表——保持原 python 行为，追加的项不 strip
            //（带空格的基名在 StripGeneric 匹配失败被忽略，与迁移前行为一致）
            if (!string.IsNullOrEmpty(bases))
                foreach (var b in bases.Split(','))
                    if (b.Trim().Length > 0) info.Bases.Add(b);
            continue;
        }
        var t = (Kind: kind, File: path, Bases: new List<string>());
        if (!string.IsNullOrEmpty(bases))
            foreach (var b in bases.Split(','))
                if (b.Trim().Length > 0) t.Bases.Add(b.Trim());
        types[name] = t;
    }
}

// 基名剥泛型：取开头的大写标识符（等价 python re.match(r'([A-Z]\w*)', b)）
// 注意：带命名空间限定的基（A.B.IFoo）会取到首段——与原版一致，不"修正"
static string? StripGeneric(string b)
{
    var m = Regex.Match(b, "^[A-Z]\\w*");
    return m.Success ? m.Value : null;
}

// 项目内声明的接口集合（BCL 接口天然排除——不在 src/ types 里）
var projIfaces = types.Where(kv => kv.Value.Kind == "interface")
    .Select(kv => kv.Key).ToHashSet();

// 传递闭包：name 沿基类/基接口链 BFS 收集可达接口
// （等价 python 递归 + 共享 seen 防环；起点不占 seen 名额——同名自环无害）
HashSet<string> TransitiveIfaces(string name)
{
    var seen = new HashSet<string>();
    var result = new HashSet<string>();
    var queue = new Queue<string>();
    queue.Enqueue(name);
    while (queue.Count > 0)
    {
        if (!types.TryGetValue(queue.Dequeue(), out var t)) continue;
        foreach (var b in t.Bases)
        {
            var base0 = StripGeneric(b);
            if (base0 == null || seen.Contains(base0)) continue;
            seen.Add(base0);
            if (projIfaces.Contains(base0)) result.Add(base0);
            queue.Enqueue(base0);
        }
    }
    return result;
}

// 接口 → {(文件, 实现类)}（非接口类型；元组去重——同名类型多文件算不同实现点）
var impl = new Dictionary<string, HashSet<(string File, string Cls)>>();
foreach (var (name, t) in types)
{
    if (t.Kind == "interface") continue;
    foreach (var ifc in TransitiveIfaces(name))
    {
        if (!impl.TryGetValue(ifc, out var set))
        {
            set = new HashSet<(string, string)>();
            impl[ifc] = set;
        }
        set.Add((t.File, name));
    }
}

// ─── 输出（逐行对齐原 python 版）───
var families = impl.Where(kv => kv.Value.Count >= 2).ToList();

// Justification: CA1303 要求 UI 文案走资源表本地化；本工具输出是 markdown 报告
// 固定表头/说明行，无本地化需求——沿 vuln-scan.cs / osc-check.cs 先例
#pragma warning disable CA1303
Console.WriteLine($"# 轴 A：接口 → 多实现族（2+ 实现，传递闭包，仅项目内接口；{DateTime.Today:yyyy-MM-dd}）");
Console.WriteLine();
Console.WriteLine("| 接口 | 实现数 | 实现清单（文件:类） |");
Console.WriteLine("|------|:------:|----------------------|");
#pragma warning restore CA1303

// 排序：实现数降序、接口名升序（python sorted key=(-len, x) 的 code point 序）
foreach (var kv in families
             .OrderByDescending(x => x.Value.Count)
             .ThenBy(x => x.Key, StringComparer.Ordinal))
{
    // 过滤词：非空且接口名不含（大小写不敏感）→ 跳过（python filt.lower() in k.lower() 取反）
    if (filt.Length > 0
        && !kv.Key.Contains(filt, StringComparison.OrdinalIgnoreCase)) continue;
    var items = kv.Value
        .OrderBy(x => x.File, StringComparer.Ordinal)
        .ThenBy(x => x.Cls, StringComparer.Ordinal)
        .ToList();
    var joined = string.Join("<br>", items.Select(x => $"{x.File}:{x.Cls}"));
    Console.WriteLine($"| {kv.Key} | {items.Count} | {joined} |");
}

// Justification: 同上——固定说明行，无本地化需求
#pragma warning disable CA1303
Console.WriteLine();
Console.WriteLine("说明：轴 B（管线孪生 Inbox↔Outbox 等无共同接口的语义姊妹）见 .ai/review/sibling-map.md 种子表；");
Console.WriteLine("联动判据（三选一）、>3 文件熔断与增长规则同样见该表。");
#pragma warning restore CA1303
return 0;
