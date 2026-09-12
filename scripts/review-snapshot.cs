// ============================================================================
// review-snapshot.cs——评审基线快照（MIG-012-B2 双镜像合一，2026-09-11）
// 由 scripts/review-snapshot.sh 与 .ai/scripts/review-snapshot.sh 等价迁移
// （两份 71 行 bash 逐字节一致、仅脚本位置不同——合一后单一 .cs 维护）。
//
// 用法：dotnet run scripts/review-snapshot.cs
// 产出：评审报告首行所需的基线数据（commit + 项目/文件/测试数 + 关键计数）。
// 目的：消除元审计 R3（过期快照）和 R8（采信记忆）——所有评审断言锚定同一快照。
// 评审报告首行应粘贴本工具输出：
//   > 评审基线：dotnet run scripts/review-snapshot.cs 的输出
//
// 计数口径（与 bash 版逐条对照，含过滤细节）：
//   项目/文件计数：find -not -path 语义——posix 路径含 /obj/（及对应的 /bin/、
//     test/PalDDD.Testing/ 前缀）即剔除；
//   PDDD 规则数：grep -roh 输出裸匹配串（无文件名），原管道 grep -v obj 对
//     "PDDD0n" 串是 no-op——本版含 obj 一起搜（实测 obj 下无 PDDD 串，计数不变）；
//   AOT 计数：只匹配属性行 ^\s*<IsAotCompatible>…（自审计 A7 修复口径），
//     避免Description 散文污染；true 侧仅显式声明 true 的项目（8 个 Dapper/PalORM），
//     其余 14 个核心项目经 Directory.Build.props 全局 true 继承；
//   异常过滤：原管道 grep -v obj / grep -v bin / grep -v .pal 过滤的是
//     "路径:行号:内容" 整行（无斜杠的子串过滤）——本版按同口径拼整行后过滤，
//     内容含这些子串的行同样被剔除（与 bash 一致）。
//
// 与 bash 版的口径差异（仅一处，仓库内运行无影响）：仓库根发现锚点为当前目录
// 向上查找 PalDDD.slnx（file-based app 运行时取不到 .cs 源路径，详见各件同款说明）。
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表；本工具输出是评审报告粘贴用的
// 固定中文标签行，无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文/制表线输出乱码——对齐 bash UTF-8
Console.OutputEncoding = Encoding.UTF8;

// 仓库根发现：向上找含 PalDDD.slnx 的目录（等价 bash _ai_root_find，锚点为 cwd）
var root = FindRepoRoot();
Directory.SetCurrentDirectory(root);

Console.WriteLine("═══ 评审基线快照 ═══");
Console.WriteLine($"时间: {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}");
Console.WriteLine($"Commit: {Git("rev-parse", "HEAD")}");
Console.WriteLine($"分支: {Git("rev-parse", "--abbrev-ref", "HEAD")}");
Console.WriteLine();

// ── 项目计数 ──
var srcProjects = EnumFiles("src", "*.csproj").Count(p => !p.Contains("/obj/"));
var testProjects = EnumFiles("test", "*.csproj")
    .Count(p => !p.Contains("/obj/") && !p.StartsWith("test/PalDDD.Testing/", StringComparison.Ordinal));
Console.WriteLine("── 项目计数 ──");
Console.WriteLine($"源项目数: {srcProjects}");
Console.WriteLine($"测试项目数(排除Testing): {testProjects}");
Console.WriteLine();

// ── 文件计数 ──
var srcFiles = EnumFiles("src", "*.cs").Count(p => !p.Contains("/obj/") && !p.Contains("/bin/"));
var testFiles = EnumFiles("test", "*.cs").Count(p => !p.Contains("/obj/") && !p.Contains("/bin/"));
Console.WriteLine("── 文件计数 ──");
Console.WriteLine($"源文件数: {srcFiles}");
Console.WriteLine($"测试文件数: {testFiles}");
Console.WriteLine();

