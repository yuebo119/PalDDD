// ═══════════════════════════════════════════════════════════════
// changelog-check.cs——变更日志结构门禁（release.md §十二 Phase 4）
// （MIG-012-C，2026-09-11；由 scripts/changelog-check.sh 107 行 bash 等价迁移）
//
// 用法（在仓库根执行）：
//   dotnet run scripts/changelog-check.cs --                校验当前工作树
//   dotnet run scripts/changelog-check.cs -- <目标版本>     顶部已发布段须 == 目标版本
//   dotnet run scripts/changelog-check.cs -- --selftest    自测（C6–C10 判定函数正反例）
//
// 挂接点（docs/release.md）：§4.1 本地必跑 + §5.1 打 tag 前步骤 2.5；
// 2026-10-26 起接入 .github/workflows/release.yml（pack 前阻断，对齐 Pal.ORM
// changelog-check 的 M1-3 位次：校验必须在推送 NuGet 之前，放 push 之后形同虚设）。
// 本门禁 FAIL 时禁止打 tag / 禁止发布（CHANGELOG 先行纪律的机械强制，§九教训 1/2）。
//
// 检查项：
//   C1 CHANGELOG 首个版本段必须是 [Unreleased]
//   C2 当前版本（VersionPrefix[+VersionSuffix] 组合全版本）若已存在同名 tag
//      → 必须已有 [版本号] 转正段（组合后 preview tag 不再被前缀失真漏判）
//   C3 最新发布段分类层顺序符合 §11.2（Added→Changed→Deprecated→Removed→
//      Fixed→Security→Dependencies→Documentation→Tests）
//   C4 Tests 段禁止预估口径数字（"约 N"/"~N"/"预计"）[WARN 不阻断]
//   C5 分类层禁内部术语泄漏（ITM-\d+ / v\d+ 轮次引用等）[WARN 不阻断——
//      分类层=最新发布段至附录之间]
//   C6 顶部已发布段版本 == 目标版本（目标 = 参数 ?? props 组合全版本；
//      与 §11.3 规则 5「转正与版本三件套同次提交」联动，2026-10-26 增）
//   C7 顶部已发布段标题格式 = ## [X.Y.Z] — YYYY-MM-DD（§11.4 长破折号格式；
//      解析失败时 C6/C8/C9/C10 跳过，不叠加误报）
//   C8 段日期 == tag 提交日（tag 存在时；无 tag 视为首发正常跳过）
//   C9 段日期单调：不早于下一发布段（防回填错位）
//   C10 目标段条目非空（至少 1 条 "- "，防空段转正）
//   C6–C10 只校验顶部第一个已发布段——历史段是审计记录，不回改也不扫。
// 退出码：0=无 FAIL；1=任一 FAIL；2=用法错。
//
// 迁移说明：
//   1) PASS 计数器语义保持（P3 修复后）：按实际执行的检查项累加——C4/C5 在
//      "无已发布段/无附录"时整段跳过（既不 pass 也不 warn/fail），不虚报。
//   2) sed 区间提取改 Regex + 行数组切片：C3/C4/C5 的 [起,止] 闭区间、
//      Tests 段范围打印（含端点行）、基线约数先行剔除——逐项对齐。
//   3) PDDD0xx/PALxxx 是公开分析器诊断 ID（消费者构建警告可见），属合法
//      消费者术语，不拦——C5 正则与原脚本逐字一致。
// ═══════════════════════════════════════════════════════════════
#pragma warning disable CA1303 // 门禁协议输出为固定控制台文案，无本地化需求——沿 flaky-parse.cs 先例

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文/emoji 会乱码——对齐 bash UTF-8 输出；
// 重定向行尾默认 \r\n（bash 为 \n），统一为 \n 保证双跑逐字节可比
Console.OutputEncoding = Encoding.UTF8;
Console.Out.NewLine = "\n";

// 参数（2026-10-26 增）：--selftest 走自测；单个自由参数为目标版本（release.yml 从
// tag 名剥 v 后传入），缺省取 Directory.Build.props 组合全版本。多参数一律用法错。
if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
if (args.Length > 1)
{
    Console.Error.WriteLine("usage: dotnet run scripts/changelog-check.cs [--selftest | <目标版本>]");
    return 2;
}
var targetArg = args.Length == 1 ? args[0] : "";

