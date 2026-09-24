// ============================================================================
// gate.cs——Pal.DDD 门禁扫描（MIG-012-A1，2026-09-11）
// 由 .ai/scripts/gate-check.sh 等价迁移为 C#（dotnet file-based app）。
// 保留集 3 项（G22/G23/G24，纯 git 编排）——源码级判定 21 项已全部下沉 C# 测试
// （ArchitectureBoundaryTests 37 方法 + SourceCodeGuardTests 守卫 1-6），
// 本脚本仅保留 C# 测试对 git 状态天然不可见的"变更集编排"三项，职责边界不变。
//
// 用法：dotnet run scripts/gate.cs -- [--allow-dirty] [--selftest]
//   --allow-dirty：跳过 G22 工作树脏检查（与 bash 版同参同名）
//   --selftest：判定逻辑单元自测（纯函数正负例，不触碰 git）
// 退出码：0=通过（FAIL=0）；1=有 FAIL 项（WARN/SKIP 不阻断）。
//
// 变更集口径（2026-09-25 修订，发布语义）：
//   G23/G24 的变更集不再只看最后一次提交，而是三级回落——① 有暂存 → --cached
//   （pre-commit 语义优先）；② 否则 HEAD 可达的最近 v* tag → f"{tag}..HEAD"
//   （发布语义：HEAD 到上一个版本 tag 之间的全部提交）；③ tag 不可解析
//   （无 tag/浅克隆/孤儿分支）且 HEAD~1 可解析 → HEAD~1..HEAD（ITM-620 原回落）；
//   ④ 皆不可解析 → 显式 SKIP。G23 判定随之从「范围合计」改为「逐提交同集耦合」
//   （快照与 CHANGELOG 必须在同一提交/同一次暂存内同时出现）——范围合计形态可被
//   「补一个只改 CHANGELOG 的提交」无条件洗白（本仓实证见 G23 段注释）。
//   详见下方「变更集基线」与「PDDD-G23」两段注释。
//
// 等价迁移说明（相对 .ai/scripts/gate-check.sh）：
//   1) git 调用改为 System.Diagnostics.Process（工作目录=仓库根）。仓库根从本 cs
//      源文件位置（编译期 CallerFilePath，dotnet run 保留原始路径）向上查找
//      PalDDD.slnx——等价 bash 的 ROOT_DIR 推导，与调用方 cwd 无关，CI/本地任意
//      目录调用均成立。
//   2) 输出行与 bash 版逐行一致（含 ANSI 颜色码）——CI grep 消费的关键字
//      PASS/FAIL/WARN/SKIP 不变。
//   3) G24 归一化检测保真复刻 bash 实际语义（匹配字面 Replace('', '/')，见 G24
//      段注释），行为等价：diff 新增行含 Path.GetFileName* 即恒 WARN。
//   4) 时间戳为本地时间（bash date 同源）；file-based app 零 package 依赖。
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的固定协议行
// （ci.yml/留档 diff 依赖 PASS/FAIL/WARN/SKIP 关键字与中文说明的逐字符匹配），无本地化
// 需求——沿 osc-check.cs / vuln-scan.cs 先例文件级禁用，避免逐行 disable 噪音
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文输出经 bash 捕获会乱码——对齐 bash UTF-8
//（沿 osc-check.cs / ci-failed-tests.cs 先例）
Console.OutputEncoding = Encoding.UTF8;

// ─── 仓库根发现：从本 cs 源文件位置向上找 PalDDD.slnx（等价 bash ROOT_DIR）───
var ROOT = FindRepoRoot();

int passed = 0, warned = 0, failedCount = 0;
// ANSI 颜色码与 bash 版逐字相同（\033[0;31m 等），留档 diff 时归一即可
const string Red = "\x1b[0;31m", Green = "\x1b[0;32m", Yellow = "\x1b[0;33m", Nc = "\x1b[0m";

