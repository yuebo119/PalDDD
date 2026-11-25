// ============================================================================
// release-notes.cs——从 CHANGELOG 版本段派生「精简 Release Body」（2026-11-25 移植）
//
// 出处：Pal.ORM scripts/release-notes.cs（发布变更日志规范 §七 的机械化载体），
// 按本仓适配：包目录 release-packages、包前缀 PalDDD.、anchor PalDDD.Core、
// 仓库 URL yuebo119/PalDDD、仓库根 marker PalDDD.slnx；新增 --selftest 供 gate-audit。
//
// 为什么需要它：本仓 release.yml 原用 awk 把整段 CHANGELOG 原文塞进 Release body，
// 而版本段含 9 分类 + ### 附录 工程叙事长表，Release 页因此失控（Pal.ORM 同族实测
// 1192~30973 字符摆动 26 倍）。改为单源派生：唯一真源是 CHANGELOG 版本段，禁止手写第二份。
//
// 用法：
//   dotnet run scripts/release-notes.cs -- <version|unreleased> [选项]
//     <version>              目标版本号（如 5.1.0），或 unreleased 预览未发布累积段
//   选项：
//     --write <path>         写入文件（默认输出到 stdout）
//     --check                形态校验：头条存在且 ≤80 字、每条带短标题、body 体量 ≤ 上限
//     --packages-dir <dir>   从实际 nupkg 推导包清单（默认 release-packages）
//     --tag <name>           覆盖对比链接的 tag 名（默认 v<version>）
//     --max-chars <n>        body 字符上限（默认 1400，仅 --check 生效）
//     --selftest             纯函数判定自测（不读仓库）
// 退出码：0 = 成功/校验通过；1 = FAIL；2 = 用法错
//
// 派生规则（单一事实源：CHANGELOG 版本段）：
//   头条一句话    = 版本标题下第一段（≤80 字，Release 页首行）
//   ⚠️ 升级须知   = ### 破坏性/行为变更 段，取「短标题：动作首句」
//   本版变更      = 其余段落的条目，只取短标题，跳过 〔内部〕 与 ### 附录 条目
//   包清单        = 一行 NuGet 链接，替代逐包表格（无产物目录时只给锚点包链接，不写包数）
//   页脚          = 完整变更日志 + compare 链接
//
// 已知不覆盖（T-34）：标题格式/日期/段日期单调/版本对账由 changelog-check C1-C10 负责，
// 本脚本只管 body 派生形态（头条/短标题/体量）。二者职责不重叠、不重复校验。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表；本脚本输出是发布协议的固定中文行
//（::error::/::warning:: 被 CI 与 grep 消费），沿 new-skip-guard.cs 先例整文件抑制。
// SYSLIB1045：顶级语句 + static local function 形态无法挂 [GeneratedRegex]（需 partial 类），
// 正则低频调用（一次运行），源生成收益不抵结构改造。
#pragma warning disable CA1303
#pragma warning disable SYSLIB1045

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return SelfTest();
}

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: release-notes.cs <version|unreleased> [--write <path>] [--check] [--packages-dir <dir>] [--tag <name>] [--max-chars <n>] [--selftest]");
    return 2;
}

var version = args[0];
var writePath = "";
var checkMode = false;
var packagesDir = "release-packages";
var tagName = version == "unreleased" ? "" : $"v{version}";
var maxChars = 1400;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--write":
            writePath = args[++i];
            break;
        case "--check":
            checkMode = true;
            break;
        case "--packages-dir":
            packagesDir = args[++i];
            break;
        case "--tag":
            tagName = args[++i];
            break;
        case "--max-chars":
            maxChars = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"::error::未知参数 {args[i]}");
            return 2;
    }
}

var repoRoot = FindRepoRoot();
Environment.CurrentDirectory = repoRoot;

if (!File.Exists("CHANGELOG.md"))
{
    Console.Error.WriteLine("::error::CHANGELOG.md 不存在");
    return 1;
}

var lines = File.ReadAllLines("CHANGELOG.md").Select(l => l.TrimEnd('\r')).ToArray();
var headingRe = new Regex(@"^## \[(?<ver>[^\]]+)\]");

