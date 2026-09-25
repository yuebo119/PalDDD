// ============================================================================
// count-audit.cs——文档计数声明审计（真源 = 推导表，不建第二台账）
//
// 动机：2026-09-25 全仓文档清扫实证计数静默漂移——诊断计数器 21→23、
// 测试用例 1379→1490、断言点 89→99、prompt 模板 9→10、ADR 22→24，
// 这些数字大半能从源码/文件系统机械推导，却只能靠 165 次人工工具调用才
// 找全。本脚本把「推导」做成门禁：文档里**写了**的数字必须与推导真值一致，
// 写错即红（声明缺失不算错——PD34 去数字化口径）。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/count-audit.cs                # 正常审计
//   dotnet run scripts/count-audit.cs -- --selftest  # 自测（判定逻辑纯函数验证）
//
// 退出码：0 = 全通过
//         1 = 有声明失实或推导失败（含仓库根定位失败、扫描面零声明 fail-closed）
//         2 = 自测失败
//
// 设计原则：
//   1. 单一真源 = 推导：脚本内 16 个推导项，全部从文件系统/源码文本机械计数；
//      不维护任何第二台账/清单文件——台账必然与真源漂移，这正是要治的病。
//   2. fail-closed：任一推导取不到值（0 项、依赖文件缺失、异常）→ 整体 FAIL，
//      不放行。防「判定恒真」式假绿：推导不出来时直接报错，而不是跳过后报绿。
//   3. 扫描面 = README.md + README.en.md + docs/**，**排除** docs/review/**
//      docs/decisions/** docs/migration/** docs/design/**（带日期的历史记录，
//      数字属于当时的事实，不随现实改写）。
//   4. 不纳入本门禁（声明在 gateForms 覆盖面里）：
//      · 测试用例数（1502/1490/1379）——需 dotnet test 实测，非静态可推导；
//      · 架构测试方法数 37——文本 grep 有注释假命中，已由
//        DocConsistencyGateTests D12a 反射锚守卫（不重复建防线）；
//      · pitfalls 分章统计行、CHANGELOG 历史数字、PAL 子系列区间
//        （PALID001-007 等）——由总数项间接覆盖或属历史快照。
//
// 推导表（名称 | 推导表达式 | 期望被声明的文档面）：
//   可打包包数            | 全仓 *.csproj 中不含 IsPackable>false 者       | README*.md、docs/release.md
//   src 项目数            | src/**/*.csproj 计数                           | README*.md、docs/conventions.md、docs/architecture.md
//   测试项目数            | test/**/*.Tests.csproj 计数                    | README*.md、conventions、release、tutorial、performance、test-coverage-baseline
//   prompt 模板数         | src/**/*.prompt.md 计数                        | docs/architecture.md、docs/conventions.md
//   prompt 目录文件数     | src/**/.pal/prompts 目录直系文件计数           | docs/architecture.md
//   遥测计数器数          | PalDiagnostics.cs 非注释行 Meter.Create*< 计数 | README*.md、docs/tutorial.md
//   Activity Start 方法数 | PalDiagnostics.cs 非注释行 public static Activity? Start 计数 | README*.md、docs/tutorial.md
//   诊断 ID 总数          | src/**.cs 引号内 (PDDD|PALID|PALMSG|PALENUM)NNN 去重 | README*.md、conventions、tutorial
//   PDDD 分析器诊断数     | 同上前缀 PDDD 去重                             | README*.md、conventions、tutorial、testing、development
//   源生成器诊断数        | 同上 PALID/PALMSG/PALENUM 前缀去重             | README*.md、conventions
//   ADR 数                | docs/decisions/*.md 计数                       | README*.md、docs/pitfalls.md
//   踩坑一至九章条目数    | pitfalls.md 表 `| **X1** |` 行计数             | docs/pitfalls.md（统计块与合计行）
//   踩坑第十章条目数      | pitfalls.md 表 `| PALORM-* |` 行计数           | docs/pitfalls.md
//   踩坑总条目数          | 上两项之和                                     | README*.md、docs/pitfalls.md
//   架构边界断言点数      | ArchitectureBoundaryTests.cs await Assert.That 计数 | docs/conventions.md、docs/testing.md
//   src 源文件数          | src/**/*.cs 计数（排除 obj/bin）               | docs/conventions.md
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是门禁的
// 固定协议行（推导表/FAIL 明细被人工与 grep 消费），固定中文非用户可配
// 文案——沿 secret-scan.cs / gate-audit.cs 先例整文件抑制。
#pragma warning disable CA1303

// Justification: CA1031 禁止宽泛 catch；推导执行器（Add）的定位是「任何
// 异常都不得逃逸掩盖结论」——依赖文件缺失、IO 失败必须转成 FAIL 明细，
// 这正是 fail-closed 声明的语义（异常即结论，非吞异常）。限定在推导执行器内。
#pragma warning disable CA1031