// 2026-09-13 增：本脚本此前无自证能力（gate-audit 矩阵标 UNVERIFIED）。G23/G24 的
// 判定都是「对 git 输出文本做计数后比对」，计数口径写错即静默放行（G23 漏判 = 公共
// API 变更不记录 / G24 漏判 = 跨平台守卫在 CI 平台 no-op）。故把两处计数抽为纯函数
// 并对合成 git 输出覆盖，不需要真实 git fixture。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

Console.WriteLine("═══════ Pal.DDD 门禁扫描（保留 3 项：G22/G23/G24）═══════");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("规范：docs/conventions.md + ArchitectureBoundaryTests.cs + SourceCodeGuardTests.cs");
Console.WriteLine("源码级判定（G1/G7-G12/G14/G17 等 21 项）已全部下沉 C# 测试，本脚本仅剩 git 编排三项");
Console.WriteLine();

// ──────────────────────────────────────────────────────────────
// PDDD-G22：工作树脏检查（任务切换前 git status 清洁或显式声明）
// 仅"已暂存待提交"（git add 后）视为合法中间态放行——暂存是显式的提交意图，
// 未暂存修改/未跟踪文件仍 FAIL。--allow-dirty 参数跳过。
// ──────────────────────────────────────────────────────────────
if (args.Contains("--allow-dirty"))
{
    Console.WriteLine($"{Yellow}SKIP{Nc} PDDD-G22: --allow-dirty 指定，跳过工作树检查");
    warned++;
}
else
{
    var g22Dirty = false;
    // 外仓检查（git 输出非空 = 有任何状态；2>/dev/null 失败时 bash 视为空 → 跳过）
    if (!string.IsNullOrWhiteSpace(Git("status --short").Output))
    {
        var unstaged = CountNonEmptyLines(Git("diff --name-only").Output);
        var untracked = CountNonEmptyLines(Git("ls-files --others --exclude-standard").Output);
        if (unstaged > 0 || untracked > 0)
        {
            Console.WriteLine($"{Red}FAIL{Nc} PDDD-G22: 外仓有未提交改动（未暂存 {unstaged} + 未跟踪 {untracked}，违规数：{unstaged + untracked}）");
            failedCount++;
            g22Dirty = true;
        }
    }
    // .ai 独立仓检查（嵌套 Git 仓库，外仓 gitignore 不覆盖其内部状态）
    if (Directory.Exists(Path.Combine(ROOT, ".ai", ".git"))
        && !string.IsNullOrWhiteSpace(Git("-C .ai status --short").Output))
    {
        var aiUnstaged = CountNonEmptyLines(Git("-C .ai diff --name-only").Output);
        var aiUntracked = CountNonEmptyLines(Git("-C .ai ls-files --others --exclude-standard").Output);
        if (aiUnstaged > 0 || aiUntracked > 0)
        {
            Console.WriteLine($"{Red}FAIL{Nc} PDDD-G22: .ai 仓有未提交改动（未暂存 {aiUnstaged} + 未跟踪 {aiUntracked}，违规数：{aiUnstaged + aiUntracked}）");
            failedCount++;
            g22Dirty = true;
        }
    }
    if (!g22Dirty)
    {
        var staged = CountNonEmptyLines(Git("diff --cached --name-only").Output);
        Console.WriteLine($"{Green}PASS{Nc} PDDD-G22: 双仓工作树清洁（{staged} 个已暂存待提交——合法中间态）");
        passed++;
    }
}