// ── 1. 定位目标段 ──
var headings = new List<(int Index, string Version)>();
for (var i = 0; i < lines.Length; i++)
{
    var m = headingRe.Match(lines[i]);
    if (m.Success)
    {
        headings.Add((i, m.Groups["ver"].Value));
    }
}

var targetIdx = headings.FindIndex(h => h.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
if (targetIdx < 0)
{
    Console.Error.WriteLine($"::error::CHANGELOG 中未找到版本段 [{version}]");
    return 1;
}

var target = headings[targetIdx];
var sectionEnd = targetIdx + 1 < headings.Count ? headings[targetIdx + 1].Index : lines.Length;
var section = lines[(target.Index + 1)..sectionEnd];

// 上一版本：CHANGELOG 中目标段之后的下一个版本段（文件顺序，不排序）
var prevVersion = targetIdx + 1 < headings.Count ? headings[targetIdx + 1].Version : "";

var fail = 0;

// ── 2. 头条一句话 ──
var headline = ExtractHeadline(section);
if (headline.Length == 0)
{
    if (checkMode)
    {
        Console.Error.WriteLine("::error::版本段缺头条：标题下第一段必须是一句话概述（≤80 字，Release 页首行）");
        fail = 1;
    }
    headline = SynthesizeHeadline(section);
}
else if (headline.Length > 80 && checkMode)
{
    Console.Error.WriteLine($"::error::头条 {headline.Length} 字，超过 80 字上限：{headline}");
    fail = 1;
}

// ── 3. 条目分组与解析 ──
var breaking = new List<Bullet>();
var changes = new List<Bullet>();
var untitled = 0;
var currentGroup = "";

foreach (var line in section)
{
    if (line.StartsWith("### ", StringComparison.Ordinal))
    {
        currentGroup = line;
        continue;
    }
    if (!line.StartsWith("- ", StringComparison.Ordinal))
    {
        continue;
    }

    var b = ParseBullet(line);
    if (!b.HasTitle)
    {
        untitled++;
        if (checkMode)
        {
            Console.Error.WriteLine($"::error::条目缺短标题（须写成 \"- **标题**：细节\"）：{Truncate(line, 60)}");
            fail = 1;
        }
    }
    else if (b.Title.Length > 32)
    {
        Console.Error.WriteLine($"::warning::条目标题 {b.Title.Length} 字，超出 32 字建议值：{b.Title}");
    }

    // ### 附录（历史段的工程过程明细）条目一律视为内部项，不进 Release body
    if (currentGroup.StartsWith("### 附录", StringComparison.Ordinal))
    {
        b = b with { Internal = true };
    }

    if (currentGroup.StartsWith("### 破坏性", StringComparison.Ordinal))
    {
        breaking.Add(b);
    }
    else
    {
        changes.Add(b);
    }
}

if (changes.Count == 0 && breaking.Count == 0)
{
    Console.Error.WriteLine("::error::版本段无任何条目");
    return 1;
}

// ── 4. 组装 body ──
var packages = DiscoverPackages(packagesDir);
var sb = new StringBuilder();
sb.Append(headline).Append("\n\n");

var breakingOut = breaking.Where(b => !b.Internal).ToList();
if (breakingOut.Count > 0)
{
    sb.Append("## ⚠️ 升级须知\n\n");
    foreach (var b in breakingOut)
    {
        var detail = FirstSentence(b.Detail);
        sb.Append(detail.Length > 0 ? $"- **{b.Title}**：{detail}\n" : $"- **{b.Title}**\n");
    }
    sb.Append('\n');
}

var changesOut = changes.Where(b => !b.Internal).ToList();
if (changesOut.Count > 0)
{
    sb.Append("## 本版变更\n\n");
    foreach (var b in changesOut)
    {
        sb.Append($"- **{b.Title}**\n");
    }
    sb.Append('\n');
}

sb.Append("---\n\n");

// 包清单压成一行：一个 NuGet 链接 + 包名列表，替代逐包表格（省约 220 字符）。
// 无实际产物目录（本地预览）时只给锚点包链接，不写包数——避免 35 这一数字出现第二真源
//（可打包包数的唯一真源是 count-audit 推导表）。
if (packages.Count > 0)
{
    var pkgNames = string.Join(" · ", packages.Select(p => p.Replace("PalDDD.", "", StringComparison.Ordinal)));
    var pkgAnchor = packages.Contains("PalDDD.Core") ? "PalDDD.Core" : packages[0];
    sb.Append($"[NuGet 上的 {packages.Count} 个包](https://www.nuget.org/packages/{pkgAnchor})：{pkgNames}\n\n");
}
else
{
    sb.Append("[NuGet 上的 PalDDD 包](https://www.nuget.org/packages/PalDDD.Core)\n\n");
}

var changelogUrl = "https://github.com/yuebo119/PalDDD/blob/main/CHANGELOG.md";
var footer = $"[完整变更日志]({changelogUrl})";
if (prevVersion.Length > 0 && tagName.Length > 0)
{
    footer += $" · [v{prevVersion}...{tagName} 对比](https://github.com/yuebo119/PalDDD/compare/v{prevVersion}...{tagName})";
}
sb.Append(footer).Append('\n');

var body = sb.ToString();

// ── 5. 体量门禁 ──
if (checkMode)
{
    var len = body.Length;
    Console.WriteLine($"  头条: {headline.Length} 字");
    Console.WriteLine($"  升级须知: {breakingOut.Count} 条（源 {breaking.Count} 条，内部 {breaking.Count - breakingOut.Count} 条）");
    Console.WriteLine($"  本版变更: {changesOut.Count} 条（源 {changes.Count} 条，内部 {changes.Count - changesOut.Count} 条）");
    Console.WriteLine($"  body 体量: {len} 字符（上限 {maxChars}）");
    if (len > maxChars)
    {
        Console.Error.WriteLine($"::error::Release body {len} 字符超过 {maxChars} 上限，请压缩条目细节或拆分为更小版本");
        fail = 1;
    }
    if (untitled > 0)
    {
        Console.WriteLine($"  无标题条目: {untitled} 条（已在上面逐条报告）");
    }
}

if (fail != 0)
{
    Console.Error.WriteLine($"\nFAIL Release notes 校验未通过（{version}）");
    return 1;
}

if (writePath.Length > 0)
{
    File.WriteAllText(writePath, body, new UTF8Encoding(false));
    if (!checkMode)
    {
        Console.WriteLine($"已写入 {writePath}（{body.Length} 字符）");
    }
}
else
{
    Console.Write(body);
}

if (checkMode)
{
    Console.WriteLine($"\nPASS Release notes 校验通过（{version}，{body.Length} 字符）");
}
return 0;

// ── 辅助 ──

static string ExtractHeadline(string[] section)
{
    var buf = "";
    foreach (var raw in section)
    {
        var line = raw.Trim();
        if (line.Length == 0)
        {
            if (buf.Length > 0)
            {
                break;
            }
            continue;
        }
        if (line.StartsWith('#'))
        {
            break;
        }
        if (line.StartsWith("- ", StringComparison.Ordinal))
        {
            break;
        }
        // Keep a Changelog 允许版本标题下写一段概述，引用式（>）与普通段落都接受
        line = line.TrimStart('>').Trim();
        buf = buf.Length == 0 ? line : $"{buf}{line}";
    }
    return buf;
}

static string SynthesizeHeadline(string[] section)
{
    var count = section.Count(l => l.StartsWith("- ", StringComparison.Ordinal));
    return count == 0 ? "本版发布。" : $"本版发布，含 {count} 项变更。";
}

static Bullet ParseBullet(string line)
{
    var text = line[2..].Trim();
    var isInternal = text.Contains("〔内部〕");
    var m = Regex.Match(line, @"^- \*\*(?<title>[^*]{1,64})\*\*(?<rest>.*)$");
    if (m.Success)
    {
        var title = m.Groups["title"].Value.Trim();
        var detail = m.Groups["rest"].Value.TrimStart('：', ':', ' ');
        return new Bullet(title, detail.Replace("〔内部〕", "").Trim(), isInternal, HasTitle: true);
    }

    // 旧格式（无短标题）：从正文派生一个可读标题，渲染不塌成空行；--check 仍判 FAIL
    var plain = text.Replace("〔内部〕", "").Trim();
    var cut = plain.IndexOfAny(['：', '，', '。', '；']);
    var title2 = cut > 0 && cut <= 32 ? plain[..cut] : Truncate(plain, 32);
    return new Bullet(title2, plain, isInternal, HasTitle: false);
}

static string FirstSentence(string detail)
{
    if (detail.Length == 0)
    {
        return "";
    }
    var end = detail.IndexOf('。');
    var s = end >= 0 ? detail[..(end + 1)] : detail;
    // 长句仍可能撑大体量：截到 160 字符，保留完整标点语义
    return s.Length > 160 ? $"{s[..160]}…" : s;
}

static List<string> DiscoverPackages(string dir)
{
    if (!Directory.Exists(dir))
    {
        return [];
    }
    // 产物目录可能残留其他版本的 nupkg（如预览未发布版本时），按版本后缀剥离而非硬替换
    return Directory.GetFiles(dir, "*.nupkg")
        .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))
        .Select(n => Regex.Replace(n!, @"\.\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", ""))
        .Where(n => n.StartsWith("PalDDD.", StringComparison.Ordinal))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();
}

