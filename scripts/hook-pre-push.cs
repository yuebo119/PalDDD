// ============================================================================
// hook-pre-push.cs——pre-push 钩子判定层（2026-10-08，C# 化替换 42 行 bash）
// ① main 分支保护（merge-base --is-ancestor：dev 已合入 main 才允许从 main 推送）
// ② 同步提示（ITM-808：多会话积压的验证债可见性，信息性不阻断）
// ③ gate-lite CI 预览（快速门禁降级路径）
// .githooks/pre-push 退化为启动器；本文件持有全部判定。
//
// 用法：dotnet run scripts/hook-pre-push.cs（由 .githooks/pre-push 启动器 exec）
// 退出码：0=通过；1=拦截。
// ============================================================================

// Justification: CA1303——固定中文协议行（与原 bash 版一致），沿 verify-ai.cs 先例。
#pragma warning disable CA1303

using System.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal))
{
    return RunSelftest();
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

// ── ① main 分支保护 ──
var branch = Git("rev-parse", "--abbrev-ref HEAD");
if (branch == "main")
{
    // merge-base --is-ancestor：dev 是否已合入 main（原 hook 的 ITM-671 修复——
    // 直接比 HEAD 不同会因 merge commit 恒真误拦）
    var merged = GitExitCode("merge-base", "--is-ancestor", "dev", "main");
    if (merged != 0)
    {
        Console.WriteLine("⛔ dev 未合入 main，请先合并：git checkout main && git merge dev");
        Console.WriteLine("   （如果不需要合并，说明你在 main 上直接做了修改——这是违规的）");
        return 1;
    }
}

// ── ② 同步提示（信息性不阻断）──
// 治「多批次并行开发后长时间不推送」的验证债：本地积压越多，CI（尤其 aot-verify
// 真发布）对积压代码的验证缺口越大（首席审计 O-1：曾实测积压 49 提交）。
var upstream = Git("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
if (upstream.Length > 0)
{
    var behind = ParseCount(Git("rev-list", "--count", $"HEAD..{upstream}"));
    var ahead = ParseCount(Git("rev-list", "--count", $"{upstream}..HEAD"));
    if (behind > 0)
        Console.WriteLine($"⚠️  pre-push：本地落后 {upstream} {behind} 个提交——推送可能被拒或需先 rebase（多会话并行写冲突风险，open-items O-2）。");
    if (ahead >= 10)
        Console.WriteLine($"⚠️  pre-push：本次推送承载 {ahead} 个积压提交——CI（含 aot-verify 真发布）将首次验证这批代码，失败排查面较大；建议日常小批推送。");
}

// ── ③ gate-lite CI 预览 ──
Console.WriteLine("🔍 gate-lite（CI 降级门禁预览）…");
var psi = new ProcessStartInfo("dotnet", "run scripts/gate-lite.cs")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    WorkingDirectory = root,
};
using var p = Process.Start(psi)!;
Console.Out.Write(p.StandardOutput.ReadToEnd());
Console.Error.Write(p.StandardError.ReadToEnd());
p.WaitForExit();
if (p.ExitCode != 0)
{
    Console.WriteLine("⛔ gate-lite 失败");
    return 1;
}

Console.WriteLine("✅ pre-push 通过");
return 0;

// ══════════════ static 局部函数 ══════════════

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
    Console.Error.WriteLine("错误：未定位到仓库根（无 PalDDD.slnx）");
    Environment.Exit(2);
    return "";
}

// git 快速调用：输出首行（异常/非零返回空串——提示类信息失败不阻断推送）
static string Git(params string[] arguments)
{
    var psi = new ProcessStartInfo("git", string.Join(' ', arguments))
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd().Trim();
    p.WaitForExit();
    return p.ExitCode == 0 ? stdout : "";
}

static int GitExitCode(params string[] arguments)
{
    var psi = new ProcessStartInfo("git", string.Join(' ', arguments))
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };
    using var p = Process.Start(psi)!;
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode;
}

static int ParseCount(string s) =>
    int.TryParse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

// ─── selftest：计数解析纯函数正负例（gate-audit SELFTEST 判定面）───
static int RunSelftest()
{
    var failures = new List<string>();
    void Case(bool actual, bool expected, string name)
    {
        if (actual == expected) Console.WriteLine($"PASS ST-HPP {name}");
        else failures.Add(name);
    }

    Case(ParseCount("7") == 7, true, "计数解析数字");
    Case(ParseCount(" 12 ") == 12, true, "计数解析含空白");
    Case(ParseCount("") == 0, true, "空串兜底 0（git 失败不阻断提示）");
    Case(ParseCount("abc") == 0, true, "非数字兜底 0");

    Console.WriteLine($"═══════ HOOK-PRE-PUSH SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var f in failures) Console.WriteLine("  FAIL " + f);
    return failures.Count == 0 ? 0 : 1;
}
