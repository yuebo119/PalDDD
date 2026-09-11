// ═══════════════════════════════════════════════════════════════
// check-all.cs——全量检查（IDE+CA+编译）（MIG-012-C，2026-09-11）
// 由 scripts/check-all.sh（37 行 bash）等价迁移为 C#（dotnet file-based app）。
//
// 用法（在仓库根执行）：dotnet run scripts/check-all.cs
// 三段：1/3 dotnet format 风格（退出码传播）→ 2/3 CA 分析 build（尾部 3 行 +
//       失败退出码传播）→ 3/3 编译 error CS 计数判定。
// 退出码：0=全过；1=format 违规或 error CS 计数 >0；2/3 段 build 失败透传其退出码。
//
// 迁移说明：
//   1) ITM-233 修复语义保持：dotnet format 退出码必须传播——此前 grep -c || true
//      把 format 失败吞掉；2026-09-09 重建历史（c2586c6 死分支清理引入语法错误）
//      不再重演。--verify-no-changes 保留（check 脚本不得改写工作树）。
//   2) 原死变量判定语义保持：ERROR_COUNT 真正参与判定（非 "error CS" 形式的
//      构建失败已由 2/3 段退出码传播拦截，3/3 段兜住 error CS 计数型失败）。
//   3) IDE_COUNT 口径保持：format 输出（stdout+stderr 合并）中含 "error" 或
//      "warning" 子串的行数（grep -c "error\|warning"，大小写敏感、只计数不判定）。
// ═══════════════════════════════════════════════════════════════

#pragma warning disable CA1303 // 检查脚本协议输出为固定控制台文案，无本地化需求——沿 flaky-parse.cs 先例

using System.Diagnostics;
using System.Text;

// Windows 控制台默认编码非 UTF-8，中文/emoji 会乱码——对齐 bash UTF-8 输出；
// 重定向行尾默认 \r\n（bash 为 \n），统一为 \n 保证双跑逐字节可比
Console.OutputEncoding = Encoding.UTF8;
Console.Out.NewLine = "\n";

Console.WriteLine("═══════ 1/3 IDE 风格 ═══════");
// 对齐 bash：FORMAT_OUTPUT=$(dotnet format ... 2>&1) || FORMAT_EXIT=$?——
// 捕获 stdout+stderr 合并流（MSBuild 错误打 stdout，stderr 通常为空，顺序失真可忽略）
var (formatExit, formatLines) = RunCapture("dotnet", "format style --verify-no-changes PalDDD.slnx");
var ideCount = formatLines.Count(l => l.Contains("error") || l.Contains("warning"));
Console.WriteLine($"  IDE 建议: {ideCount} 项");
if (formatExit != 0)
{
    Console.WriteLine($"  ❌ dotnet format 退出码 {formatExit}——存在格式违规");
    return 1;
}

Console.WriteLine("═══════ 2/3 CA 分析 ═══════");
// 对齐 bash：dotnet build ... 2>&1 | tail -3——打印尾部 3 行；
// pipefail + set -e：build 退出码非零时脚本以该码退出（tail 输出已打印完毕）
var (buildExit, buildLines) = RunCapture("dotnet", "build PalDDD.slnx -c Debug --nologo");
foreach (var line in buildLines.TakeLast(3))
{
    Console.WriteLine(line);
}
if (buildExit != 0) return buildExit;

Console.WriteLine("═══════ 3/3 编译 ═══════");
// 第二次增量 build：退出码不参与判定（对齐 bash grep -c ... || true 兜底——
// 非 error CS 形式的失败已由 2/3 段拦截），只数含 "error CS" 子串的行数
var (_, build2Lines) = RunCapture("dotnet", "build PalDDD.slnx -c Debug --nologo");
var errorCount = build2Lines.Count(l => l.Contains("error CS"));
Console.WriteLine($"  编译错误: {errorCount} 项");
if (errorCount > 0)
{
    Console.WriteLine($"  ❌ 编译存在 {errorCount} 项 error CS");
    return 1;
}
Console.WriteLine("═══ 完成 ═══");
return 0;

// ─── 子进程执行：stdout+stderr 全捕获为行列表，返回退出码 ───
// 异步双流读取避免缓冲区死锁（build 输出量大）；行尾统一由重建方处理
static (int Exit, List<string> Lines) RunCapture(string fileName, string arguments)
{
    var info = new ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,   // dotnet CLI 重定向输出为 UTF-8
        StandardErrorEncoding = Encoding.UTF8,
    };
    using var process = new Process { StartInfo = info };
    var lines = new List<string>();
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) lines.Add(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lines.Add(e.Data); };
    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    process.WaitForExit();
    process.WaitForExit();   // 双调用：确保异步缓冲 flush（Framework 行为差异防御）
    return (process.ExitCode, lines);
}
