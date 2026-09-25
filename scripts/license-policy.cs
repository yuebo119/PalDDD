// ============================================================================
// license-policy.cs——依赖许可策略门禁（file-based app · 零 package 依赖）
//
// ── 策略声明（2026-09-25 裁决 · 本文件即该裁决的机械执行层）──────────────
// 裁决原话（2026-09-25 · 维护者）：
//     「所有引用的库必须是开源的许可，不使用商业许可。」
// 裁决出处（两条既有可回溯记录，不是本次新造的规则）：
//     · `.github/dependabot.yml` 的 `Verify.TUnit >= 33` ignore 注释 —— 2026-09-23
//       裁决「本项目只使用开源依赖，不接带商业许可门的库」，豁免声明 / 赞助 /
//       Verify_SponsorshipLicenseIgnored 三条路都不走；
//     · `CHANGELOG.md` `[Unreleased]` → `### Dependencies` 段同一裁决的发布记录。
// 为什么写成脚本：声明只活在文档里，而文档不会执行。依赖一变就跑、跑不过就拦，
// 才叫机械门禁（全局 AGENTS.md「确定性 > 概率性」）。
//
// ── 退出码（三值；改本行须同提交改 AGENTS.md §2 门禁表与 ci.yml 注释）─────
//     0 = 全部通过
//     1 = 存在非白名单许可 / 禁令命中 / 未登记或已到期的 gap / 无法判定（fail-closed）
//     2 = 自测失败（--selftest 有断言不成立）
//   注：仓库根定位失败、依赖图取不到、nuspec 读不到——全部归入 1（「无法判定」），
//       不另设码；本文件的三值语义是裁决的一部分，不沿用同族脚本的「2 = 根失败」。
//
// ── 判定输入（全部离线、确定性、不联网）─────────────────────────────────
//   · 直接依赖：`Directory.Packages.props` 的 `PackageVersion`
//              ∪ 各 `*.csproj` 的 `PackageReference`（读 XML，不靠文本 grep）；
//   · 传递依赖：`dotnet list PalDDD.slnx package --include-transitive --format json`
//              —— 输出先落临时文件再解析（十几万字节的 JSON 不进控制台），跑完即删；
//   · 许可证据：本机 NuGet 缓存 nuspec
//              `<NUGET_PACKAGES 或 ~/.nuget/packages>/<包 id>/<版本>/<id>.nuspec`，
//              取证顺序 `<license type="expression">` → `<license type="file">`
//              → `<repository licenseUrl>` → `<licenseUrl>`；
//              取不到、或取到的值推不出 SPDX 标识 → **fail-closed：不猜**（走 gap 台账）。
//
// ── 三条策略 ────────────────────────────────────────────────────────────
//   [1] 白名单 AllowList（deny-by-default）——OSI/FSF 认可的开源许可表达式。
//       不在表内 = 拒绝；**不存在「不认识就放行」的分支**。
//   [2] 禁令清单 BanList —— 包 + 版本下限 + 原因 + 裁决日期，优先于白名单。
//       关键理由：Verify 33+ 的 nuspec 许可表达式**仍是 MIT**，上游把商业门
//       （SponsorCheck 商业维护费）做进了构建期、不在许可字符串里 —— 纯白名单
//       抓不到，只有包级禁令能拦。因此禁令按「包 + 版本下限」写，与许可字符串正交。
//       新增条目的准入：必须能从 nuspec 或官网证实「商业或非 OSI」，并写明
//       裁决日期；不能证实的不进表（P0 #4 不捏造技术实体）。
//   [3] gap 台账 GapLedger —— 许可缺失 / 文件型许可 / 无法归类的包登记为观察态
//       （包名 + 原因 + owner + 到期日），到期未处置即 FAIL。对齐本仓
//       `TechDebtGuardTests` 的观察态纪律（T-24）：观察态必须有 owner 与过期时间，
//       否则它永久存在且无人再看，退化为「看起来在管」的静默放行。
//       ⚠️ 台账不是白名单：台账项只在**证据推不出 SPDX 标识**时才被查；一旦
//       证据可判且不在白名单，照常 FAIL（见 Judge 的判定顺序）——不许把不确定的
//       包塞进白名单来换绿。
//
// ── 判定集口径（scope）──────────────────────────────────────────────────
//     referenced = `*.csproj` 的 `PackageReference` ∪ 已解析依赖图
//   只在 `Directory.Packages.props` 声明、全仓无 `PackageReference`、也不在已解析
//   依赖图中的条目 = **零消费声明**，不判定（逐条打印在输出里）。裁决的对象是
//   「引用的库」——没有引用发生就还没有许可问题；这不是许可豁免，一旦被引用
//   即自动进入判定集。注意：禁令清单不受此口径约束（声明即视为意向使用）。
//
// 用法：dotnet run scripts/license-policy.cs
//       dotnet run scripts/license-policy.cs -- --selftest
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的固定
// 协议行（FAIL [禁令] / PASS / 全部通过 被 grep 与 ci-failed-tests.cs 消费），
// 固定中文非用户可配文案——沿 secret-scan.cs / vuln-scan.cs / verify-ai.cs 先例
// 整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

// Windows 控制台默认编码非 UTF-8，中文输出会乱码——对齐同族门禁脚本
Console.OutputEncoding = Encoding.UTF8;

// ══════════════ 策略数据（上方声明块的机器可读形态 · 两侧须同提交同步）══════════════

