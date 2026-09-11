// ============================================================================
// verify-conventions.cs——Pal.DDD 规范验证（MIG-012-A2，2026-09-11）
// 由 scripts/verify-conventions.sh（176 行）等价迁移为 C#（dotnet file-based app）。
//
// 用法：
//   dotnet run scripts/verify-conventions.cs               # 全部检查（grep + build + test）
//   dotnet run scripts/verify-conventions.cs -- --quick    # 仅 grep 静态检查（秒级）
//   dotnet run scripts/verify-conventions.cs -- --build    # grep + build（不含 test）
//
// 检查项（V5-V7，与原 bash 逐项对应）：
//   V5. TODO / HACK / FIXME / WORKAROUND 扫描（行级正则）
//   V6. dotnet build 零错误零警告（Process 调用 + 输出解析，双语锚定口径）
//   V7. dotnet test 零失败（逐项目执行——MTP 协议：slnx 批量触发 VSTest 握手
//       exit 5，2026-08-16 终验轮 B-3 实测复现；PalDDD.Testing 为支持库非测试项目）
//
// 已下沉判定（MIG-007/008/009，与原 bash 头注释一致）：
//   V1 零反射族 / V2 async void / V3 .Result / V4 .Wait() 由
//   test/PalDDD.Core.Tests/SourceCodeGuardTests.cs 承接（Roslyn 语法树判定），
//   由 V7 的 dotnet test 阶段执行；--quick 模式仅剩 V5 TODO 扫描。
//
// 迁移说明：
//   1) V6 判定复刻"锚定数字边界"：正向 (^|[^0-9])0( 个错误|…) + 负向
//      [1-9][0-9]* (个错误|…)——"10 个错误"包含"0 个错误"子串，纯子串匹配
//      会让坏构建假通过（三十五轮 A2 门禁假绿教训）。
//   2) 原版输出带 ANSI 颜色码；本版不输出颜色码，文本逐行一致（双跑归一
//      颜色码后 diff 验证）。WARN: unknown MODE 行照原版复刻。
//   3) 子进程输出编码：dotnet CLI 重定向输出为 UTF-8，显式指定
//      StandardOutputEncoding 避免 GBK 控制台默认编码把中文摘要读花。
//   4) 仓库根定位同 verify-ai.cs（CWD 向上找 PalDDD.slnx，BaseDirectory 兜底）。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的
// 固定协议行（✅/❌ + 英文关键词被 grep 消费），固定中文非用户可配文案——沿
// vuln-scan.cs / verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文/emoji 输出会乱码——对齐 bash echo -e UTF-8
Console.OutputEncoding = Encoding.UTF8;

// ─── 仓库根定位 ───
var rootDir = FindRepoRoot();
Environment.CurrentDirectory = rootDir;

// ─── 参数解析（照原版：MODE=第一个参数，默认 full；未知值打 WARN）───
var mode = args.Length > 0 ? args[0] : "full";
if (mode is not ("full" or "quick" or "build"))
    Console.WriteLine($"WARN: unknown MODE: {mode}");

var fail = 0;

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  Pal.DDD 规范验证（mode: {mode}）");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

// ─── V5：grep 静态检查（所有模式都执行）───
// V1-V4（零反射族/async void/.Result/.Wait）已下沉 SourceCodeGuardTests（见头注释）
{
    var matches = new List<string>();
    foreach (var f in EnumerateSrcCs("src"))
    {
        var posix = ToPosix(f);
        var lineno = 0;
        foreach (var content in File.ReadLines(f))
        {
            lineno++;
            if (!Regex.IsMatch(content, "TODO|HACK|FIXME|WORKAROUND")) continue;
            // 构造 grep -rn 输出行后按原版三重子串过滤（/obj/、/bin/、:[[:space:]]*//）
            var line = $"{posix}:{lineno}:{content}";
            if (line.Contains("/obj/") || line.Contains("/bin/")) continue;
            if (Regex.IsMatch(line, ":[ \\t\\r\\n\\f\\v]*//")) continue;
            matches.Add(line);
        }
    }
    if (matches.Count > 0)
    {
        Console.WriteLine($"❌ 禁止 TODO/HACK/FIXME 违反");
        foreach (var m in matches.Take(10)) Console.WriteLine(m);
        Console.WriteLine();
        fail = 1;
    }
    else Console.WriteLine($"✅ 禁止 TODO/HACK/FIXME 通过");
}

