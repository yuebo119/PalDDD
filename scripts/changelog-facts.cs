// ═══════════════════════════════════════════════════════════════
// changelog-facts.cs——变更日志事实收集器（release.md §十二 Phase 1）
// （MIG-012-C，2026-09-11；由 scripts/changelog-facts.sh 105 行 bash 等价迁移）
//
// 用法（在仓库根执行）：
//   dotnet run scripts/changelog-facts.cs -- <from-tag> [to-ref=HEAD]
//   dotnet run scripts/changelog-facts.cs -- v2.1.0     收集 v2.1.0..HEAD 事实
//
// 定位（docs/release.md §十一/§十二）：只收集「机械可验证事实」，不生成最终
// 文案——文案由起草者按 §11.4 模板基于本清单撰写。每条事实带可复查命令，
// 禁止在事实清单之外的来源编造条目。
//
// 产出段：
//   1 范围与提交分布        5 废弃扫描
//   2 公共 API 变更         6 ADR 与文档增删
//   3 新增分析器诊断        7 依赖变更
//   4 脚本/工作流增删       8 测试面板实测（提醒，不自动跑）
//   9 当前 [Unreleased] 段
// 退出码：0=完成；1=from-tag 不存在或缺参；git 基础查询失败透传其退出码
//        （set -e 语义——段 1 的 log 查询无兜底；段 2-7 均有 || true 兜底不阻断）。
//
// 迁移说明：
//   1) 段 1 类型分布的 uniq -c 输出格式逐字符对齐：7 宽右对齐计数 + 空格 +
//      值；sort -rn 同计数时按整行逆字节序（GNU sort last-resort，Ordinal 近似）。
//   2) 段 9 Unreleased 提取对齐 sed 范围语义：从 ## [Unreleased] 标题的下一行
//      起到下一个 ## [ 行（不含），删空行后 head -40 截断。
//   3) 生成时间行为本机本地时间（date '+%Y-%m-%d %H:%M' 等价）——分钟级精度，
//      双跑跨分钟时该行差异属预期。
// ═══════════════════════════════════════════════════════════════
#pragma warning disable CA1303 // 事实清单为固定控制台文案，无本地化需求——沿 flaky-parse.cs 先例

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文/emoji 会乱码——对齐 bash UTF-8 输出；
// 重定向行尾默认 \r\n（bash 为 \n），统一为 \n 保证双跑逐字节可比
Console.OutputEncoding = Encoding.UTF8;
Console.Error.NewLine = "\n";   // 用法提示行尾对齐 bash（\n）
Console.Out.NewLine = "\n";

// 参数：FROM 必需（缺参用法提示 + 退出 1，对齐 bash ${1:?...}）；TO 缺省 HEAD
if (args.Length < 1)
{
    Console.Error.WriteLine("用法: dotnet run scripts/changelog-facts.cs -- <from-tag> [to-ref]");
    return 1;
}
var from = args[0];
var to = args.Length > 1 ? args[1] : "HEAD";

// from-tag 存在性验证（--quiet 静默，只看退出码）
var (revExit, _) = RunCapture("git", $"rev-parse --verify --quiet {from}^{{commit}}");
if (revExit != 0)
{
    Console.WriteLine($"❌ from-tag 不存在: {from}");
    return 1;
}

Console.WriteLine($"# 变更日志事实清单（{from}..{to}）");
Console.WriteLine($"> 生成: {DateTime.Now:yyyy-MM-dd HH:mm} · 每条事实附可复查命令 · 文案撰写规范见 docs/release.md §十一/§十二");
Console.WriteLine();

// ── 1 范围与提交分布 ──────────────────────────────────────────
// git log 查询无兜底（set -e 语义）：失败时透传退出码，stderr 已透传终端
var (logExit, logOneline) = RunCapture("git", $"log --oneline {from}..{to}");
if (logExit != 0) return logExit;
var count = NonEmptyLines(logOneline).Count;
var (toExit, toCommit) = RunCapture("git", $"log -1 --format=\"%h %s\" {to}");
if (toExit != 0) return toExit;
var (fromExit, fromCommit) = RunCapture("git", $"log -1 --format=%h {from}");
if (fromExit != 0) return fromExit;
var toHash = toCommit.Split(' ')[0];   // bash ${TO_COMMIT%% *}：删除首个空格起全部
Console.WriteLine("## 1 范围与提交分布");
Console.WriteLine($"- 范围：{from}({fromCommit.Trim()}) → {to}（{toHash}），共 {count} 个提交");
Console.WriteLine($"- 复查: git log --oneline {from}..{to}");
Console.WriteLine("- 类型分布（提交主题前缀，供 Fixed/Added 分族参考）：");
var (_, subjects) = RunCapture("git", $"log --pretty=format:%s {from}..{to}");
// sed -E 's/[：:].*//'：删除首个全角/半角冒号及之后 → 前缀；无冒号保留原行
// （空主题提交保留空前缀参与计数——bash 管道不滤空行）
foreach (var line in PrefixHistogram(SplitLines(subjects)))
{
    Console.WriteLine("    " + line);
}
Console.WriteLine();

