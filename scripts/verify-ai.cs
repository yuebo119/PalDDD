// ============================================================================
// verify-ai.cs——.ai 系统一致性校验（MIG-012-A2，2026-09-11）
// 由 .ai/scripts/verify-ai-system.sh（447 行）等价迁移为 C#（dotnet file-based app）。
//
// 用法：dotnet run scripts/verify-ai.cs
// 退出码：0=23 项全过；1=任一失败；2=仓库根定位失败。
//
// 23 项检查与原 bash 逐项对应：
//   V1 引擎/双 profile/工具提示词齐全          V13 lessons 含 SPD 系列 + 误判防治
//   V2 提示词引用的文档与脚本存在              V14 覆盖率基线（行覆盖率数值 + 门禁阈值）
//   V3 conventions.md ≥10 个二级章节           V15 lessons 审计章锚点（行锚定）
//   V4 pitfalls.md ≥50 行                      V16 全部 .sh bash -n 语法（Process 调 bash）
//   V5 门禁编号集合一致 + README 速览区断言    V17 账本轮次结构（日期单调 + 列数）
//   V6 公共 API 快照防线（≥50 行）             V18 修复门两问协议在档
//   V7 误判知识库双源口径（完整版/速版）       V19 传感器台账 90 天定标周期（DateTime）
//   V8 视角发现率账本六流以上                  V20 任务进件模板（验收断言 + 拒绝路径）
//   V9 README 文件地图与实际一致               V21 P3 账本 30 天老化（DateTime）
//   V10 机械防线测试文件齐全                   V22 lessons 经验编号跨章唯一
//   V11 metrics 账本结构完整                   V23 镜像脚本对归一化比对
//   V12 编译期诊断全表面 ≥38 条
//
// 迁移说明：
//   1) 输出格式与原 bash 逐行一致（双跑归一时间行后 diff 验证）；FAIL 详情中
//      V5 的 diff 摘要与 V23 的差异块用对称差近似（PASS 路径不触发，红测只验检出）。
//   2) V19/V21 日期差：原 awk int((systime()-mktime(当日))/86400)（UTC 口径）→
//      (DateTime.UtcNow.Date - d).Days，截断语义一致。
//   3) 仓库根定位：file-based app 的 AppContext.BaseDirectory 实测指向
//      %TEMP%\dotnet\runfile\...（临时编译目录，MIG-012 探针实证），向上找不到
//      PalDDD.slnx——故以 CWD 向上找为主、BaseDirectory 兜底，替代原
//      bash 的 cd "$(dirname "$0")/../.."。
//   4) 零 package 依赖（NU1510 即错误）；顶层语句 + static 局部函数。
//   5) ITM-664 已清偿（v87）：--selftest 红绿矩阵 14 例（V17 单调/乱序/同日 ·
//      V19 定标/超期/UNKNOWN/非法日期 · V21 老化/豁免 · V22 唯一/跨章重复/ITM 豁免 ·
//      V2 缺失/空绿）——S3 红测：破坏 V19 超期判定 → 自测必红。改 V 项逻辑时须同步改自测。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的
// 固定协议行（PASS V* / FAIL V* 被 grep 消费），固定中文非用户可配文案——沿
// vuln-scan.cs / osc-check.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文输出会乱码——对齐 bash printf UTF-8
Console.OutputEncoding = Encoding.UTF8;

// ITM-664（2026-09-13 清偿）：--selftest 自测入口——红绿矩阵验证核心校验逻辑能失败
//（PD29 验证验证者：没看过仪器故意产生错误答案，就不信它输出的任何数字）。
// 矩阵：V17 日期单调（绿/乱序红/行数不足红）· V19 定标 90 天（绿/超期红/UNKNOWN 不误判/
// 非法日期红）· V21 老化 30 天（绿/超期红/含"过期"豁免绿）· V22 编号唯一（绿/跨章重复红/
// ITM- 追溯索引豁免绿）· V2 缺失清单（红/空绿）。自测模式不触达真实仓库文件。
if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return RunSelftest();
}

// ─── 仓库根定位（见头注释迁移说明 3）───
var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var lines = new List<string>
{
    "═══════ .ai 系统一致性校验（Pal.DDD · V1-V25）═══════",
    $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
    "",
};
var passed = 0;
var failed = 0;

// ─── V1 引擎 + 双 profile + 工具提示词与模板齐全 ───
{
    var missing = MissingList([
        ".ai/review/engine.md", ".ai/review/metrics.md", ".ai/review/prompt.md",
        ".ai/review/known-false-positives.md", ".ai/gate/prompt.md",
        ".ai/refine/prompt.md", ".ai/test/prompt.md",
    ]);
    if (missing.Length == 0) { passed++; lines.Add("PASS V1: 引擎/双profile/工具提示词齐全"); }
    else { failed++; lines.Add($"FAIL V1: 提示词缺失（{missing}）"); }
}

// ─── V2 提示词引用的权威文档与脚本存在 ───
// MIG-012-D 收口（2026-09-11）：.sh 全删后名单改为 .cs file-based app + 永久保留的两 .sh。
{
    var missing = MissingList([
        "docs/conventions.md", "docs/architecture.md", "docs/pitfalls.md", "docs/testing.md",
        "scripts/gate.cs", "scripts/tech-debt.cs", "scripts/test-gate.cs",
        "scripts/doc-consistency.cs", "scripts/verify-ai.cs", "scripts/encoding-gate.cs",
        "scripts/verify-conventions.cs", "scripts/secret-scan.cs", "scripts/gate-lite.cs",
        "scripts/sibling-map.cs", "scripts/osc-check.cs", "scripts/flaky-parse.cs",
        "scripts/fix-orchestrator.cs", "scripts/fix-completeness.cs", "scripts/sister-axis.cs",
        "scripts/review-scope.cs", "scripts/review-gate.cs", "scripts/probe-template.cs",
        "scripts/verify-action-items.cs", "scripts/review-snapshot.cs", "scripts/refine-scan.cs",
        "scripts/changelog-check.cs", "scripts/changelog-facts.cs", "scripts/check-all.cs",
        "scripts/ci-coverage.cs", "scripts/ci-failed-tests.cs", "scripts/vuln-scan.cs",
        ".ai/scripts/install-ai-system.sh", ".ai/scripts/template-gate.sh",
    ]);
    if (missing.Length == 0) { passed++; lines.Add("PASS V2: 提示词引用的文档与脚本全部存在"); }
    else { failed++; lines.Add($"FAIL V2: 被引用文件缺失（{missing}）"); }
}

// ─── V3 conventions.md 章节数（≥10 个二级章节）───
{
    var convSections = CountLinesMatching(TryReadLines("docs/conventions.md"), "^## ");
    if (convSections >= 10) { passed++; lines.Add($"PASS V3: conventions.md 结构完整（{convSections} 个二级章节）"); }
    else { failed++; lines.Add($"FAIL V3: conventions.md 章节数不足（仅 {convSections} 个 ## 章节（预期 ≥10））"); }
}

// ─── V4 pitfalls.md 行数（≥50）───
{
    var pitfallOk = File.Exists("docs/pitfalls.md");
    var pitfallLines = pitfallOk ? CountNewlines(File.ReadAllBytes("docs/pitfalls.md")) : 0;
    if (pitfallOk && pitfallLines >= 50) { passed++; lines.Add($"PASS V4: pitfalls.md 完整（{pitfallLines} 行）"); }
    else { failed++; lines.Add($"FAIL V4: pitfalls.md 异常（行数 {pitfallLines}（预期 ≥50））"); }
}

// ─── V5 门禁编号：脚本↔G 表集合比对 + README 速览区保留集断言 ───
// MIG-012-D：gate-check.sh 已删（gate.cs 不含 PDDD-G 编号字符串）——改为以
// gate/prompt.md 的 G 表为真源，对照固定保留集 {G22,G23,G24}（编号集合恒定）。
{
    var gPromptIds = SortedMatches(TryReadText(".ai/gate/prompt.md"), "PDDD-G[0-9]+");
    var expectedIds = new[] { "PDDD-G22", "PDDD-G23", "PDDD-G24" };
    var gCount = gPromptIds.Count;
    // sed 区间 ^## 快速概览 .. ^---（含端点，可重复触发）内的违规编号（<22 → G1-G21 残留）
    var readmeBad = string.Concat(SortedMatches(
            string.Join("\n", SedRange(TryReadLines(".ai/README.md"), "^## 快速概览", "^---")),
            "PDDD-G[0-9]+")
        .Where(id => int.Parse(id["PDDD-G".Length..], System.Globalization.CultureInfo.InvariantCulture) < 22)
        .Select(id => $"G{id["PDDD-G".Length..]} "));
    if (gPromptIds.ToHashSet().SetEquals(expectedIds) && readmeBad.Length == 0)
    {
        passed++; lines.Add($"PASS V5: 门禁编号集合一致（G 表 {gCount} 项=G22/G23/G24 保留集；README 速览区无 G1-G21 残留）");
    }
    else
    {
        var gDiff = string.Join(" ", expectedIds.Except(gPromptIds).Select(x => "< " + x)
            .Concat(gPromptIds.Except(expectedIds).Select(x => "> " + x)));
        failed++; lines.Add($"FAIL V5: 门禁编号不同步/README 旧口径残留（G 表 vs 保留集差异：{gDiff}；README 速览区违规编号：{(readmeBad.Length > 0 ? readmeBad : "无")}）");
    }
}

