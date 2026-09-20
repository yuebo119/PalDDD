// ============================================================================
// gate-audit.cs——门禁可信度审计（静态接线矩阵 + 隔离式变异探针）
//
// 动机：AGENTS.md「验证验证者（Verifying the Verifier）」要求任何门禁在信任其
// 输出前，必须见过它拒绝坏输入。本仓库已有两次「门禁假绿」实案（vuln-scan v53
// 为 exit-0 no-op；secret-scan 因 pipefail 缺位被 tee 掩码），但两者的修复都是
// 一次性人工探针，未沉淀为可重复的机械验证。本脚本把该动作自动化。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/gate-audit.cs              静态矩阵 + 变异探针（默认）
//   dotnet run scripts/gate-audit.cs -- --inventory  仅静态矩阵（快，不跑探针）
//   dotnet run scripts/gate-audit.cs -- --selftest   自测（判定逻辑单元验证）
//
// 退出码：0=静态矩阵产出且全部探针通过；1=有门禁未拒绝已知坏输入（探针失败）；
//         2=仓库根定位失败。
//
// 矩阵三维（每维度都是「看起来启用 vs 实际可用」的问题）：
//   WIRED   是否被 .githooks/ 或 .github/workflows/ 引用——未接线的门禁即使能
//           拒绝也永远不会触发（观察态门禁，即覆盖率门禁当前的形态）。
//   SELFTEST  是否自带 --selftest——无自证能力的门禁，退化时无人知道。
//   PROBED  本次是否被实际注入过坏输入并确认拒绝。
//
// 探针隔离纪律（2026-09-13 实测教训）：探针仓库建在系统临时目录，且**只显式
// git add 目标文件**——不得用 `git add -A`。原因：file-based app 的 dotnet
// runfile 构建产物会落在 CWD 相对的 dotnet/ 路径，`-A` 会把它一并暂存，使被
// 扫描文件数与预期不符（实测 2 → 32），探针随即不可信。隔离仓库另置
// .gitignore（dotnet/, bin/, obj/）作第二道保险。
//
// 探针必须在真实仓库之外运行：绝不在仓库索引内注入假凭据（P0 #1 防泄露，
// 且避免并行工作时污染他人暂存区）。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是审计门禁的
// 固定协议行（MATRIX/PROBE 被人工与 grep 消费），固定中文非用户可配文案——
// 沿 secret-scan.cs / verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

// Justification: CA1031 禁止宽泛 catch；探针执行的定位是「任何异常都不得逃逸
// 掩盖探针结论」——探针内部异常必须转成 FAIL 明细返回，而非中断整个审计。
// 清理路径同理（临时目录删除失败不得翻转为探针失败）。限定在探针封装内。
#pragma warning disable CA1031

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

// ─── 参数路由 ───
if (args.Contains("--selftest"))
{
    return SelfTest();
}

var inventoryOnly = args.Contains("--inventory");
var root = FindRepoRoot();
var scriptsDir = Path.Combine(root, "scripts");

// ══════════════ 1. 静态矩阵 ══════════════

var gateFiles = Directory.GetFiles(scriptsDir, "*.cs")
    .Select(Path.GetFileNameWithoutExtension)
    .Where(n => n is not null)
    .Select(n => n!)
    .OrderBy(n => n, StringComparer.Ordinal)
    .ToList();

if (gateFiles.Count == 0)
{
    Console.Error.WriteLine($"ERROR: {scriptsDir} 下未发现任何 .cs 门禁脚本");
    return 2;
}

var wiredNames = CollectWiredNames(root);

// 本次实际探测的门禁——矩阵 PROBED 列与下方探针列表由本数组单向对齐，
// 并在跑探针前断言一致（防两处清单漂移，同 E1/E2 目录清单教训）。
string[] probedGates = ["secret-scan", "encoding-gate", "dapper-param-guard", "gate-lite", "verify-conventions"];