using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

// ─── 参数路由 ───
if (args.Contains("--selftest"))
{
    return SelfTest();
}

var root = FindRepoRoot();
if (root is null)
{
    Console.Error.WriteLine("FAIL count-audit：仓库根定位失败（向上未找到 PalDDD.slnx）——推导失败 fail-closed");
    return 1;
}

// ─── 推导：16 项真值，每项独立执行，失败即入清单（不中断其余项的报告）───
var items = new List<Item>();
var deriveFailures = new List<string>();

void Add(string key, Func<int> derive)
{
    var meta = MetaFor(key);
    int truth;
    try
    {
        truth = derive();
    }
    catch (Exception ex)
    {
        deriveFailures.Add($"{meta.Label}：推导异常 {ex.GetType().Name}：{ex.Message}（{meta.How}）");
        return;
    }

    var guard = GuardTruth(meta.Label, truth);
    if (guard is not null)
    {
        deriveFailures.Add($"{guard}（{meta.How}）");
        return;
    }

    var patterns = new Regex[meta.Patterns.Length];
    for (var i = 0; i < patterns.Length; i++)
    {
        patterns[i] = new Regex(meta.Patterns[i], RegexOptions.CultureInvariant);
    }

    items.Add(new Item(key, meta.Label, meta.How, meta.Surface, truth, patterns));
}

Add("packable", () => DerivePackable(root));
Add("src-projects", () => DeriveSrcProjects(root));
Add("test-projects", () => DeriveTestProjects(root));
Add("prompt-templates", () => DerivePromptTemplates(root));
Add("prompt-dirs", () => DerivePromptDirFiles(root));
Add("instruments", () => DeriveInstruments(root));
Add("starts", () => DeriveStartMethods(root));
Add("diag-total", () => DeriveDiagCount(root, "all"));
Add("diag-pddd", () => DeriveDiagCount(root, "pddd"));
Add("diag-gen", () => DeriveDiagCount(root, "gen"));
Add("adr", () => DeriveAdrCount(root));
Add("pitfalls-19", () => CountBoldIdRows(ReadPitfalls(root)));
Add("pitfalls-10", () => CountCh10Rows(ReadPitfalls(root)));
Add("pitfalls-total", () => CountBoldIdRows(ReadPitfalls(root)) + CountCh10Rows(ReadPitfalls(root)));
Add("asserts", () => DeriveAssertions(root));
Add("src-files", () => DeriveSrcFiles(root));

Console.WriteLine("=== count-audit 计数声明审计（真源 = 推导表，不建第二台账）===");
Console.WriteLine($"仓库根：{root}");
Console.WriteLine("--- 推导表（真值供人工核对；0 值/异常/依赖文件缺失即整体 FAIL）---");
for (var i = 0; i < items.Count; i++)
{
    var item = items[i];
    Console.WriteLine($"[{i + 1:00}] {item.Label} = {item.Truth}｜{item.How}｜声明面：{item.Surface}");
}

foreach (var failure in deriveFailures)
{
    Console.WriteLine($"FAIL 推导 {failure}");
}

if (deriveFailures.Count > 0)
{
    Console.WriteLine($"FAIL count-audit：{deriveFailures.Count} 项推导不可用——fail-closed 整体不放行");
    return 1;
}

// ─── 扫描：声明面逐行核对（声明缺失不算错，写了必须真）───
var surface = ScanSurface(root).ToList();
var claims = new List<Claim>();
foreach (var file in surface)
{
    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
    var lines = File.ReadAllLines(file);
    foreach (var item in items)
    {
        claims.AddRange(ScanFile(rel, lines, item));
    }
}

Console.WriteLine($"--- 扫描面 {surface.Count} 文件（README.md / README.en.md / docs/** 排除 review·decisions·migration·design）---");

foreach (var claim in claims.Where(c => !c.Correct))
{
    Console.WriteLine($"FAIL {claim.File}:{claim.Line}：声明 {claim.Text} vs 推导 {claim.Truth}（{claim.Label}）");
}

var wrong = claims.Count(c => !c.Correct);
if (wrong > 0)
{
    Console.WriteLine($"FAIL count-audit：{wrong} 处声明失实（{items.Count} 项推导 · {surface.Count} 文件 · {claims.Count} 处声明核对）");
    return 1;
}

if (!ClaimsUsable(claims.Count))
{
    Console.WriteLine("FAIL count-audit：扫描面 0 处计数声明——模式失配或扫描面为空（fail-closed 防「判定恒真」）");
    return 1;
}

Console.WriteLine($"PASS count-audit：{items.Count} 项推导 · {surface.Count} 文件 · {claims.Count} 处声明全一致");
return 0;