// ─── V6 公共 API 快照防线：测试存在 + 快照基线非空 ───
{
    var apiOk = File.Exists("test/PalDDD.Core.Tests/PublicApiSnapshotTests.cs");
    var snapFile = Directory.Exists("test/PalDDD.Core.Tests/Snapshots")
        ? Directory.EnumerateFiles("test/PalDDD.Core.Tests/Snapshots", "*.txt").FirstOrDefault() : null;
    var snapLines = snapFile is not null ? CountNewlines(File.ReadAllBytes(snapFile)) : 0;
    if (apiOk && snapLines >= 50)
    {
        passed++; lines.Add($"PASS V6: 公共 API 快照防线完整（{ToPosix(snapFile!)}：{snapLines} 行）");
    }
    else
    {
        failed++; lines.Add($"FAIL V6: API 快照防线异常（snap_file={(snapFile is null ? "" : ToPosix(snapFile))} 行数 {snapLines}（预期 ≥50））");
    }
}

// ─── V7 误判知识库双源口径（完整版条数 + 速版覆盖到完整版最大 PD 号 + F-05 prose 等值）───
{
    var kb = TryReadLines(".ai/review/known-false-positives.md");
    var fullNums = kb.Select(l => Regex.Match(l, "^### 模式 PD([0-9]+)：") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0)
        .Where(n => n > 0).ToList();
    var kbFull = fullNums.Count;
    var kbMax = fullNums.Count > 0 ? fullNums.Max() : 0;
    // awk 区间 ^### Pal.DDD 专项模式（PD1-PD .. ^## （含端点）内速版 ^PDn. 行的最大号
    var quickNums = SedRange(kb, @"^### Pal\.DDD 专项模式（PD1-PD", "^## ")
        .Select(l => Regex.Match(l, "^PD([0-9]+)\\."))
        .Where(m => m.Success)
        .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
        .ToList();
    var kbQuickMax = quickNums.Count > 0 ? quickNums.Max() : 0;
    // bash 语义：kb_max 无匹配默认 99（恒假）、kb_quick_max 无匹配默认 0
    var kbMaxBash = fullNums.Count > 0 ? kbMax : 99;

    // F-05（2026-09-21，审计 H2）：prose 计数等值校验。
    // 原判据 `kbFull >= 37` 是**下限**——PD 涨到 39 时全部 prose 计数（KFP 头/README/
    // engine/charter/v51/metrics/sensor-ledger 共 11 处）静默过期而门禁全绿，实测出现
    // 33/37/39/45 四种口径并存。现从这些 prose 行提取"声称的 PD 最大号/模式总数"，
    // 与实测比对：不一致即 FAIL（修 prose 或修知识库，二选一，不留静默漂移）。
    // 声称形态：`至 PD{N}` / `PD1-PD{N}` / `共 {M} 模式` / `{M} 模式含速版`。
    // 累计校验过的 prose 声明数（PASS 行的可观测口径）。
    var proseChecked = 0;
    var kbRepoRoot = root;
    foreach (var (file, claims) in new (string File, List<(string Kind, int Value)>)[]
    {
        (".ai/README.md", ExtractProsePdClaimsFromFile(Path.Combine(kbRepoRoot, ".ai", "README.md"))),
        (".ai/review/engine.md", ExtractProsePdClaimsFromFile(Path.Combine(kbRepoRoot, ".ai", "review", "engine.md"))),
        (".ai/review/review-charter-v2.md", ExtractProsePdClaimsFromFile(Path.Combine(kbRepoRoot, ".ai", "review", "review-charter-v2.md"))),
        (".ai/review/lessons-learned-v51.md", ExtractProsePdClaimsFromFile(Path.Combine(kbRepoRoot, ".ai", "review", "lessons-learned-v51.md"))),
    })
    {
        foreach (var (kind, value) in claims)
        {
            var actual = kind == "maxPd" ? kbMax : 8 + kbMax;
            if (value != actual)
            {
                failed++; lines.Add($"FAIL V7: {file} 的 prose 计数声称 {kind}={value}，实测 {kind}={actual}（PD 最大号 {kbMax}，模式总数 {8 + kbMax}）");
                goto v7Done;
            }
            proseChecked++;
        }
    }

    if (kbFull >= 8 && kbQuickMax >= kbMaxBash)
    {
        passed++; lines.Add($"PASS V7: 误判知识库完整（完整版 {kbFull} 条 · 速版覆盖至 PD{kbQuickMax}=最大号 · prose 计数 {proseChecked} 处与实测一致）");
    }
    else
    {
        failed++; lines.Add($"FAIL V7: 误判知识库不完整（完整版 {kbFull} 条 · 速版至 PD{kbQuickMax}(需≥PD{(fullNums.Count > 0 ? kbMax.ToString() : "?")})——防速版断链）");
    }
    v7Done: ;
}

// ─── V8 视角发现率账本存在且六流以上有记录 ───
{
    var c = CountLinesMatching(TryReadLines(".ai/review/perspective-stats.md"),
        @"^\| (架构|安全|资源|并发|错误|AOT|生成语义) ?流");
    if (File.Exists(".ai/review/perspective-stats.md") && c >= 6)
    {
        passed++; lines.Add("PASS V8: 视角发现率账本存在且六流以上有记录（三十七轮勘正）");
    }
    else
    {
        failed++; lines.Add("FAIL V8: 视角发现率账本缺失或七流记录不全（.ai/review/perspective-stats.md）");
    }
}

// ─── V9 README 文件地图与实际目录一致 ───
{
    var refs = SortedMatches(TryReadText(".ai/README.md"), "(gate|refine|review|test)/[a-z0-9/-]*\\.md");
    var missing = string.Join("", refs.Where(f => !File.Exists(Path.Combine(".ai", f)))
        .Select(f => $" .ai/{f}"));
    if (missing.Length == 0) { passed++; lines.Add("PASS V9: README 文件地图与实际一致"); }
    else { failed++; lines.Add($"FAIL V9: README 文件地图指向不存在的文件（{missing}）"); }
}

// ─── V10 机械防线存在（架构边界/契约/不变量/分配/诊断覆盖五测试）───
{
    var defenseOk = File.Exists("test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs")
        && File.Exists("test/PalDDD.Core.Tests/AotContractTests.cs")
        && File.Exists("test/PalDDD.Core.Tests/AggregateRootInvariantTests.cs")
        && File.Exists("test/PalDDD.Core.Tests/AllocationContractTests.cs")
        && File.Exists("test/PalDDD.Core.Tests/DiagnosticCoverageGateTests.cs");
    if (defenseOk) { passed++; lines.Add("PASS V10: 机械防线齐全（ArchitectureBoundaryTests + AotContract + Invariant + Allocation + DiagnosticCoverageGate）"); }
    else { failed++; lines.Add("FAIL V10: 机械防线缺失（需含 ArchitectureBoundaryTests/AotContractTests/AggregateRootInvariantTests/AllocationContractTests/DiagnosticCoverageGateTests）"); }
}

// ─── V11 metrics 账本存在且结构完整 ───
{
    var text = TryReadText(".ai/review/metrics.md");
    if (File.Exists(".ai/review/metrics.md") && text.Contains("## 缺陷逃逸账本") && text.Contains("## 轮次记录"))
    {
        passed++; lines.Add("PASS V11: metrics 账本存在且结构完整");
    }
    else
    {
        failed++; lines.Add("FAIL V11: metrics 账本缺失或结构不完整（.ai/review/metrics.md 需含「轮次记录」与「缺陷逃逸账本」）");
    }
}

// ─── V12 编译期诊断全表面：4 个定义源唯一 ID 计数（分析器 PDDD + 3 生成器）───
{
    var ids = new HashSet<string>();
    foreach (var dir in (string[])["src/PalDDD.Analyzers", "src/PalDDD.Core.SourceGen"])
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs"))
                foreach (Match m in Regex.Matches(TryReadText(f), "\"(PDDD|PALMSG|PALENUM|PALID)[0-9]{3}\""))
                    ids.Add(m.Value);
    if (ids.Count >= 38) { passed++; lines.Add($"PASS V12: 编译期诊断全表面 {ids.Count} 条（≥38：分析器 15 + 生成器 23）"); }
    else { failed++; lines.Add($"FAIL V12: 编译期诊断数不足（四定义源唯一 ID 实测 {ids.Count} < 38（口径为 38））"); }
}

// ─── V13 lessons.md 含 AI 协作教训（SPD 系列 + 误判防治）───
{
    var text = TryReadText(".ai/lessons.md");
    if (text.Contains("SPD-") && text.Contains("误判"))
    {
        passed++; lines.Add("PASS V13: lessons.md 含 AI 协作教训（SPD 系列 + 误判防治）");
    }
    else
    {
        failed++; lines.Add("FAIL V13: lessons.md 缺 AI 协作教训（需含 SPD-NNN 规则与误判防治内容）");
    }
}

