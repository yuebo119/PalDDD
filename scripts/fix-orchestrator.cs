// ============================================================================
// fix-orchestrator.cs——修复轮编排器（MIG-012-B1，2026-09-11）
// 由 .ai/scripts/fix-orchestrator.sh（102 行，unified v2.0 优化二）等价迁移为
// C#（file-based app）。自动化 engine.md 评审-修复循环协议的机械前置项：
//   ① 姊妹联动：HEAD diff 触及的 .cs 文件命中哪些接口族（调 scripts/sibling-map.cs）
//   ② 修复门两问提示（传感器 s / 外溢 p'——人/agent 决策辅助，非阻断）
//   ③ 快速回归检查清单（构建 + 受影响测试项目枚举 + 机械轴——机械执行）
//   ④ 同构模式姊妹收口核查（v85 新增：三模式族，轴 A 接口族的补充）
//
// 用法：在仓库根执行
//   dotnet run scripts/fix-orchestrator.cs               # 交互式：读 HEAD diff
//   dotnet run scripts/fix-orchestrator.cs -- ITM-660    # 带 ITM：输出留痕模板行
// 退出码：0=编排完成（是否执行由人/agent 决策）
//
// 等价迁移说明：
//   1) 结尾行输出字面 ${CYAN}/${NC}——原版第 102 行 printf 用单引号未展开变量
//      （笔误），本版保真不改，避免输出漂移；标题行的 ANSI 色码为真实输出。
//   2) grep -r 递归枚举顺序：MSYS2 GNU grep 为 NTFS 字母序，本版 EnumerateFiles
//      同为 NTFS 字母序——行集与顺序双重对照（见迁移对照记录）。
//   3) 路径显示统一正斜杠（GNU grep/MSYS2 风格）；grep -v '/obj/' 行过滤随之生效。
//   4) .cs 调 .cs：Process 执行 dotnet run scripts/sibling-map.cs（cwd=仓库根）。
//   5) 行内容保留 CRLF 行尾 \r（GNU grep 对 CRLF 文件按 \n 切行、\r 留在行尾）。
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本工具输出是修复轮编排的
// 固定中文提示/清单行（与原 bash 版逐行一致），无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

Console.OutputEncoding = Encoding.UTF8;
// 重定向时 Console.WriteLine 默认 \r\n（Windows）——对齐 bash echo/printf 的 \n 行尾
Console.Out.NewLine = "\n";
var dir = Environment.CurrentDirectory;
while (!File.Exists(Path.Combine(dir, "PalDDD.slnx"))
       && Path.GetFullPath(dir) != Path.GetPathRoot(Path.GetFullPath(dir)))
    dir = Path.GetDirectoryName(Path.GetFullPath(dir))!;
Environment.CurrentDirectory = dir;

var itm = args.Length > 0 ? args[0] : "";
const string Cyan = "\x1b[0;36m", Nc = "\x1b[0m";

Console.WriteLine($"{Cyan}═══ 修复轮编排器 ═══{Nc}");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm}  HEAD：{Run("git", "rev-parse --short HEAD").Trim()}  ITM：{(itm.Length > 0 ? itm : "（未指定）")}");
Console.WriteLine();

// ── ① 姊妹联动：HEAD diff 触及的文件命中哪些族 ──
// 变更集 = git diff HEAD~1 的 .cs 文件（排除 .g.cs 生成物）
var changed = Run("git", "diff HEAD~1 --name-only")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(l => l.TrimEnd('\r'))
    .Where(l => l.EndsWith(".cs", StringComparison.Ordinal)
             && !l.EndsWith(".g.cs", StringComparison.Ordinal))
    .ToList();

if (changed.Count > 0)
{
    // sibling-map 现算（.cs 调 .cs：dotnet run，失败容忍——等价 bash || true）
    var sibOut = Run("dotnet", "run scripts/sibling-map.cs");
    Console.WriteLine("── ① 姊妹联动（变更文件命中族）──");
    // 表行解析（等价 awk -F'|'：'|' I 开头行取第 2 列去空格作接口名，行内含变更文件即命中）
    var sibRows = sibOut.Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("| I", StringComparison.Ordinal))
        .ToList();
    var hitFiles = 0;
    foreach (var f in changed)
    {
        var hits = sibRows.Where(r => r.Contains(f, StringComparison.Ordinal))
            .Select(r => $"  {r.Split('|')[1].Replace(" ", "")} ← {f}")
            .ToList();
        if (hits.Count <= 0) continue;
        hits.ForEach(Console.WriteLine);
        hitFiles++;
    }
    if (hitFiles == 0)
        Console.WriteLine("  （无族命中——检查管线孪生轴 sibling-map.md 轴 B 种子表）");
    Console.WriteLine("  联动规则：姊妹对同族派发；判据三选一；>3 文件人工裁决");
    if (itm.Length > 0)
        Console.WriteLine($"  → 联动留痕模板见 sibling-map.md「联动留痕格式」节，ITM={itm}");
}
else
{
    Console.WriteLine("── ① 姊妹联动：无 .cs 变更 ──");
}