// ═══ 变更集基线（2026-09-25 修订为发布语义；ITM-620 于 2026-09-10 立基）═══
// 三级回落，判定抽为纯函数 ResolveChangeRange（输入 = git 探测结果，输出 = 范围）：
//   ① 暂存集非空 → "--cached"（pre-commit 语义优先：即将落盘的内容才是判定对象）
//   ② 否则 HEAD 可达的最近 v* tag → f"{tag}..HEAD"（发布语义：HEAD 到上一个版本
//      tag 之间的全部提交，而非只看最后一次提交——"只看最后一次"让快照提交未带
//      CHANGELOG 触发的红可被随后一个只改 CHANGELOG 的提交洗白，G23 沦为可被
//      「再提交一次」无条件绕过的装饰门禁，详见 PDDD-G23 段注释）
//   ③ tag 不可解析（无 v* tag/浅克隆/孤儿分支）且 HEAD~1 可解析 → "HEAD~1..HEAD"
//      （ITM-620 原回落：单棵树对一棵树，保留 merge 感知的树差形态）
//   ④ 皆不可解析 → None → G23/G24 显式 SKIP（计入 WARNED，绝不假 PASS）
// tag 探测用 describe --tags --abbrev=0 --match v*：describe 只返回 HEAD 可达的
// tag（"最近"而非"全局最高版本"——在自旧 tag 拉出的分支上取全局最新 tag 会拿到
// 不可达基线），--match v* 排除非版本 tag；describe 失败即视为无 tag，绝不报错。
// describe 成功后仍以 rev-parse 复核一次（防非常规 tag 对象），不指向 commit 视同无 tag。
var stagedNames = Git("diff --cached --name-only").Output;
var describe = Git("describe --tags --abbrev=0 --match v*");
string? releaseTag = describe.ExitCode == 0 && !string.IsNullOrWhiteSpace(describe.Output)
    ? describe.Output.Trim()
    : null;
if (releaseTag is not null && Git($"rev-parse --verify --quiet {releaseTag}^{{commit}}").ExitCode != 0)
    releaseTag = null;

var range = ResolveChangeRange(
    stagedNonEmpty: !string.IsNullOrWhiteSpace(stagedNames),
    releaseTag: releaseTag,
    headParentResolvable: Git("rev-parse --verify -q HEAD~1").ExitCode == 0);

// 范围是否可解析的唯一判定点（G23/G24 共用——同一条件不写两遍，改口径只改一处）
bool hasChangeSet = range.Kind != ChangeRangeKind.None;

// G23 的纯函数输入：逐提交变更文件清单文本（段分隔符 \x1e = git --format=%x1e）。
//   Release 模式**必须逐提交**（git log --no-merges --name-only）——树差把整个窗口
//     汇成一份，"快照提交未带 CHANGELOG"与"随后补记的 CHANGELOG 提交"同窗即被洗白
//     （本仓实证：v3.1.0 窗口 11a4f2d 只改快照、b0feaa7 只补 CHANGELOG）；
//   Staged/Head 模式是单一判定单元（暂存集 / 一棵树对一棵树），拼为单记录文本。
// G24 的纯函数输入：范围内的新增行 diff（-U0），与 G23 共用同一范围口径。
string? setsText = range.Kind switch
{
    ChangeRangeKind.Staged => SingleSetText("staged", stagedNames),
    ChangeRangeKind.Head => SingleSetText("HEAD~1..HEAD", Git("diff --name-only HEAD~1..HEAD").Output),
    ChangeRangeKind.Release => Git($"log --no-merges --name-only --format={GateAnchors.LogSetFormat} {range.Spec}").Output,
    _ => null,
};
var changedDiff = hasChangeSet ? Git($"diff -U0 {range.Spec}").Output : "";

