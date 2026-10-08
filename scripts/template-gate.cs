// ============================================================================
// template-gate.cs——.pal/prompts 模板编译门禁（v56 机械门禁；2026-10-08 自
// .ai/scripts/template-gate.sh（14 行定位包装）C# 化迁入主仓——判定真源本就是
// 主仓 scripts/template-compile-probe/ 工程，本文件只做仓库根定位 + 转发）。
//
// 根因（v56 设立时）：v53-v55 连续四轮在模板区发现 P1（照抄必炸缺陷）——
// 人工评审概率性拦截不彻底，改用确定性强制：逐块真实编译全部模板代码
//（输出格式段挂 PalDDD 三源生成器 + StrategicDddAnalyzer）。
//
// 用法：dotnet run scripts/template-gate.cs [probe 参数透传]
// 退出码：0=全部可编译；1=存在编译失败；2=仓库根定位失败。
// ============================================================================

using System.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 仓库根定位（CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底——verify-ai.cs 同模式）
string? root = null;
foreach (var start in (string?[])[Environment.CurrentDirectory, AppContext.BaseDirectory])
{
    var dir = start;
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir, "PalDDD.slnx"))) { root = dir; break; }
        dir = Path.GetDirectoryName(dir);
    }
    if (root is not null) break;
}
if (root is null)
{
    Console.Error.WriteLine("错误：未定位到仓库根（无 PalDDD.slnx）——请在仓库内执行");
    return 2;
}

var probeArgs = args.Length > 0 ? " -- " + string.Join(' ', args) : "";
var psi = new ProcessStartInfo("dotnet", $"run --project scripts/template-compile-probe{probeArgs}")
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
return p.ExitCode;