// ── ② 修复门两问（人/agent 决策提示）──
Console.WriteLine();
Console.WriteLine("── ② 修复门两问 ──");
Console.WriteLine("  Q1 这个故障类有传感器吗？→ 复现测试存在吗？（不存在先写：s 从 0→1）");
Console.WriteLine("  Q2 这个改动外溢吗？→ 共享函数/多调用方？→ grep 调用方评估 p'");
Console.WriteLine("  规则：s ≤ p' 先补传感器再修；同测试翻转两次=打摆换方案");

// ── ③ 快速回归清单 ──
Console.WriteLine();
Console.WriteLine("── ③ 回归清单（按序执行）──");
Console.WriteLine("  1. dotnet build PalDDD.slnx -c Release --verbosity quiet     # 全仓构建");
Console.WriteLine("  2. dotnet test <受影响项目> -c Release --verbosity quiet      # 定向测试");
if (changed.Count > 0)
{
    // 变更路径中的 test/<项目>/ 前缀去重排序，各自列首个 csproj（等价 ls ${d}*.csproj | head -1）
    var testDirs = changed
        .SelectMany(f => Regex.Matches(f, "test/[^/]+/").Select(m => m.Value))
        .Distinct()
        .OrderBy(x => x, StringComparer.Ordinal);
    foreach (var d in testDirs)
    {
        var csproj = Directory.Exists(d)
            ? Directory.EnumerateFiles(d, "*.csproj")
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault() ?? ""
            : "";
        Console.WriteLine($"     → {d}{csproj.Replace('\\', '/')}");
    }
}
Console.WriteLine("  3. bash .ai/scripts/verify-ai-system.sh                       # 系统一致性");
Console.WriteLine("  4. bash .ai/scripts/gate-check.sh                             # 架构门禁");
Console.WriteLine("  5. git add <相关文件> && git commit -m '修复：<描述>'");