// ═══ PDDD-G23：公共 API 变更三件套（lessons XV BINC-1）═══
// 判定口径（2026-09-25 修订为「逐提交同集耦合」）：变更集（发布窗口 / 暂存集）内
// **任一提交**改了公共 API 快照（Snapshots/core-packages-public-api.txt，子串匹配）
// 而未在**同一提交**改 CHANGELOG.md（整行匹配）→ FAIL。
// 为什么不能是范围合计（旧口径，ITM-620 后长期如此）：合计只问"窗口内两样都在"，
// 于是"快照提交未带 CHANGELOG → G23 红"可被随后一个**只改 CHANGELOG** 的提交洗白
// ——本仓实证：v3.0.0→v3.1.0 窗口内 11a4f2d 只改快照（API 版本行），b0feaa7 只补
// CHANGELOG（提交信息自述 BINC-1 三真源），合计口径在 b0feaa7 上转绿。门禁因此没有
// 真正保证快照变更在发布前被记录。三真源 = 快照 + CHANGELOG + 二进制兼容评估，
// 必须同集落盘（同一提交 / 同一次暂存）才算记录。
if (!hasChangeSet)
{
    Console.WriteLine($"{Yellow}SKIP{Nc} PDDD-G23: 变更集基线不可解析（无暂存、无可达 v* tag、HEAD~1 不可解析——孤儿分支/初始提交/浅克隆），不判定快照同步（非 PASS）");
    warned++;
}
else
{
    // 快照子串匹配 / CHANGELOG 整行匹配的口径与 bash 一致，抽在纯函数 G23Judge 内
    var (violates, offender) = G23Judge(setsText!);
    if (violates)
    {
        Console.WriteLine($"{Red}FAIL{Nc} PDDD-G23: 公共 API 快照变更未与 CHANGELOG.md 同提交记录（提交 {offender}；BINC-1：快照+CHANGELOG+二进制兼容评估三真源须同集，窗口合计口径可被补记提交洗白）");
        failedCount++;
    }
    else
    {
        Console.WriteLine($"{Green}PASS{Nc} PDDD-G23: 公共 API 快照与 CHANGELOG 同提交同步（或本次无快照变更）");
        passed++;
    }
}

// ═══ PDDD-G24：跨平台路径守卫（lessons XV PLAT-1）═══
// 新增的 Path.GetFileName*/GetFullPath 调用若处理 csproj/XML 等文档路径，必须先归一化
// 分隔符——Unix 上反斜杠不是分隔符，Windows 验证过的守卫在 CI (ubuntu) 上可能永不命中。
// 警告级（Windows 专属工具代码不误伤）。变更集口径与 G23 完全一致（同一次范围解析）：
// 暂存 → 发布窗口 tag..HEAD → HEAD~1..HEAD；窗口变大只增加召回面，不改变判定形状
// （hits>0 且归一化字面同在窗口内仍 PASS），且本项为 WARN 级不阻断。
//
// ⚠️ 保真复刻说明（MIG-012-A1 迁移时发现的原 bash 缺陷）：
// bash 版模式 "Replace('\\', '/')" 经双引号转义为 Replace('\', '/')，GNU BRE 把
// \' 解析为字面单引号 → 实际匹配的是空参数形态 Replace('', '/')，与 C# 源码真实
// 归一化写法 Replace('\\', '/') 错位——实际恒不命中，效果 = 新增 Path.GetFileName*
// 即恒 WARN。本复刻保持字面匹配（行为等价），是否修复为真实检测交主线程裁决。
if (!hasChangeSet)
{
    Console.WriteLine($"{Yellow}SKIP{Nc} PDDD-G24: 变更集基线不可解析（无暂存且 HEAD~1 不存在——孤儿分支/初始提交/浅克隆），不判定路径归一化（非 PASS）");
    warned++;
}
else
{
    // 只看 diff 新增行（^+，含 +++ 头行——与 bash grep "^+" 同口径）
    var (g24Hits, g24Normalized) = G24Counts(changedDiff);
    if (g24Hits > 0)
    {
        if (g24Normalized == 0)
        {
            Console.WriteLine($"{Yellow}WARN{Nc} PDDD-G24: 新增 {g24Hits} 处 Path.GetFileName* 调用但未见分隔符归一化——若处理文档路径（csproj/相对路径），Unix 上反斜杠不拆分（PLAT-1：守卫在 CI 平台 no-op）；仅 Windows 专属工具可忽略");
            warned++;
        }
        else
        {
            Console.WriteLine($"{Green}PASS{Nc} PDDD-G24: 新增路径调用含分隔符归一化（{g24Hits} 处）");
            passed++;
        }
    }
    else
    {
        Console.WriteLine($"{Green}PASS{Nc} PDDD-G24: 无新增裸路径文件名调用");
        passed++;
    }
}