// ─── 未接线脚本的分类（2026-09-13 增）───
// 此前矩阵对一切未接线者判「OBSERVE 未接线——永远不触发」，实测 17 个中 16 个是
// **按设计手工调用的工具**（定位是「按需运行并读输出」，非「不通过则阻断」），
// 一律报成问题属虚假告警——17 次狼来了之后，真缺口（ci-coverage）会被淹没。
// 故逐条登记理由。判据：该脚本是否被设计为某个流程中的人工步骤。
var manualTools = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["changelog-facts"] = "发布流程 Phase 1 事实收集，按 need 运行",
    ["changelog-check"] = "发布流程 Phase 4 结构校验，打 tag 前运行",
    ["check-all"] = "开发者全量自检（format+CA+编译），按需运行",
    ["fix-completeness"] = "修复提交前运行的姊妹轴覆盖验证",
    ["fix-orchestrator"] = "guard 失败时输出的修复指引入口",
    ["flaky-parse"] = "抖动测试日志分析，按需运行",
    ["gate-audit"] = "门禁审计工具自身（本脚本），按需运行",
    ["osc-check"] = "由 test-gate 调用的 OSC 翻转检测器",
    ["probe-template"] = "探针模板生成，按需运行",
    ["refine-scan"] = "精炼扫描，按需运行",
    ["review-gate"] = "评审轮次路由决策（全量/增量/跳过）",
    ["review-scope"] = "评审范围计算，评审前运行",
    ["review-snapshot"] = "评审快照——评审报告须粘贴其输出（R0 可信度锚）",
    ["sibling-map"] = "姊妹文件映射，评审辅助",
    ["sister-axis"] = "姊妹轴对称核查，评审辅助",
    ["verify-action-items"] = "按参数校验指定清单文件，按需运行",
};

// 应接线而未接线的（真缺口）——逐条登记原因与解锁条件
var intendedWire = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["ci-coverage"] = "阈值未校准（本机 Docker 缺失致全局 line-rate 不可测）——前置见 docs/test-coverage-baseline.md §门禁阈值",
};

var rows = gateFiles
    .Select(name =>
    {
        var text = File.ReadAllText(Path.Combine(scriptsDir, name + ".cs"), new UTF8Encoding(false, false));
        return new Row(
            Name: name,
            Wired: wiredNames.Contains(name),
            SelfTest: HasSelfVerification(text),
            DeclaresExitCode: text.Contains("退出码"),
            Probed: probedGates.Contains(name, StringComparer.Ordinal));
    })
    .ToList();

Console.WriteLine("=== 门禁可信度矩阵 ===");
Console.WriteLine($"{"门禁",-24} {"WIRED",-7} {"SELFTEST",-9} {"PROBED",-8} {"退出码",-7} 判定");
Console.WriteLine(new string('-', 92));
foreach (var r in rows)
{
    // OK=接线且有自证；UNVERIFIED=接线但无自证；TOOL=按设计手工调用；
    // UNWIRED-GATE=应接线未接（真缺口）；REVIEW=未接线也未归类（须归类，不放过新脚本）
    var verdict = ClassifyVerdict(r.Wired, r.SelfTest, manualTools.ContainsKey(r.Name), intendedWire.ContainsKey(r.Name));
    Console.WriteLine($"{r.Name,-24} {(r.Wired ? "yes" : "-"),-7} {(r.SelfTest ? "yes" : "-"),-9} {(r.Probed ? "yes" : "-"),-8} {(r.DeclaresExitCode ? "yes" : "-"),-7} {verdict}");
}

var wired = rows.Where(r => r.Wired).ToList();
var wiredUnverified = wired.Where(r => !r.SelfTest).ToList();
var unwiredTools = rows.Where(r => !r.Wired && manualTools.ContainsKey(r.Name)).ToList();
var unwiredGaps = rows.Where(r => !r.Wired && intendedWire.ContainsKey(r.Name)).ToList();
var unclassified = rows.Where(r => !r.Wired && !manualTools.ContainsKey(r.Name) && !intendedWire.ContainsKey(r.Name)).ToList();