// [1] 白名单：OSI/FSF 认可的开源许可 SPDX 表达式。表内每一项要么来自 2026-09-25
//     裁决给定的清单，要么来自本仓依赖图里真实出现且必须放行的项（AGPL-3.0-only
//     = PalORM 五包；PostgreSQL = Npgsql，OSI 2010-02-10 批准，
//     见 https://opensource.org/license/PostgreSQL）。带 WITH 例外的整串与
//     OR/AND 复合表达式由 IsAllowListed 处理，无需在此登记复合形态。
string[] allowList =
[
    "MIT",
    "Apache-2.0",
    "BSD-2-Clause",
    "BSD-3-Clause",
    "BSD-4-Clause",
    "ISC",
    "0BSD",
    "MPL-2.0",
    "LGPL-2.1-or-later",
    "LGPL-3.0-or-later",
    "GPL-3.0-only",
    "GPL-3.0-or-later",
    "AGPL-3.0-only",
    "AGPL-3.0-or-later",
    "CC0-1.0",
    "Unlicense",
    "Unicode-3.0",
    "MS-PL",
    "MS-RL",
    "PostgreSQL",
    "Apache-2.0 WITH LLVM-exception",
];

// [2] 禁令清单：包 + 版本下限（仅比对主版本）+ 原因 + 裁决日期。
BanEntry[] banList =
[
    new("Verify", 33,
        "上游 33 起构建期强制 SponsorCheck 商业维护费声明（SC021），未声明即中断构建——属带商业许可门的库",
        new DateOnly(2026, 9, 23)),
    new("Verify.TUnit", 33,
        "同一 SponsorCheck 门（Verify 为其中间依赖）；2026-09-23 Dependabot PR #2 实证使 build-and-test / coverage / CodeQL 三处同时红",
        new DateOnly(2026, 9, 23)),
];

// [3] gap 台账：许可证据推不出 SPDX 标识的包，登记为有期限的观察态。
//     到期未处置即 FAIL；到期后要么补证（能判则进白名单判定）、要么移除该包。
//     以下 6 条为 2026-09-25 首次全量实跑的存量发现，逐条附 nuspec 实证。
GapEntry[] gapLedger =
[
    new("CommandLineParser",
        "nuspec 为 <license type=\"file\">License.md</license>，licenseUrl 是 https://aka.ms/deprecateLicenseUrl 占位——离线推不出 SPDX 标识",
        "框架维护者", new DateOnly(2026, 12, 31), "读包内 License.md 确认许可后改判（命中白名单即销号），或等上游改发 SPDX expression"),
    new("Microsoft.DotNet.PlatformAbstractions",
        "nuspec 为 <license type=\"file\">LICENSE.TXT</license>，licenseUrl 同为 aka.ms 占位——离线推不出 SPDX 标识",
        "框架维护者", new DateOnly(2026, 12, 31), "读包内 LICENSE.TXT 确认许可后改判；该包 3.1.6 后停更，也可评估移除"),
    new("Microsoft.Testing.Extensions.CodeCoverage",
        "nuspec 为 <license type=\"file\">License.txt</license>，licenseUrl 同为 aka.ms 占位——离线推不出 SPDX 标识",
        "框架维护者", new DateOnly(2026, 12, 31), "读包内 License.txt 确认许可后改判（TUnit 传递依赖，测试期使用）"),
    new("SQLite",
        "nuspec 为 <license type=\"file\">LICENSE.txt</license>，licenseUrl 同为 aka.ms 占位——离线推不出 SPDX 标识",
        "框架维护者", new DateOnly(2026, 12, 31), "SQLitePCLRaw 传递依赖；读包内 LICENSE.txt 确认许可后改判"),
    new("NETStandard.Library",
        "nuspec 无 <license> 元素，仅有 <licenseUrl> 指向 github 上的 LICENSE.TXT 正文——离线取不回正文",
        "框架维护者", new DateOnly(2026, 12, 31), "改为可离线判定的取证方式，或换用能发 SPDX expression 的依赖线"),
    new("librdkafka.redist",
        "nuspec 无 <license> 元素，仅有 <licenseUrl> 指向 github 上的 LICENSES.txt（多许可合集）——离线取不回正文",
        "框架维护者", new DateOnly(2026, 12, 31), "Confluent.Kafka 传递依赖（含原生二进制再分发）；逐项核对合集后改判"),
];

var policy = new Policy(
    new HashSet<string>(allowList, StringComparer.OrdinalIgnoreCase),
    banList,
    gapLedger);

if (args.Contains("--selftest"))
{
    return SelfTest(policy, DateOnly.FromDateTime(DateTime.UtcNow));
}

var today = DateOnly.FromDateTime(DateTime.UtcNow);

// ─── 1. 仓库根定位（失败即 fail-closed，退出码 1）───
var root = FindRepoRootOrNull();
if (root is null)
{
    Console.WriteLine("FAIL 无法定位仓库根（无 PalDDD.slnx）——许可扫描未执行，拒绝报绿");
    return 1;
}

// ─── 2. 直接依赖声明（props 的 PackageVersion ∪ csproj 的 PackageReference）───
var declaredResult = ReadDeclared(root);
if (declaredResult.Error is not null)
{
    Console.WriteLine($"FAIL {declaredResult.Error}——直接依赖清单未取得，拒绝报绿");
    return 1;
}
var declared = declaredResult.Packages;

// 已知版本（props 先入为主——CPM 下 props 是版本权威，csproj 通常不带 Version）
var directVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var d in declared)
    if (d.Version is not null)
        directVersions.TryAdd(d.Id, d.Version);