// ─── V14 覆盖率基线文档存在（行覆盖率数值 + 门禁阈值）───
// F-06（2026-09-21，审计 H3）：原实现 `Regex.Match(text, "[0-9]+\.[0-9]+%")` 抓文档中
// **第一个百分比**——那是 2026-07-30 旧基线 67.9%，门禁输出自己显示旧数；而现行门禁
// 阈值是 0.70（docs/test-coverage-baseline.md:29-30）。现改为提取"门禁阈值"行的数值
// 并与 scripts/ci-coverage.cs 的默认阈值比对（双源一致），同时仍校验行覆盖率数值存在。
{
    var text = TryReadText("docs/test-coverage-baseline.md");
    // 只取「## 门禁阈值」节到下一个二级标题之间的内容——避免误抓历史陈述
    // （如"旧阈值 0.65"在阈值校准依据表里，属被取代的旧值）。
    var gateSection = SedRange(text.Split('\n'), @"^##\s*门禁阈值", @"^##\s").ToList();
    var gateText = string.Join('\n', gateSection);
    // 门禁阈值口径：节内形如「阈值 0.70」/「取整 0.70」/「不低于 70%」/「threshold 0.70」。
    // 两种书写形态（小数 0.70 与百分比 70%）统一归一为小数后比对。
    var docThreshold = Regex.Match(gateText, @"(?:阈值|threshold|取整)\s*[：:]?\s*(0\.\d+)", RegexOptions.IgnoreCase);
    var docPercent = Regex.Match(gateText, @"不低于\s*(\d+)\s*%", RegexOptions.IgnoreCase);
    // ci-coverage.cs 的默认阈值（COVERAGE_THRESHOLD 环境变量缺省值）
    var gateThreshold = Regex.Match(
        TryReadText("scripts/ci-coverage.cs"),
        @"COVERAGE_THRESHOLD[^\n]*?(?:默认|default)\s*(0\.\d+)", RegexOptions.IgnoreCase);
    if (!gateThreshold.Success)
        gateThreshold = Regex.Match(TryReadText("scripts/ci-coverage.cs"), @"threshold\s*[=:]\s*(0\.\d+)", RegexOptions.IgnoreCase);

    var hasLineRate = Regex.IsMatch(text, "[0-9]+\\.[0-9]+%");
    // 归一化：百分比形态（如 "70"）转小数（0.70），小数形态原样
    static string? NormalizeThreshold(Match m)
    {
        if (!m.Success) return null;
        var raw = m.Groups[1].Value;
        if (raw.Contains('.')) return raw;
        return (int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) / 100.0)
            .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }
    var docVal = NormalizeThreshold(docThreshold) ?? NormalizeThreshold(docPercent);
    var gateVal = NormalizeThreshold(gateThreshold);
    var thresholdConsistent = docVal is not null && gateVal is not null && docVal == gateVal;

    if (File.Exists("docs/test-coverage-baseline.md")
        && hasLineRate
        && Regex.IsMatch(text, "Line coverage|行覆盖率")
        && thresholdConsistent)
    {
        passed++; lines.Add($"PASS V14: 覆盖率基线存在（含行覆盖率数值 · 门禁阈值 {docVal} 与 ci-coverage.cs 一致）");
    }
    else if (File.Exists("docs/test-coverage-baseline.md") && hasLineRate && !thresholdConsistent)
    {
        failed++; lines.Add(
            $"FAIL V14: 门禁阈值双源不一致（docs/test-coverage-baseline.md 阈值 {docVal ?? "未提取到"} vs scripts/ci-coverage.cs {gateVal ?? "未提取到"}）");
    }
    else
    {
        failed++; lines.Add("FAIL V14: 覆盖率基线缺失（docs/test-coverage-baseline.md 需含行覆盖率数值 + 门禁阈值）");
    }
}

// ─── V15 lessons.md 章覆盖完整性（XIII/XVII/XVIII 审计锚点，行锚定）───
{
    var ls = TryReadLines(".ai/lessons.md");
    var anchors = new[] { "COV-1", "PINV-1", "SEC-1", "AUD-1", "LAYER-1", "SYNC-1", "OPS-1" };
    var allPresent = true;
    foreach (var id in anchors)
        if (!ls.Any(l => l.StartsWith($"| {id} |", StringComparison.Ordinal))) { allPresent = false; break; }
    if (allPresent)
    {
        passed++; lines.Add("PASS V15: lessons.md 含测试审计(XIII) + 诊断审计(XVII AUD/LAYER) + 收口审计(XVIII SYNC/OPS)");
    }
    else
    {
        failed++; lines.Add("FAIL V15: lessons.md 缺审计教训章（需含 XIII（COV/PINV/SEC）与 XVII（AUD/LAYER）与 XVIII（SYNC/OPS）锚点（行锚定））");
    }
}

// ─── V16 全部 .ai 与根 scripts 脚本 bash -n 语法检查（Process 调 bash）───
{
    var syntaxBad = new StringBuilder();
    foreach (var s in GlobSh(".ai/scripts").Concat(GlobSh("scripts")))
        if (RunBashSyntaxCheck(s) != 0) syntaxBad.Append(' ').Append(s);
    if (syntaxBad.Length == 0) { passed++; lines.Add("PASS V16: 全部 .ai 与根 scripts 脚本 bash -n 语法通过"); }
    else { failed++; lines.Add($"FAIL V16: 脚本语法/存在性失败（.ai 与根 scripts）（{syntaxBad}）"); }
}

// ─── V17 账本轮次结构完整性（两区区间：轮次记录..逃逸账本 + 轮次记录（续..EOF）───
{
    var metrics = TryReadLines(".ai/review/metrics.md");
    var range = SedRange(metrics, "^## 轮次记录$", "^## 缺陷逃逸账本")
        .Concat(SedRange(metrics, "^## 轮次记录（续", null));
    var (rows, v17Bad) = CheckDateMonotonic(range);
    if (rows >= 15 && v17Bad.Length == 0) { passed++; lines.Add($"PASS V17: 账本轮次结构完整（{rows} 行日期单调）"); }
    else
    {
        failed++; lines.Add($"FAIL V17: 账本轮次结构异常（rows={rows} bad={(v17Bad.Length > 0 ? v17Bad.ToString() : "无")}）");
    }
}

// ─── V18 修复门协议存在性 ───
{
    var text = TryReadText(".ai/review/engine.md");
    if (text.Contains("修复门两问") && text.Contains("s ≤ p'"))
    {
        passed++; lines.Add("PASS V18: 修复门两问协议在档（engine.md 评审-修复循环协议第 6 条）");
    }
    else
    {
        failed++; lines.Add("FAIL V18: 修复门协议缺失（engine.md 需含「修复门两问」与 s ≤ p' 关键词）");
    }
}

// ─── V20 任务进件模板存在性（bash 源中物理顺序在 V19 前，输出顺序保持一致）───
{
    var path = "src/PalDDD.Prompts/.pal/prompts/task-intake.prompt.md";
    var text = TryReadText(path);
    if (File.Exists(path) && text.Contains("验收断言") && text.Contains("拒绝发起"))
    {
        passed++; lines.Add("PASS V20: 任务进件模板在档（验收断言必填 + 拒绝路径）");
    }
    else
    {
        failed++; lines.Add("FAIL V20: 任务进件模板缺失/不完整（需含「验收断言」与「拒绝发起」）");
    }
}

// ─── V19 传感器台账超期校验（90 天周期，DateTime 计算 + F-03 路径存在性）───
{
    var ledger = TryReadLines(".ai/gate/sensor-ledger.md");
    var (v19Bad, unknown) = CheckLedgerExpiry(ledger, 90, DateTime.UtcNow.Date);
    // F-03：传感器路径存在性（原盲区——台账指向已删脚本而门禁全绿）
    var v19PathBad = CheckLedgerSensorPaths(ledger, root);
    if (File.Exists(".ai/gate/sensor-ledger.md") && v19Bad.Length == 0 && v19PathBad.Length == 0)
    {
        passed++; lines.Add($"PASS V19: 传感器台账定标有效（UNKNOWN {unknown} 处待定标，90 天周期，路径 {CountLedgerSensorRows(ledger)} 行全存在）");
    }
    else
    {
        var detail = v19Bad.Length > 0 ? v19Bad.ToString()
            : v19PathBad.Length > 0 ? v19PathBad.ToString() : "台账缺失";
        failed++; lines.Add($"FAIL V19: 传感器台账超期/路径失实（{detail}）");
    }
}

// ─── V21 P3 账本老化校验（30 天周期，DateTime 计算）───
{
    var v21Bad = CheckP3Aging(TryReadLines(".ai/review/action-items-p3-backlog.md"), 30, DateTime.UtcNow.Date);
    if (File.Exists(".ai/review/action-items-p3-backlog.md") && v21Bad.Length == 0)
    {
        passed++; lines.Add("PASS V21: P3 账本无超期未升级条目（30 天老化周期）");
    }
    else
    {
        failed++; lines.Add($"FAIL V21: P3 账本存在超期未处置条目（{(v21Bad.Length > 0 ? v21Bad.ToString() : "账本缺失")}）");
    }
}

// ─── V22 经验编号跨章唯一性（排除 ITM-*/ADR-* 追溯索引）───
{
    var dup = CheckIdUniqueness(TryReadLines(".ai/lessons.md"));
    if (dup.Count == 0) { passed++; lines.Add("PASS V22: lessons 经验编号跨章唯一（无同名异章 ID）"); }
    else
    {
        // bash `... | sort -u | tr '\n' ' '`：元素空格连接且尾随一个空格
        failed++; lines.Add($"FAIL V22: lessons 经验编号冲突（跨章同名 ID: {string.Join(" ", dup)} ）");
    }
}

