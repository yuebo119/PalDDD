// ============================================================================
// review-gate.cs——评审轮次路由（MIG-012-B1，2026-09-11）
// 由 .ai/scripts/review-gate.sh（94 行）等价迁移为 C#（file-based app）。
// 判断本次变更是否需要全量评审/增量评审/跳过——基于 v2.0 章程的评审轮次分级制度。
//
// 用法：在仓库根执行
//   dotnet run scripts/review-gate.cs                    # compare-ref 默认 HEAD~1
//   dotnet run scripts/review-gate.cs -- HEAD~3          # 与指定提交比较
// 退出码：0（全部路由均为提示性输出，非阻断）。
//
// 五分支路由（顺序判定，命中即返回）：
//   1) src/*.cs 变更 > 0        → 全量轮（六片并行；P0-P2 当轮修 + P3 批量）
//   2) csproj/props/targets 变更 → 增量轮（抽查变更项目 + 随机 1 片）
//   3) 仅 test/*.cs 变更         → 增量轮（抽查测试断言质量）
//   4) 仅 docs/md 或 .ai/ 元数据 → SKIP（无行为影响）
//   5) 其余                      → SKIP（无需评审）
//
// 等价迁移说明：
//   1) 原版 HAS_TEST_ONLY 变量与 for 循环计算后从未使用（死代码），不迁移；
//      HAS_SRC_CS 与 SRC_CS 计数重复，合并为一个。
//   2) 原版不 cd 仓库根（依赖执行目录），本版保持一致——须在仓库根执行。
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本工具输出是评审路由的固定
// 中文提示行（与原 bash 版逐行一致），无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

Console.OutputEncoding = Encoding.UTF8;
// 重定向时 Console.WriteLine 默认 \r\n（Windows）——对齐 bash echo -e 的 \n 行尾
Console.Out.NewLine = "\n";

var refName = args.Length > 0 ? args[0] : "HEAD~1";

const string Green = "\x1b[0;32m", Yellow = "\x1b[1;33m", Cyan = "\x1b[0;36m", Nc = "\x1b[0m";

Console.WriteLine("════════════════════════════════════════════════");
Console.WriteLine($" 评审轮次路由 (compare: {refName})");
Console.WriteLine("════════════════════════════════════════════════");

// 获取变更文件列表（git 失败容忍——等价 bash || true → 空表走 SKIP）
var changed = Run("git", $"diff --name-only {refName}")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(l => l.TrimEnd('\r'))
    .ToList();

if (changed.Count == 0)
{
    Console.WriteLine($"{Green}SKIP{Nc} — 无变更");
    return 0;
}

// 分类统计（等价 grep -cE 各模式）
var srcCs = changed.Count(f => Regex.IsMatch(f, "^src/.*\\.cs$"));
var testCs = changed.Count(f => Regex.IsMatch(f, "^test/.*\\.cs$"));
var docs = changed.Count(f => Regex.IsMatch(f, "\\.(md|txt)$|^docs/"));
var aiMeta = changed.Count(f => Regex.IsMatch(f, "^\\.ai/"));
var csproj = changed.Count(f => Regex.IsMatch(f, "\\.csproj$|\\.props$|\\.targets$"));
// 全仓扫描修复（漏判）：原分类表只有 5 类，scripts/**、bench/**、samples/**、.github/**、
// .githooks/** 的改动一个计数都不增 → 落到末尾 `SKIP — 无需评审（无代码/配置/文档变更）`，
// 而文案与实际相反（明明有代码/门禁变更）。补「工具/样例代码」与「CI/钩子」两桶，
// 并统计未命中任何桶的文件数，兜底改为保守档（宁可多评审，不可漏评审）。
var toolCs = changed.Count(f => Regex.IsMatch(f, "^(scripts|bench|samples)/.*\\.cs$"));
var ciHooks = changed.Count(f => Regex.IsMatch(f, "^(\\.github/|\\.githooks/)"));
var classified = changed.Count(f => Regex.IsMatch(f,
    "^src/.*\\.cs$|^test/.*\\.cs$|\\.(md|txt)$|^docs/|^\\.ai/|\\.csproj$|\\.props$|\\.targets$"
    + "|^(scripts|bench|samples)/.*\\.cs$|^(\\.github/|\\.githooks/)"));