// ─── 3. 禁令先判（只读声明、不跑子进程、不读缓存——便宜且与许可字符串正交）───
//    按**已知版本**比对：CPM 下 csproj 的 PackageReference 不带 Version，拿它去比
//    会把当前钉住的 32.x 误判成「版本解析不出」；props 有版本就用 props 的。
//    真的连版本都没有时才 fail-closed（禁令下限无法比对 = 不能证明合规）。
var bannedDirect = new List<(string Id, string Version, string Reason)>();
foreach (var id in declared.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase))
{
    var version = directVersions.TryGetValue(id, out var known) ? known : "?";
    var reason = BanReason(id, version, banList);
    if (reason is not null) bannedDirect.Add((id, version, reason));
}
if (bannedDirect.Count > 0)
{
    foreach (var hit in bannedDirect)
        Console.WriteLine($"FAIL [禁令] {hit.Id} {hit.Version} —— {hit.Reason}");
    Console.WriteLine("═══ 许可策略：禁令命中，终止（不继续扫描）═══");
    return 1;
}

// ─── 4. 依赖图（传递依赖；输出落临时文件再解析）───
var graphResult = ReadGraph(root);
if (graphResult.Error is not null)
{
    Console.WriteLine($"FAIL {graphResult.Error}——依赖图未取得，拒绝报绿");
    return 1;
}
var graphPairs = graphResult.Pairs;

