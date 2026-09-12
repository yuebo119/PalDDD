// ============================================================================
// tech-debt.cs——Pal.DDD 技术债扫描（MIG-012-A1，2026-09-11）
// 由 .ai/scripts/tech-debt-scan.sh 等价迁移为 C#（dotnet file-based app）。
// 残余 12 项（#1-#7/#9/#10/#12/#21/#22）；#8/#11/#13-#20 已下沉
// TechDebtGuardTests / DiagnosticCoverageGateTests（编号保留不重排，见 bash 头注释）。
//
// 用法：dotnet run scripts/tech-debt.cs
// 退出码：0=通过（FAIL=0）；1=有 FAIL 项（allow/WARN 为口径透明项不阻断）。
//
// 等价迁移说明（相对 .ai/scripts/tech-debt-scan.sh）：
//   1) 仓库根发现：从本 cs 源文件位置（编译期 CallerFilePath）向上找 PalDDD.slnx
//      ——等价 bash _ai_root_find，与调用方 cwd 无关。
//   2) grep/find/awk 全部改 C# 逐行匹配：行级排除（'obj/|bin/'）基于整行
//      "路径:行号:内容"（bash grep -v 同口径——内容含 obj/ 也排除）；路径级排除
//      （*/obj/* 等）基于相对路径段。文件枚举序为 Ordinal 稳定排序（bash grep -r
//      为文件系统序），仅影响显示行序，计数与 PASS/FAIL/ALLOW 结论一致。
//   3) #22 的 git ls-files 'src/**/*.csproj' 经 Process 原样传参（git 自行展开
//      glob，等价 bash 单引号防展开）。
//   4) #6/#7 行长判定用 string.Length（UTF-16 码元）——bash awk length 在 UTF-8
//      locale 下按字符计，仅非 BMP 字符（代理对）有 ±差，180 边界翻转风险可忽略。
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的固定
// 协议行（PASS/FAIL/ALLOW/WARN 关键字 + 中文口径说明，留档 diff 逐字符比对），
// 无本地化需求——沿 osc-check.cs / vuln-scan.cs 先例文件级禁用
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文输出对齐 bash UTF-8（沿 osc-check.cs 先例）
Console.OutputEncoding = Encoding.UTF8;

var ROOT = FindRepoRoot();

int pass = 0, fail = 0, allowed = 0, warned = 0;

Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine($" Pal.DDD 技术债扫描 ({DateTime.Now:yyyy-MM-dd HH:mm})");
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine();

// ─── 1. [Obsolete] 残留（有明确移除计划的标记为 allow）───
// WARN 单列（自审计：不再计入 PASS），显示前 5 行
var obsolete = GrepDirs(["src", "test", "tools", "bench"],
    line => line.Contains("[Obsolete"), ["obj/", "bin/"]);
if (obsolete.Count == 0)
{
    Console.WriteLine("PASS  1. [Obsolete] 残留 (0 处)");
    pass++;
}
else
{
    Console.WriteLine($"WARN  1. [Obsolete] 残留 ({obsolete.Count} 处——有明确移除计划的允许)");
    foreach (var l in obsolete.Take(5)) Console.WriteLine($"      {l}");
    warned++;
}

// ─── 2. TODO/HACK/FIXME/XXX 注释（排除 bench/BenchmarkDotNet/ fork 源码）───
Check("2. TODO/HACK/FIXME 注释",
    GrepDirs(["src", "test", "tools", "bench"],
        new Regex(@"// TODO|// HACK|// FIXME|// XXX").IsMatch,
        ["obj/", "bin/", "bench/BenchmarkDotNet/"]),
    "strict");

// ─── 3. Console.WriteLine 在 src/ ───
Check("3. Console.WriteLine 在 src/",
    GrepDirs(["src"], line => line.Contains("Console."), ["obj/", "bin/"]),
    "strict");

// ─── 4. 空 catch 无注释 ───
Check("4. 空 catch 无注释",
    GrepDirs(["src", "test"],
        new Regex(@"catch.*\{\}").IsMatch, ["obj/", "bin/"]),
    "strict");

// ─── 5. tab 字符（应为 space）——文件级命中清单（等价 grep -l）───
Check("5. tab 字符（应为 space）",
    EnumerateCs(ROOT, "src")
        .Where(f => !ContainsSegment(f.Rel, "obj", "bin"))
        .Where(f => File.ReadLines(f.Abs).Any(l => l.Contains('\t')))
        .Select(f => f.Rel)
        .ToList(),
    "strict");

// ─── 6. src/ 超长行 > 180（SourceGen Emitter + 生成的 SQL 字符串允许）───
Check("6. src/ 超长行 > 180（SourceGen 允许）",
    LongLines("src", ["obj", "bin", "PalDDD.Core.SourceGen"]).Take(20).ToList(),
    "allow");   // Core 超长行多为多列 Select/聚合方法签名——拆行损害可读性

// ─── 7. test/ 超长行 > 180（AotTest + Provider 测试允许）───
Check("7. test/ 超长行 > 180（AotTest 允许）",
    LongLines("test", ["obj", "bin"]).Take(20).ToList(),
    "allow");   // PgTests/MySqlTests 单行 DDL 语句——SQL 语义紧凑