// ══════════════ 自测（纯函数，不触文件系统）══════════════
// 正例：真值一致的声明不报错；负例：注入假声明/漂移真值必须报红。
static int SelfTest()
{
    var passed = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}");
    }

    static Item ItemFor(string key, int truth)
    {
        var meta = MetaFor(key);
        var patterns = new Regex[meta.Patterns.Length];
        for (var i = 0; i < patterns.Length; i++)
        {
            patterns[i] = new Regex(meta.Patterns[i], RegexOptions.CultureInvariant);
        }

        return new Item(key, meta.Label, meta.How, meta.Surface, truth, patterns);
    }

    // ── fail-closed 守卫 ──
    Case("GuardTruth 正例（35 → null 放行）", GuardTruth("可打包包数", 35) is null);
    Case("GuardTruth 负例（0 → 报错，防判定恒真）", GuardTruth("可打包包数", 0) is not null);
    Case("GuardTruth 负例（-3 → 报错）", GuardTruth("任意项", -3) is not null);
    Case("ClaimsUsable(0) → false（零声明防空转）", !ClaimsUsable(0));
    Case("ClaimsUsable(7) → true", ClaimsUsable(7));

    // ── 扫描面排除 ──
    Case("IsExcludedDoc 排除四个历史目录",
        IsExcludedDoc("docs/review/x.md") && IsExcludedDoc("docs/decisions/y.md")
        && IsExcludedDoc("docs/migration/z.md") && IsExcludedDoc("docs/design/w.md"));
    Case("IsExcludedDoc 放行 README 与 pitfalls 等现行文档",
        !IsExcludedDoc("README.md") && !IsExcludedDoc("docs/pitfalls.md") && !IsExcludedDoc("docs/sql/x.md"));

    // ── packable 声明模式 ──
    string[] packableOk = ["标准化为 35 个独立 NuGet 包。"];
    var packableOkHits = ScanFile("t.md", packableOk, ItemFor("packable", 35));
    Case("packable 正例（35 声明 = 35 真值）", packableOkHits.Count == 1 && packableOkHits[0].Correct);

    string[] packableBad = ["标准化为 36 个独立 NuGet 包。"];
    var packableBadHits = ScanFile("t.md", packableBad, ItemFor("packable", 35));
    Case("packable 负例（注入假声明 36 → 必红，报「声明 36 vs 推导 35」）",
        packableBadHits.Count == 1 && !packableBadHits[0].Correct
        && packableBadHits[0].Text == "36" && packableBadHits[0].Truth == 35);

    string[] packableDedupe = ["PalDDD 自有 35 个独立 NuGet 包 + 5 个第三方包。"];
    Case("packable 同行两模式命中同数字 → 去重为 1 声明",
        ScanFile("t.md", packableDedupe, ItemFor("packable", 35)).Count == 1);

    string[] packableHuge = ["本库以 99999999999 个独立 NuGet 包发布。"];
    var packableHugeHits = ScanFile("t.md", packableHuge, ItemFor("packable", 35));
    Case("packable 超长数字解析失败 → 按失实处理（不抛异常）",
        packableHugeHits.Count == 1 && !packableHugeHits[0].Correct);

    // ── ADR 模式（lookbehind 挡段号）──
    string[] adrSection = ["### 6.2 ADR 模板（doc 设计）"];
    Case("adr 负例（段号 6.2 的 2 不被误抓为声明）",
        ScanFile("t.md", adrSection, ItemFor("adr", 24)).Count == 0);

    string[] adrOk = ["已沉淀 24 份 ADR，另见 docs/decisions/001-024。"];
    var adrOkHits = ScanFile("t.md", adrOk, ItemFor("adr", 24));
    Case("adr 正例（24 份 ADR 与 001-024 两形态 → 各 1 且全对）",
        adrOkHits.Count == 2 && adrOkHits.All(c => c.Correct));

    string[] adrBad = ["已沉淀 22 份 ADR。"];
    var adrBadHits = ScanFile("t.md", adrBad, ItemFor("adr", 24));
    Case("adr 负例（22 vs 24 → 红）", adrBadHits.Count == 1 && !adrBadHits[0].Correct);

    // ── 诊断模式（负向 lookahead 挡「诊断规则」）──
    string[] diagRule = ["审查器共 15 条诊断规则。"];
    Case("diag-total 负例（「15 条诊断规则」不计入 38 项）",
        ScanFile("t.md", diagRule, ItemFor("diag-total", 38)).Count == 0);

    var diagOkHits = ScanFile("t.md", diagRule, ItemFor("diag-pddd", 15));
    Case("diag-pddd 正例（同句 15 = 15 → 对）", diagOkHits.Count == 1 && diagOkHits[0].Correct);

    var diagBadHits = ScanFile("t.md", diagRule, ItemFor("diag-pddd", 16));
    Case("diag-pddd 负例（真值漂移 16 ≠ 声明 15 → 红）",
        diagBadHits.Count == 1 && !diagBadHits[0].Correct);

    // ── 断言点模式（lookahead 挡 step 断言动词）──
    string[] releaseLine = ["§6.2 step 7 断言 nupkg 数量。"];
    Case("asserts 负例（「step 7 断言 nupkg」是动词断言，不命中）",
        ScanFile("t.md", releaseLine, ItemFor("asserts", 99)).Count == 0);

    string[] pyramid = ["│ 37 方法 99 断言  ││"];
    var pyramidHits = ScanFile("t.md", pyramid, ItemFor("asserts", 99));
    Case("asserts 正例（37 方法 99 断言 → 只抓 99 且对）",
        pyramidHits.Count == 1 && pyramidHits[0].Text == "99" && pyramidHits[0].Correct);

    var pyramidBadHits = ScanFile("t.md", pyramid, ItemFor("asserts", 98));
    Case("asserts 负例（真值 98 ≠ 声明 99 → 红）",
        pyramidBadHits.Count == 1 && !pyramidBadHits[0].Correct);

    // ── 测试项目模式（挡 EFCore「（5 项目）」旁支计数）──
    string[] efcore = ["| **PalDDD.*.EFCore**（5 项目） |"];
    Case("test-projects 负例（「（5 项目）」不误抓为 16）",
        ScanFile("t.md", efcore, ItemFor("test-projects", 16)).Count == 0);

    string[] sixteen = ["1502 项实测（16 项目：1434 通过）"];
    var sixteenHits = ScanFile("t.md", sixteen, ItemFor("test-projects", 16));
    Case("test-projects 正例（（16 项目：）命中 16 且对）",
        sixteenHits.Count == 1 && sixteenHits[0].Text == "16" && sixteenHits[0].Correct);

    // ── prompt 双形态（模板数与目录文件数各归其项）──
    string[] promptLine = ["9 个 `.prompt.md` 文件 + 1 个 `README.md`，共 10 份。"];
    var promptTemplateHits = ScanFile("t.md", promptLine, ItemFor("prompt-templates", 9));
    var promptDirHits = ScanFile("t.md", promptLine, ItemFor("prompt-dirs", 10));
    Case("prompt 行双项各自命中（9 归模板项、10 归目录项）",
        promptTemplateHits.Count == 1 && promptTemplateHits[0].Text == "9"
        && promptDirHits.Count == 1 && promptDirHits[0].Text == "10");

    // ── src 项目数多形态 ──
    string[] srcLine = ["├── src/    # 36 个源项目，Clean Architecture 分层"];
    var srcHits = ScanFile("t.md", srcLine, ItemFor("src-projects", 36));
    Case("src-projects 正例（36 个源项目 → 对）", srcHits.Count == 1 && srcHits[0].Correct);

    // ── 踩坑统计行三形态（第一至九章 / 另有 / 全文共）──
    string[] pitLine = ["第一至九章共 66 条；第十章 PalORM 适配层另有 16 条（PALORM-SG1-5），全文共 82 条。"];
    var pit19Hits = ScanFile("t.md", pitLine, ItemFor("pitfalls-19", 66));
    var pit10Hits = ScanFile("t.md", pitLine, ItemFor("pitfalls-10", 16));
    var pitTotalHits = ScanFile("t.md", pitLine, ItemFor("pitfalls-total", 82));
    Case("踩坑统计行三形态各 1 且全对",
        pit19Hits.Count == 1 && pit19Hits[0].Correct
        && pit10Hits.Count == 1 && pit10Hits[0].Correct
        && pitTotalHits.Count == 1 && pitTotalHits[0].Correct);

    string[] pitWrong = ["第一至九章共 65 条。"];
    var pitWrongHits = ScanFile("t.md", pitWrong, ItemFor("pitfalls-19", 66));
    Case("踩坑负例（声明 65 vs 推导 66 → 红）",
        pitWrongHits.Count == 1 && !pitWrongHits[0].Correct);

    string[] pitTotalRow = ["| **合计** | **66** | — |"];
    var pitRowHits = ScanFile("t.md", pitTotalRow, ItemFor("pitfalls-19", 66));
    Case("踩坑合计表行模式（**合计** | **66** → 66）",
        pitRowHits.Count == 1 && pitRowHits[0].Text == "66" && pitRowHits[0].Correct);

    // ── 多项同行：各自命中、单项漂移只红该项 ──
    string[] mixed = ["38 条编译期诊断中，15 战略分析器贡献主要规则。"];
    var mixedTotal = ScanFile("t.md", mixed, ItemFor("diag-total", 38));
    var mixedPddd = ScanFile("t.md", mixed, ItemFor("diag-pddd", 15));
    Case("多项同行（38 诊断 + 15 分析器）各自命中且对",
        mixedTotal.Count == 1 && mixedTotal[0].Correct
        && mixedPddd.Count == 1 && mixedPddd[0].Correct);

    var mixedDrift = ScanFile("t.md", mixed, ItemFor("diag-pddd", 14));
    Case("单项漂移只红该项（声明 15 vs 推导 14）",
        mixedDrift.Count == 1 && !mixedDrift[0].Correct && mixedDrift[0].Label == "PDDD 分析器诊断数");

    // ── 无声明行（声明缺失不算错）──
    string[] plain = ["这行没有任何计数声明。"];
    Case("无声明行 → 0 声明（不算错）", ScanFile("t.md", plain, ItemFor("packable", 35)).Count == 0);

    // ── 纯推导函数（文件文本 → 计数）──
    var pitSample = """
        | # | 场景 | 设计 | 状态 |
        |---|------|------|-----|
        | **E1** | 行一 | x | ✅ |
        | **T2** | 行二 | x | ✅ |
        | PALORM-SG1 | 十章行 | x | ⚠️ |
        """;
    Case("CountBoldIdRows 样本 → 2", CountBoldIdRows(pitSample) == 2);
    Case("CountCh10Rows 样本 → 1", CountCh10Rows(pitSample) == 1);
    Case("踩坑合计 = 2 + 1 = 3", CountBoldIdRows(pitSample) + CountCh10Rows(pitSample) == 3);

    var csSample = """
        using System.Diagnostics.Metrics;
        internal static class PalDiagnostics
        {
            /// <summary>见 <c>Meter.CreateCounter</c> 文档</summary>
            public static readonly Counter<long> C = Meter.CreateCounter<long>("probe");
            public static Activity? StartFoo() => null;
        }
        """;
    Case("CountInstruments 跳过 /// 注释行只计真行 → 1", CountInstruments(csSample) == 1);
    Case("CountInstruments 纯注释 → 0（推导侧将 fail-closed）",
        CountInstruments("/// Meter.CreateCounter<long>(\"probe\");") == 0);
    Case("CountStartMethods → 1", CountStartMethods(csSample) == 1);
    Case("CountStartMethods 无 Activity 声明 → 0", CountStartMethods("public static int StartFoo() => 0;") == 0);

    const string diagSample = "var a = \"PDDD001\"; var b = \"PDDD001\"; var c = \"PALMSG004\";";
    Case("ExtractDiagIds 去重 → 2", ExtractDiagIds(diagSample).Length == 2);
    Case("ExtractDiagIds 无 ID → 0", ExtractDiagIds("var x = 1;").Length == 0);

    Case("CountAssertPoints → 2", CountAssertPoints("await Assert.That(1); await Assert.That(2);") == 2);
    Case("CountAssertPoints 无匹配 → 0", CountAssertPoints("Assert.Equal(1, 2);") == 0);

    Console.WriteLine($"SELFTEST 汇总 {passed}/{total} 通过");
    return passed == total ? 0 : 2;
}

