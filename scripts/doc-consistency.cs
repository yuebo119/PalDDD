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

Console.WriteLine("═══════ Pal.DDD 文档一致性校验（薄壳：D1-D6/D8-D12 已下沉测试，本壳仅 D7）═══════");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine();

// ── D7: .ai/README.md 文件地图与实际一致 ──
// 文件地图里列出的 .ai 文件必须存在（与 verify-ai-system V9 互补）。
// 存在性守卫：.ai/README.md 不存在（CI 无 .ai）→ 空集 → PASS（bash awk 读不到同路径）
var readme = Path.Combine(ROOT, ".ai", "README.md");
var mapEntries = new List<string>();
if (File.Exists(readme))
{
    // awk 选行与 grep -oE 提取共用同一模式（行内全部匹配提取，不区分出现次数）
    var mapRx = new Regex(@"(gate|refine|review|test)/[a-z0-9/-]*\.md");
    foreach (var line in File.ReadLines(readme))
        foreach (Match m in mapRx.Matches(line))
            mapEntries.Add(m.Value);
}
// sort -u：Ordinal 字节序排序 + 去重
var missing = mapEntries
    .Distinct()
    .OrderBy(x => x, StringComparer.Ordinal)
    .Where(f => !File.Exists(Path.Combine(ROOT, ".ai", f)))
    .ToList();
if (missing.Count == 0)
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

Console.WriteLine();
Console.WriteLine($"通过: {passedCount}  失败: {failedCount}");
Console.WriteLine("═══════ 校验完成 ═══════");

return failedCount > 0 ? 1 : 0;

// ─── 局部函数 ───

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