// ─── 9. 测试用例数（grep [Test] 标记数，不跑测试）───
var allTestCount = EnumerateCs(ROOT, "test")
    .Where(f => !ContainsSegment(f.Rel, "obj", "bin"))
    .Sum(f => File.ReadLines(f.Abs).Count(l => l.Contains("[Test]")));
if (allTestCount >= 400)
{
    Console.WriteLine($"PASS  9. 测试用例数充足（[Test] 标记 {allTestCount} 个）");
    pass++;
}
else
{
    Console.WriteLine($"FAIL  9. 测试用例数不足（[Test] 标记 {allTestCount} 个，预期 ≥400）");
    fail++;
}

// ─── 10. 版本管理一致性（DDD 用 Directory.Build.props 统一管）───
var dbpPath = Path.Combine(ROOT, "Directory.Build.props");
var dbpVer = File.Exists(dbpPath)
    ? new Regex(@"<VersionPrefix>[^<]+").Match(File.ReadAllText(dbpPath)) is { Success: true } m
        ? m.Value["<VersionPrefix>".Length..]
        : null
    : null;
if (dbpVer is not null)
{
    Console.WriteLine($"PASS  10. 版本统一管理（Directory.Build.props VersionPrefix={dbpVer}）");
    pass++;
}
else
{
    Console.WriteLine("FAIL  10. Directory.Build.props 缺少 VersionPrefix");
    fail++;
}

// ─── 12. .gitignore 关键排除项（DDD 实际配置）───
// bash 口径：缺第一个即报一次 FAIL 并 break（不重复计数）；全存在才 PASS
var gitignoreText = File.Exists(Path.Combine(ROOT, ".gitignore"))
    ? File.ReadAllText(Path.Combine(ROOT, ".gitignore")) : "";
string? missingPattern = null;
foreach (var pattern in (string[])["appsettings.test.local.json", "bin/", "obj/"])
{
    if (!gitignoreText.Contains(pattern))
    {
        missingPattern = pattern;
        break;
    }
}
if (missingPattern is null)
{
    Console.WriteLine("PASS  12. .gitignore 关键排除项完整");
    pass++;
}
else
{
    Console.WriteLine($"FAIL  12. .gitignore 缺少排除项: {missingPattern}");
    fail++;
}

// ─── 21. MTP 批量/--collect 禁令（PD27：一次一项目 + 原生 --coverage）───
// 扫描 CI/脚本层命令；排除：.ai/scripts/tech-debt-scan.sh 自身行、行内容以 # 开头
// 的注释行、'exit 5' 字面、含 禁用/弃用/注释 的说明行（与 bash 管道过滤逐项等价）
var mtpRx = new Regex(@"dotnet test [^ ]*\.slnx|--collect");
var commentLineRx = new Regex(@"^[^:]+:[0-9]+:\s*#");
var scanTargets = new List<string> { ".github/workflows/ci.yml", "ci-coverage.sh" };
scanTargets.AddRange(ShFilesIn("scripts"));        // scripts/*.sh（存在才扫，glob 无匹配时 bash 传字面量给 grep → no such file 吞掉）
scanTargets.AddRange(ShFilesIn(".ai/scripts"));    // .ai/scripts/*.sh
var mtpViolations = new List<string>();
foreach (var rel in scanTargets)
{
    var abs = Path.Combine(ROOT, rel);
    if (!File.Exists(abs)) continue;
    var ln = 0;
    foreach (var line in File.ReadLines(abs))
    {
        ln++;
        if (!mtpRx.IsMatch(line)) continue;
        var row = $"{rel}:{ln}:{line}";
        if (row.StartsWith(".ai/scripts/tech-debt-scan.sh:", StringComparison.Ordinal)) continue;   // bash 排除自身（迁移保真：集合与排除规则不变）
        if (commentLineRx.IsMatch(row)) continue;
        if (row.Contains("exit 5")) continue;
        if (row.Contains("禁用") || row.Contains("弃用") || row.Contains("注释")) continue;
        mtpViolations.Add(row);
    }
}
Check("21. MTP 批量/--collect 禁令（PD27：一次一项目 + 原生 --coverage）",
    mtpViolations, "strict");

// ─── 22. slnx 成员完整性（PD28：公开元包不在解决方案 = 发布完整性缺口）───
// 每个 src/**/*.csproj 必须以 "路径" 带引号形态出现在 PalDDD.slnx
var slnxGaps = new List<string>();
var slnxText = File.Exists(Path.Combine(ROOT, "PalDDD.slnx"))
    ? File.ReadAllText(Path.Combine(ROOT, "PalDDD.slnx")) : "";