var unclassified = changed.Count - classified;

Console.WriteLine();
Console.WriteLine(" 变更分类：");
Console.WriteLine($"   src/*.cs      : {srcCs}");
Console.WriteLine($"   test/*.cs     : {testCs}");
Console.WriteLine($"   docs/md       : {docs}");
Console.WriteLine($"   .ai/meta      : {aiMeta}");
Console.WriteLine($"   csproj/props  : {csproj}");
Console.WriteLine($"   工具/样例 .cs : {toolCs}");
Console.WriteLine($"   CI/钩子       : {ciHooks}");
Console.WriteLine($"   未分类        : {unclassified}");
Console.WriteLine();

// 路由逻辑（顺序判定，命中即返回）
if (srcCs > 0)
{
    Console.WriteLine($"{Cyan}→ 全量轮{Nc} — src/ 行为级代码变更（{srcCs} 个 .cs 文件）");
    Console.WriteLine("  评审深度：六片并行全量");
    Console.WriteLine("  修复策略：P0-P2 当轮修 + P3 批量");
    return 0;
}

if (csproj > 0)
{
    Console.WriteLine($"{Cyan}→ 增量轮{Nc} — 项目配置变更（依赖/属性）");
    Console.WriteLine("  评审深度：抽查变更项目 + 随机 1 片");
    return 0;
}

if (testCs > 0 && srcCs == 0)
{
    Console.WriteLine($"{Yellow}→ 增量轮{Nc} — 仅测试变更");
    Console.WriteLine("  评审深度：抽查测试断言质量");
    return 0;
}

if (toolCs > 0 || ciHooks > 0)
{
    Console.WriteLine($"{Yellow}→ 增量轮{Nc} — 工具/CI 面变更（scripts|bench|samples 代码、.github/.githooks）");
    Console.WriteLine("  评审深度：抽查变更脚本 + 其调用点与 gate-audit 矩阵登记");
    return 0;
}

if (docs > 0 || aiMeta > 0)
{
    Console.WriteLine($"{Yellow}→ SKIP{Nc} — 仅文档/元数据变更，无行为影响");
    return 0;
}

if (unclassified > 0)
{
    // 保守档（全仓扫描修复）：仍有未能归类的变更时不得判 SKIP——原实现的落点在此，
    // 且文案断言「无代码/配置/文档变更」，与实际相反。
    Console.WriteLine($"{Yellow}→ 增量轮{Nc} — {unclassified} 个变更未匹配既有分类（保守档：宁多评审不漏评审）");
    Console.WriteLine("  建议：确认其影响面后补入本脚本的分类表");
    return 0;
}

// 理论上不可达（changed.Count > 0 时每个文件非「命中某桶」即「未分类」，后者已在上方拦下）
// ——保留为防御性兜底。
Console.WriteLine($"{Green}→ SKIP{Nc} — 无变更");
return 0;

// ─── 工具函数 ───

// 执行外部命令取 stdout（UTF-8 读——git 输出 UTF-8；stderr 丢弃）
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
    // stderr 必须并发排空：RedirectStandardError 是管道，不排空时子进程写满缓冲即与
    // 父进程的 stdout ReadToEnd 互等（死锁）。内容按头注释契约丢弃。
    p.ErrorDataReceived += static (_, _) => { };
    p.BeginErrorReadLine();
    var text = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    p.WaitForExit();   // 双调用：确保异步缓冲 flush（沿 check-all.cs 先例）
    return text;
}

#pragma warning restore CA1303