Console.WriteLine();
Console.WriteLine($"合计 {rows.Count} 个脚本：接线 {wired.Count} · 未接线 {rows.Count - wired.Count}");
Console.WriteLine($"接线且有自证（OK）：{wired.Count - wiredUnverified.Count}");
Console.WriteLine($"接线但无自证（UNVERIFIED）：{wiredUnverified.Count}");
Console.WriteLine($"未接线·工具（按设计）：{unwiredTools.Count}");
Console.WriteLine($"未接线·应接未接（缺口）：{unwiredGaps.Count}");
Console.WriteLine($"未接线·未归类（须归类）：{unclassified.Count}");
foreach (var g in unwiredGaps) Console.WriteLine($"  缺口：{g.Name} —— {intendedWire[g.Name]}");
foreach (var u in unclassified) Console.WriteLine($"  未归类：{u.Name}（登记进 manualTools 或 intendedWire）");

if (inventoryOnly)
{
    Console.WriteLine();
    Console.WriteLine("（--inventory 模式：跳过探针）");
    return 0;
}

// ══════════════ 2. 变异探针（隔离仓库，真实注入坏输入）══════════════

Console.WriteLine();
Console.WriteLine("=== 变异探针（隔离仓库，不触碰本仓库索引）===");

var probes = new List<Probe>
{
    new(
        Name: "secret-scan 拒绝已知密钥格式",
        Gate: "secret-scan",
        ExpectExit: 1,
        MustContainInStdout: "bad.cs",
        Setup: dir =>
        {
            // 「自指陷阱」规避（沿 encoding-gate E3 先例：指纹字符以 \uXXXX 转义书写，
            // 否则本文件自身会命中被检查的模式）。此处探针内容必须命中 secret-scan 的
            // 已知密钥前缀模式，但字面写入本文件会让本文件自己被 secret-scan 命中
            // （实测：pre-commit 在 scripts/gate-audit.cs:138 拦截过本提交），故运行期拼接。
            var fakeKey = string.Concat("AK", "IA", "IOSFODNN7EXAMPLE");
            File.WriteAllText(Path.Combine(dir, "bad.cs"), $"var k = \"{fakeKey}\";\n");
            return ["bad.cs"];
        }),
    new(
        Name: "secret-scan 放行干净输入（负向对照）",
        Gate: "secret-scan",
        ExpectExit: 0,
        MustContainInStdout: "PASS",
        Setup: dir =>
        {
            File.WriteAllText(Path.Combine(dir, "clean.cs"), "var k = \"hello\";\n");
            return ["clean.cs"];
        }),
    // 全仓扫描修复的 fail-closed 路径探针：暂存集为空时（harness 只建 PalDDD.slnx，
    // 它不在 secret-scan 的可扫描扩展名内），原实现打印 PASS + exit 0——门禁没真正
    // 执行却报「干净」；修复后必须非零退出并给出显式 FAIL。
    new(
        Name: "secret-scan 空输入 fail-closed（零可扫描文件不得报 PASS）",
        Gate: "secret-scan",
        ExpectExit: 1,
        MustContainInStdout: "输入为空",
        Setup: _ => []),
    // 全仓扫描修复的 fail-closed 路径探针：无 src/ 时 G1-G3 计数恒为 0，
    // 原实现给出三个 ✅ + exit 0（空输入假绿）；修复后必须非零退出。
    new(
        Name: "gate-lite 缺 src/ fail-closed（计数恒 0 不得判绿）",
        Gate: "gate-lite",
        ExpectExit: 1,
        MustContainInStdout: "前置失败",
        Setup: _ => []),
    new(
        Name: "encoding-gate 拒绝 src/ 下 .cs UTF-8 BOM（核心判定）",
        Gate: "encoding-gate",
        ExpectExit: 1,
        MustContainInStdout: "bom.cs",
        Setup: dir =>
        {
            // E2：头 3 字节 EF BB BF 即违规（*.g.cs 与 obj/bin 除外，故用普通名）
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllBytes(Path.Combine(dir, "src", "bom.cs"),
                [0xEF, 0xBB, 0xBF, .. "var x = 1;\n"u8.ToArray()]);
            return ["src/bom.cs"];
        }),
    new(
        Name: "encoding-gate 覆盖 scripts/ 范围（范围回归——2026-09-13 修复前盲区）",
        Gate: "encoding-gate",
        ExpectExit: 1,
        MustContainInStdout: "bom.cs",
        Setup: dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "scripts"));
            File.WriteAllBytes(Path.Combine(dir, "scripts", "bom.cs"),
                [0xEF, 0xBB, 0xBF, .. "var x = 1;\n"u8.ToArray()]);
            return ["scripts/bom.cs"];
        }),
    new(
        Name: "encoding-gate 拒绝源文件裸 LF（E5 行尾漂移——2026-09-14 增）",
        Gate: "encoding-gate",
        ExpectExit: 1,
        MustContainInStdout: "drifted.md",
        Setup: dir =>
        {
            // E5：.md 纯 LF（或混合）即违规——CRLF 是仓库规范（.gitattributes/.editorconfig）
            File.WriteAllBytes(Path.Combine(dir, "drifted.md"), "纯 LF 文档\n第二行\n"u8.ToArray());
            return ["drifted.md"];
        }),
    new(
        Name: "dapper-param-guard 拒绝枚举直传（CI #94 根因回归守卫——2026-09-14 增）",
        Gate: "dapper-param-guard",
        ExpectExit: 1,
        MustContainInStdout: "ProjectionCheckpointStatus",
        Setup: dir =>
        {
            // CI #94 真实形态：匿名 Dapper 参数对象内枚举直传（未 (int) 化）
            Directory.CreateDirectory(Path.Combine(dir, "src", "PalDDD.Dapper"));
            File.WriteAllText(Path.Combine(dir, "src", "PalDDD.Dapper", "Probe.cs"),
                "var p = new { status = ProjectionCheckpointStatus.Processing };\n");
            return ["src/PalDDD.Dapper/Probe.cs"];
        }),
    new(
        Name: "verify-conventions 拒绝缺段决策文档（V11——2026-09-18 增）",
        Gate: "verify-conventions",
        ExpectExit: 1,
        MustContainInStdout: "decision-bad.md",
        Setup: dir =>
        {
            // V11 形态回归：三必填段全缺——决策文档「落盘即免检」缺口的机械拦截
            //（2026-09-17 decision 文档实证：无锚引用 + 不在任何评审触发面）。
            // 注：探针以无参（full 模式）运行，隔离目录空 slnx 的 build 失败同为
            // exit 1，但 stdout 含 decision-bad.md 仅当 V11 真拦截——断言语义完整。
            Directory.CreateDirectory(Path.Combine(dir, "docs", "review"));
            File.WriteAllText(Path.Combine(dir, "docs", "review", "decision-bad.md"),
                "# 决策论证：探针\n\n## 结论\n无必填段。\n");
            return ["docs/review/decision-bad.md"];
        }),
};