// ── 2 公共 API 变更（快照 diff，核心 12 程序集口径）────────────
const string snap = "test/PalDDD.Core.Tests/Snapshots/core-packages-public-api.txt";
Console.WriteLine("## 2 公共 API 变更（快照口径：核心 12 程序集；Analyzers 诊断见段 3）");
var (_, diffSnap) = RunCapture("git", $"diff {from}..{to} -- {snap}");
var versionRx = new Regex("Version=2\\.[0-9]");   // grep -v 'Version=2\.[0-9]' 排除口径
var apiDiff = NonEmptyLines(diffSnap)
    .Where(l => (l.StartsWith('+') || l.StartsWith('-'))
        && !(l.StartsWith("++", StringComparison.Ordinal) || l.StartsWith("--", StringComparison.Ordinal)))
    .Where(l => !versionRx.IsMatch(l))
    .ToList();
if (apiDiff.Count > 0)
{
    foreach (var l in apiDiff) Console.WriteLine("    " + l);
    Console.WriteLine("  ⚠️ 删除行（-）= 签名变更/移除，必须逐条在 Added/Changed/Removed 分类交代");
}
else
{
    Console.WriteLine("    （无变更）");
}
Console.WriteLine($"- 复查: git diff {from}..{to} -- {snap} | grep -v Version=");
Console.WriteLine();

// ── 3 新增分析器诊断（线索级，需打开 descriptor 核对语义）────────
Console.WriteLine("## 3 新增分析器诊断（线索，起草前须读 PalDiagnostics.cs 对应 descriptor）");
var (_, diffDiag) = RunCapture("git", $"diff {from}..{to} -- src/PalDDD.Core/PalDiagnostics.cs");
var diagRx = new Regex("PAL[A-Z]+[0-9]{3}");
var diags = NonEmptyLines(diffDiag)
    .Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal) && diagRx.IsMatch(l))
    .SelectMany(l => diagRx.Matches(l).Select(m => m.Value))   // grep -oE：一行可多个
    .Distinct()
    .OrderBy(x => x, StringComparer.Ordinal)   // sort -u 字节序
    .ToList();
if (diags.Count > 0)
{
    foreach (var d in diags) Console.WriteLine("    " + d);
}
else
{
    Console.WriteLine("    （无新增）");
}
Console.WriteLine();

// ── 4 脚本 / 工作流增删 ───────────────────────────────────────
Console.WriteLine("## 4 脚本与工作流增删（A=新增 D=删除）");
var (_, wf) = RunCapture("git", $"diff --name-status {from}..{to} -- scripts/ .github/workflows/");
var wfLines = NonEmptyLines(wf);
if (wfLines.Count > 0)
{
    foreach (var l in wfLines) Console.WriteLine("    " + l);
}
else
{
    Console.WriteLine("    （无）");
}
Console.WriteLine();

// ── 5 废弃扫描 ───────────────────────────────────────────────
Console.WriteLine("## 5 废弃扫描（diff 新增 [Obsolete] 行——仅供定位，语义须读原文件核实）");
var (_, diffSrc) = RunCapture("git", $"diff {from}..{to} -- src/");
var obs = NonEmptyLines(diffSrc)
    .Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal) && l.Contains("[Obsolete"))
    .ToList();
if (obs.Count > 0)
{
    foreach (var l in obs) Console.WriteLine("    " + l);
    Console.WriteLine("  ⚠️ 废弃条目必须写明移除版本窗口与理由（§11.3 规则 3）");
}
else
{
    Console.WriteLine("    （无新增废弃）");
}
Console.WriteLine();

// ── 6 ADR 与文档增删 ─────────────────────────────────────────
Console.WriteLine("## 6 ADR 与文档增删");
// *.md 经 bash 裸 glob 展开为根目录 .md 文件列表（见 ExpandRootMdGlob 注释）
var (_, docs) = RunCapture("git", $"diff --name-status {from}..{to} -- docs/ {ExpandRootMdGlob()}");
var docsLines = NonEmptyLines(docs);
if (docsLines.Count > 0)
{
    foreach (var l in docsLines) Console.WriteLine("    " + l);
}
else
{
    Console.WriteLine("    （无）");
}
Console.WriteLine();

// ── 7 依赖变更 ───────────────────────────────────────────────
Console.WriteLine("## 7 依赖变更（Directory.Packages.props + 适配层引用）");
var (_, diffDeps) = RunCapture("git", $"diff {from}..{to} -- Directory.Packages.props");
var depsRx = new Regex("PackageVersion|PalORM");
var deps = NonEmptyLines(diffDeps)
    .Where(l => (l.StartsWith('+') || l.StartsWith('-'))
        && !(l.StartsWith("++", StringComparison.Ordinal) || l.StartsWith("--", StringComparison.Ordinal)))
    .Where(l => depsRx.IsMatch(l))
    .ToList();