// ─── --quick 模式：仅 grep 检查，跳过 build/test ───
if (mode == "--quick")
{
    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine(fail == 0
        ? "  ✅ 静态检查通过（--quick 模式，跳过 build/test）"
        : "  ❌ 静态检查未通过");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    return fail;
}

// ─── V6：dotnet build（--build 和 full 模式）───
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  构建");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

{
    var (buildRc, buildOutput) = RunCapture("dotnet", "build PalDDD.slnx", rootDir);
    // 双语锚定：数字边界（前面不是数字）的 "0 个错误"/"0 Error(s)" 才算零；
    // 同时任一非零计数行出现即坏（三十五轮 A2：纯子串匹配的门禁假绿）
    var zeroOk = Regex.IsMatch(buildOutput, @"(^|[^0-9])0( 个错误| 个警告| Error| Warning)", RegexOptions.Multiline);
    var anyBad = Regex.IsMatch(buildOutput, @"[1-9][0-9]* (个错误|个警告|Error|Warning)");
    if (buildRc == 0 && zeroOk && !anyBad)
    {
        Console.WriteLine($"✅ dotnet build 零错误零警告");
    }
    else
    {
        Console.WriteLine($"❌ dotnet build 有错误或警告（rc={buildRc}）");
        foreach (var l in buildOutput.Split('\n').Reverse().Take(10).Reverse()) Console.WriteLine(l.TrimEnd('\r'));
        fail = 1;
    }
}

// ─── --build 模式：跳过 test ───
if (mode == "--build")
{
    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine(fail == 0
        ? "  ✅ 验证通过（--build 模式，跳过 test）"
        : "  ❌ 验证未通过");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    return fail;
}

// ─── V7：dotnet test（full 模式；build 已失败则跳过——照原版）───
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  测试");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

if (fail == 0)
{
    // MTP 手写协议：一次只跑一个测试项目；PalDDD.Testing 为支持库非测试项目
    var testFail = 0;
    var csprojs = new List<string>();
    if (Directory.Exists("test"))
        foreach (var f in Directory.EnumerateFiles("test", "*.csproj", SearchOption.AllDirectories))
            if (Path.GetFileName(f) != "PalDDD.Testing.csproj")
                csprojs.Add(ToPosix(f));
    csprojs.Sort(StringComparer.Ordinal);   // find | sort：C locale 字节序 = Ordinal
    foreach (var csproj in csprojs)
    {
        Console.WriteLine($"  测试: {csproj}");
        var (rc, _) = RunCapture("dotnet", $"test {csproj} --no-restore --no-build", rootDir);
        if (rc != 0)
        {
            Console.WriteLine($"❌ {csproj} 测试失败");
            testFail = 1;
        }
    }
    if (testFail == 0) Console.WriteLine($"✅ dotnet test 零失败（全部测试项目逐个执行）");
    else fail = 1;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
if (fail == 0)
{
    Console.WriteLine("  ✅ 规范验证全部通过");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    return 0;
}
Console.WriteLine("  ❌ 规范验证未通过，请修复上述问题");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
return 1;

// ══════════════ static 局部函数 ══════════════

// 仓库根定位：CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底（file-based app
// 的 BaseDirectory 实测指向 %TEMP%\dotnet\runfile\...，向上不可达仓库根）
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

// src/ 递归 .cs（路径 /obj/、/bin/ 排除在调用侧行级过滤，对齐 grep -v 输出行过滤语义）
static IEnumerable<string> EnumerateSrcCs(string root) =>
    Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories) : [];

// 子进程调用并捕获合并输出（等价 bash $(cmd 2>&1)；dotnet CLI 重定向输出为 UTF-8）
static (int Rc, string Output) RunCapture(string fileName, string arguments, string workingDir)
{
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = workingDir,
    };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, output);
}

// 路径转 posix 正斜杠（对齐 bash find/grep 输出形式）
static string ToPosix(string path) => path.Replace('\\', '/');