var probeFails = 0;

// 双向对齐断言（2026-09-14 补反向——本守卫开发时实际踩到该缺口）：
// 正向：探针列表的 Gate 必须都在 probedGates 内（防探针测了未登记的门禁）。
// 反向：probedGates 每项**必须被至少一个探针覆盖**——只登记不写探针时，
// 矩阵 PROBED 列显示 yes 而实际从未探测（假绿）。中间态实证：加
// dapper-param-guard 到 probedGates 后忘插探针，矩阵不报错且 PROBED=yes。
var drift = probes.Select(p => p.Gate).Distinct().Where(g => !probedGates.Contains(g, StringComparer.Ordinal)).ToList();
if (drift.Count > 0)
{
    Console.Error.WriteLine($"ERROR: 探针所测门禁未登记进 probedGates：{string.Join(", ", drift)}——矩阵 PROBED 列会失真");
    return 2;
}
var uncovered = probedGates.Where(g => !probes.Any(p => p.Gate == g)).ToList();
if (uncovered.Count > 0)
{
    Console.Error.WriteLine($"ERROR: probedGates 中以下门禁无对应探针：{string.Join(", ", uncovered)}——PROBED 列将假绿（登记了但从未探测）");
    return 2;
}

foreach (var probe in probes)
{
    var (ok, detail) = RunProbe(root, probe);
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} PROBE {probe.Name}{(ok ? "" : " —— " + detail)}");
    if (!ok) probeFails++;
}