// ══════════════ 仓库根定位（CWD 优先向上，探针在隔离目录直接跑）══════════════
static string? FindRepoRoot()
{
    string[] starts = [Environment.CurrentDirectory, AppContext.BaseDirectory];
    foreach (var start in starts)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PalDDD.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }
    }

    return null;
}

// ══════════════ fail-closed 守卫 ══════════════
static string? GuardTruth(string label, int truth) => truth > 0 ? null : $"{label}：推导值 {truth} ≤ 0";

static bool ClaimsUsable(int claimCount) => claimCount > 0;

// ══════════════ 推导项元数据（名称 | 推导表达式 | 声明面 | 声明模式）══════════════
// 声明模式统一形态：数字 + 关键词；`(?<![0-9.])` 挡段号/版本号（6.2、3.0），
// 负向 lookahead 挡动词同形（「诊断规则」的规则、「step 7 断言」的断言）。
static (string Label, string How, string Surface, string[] Patterns) MetaFor(string key) => key switch
{
    "packable" => (
        "可打包包数",
        "全仓 *.csproj 中不含 IsPackable>false 者",
        "README*.md、docs/release.md",
        [
            @"(?<![0-9.])(\d+)\s*个独立\s*NuGet\s*包",
            @"自有\s*(?<![0-9.])(\d+)\s*个",
            @"(?<![0-9.])(\d+)\s*independent NuGet packages",
            @"(?<![0-9.])(\d+)\s*PalDDD packages",
            @"应等于\s*(?<![0-9.])(\d+)",
            @"包(?:数量|数)断言\s*(?<![0-9.])(\d+)",
        ]),
    "src-projects" => (
        "src 项目数",
        "src/**/*.csproj 计数",
        "README*.md、docs/conventions.md、docs/architecture.md",
        [
            @"(?<![0-9.])(\d+)\s*个?源项目",
            @"(?<![0-9.])(\d+)\s*source projects",
            @"源文件·(?<![0-9.])(\d+)\s*项目",
            @"(?<=/)(\d+)\s*项目遵循",
            @"合规状态\*{0,2}：(?<![0-9.])(\d+)\s*项目",
        ]),
    "test-projects" => (
        "测试项目数",
        "test/**/*.Tests.csproj 计数",
        "README*.md、conventions、release、tutorial、performance、test-coverage-baseline",
        [
            @"(?<![0-9.])(\d+)\s*个?测试项目",
            @"(?<![0-9.])(\d+)\s*test projects",
            @"（(?<![0-9.])(\d+)\s*项目[：，]",
            @"(?<![0-9.])(\d+)\s*项目\s+\d+\s*用例",
            @"(?<![0-9.])(\d+)\s*projects\s*:",
        ]),
    "prompt-templates" => (
        "prompt 模板数",
        "src/**/*.prompt.md 计数",
        "docs/architecture.md、docs/conventions.md",
        [
            @"(?<![0-9.])(\d+)\s*个\s*`?\.prompt\.md",
            @"(?<![0-9.])(\d+)\s*个模板",
        ]),
    "prompt-dirs" => (
        "prompt 目录文件数",
        "src/**/.pal/prompts 目录直系文件计数",
        "docs/architecture.md",
        [@"README\.md`，共\s*(?<![0-9.])(\d+)\s*份"]),
    "instruments" => (
        "遥测计数器数",
        "PalDiagnostics.cs 非注释行 Meter.Create*< 计数",
        "README*.md、docs/tutorial.md",
        [
            @"(?<![0-9.])(\d+)\s*个遥测\s*instruments?",
            @"(?<![0-9.])(\d+)\s*telemetry instruments",
            @"(?<![0-9.])(\d+)\s*个预定义指标",
            @"(?<![0-9.])(\d+)\s*个 OpenTelemetry 指标",
            @"(?<![0-9.])(\d+)\s*个指标",
        ]),
    "starts" => (
        "Activity Start 方法数",
        "PalDiagnostics.cs 非注释行 public static Activity? Start 计数",
        "README*.md、docs/tutorial.md",
        [
            @"(?<![0-9.])(\d+)\s*个\s*Start\s*方法",
            @"(?<![0-9.])(\d+)\s*Start methods",
            @"(?<![0-9.])(\d+)\s*种 Activity",
        ]),
    "diag-total" => (
        "诊断 ID 总数",
        "src/**.cs 引号内 (PDDD|PALID|PALMSG|PALENUM)NNN 去重",
        "README*.md、docs/conventions.md、docs/tutorial.md",
        [
            @"(?<![0-9.])(\d+)\s*条(?:编译期)?诊断(?!\s*规则)",
            @"(?<![0-9.])(\d+)\s*compile-time diagnostics",
        ]),
    "diag-pddd" => (
        "PDDD 分析器诊断数",
        "src/**.cs 引号内 PDDD 前缀 ID 去重",
        "README*.md、conventions、tutorial、testing、development",
        [
            @"(?<![0-9.])(\d+)\s*条战略\s*Roslyn\s*分析器",
            @"(?<![0-9.])(\d+)\s*战略分析器",
            @"(?<![0-9.])(\d+)\s*条(?:编译期|诊断)?规则",
            @"(?<![0-9.])(\d+)\s*strategic Roslyn analyzers",
            @"PDDD001-(\d+)",
        ]),
    "diag-gen" => (
        "源生成器诊断数",
        "src/**.cs 引号内 PALID/PALMSG/PALENUM 前缀 ID 去重",
        "README*.md、docs/conventions.md",
        [
            @"(?<![0-9.])(\d+)\s*条?源生成器诊断",
            @"(?<![0-9.])(\d+)\s*条输入契约诊断",
            @"(?<![0-9.])(\d+)\s*source-generator diagnostics",
        ]),
    "adr" => (
        "ADR 数",
        "docs/decisions/*.md 计数",
        "README*.md、docs/pitfalls.md",
        [
            @"(?<![0-9.])(\d+)\s*份\s*ADRs?\b",
            @"(?<![0-9.])(\d+)\s*ADRs?\b",
            @"decisions/001-(\d+)",
        ]),
    "pitfalls-19" => (
        "踩坑一至九章条目数",
        "pitfalls.md 表 | **X1** | 行计数",
        "docs/pitfalls.md（统计块与合计行）",
        [
            @"第一至九章共\s*(?<![0-9.])(\d+)\s*条",
            @"\*\*合计\*\*\s*\|\s*\*\*(?<![0-9.])(\d+)\*\*",
        ]),
    "pitfalls-10" => (
        "踩坑第十章条目数",
        "pitfalls.md 表 | PALORM-* | 行计数",
        "docs/pitfalls.md",
        [@"(?<![0-9.])(\d+)\s*条（PALORM"]),
    "pitfalls-total" => (
        "踩坑总条目数",
        "上两项之和",
        "README*.md、docs/pitfalls.md",
        [
            @"全文共\s*(?<![0-9.])(\d+)\s*条",
            @"(?<![0-9.])(\d+)\s*条 DDD/AOT/并发实战踩坑",
            @"(?<![0-9.])(\d+)\s*real-world DDD/AOT/concurrency pitfalls",
        ]),
    "asserts" => (
        "架构边界断言点数",
        "ArchitectureBoundaryTests.cs 中 await Assert.That 计数",
        "docs/conventions.md、docs/testing.md",
        [
            @"(?<![0-9.])(\d+)\s*断言点",
            @"(?<![0-9.])(\d+)\s*断言(?=\s*[│|])",
        ]),
    "src-files" => (
        "src 源文件数",
        "src/**/*.cs 计数（排除 obj/bin）",
        "docs/conventions.md",
        [@"(?<![0-9.])(\d+)\s*源文件"]),
    _ => throw new ArgumentException($"未知推导项 {key}"),
};