// 已被引用（= 进入判定集）：csproj 直接引用 ∪ 依赖图
var graphIds = new HashSet<string>(graphPairs.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
var csprojIds = new HashSet<string>(declared.Where(d => d.Source == "csproj").Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
var referenced = new HashSet<string>(graphIds, StringComparer.OrdinalIgnoreCase);
foreach (var id in csprojIds) referenced.Add(id);

// 零消费声明（有 PackageVersion、但既无 csproj 引用也不在依赖图 → 不判定，逐条打印）
var dormant = declared
    .Where(d => d.Source == "props" && !referenced.Contains(d.Id))
    .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
    .ToList();

// 被引用却推不出任何版本 ⇒ 定位不到 nuspec ⇒ fail-closed
var versionless = referenced
    .Where(id => !graphIds.Contains(id) && !directVersions.ContainsKey(id))
    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
    .ToList();

// ─── 5. 组判定集：依赖图全部版本 + 被引用包的声明版本 ───
var judgePairs = new HashSet<(string Id, string Version)>(graphPairs);
foreach (var d in declared)
{
    if (d.Version is null) continue;
    if (d.Source == "csproj" || referenced.Contains(d.Id))
        judgePairs.Add((d.Id, d.Version));
}

if (judgePairs.Count == 0 && versionless.Count == 0)
{
    Console.WriteLine("FAIL 判定集为空（无任何被引用的包）——空输入不构成许可结论（fail-closed）");
    return 1;
}

// ─── 6. 逐包读 nuspec → 判定 ───
var cacheRoot = NuGetCacheRoot();
var packageDirs = IndexPackageDirs(cacheRoot);

var findings = new List<Finding>();
foreach (var pair in judgePairs.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase))
{
    var evidence = ReadEvidence(packageDirs, pair.Id, pair.Version);
    var judgment = Judge(pair.Id, pair.Version, evidence, policy, today);
    findings.Add(new Finding(pair.Id, pair.Version, judgment));
}

// ─── 7. 输出 ───
Console.WriteLine("═══ 依赖许可策略（license-policy）═══");
Console.WriteLine($"仓库根：{root}");
Console.WriteLine($"NuGet 缓存：{cacheRoot}（{packageDirs.Count} 个包目录）");
Console.WriteLine($"直接声明 {declared.Count} 条 · 已解析图 {graphPairs.Count} 条 · 判定集 {findings.Count} 个包版本");
Console.WriteLine();

if (dormant.Count > 0)
{
    Console.WriteLine($"零消费声明（有 PackageVersion、无 PackageReference 且不在已解析图，故不判定；共 {dormant.Count} 条）：");
    foreach (var d in dormant)
        Console.WriteLine($"  · {d.Id} {d.Version} —— 一旦被引用即自动进入判定集");
    Console.WriteLine();
}

var failCount = 0;
foreach (var id in versionless)
{
    Console.WriteLine($"FAIL [无法判定] {id} ? —— 被引用但声明与依赖图都推不出版本，无法定位 nuspec");
    failCount++;
}

foreach (var f in findings)
{
    switch (f.Outcome.Result)
    {
        case Verdict.Allow:
            Console.WriteLine($"PASS {f.Id} {f.Version} —— {f.Outcome.Detail}");
            break;
        case Verdict.GapOpen:
            Console.WriteLine($"PASS [gap 观察] {f.Id} {f.Version} —— {f.Outcome.Detail}");
            break;
        case Verdict.BanHit:
            Console.WriteLine($"FAIL [禁令] {f.Id} {f.Version} —— {f.Outcome.Detail}");
            failCount++;
            break;
        case Verdict.NotAllowListed:
            Console.WriteLine($"FAIL [非白名单] {f.Id} {f.Version} —— {f.Outcome.Detail}");
            failCount++;
            break;
        case Verdict.GapExpired:
            Console.WriteLine($"FAIL [gap 到期] {f.Id} {f.Version} —— {f.Outcome.Detail}");
            failCount++;
            break;
        default:
            Console.WriteLine($"FAIL [无法判定] {f.Id} {f.Version} —— {f.Outcome.Detail}");
            failCount++;
            break;
    }
}

var observing = findings.Count(f => f.Outcome.Result == Verdict.GapOpen);
Console.WriteLine();
if (failCount > 0)
{
    Console.WriteLine($"═══ 许可策略：{failCount} 失败（判定集 {findings.Count} · 观察态 {observing}）═══");
    return 1;
}
Console.WriteLine($"═══ 许可策略：全部通过（判定集 {findings.Count} · 观察态 {observing} · 零消费 {dormant.Count}）═══");
return 0;

// ══════════════ 判定（纯函数，供 --selftest 覆盖）══════════════

// 白名单判定（deny-by-default）：整串命中（覆盖 `Apache-2.0 WITH LLVM-exception`
// 这类带例外的表达式）→ 否则按 OR/AND 拆操作数，**每一个**操作数都必须在表内
//（`Apache-2.0 OR MPL-2.0` 是任选其一，所以每个候选本身都得是开源许可）。
// 无 OR/AND 结构且整串不在表内 → 拒绝。
static bool IsAllowListed(string expression, HashSet<string> allow)
{
    var trimmed = expression.Trim();
    if (trimmed.Length == 0) return false;
    if (allow.Contains(trimmed)) return true;

    var parts = SplitKeepEmpty(trimmed, " OR ");
    if (parts.Length == 1) parts = SplitKeepEmpty(trimmed, " AND ");
    if (parts.Length == 1) return false;   // 无复合结构，整串已判定为不在表内

    foreach (var part in parts)
        if (!IsAllowListed(part, allow)) return false;
    return true;
}

// 按单个字符串分隔（不用数组字面量，避开 CA1861 常量数组参数）
static string[] SplitKeepEmpty(string text, string separator) =>
    text.Split(separator, StringSplitOptions.None);

// 禁令判定：包名忽略大小写相同、且主版本 ≥ 下限即命中；版本解析不出时 fail-closed
//（不能因为「不知道版本」就放行）。返回 null = 未命中。
static string? BanReason(string id, string version, BanEntry[] bans)
{
    foreach (var b in bans)
    {
        if (!string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
        var major = MajorOf(version);
        if (major < 0)
            return $"{b.Id} 的版本「{version}」解析不出主版本——禁令下限 {b.MinMajor} 无法比对（fail-closed）；裁决 {b.DecidedOn:yyyy-MM-dd}";
        if (major >= b.MinMajor)
            return $"{b.Id} 主版本 {major} ≥ {b.MinMajor}（{b.Reason}）；裁决 {b.DecidedOn:yyyy-MM-dd}";
    }
    return null;
}

// 取主版本号；非数字/空 → -1（由 BanReason 转 fail-closed）
static int MajorOf(string version)
{
    if (string.IsNullOrWhiteSpace(version)) return -1;
    var end = 0;
    while (end < version.Length && char.IsAsciiDigit(version[end])) end++;
    if (end == 0) return -1;
    // 后继必须是分隔符（. - +），否则整体不是版本形态（如 "abc"）
    if (end < version.Length && version[end] is not ('.' or '-' or '+')) return -1;
    return int.TryParse(version[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : -1;
}

// gap 台账查找（包名忽略大小写）
static GapEntry? FindGap(string id, GapEntry[] gaps)
{
    foreach (var g in gaps)
        if (string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) return g;
    return null;
}

// 单包判定（核心纯函数）。顺序不可调换：
//   1) 禁令 —— 与许可字符串无关，优先级最高；
//   2) 证据能推出标识 → 白名单判定（**此时 gap 台账不参与**：台账只接「判不了」，
//      不接「判了但不在白名单」，否则台账就成了绕过白名单的后门）；
//   3) 证据推不出 → 查台账：登记且未到期 = 观察态放行；到期 = FAIL；未登记 = FAIL。
static Judgment Judge(string id, string version, LicenseEvidence evidence, Policy policy, DateOnly today)
{
    var ban = BanReason(id, version, policy.Bans);
    if (ban is not null) return new Judgment(Verdict.BanHit, ban);

    var candidate = LicenseCandidate(evidence);
    if (candidate is not null)
    {
        return IsAllowListed(candidate, policy.Allow)
            ? new Judgment(Verdict.Allow, $"SPDX {candidate}")
            : new Judgment(Verdict.NotAllowListed, $"SPDX「{candidate}」不在白名单（deny-by-default）");
    }

    var gap = FindGap(id, policy.Gaps);
    if (gap is not null)
    {
        return today > gap.ExpiresOn
            ? new Judgment(Verdict.GapExpired,
                $"观察态已于 {gap.ExpiresOn:yyyy-MM-dd} 到期未处置 —— {gap.Action}（owner {gap.Owner}）")
            : new Judgment(Verdict.GapOpen,
                $"登记观察态（owner {gap.Owner} · 到期 {gap.ExpiresOn:yyyy-MM-dd}）—— {gap.Reason}");
    }

    return new Judgment(Verdict.Unresolved, EvidenceLabel(evidence));
}

// 证据 → 可用于白名单比对的 SPDX 标识；推不出返回 null（fail-closed 的入口）。
// 只有 `https://licenses.nuget.org/<SPDX>` 形态的 licenseUrl 能离线推出标识，
// 其余 URL（含 https://aka.ms/deprecateLicenseUrl 占位、github 上的正文链接）
// 离线取不回正文 → 不猜。
static string? LicenseCandidate(LicenseEvidence evidence) => evidence.Kind switch
{
    LicenseKind.Expression => evidence.Value.Trim(),
    LicenseKind.Url when evidence.Value.StartsWith("https://licenses.nuget.org/", StringComparison.OrdinalIgnoreCase)
        => Uri.UnescapeDataString(evidence.Value["https://licenses.nuget.org/".Length..]).Trim(),
    _ => null,
};

// 证据标签（仅用于 FAIL 明细，让失败一眼可定位）
static string EvidenceLabel(LicenseEvidence evidence) => evidence.Kind switch
{
    LicenseKind.NoNuspec => "NuGet 缓存里没有该版本的 nuspec（未 restore，或版本号与声明对不上）",
    LicenseKind.None => "nuspec 既无 <license> 也无 licenseUrl / repository licenseUrl",
    LicenseKind.File => $"文件型许可「{evidence.Value}」——离线推不出 SPDX 标识（且未登记 gap）",
    LicenseKind.Url => $"licenseUrl「{evidence.Value}」非 licenses.nuget.org 形态，离线取不回正文（且未登记 gap）",
    _ => evidence.Value,
};

// nuspec → 许可证据（纯函数）。取证顺序见文件头；`<license>` 的 type 只认
// expression / file，未知 type 与缺失 type 一律不认（继续找下一级证据源，不猜）。
static LicenseEvidence ExtractLicense(string nuspecXml)
{
    XDocument document;
    try
    {
        document = XDocument.Parse(nuspecXml);
    }
    catch (System.Xml.XmlException)
    {
        return new LicenseEvidence(LicenseKind.None, "");   // 非良构 nuspec = 取不到
    }

    foreach (var element in document.Descendants())
    {
        if (element.Name.LocalName != "license") continue;
        var value = element.Value.Trim();
        var type = element.Attribute("type")?.Value.Trim() ?? "";
        if (value.Length == 0) continue;
        if (type.Equals("expression", StringComparison.OrdinalIgnoreCase))
            return new LicenseEvidence(LicenseKind.Expression, value);
        if (type.Equals("file", StringComparison.OrdinalIgnoreCase))
            return new LicenseEvidence(LicenseKind.File, value);
    }

    foreach (var element in document.Descendants())
        if (element.Name.LocalName == "repository"
            && element.Attribute("licenseUrl") is { } repositoryUrl
            && repositoryUrl.Value.Trim().Length > 0)
            return new LicenseEvidence(LicenseKind.Url, repositoryUrl.Value.Trim());

    foreach (var element in document.Descendants())
        if (element.Name.LocalName == "licenseUrl" && element.Value.Trim().Length > 0)
            return new LicenseEvidence(LicenseKind.Url, element.Value.Trim());

    return new LicenseEvidence(LicenseKind.None, "");
}

// ══════════════ 输入读取（带 I/O，非纯函数）══════════════

// 仓库根定位：CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底（file-based app 的
// BaseDirectory 实测指向 %TEMP%\dotnet\runfile\...，向上不可达仓库根）。
// 找不到返回 null——由调用方 fail-closed（退出码 1，见文件头三值语义）。
static string? FindRepoRootOrNull()
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
    return null;
}

// NuGet 全局包缓存根：优先标准环境变量 NUGET_PACKAGES，其次 HOME，再次 UserProfile
static string NuGetCacheRoot()
{
    var fromEnv = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
    var home = Environment.GetEnvironmentVariable("HOME");
    if (string.IsNullOrWhiteSpace(home))
        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".nuget", "packages");
}

// 包目录索引（一次枚举，避免每个包各扫一遍缓存）。
// 目录名按 NuGet 约定是小写，这里用 OrdinalIgnoreCase 建表按 id 查——不做大小写
// 归一化（CA1308：ToLower 对部分字符会丢失信息，且 NuGet 布局不该由本脚本再编码一次）。
static Dictionary<string, string> IndexPackageDirs(string cacheRoot)
{
    var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!Directory.Exists(cacheRoot)) return index;
    foreach (var dir in Directory.EnumerateDirectories(cacheRoot))
        index[Path.GetFileName(dir)] = dir;
    return index;
}

// 读 nuspec → 许可证据。任何缺失（缓存无此包 / 无此版本 / 无 nuspec / 读不出来）
// 都映射为 NoNuspec，由 Judge 归入 fail-closed。
static LicenseEvidence ReadEvidence(IReadOnlyDictionary<string, string> packageDirs, string id, string version)
{
    if (!packageDirs.TryGetValue(id, out var packageDir))
        return new LicenseEvidence(LicenseKind.NoNuspec, "");

    var versionDir = Path.Combine(packageDir, version);
    if (!Directory.Exists(versionDir))
        return new LicenseEvidence(LicenseKind.NoNuspec, "");

    string? nuspec = null;
    foreach (var file in Directory.GetFiles(versionDir, "*.nuspec"))
        if (string.Equals(Path.GetFileNameWithoutExtension(file), id, StringComparison.OrdinalIgnoreCase))
        {
            nuspec = file;
            break;
        }
    if (nuspec is null) return new LicenseEvidence(LicenseKind.NoNuspec, "");

    string xml;
    try
    {
        xml = File.ReadAllText(nuspec, new UTF8Encoding(false, false));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return new LicenseEvidence(LicenseKind.NoNuspec, "");
    }
    return ExtractLicense(xml);
}

// 直接依赖声明：Directory.Packages.props 的 PackageVersion + 各 csproj 的 PackageReference
static (List<Declared> Packages, string? Error) ReadDeclared(string root)
{
    var packages = new List<Declared>();

    var propsPath = Path.Combine(root, "Directory.Packages.props");
    if (File.Exists(propsPath))
    {
        var loaded = LoadXml(propsPath);
        if (loaded.Error is not null) return ([], loaded.Error);
        foreach (var element in loaded.Document!.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
        {
            var id = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            packages.Add(new Declared(id.Trim(), element.Attribute("Version")?.Value.Trim(), "props"));
        }
    }

    foreach (var project in EnumerateProjectFiles(root))
    {
        var loaded = LoadXml(project);
        if (loaded.Error is not null) return ([], loaded.Error);
        foreach (var element in loaded.Document!.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            packages.Add(new Declared(id.Trim(), element.Attribute("Version")?.Value.Trim(), "csproj"));
        }
    }

    return (packages, null);
}

// 读 XML；非良构 / 读不出来 → Error（调用方 fail-closed）
static (XDocument? Document, string? Error) LoadXml(string path)
{
    try
    {
        return (XDocument.Load(path), null);
    }
    catch (System.Xml.XmlException ex)
    {
        return (null, $"XML 非良构：{path}（{ex.Message}）");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return (null, $"读取失败：{path}（{ex.Message}）");
    }
}

// 递归枚举 *.csproj（跳过生成/临时目录）
static IEnumerable<string> EnumerateProjectFiles(string root)
{
    var skip = new HashSet<string>(StringComparer.Ordinal)
        { "obj", "bin", ".git", "TestResults", "node_modules", ".vs", ".serena", ".probe-dump", "nupkgs" };
    var stack = new Stack<string>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var dir = stack.Pop();
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (!skip.Contains(Path.GetFileName(sub))) stack.Push(sub);
        foreach (var file in Directory.EnumerateFiles(dir, "*.csproj"))
            yield return file;
    }
}

// 依赖图：dotnet list … --include-transitive --format json → 落临时文件再解析
static (List<(string Id, string Version)> Pairs, string? Error) ReadGraph(string root)
{
    var slnx = Path.Combine(root, "PalDDD.slnx");
    var tempFile = Path.Combine(Path.GetTempPath(), "license-policy-graph-" + Guid.NewGuid().ToString("N") + ".json");
    var stdout = string.Empty;
    var exitCode = -1;
    var json = string.Empty;
    try
    {
        var psi = new ProcessStartInfo("dotnet", $"list \"{slnx}\" package --include-transitive --format json")
        {
            RedirectStandardOutput = true,          // 大 JSON 只落临时文件，不进控制台
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ([], $"dotnet 启动失败（{ex.Message}）——依赖图未取得");
        }
        if (started is null) return ([], "dotnet 启动失败（返回 null）——依赖图未取得");

        using (started)
        {
            // stderr 不重定向：重定向却不排空时子进程写满管道会与 stdout ReadToEnd 互等
            //（经典死锁）——沿 secret-scan.cs 的 ITM 取舍，诊断信息直出更可观察
            stdout = started.StandardOutput.ReadToEnd();
            started.WaitForExit();
            exitCode = started.ExitCode;
        }

        File.WriteAllText(tempFile, stdout, new UTF8Encoding(false, false));
        json = File.ReadAllText(tempFile, Encoding.UTF8);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return ([], $"依赖图临时文件读写失败（{ex.Message}）");
    }
    finally
    {
        try { if (File.Exists(tempFile)) File.Delete(tempFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* 清理失败不掩盖判定结论 */ }
    }

    if (exitCode != 0)
        return ([], $"`dotnet list package --include-transitive` 退出码 {exitCode}——依赖图未取得");

    if (json.Length == 0)
        return ([], "dotnet list 输出为空——依赖图未取得");

    return ParseGraph(json);
}

// 解析 dotnet list JSON → (id, resolvedVersion) 去重集合。
// projects 缺失/非数组、或整体不是 JSON → Error（调用方 fail-closed，同 vuln-scan 口径）。
static (List<(string Id, string Version)> Pairs, string? Error) ParseGraph(string json)
{
    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(json);
    }
    catch (JsonException)
    {
        var head = json.Length <= 120 ? json : json[..120];
        return ([], $"dotnet list 输出不是合法 JSON——依赖图未取得（开头：{head.Replace('\n', ' ')}）");
    }

    using (document)
    {
        var rootElement = document.RootElement;
        if (rootElement.ValueKind != JsonValueKind.Object
            || !rootElement.TryGetProperty("projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array)
        {
            return ([], "dotnet list 输出缺少 projects 数组——依赖图未取得");
        }

        var pairs = new HashSet<(string Id, string Version)>();
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object
                || !project.TryGetProperty("frameworks", out var frameworks)
                || frameworks.ValueKind != JsonValueKind.Array) continue;
            foreach (var framework in frameworks.EnumerateArray())
            {
                if (framework.ValueKind != JsonValueKind.Object) continue;
                foreach (var section in (string[])["topLevelPackages", "transitivePackages"])
                    foreach (var package in ArrayOrEmpty(framework, section))
                    {
                        if (!package.TryGetProperty("id", out var idProperty)
                            || idProperty.ValueKind != JsonValueKind.String) continue;
                        if (!package.TryGetProperty("resolvedVersion", out var versionProperty)
                            || versionProperty.ValueKind != JsonValueKind.String) continue;
                        var id = idProperty.GetString();
                        var version = versionProperty.GetString();
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version)) continue;
                        pairs.Add((id, version));
                    }
            }
        }
        return (pairs.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
                     .ToList(), null);
    }
}