Console.WriteLine();
Console.WriteLine($"探针 {probes.Count} 项：通过 {probes.Count - probeFails} · 失败 {probeFails}");
if (probeFails > 0)
{
    Console.Error.WriteLine("FAIL 有门禁未拒绝已知坏输入——该门禁当前不可信，修复前不得依赖其输出。");
    return 1;
}

Console.WriteLine("PASS 全部探针通过：被探测门禁已证明能拒绝坏输入。");
Console.WriteLine($"注意：矩阵中 PROBED 为 '-' 的门禁可信度仍未验证（本次仅探测 {probedGates.Length} 个）。加探针＝向 probes 列表追加一条并登记 probedGates。");
return 0;

// ══════════════ 探针执行 ══════════════

static (bool Ok, string Detail) RunProbe(string repoRoot, Probe probe)
{
    var gatePath = Path.Combine(repoRoot, "scripts", probe.Gate + ".cs");
    if (!File.Exists(gatePath)) return (false, $"门禁脚本不存在：{probe.Gate}.cs");

    var tmp = Path.Combine(Path.GetTempPath(), "gate-audit-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(tmp);
        // 仓库根定位锚（FindRepoRoot 向上找 PalDDD.slnx）
        File.WriteAllText(Path.Combine(tmp, "PalDDD.slnx"), "");
        // 第二道保险：runfile 构建产物不得入索引
        File.WriteAllText(Path.Combine(tmp, ".gitignore"), "dotnet/\nbin/\nobj/\n");

        RunGit(tmp, "init -q");
        RunGit(tmp, "config user.email probe@gate-audit.local");
        RunGit(tmp, "config user.name gate-audit");

        var staged = probe.Setup(tmp);
        // 显式暂存——绝不用 `git add -A`（见文件头隔离纪律）
        foreach (var f in staged) RunGit(tmp, $"add -- {f}");
        RunGit(tmp, "add -- PalDDD.slnx");

        var (exitCode, stdout) = RunGate(gatePath, tmp);

        if (exitCode != probe.ExpectExit)
            return (false, $"退出码 {exitCode} ≠ 期望 {probe.ExpectExit}");
        if (!stdout.Contains(probe.MustContainInStdout, StringComparison.Ordinal))
            return (false, $"输出未含「{probe.MustContainInStdout}」——探针可能未真正扫到注入文件");

        return (true, "");
    }
    catch (Exception ex)
    {
        return (false, $"探针异常：{ex.GetType().Name} {ex.Message}");
    }
    finally
    {
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* 清理失败不掩盖探针结论 */ }
    }
}

static (int ExitCode, string Stdout) RunGate(string gatePath, string workingDir)
{
    var psi = new ProcessStartInfo("dotnet", $"run \"{gatePath}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = workingDir,
    };
    using var p = Process.Start(psi)!;
    var outText = p.StandardOutput.ReadToEnd();
    var errText = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, outText + errText);
}

static void RunGit(string workingDir, string arguments)
{
    var psi = new ProcessStartInfo("git", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = workingDir,
    };
    using var p = Process.Start(psi)!;
    p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
}

// ══════════════ 静态判定 ══════════════

// 接线集合：.githooks/* 与 .github/workflows/* 中出现的脚本名。
// 采用「名字出现即视为接线」的宽松口径——CI 的门禁循环用变量插值
// （for g in encoding-gate doc-consistency ...）无法用字面路径匹配，
// 收紧口径反而会漏判真实接线。
static HashSet<string> CollectWiredNames(string repoRoot)
{
    var names = new HashSet<string>(StringComparer.Ordinal);
    var files = new List<string>();

    var hooksDir = Path.Combine(repoRoot, ".githooks");
    if (Directory.Exists(hooksDir)) files.AddRange(Directory.GetFiles(hooksDir));

    var wfDir = Path.Combine(repoRoot, ".github", "workflows");
    if (Directory.Exists(wfDir)) files.AddRange(Directory.GetFiles(wfDir, "*.yml"));

    var scripts = Directory.GetFiles(Path.Combine(repoRoot, "scripts"), "*.cs")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(n => n is not null)
        .Select(n => n!);

    var corpus = new StringBuilder();
    foreach (var f in files)
    {
        try { corpus.AppendLine(File.ReadAllText(f, new UTF8Encoding(false, false))); }
        catch (IOException) { /* 不可读文件跳过 */ }
    }
    var text = corpus.ToString();

    foreach (var s in scripts)
        if (text.Contains(s, StringComparison.Ordinal)) names.Add(s);

    return names;
}