// ══════════════ 文件系统遍历（跳过生成物目录——runfile 产物落在 CWD 相对 dotnet/）══════════════
static IEnumerable<string> AllFiles(string root, string searchPattern)
{
    var skip = SkipDirs();
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.Count > 0)
    {
        var dir = pending.Pop();
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!skip.Contains(Path.GetFileName(sub)))
            {
                pending.Push(sub);
            }
        }

        foreach (var file in Directory.EnumerateFiles(dir, searchPattern))
        {
            yield return file;
        }
    }
}

static IEnumerable<string> AllDirs(string root)
{
    var skip = SkipDirs();
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.Count > 0)
    {
        var dir = pending.Pop();
        yield return dir;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!skip.Contains(Path.GetFileName(sub)))
            {
                pending.Push(sub);
            }
        }
    }
}

static HashSet<string> SkipDirs() =>
[
    "obj", "bin", ".git", "dotnet", "node_modules", "TestResults",
    ".vs", ".serena", ".probe-dump", "BenchmarkDotNet.Artifacts",
];

// ══════════════ 推导（依赖文件缺失即抛 → fail-closed）══════════════
static List<string> RequireFiles(string root, string subdir, string fileName, string why)
{
    var found = AllFiles(Path.Combine(root, subdir), fileName).ToList();
    if (found.Count == 0)
    {
        throw new FileNotFoundException($"{subdir}/ 下未找到 {fileName}（{why}的依赖文件）");
    }

    return found;
}