// ── 汇总 ──────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("═══════ 扫描完成 ═══════");
Console.WriteLine($"通过：{passed}  警告：{warned}  失败：{failedCount}  总计：{passed + warned + failedCount}");
Console.WriteLine("权威依据：docs/conventions.md + ArchitectureBoundaryTests.cs + SourceCodeGuardTests.cs");

return failedCount > 0 ? 1 : 0;

// ─── 局部函数（file-based app：顶层语句 + static 方法） ───

// 仓库根发现：从本 cs 源文件位置向上找含 PalDDD.slnx 的目录。
// CallerFilePath 由编译器填入源文件原始路径（dotnet run file.cs 保留原始位置），
// 与调用方 cwd 无关——等价 bash 的 SCRIPT_DIR/ROOT_DIR 推导。
static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string src = "")
{
    var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(src)
        ? Environment.CurrentDirectory
        : Path.GetDirectoryName(src)!);
    while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("未找到仓库根（PalDDD.slnx）——请从仓库内运行");
}

// git 调用：工作目录=仓库根；stdout 捕获（stderr 丢弃——等价 bash 2>/dev/null）。
// git 不存在/调用失败时返回 rc=-1 + 空输出，上层按空输出处理（bash 同路径）。
static (int ExitCode, string Output) Git(string gitArgs)
{
    try
    {
        var psi = new ProcessStartInfo("git", gitArgs)
        {
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
    catch (Exception e) when (e is Win32Exception or InvalidOperationException)
    {
        return (-1, "");
    }
}

// 输出按行拆分（兼容 \r\n），保留原行内容
static List<string> ToLines(string output) =>
    output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

// 等价 bash 的 `grep -c .`：非空行计数
static int CountNonEmptyLines(string output) =>
    ToLines(output).Count(l => l.Length > 0);

// ══════════════ 判定（纯函数，供 --selftest 覆盖）══════════════
// 注：锚点常量 / 范围枚举 / 记录类型等**成员声明**集中在文件末尾——顶级语句与局部
// 函数必须位于类型声明之前（CS8803），故不插在局部函数之间。

// ── 变更集范围解析（G23/G24 共用；三级回落语义见上方「变更集基线」注释）──
static ChangeRange ResolveChangeRange(bool stagedNonEmpty, string? releaseTag, bool headParentResolvable)
{
    // ① 暂存优先（pre-commit 语义）：tag 范围只覆盖已提交内容，看不到即将落盘的改动
    if (stagedNonEmpty) return new ChangeRange(ChangeRangeKind.Staged, "--cached");
    // ② 发布语义：HEAD 到 HEAD 可达的最近 v* tag 之间的全部提交
    if (!string.IsNullOrWhiteSpace(releaseTag))
        return new ChangeRange(ChangeRangeKind.Release, $"{releaseTag.Trim()}..HEAD");
    // ③ ITM-620 原回落：单棵树对一棵树（merge 感知）
    if (headParentResolvable) return new ChangeRange(ChangeRangeKind.Head, "HEAD~1..HEAD");
    // ④ 皆不可解析：上层显式 SKIP（绝不假 PASS）
    return ChangeRange.None;
}

// ── G23 逐提交同集耦合判定（BINC-1；锚点常量与记录类型见文件末尾 GateAnchors）──

// 单记录文本（Staged / Head 模式用）：首行必须是记录标签——解析器取每段首行作为
// 记录身份、不参与口径判定，缺了它第一行文件会被误当标签而漏判。
static string SingleSetText(string label, string nameOnlyOutput) => $"{GateAnchors.SetSeparator}{label}\n{nameOnlyOutput}";

// 解析逐提交清单文本 → 记录列表。空行跳过（git 在格式行与文件行之间有空行；
// merge 提交已被 --no-merges 排除；空提交/仅 mode 变更产生无文件记录，天然不违规）。
static List<CommitSet> ParseCommitSets(string setsText)
{
    var sets = new List<CommitSet>();
    foreach (var segment in setsText.Split(GateAnchors.SetSeparator))
    {
        var lines = ToLines(segment).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) continue;
        sets.Add(new CommitSet(lines[0], lines.Skip(1).ToList()));
    }
    return sets;
}

// G23 判定（文本级入口）：输入 = 逐提交变更文件清单文本（changedFiles 文本的逐提交
// 形态），配置 = 两个锚点口径。违规 = 任一记录改了快照而未**同记录**改 CHANGELOG，
// 返回首个违规记录身份（提交 hash / "staged" / "HEAD~1..HEAD"）供 FAIL 行定位。
// 为什么逐记录而不是范围合计：合计口径下"快照提交未带 CHANGELOG"可被随后一个只改
// CHANGELOG 的提交洗白（本仓 v3.1.0 窗口实证，见 PDDD-G23 段注释）。
static (bool Violates, string Offender) G23Judge(string setsText,
    string snapshotMarker = GateAnchors.Snapshot, string changelogMarker = GateAnchors.Changelog)
{
    foreach (var (hash, files) in ParseCommitSets(setsText))
    {
        var snap = files.Count(f => f.Contains(snapshotMarker, StringComparison.Ordinal));
        var changelog = files.Count(f => f == changelogMarker);
        if (snap > 0 && changelog == 0) return (true, hash);
    }
    return (false, "");
}

// G24 计数：只看新增行（`+` 起首，含 `+++` 头行——与 bash grep "^+" 同口径），
// 分别计 Path.GetFileName* 命中与字面 Replace('', '/') 归一化命中
// （后者是保真复刻 bash 的实际语义，见 G24 段 ⚠️ 说明）。
static (int Hits, int Normalized) G24Counts(string changedDiffOutput)
{
    var plusLines = ToLines(changedDiffOutput).Where(l => l.StartsWith('+')).ToList();
    var rx = new Regex(@"Path\.GetFileName(WithoutExtension)?");
    return (plusLines.Count(l => rx.IsMatch(l)), plusLines.Count(l => l.Contains("Replace('', '/')")));
}

// ══════════════ 自测 ══════════════

static int SelfTest()
{
    var passedCount = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passedCount++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    // ── 工具函数 ──
    Case("ToLines 拆 CRLF", ToLines("a\r\nb\r\n") is ["a", "b", ""]);
    Case("CountNonEmptyLines 忽略空行", CountNonEmptyLines("a\n\nb\n") == 2);
    Case("CountNonEmptyLines 空输出为 0", CountNonEmptyLines("") == 0);

    // ── G23（BINC-1 逐提交同集耦合）──
    var rs = GateAnchors.SetSeparator;
    // 正例：快照与 CHANGELOG 同集（同一提交 / 同一次暂存）
    Case("G23 快照与 CHANGELOG 同提交 → PASS",
        !G23Judge($"{rs}aaa\nSnapshots/core-packages-public-api.txt\nCHANGELOG.md\n").Violates);
    Case("G23 仅 CHANGELOG → PASS", !G23Judge($"{rs}aaa\nCHANGELOG.md\n").Violates);
    Case("G23 无关变更 → PASS", !G23Judge($"{rs}aaa\nsrc/PalDDD.Core/X.cs\n").Violates);
    Case("G23 空输出 → PASS", !G23Judge("").Violates);
    // 负例：快照在同集、CHANGELOG 不在同集
    Case("G23 快照变更无同提交 CHANGELOG → FAIL",
        G23Judge($"{rs}aaa\ntest/PalDDD.Core.Tests/Snapshots/core-packages-public-api.txt\n").Violates);
    // 负例（本仓真实事故形态）：快照变更在更早提交、最后一次提交只改 CHANGELOG。
    // 旧口径（窗口合计两样都在）在补记提交上转绿；逐提交同集口径必须红在快照提交上。
    Case("G23 快照在更早提交、末提交只补 CHANGELOG → FAIL（跨提交洗白形态，旧口径放过）",
        G23Judge($"{rs}11a4f2d\ntest/PalDDD.Core.Tests/Snapshots/core-packages-public-api.txt\n{rs}b0feaa7\nCHANGELOG.md\n") is (true, "11a4f2d"));
    // 负例：多提交中任一快照提交缺同集 CHANGELOG，且报出该提交
    Case("G23 多提交中间快照提交缺 CHANGELOG → FAIL 且定位到该提交",
        G23Judge($"{rs}a1\nsrc/A.cs\n{rs}b2\nCHANGELOG.md\n{rs}c3\nSnapshots/core-packages-public-api.txt\n") is (true, "c3"));
    // 正例：单记录文本（staged / HEAD~1 树差）——首行标签不参与口径判定
    Case("G23 单记录文本（staged 标签行不误判）",
        !G23Judge(SingleSetText("staged", "Snapshots/core-packages-public-api.txt\nCHANGELOG.md\n")).Violates);
    Case("G23 单记录文本缺 CHANGELOG → FAIL",
        G23Judge(SingleSetText("HEAD~1..HEAD", "Snapshots/core-packages-public-api.txt\n")) is (true, "HEAD~1..HEAD"));
    // 口径差异（钉住原 bash 行为）：快照子串匹配——更长路径亦命中
    Case("G23 快照为子串口径（更长路径亦命中）",
        G23Judge($"{rs}aaa\ndocs/backup/Snapshots/core-packages-public-api.txt\n").Violates);
    // 口径差异：CHANGELOG 整行匹配——带目录前缀的同名文件不计入（快照提交仍判违规）
    Case("G23 CHANGELOG 为整行口径（docs/CHANGELOG.md 不计入）",
        G23Judge($"{rs}aaa\nSnapshots/core-packages-public-api.txt\ndocs/CHANGELOG.md\n").Violates);

    // ── 变更集范围解析（三级回落，发布语义）──
    Case("范围：暂存优先（有暂存即 --cached，tag 不参与）",
        ResolveChangeRange(stagedNonEmpty: true, releaseTag: "v3.1.0", headParentResolvable: true)
            == new ChangeRange(ChangeRangeKind.Staged, "--cached"));
    Case("范围：无暂存 + 有 tag → tag..HEAD（发布语义）",
        ResolveChangeRange(false, "v3.1.0", true) == new ChangeRange(ChangeRangeKind.Release, "v3.1.0..HEAD"));
    Case("范围：无暂存 + 无 tag → HEAD~1..HEAD（ITM-620 回落）",
        ResolveChangeRange(false, null, true) == new ChangeRange(ChangeRangeKind.Head, "HEAD~1..HEAD"));
    Case("范围：无暂存 + 无 tag + HEAD~1 不可解析 → None（上层 SKIP，绝不假 PASS）",
        ResolveChangeRange(false, null, false) == new ChangeRange(ChangeRangeKind.None, ""));
    Case("范围：空白 tag 视同无 tag",
        ResolveChangeRange(false, "   ", true) == new ChangeRange(ChangeRangeKind.Head, "HEAD~1..HEAD"));
    Case("范围：tag 前后空白被裁剪",
        ResolveChangeRange(false, "  v3.1.0\n", true) == new ChangeRange(ChangeRangeKind.Release, "v3.1.0..HEAD"));
    // 解析：逐提交分段 + 单记录文本
    var parsed = ParseCommitSets($"{rs}aaa\nsrc/A.cs\n{rs}bbb\nCHANGELOG.md\n");
    Case("解析：逐提交分段（首行 hash + 文件行）",
        parsed.Count == 2 && parsed[0].Hash == "aaa" && parsed[0].Files.Count == 1
        && parsed[0].Files[0] == "src/A.cs" && parsed[1].Hash == "bbb" && parsed[1].Files[0] == "CHANGELOG.md");
    var single = ParseCommitSets(SingleSetText("staged", "a.cs\nb.cs\n"));
    Case("解析：单记录文本首行是标签、其余是文件",
        single.Count == 1 && single[0].Hash == "staged" && single[0].Files.Count == 2 && single[0].Files[1] == "b.cs");
    Case("解析：空文本 → 无记录", ParseCommitSets("").Count == 0);

    // ── G24 ──
    // 病态样本运行期拼装（自指陷阱一般化规则）：本行若字面写出被检测形态，
    // 会让本文件自身成为 G24 的观察对象（虽不影响门禁判定，但保持规则一致）
    var getFileName = "Path.GetFileName(";
    var getFileNameNoExt = "Path.GetFileNameWithoutExtension(";
    var normalizedLiteral = "Replace('', '/')";

    Case("G24 新增调用且无归一化 → (1,0) 应 WARN",
        G24Counts($"+var n = {getFileName}p);\n") == (1, 0));
    Case("G24 识别 WithoutExtension 变体",
        G24Counts($"+var n = {getFileNameNoExt}p);\n") == (1, 0));
    Case("G24 含归一化字面 → PASS",
        G24Counts($"+var n = {getFileName}{normalizedLiteral});\n") == (1, 1));
    Case("G24 非新增行不计（`-` 与空格起首）",
        G24Counts($"-var n = {getFileName}p);\n var m = {getFileName}q);\n") == (0, 0));
    Case("G24 `+++` 头行本身不含调用则不命中", G24Counts("+++ b/src/X.cs\n") == (0, 0));
    Case("G24 无命中 → PASS", G24Counts("+var x = 1;\n") == (0, 0));
    Case("G24 空输出 → PASS", G24Counts("") == (0, 0));

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passedCount}/{total} 通过");
    return passedCount == total ? 0 : 1;
}