// 自证能力：脚本是否处理 --selftest（约定式自测入口）
static bool HasSelfVerification(string gateSource) =>
    gateSource.Contains("--selftest", StringComparison.Ordinal);

// 矩阵判定（纯函数，供 --selftest 覆盖）：接线优先于分类——已接线者只看自证；
// 未接线者按「是否按设计手工调用」分工具/缺口/未归类三态。未归类单列而不并入
// 工具，使新脚本必须被显式归类（不放过新增项）。
static string ClassifyVerdict(bool wired, bool selfTest, bool isTool, bool isIntendedWire) =>
    wired
        ? (selfTest ? "OK" : "UNVERIFIED 已接线但无自证")
        : isTool ? "TOOL 手工调用（按设计）"
        : isIntendedWire ? "UNWIRED-GATE 应接线未接（缺口）"
        : "REVIEW 未接线且未归类（须归入 TOOL 或 UNWIRED-GATE）";

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
    return ""; // 不可达
}

// ══════════════ 自测（判定逻辑单元验证，不跑探针）══════════════

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

    // 自证能力判定：正例/反例各一（防「永远返回 true」的假绿判定器）
    Case("HasSelfVerification 识别 --selftest 存在", HasSelfVerification("if (args.Contains(\"--selftest\")) return SelfTest();"));
    Case("HasSelfVerification 对无自证脚本返回 false", !HasSelfVerification("Console.WriteLine(\"hi\"); return 0;"));

    // 接线判定口径：名字出现即接线（含变量插值形态）
    var wiredCorpus = "for g in encoding-gate doc-consistency; do dotnet run scripts/$g.cs; done";
    Case("接线判定命中变量插值形态", wiredCorpus.Contains("encoding-gate", StringComparison.Ordinal));
    Case("接线判定不误报未出现名", !wiredCorpus.Contains("sibling-map", StringComparison.Ordinal));

    // 隔离仓库契约：.gitignore 必须屏蔽 runfile 产物（否则 git add 会夹带构建产物）
    var expectedIgnore = "dotnet/\nbin/\nobj/\n";
    Case("隔离仓库 .gitignore 覆盖 dotnet/", expectedIgnore.Contains("dotnet/", StringComparison.Ordinal));

    // 矩阵判定（五态，防某一态被合并掉）
    Case("判定：接线+自证 → OK", ClassifyVerdict(true, true, false, false) == "OK");
    Case("判定：接线无自证 → UNVERIFIED", ClassifyVerdict(true, false, false, false).StartsWith("UNVERIFIED", StringComparison.Ordinal));
    Case("判定：未接线且为工具 → TOOL", ClassifyVerdict(false, false, true, false).StartsWith("TOOL", StringComparison.Ordinal));
    Case("判定：未接线且应接未接 → UNWIRED-GATE", ClassifyVerdict(false, false, false, true).StartsWith("UNWIRED-GATE", StringComparison.Ordinal));
    Case("判定：未接线且未归类 → REVIEW（不放过新脚本）", ClassifyVerdict(false, false, false, false).StartsWith("REVIEW", StringComparison.Ordinal));
    // 边界：已接线者不受工具/缺口标记影响（分类只在未接线时生效）
    Case("判定：已接线时工具标记不改变结论", ClassifyVerdict(true, true, true, true) == "OK");
    Case("判定：已接线无自证时缺口标记不改变结论", ClassifyVerdict(true, false, false, true).StartsWith("UNVERIFIED", StringComparison.Ordinal));

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}

// ══════════════ 类型 ══════════════

internal sealed record Row(string Name, bool Wired, bool SelfTest, bool DeclaresExitCode, bool Probed);

internal sealed record Probe(
    string Name,
    string Gate,
    int ExpectExit,
    string MustContainInStdout,
    Func<string, string[]> Setup);