// ── 架构守护 ──
var archFile = "test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs";
var archTests = File.Exists(archFile)
    ? File.ReadLines(archFile).Count(l => l.Contains("[Test]")).ToString()
    : "?"; // 文件缺失时 bash 版 $(... || echo "?") 同样给 "?"
var pdddRuleRe = new Regex("PDDD0[0-9]+");
var pdddRules = EnumFiles("src/PalDDD.Analyzers", "*.cs") // 含 obj——见文件头口径说明
    .SelectMany(p => pdddRuleRe.Matches(File.ReadAllText(p)).Select(m => m.Value))
    .Distinct()
    .Count();
Console.WriteLine("── 架构守护 ──");
Console.WriteLine($"架构边界测试用例数: {archTests}");
Console.WriteLine($"PDDD 诊断规则数: {pdddRules}");
Console.WriteLine();

// ── AOT 配置 ──
var aotTrue = CountCsprojWithAot("true");
var aotFalse = CountCsprojWithAot("false");
Console.WriteLine("── AOT 配置 ──");
Console.WriteLine($"IsAotCompatible=true 项目（显式声明口径）: {aotTrue}");
Console.WriteLine($"IsAotCompatible=false 项目: {aotFalse}");
Console.WriteLine();

// ── 异常过滤 ──
// 整行 = "posix路径:行号:内容"；obj/bin/.pal 为无斜杠子串过滤（剔除 obj/bin 路径，
// 同时按 bash 同口径剔除内容含这些子串的行）
var srcCsLines = EnumFiles("src", "*.cs") // grep -rn 同样递归进 obj/bin，靠行过滤剔除
    .SelectMany(p => File.ReadAllLines(p).Select((line, i) => $"{p}:{i + 1}:{line}"))
    .ToList();
var catchAll = srcCsLines.Count(l => l.Contains("catch (Exception")
    && !l.Contains("obj") && !l.Contains("bin") && !l.Contains(".pal"));
var oceFilter = srcCsLines.Count(l => l.Contains("OperationCanceledException")
    && !l.Contains("obj") && !l.Contains("bin") && !l.Contains(".pal"));
Console.WriteLine("── 异常过滤 ──");
Console.WriteLine($"catch(Exception) 总数: {catchAll}");
Console.WriteLine($"OperationCanceledException 引用数: {oceFilter}");
Console.WriteLine();

Console.WriteLine("── 测试状态（需手动运行 dotnet test 获取精确通过/失败数）──");
Console.WriteLine("命令: 逐项目 dotnet test <project>.csproj --no-build --nologo（MTP 一次一个，批量会 handshake exit 5）");
Console.WriteLine();

Console.WriteLine("═══ 快照结束 — 粘贴以上内容到评审报告首行 ═══");
return 0;

// ─── 辅助 ───

static int CountCsprojWithAot(string trueFalse)
{
    // 等价 grep -rlE '^\s*<IsAotCompatible>{v}</IsAotCompatible>' src/ --include="*.csproj" | grep -v obj | wc -l
    var re = new Regex(@"^\s*<IsAotCompatible>" + trueFalse + @"</IsAotCompatible>");
    return EnumFiles("src", "*.csproj")
        .Where(p => !p.Contains("obj"))
        .Count(p => File.ReadLines(p).Any(re.IsMatch));
}

static List<string> EnumFiles(string rootDir, string pattern)
{
    // 递归枚举并归一为 posix 相对路径（bash grep/find 路径形态，供前缀/子串过滤）
    var list = new List<string>();
    if (!Directory.Exists(rootDir)) return list;
    foreach (var f in Directory.EnumerateFiles(rootDir, pattern, SearchOption.AllDirectories))
        list.Add(f.Replace('\\', '/'));
    return list;
}

static string Git(params string[] arguments)
{
    var psi = new ProcessStartInfo("git")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd().Trim();
    p.WaitForExit();
    return output;
}

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