if (deps.Count > 0)
{
    foreach (var l in deps) Console.WriteLine("    " + l);
}
else
{
    Console.WriteLine("    （中央版本无变更——注意排查各 csproj 直接引用与 docs/release.md §二 依赖表）");
}
Console.WriteLine($"- 复查: git diff {from}..{to} -- '**/*.csproj' 'Directory.Packages.props'");
Console.WriteLine();

// ── 8 测试面板实测（不自动跑——发布前验证阶段已有实测值）─────────
Console.WriteLine("## 8 测试面板实测值（§11.3 规则 1：只允许写实测数字，禁止预估）");
Console.WriteLine("  ▶ 发布前验证（§4.1）跑完后回填：");
Console.WriteLine("    for p in $(find test -name '*.Tests.csproj' | sort); do dotnet test \"$p\" -c Release --no-restore --no-build --nologo; done");
Console.WriteLine("  ▶ 回填格式：16 项目总计 N = 本机通过 X + 环境依赖 Y（JSON 报告归类，CI Testcontainers 权威判定）；基线 = 上一版 Tests 段实测值");
Console.WriteLine();

// ── 9 当前 [Unreleased] 段 ───────────────────────────────────
Console.WriteLine("## 9 当前 [Unreleased] 段（转正原料——按 §11.4 模板分类重写，不是原样搬运）");
var cl = File.ReadAllLines("CHANGELOG.md");
var unrel = new List<string>();
var inUnrel = false;
foreach (var l in cl)
{
    if (!inUnrel)
    {
        if (l.StartsWith("## [Unreleased]", StringComparison.Ordinal)) inUnrel = true;
        continue;
    }
    if (l.StartsWith("## [", StringComparison.Ordinal)) break;   // 下一版本段标题（含）即止
    unrel.Add(l);
}
foreach (var l in unrel.Where(l => l.Length > 0).Take(40))   // sed '/^$/d' + head -40
{
    Console.WriteLine(l);
}
Console.WriteLine("    （完整段自查: sed -n '/^## \\[Unreleased\\]/,/^## \\[/p' CHANGELOG.md）");
Console.WriteLine();
Console.WriteLine("═══ 事实清单结束 → 按 docs/release.md §十二 Phase 2-4 核验/起草/校验 ═══");
return 0;

// ─── 子进程执行：stdout 捕获（stderr 继承终端——对齐 bash 管道行为）───
static (int Exit, string Output) RunCapture(string fileName, string arguments)
{
    var info = new ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        StandardOutputEncoding = Encoding.UTF8,   // git 重定向输出为 UTF-8
    };
    using var process = new Process { StartInfo = info };
    process.Start();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output);
}

// ─── 行切分：按 \r\n / \n / \r 分行并滤空（bash 管道行语义）───
static List<string> NonEmptyLines(string text) =>
    text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries).ToList();

// ─── 行切分（保留空行，仅去尾部换行伪行）───
// bash 管道（sort|uniq -c）不滤空行——空主题提交以空前缀参与计数；
// 尾部换行 split 出的末尾空元素对应 bash 中不存在的"末行后空行"，去除
static List<string> SplitLines(string text)
{
    var parts = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToList();
    if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
    return parts;
}

// ─── bash 裸 glob 展开：当前目录 *.md → 文件名列表；无匹配保留字面量 ───
// 原脚本 `-- docs/ *.md` 中 *.md 未加引号——bash 先展开为根目录 .md 文件列表
// 再传 git（非 git pathspec 跨目录匹配）；C# 模拟同一集合（git 按路径序输出，
// 参数顺序无关紧要）。Windows 匹配大小写不敏感，本仓根 .md 后缀全小写，等价。
static string ExpandRootMdGlob()
{
    var mds = Directory.EnumerateFiles(".", "*.md")
        .Select(Path.GetFileName)
        .Where(n => n is not null)
        .Cast<string>()
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();
    return mds.Count > 0 ? string.Join(" ", mds) : "*.md";
}

// ─── 主题前缀：删除首个全角/半角冒号及之后（sed -E 's/[：:].*//'）───
static string PrefixOf(string subject)
{
    var full = subject.IndexOf('：');
    var half = subject.IndexOf(':');
    var cut = full >= 0 && half >= 0 ? Math.Min(full, half) : Math.Max(full, half);
    return cut < 0 ? subject : subject[..cut];
}

// ─── uniq -c + sort -rn 模拟 ───
// 输出行格式：7 宽右对齐计数 + 空格 + 值（GNU uniq -c 固定列宽）；
// 排序：计数数值降序，同计数按整行逆字节序（sort -r 反转 GNU last-resort 比较，
// Ordinal 对 BMP 字符与 UTF-8 字节序一致）
static List<string> PrefixHistogram(List<string> prefixes)
{
    var groups = prefixes.GroupBy(PrefixOf)
        .Select(g => (N: g.Count(), V: g.Key))
        .ToList();
    groups.Sort((a, b) =>
    {
        var byCount = b.N.CompareTo(a.N);
        return byCount != 0 ? byCount : string.CompareOrdinal(Fmt(b), Fmt(a));
    });
    return groups.Select(Fmt).ToList();

    static string Fmt((int N, string V) g) => g.N.ToString().PadLeft(7) + " " + g.V;
}