foreach (var csproj in Git("ls-files src/**/*.csproj").Output
             .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
{
    if (!slnxText.Contains($"\"{csproj}\"")) slnxGaps.Add(csproj);
}
// bash 口径：gaps 以空格拼接为单行入 check（计数显示 "1 处"，前导空格为拼接产物）
Check("22. slnx 成员完整性（src csproj 全覆盖，PD28）",
    slnxGaps.Count == 0 ? [] : [" " + string.Join(" ", slnxGaps)],
    "strict");

// ─── 汇总 ───
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine($" 结果: {pass} 通过 / {allowed} 允许(已知债务) / {warned} 提示 / {fail} 失败");
Console.WriteLine(" 阻断退出：仅 FAIL>0；allow/WARN 为口径透明项（自审计 P3 修复）");
Console.WriteLine("═══════════════════════════════════════════");

return fail > 0 ? 1 : 0;

// ─── 局部函数 ───

// check() 等价：strict>0 → FAIL（前 5 行）；allow>0 → ALLOW 单列（前 3 行）；
// 否则 PASS。count = 非空行数（bash grep -c .）
void Check(string name, List<string> result, string allow)
{
    var count = result.Count(l => l.Length > 0);
    if (allow == "strict" && count > 0)
    {
        Console.WriteLine($"FAIL  {name} ({count} 处)");
        foreach (var l in result.Take(5)) Console.WriteLine($"      {l}");
        fail++;
    }
    else if (allow == "allow" && count > 0)
    {
        Console.WriteLine($"ALLOW {name} ({count} 处)");
        foreach (var l in result.Take(3)) Console.WriteLine($"      {l}");
        allowed++;
    }
    else
    {
        Console.WriteLine($"PASS  {name} ({count} 处)");
        pass++;
    }
}

// 仓库根发现：从本 cs 源文件位置向上找含 PalDDD.slnx 的目录（等价 bash _ai_root_find）
static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string src = "")
{
    var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(src)
        ? Environment.CurrentDirectory
        : Path.GetDirectoryName(src)!);
    while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("未找到仓库根（PalDDD.slnx）——请从仓库内运行");
}

// 递归枚举各顶层目录下全部 .cs（Ordinal 稳定序；目录不存在跳过——bash grep 报错吞掉同路径）
static List<(string Rel, string Abs)> EnumerateCs(string root, params string[] topDirs)
{
    var files = new List<(string Rel, string Abs)>();
    foreach (var d in topDirs)
    {
        var abs = Path.Combine(root, d);
        if (!Directory.Exists(abs)) continue;
        foreach (var f in Directory.EnumerateFiles(abs, "*.cs", SearchOption.AllDirectories))
            files.Add((ToPosix(Path.GetRelativePath(root, f)), f));
    }
    files.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
    return files;
}

// grep -rn 等价：逐文件逐行匹配 → "相对路径:行号:内容"；行级排除对整行做子串过滤
//（bash grep -v 同口径——内容含排除串的行同样剔除）
static List<string> GrepDirs(string[] topDirs, Func<string, bool> match, string[] lineExcludes)
{
    var root = FindRepoRoot();
    var rows = new List<string>();
    foreach (var f in EnumerateCs(root, topDirs))
    {
        var ln = 0;
        foreach (var line in File.ReadLines(f.Abs))
        {
            ln++;
            if (!match(line)) continue;
            var row = $"{f.Rel}:{ln}:{line}";
            if (Array.Exists(lineExcludes, row.Contains)) continue;
            rows.Add(row);
        }
    }
    return rows;
}

// find+awk 超长行等价：路径段排除 + length>180 → "相对路径:行号"
static List<string> LongLines(string topDir, string[] excludeSegments)
{
    var root = FindRepoRoot();
    var rows = new List<string>();
    foreach (var f in EnumerateCs(root, topDir))
    {
        if (ContainsSegment(f.Rel, excludeSegments)) continue;
        var ln = 0;
        foreach (var line in File.ReadLines(f.Abs))
        {
            ln++;
            if (line.Length > 180) rows.Add($"{f.Rel}:{ln}");
        }
    }
    return rows;
}

// 路径段排除等价（bash -path '*/obj/*'）：rel 含 "/<seg>/" 段
static bool ContainsSegment(string relPath, params string[] segments) =>
    segments.Any(s => relPath.Contains($"/{s}/"));

// 指定顶层目录下 *.sh 清单（glob 字母序展开等价；目录不存在 → 空）
static List<string> ShFilesIn(string topDir)
{
    var root = FindRepoRoot();
    var abs = Path.Combine(root, topDir);
    if (!Directory.Exists(abs)) return [];
    return Directory.EnumerateFiles(abs, "*.sh")
        .Select(f => ToPosix(Path.Combine(topDir, Path.GetFileName(f))))
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToList();
}

// Windows 路径 → POSIX 相对路径形态（与 bash grep/find 输出一致）
static string ToPosix(string p) => p.Replace('\\', '/');

// git 调用：工作目录=仓库根；stdout 捕获（stderr 丢弃——等价 bash 2>/dev/null）
static (int ExitCode, string Output) Git(string gitArgs)
{
    try
    {
        var psi = new ProcessStartInfo("git", gitArgs)
        {
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
    catch (Exception e) when (e is Win32Exception or InvalidOperationException)
    {
        return (-1, "");
    }
}
