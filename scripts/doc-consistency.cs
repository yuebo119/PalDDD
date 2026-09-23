// ============================================================================
// doc-consistency.cs——Pal.DDD 文档一致性机械校验（MIG-012-A1，2026-09-11）
// 由 .ai/scripts/doc-consistency-check.sh 等价迁移为 C#（dotnet file-based app）。
// 判定项 D1-D6/D8-D12 已下沉 test/PalDDD.DependencyInjection.Tests/DocConsistencyGateTests.cs
//（反射 + 编译产物权威判定），本脚本仅保留 D7（.ai/README.md 文件地图）。
//
// 用法：dotnet run scripts/doc-consistency.cs
// 退出码：0=通过；1=D7 失败（文件地图指向不存在的文件）。
//
// 等价迁移说明（相对 .ai/scripts/doc-consistency-check.sh）：
//   1) 仓库根发现：从本 cs 源文件位置（编译期 CallerFilePath）向上找 PalDDD.slnx
//      ——等价 bash cd "$(dirname "$0")/../.."，与调用方 cwd 无关。
//   2) 存在性守卫保留：.ai/README.md 不存在（CI 无 .ai 独立仓）→ 空集 → PASS。
//   3) 路径提取正则逐字等价：awk 行选择与 grep -oE 提取共用同一模式
//      (gate|refine|review|test)/[a-z0-9/-]*\.md（awk 的 \/ 转义与未转义等价），
//      sort -u 对应 Ordinal 排序去重（字节序一致）。
//   4) ANSI 颜色码逐行保留；PASS/FAIL 关键字不变（CI grep 消费）。
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的固定
// 协议行（PASS/FAIL 关键字 + 中文口径说明），无本地化需求——沿 osc-check.cs 先例
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文输出对齐 bash UTF-8（沿 osc-check.cs 先例）
Console.OutputEncoding = Encoding.UTF8;

// 仓库根发现：从本 cs 源文件位置向上找含 PalDDD.slnx 的目录
var ROOT = FindRepoRoot();

int passedCount = 0, failedCount = 0;
const string Red = "\x1b[0;31m", Green = "\x1b[0;32m", Nc = "\x1b[0m";

Console.WriteLine("═══════ Pal.DDD 文档一致性校验（薄壳：D1-D6/D8-D12 已下沉测试，本壳 D7 + D13）═══════");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine();

// 2026-09-13 增：本脚本此前无自证能力（gate-audit 矩阵标 UNVERIFIED）。D7 的判定
// 分两步——路径提取（正则）与缺失过滤——两步都可能静默失效（提取正则写窄则
// 一条都不匹配、写宽则误报），故抽为纯函数并覆盖。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

// ── D7: .ai/README.md 文件地图与实际一致 ──
// 文件地图里列出的 .ai 文件必须存在（与 verify-ai-system V9 互补）。
// 存在性守卫：.ai/README.md 不存在（CI 无 .ai）→ 空集 → PASS（bash awk 读不到同路径）
var readme = Path.Combine(ROOT, ".ai", "README.md");
var mapEntries = File.Exists(readme) ? ExtractMapEntries(File.ReadLines(readme)) : [];
var missing = MissingMapEntries(mapEntries, f => File.Exists(Path.Combine(ROOT, ".ai", f)));
// 全仓扫描修复（空输入假绿）：上一条守卫的前提是「.ai 整棵不存在」。若 README **在**
// 却提取到零条目（地图改格式、路径改写到子目录、字符集不再匹配），missing 同样恒为空
// → 与「地图全部存在」一样判 PASS，而检查实际没跑。仅对后者 fail-closed，保留既有跳过语义。
var extractionEmpty = IsMapExtractionEmpty(File.Exists(readme), mapEntries.Count);
if (extractionEmpty)
{
    Console.WriteLine($"{Red}FAIL{Nc} D7 .ai/README.md 存在但提取到 0 条文件地图条目——提取规则与文档格式可能已漂移（fail-closed）");
    failedCount++;
}
else if (missing.Count == 0)
{
    Console.WriteLine($"{Green}PASS{Nc} D7 .ai/README.md 文件地图与实际一致");
    passedCount++;
}
else
{
    // bash 口径：missing 为前导空格 + 空格连接串（"$missing .ai/$f" 拼接产物）
    Console.WriteLine($"{Red}FAIL{Nc} D7 文件地图指向不存在的文件： {string.Join(" ", missing.Select(f => $".ai/{f}"))}");
    failedCount++;
}

// ── D13: 跨仓契约版本锚（T-37，2026-09-22 增）──
// 两仓互相引用（主仓 scripts/verify-ai.cs 校验 .ai 内容；.ai 的 prompt/engine 描述主仓门禁），
// 但独立版本化、无同步机制。**引用层**一致性已有守护（verify-ai V2 校验 .ai 提示词引用的主仓
// 脚本存在、V19 校验传感器台账路径、V25 校验命令形态）；**残余缺口是契约本身的版本**——
// 当跨仓契约变化（.ai 文档需描述的脚本集/命令形态/账本结构改变）时，没有任何东西要求两侧同步。
// 本项是 tripwire：两侧各声明一个契约版本号，不一致即红。存在性守卫同 D7（CI 无 .ai → PASS）。
const int ExpectedCrossRepoContractVersion = 1;
if (!File.Exists(readme))
{
    Console.WriteLine($"{Green}PASS{Nc} D13 跨仓契约版本锚（无 .ai 独立仓，跳过）");
    passedCount++;
}
else
{
    var declaredContract = ExtractCrossRepoContractVersion(File.ReadLines(readme));
    if (declaredContract is null)
    {
        Console.WriteLine($"{Red}FAIL{Nc} D13 .ai/README.md 存在但未声明跨仓契约版本（缺「跨仓契约版本」行）——fail-closed");
        failedCount++;
    }
    else if (declaredContract != ExpectedCrossRepoContractVersion)
    {
        Console.WriteLine($"{Red}FAIL{Nc} D13 跨仓契约版本不一致：.ai 声明 {declaredContract}，主仓期望 {ExpectedCrossRepoContractVersion}——两侧同步升位或修正");
        failedCount++;
    }
    else
    {
        Console.WriteLine($"{Green}PASS{Nc} D13 跨仓契约版本一致（{declaredContract}）");
        passedCount++;
    }
}

