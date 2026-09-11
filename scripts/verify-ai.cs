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

// ─── 仓库根定位（见头注释迁移说明 3）───
var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var lines = new List<string>
{
    "═══════ .ai 系统一致性校验（Pal.DDD · V1-V23）═══════",
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

// ─── V7 误判知识库双源口径（完整版 ≥37 条；速版覆盖到完整版最大 PD 号）───
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
    if (kbFull >= 37 && kbQuickMax >= kbMaxBash)
    {
        passed++; lines.Add($"PASS V7: 误判知识库完整（完整版 {kbFull} 条 · 速版覆盖至 PD{kbQuickMax}=最大号）");
    }
    else
    {
        failed++; lines.Add($"FAIL V7: 误判知识库不完整（完整版 {kbFull} 条(需≥37) · 速版至 PD{kbQuickMax}(需≥PD{(fullNums.Count > 0 ? kbMax.ToString() : "?")})——防速版断链）");
    }
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
{
    var text = TryReadText("docs/test-coverage-baseline.md");
    var covLine = Regex.Match(text, "[0-9]+\\.[0-9]+%").Value;
    if (File.Exists("docs/test-coverage-baseline.md")
        && Regex.IsMatch(text, "Line coverage|行覆盖率")
        && Regex.IsMatch(text, "门禁|threshold", RegexOptions.IgnoreCase))
    {
        passed++; lines.Add($"PASS V14: 覆盖率基线存在（{covLine}）");
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
    var v17Bad = new StringBuilder();
    var rows = 0;
    string? prev = null;
    foreach (var line in range)
    {
        // bash case \|*\|：以 | 开头且以 | 结尾的表格行
        if (line.Length < 2 || line[0] != '|' || line[^1] != '|') continue;
        var parts = line.Split('|');
        var d2 = parts.Length > 1 ? parts[1].Replace(" ", "") : "";
        var d3 = parts.Length > 2 ? parts[2].Replace(" ", "") : "";
        var d = Regex.IsMatch(d2, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$") ? d2 : d3;
        if (!Regex.IsMatch(d, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$")) continue;
        rows++;
        var pipes = line.Count(c => c == '|');
        if (pipes < 10) v17Bad.Append($" {d}:列数不足({pipes})");
        if (prev is not null && string.CompareOrdinal(d, prev) < 0)
            v17Bad.Append($" {d}:乱序(前值{prev})");
        prev = d;
    }
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

// ─── V19 传感器台账超期校验（90 天周期，DateTime 计算）───
{
    var ledger = TryReadLines(".ai/gate/sensor-ledger.md");
    var v19Bad = new StringBuilder();
    var unknown = 0;
    foreach (var line in ledger)
    {
        var parts = line.Split('|');
        if (parts.Length >= 6 && parts[5].Trim(' ', '\t', '\r') == "UNKNOWN") unknown++;
        if (parts.Length < 7) continue;
        var name = parts[1].Trim(' ', '\t', '\r');
        var status = parts[5].Trim(' ', '\t', '\r');
        if (status is not ("OK" or "观察中")) continue;
        var ds = parts[6].Trim(' ', '\t', '\r');
        // awk：日期前缀匹配（无 $ 锚定）即提取前 10 位做日历校验
        if (!Regex.IsMatch(ds, "^[0-9]{4}-[0-9]{2}-[0-9]{2}"))
        {
            v19Bad.Append(name).Append("：日期缺失/非法(").Append(ds).Append(")\n");
            continue;
        }
        var date = ParseDate(ds[..10]);
        if (date is null)
        {
            v19Bad.Append(name).Append("：日期缺失/非法(").Append(ds.AsSpan(0, 10)).Append(")\n");
            continue;
        }
        var age = (DateTime.UtcNow.Date - date.Value).Days;
        if (age > 90)
            v19Bad.Append(name).Append("：定标超期 ").Append(age).Append(" 天(")
                .Append(ds.AsSpan(0, 10)).Append(")→STALE\n");
    }
    if (File.Exists(".ai/gate/sensor-ledger.md") && v19Bad.Length == 0)
    {
        passed++; lines.Add($"PASS V19: 传感器台账定标有效（UNKNOWN {unknown} 处待定标，90 天周期）");
    }
    else
    {
        failed++; lines.Add($"FAIL V19: 传感器台账超期/异常（{(v19Bad.Length > 0 ? v19Bad.ToString() : "台账缺失")}）");
    }
}

// ─── V21 P3 账本老化校验（30 天周期，DateTime 计算）───
{
    var v21Bad = new StringBuilder();
    foreach (var line in TryReadLines(".ai/review/action-items-p3-backlog.md"))
    {
        if (!line.StartsWith("- [ ] P3-", StringComparison.Ordinal)) continue;
        var m = Regex.Match(line, "[0-9]{4}-[0-9]{2}-[0-9]{2}");
        if (!m.Success) continue;
        var prefix = line.Length > 6 ? line.Substring(6, Math.Min(16, line.Length - 6)) : "";
        var date = ParseDate(m.Value);
        if (date is null)
        {
            v21Bad.Append(prefix).Append("…：日期非法(").Append(m.Value).Append(")\n");
            continue;
        }
        var age = (DateTime.UtcNow.Date - date.Value).Days;
        if (age > 30 && !line.Contains("过期"))
            v21Bad.Append(prefix).Append("…：未勾选已 ").Append(age).Append(" 天(").Append(m.Value)
                .Append(")，按规则应升 P2\n");
    }
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
    var dup = new SortedSet<string>();
    var seen = new Dictionary<string, string>();
    string? ch = null;
    foreach (var line in TryReadLines(".ai/lessons.md"))
    {
        if (Regex.IsMatch(line, "^## [IVX]+\\."))
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
