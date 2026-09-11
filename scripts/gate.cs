// ============================================================================
// gate.cs——Pal.DDD 门禁扫描（MIG-012-A1，2026-09-11）
// 由 .ai/scripts/gate-check.sh 等价迁移为 C#（dotnet file-based app）。
// 保留集 3 项（G22/G23/G24，纯 git 编排）——源码级判定 21 项已全部下沉 C# 测试
// （ArchitectureBoundaryTests 37 方法 + SourceCodeGuardTests 守卫 1-6），
// 本脚本仅保留 C# 测试对 git 状态天然不可见的"变更集编排"三项，职责边界不变。
//
// 用法：dotnet run scripts/gate.cs -- [--allow-dirty]
//   --allow-dirty：跳过 G22 工作树脏检查（与 bash 版同参同名）
// 退出码：0=通过（FAIL=0）；1=有 FAIL 项（WARN/SKIP 不阻断）。
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

// ═══ 变更集基线（ITM-620 修复，2026-09-10）═══
// G23/G24 优先本地暂存集（pre-commit 语义）；为空回退 HEAD~1..HEAD（CI/已提交场景）。
// HEAD~1 不可解析（孤儿分支跟踪/初始提交/浅克隆 depth=1）时显式 SKIP（计入 WARNED）
// ——不引入对 CI 环境变量的依赖（github.event.before 基线方案留待 CI workflow 改造）。
string? diffRange;
if (!string.IsNullOrWhiteSpace(Git("diff --cached --name-only").Output))
    diffRange = "--cached";
else if (Git("rev-parse --verify -q HEAD~1").ExitCode == 0)
    diffRange = "HEAD~1..HEAD";
else
    diffRange = null;

var changedFiles = diffRange is null ? "" : Git($"diff --name-only {diffRange}").Output;
var changedDiff = diffRange is null ? "" : Git($"diff -U0 {diffRange}").Output;

// ═══ PDDD-G23：公共 API 变更三件套（lessons XV BINC-1）═══
// 快照（Snapshots/core-packages-public-api.txt）变更的变更集必须同时包含 CHANGELOG.md
// ——公共 API 变更不记录 = 三个真源（代码/快照/CHANGELOG）失同步。
if (diffRange is null)
{
    Console.WriteLine($"{Yellow}SKIP{Nc} PDDD-G23: 变更集基线不可解析（无暂存且 HEAD~1 不存在——孤儿分支/初始提交/浅克隆），不判定快照同步（非 PASS）");
    warned++;
}
else
{
    // grep -c 无锚子串匹配 / ^CHANGELOG.md$ 整行匹配——口径与 bash 一致
    var snapCount = ToLines(changedFiles).Count(l => l.Contains("Snapshots/core-packages-public-api.txt"));
    var changelogCount = ToLines(changedFiles).Count(l => l == "CHANGELOG.md");
    if (snapCount > 0 && changelogCount == 0)
    {
        Console.WriteLine($"{Red}FAIL{Nc} PDDD-G23: 公共 API 快照已变更但 CHANGELOG.md 未同步（BINC-1：发布后 API 变更三件套=快照+CHANGELOG+二进制兼容评估）");
        failedCount++;
    }
    else
    {
        Console.WriteLine($"{Green}PASS{Nc} PDDD-G23: 公共 API 快照与 CHANGELOG 同步（或本次无快照变更）");
        passed++;
    }
}

// ═══ PDDD-G24：跨平台路径守卫（lessons XV PLAT-1）═══
// 新增的 Path.GetFileName*/GetFullPath 调用若处理 csproj/XML 等文档路径，必须先归一化
// 分隔符——Unix 上反斜杠不是分隔符，Windows 验证过的守卫在 CI (ubuntu) 上可能永不命中。
// 警告级（Windows 专属工具代码不误伤）。
//
// ⚠️ 保真复刻说明（MIG-012-A1 迁移时发现的原 bash 缺陷）：
// bash 版模式 "Replace('\\', '/')" 经双引号转义为 Replace('\', '/')，GNU BRE 把
// \' 解析为字面单引号 → 实际匹配的是空参数形态 Replace('', '/')，与 C# 源码真实
// 归一化写法 Replace('\\', '/') 错位——实际恒不命中，效果 = 新增 Path.GetFileName*
// 即恒 WARN。本复刻保持字面匹配（行为等价），是否修复为真实检测交主线程裁决。
if (diffRange is null)
{
    Console.WriteLine($"{Yellow}SKIP{Nc} PDDD-G24: 变更集基线不可解析（无暂存且 HEAD~1 不存在——孤儿分支/初始提交/浅克隆），不判定路径归一化（非 PASS）");
    warned++;
}
else
{
    // 只看 diff 新增行（^+，含 +++ 头行——与 bash grep "^+" 同口径）
    var plusLines = ToLines(changedDiff).Where(l => l.StartsWith('+')).ToList();
    // bash BRE "Path\.GetFileName\(WithoutExtension\)\?" 等价正则
    var g24Rx = new Regex(@"Path\.GetFileName(WithoutExtension)?");
    var g24Hits = plusLines.Count(l => g24Rx.IsMatch(l));
    if (g24Hits > 0)
    {
        // 保真复刻：匹配字面 Replace('', '/')（bash 实际语义，见上 ⚠️）
        var g24Normalized = plusLines.Count(l => l.Contains("Replace('', '/')"));
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