const string changelog = "CHANGELOG.md";
var pass = 0;
var warn = 0;
var fail = 0;

// 输出格式对齐 bash pass()/warn()/fail()：pass "1 消息" → "PASS C1 消息: "（尾随冒号空格）
void Pass(int id, string msg) { Console.WriteLine($"PASS C{id} {msg}: "); pass++; }
void Warn(int id, string msg) { Console.WriteLine($"WARN C{id} {msg}: "); warn++; }
void Fail(int id, string msg) { Console.WriteLine($"FAIL C{id} {msg}: "); fail++; }

var lines = File.ReadAllLines(changelog);

// ── C1 首个版本段 = [Unreleased] ─────────────────────────────
var first = lines.FirstOrDefault(l => l.StartsWith("## [", StringComparison.Ordinal)) ?? "";
if (first == "## [Unreleased]")
{
    Pass(1, "CHANGELOG 首个版本段为 [Unreleased]");
}
else
{
    Fail(1, $"CHANGELOG 首个版本段为 '{first}'——应为 [Unreleased]（[Unreleased] 段缺失即违反 §11.3 规则 5）");
}

// ── C2 同名 tag 已存在 → 必须已转正 ──────────────────────────
// 2026-10-26 起 tag 判定与段名匹配均用 VersionPrefix+VersionSuffix 组合全版本：
// preview 期 tag 是 v5.0.0-preview.1，只看前缀会漏判（v5.0.0 不存在 → 转正义务漏检）。
var propsText = File.ReadAllText("Directory.Build.props");
var versionPrefix = Regex.Match(propsText, "(?<=<VersionPrefix>)[^<]+").Value;
var versionSuffix = Regex.Match(propsText, "(?<=<VersionSuffix>)[^<]+").Value;
var version = ComposeFullVersion(versionPrefix, versionSuffix);
if (versionPrefix.Length == 0)
{
    Fail(2, "无法从 Directory.Build.props 解析 VersionPrefix");
}
else
{
    // git rev-parse --verify --quiet "v${VERSION}^{commit}"：只看退出码（输出静默丢弃）
    var (revExit, _) = RunCapture("git", $"rev-parse --verify --quiet v{version}^{{commit}}");
    if (revExit == 0)
    {
        if (lines.Any(l => Regex.IsMatch(l, $"^## \\[{Regex.Escape(version)}\\]")))
        {
            Pass(2, $"版本 {version} 的 tag 已存在且 [{version}] 段已转正");
        }
        else
        {
            Fail(2, $"版本 {version} 已有本地 tag 但 CHANGELOG 无 [{version}] 转正段——禁止打 tag（§九教训 2：v2.0.0 未转正实录）");
        }
    }
    else
    {
        Pass(2, $"版本 {version} 尚无 tag（[Unreleased] 累积期，转正义务在打 tag 时点）");
    }
}

// ── C3 最新发布段分类顺序（§11.2 固定顺序）────────────────────
string[] order = ["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security", "Dependencies", "Documentation", "Tests"];
var headingLines = lines
    .Select((l, i) => (Line: l, No: i + 1))
    .Where(x => x.Line.StartsWith("## [", StringComparison.Ordinal))
    .ToList();