// D13 提取器（纯函数）：从 .ai/README.md 解析「跨仓契约版本：N」声明；未声明返回 null。
static int? ExtractCrossRepoContractVersion(IEnumerable<string> lines)
{
    foreach (var line in lines)
    {
        var m = Regex.Match(line, @"跨仓契约版本\**\s*[:：]\s*([0-9]+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) return v;
    }
    return null;
}

Console.WriteLine();
Console.WriteLine($"通过: {passedCount}  失败: {failedCount}");
Console.WriteLine("═══════ 校验完成 ═══════");

return failedCount > 0 ? 1 : 0;

// ─── 局部函数 ───

// D7 路径提取（纯函数）：行内全部匹配提取，不区分出现次数（对齐 grep -oE 语义）
static List<string> ExtractMapEntries(IEnumerable<string> lines)
{
    var rx = new Regex(@"(gate|refine|review|test)/[a-z0-9/-]*\.md");
    var entries = new List<string>();
    foreach (var line in lines)
        foreach (Match m in rx.Matches(line))
            entries.Add(m.Value);
    return entries;
}

// D7 缺失过滤（纯函数）：去重 + Ordinal 排序（对齐 sort -u）+ 存在性过滤
static List<string> MissingMapEntries(List<string> entries, Func<string, bool> exists) =>
    entries.Distinct()
           .OrderBy(x => x, StringComparer.Ordinal)
           .Where(f => !exists(f))
           .ToList();

// D7 空输入判定（纯函数，供 --selftest 与变异验证）：README **存在**却提取到 0 条
// 条目 → 检查实际没跑（地图改格式/路径改写即触发），必须 fail-closed。
// 与「README 不存在（CI 无 .ai）」区分：后者保持既有跳过语义（返回 false）。
static bool IsMapExtractionEmpty(bool readmeExists, int entryCount) => readmeExists && entryCount == 0;

// ─── 自测 ───

static int SelfTest()
{
    var passed = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    // 提取：正例（四类前缀 + 数字/连字符/斜杠）
    Case("提取 gate/ 条目", ExtractMapEntries(["| [gate/prompt.md](gate/prompt.md) |"]).Contains("gate/prompt.md"));
    Case("提取 refine/ 含数字与连字符",
        ExtractMapEntries(["refine/baseline-unified-v2.md"]).Contains("refine/baseline-unified-v2.md"));
    Case("提取同行情多次出现",
        ExtractMapEntries(["review/a.md 见 review/b.md"]).Count == 2);

    // 提取：反例（字符集只含小写与数字/连字符/斜杠）
    Case("不提取大写路径", ExtractMapEntries(["gate/Prompt.md"]).Count == 0);
    Case("不提取下划线路径（_ 不在字符集）", ExtractMapEntries(["test/my_file.md"]).Count == 0);
    Case("不提取非 .md", ExtractMapEntries(["gate/prompt.txt"]).Count == 0);
    Case("空行不产生条目", ExtractMapEntries(["", "   "]).Count == 0);

    // 已知宽口径：正则无行首锚定，故 docs/review/x.md 中的 review/x.md 也会被提取。
    // 本仓 .ai/README.md 的实际写法是相对 .ai/ 的短路径，故不构成问题；但该行为需钉住，
    // 否则将来给正则加锚定会无声改变提取集合。
    Case("已知宽口径：review/ 片段可从任意路径中截出",
        ExtractMapEntries(["docs/review/x.md"]).Contains("review/x.md"));

    // D7 空输入判定（全仓扫描修复的 fail-closed 路径）
    Case("D7 空输入：README 在且零条目 → fail-closed", IsMapExtractionEmpty(true, 0));
    Case("D7 负例：README 在且有条目 → 正常判定", !IsMapExtractionEmpty(true, 2));
    Case("D7 负例：README 不在（CI 无 .ai）→ 保持跳过语义", !IsMapExtractionEmpty(false, 0));

    // 缺失过滤：去重、Ordinal 排序、存在性
    Case("缺失过滤去重", MissingMapEntries(["a/gate.md", "a/gate.md"], _ => true).Count == 0);
    Case("缺失过滤返回不存在项",
        MissingMapEntries(["gate/ok.md", "gate/gone.md"], f => f == "gate/ok.md") is ["gate/gone.md"]);
    Case("缺失过滤按 Ordinal 排序",
        MissingMapEntries(["gate/b.md", "gate/a.md"], _ => false) is ["gate/a.md", "gate/b.md"]);
    Case("空输入无缺失", MissingMapEntries([], _ => false).Count == 0);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}

// 仓库根发现：从本 cs 源文件位置向上找含 PalDDD.slnx 的目录
static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string src = "")
{
    var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(src)
        ? Environment.CurrentDirectory
        : Path.GetDirectoryName(src)!);
    while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("未找到仓库根（PalDDD.slnx）——请从仓库内运行");
}