static int DerivePackable(string root)
{
    var n = 0;
    foreach (var file in AllFiles(root, "*.csproj"))
    {
        if (!Regex.IsMatch(File.ReadAllText(file), @"IsPackable>\s*false"))
        {
            n++;
        }
    }

    return n;
}

static int DeriveSrcProjects(string root) => AllFiles(Path.Combine(root, "src"), "*.csproj").Count();

static int DeriveTestProjects(string root) =>
    AllFiles(Path.Combine(root, "test"), "*.csproj")
        .Count(f => Path.GetFileName(f).EndsWith(".Tests.csproj", StringComparison.Ordinal));

static int DerivePromptTemplates(string root) =>
    AllFiles(Path.Combine(root, "src"), "*.md")
        .Count(f => f.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase));

static int DerivePromptDirFiles(string root)
{
    var n = 0;
    foreach (var dir in AllDirs(Path.Combine(root, "src")))
    {
        if (!Path.GetFileName(dir).Equals("prompts", StringComparison.Ordinal))
        {
            continue;
        }

        var parent = Path.GetDirectoryName(dir);
        if (parent is not null && Path.GetFileName(parent).Equals(".pal", StringComparison.Ordinal))
        {
            n += Directory.GetFiles(dir).Length;
        }
    }

    return n;
}