// bash：grep -nE '^## \[' | grep -v 'Unreleased' | head -1——行内容含 Unreleased 即排除
(string Line, int No)? latest = null;
foreach (var h in headingLines)
{
    if (!h.Line.Contains("Unreleased")) { latest = h; break; }
}
int? latestLine = null, endLine = null, appendixLine = null;
if (latest is null)
{
    Warn(3, "CHANGELOG 无已发布版本段（首次发布前），跳过顺序检查");
}
else
{
    latestLine = latest.Value.No;
    // 先取硬边界：下一个 ## [ 行 ?? 总行数
    foreach (var h in headingLines)
    {
        if (h.No > latestLine) { endLine = h.No; break; }
    }
    endLine ??= lines.Length;
    // 其后第一个 "### 附录" 行（/^### 附录/ 行首匹配）——只在本段内找（2026-10-11 修复：
    // 原实现先全域找附录再无条件覆盖 endLine，当最新段无附录而更旧段有附录时，会把
    // 旧段的附录行当成最新段的边界，C3 把两段分类拼接误报顺序违反、C4/C5 区间同步
    // 受染——v4.0 转正实证：[4.0.0] 无附录、[3.3.0] 有附录，C3 实际输出含 [3.3.0] 的
    // Changed/Dependencies/Tests 三段。附录搜索上界 = 硬边界（0 基排他，对齐 Take 语义））
    var searchUpper = endLine.Value - 1;
    for (var i = latestLine.Value; i < searchUpper; i++)   // i 为 0 基，行号 i+1 > latestLine
    {
        if (lines[i].StartsWith("### 附录", StringComparison.Ordinal))
        {
            appendixLine = i + 1;
            break;
        }
    }
    // END_LINE = 附录行（若在本段内）?? 硬边界（三重兜底对齐）
    if (appendixLine is not null) endLine = appendixLine;

    // 提取区间 [latestLine, endLine] 内的 ^### [A-Za-z]+ 分类名（grep -oE + sed 去前缀）
    var sections = lines
        .Skip(latestLine.Value - 1)
        .Take(endLine.Value - latestLine.Value + 1)
        .Select(l => Regex.Match(l, "^### ([A-Za-z]+)"))
        .Where(m => m.Success)
        .Select(m => m.Groups[1].Value)
        .ToList();
    var sequenced = true;
    var prevIdx = 0;
    foreach (var s in sections)
    {
        var idx = Array.IndexOf(order, s);
        if (idx < 0) continue;   // 非标准分类（如中文主题段）跳过
        if (idx < prevIdx) { sequenced = false; break; }
        prevIdx = idx;
    }
    if (sequenced)
    {
        Pass(3, "最新发布段分类顺序符合 §11.2");
    }
    else
    {
        Fail(3, "最新发布段分类顺序违反 §11.2 固定顺序（Added→Changed→Deprecated→Removed→Fixed→Security→Dependencies→Documentation→Tests），实际: " + string.Join(" ", sections));
    }
}

// ── C4 Tests 段预估口径（WARN——数字真伪仍需人工对照实测，机械只能拦措辞）──
if (latestLine is not null)
{
    // sed -n '/^### Tests/,/^### \|^## [^[]/p'：从 ### Tests 行（含）到下一个
    // ### 或 "## 非[" 行（含）——sed 范围终点从起点下一行起扫，起点行不会自终止
    var block = lines.Skip(latestLine.Value - 1).Take(endLine!.Value - latestLine.Value + 1).ToList();
    var testsLines = new List<string>();
    var inTests = false;
    foreach (var l in block)
    {
        if (!inTests)
        {
            if (l.StartsWith("### Tests", StringComparison.Ordinal))
            {
                inTests = true;
                testsLines.Add(l);
            }
        }
        else
        {
            testsLines.Add(l);
            if (l.StartsWith("### ", StringComparison.Ordinal)
                || (l.StartsWith("## ", StringComparison.Ordinal) && l.Length > 3 && l[3] != '['))
            {
                break;
            }
        }
    }
    // 先剔除"基线约 N"合法形态（历史基线允许约数），再匹配剩余预估措辞
    var baselineRx = new Regex("基线约 ?[0-9]+");
    var vagueRx = new Regex("约 ?[0-9]+|[~～][0-9]+|预计");
    var vague = testsLines
        .Select(l => baselineRx.Replace(l, ""))
        .Where(l => vagueRx.IsMatch(l))
        .ToList();
    if (vague.Count > 0)
    {
        Warn(4, "Tests 段含疑似预估口径（§11.3 规则 1 要求实测/精确推导值，请校准）: " + string.Join("\n", vague.Take(2)));
    }
    else
    {
        Pass(4, "Tests 段无预估口径措辞");
    }
}

// ── C5 分类层内部术语泄漏（WARN——分类层 = 最新发布段至附录之间）──
// 注意：PDDD0xx/PALxxx 是公开分析器诊断 ID（消费者在构建警告中可见），属合法消费者术语，不拦
if (latestLine is not null && appendixLine is not null)
{
    var leakRx = new Regex("ITM-[0-9]+|(v[0-9]{2} 轮)|评审片|探针双红");
    var leak = new List<string>();
    for (var i = latestLine.Value - 1; i <= appendixLine.Value - 1 && leak.Count < 3; i++)
    {
        if (leakRx.IsMatch(lines[i])) leak.Add($"{i - latestLine.Value + 1}:{lines[i]}");   // grep -n：区间内相对行号
    }
    if (leak.Count > 0)
    {
        Warn(5, "分类层疑似内部术语泄漏（应外置到附录层）: " + string.Join("\n", leak.Take(2)));
    }
    else
    {
        Pass(5, "分类层无内部术语泄漏");
    }
}