// ─── V23 镜像脚本对内容比对（MIG-012-D：双镜像合一后本项退役为 PASS 说明）───
{
    // MIG-012-B2 将三组双镜像（review-snapshot/refine-scan/verify-action-items）合一为
    // 单一 .cs（scripts/*.cs 从 CWD 向上找仓库根，天然无 ROOT 定位差异）——镜像漂移类
    // 问题在结构上不可再发生。保留 V23 编号与 PASS 输出（V 计数口径不变），判定恒过。
    passed++; lines.Add("PASS V23: 镜像脚本对已合一（MIG-012-B2 双镜像退役，结构上无漂移面）");
}

// ─── V24 轮次号↔报告文件存在性（F-13，2026-09-21 审计 M4）───
// 背景：metrics.md 有 39 个轮次（v53-v91）在 .ai/review/history/reports 与主仓
// docs/review 两地均无报告文件——"验证轮抓出假修"这类核心产出只剩一行摘要，不可回放。
// V17 只校验 metrics 表格内日期单调，发现不了"轮次号↔报告文件"的对应断裂。
// 口径：metrics 轮次表里每个 v{n} 轮次，须存在文件名含 "-v{n}" 的报告；缺文件即 FAIL。
// **归档缺口声明机制**：缺口允许在 metrics「轮次记录」节用 `归档缺口声明：v{A}-v{B}`
// 显式登记（单一真源）。V24 比对「声明区间展开集 == 实测缺失集」：相等 → PASS（缺口
// 如实文档化而非隐性丢失）；不等 → FAIL 并列差异（声明外有缺口 = 新轮次未归档；
// 声明内有报告 = 声明过期需缩区）。这样缺口被记录，同时任何新缺口仍被机械捕获。
{
    var metricsLines = TryReadLines(".ai/review/metrics.md");
    var reportsDir = Path.Combine(root, ".ai", "review", "history", "reports");
    var reportNames = Directory.Exists(reportsDir)
        ? Directory.GetFiles(reportsDir, "*.md").Select(Path.GetFileNameWithoutExtension).ToList()
        : new List<string?>();
    // 也认主仓 docs/review/ 的报告（v88 起的轮次报告落在那儿）
    var docsReviewDir = Path.Combine(root, "docs", "review");
    if (Directory.Exists(docsReviewDir))
        reportNames.AddRange(Directory.GetFiles(docsReviewDir, "*.md").Select(Path.GetFileNameWithoutExtension));

    // 解析归档缺口声明行里的 `v{A}-v{B}` 区间（可多个）——提前到行扫描之前：
    // T-28（2026-09-22）起"无报告"豁免须与声明联动，故需先有 declared。
    var declared = new HashSet<int>();
    foreach (var line in metricsLines)
    {
        if (!line.Contains("归档缺口声明", StringComparison.Ordinal)) continue;
        foreach (Match dm in Regex.Matches(line, @"v([0-9]+)\s*-\s*v([0-9]+)"))
        {
            var lo = int.Parse(dm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var hi = int.Parse(dm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            for (var i = lo; i <= hi; i++) declared.Add(i);
        }
    }

    var missing = new List<int>();
    var checkedCount = 0;
    var unregisteredExempt = new List<int>();
    foreach (var line in metricsLines)
    {
        var m = Regex.Match(line, @"^\|\s*v([0-9]+)\s*\|");
        if (!m.Success) continue;
        var n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        // T-28（2026-09-22）：原实现 `if (line.Contains("无报告")) continue;` 位于 checkedCount++
        // 之前——任一轮次行写上"无报告"三字即**永久免检**：无需登记、无上限、无闭合期限。
        // 现改为与归档缺口声明联动：写了"无报告"却未落在声明区间内 ⇒ 计入未登记豁免并 FAIL。
        if (line.Contains("无报告", StringComparison.Ordinal))
        {
            if (!declared.Contains(n)) unregisteredExempt.Add(n);
            continue;
        }
        checkedCount++;
        // 文件名须含 "-v{n}" 或 "-v{n}-"（如 review-2026-08-26-full-carpet-v10-verification）
        var hit = reportNames.Any(r => r is not null
            && (r.Contains($"-v{n}", StringComparison.Ordinal)
                || r.Contains($"-v{n}-", StringComparison.Ordinal)));
        if (!hit) missing.Add(n);
    }

    // 覆盖边界（T-28）：本判定只认**首格为 vN** 的行。轮次号写在"类型"列的旧时代行
    // （形如 `全量（v26 六片完整）`）结构上不可见——实测 34 轮（v14/v15/v17-v51）既无报告
    // 也不在声明区间。此处显式报出该边界，使 PASS 不再暗示"全部轮次已核对"。
    var eraRows = metricsLines.Count(l =>
        Regex.IsMatch(l, @"^\|\s*20[0-9]{2}-") && Regex.IsMatch(l, @"\bv[0-9]+"));

    var missingSet = missing.ToHashSet();
    var undeclared = missingSet.Where(n => !declared.Contains(n)).OrderBy(n => n).ToList();
    var staleDeclared = declared.Where(n => !missingSet.Contains(n)).OrderBy(n => n).ToList();

    if (unregisteredExempt.Count > 0)
    {
        failed++; lines.Add($"FAIL V24: 「无报告」豁免未与归档缺口声明联动（v{string.Join(" v", unregisteredExempt.Take(20))}）——写了豁免必须同步登记声明区间");
    }
    else if (missingSet.Count == 0)
    {
        passed++; lines.Add($"PASS V24: 轮次号↔报告文件对应完整（{checkedCount} 个 v 轮次全部有报告；另有 {eraRows} 行时代列行不在面内，见覆盖边界说明）");
    }
    else if (undeclared.Count == 0 && staleDeclared.Count == 0)
    {
        passed++; lines.Add($"PASS V24: 轮次号↔报告文件对应（{checkedCount} 轮中 {missingSet.Count} 个缺口已由归档缺口声明登记 v{missingSet.Min()}-v{missingSet.Max()}，声明与实测一致；另有 {eraRows} 行时代列行不在面内）");
    }
    else
    {
        var parts = new List<string>();
        if (undeclared.Count > 0)
            parts.Add($"{undeclared.Count} 个未登记缺口（v{string.Join(" v", undeclared.Take(20))}{(undeclared.Count > 20 ? " …" : "")}）");
        if (staleDeclared.Count > 0)
            parts.Add($"声明过期 {staleDeclared.Count} 个（v{string.Join(" v", staleDeclared.Take(20))} 已有报告，应缩小区间）");
        failed++; lines.Add($"FAIL V24: 轮次报告缺口与归档声明不一致（{string.Join("；", parts)}）——补报告、登记缺口或修正声明区间");
    }
}

// ─── V25 .ai 文档命令形态与死引用（2026-09-21 实践驱动新增）───
// **为什么有这个门禁（实践实证，非推测）**：审计修复 22 项时，F-07/F-09 手工校准了
// engine/fix-protocol/charter/README 的命令形态；修完当场再 grep，**又抓到我漏看的
// 6 处同类失实**（test/prompt.md:266 `bash scripts/test-gate.cs`、lessons.md:40 与
// README.md:7/109-112 的已删 `.ai/scripts/*.sh`）。结论：该类失败（MIG-012 迁移后
// 遗留的旧命令形态）**手工修复不收敛**——每次只修"知道的那几处"，新阅读处必再发现。
// 必须机械固化，与 V19 路径校验（传感器台账）构成"指针层"双闸。
// 判定两类：
//   ① 命令形态错：`bash scripts/X.cs` / `bash .ai/scripts/X.cs`——bash 不能执行 C#
//      file-based app（Gates 真身自 MIG-012 起是 .cs，须 dotnet run）；
//   ② 死引用：文档引用 `.ai/scripts/X.sh` 或 `scripts/X.sh` 而该文件不存在。
// 排除：history/ 归档（历史档案如实保留旧形态）；含历史标记的行（勘正/原写/已删/
// 已退役/旧形态/历史版本——F-NN 勘正注有意引用旧形态以说明改了什么）。
{
    var v25Bad = CheckAiDocCommandForms(root);
    if (v25Bad.Count == 0)
    {
        var aiMdCount = Directory.Exists(Path.Combine(root, ".ai"))
            ? Directory.GetFiles(Path.Combine(root, ".ai"), "*.md", SearchOption.AllDirectories)
                .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}history{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            : 0;
        passed++; lines.Add($"PASS V25: .ai 文档命令形态与引用完好（{aiMdCount} 个文档，无 bash-.cs 误用、无死引用）");
    }
    else
    {
        var shown = v25Bad.Count > 60 ? string.Join("；", v25Bad.Take(60)) + $"；…等 {v25Bad.Count} 处" : string.Join("；", v25Bad);
        failed++; lines.Add($"FAIL V25: .ai 文档命令形态/死引用 {v25Bad.Count} 处（{shown}）——改为 dotnet run 或删死引用");
    }
}

lines.Add("");
lines.Add($"通过：{passed}  失败：{failed}  总计：{passed + failed}");
lines.Add("═══════ 校验完成 ═══════");
foreach (var l in lines) Console.WriteLine(l);
return failed == 0 ? 0 : 1;

// ══════════════ static 局部函数（bash/awk 语义等价实现）══════════════

// 仓库根定位：CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底（见头注释迁移说明 3）
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
    return ""; // 不可达（Environment.Exit 不返回）
}

// 文件存在性清单：缺失项拼 " f1 f2"（前导空格，与 bash missing="$missing $f" 一致）
static string MissingList(string[] files)
{
    var sb = new StringBuilder();
    foreach (var f in files)
        if (!File.Exists(f)) sb.Append(' ').Append(f);
    return sb.ToString();
}

// 容错读：缺失/不可读返回空（与 bash 2>/dev/null || echo 0 兜底语义一致）
static string TryReadText(string path)
{
    try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
    catch (IOException) { return ""; }
    catch (UnauthorizedAccessException) { return ""; }
}

static string[] TryReadLines(string path)
{
    try { return File.Exists(path) ? File.ReadAllLines(path) : []; }
    catch (IOException) { return []; }
    catch (UnauthorizedAccessException) { return []; }
}

// 行级正则计数（grep -c 语义）
static int CountLinesMatching(string[] lines, string pattern) =>
    lines.Count(l => Regex.IsMatch(l, pattern));

// wc -l 语义：数换行符（\n）字节数
static int CountNewlines(byte[] bytes)
{
    var n = 0;
    foreach (var b in bytes) if (b == (byte)'\n') n++;
    return n;
}

// grep -oE | sort -u：全部非重叠匹配去重排序（Ordinal 序 = C locale sort）
static List<string> SortedMatches(string text, string pattern)
{
    var set = new SortedSet<string>(StringComparer.Ordinal);
    foreach (Match m in Regex.Matches(text, pattern)) set.Add(m.Value);
    return set.ToList();
}

// sed -n '/start/,/end/p' 区间（含端点，可重复触发；end=null 表示到 EOF）
static List<string> SedRange(string[] all, string startPattern, string? endPattern)
{
    var result = new List<string>();
    var inRange = false;
    foreach (var line in all)
    {
        if (!inRange)
        {
            if (Regex.IsMatch(line, startPattern)) inRange = true;
            else continue;
        }
        else if (endPattern is not null && Regex.IsMatch(line, endPattern))
        {
            result.Add(line);
            inRange = false;
            continue;
        }
        if (inRange) result.Add(line);
    }
    return result;
}

// bash glob *.sh：目录内 .sh 按名称序（NTFS readdir 字母序与 bash glob 一致）
static List<string> GlobSh(string dir) =>
    Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.sh").Select(ToPosix).ToList()
        : [];

// 路径转 posix 正斜杠（对齐 bash 输出形式）
static string ToPosix(string path) => path.Replace('\\', '/');

// bash -n 语法检查（Process 调 bash；退出码 0=语法通过）
static int RunBashSyntaxCheck(string script)
{
    var psi = new ProcessStartInfo("bash", "-n " + script)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
}

// 日历合法性（含闰年；等价 awk Dim()）→ 非法返回 null
static DateTime? ParseDate(string ymd)
{
    return DateTime.TryParse(ymd, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d)
        && d.TimeOfDay == TimeSpan.Zero ? d : null;
}

// （MIG-012-D：NormalizeMirror/SimpleDiff 随 V23 镜像比对退役——双镜像已合一，保留即 CS8321 死代码）

// ══════════════ ITM-664（v87）：核心校验逻辑提取 + --selftest 红绿矩阵 ══════════════
// 四个 V 项的判定核心从内联块提取为纯函数（lines 进、结果出）——主流程与自测共用
// 同一实现（无复制漂移面）；自测矩阵验证"仪器能失败"（PD29）。

// V17 核心：metrics 表格行日期单调性 + 列数（bash case \|*\| 口径）
static (int Rows, string Bad) CheckDateMonotonic(IEnumerable<string> lines)
{
    var bad = new StringBuilder();
    var rows = 0;
    string? prev = null;
    foreach (var line in lines)
    {
        if (line.Length < 2 || line[0] != '|' || line[^1] != '|') continue;
        var parts = line.Split('|');
        var d2 = parts.Length > 1 ? parts[1].Replace(" ", "") : "";
        var d3 = parts.Length > 2 ? parts[2].Replace(" ", "") : "";
        var d = Regex.IsMatch(d2, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$") ? d2 : d3;
        if (!Regex.IsMatch(d, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$")) continue;
        rows++;
        var pipes = line.Count(c => c == '|');
        if (pipes < 10) bad.Append($" {d}:列数不足({pipes})");
        if (prev is not null && string.CompareOrdinal(d, prev) < 0)
            bad.Append($" {d}:乱序(前值{prev})");
        prev = d;
    }
    return (rows, bad.ToString());
}

// V19 核心：传感器台账定标周期（90 天；UNKNOWN 计数不判坏；非法日期 fail-closed）
// F-03（2026-09-21，审计 S2）：加**传感器路径存在性**校验。原实现只验日期——
// MIG-012 后台账 5+1 个传感器指向已删 `.ai/scripts/*.sh`，实测把某行改成
// `PROBE-NONEXISTENT.sh` 仍 23/23 全过（"对着空气 OK"）。现解析"传感器"列的
// 路径 token 并断言文件真实存在。
static (string Bad, int Unknown) CheckLedgerExpiry(IEnumerable<string> lines, int maxAgeDays, DateTime today)
{
    var bad = new StringBuilder();
    var unknown = 0;
    foreach (var line in lines)
    {
        var parts = line.Split('|');
        if (parts.Length >= 6 && parts[5].Trim(' ', '\t', '\r') == "UNKNOWN") unknown++;
        if (parts.Length < 7) continue;
        var name = parts[1].Trim(' ', '\t', '\r');
        var status = parts[5].Trim(' ', '\t', '\r');
        if (status is not ("OK" or "观察中")) continue;
        var ds = parts[6].Trim(' ', '\t', '\r');
        if (!Regex.IsMatch(ds, "^[0-9]{4}-[0-9]{2}-[0-9]{2}"))
        {
            bad.Append(name).Append("：日期缺失/非法(").Append(ds).Append(")\n");
            continue;
        }
        var date = ParseDate(ds[..10]);
        if (date is null)
        {
            bad.Append(name).Append("：日期缺失/非法(").Append(ds.AsSpan(0, 10)).Append(")\n");
            continue;
        }
        var age = (today - date.Value).Days;
        if (age > maxAgeDays)
            bad.Append(name).Append("：定标超期 ").Append(age).Append(" 天(")
                .Append(ds.AsSpan(0, 10)).Append(")→STALE\n");
    }
    return (bad.ToString(), unknown);
}

// V19 附属：统计台账中被校验路径的传感器行数（PASS 行的可观测口径）
static int CountLedgerSensorRows(IEnumerable<string> lines)
{
    var n = 0;
    foreach (var line in lines)
    {
        var parts = line.Split('|');
        if (parts.Length < 7) continue;
        if (parts[5].Trim(' ', '\t', '\r') is not ("OK" or "观察中")) continue;
        n++;
    }
    return n;
}

// F-05：从文本行提取"声称的 PD 计数"。两种 kind：
//   maxPd  — `至 PD37` / `PD1-PD37` / `PD1-PD{N}`（声称的最大 PD 号）
//   total  — `共 45 模式` / `45 模式含速版` / `{M} 模式`（声称的模式总数）
// 只取当前有效声明行；历史记录（history/ 归档、metrics 历史轮次行）由调用方传入的
// 文件范围天然排除——本函数只处理调用方给定的行集。
static List<(string Kind, int Value)> ExtractProsePdClaims(IEnumerable<string> lines)
{
    var claims = new List<(string, int)>();
    foreach (var line in lines)
    {
        foreach (Match m in Regex.Matches(line, @"至\s*PD([0-9]+)"))
            claims.Add(("maxPd", int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        foreach (Match m in Regex.Matches(line, @"PD1-PD([0-9]+)"))
            claims.Add(("maxPd", int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        foreach (Match m in Regex.Matches(line, @"共\s*([0-9]+)\s*模式"))
            claims.Add(("total", int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        foreach (Match m in Regex.Matches(line, @"([0-9]+)\s*模式含速版"))
            claims.Add(("total", int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }
    return claims;
}

// F-05：从单个文件读取并提取 prose 计数（文件不存在返回空列表）
static List<(string Kind, int Value)> ExtractProsePdClaimsFromFile(string path)
    => File.Exists(path) ? ExtractProsePdClaims(File.ReadAllLines(path, Encoding.UTF8)) : new List<(string, int)>();

// F-03 路径存在性：从台账"传感器"列提取路径 token，断言文件真实存在。
// 解析规则（按优先级）：
//   ① 反引号包裹的 token（最可靠，台账主要形态）
//   ② 形如 `scripts/x.cs` / `test/.../x.cs` / `.ai/...` 的裸 token
//   ③ 反引号内的裸文件名（如 `known-false-positives.md`）→ 依次尝试
//      <root>/.ai/review/<name>、<root>/.ai/<name>、<root>/<name>
// `…`（省略号）转 glob：`test/…/ArchitectureBoundaryTests.cs` → 递归查找文件名。
// 无法解析为路径的 token（自然语言描述、`dotnet build` 等）跳过——只验"像路径的"。
static string CheckLedgerSensorPaths(IEnumerable<string> lines, string repoRoot)
{
    var bad = new StringBuilder();
    foreach (var line in lines)
    {
        var parts = line.Split('|');
        if (parts.Length < 7) continue;
        var name = parts[1].Trim(' ', '\t', '\r');
        var status = parts[5].Trim(' ', '\t', '\r');
        if (status is not ("OK" or "观察中")) continue;

        // 收集候选路径 token：反引号内 + 裸路径形态。
        // 列位：parts[1]=关切 / parts[2]=**传感器**（路径所在列）/ parts[5]=定标状态 / parts[6]=日期
        var candidates = new List<string>();
        foreach (Match m in Regex.Matches(parts[2], "`([^`]+)`"))
        {
            // 反引号内可能含参数（`scripts/verify-ai.cs --selftest`）或命令前缀
            // （`dotnet run scripts/verify-ai.cs`）——按空白切段，只取"像路径"的段。
            foreach (var seg in m.Groups[1].Value.Split(' ', '\t'))
                if (LooksLikePath(seg)) candidates.Add(seg.Trim());
        }
        foreach (Match m in Regex.Matches(parts[2],
            @"(?<![\w/`.-])((?:\.ai/|scripts/|test/|docs/)[\w./…-]+\.(?:cs|md|sh|json))"))
            candidates.Add(m.Groups[1].Value);

        foreach (var token in candidates)
        {
            if (SensorPathExists(token, repoRoot)) continue;
            // 反引号内可能是自然语言（如 `dotnet build`）——含空白或不像路径则跳过
            if (token.Contains(' ') && !token.Contains('/')) continue;
            bad.Append(name).Append("：传感器路径不存在(").Append(token).Append(")\n");
        }
    }
    return bad.ToString();
}

// V25 核心（2026-09-21 实践驱动）：扫描 .ai 下非 history 的 .md，检出两类指针失实。
// 提取为纯函数供主流程与 --selftest 共用（selftest 用临时目录注入坏样本）。
//   ① 命令形态错：`bash scripts/X.cs` / `bash .ai/scripts/X.cs`（bash 不能跑 .cs）；
//   ② 死引用：`.ai/scripts/X.sh` 或 `scripts/X.sh` 指向不存在的文件。
// 排除：history/ 归档；含历史标记的行（勘正/原写/已删/退役/旧形态/历史版本/反例 等
// ——F-NN 勘正注与 KFP 反例记录有意引用旧形态以说明当时发生了什么）。
// ③ 的文档角色界定（T-26，2026-09-22）：裸名判定只作用于**现行命令面**文档。
// 理由：历史/账本类文档（lessons/metrics/误判知识库/行动项账本/姊妹快照）按定义描述"当时
// 发生了什么"，其中的脚本名是历史事实——改写等于篡改历史（本仓"报告不可变"规则）。
// 实测：不加界定则 46 处命中全部落在这类文档；界定后只有现行协议文档（engine/fix-protocol/
// README/prompt 等）报红，即复核报告所指"对外展示面宣称的防线有一半不存在"那一类。
static bool IsCurrentCommandSurface(string relPath)
{
    var name = Path.GetFileName(relPath);
    return !(name.StartsWith("lessons", StringComparison.Ordinal)
          || name == "metrics.md"
          || name == "known-false-positives.md"
          || name.StartsWith("action-items", StringComparison.Ordinal)
          || name == "sibling-map.md"
          || name == "perspective-stats.md");
}

static List<string> CheckAiDocCommandForms(string repoRoot)
{
    // ③ 的待改名基线（T-26，2026-09-22）：现行协议文档中仍以旧 `.sh` 名引用已迁移门禁的位置。
    // 这些条目**被 ③ 跳过**（V25 因此仍为 25/25），但基线本身、owner 与到期日都在此可见——
    // 满足 T-24 的观察态纪律（观察态须有 owner 与到期时间，否则退化为无人再看的静默 no-op）。
    // 到期（2026-12-31）须处置：改名、或续期并写明理由。
    // 清空方式：左列 `.sh` 换成现行 `.cs`——同干名者换扩展名；`*-check.sh` 去 `-check`
    // （`doc-consistency-check.sh`→`doc-consistency.cs`、`fix-completeness-check.sh`→`fix-completeness.cs`）；
    // `sister-axis-scan.sh`→`sister-axis.cs`；`gate-check.sh`→`gate.cs`；
    // `assertion-strength-check.sh` 判定已下沉 AssertionStrengthGateTests（无对应脚本）。
    // owner: 框架维护者 · 到期: 2026-12-31
    var pendingRename = new HashSet<string>(StringComparer.Ordinal)
    {
        ".ai/review/engine.md|fix-orchestrator.sh",
        ".ai/review/engine.md|review-scope.sh",
        ".ai/review/fix-protocol.md|sister-axis-scan.sh",
        ".ai/review/fix-protocol.md|fix-completeness-check.sh",
        ".ai/review/prompt.md|doc-consistency-check.sh",
        ".ai/review/prompt.md|assertion-strength-check.sh",
        ".ai/review/prompt.md|review-scope.sh",
        ".ai/review/prompt.md|probe-template.sh",
        ".ai/review/review-charter-v2.md|sister-axis-scan.sh",
        ".ai/review/review-charter-v2.md|assertion-strength-check.sh",
        ".ai/review/review-charter-v2.md|post-fix-check.sh",
        ".ai/review/review-charter-v2.md|fix-completeness-check.sh",
        ".ai/review/review-charter-v2.md|review-gate.sh",
        ".ai/test/prompt.md|gate-check.sh",
        ".ai/test/prompt.md|test-gate.sh",
    };

    var bad = new List<string>();
    var aiDir = Path.Combine(repoRoot, ".ai");
    if (!Directory.Exists(aiDir)) return bad;

    var aiMd = Directory.GetFiles(aiDir, "*.md", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}history{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .ToList();
    // 死引用判定的真源：两个 scripts 目录的现存文件名集
    var aiScripts = Directory.Exists(Path.Combine(aiDir, "scripts"))
        ? Directory.GetFiles(Path.Combine(aiDir, "scripts")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string?>();
    var rootScripts = Directory.Exists(Path.Combine(repoRoot, "scripts"))
        ? Directory.GetFiles(Path.Combine(repoRoot, "scripts")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string?>();

    foreach (var file in aiMd)
    {
        var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
        var lineno = 0;
        foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            lineno++;
            var line = raw;
            if (line.Contains("勘正", StringComparison.Ordinal)
                || line.Contains("原写", StringComparison.Ordinal)
                || line.Contains("原引用", StringComparison.Ordinal)
                || line.Contains("已删", StringComparison.Ordinal)
                || line.Contains("已退役", StringComparison.Ordinal)
                || line.Contains("旧形态", StringComparison.Ordinal)
                || line.Contains("历史版本", StringComparison.Ordinal)
                || line.Contains("退役", StringComparison.Ordinal)
                || line.Contains("反例", StringComparison.Ordinal)
                || Regex.IsMatch(line, @"原.{0,6}(bash|命令|引用)"))
                continue;

            // ① bash 跑 .cs
            if (Regex.IsMatch(line, @"bash\s+(?:\.ai/)?scripts/[A-Za-z0-9_-]+\.cs"))
                bad.Add($"{rel}:{lineno} bash 执行 .cs（Gates 是 C# file-based app，须 dotnet run）");

            // ② .ai/scripts/X.sh 死引用
            foreach (Match m in Regex.Matches(line, @"\.ai/scripts/([A-Za-z0-9_-]+\.sh)"))
                if (!aiScripts.Contains(m.Groups[1].Value))
                    bad.Add($"{rel}:{lineno} 引用已删脚本 .ai/scripts/{m.Groups[1].Value}");

            // ②' 根 scripts/X.sh 死引用
            foreach (Match m in Regex.Matches(line, @"(?<!\.ai/)\bscripts/([A-Za-z0-9_-]+\.sh)"))
                if (!rootScripts.Contains(m.Groups[1].Value) && !aiScripts.Contains(m.Groups[1].Value))
                    bad.Add($"{rel}:{lineno} 引用不存在脚本 scripts/{m.Groups[1].Value}");

            // ③ 裸文件名死引用（T-26，2026-09-22 增）：无路径前缀的 `X.sh` 若在两个 scripts
            // 目录均不存在即判死引用。修前 V25 只认 ①② 的带前缀形态——实测同一行注入
            // `bash scripts/verify-ai.cs`（被抓）与裸名 `gate-check.sh`（零报告），
            // 而 .ai/README.md 宣称的防线里 6 个已删 .sh 正是靠这个盲区存活。
            // 排除：带路径前缀者（交给 ②/②'，避免重复报）、`.sh.template`（模板快照非脚本）、
            // 以及非现行命令面的历史/账本类文档（见 IsCurrentCommandSurface）。
            if (IsCurrentCommandSurface(rel))
            {
                foreach (Match m in Regex.Matches(line, @"(?<![A-Za-z0-9_/.\-])([A-Za-z0-9_\-]+\.sh)(?!\.)"))
                {
                    var bare = m.Groups[1].Value;
                    if (rootScripts.Contains(bare) || aiScripts.Contains(bare)) continue;
                    if (pendingRename.Contains($"{rel}|{bare}")) continue;   // 待改名基线（见方法头声明）
                    bad.Add($"{rel}:{lineno} 引用不存在脚本（裸名）{bare}");
                }
            }
        }
    }
    return bad;
}

// 单个路径 token 的存在性判定（含 `…` glob 与裸文件名回退）
static bool SensorPathExists(string token, string repoRoot)
{
    // 绝对/相对明确路径
    var direct = token.Replace('/', Path.DirectorySeparatorChar);
    var abs = Path.IsPathRooted(direct) ? direct : Path.Combine(repoRoot, direct);
    if (File.Exists(abs)) return true;

    // `…` 省略号：转为按文件名递归查找（如 test/…/ArchitectureBoundaryTests.cs）
    if (token.Contains('…'))
    {
        var fileName = Path.GetFileName(token.Replace('…', 'x'));
        var searchRoot = repoRoot;
        var seg = token.Split('/')[0];
        if (seg is "test" or "src" or "docs" or "scripts")
        {
            var sub = Path.Combine(repoRoot, seg);
            if (Directory.Exists(sub)) searchRoot = sub;
        }
        return Directory.EnumerateFiles(searchRoot, fileName, SearchOption.AllDirectories).Any();
    }

    // 裸文件名：按台账文件的常见邻位回退
    if (!token.Contains('/'))
    {
        foreach (var dir in new[]
        {
            Path.Combine(repoRoot, ".ai", "review"),
            Path.Combine(repoRoot, ".ai"),
            Path.Combine(repoRoot, "docs"),
            repoRoot,
        })
        {
            if (File.Exists(Path.Combine(dir, token.Replace('/', Path.DirectorySeparatorChar)))) return true;
        }
    }
    return false;
}

// 判定 token 是否"像路径"（供反引号内容切段后筛选）：
// 含目录分隔符，或带已知文件扩展名。排除 `dotnet build`、`DiagnosticCoverageGateTests`
// （类名）、`V1-V23`（编号）等非路径描述——它们不应参与存在性校验。
static bool LooksLikePath(string seg)
{
    if (string.IsNullOrWhiteSpace(seg)) return false;
    if (seg.Contains('/') || seg.Contains('\\')) return true;
    return Regex.IsMatch(seg, @"\.(cs|md|sh|json|csproj|slnx)$", RegexOptions.IgnoreCase);
}

// V21 核心：P3 账本 30 天老化（未勾选 + 超期 + 不含"过期"→ 判坏）
static string CheckP3Aging(IEnumerable<string> lines, int maxAgeDays, DateTime today)
{
    var bad = new StringBuilder();
    foreach (var line in lines)
    {
        if (!line.StartsWith("- [ ] P3-", StringComparison.Ordinal)) continue;
        var m = Regex.Match(line, "[0-9]{4}-[0-9]{2}-[0-9]{2}");
        if (!m.Success) continue;
        var prefix = line.Length > 6 ? line.Substring(6, Math.Min(16, line.Length - 6)) : "";
        var date = ParseDate(m.Value);
        if (date is null)
        {
            bad.Append(prefix).Append("…：日期非法(").Append(m.Value).Append(")\n");
            continue;
        }
        var age = (today - date.Value).Days;
        if (age > maxAgeDays && !line.Contains("过期"))
            bad.Append(prefix).Append("…：未勾选已 ").Append(age).Append(" 天(").Append(m.Value)
                .Append(")，按规则应升 P2\n");
    }
    return bad.ToString();
}

// V22 核心：lessons 经验编号跨章唯一（排除 ITM-*/ADR-* 追溯索引与表头"编号"）
static List<string> CheckIdUniqueness(IEnumerable<string> lines)
{
    var dup = new SortedSet<string>();
    var seen = new Dictionary<string, string>();
    string? ch = null;
    foreach (var line in lines)
    {
        if (Regex.IsMatch(line, "^## [IVX]+[.]"))
        {
            var words = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1) ch = words[1].TrimEnd('.');
        }
        else if (line.Length >= 3 && line[0] == '|' && line[1] == ' ' && line[2] is >= 'A' and <= 'Z')
        {
            var words = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) continue;
            var id = words[1];
            if (id.StartsWith("ITM-", StringComparison.Ordinal)
                || id.StartsWith("ADR-", StringComparison.Ordinal)) continue;
            if (id == "编号") continue;
            if (seen.TryGetValue(id, out var first)) { if (first != ch) dup.Add($"{id}({first}->{ch})"); }
            else seen[id] = ch ?? "";
        }
    }
    return dup.ToList();
}

// ─── ITM-664 自测：红绿矩阵（不触达真实仓库文件——纯合成输入）───
// 退出码：0=全部通过（绿例过 + 红例必红）；1=任一用例失败（仪器失灵，禁止信任其输出）。
static int RunSelftest()
{
    var failures = new List<string>();
    var today = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    // ── V17 日期单调 ──
    // 12 列表格行（13 个管道符 ≥ 10——首版 helper 缺内部管道被"列数不足"全量误判）
    string Row(string d) => $"| {d} | x | x | x | x | x | x | x | x | x | x |";
    var mono = Enumerable.Range(1, 20).Select(i => Row($"2026-08-{i:00}"));
    var (monoRows, monoBad) = CheckDateMonotonic(mono);
    if (monoRows == 20 && monoBad.Length == 0) Console.WriteLine("PASS ST-V17a 日期单调 20 行零坏");
    else failures.Add($"ST-V17a: rows={monoRows} bad={monoBad}");

    var (disBad2, disBad) = (0, "");
    (disBad2, disBad) = CheckDateMonotonic(new[] { Row("2026-09-13"), Row("2026-09-12") });
    if (disBad.Contains("乱序")) Console.WriteLine("PASS ST-V17b 乱序检出");
    else failures.Add($"ST-V17b: bad={disBad}");

    if (CheckDateMonotonic(new[] { Row("2026-09-13"), Row("2026-09-13") }).Bad.Length == 0)
        Console.WriteLine("PASS ST-V17c 同日允许（非乱序）");
    else failures.Add("ST-V17c: 同日被误判乱序");

    // ── V19 定标 90 天 ──
    // 列位对齐：parts[1]=name / parts[5]=status / parts[6]=date（首版多一列 filler 使列偏位全漏检）
    string LedgerRow(string name, string status, string date) => $"| {name} | x | x | x | {status} | {date} | x |";
    var (ok19, unk19) = CheckLedgerExpiry(new[]
    {
        LedgerRow("Fresh", "OK", "2026-09-01 10:00"),
        LedgerRow("Pending", "UNKNOWN", "2026-01-01"),
    }, 90, today);
    if (ok19.Length == 0 && unk19 == 1) Console.WriteLine("PASS ST-V19a 新定标绿 + UNKNOWN 仅计数");
    else failures.Add($"ST-V19a: bad={ok19} unknown={unk19}");

    var (stale19, _) = CheckLedgerExpiry(new[]
    {
        LedgerRow("Stale", "OK", "2026-01-01"),
    }, 90, today);
    if (stale19.Contains("STALE")) Console.WriteLine("PASS ST-V19b 超期 254 天检出 STALE");
    else failures.Add($"ST-V19b: bad={stale19}");

    var (ill19, _) = CheckLedgerExpiry(new[]
    {
        LedgerRow("BadDate", "OK", "not-a-date"),
    }, 90, today);
    if (ill19.Contains("非法")) Console.WriteLine("PASS ST-V19c 非法日期 fail-closed");
    else failures.Add($"ST-V19c: bad={ill19}");

    // ── F-03 传感器路径存在性（审计 S2：原 V19 只查日期不查路径）──
    // 红测语义：指向不存在文件必须 FAIL（修复前台账 5+1 个传感器指向已删 .sh 却全绿）。
    // 注：RunSelftest 是 static 局部函数，不能引用顶层 root——本地调 FindRepoRoot()。
    var stRoot = FindRepoRoot();
    var pathOkRow = new[] { "| 真传感器 | `scripts/verify-ai.cs` | x | x | OK | 2026-09-01 | x |" };
    var pathBadReal = CheckLedgerSensorPaths(pathOkRow, stRoot);
    if (pathBadReal.Length == 0) Console.WriteLine("PASS ST-V19d 现存文件路径放行");
    else failures.Add($"ST-V19d: {pathBadReal}");

    var ghostRow = new[] { "| 幽灵传感器 | `scripts/verify-ai-system-PROBE-NONEXISTENT.cs` | x | x | OK | 2026-09-01 | x |" };
    var pathBadGhost = CheckLedgerSensorPaths(ghostRow, stRoot);
    if (pathBadGhost.Contains("PROBE-NONEXISTENT")) Console.WriteLine("PASS ST-V19e 幽灵路径检出");
    else failures.Add($"ST-V19e: {pathBadGhost}");

    var globGhostRow = new[] { "| glob 传感器 | `test/…/NoSuchTestFile.cs` | x | x | OK | 2026-09-01 | x |" };
    var pathBadGlob = CheckLedgerSensorPaths(globGhostRow, stRoot);
    if (pathBadGlob.Contains("NoSuchTestFile")) Console.WriteLine("PASS ST-V19f 省略号 glob 幽灵检出");
    else failures.Add($"ST-V19f: {pathBadGlob}");

    var globOkRow = new[] { "| glob 真传感器 | `test/…/ArchitectureBoundaryTests.cs` | x | x | OK | 2026-09-01 | x |" };
    var pathOkGlob = CheckLedgerSensorPaths(globOkRow, stRoot);
    if (pathOkGlob.Length == 0) Console.WriteLine("PASS ST-V19g 省略号 glob 现存文件放行");
    else failures.Add($"ST-V19g: {pathOkGlob}");

    var langRow = new[] { "| 自然语言 | `dotnet build` | x | x | OK | 2026-09-01 | x |" };
    var pathNaturalLang = CheckLedgerSensorPaths(langRow, stRoot);
    if (pathNaturalLang.Length == 0) Console.WriteLine("PASS ST-V19h 非路径描述不误判");
    else failures.Add($"ST-V19h: {pathNaturalLang}");

    var argRow = new[] { "| 带参路径 | `scripts/verify-ai.cs --selftest` | x | x | OK | 2026-09-01 | x |" };
    var pathWithArg = CheckLedgerSensorPaths(argRow, stRoot);
    if (pathWithArg.Length == 0) Console.WriteLine("PASS ST-V19i 反引号内含参数只取路径段");
    else failures.Add($"ST-V19i: {pathWithArg}");

    var classNameRow = new[] { "| 类名 | `DiagnosticCoverageGateTests` | x | x | OK | 2026-09-01 | x |" };
    var pathClassName = CheckLedgerSensorPaths(classNameRow, stRoot);
    if (pathClassName.Length == 0) Console.WriteLine("PASS ST-V19j 无扩展名类名不误判");
    else failures.Add($"ST-V19j: {pathClassName}");

    // ── V25 .ai 文档命令形态与死引用（2026-09-21 实践驱动）──
    // 实践实证：F-07/F-09 手工校准命令形态后当场再 grep 又抓到 6 处同类，机械化后
    // 一次抓 32 处——手工修复不收敛，必须门禁固化。红测覆盖三类判定 + 两类排除。
    var v25Dir = Path.Combine(Path.GetTempPath(), "verify-ai-v25-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        Directory.CreateDirectory(Path.Combine(v25Dir, ".ai", "scripts"));
        Directory.CreateDirectory(Path.Combine(v25Dir, "scripts"));
        // 现存脚本（真源）：.ai/scripts 两个 bash + 根 scripts 一个 .cs
        File.WriteAllText(Path.Combine(v25Dir, ".ai", "scripts", "install-ai-system.sh"), "#!/bin/bash\n");
        File.WriteAllText(Path.Combine(v25Dir, ".ai", "scripts", "template-gate.sh"), "#!/bin/bash\n");
        File.WriteAllText(Path.Combine(v25Dir, "scripts", "verify-ai.cs"), "// gate\n");

        var v25Doc = Path.Combine(v25Dir, ".ai", "probe.md");
        File.WriteAllLines(v25Doc, V25Samples.Bad);
        var v25Bad = CheckAiDocCommandForms(v25Dir);
        if (v25Bad.Count == 3 && v25Bad.Any(b => b.Contains("bash 执行 .cs"))
            && v25Bad.Any(b => b.Contains("gate-check.sh"))
            && v25Bad.Any(b => b.Contains("verify-conventions.sh")))
            Console.WriteLine("PASS ST-V25a 三类失实检出（bash-.cs / 已删 .ai .sh / 不存在根 .sh）");
        else failures.Add($"ST-V25a: 期望 3 处，实得 {v25Bad.Count}（{string.Join(" | ", v25Bad)}）");

        // 负向：只有正常形态与历史标记时零检出
        File.WriteAllLines(v25Doc, V25Samples.Clean);
        var v25Clean = CheckAiDocCommandForms(v25Dir);
        if (v25Clean.Count == 0) Console.WriteLine("PASS ST-V25b 正常形态与历史标记行零误判");
        else failures.Add($"ST-V25b: {string.Join(" | ", v25Clean)}");
    }
    finally
    {
        // 清理失败不掩盖探针结论（临时目录，失败无副作用）
        try { if (Directory.Exists(v25Dir)) Directory.Delete(v25Dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── V21 老化 30 天 ──
    string[] fresh21Lines = ["- [ ] P3-1 新条目 2026-09-01"];
    string[] old21Lines = ["- [ ] P3-2 陈旧条目 2026-07-01"];
    string[] exempt21Lines = ["- [ ] P3-3 已豁免条目（过期） 2026-07-01"];
    var fresh21 = CheckP3Aging(fresh21Lines, 30, today);
    if (fresh21.Length == 0) Console.WriteLine("PASS ST-V21a 12 天新条目绿");
    else failures.Add($"ST-V21a: bad={fresh21}");

    var old21 = CheckP3Aging(old21Lines, 30, today);
    if (old21.Contains("应升 P2")) Console.WriteLine("PASS ST-V21b 超期 74 天检出应升 P2");
    else failures.Add($"ST-V21b: bad={old21}");

    var exempt21 = CheckP3Aging(exempt21Lines, 30, today);
    if (exempt21.Length == 0) Console.WriteLine("PASS ST-V21c 含\"过期\"豁免绿");
    else failures.Add($"ST-V21c: bad={exempt21}");

    // ── V22 编号唯一 ──
    var uniqLessons = new[]
    {
        "## XIII. 测试教训",
        "| COV-1 | x |",
        "## XVII. 诊断教训",
        "| AUD-1 | x |",
    };
    if (CheckIdUniqueness(uniqLessons).Count == 0) Console.WriteLine("PASS ST-V22a 跨章不同 ID 绿");
    else failures.Add("ST-V22a: 无冲突被误判");

    var dupLessons = new[]
    {
        "## XIII. 测试教训",
        "| COV-1 | x |",
        "## XVII. 诊断教训",
        "| COV-1 | x |",
    };
    var dup22 = CheckIdUniqueness(dupLessons);
    if (dup22.Count == 1 && dup22[0].Contains("COV-1(XIII->XVII)")) Console.WriteLine("PASS ST-V22b 跨章同名检出");
    else failures.Add($"ST-V22b: dup={string.Join(",", dup22)}");

    var itmLessons = new[]
    {
        "## XIII. 测试教训",
        "| ITM-123 | x |",
        "## XVII. 诊断教训",
        "| ITM-123 | x |",
    };
    if (CheckIdUniqueness(itmLessons).Count == 0) Console.WriteLine("PASS ST-V22c ITM- 追溯索引豁免");
    else failures.Add("ST-V22c: ITM- 前缀未豁免");

    // ── V2 缺失清单 ──
    if (MissingList([" definitely-missing-file.xyz"]).Contains("definitely-missing-file.xyz"))
        Console.WriteLine("PASS ST-V2a 缺失文件进清单");
    else failures.Add("ST-V2a: 缺失文件未检出");
    if (MissingList([]).Length == 0) Console.WriteLine("PASS ST-V2b 空清单绿");
    else failures.Add("ST-V2b: 空清单误报");

    // ── V5 保留集断言逻辑（ITM-668：G 编号提取与 <22 过滤——用 PDDD-G 前缀匹配实际格式）───
    var gValid = SortedMatches("PDDD-G22 PDDD-G23 PDDD-G24", "PDDD-G[0-9]+");
    var gValidOk = gValid.Count == 3 && gValid.All(id => int.Parse(id["PDDD-G".Length..], System.Globalization.CultureInfo.InvariantCulture) >= 22);
    if (gValidOk) Console.WriteLine("PASS ST-V5a 保留集 {G22,G23,G24} 全 ≥22");
    else failures.Add($"ST-V5a: gValid={string.Join(",", gValid)}");

    var gStale = SortedMatches("PDDD-G1..G24 全阻断", "PDDD-G[0-9]+");
    var gStaleOk = gStale.Any(id => int.Parse(id["PDDD-G".Length..], System.Globalization.CultureInfo.InvariantCulture) < 22);
    if (gStaleOk) Console.WriteLine("PASS ST-V5b 旧口径 G1..G24 范围写法被 <22 过滤捕获");
    else failures.Add($"ST-V5b: gStale={string.Join(",", gStale)}");

    // ── 汇总 ──
    Console.WriteLine($"═══════ VERIFY-AI SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var f in failures) Console.WriteLine("  FAIL " + f);
    return failures.Count == 0 ? 0 : 1;
}
  

// V25 selftest 样本（类型声明须位于顶层语句之后——top-level program 规则）
internal static class V25Samples
{
    // 坏样本：三类失实各一 + 历史标记两行 + 正常行一
    internal static readonly string[] Bad =
    [
        "用法：bash scripts/verify-ai.cs",                 // ① bash 跑 .cs → 抓
        "见 `.ai/scripts/gate-check.sh` 说明",             // ② 已删 .sh → 抓
        "运行 `scripts/verify-conventions.sh`",            // ②' 根 .sh 不存在 → 抓
        "勘正：原写 `bash scripts/gate.cs`（已改）",        // 历史标记 → 放行
        "反例：当时 `scripts/publish-main.sh` 强推 main",   // 反例记录 → 放行
        "正确形态：dotnet run scripts/verify-ai.cs",        // 正常行 → 放行
    ];

    // 干净样本：正常形态 + 历史标记（应零检出）
    internal static readonly string[] Clean =
    [
        "dotnet run scripts/verify-ai.cs",
        "勘正：原写 bash scripts/gate.cs",
        "反例：当时 .ai/scripts/gate-check.sh 强推",
    ];
}