static int DeriveInstruments(string root)
{
    var n = 0;
    foreach (var file in RequireFiles(root, "src", "PalDiagnostics.cs", "遥测计数器"))
    {
        n += CountInstruments(File.ReadAllText(file));
    }

    return n;
}

static int DeriveStartMethods(string root)
{
    var n = 0;
    foreach (var file in RequireFiles(root, "src", "PalDiagnostics.cs", "Activity Start 方法"))
    {
        n += CountStartMethods(File.ReadAllText(file));
    }

    return n;
}

static int DeriveDiagCount(string root, string series)
{
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var file in AllFiles(Path.Combine(root, "src"), "*.cs"))
    {
        CollectDiagIds(File.ReadAllText(file), ids);
    }

    return series switch
    {
        "all" => ids.Count,
        "pddd" => ids.Count(id => id.StartsWith("PDDD", StringComparison.Ordinal)),
        "gen" => ids.Count(id => !id.StartsWith("PDDD", StringComparison.Ordinal)),
        _ => throw new ArgumentException($"未知诊断系列 {series}"),
    };
}

static int DeriveAdrCount(string root) =>
    Directory.EnumerateFiles(Path.Combine(root, "docs", "decisions"), "*.md").Count();

static string ReadPitfalls(string root) => File.ReadAllText(Path.Combine(root, "docs", "pitfalls.md"));