// ── ④ 同构模式姊妹收口核查（v85 新增，2026-09-11）──
// 根因：v3 轮 7/7 P2 全是"多处同构改一处"——三个 Processor 的 OCE 过滤是管线孪生但
// 接口各不相同，轴 A（sibling-map 接口族）抓不到。本段从 diff 改动行提取「同构特征
// 模式」，grep 全仓剩余同模式位置，输出"改了 A、B/C 未同步"候选清单（提示性，非阻断）。
Console.WriteLine();
Console.WriteLine("── ④ 同构模式姊妹收口核查（v85：模式族——轴 A 接口族的补充）──");
if (changed.Count > 0)
{
    // 改动行集 = git diff -U0 的 + 行（排除 +++ 文件头）
    var added = Run("git", "diff HEAD~1 -U0")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(l => l[0] == '+'
                 && !l.StartsWith("+++", StringComparison.Ordinal))
        .ToList();
    var syncHits = 0;

    // 模式族 1：OCE 过滤 catch（ITM-653 根因族——管线 Processor 孪生）
    if (added.Any(l => l.Contains("is not OperationCanceledException", StringComparison.Ordinal)))
    {
        Console.WriteLine("  [模式族: OCE 过滤 catch] 你改动了该形态——全仓剩余实例（应逐处裁决是否同步）：");
        var rows = Grep("src/", new Regex("when.*is not OperationCanceledException"))
            .Where(l => !l.Contains("/obj/", StringComparison.Ordinal))
            .ToList();
        if (rows.Count > 0)
            rows.ForEach(r => Console.WriteLine($"    {r}"));
        else
            Console.WriteLine("    （零剩余，收口完整）");
        syncHits++;
    }

    // 模式族 2：参数守卫（ITM-659 根因族——为参数 X 加守卫时查姊妹栈同参数）
    // 提取 ThrowIfXxx(param, / ThrowIfXxx(param) 中的参数名，去重排序
    var guarded = added
        .SelectMany(l => Regex.Matches(l, @"ThrowIf[A-Za-z]+\([a-zA-Z]+[,)]"))
        .Select(m => Regex.Match(m.Value, @"\([a-zA-Z]+").Value.TrimStart('('))
        .Distinct()
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToList();
    if (guarded.Count > 0)
    {
        Console.WriteLine("  [模式族: 参数守卫] 你为以下参数加了守卫——姊妹栈同参数是否有同款（ITM-659 教训：EFCore 派生类 override 架空基类守卫）：");
        foreach (var prm in guarded)
        {
            Console.WriteLine($"    参数 {prm} 的其余用法（无守卫的调用/签名处）：");
            foreach (var row in GrepSub("src/", prm)
                         .Where(l => !l.Contains("/obj/", StringComparison.Ordinal)
                                  && !l.Contains("ThrowIf", StringComparison.Ordinal))
                         .Take(5))
                Console.WriteLine($"      {row}");
        }
        syncHits++;
    }

    // 模式族 3：守卫类测试（ITM-654 根因族——加了 X 的守卫测试，查姊妹方法）
    // 提取 new public async Task <方法>_AfterDispose/_Throws 测试的方法名首段
    var newGuardTests = added
        .SelectMany(l => Regex.Matches(l,
            @"public async Task [A-Za-z]+_AfterDispose|public async Task [A-Za-z]+_Throws"))
        .Select(m => m.Value.Split(' ')[3].Split('_')[0])
        .Distinct()
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToList();
    if (newGuardTests.Count > 0)
    {
        Console.WriteLine("  [模式族: 守卫测试姊妹] 你为以下方法加了 AfterDispose/Throws 测试——姊妹方法是否也有：");
        foreach (var m in newGuardTests)
        {
            Console.WriteLine($"    {m} 的姊妹守卫测试现状：");
            // test/ 内命中行提取三段标识符（grep -oE ... | sort -u | head -5）
            foreach (var name in Grep("test/",
                         new Regex($"{Regex.Escape(m)}_AfterDispose|{Regex.Escape(m)}_.*_Throws<ObjectDisposed"))
                     .SelectMany(l => Regex.Matches(l, @"[A-Za-z]+_[A-Za-z]+_[A-Za-z]+").Select(x => x.Value))
                     .Distinct()
                     .OrderBy(x => x, StringComparer.Ordinal)
                     .Take(5))
                Console.WriteLine($"      {name}");
            // src/ 内目标方法签名（ValueTask 异步形态）
            foreach (var row in Grep("src/", new Regex($"public.*ValueTask.*{Regex.Escape(m)}Async"))
                         .Where(l => !l.Contains("/obj/", StringComparison.Ordinal))
                         .Take(3))
                Console.WriteLine($"      src: {row}");
        }
        syncHits++;
    }

    if (syncHits == 0)
        Console.WriteLine("  （diff 未命中已知模式族——管线孪生仍须对照 sibling-map.md 轴 B 人工确认）");
}
else
{
    Console.WriteLine("  （无 .cs 变更）");
}

// 结尾行为原版字面 ${CYAN}...（单引号笔误保真，见头注释等价说明 1）
Console.WriteLine();
Console.WriteLine("${CYAN}═══ 编排完成——执行决策归修复者 ═══${NC}");
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

// 等价 grep -rn <rx> <root> --include='*.cs'：输出 file:line:text 行。
// 路径显示统一正斜杠（GNU grep/MSYS2 风格）；行尾 \r 剥离（GNU grep 管道输出为
// text mode：CRLF→LF）。枚举序 = NTFS 字母混排 + 子目录即时递归（模拟 GNU grep
// fts——EnumerateFiles(AllDirectories) 会把子目录延后导致顺序漂移，不用）。
static List<string> Grep(string root, Regex rx)
{
    var result = new List<string>();
    if (!Directory.Exists(root)) return result;
    foreach (var file in EnumerateCsFiles(root))
    {
        var display = file.Replace('\\', '/');
        var lines = Encoding.UTF8.GetString(File.ReadAllBytes(file)).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!rx.IsMatch(lines[i])) continue;
            // 行尾 \r 剥离（MSYS2 GNU grep 管道输出 text mode：CRLF→LF）
            var text = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
            result.Add($"{display}:{i + 1}:{text}");
        }
    }
    return result;
}

// 递归枚举 .cs（NTFS 大小写不敏感字母混排；文件即时处理、目录立即下降）
static IEnumerable<string> EnumerateCsFiles(string root)
{
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

// 子串版 grep（KEY 为普通标识符，BRE 字面语义 = 子串匹配）
static List<string> GrepSub(string root, string needle) => Grep(root, new Regex(Regex.Escape(needle)));

#pragma warning restore CA1303