// ── C6–C10 目标版本段对账（2026-10-26 增，对齐 Pal.ORM changelog-check）──
// 目标版本 = 参数（CI 从 tag 剥 v 传入）优先，缺省取 props 组合全版本；
// 只校验顶部第一个已发布段（[Unreleased] 跳过）——历史段是审计记录，不回改也不扫。
var targetVersion = targetArg.Length > 0 ? targetArg : version;
if (targetVersion.Length == 0)
{
    Fail(6, "无法确定目标版本（props 无 VersionPrefix 且未传目标版本参数）");
}
else
{
    foreach (var (id, kind, msg) in ValidateTargetSection(lines, targetVersion, TagCommitDate(targetVersion)))
    {
        if (kind == 0) Pass(id, msg);
        else if (kind == 1) Warn(id, msg);
        else Fail(id, msg);
    }
}

Console.WriteLine($"═══ 结果: {pass} 通过 / {warn} 警告 / {fail} 失败 ═══");
return fail == 0 ? 0 : 1;

// ─── 子进程执行：stdout/stderr 捕获丢弃（对齐 >/dev/null），只取退出码 ───
static (int Exit, string Output) RunCapture(string fileName, string arguments)
{
    var info = new ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    using var process = new Process { StartInfo = info };
    process.Start();
    // stderr 必须**并发排空**：.NET 的 RedirectStandardError 是管道——不同于 bash 的
    // `>/dev/null` 直写空设备，不排空时子进程写满管道缓冲会与父进程的 stdout
    // ReadToEnd 互等（死锁，子进程不退则一起挂住）。内容按原契约丢弃，仅消费掉缓冲。
    process.ErrorDataReceived += static (_, _) => { };
    process.BeginErrorReadLine();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    process.WaitForExit();   // 双调用：确保异步缓冲 flush（沿 check-all.cs 先例）
    return (process.ExitCode, output);
}

// ─── C6–C10 判定函数（纯函数，供 --selftest 直接调用生产逻辑）───
// Kind：0=PASS；1=WARN（跳过/不阻断，含 C7 失败后的级联跳过）；2=FAIL。
static List<(int Id, int Kind, string Msg)> ValidateTargetSection(string[] lines, string targetVersion, string tagDate)
{
    var results = new List<(int Id, int Kind, string Msg)>();
    var headings = lines
        .Select((l, i) => (Line: l, No: i))
        .Where(x => x.Line.StartsWith("## [", StringComparison.Ordinal))
        .ToList();
    // 第一个非 [Unreleased] 的 ## [ 段（与 C3 的排除口径一致：行内容含 Unreleased 即排除）
    var firstReleased = headings.FirstOrDefault(h => !h.Line.Contains("Unreleased"));
    if (firstReleased.Line is null)
    {
        results.Add((7, 2, "CHANGELOG 无任何已发布段标题（期望 ## [X.Y.Z] — YYYY-MM-DD）——目标版本段不存在"));
        AddCascadeSkips(results, "跳过（C7 未过：无已发布段）");
        return results;
    }

    var parsed = ParseVersionHeading(firstReleased.Line);
    if (parsed is null)
    {
        results.Add((7, 2, $"已发布段标题格式不符 §11.4（期望 ## [X.Y.Z] — YYYY-MM-DD 长破折号）: {firstReleased.Line}"));
        AddCascadeSkips(results, "跳过（C7 未过：标题不可解析）");
        return results;
    }
    results.Add((7, 0, $"已发布段标题格式符合 §11.4: {firstReleased.Line}"));
    var (topVersion, topDate) = parsed.Value;

    // C6 顶部已发布段版本 == 目标版本
    if (topVersion == targetVersion)
        results.Add((6, 0, $"顶部已发布段 [{topVersion}] == 目标版本 {targetVersion}"));
    else
        results.Add((6, 2, $"顶部已发布段 [{topVersion}] ≠ 目标版本 {targetVersion}——新版本段必须置于顶部（转正与版本三件套同次提交，§11.3 规则 5）"));

    // C8 段日期 == tag 提交日（tag 存在时；沿 C2 口径，无 tag 视为首发正常）
    if (tagDate.Length == 0)
        results.Add((8, 0, $"tag v{targetVersion} 尚未创建（首发正常），跳过与 tag 提交日的比对"));
    else if (tagDate != topDate)
        results.Add((8, 2, $"版本段日期 {topDate} ≠ tag v{targetVersion} 提交日 {tagDate}"));
    else
        results.Add((8, 0, $"段日期与 tag v{targetVersion} 提交日一致（{topDate}）"));

    // C9 日期单调：不早于下一发布段（不可解析的历史段标题跳过，不扫历史内容）
    (string Version, string Date)? next = null;
    foreach (var h in headings)
    {
        if (h.No <= firstReleased.No) continue;
        var p = ParseVersionHeading(h.Line);
        if (p is not null) { next = p; break; }
    }
    if (next is null)
        results.Add((9, 0, "无更早版本段，日期单调检查跳过"));
    else if (string.CompareOrdinal(topDate, next.Value.Date) < 0)
        results.Add((9, 2, $"日期倒序：[{topVersion}] 为 {topDate}，早于下一版本 [{next.Value.Version}] 的 {next.Value.Date}"));
    else
        results.Add((9, 0, $"日期单调：{topDate} ≥ [{next.Value.Version}] 的 {next.Value.Date}"));

    // C10 目标段条目非空（段区间 = 本标题行到下一 ## [ 行；附录层条目同样计入）
    var nextHeadingNo = headings.Where(h => h.No > firstReleased.No).Select(h => (int?)h.No).FirstOrDefault() ?? lines.Length;
    var bullets = 0;
    for (var i = firstReleased.No + 1; i < nextHeadingNo; i++)
    {
        if (lines[i].StartsWith("- ", StringComparison.Ordinal)) bullets++;
    }
    if (bullets == 0)
        results.Add((10, 2, $"[{topVersion}] 段无任何条目（至少 1 条 \"- \" 变更描述）"));
    else
        results.Add((10, 0, $"[{topVersion}] 段条目数 {bullets} 条"));

    return results;
}

