// ═══════════════════════════════════════════════════════════════
// changelog-check.cs——变更日志结构门禁（release.md §十二 Phase 4）
// （MIG-012-C，2026-09-11；由 scripts/changelog-check.sh 107 行 bash 等价迁移）
//
// 用法（在仓库根执行）：
//   dotnet run scripts/changelog-check.cs --     校验当前工作树
//
// 挂接点（docs/release.md）：§4.1 本地必跑 + §5.1 打 tag 前步骤 2.5；
// 本门禁 FAIL 时禁止打 tag（CHANGELOG 先行纪律的机械强制，§九教训 1/2）。
//
// 检查项：
//   C1 CHANGELOG 首个版本段必须是 [Unreleased]
//   C2 当前 VersionPrefix 若已存在同名 tag → 必须已有 [版本号] 转正段
//   C3 最新发布段分类层顺序符合 §11.2（Added→Changed→Deprecated→Removed→
//      Fixed→Security→Dependencies→Documentation→Tests）
//   C4 Tests 段禁止预估口径数字（"约 N"/"~N"/"预计"）[WARN 不阻断]
//   C5 分类层禁内部术语泄漏（ITM-\d+ / v\d+ 轮次引用等）[WARN 不阻断——
//      分类层=最新发布段至附录之间]
// 退出码：0=无 FAIL；1=任一 FAIL。
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
var version = Regex.Match(File.ReadAllText("Directory.Build.props"), "(?<=<VersionPrefix>)[^<]+").Value;
if (version.Length == 0)
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
    // 其后第一个 "### 附录" 行（/^### 附录/ 行首匹配）
    for (var i = latestLine.Value; i < lines.Length; i++)   // i 为 0 基，行号 i+1 > latestLine
    {
        if (lines[i].StartsWith("### 附录", StringComparison.Ordinal))
        {
            appendixLine = i + 1;
            break;
        }
    }
    // END_LINE = 附录行 ?? 下一个 ## [ 行 ?? 总行数（三重兜底对齐）
    foreach (var h in headingLines)
    {
        if (h.No > latestLine) { endLine = h.No; break; }
    }
    endLine ??= lines.Length;
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
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output);
}