static string Truncate(string s, int max) => s.Length <= max ? s : $"{s[..max]}…";

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalDDD.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalDDD.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}

// ── 自测（纯函数，供 gate-audit 隔离探针）──

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

    // ParseBullet
    var b1 = ParseBullet("- **短标题**：这里的细节");
    Case("ParseBullet 新格式：标题与细节分离", b1.HasTitle && b1.Title == "短标题" && b1.Detail == "这里的细节");
    var b2 = ParseBullet("- **标题**：细节内容〔内部〕");
    Case("ParseBullet 剥离〔内部〕标记并置 Internal", b2.Internal && !b2.Detail.Contains("〔内部〕", StringComparison.Ordinal));
    var b3 = ParseBullet("- 一段没有短标题的旧格式描述，后面还有内容");
    Case("ParseBullet 旧格式：HasTitle=false", !b3.HasTitle);
    var b4 = ParseBullet("- **" + new string('长', 70) + "**：x");
    Case("ParseBullet 标题超 64 字：回退为无标题", !b4.HasTitle);
    var b5 = ParseBullet("- **带冒号:的标题**：细节");
    Case("ParseBullet 中文冒号与英文冒号均可剥离", b5.Detail.Length > 0 && !b5.Detail.StartsWith('：'));

    // ExtractHeadline
    Case("ExtractHeadline 普通段落", ExtractHeadline(["本版一句话概述。", "", "- 条目"]) == "本版一句话概述。");
    Case("ExtractHeadline 遇 - 条目停止", ExtractHeadline(["概述第一句。", "- **x**：y"]).StartsWith("概述第一句。", StringComparison.Ordinal)
        && !ExtractHeadline(["概述第一句。", "- **x**：y"]).Contains('x'));
    Case("ExtractHeadline 遇 # 标题停止", ExtractHeadline(["概述。", "### 变更要点", "- a"]).TrimEnd() == "概述。");
    Case("ExtractHeadline 去 > 引用前缀", ExtractHeadline(["> 引用式概述。", ""]) == "引用式概述。");
    Case("ExtractHeadline 多行拼接无分隔", ExtractHeadline(["上句，", "下句。", ""]) == "上句，下句。");
    Case("ExtractHeadline 空段返回空", ExtractHeadline(["", "", "- a"]).Length == 0);

    // FirstSentence
    Case("FirstSentence 取到句号", FirstSentence("第一句。第二句。") == "第一句。");
    Case("FirstSentence 无句号取整句", FirstSentence("没有句号的细节") == "没有句号的细节");
    Case("FirstSentence 空串安全", FirstSentence("").Length == 0);
    var longSentence = new string('字', 200);
    Case("FirstSentence 超 160 截断加省略号", FirstSentence(longSentence).EndsWith('…') && FirstSentence(longSentence).Length == 161);

    // SynthesizeHeadline
    Case("SynthesizeHeadline 计数条目", SynthesizeHeadline(["- a", "- b"]).Contains('2'));
    Case("SynthesizeHeadline 空段", SynthesizeHeadline([]) == "本版发布。");

    // Truncate
    Case("Truncate 不超限原样", Truncate("abc", 5) == "abc");

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}

internal sealed record Bullet(string Title, string Detail, bool Internal, bool HasTitle);