static int DeriveAssertions(string root)
{
    var n = 0;
    foreach (var file in RequireFiles(root, "test", "ArchitectureBoundaryTests.cs", "断言点"))
    {
        n += CountAssertPoints(File.ReadAllText(file));
    }

    return n;
}

static int DeriveSrcFiles(string root) => AllFiles(Path.Combine(root, "src"), "*.cs").Count();

// ══════════════ 纯推导核（文本 → 计数；自测直接钉这些函数）══════════════
static bool IsCommentLine(string line)
{
    var trimmed = line.TrimStart();
    return trimmed.StartsWith("//", StringComparison.Ordinal)
        || trimmed.StartsWith("/*", StringComparison.Ordinal)
        || trimmed.StartsWith('*');
}

static int CountInstruments(string csSource)
{
    var n = 0;
    foreach (var line in csSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
    {
        if (IsCommentLine(line))
        {
            continue;
        }

        n += Regex.Count(line, @"Meter\.Create[A-Za-z]+<");
    }

    return n;
}

static int CountStartMethods(string csSource)
{
    var n = 0;
    foreach (var line in csSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
    {
        if (IsCommentLine(line))
        {
            continue;
        }

        n += Regex.Count(line, @"public\s+static\s+Activity\??\s+Start");
    }

    return n;
}

static void CollectDiagIds(string csSource, HashSet<string> sink)
{
    foreach (Match m in Regex.Matches(csSource, "\"(PDDD|PALID|PALMSG|PALENUM)[0-9]{3}\""))
    {
        sink.Add(m.Value.Trim('"'));
    }
}

static string[] ExtractDiagIds(string csSource)
{
    var sink = new HashSet<string>(StringComparer.Ordinal);
    CollectDiagIds(csSource, sink);
    return [.. sink];
}

static int CountBoldIdRows(string pitfallsMd) =>
    Regex.Count(pitfallsMd, @"(?m)^\|\s*\*\*[A-Za-z]+\d+\*\*");

static int CountCh10Rows(string pitfallsMd) =>
    Regex.Count(pitfallsMd, @"(?m)^\|\s*(?:PALORM-(?:SG|RT)\d+|MYSQL\d+|CSHARP\d+|SEC\d+)\s*\|");

static int CountAssertPoints(string csSource) => Regex.Count(csSource, @"await\s+Assert\.That");

// ══════════════ 扫描面与声明核对 ══════════════
static bool IsExcludedDoc(string relPath) =>
    relPath.StartsWith("docs/review/", StringComparison.Ordinal)
    || relPath.StartsWith("docs/decisions/", StringComparison.Ordinal)
    || relPath.StartsWith("docs/migration/", StringComparison.Ordinal)
    || relPath.StartsWith("docs/design/", StringComparison.Ordinal);

static IEnumerable<string> ScanSurface(string root)
{
    string[] readmes = ["README.md", "README.en.md"];
    foreach (var name in readmes)
    {
        var path = Path.Combine(root, name);
        if (File.Exists(path))
        {
            yield return path;
        }
    }

    var docs = Path.Combine(root, "docs");
    if (!Directory.Exists(docs))
    {
        yield break;
    }

    foreach (var file in AllFiles(docs, "*.md"))
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (IsExcludedDoc(rel))
        {
            continue;
        }

        yield return file;
    }
}

static List<Claim> ScanFile(string relPath, string[] lines, Item item)
{
    var result = new List<Claim>();
    for (var i = 0; i < lines.Length; i++)
    {
        // 同行多模式命中同一数字 → 去重（同一声明被两个形态描述不重复计数）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in item.Patterns)
        {
            foreach (Match m in pattern.Matches(lines[i]))
            {
                var text = m.Groups[1].Value;
                if (!seen.Add(text))
                {
                    continue;
                }

                var correct = int.TryParse(text, out var claimed) && claimed == item.Truth;
                result.Add(new Claim(relPath, i + 1, item.Label, text, item.Truth, correct));
            }
        }
    }

    return result;
}

// ══════════════ 类型 ══════════════
internal sealed record Item(string Key, string Label, string How, string Surface, int Truth, Regex[] Patterns);

internal sealed record Claim(string File, int Line, string Label, string Text, int Truth, bool Correct);