// 段标题解析：## [X.Y.Z] — YYYY-MM-DD（§11.4 长破折号；不锚定行尾，容忍日期后副标题）。
// [Unreleased] 与缺日期/缺分隔符的标题均返回 null（C7 FAIL / 跳过的分界）。
static (string Version, string Date)? ParseVersionHeading(string line)
{
    var m = Regex.Match(line, @"^## \[(?<ver>[^\]]+)\]\s*—\s*(?<date>\d{4}-\d{2}-\d{2})");
    return m.Success ? (m.Groups["ver"].Value, m.Groups["date"].Value) : null;
}

// 全版本组合：VersionPrefix + （非空 VersionSuffix 时）"-" + VersionSuffix；空前缀返回空串。
static string ComposeFullVersion(string prefix, string suffix) =>
    prefix.Length == 0 ? "" : suffix.Length == 0 ? prefix : $"{prefix}-{suffix}";

// C7 失败后的级联跳过：C6/C8/C9/C10 以 WARN 记跳过，不叠加误报（阻断由 C7 FAIL 承担）。
static void AddCascadeSkips(List<(int Id, int Kind, string Msg)> results, string why)
{
    results.Add((6, 1, why));
    results.Add((8, 1, why));
    results.Add((9, 1, why));
    results.Add((10, 1, why));
}

// 自测断言辅助：指定检查项是否存在指定判定（避免常量数组实参触发 CA1861）。
static bool HasKind(List<(int Id, int Kind, string Msg)> results, int id, int kind) =>
    results.Any(r => r.Id == id && r.Kind == kind);

// tag 提交日（作者日期，--date=short）：tag 不存在返回空串（C8 视为首发跳过）。
static string TagCommitDate(string version)
{
    var (_, output) = RunCapture("git", $"log -1 --format=%ad --date=short v{version}");
    return output.Trim();
}