// ══════════════ 成员声明区 ══════════════
// 集中置于文件末尾：顶级语句与局部函数必须位于类型声明之前（CS8803）；
// 常量收进静态类（顶级语句之后的裸 const 会成为局部量，局部函数引用即 CS0841）。

// G23 三真源锚点（BINC-1）：
//   公共 API 快照路径——**子串**匹配，钉住 bash grep -c 无锚口径（更长路径如
//     docs/backup/Snapshots/core-packages-public-api.txt 同样命中）；
//   CHANGELOG 根文件——**整行**匹配，钉住 bash ^CHANGELOG.md$ 口径
//     （docs/CHANGELOG.md 是另一份文档，不计入）。
static class GateAnchors
{
    public const string Snapshot = "Snapshots/core-packages-public-api.txt";
    public const string Changelog = "CHANGELOG.md";
    // 段分隔符 RS (0x1e)：git --format=%x1e 每个提交输出一个；拼单记录文本时同样以它开头
    public const string SetSeparator = "\x1e";
    // git log 的格式串（%x1e 分段 + %H 记录身份）——传给 git 的字面量，不经过 shell
    public const string LogSetFormat = "%x1e%H";
}

// 变更集范围（G23/G24 共用；三级回落语义见上方「变更集基线」注释）：
//   Staged  = --cached（暂存集，pre-commit 语义）
//   Release = v3.1.0..HEAD（发布窗口：HEAD 到 HEAD 可达的最近 v* tag）
//   Head    = HEAD~1..HEAD（无 tag 时的 ITM-620 回落，树差形态）
//   None    = 皆不可解析（上层显式 SKIP，绝不假 PASS）
enum ChangeRangeKind { Staged, Release, Head, None }

sealed record ChangeRange(ChangeRangeKind Kind, string Spec)
{
    public static readonly ChangeRange None = new(ChangeRangeKind.None, "");
}

// 一个提交（或一次暂存）内的变更文件清单；Hash 是提交 hash 或记录标签。
sealed record CommitSet(string Hash, IReadOnlyList<string> Files);