// JSON 对象上取指定名字的数组（缺失/非数组 → 空）
static IEnumerable<JsonElement> ArrayOrEmpty(JsonElement parent, string name) =>
    parent.ValueKind == JsonValueKind.Object
    && parent.TryGetProperty(name, out var value)
    && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray()
        : [];

// ══════════════ 自测（全部构造合成输入，不读真实缓存、不跑子进程）══════════════

static int SelfTest(Policy policy, DateOnly today)
{
    var passed = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    var allow = policy.Allow;
    var bans = policy.Bans;

    // ── [1] 白名单：裁决给定清单逐项正例 ──
    Case("白名单整表逐项命中（deny-by-default 的正例面）", allow.All(a => IsAllowListed(a, allow)));
    Case("白名单正例 MIT", IsAllowListed("MIT", allow));
    Case("白名单正例 AGPL-3.0-only（PalORM 五包实际许可）", IsAllowListed("AGPL-3.0-only", allow));
    Case("白名单正例 PostgreSQL（Npgsql 实际许可 · OSI 2010-02-10 批准）", IsAllowListed("PostgreSQL", allow));
    Case("白名单大小写不敏感", IsAllowListed("mit", allow));
    Case("带 WITH 例外的整串命中", IsAllowListed("Apache-2.0 WITH LLVM-exception", allow));
    Case("OR 复合：两侧都在表内 → 放行", IsAllowListed("Apache-2.0 OR MPL-2.0", allow));
    Case("AND 复合：两侧都在表内 → 放行", IsAllowListed("MIT AND ISC", allow));
    Case("嵌套复合 OR/AND → 逐操作数判", IsAllowListed("MIT OR ISC AND Apache-2.0", allow));

    // ── [1] 负例：商业/非 OSI 许可必须红（防「判定恒真」）──
    Case("负例 SSPL-1.0（非 OSI）→ 拒绝", !IsAllowListed("SSPL-1.0", allow));
    Case("负例 专有 LicenseRef → 拒绝", !IsAllowListed("LicenseRef-Proprietary", allow));
    Case("负例 商业双轨许可 → 拒绝", !IsAllowListed("Commercial-Dual-Use", allow));
    Case("负例 OR 复合里混入非 OSI → 整体拒绝", !IsAllowListed("Apache-2.0 OR SSPL-1.0", allow));
    Case("负例 空字符串 → 拒绝", !IsAllowListed("", allow));
    Case("负例 纯空白 → 拒绝", !IsAllowListed("   ", allow));
    Case("负例 WITH 例外不在表内 → 拒绝", !IsAllowListed("GPL-3.0-only WITH Qt-LGPL-exception", allow));
    Case("负例 复合尾部空操作数 → 拒绝", !IsAllowListed("MIT OR ", allow));

    // ── [2] 禁令：包 + 版本下限 ──
    Case("禁令命中 Verify.TUnit 33.0.1", BanReason("Verify.TUnit", "33.0.1", bans) is not null);
    Case("禁令命中 Verify 33.0.0-preview.1（预发布也算）", BanReason("Verify", "33.0.0-preview.1", bans) is not null);
    Case("禁令负例 Verify.TUnit 32.0.1 → 不命中（当前钉住的版本）", BanReason("Verify.TUnit", "32.0.1", bans) is null);
    Case("禁令负例 无关包 Dapper → 不命中", BanReason("Dapper", "2.1.89", bans) is null);
    Case("禁令 fail-closed：版本解析不出 → 命中", BanReason("Verify", "abc", bans) is not null);
    Case("禁令大小写不敏感", BanReason("verify.tunit", "33.0.1", bans) is not null);
    Case("禁令边界：主版本 33 命中、32 不命中",
        BanReason("Verify", "33", bans) is not null && BanReason("Verify", "32.9.9", bans) is null);

    // ── Judge：核心纯函数的正负例 ──
    Case("Judge 白名单命中 → Allow",
        Judge("Dapper", "2.1.89", new LicenseEvidence(LicenseKind.Expression, "MIT"), policy, today).Result is Verdict.Allow);
    Case("Judge 注入商业许可包 → 必红（非白名单）",
        Judge("EvilCorp", "1.0.0", new LicenseEvidence(LicenseKind.Expression, "LicenseRef-Commercial"), policy, today).Result is Verdict.NotAllowListed);
    Case("Judge 禁令命中优先于白名单（MIT 也拦）",
        Judge("Verify.TUnit", "33.0.1", new LicenseEvidence(LicenseKind.Expression, "MIT"), policy, today).Result is Verdict.BanHit);
    Case("Judge 文件型许可未登记 → 无法判定",
        Judge("X", "1.0.0", new LicenseEvidence(LicenseKind.File, "LICENSE.txt"), policy, today).Result is Verdict.Unresolved);
    Case("Judge nuspec 缺失未登记 → 无法判定",
        Judge("Y", "1.0.0", new LicenseEvidence(LicenseKind.NoNuspec, ""), policy, today).Result is Verdict.Unresolved);
    Case("Judge 存量台账项（CommandLineParser）→ 观察态放行",
        Judge("CommandLineParser", "2.9.1", new LicenseEvidence(LicenseKind.File, "License.md"), policy, today).Result is Verdict.GapOpen);

    // ── [3] gap 台账：观察态放行 / 到期即红 / 台账不得绕过白名单 ──
    var openPolicy = new Policy(allow, bans, [new GapEntry("Gappy", "文件型许可", "框架维护者", new DateOnly(2026, 12, 31), "补证后销号")]);
    var expiredPolicy = new Policy(allow, bans, [new GapEntry("Stale", "文件型许可", "框架维护者", new DateOnly(2026, 1, 1), "补证后销号")]);
    var bypassPolicy = new Policy(allow, bans, [new GapEntry("EvilCorp", "误登记", "框架维护者", new DateOnly(2026, 12, 31), "x")]);

    Case("gap 登记且未到期 → 观察态放行",
        Judge("Gappy", "1.0.0", new LicenseEvidence(LicenseKind.File, "LICENSE"), openPolicy, today).Result is Verdict.GapOpen);
    Case("gap 登记但已到期 → FAIL",
        Judge("Stale", "1.0.0", new LicenseEvidence(LicenseKind.File, "LICENSE"), expiredPolicy, today).Result is Verdict.GapExpired);
    Case("gap 边界：到期日当天仍放行，次日起才红",
        Judge("Stale", "1.0.0", new LicenseEvidence(LicenseKind.File, "LICENSE"), expiredPolicy, new DateOnly(2026, 1, 1)).Result is Verdict.GapOpen
        && Judge("Stale", "1.0.0", new LicenseEvidence(LicenseKind.File, "LICENSE"), expiredPolicy, new DateOnly(2026, 1, 2)).Result is Verdict.GapExpired);
    Case("台账不是白名单：证据可判且非白名单时照常 FAIL",
        Judge("EvilCorp", "1.0.0", new LicenseEvidence(LicenseKind.Expression, "SSPL-1.0"), bypassPolicy, today).Result is Verdict.NotAllowListed);

    // ── 证据取证：nuspec 四级来源与优先级 ──
    const string NuspecExpression =
        "<package><metadata><license type=\"expression\">MIT</license><licenseUrl>https://licenses.nuget.org/MIT</licenseUrl></metadata></package>";
    Case("取证 1：license expression 优先于 licenseUrl",
        ExtractLicense(NuspecExpression) is { Kind: LicenseKind.Expression, Value: "MIT" });
    Case("取证 2：文件型许可识别为 File",
        ExtractLicense("<package><metadata><license type=\"file\">LICENSE.txt</license></metadata></package>")
            is { Kind: LicenseKind.File, Value: "LICENSE.txt" });
    Case("取证 3：repository licenseUrl 兜底",
        ExtractLicense("<package><metadata><repository url=\"x\" licenseUrl=\"https://licenses.nuget.org/ISC\"/></metadata></package>")
            is { Kind: LicenseKind.Url });
    Case("取证 4：licenseUrl 最后兜底",
        ExtractLicense("<package><metadata><licenseUrl>https://licenses.nuget.org/ISC</licenseUrl></metadata></package>")
            is { Kind: LicenseKind.Url });
    Case("取证 5：两者皆无 → None",
        ExtractLicense("<package><metadata><id>A</id></metadata></package>") is { Kind: LicenseKind.None });
    Case("取证 6：license 缺 type 属性 → 不当 expression（不猜）",
        ExtractLicense("<package><metadata><license>MIT</license></metadata></package>") is not { Kind: LicenseKind.Expression });
    Case("取证 7：非良构 XML → None（fail-closed）",
        ExtractLicense("<package><metadata>") is { Kind: LicenseKind.None });

    // ── licenseUrl → SPDX 标识的离线推导 ──
    Case("URL 推导：licenses.nuget.org/MIT → MIT",
        LicenseCandidate(new LicenseEvidence(LicenseKind.Url, "https://licenses.nuget.org/MIT")) == "MIT");
    Case("URL 推导：百分号编码的复合表达式还原",
        LicenseCandidate(new LicenseEvidence(LicenseKind.Url, "https://licenses.nuget.org/Apache-2.0%20OR%20MPL-2.0")) == "Apache-2.0 OR MPL-2.0");
    Case("URL 推导：aka.ms 占位 URL → null（fail-closed，不猜）",
        LicenseCandidate(new LicenseEvidence(LicenseKind.Url, "https://aka.ms/deprecateLicenseUrl")) is null);
    Case("URL 推导：github 正文链接 → null（离线取不回）",
        LicenseCandidate(new LicenseEvidence(LicenseKind.Url, "https://github.com/dotnet/standard/blob/master/LICENSE.TXT")) is null);
    Case("文件型许可 → null（fail-closed 的唯一入口）",
        LicenseCandidate(new LicenseEvidence(LicenseKind.File, "LICENSE.txt")) is null);

    // ── 主版本解析（禁令的比对基础）──
    Case("MajorOf 33.0.1 → 33", MajorOf("33.0.1") == 33);
    Case("MajorOf 32.0.1 → 32", MajorOf("32.0.1") == 32);
    Case("MajorOf 33（无点号）→ 33", MajorOf("33") == 33);
    Case("MajorOf 预发布 33.0.0-preview.1 → 33", MajorOf("33.0.0-preview.1") == 33);
    Case("MajorOf abc → -1", MajorOf("abc") == -1);
    Case("MajorOf 空 → -1", MajorOf("") == -1);

    // ── 输入形状（依赖图 JSON 的 fail-closed）──
    Case("ParseGraph 合法输出 → 两个包对",
        ParseGraph("""{"version":1,"projects":[{"frameworks":[{"framework":"net11.0","topLevelPackages":[{"id":"A","requestedVersion":"1.0.0","resolvedVersion":"1.0.0"}],"transitivePackages":[{"id":"B","resolvedVersion":"2.0.0"}]}]}]}""").Pairs.Count == 2);
    Case("ParseGraph 缺 projects → Error（fail-closed）",
        ParseGraph("""{"version":1}""").Error is not null);
    Case("ParseGraph 非 JSON → Error（fail-closed）",
        ParseGraph("not json").Error is not null);
    Case("ParseGraph projects 为空数组 → 合法但零对",
        ParseGraph("""{"projects":[]}""").Pairs.Count == 0);

    // ── 策略数据自身的完整性（台账/禁令/白名单被清空后空转即红）──
    Case("台账存在性断言（登记表被清空即红）", policy.Gaps.Length > 0);
    Case("禁令清单存在性断言", policy.Bans.Length > 0);
    Case("白名单存在性断言", policy.Allow.Count > 0);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 2;   // 2 = 自测失败（见文件头三值语义）
}

// ══════════════ 类型 ═══════════════

internal sealed record Policy(HashSet<string> Allow, BanEntry[] Bans, GapEntry[] Gaps);

internal sealed record BanEntry(string Id, int MinMajor, string Reason, DateOnly DecidedOn);

internal sealed record GapEntry(string Id, string Reason, string Owner, DateOnly ExpiresOn, string Action);

internal sealed record Declared(string Id, string? Version, string Source);

internal sealed record LicenseEvidence(LicenseKind Kind, string Value);

internal enum LicenseKind { NoNuspec, None, Expression, File, Url }

internal enum Verdict { Allow, GapOpen, BanHit, NotAllowListed, GapExpired, Unresolved }

internal sealed record Judgment(Verdict Result, string Detail);

internal sealed record Finding(string Id, string Version, Judgment Outcome);