// ─── 自测：C6–C10 判定函数正反例（调用生产函数本身，非字符串语料）───
static int SelfTest()
{
    Console.WriteLine("═══ changelog-check.cs 自测（C6–C10 判定函数正反例）═══");
    var pass = 0;
    var total = 0;
    void Case(string name, bool ok)
    {
        total++;
        if (ok) { pass++; Console.WriteLine($"  ok   {name}"); }
        else Console.WriteLine($"  FAIL {name}");
    }

    // ComposeFullVersion
    Case("全版本组合：空后缀 = 前缀", ComposeFullVersion("4.0.0", "") == "4.0.0");
    Case("全版本组合：preview 后缀拼接", ComposeFullVersion("5.0.0", "preview.1") == "5.0.0-preview.1");
    Case("全版本组合：空前缀 = 空串（失败路径）", ComposeFullVersion("", "preview.1").Length == 0);

    // ParseVersionHeading
    Case("标题解析：本仓长破折号格式", ParseVersionHeading("## [4.0.0] — 2026-10-11") == ("4.0.0", "2026-10-11"));
    Case("标题解析：[Unreleased] 不可解析", ParseVersionHeading("## [Unreleased]") is null);
    Case("标题解析：缺分隔符不可解析（C7 FAIL 路径）", ParseVersionHeading("## [4.0.0] 2026-10-11") is null);
    Case("标题解析：容忍日期后副标题", ParseVersionHeading("## [5.0.0] — 2026-10-11 · 副标题")!.Value.Version == "5.0.0");

    // ValidateTargetSection：干净输入全过
    var clean = new[] { "# PalDDD 变更日志", "", "## [Unreleased]", "", "- 累积中", "", "## [9.9.9] — 2026-01-02", "", "### Added 新增", "", "- 新条目" };
    var cleanResult = ValidateTargetSection(clean, "9.9.9", "2026-01-02");
    Case("干净输入：零 FAIL", cleanResult.All(r => r.Kind != 2));
    Case("干净输入：C6/C7/C8/C10 均 PASS",
        HasKind(cleanResult, 6, 0) && HasKind(cleanResult, 7, 0) && HasKind(cleanResult, 8, 0) && HasKind(cleanResult, 10, 0));

    // 目标版本失配 → C6 FAIL（报文两侧版本都要有，可定位）
    var mismatch = ValidateTargetSection(clean, "9.9.8", "");
    Case("目标版本失配：C6 FAIL 且报文含两侧版本", mismatch.Any(r => r.Id == 6 && r.Kind == 2 && r.Msg.Contains("9.9.8") && r.Msg.Contains("9.9.9")));

    // 日期与 tag 提交日不一致 → C8 FAIL
    Case("tag 日期失配：C8 FAIL", ValidateTargetSection(clean, "9.9.9", "2026-01-03").Any(r => r.Id == 8 && r.Kind == 2));

    // 日期倒序 → C9 FAIL
    var mono = new[] { "## [Unreleased]", "", "## [9.9.9] — 2026-01-02", "", "- a", "", "## [8.8.8] — 2026-05-05", "", "- b" };
    Case("日期倒序：C9 FAIL", ValidateTargetSection(mono, "9.9.9", "").Any(r => r.Id == 9 && r.Kind == 2));

    // 条目为空 → C10 FAIL
    var emptySection = new[] { "## [Unreleased]", "", "## [9.9.9] — 2026-01-02", "", "### Added 新增", "", "## [8.8.8] — 2025-12-01", "", "- b" };
    Case("版本段零条目：C10 FAIL", ValidateTargetSection(emptySection, "9.9.9", "").Any(r => r.Id == 10 && r.Kind == 2));

    // 标题格式破坏 → C7 FAIL + 后续检查级联跳过（WARN，不叠加误报）
    var badFmt = new[] { "## [Unreleased]", "", "## [9.9.9] 2026-01-02", "", "- a" };
    var badFmtResult = ValidateTargetSection(badFmt, "9.9.9", "");
    Case("标题格式破坏：C7 FAIL", badFmtResult.Any(r => r.Id == 7 && r.Kind == 2));
    Case("标题格式破坏：C6/C8/C9/C10 级联跳过",
        HasKind(badFmtResult, 6, 1) && HasKind(badFmtResult, 8, 1) && HasKind(badFmtResult, 9, 1) && HasKind(badFmtResult, 10, 1));

    // 无已发布段 → C7 FAIL（首个发布前形态）
    var none = new[] { "## [Unreleased]", "", "- 累积中" };
    Case("无已发布段：C7 FAIL", ValidateTargetSection(none, "9.9.9", "").Any(r => r.Id == 7 && r.Kind == 2));

    Console.WriteLine($"═══ 自测结果: {pass}/{total} ═══");
    return pass == total ? 0 : 1;
}
