// ============================================================================
// secret-scan.cs——受跟踪文件硬编码凭据扫描（MIG-012-A2，2026-09-11）
// 由 scripts/secret-scan.sh（57 行）等价迁移为 C#（dotnet file-based app）。
//
// 用法：dotnet run scripts/secret-scan.cs
// 退出码：0=无高置信凭据；1=发现疑似凭据；2=仓库根定位失败。
//
// 设计纪律（宁缺毋滥——低精度门禁比无门禁更坏，会被噪声淹没后失效）：
//   1. 只报高置信度形态（云密钥前缀格式、连接串密码、私钥块头），
//      不报 token:/secret: 等泛词赋值（实测在干净仓库产生 52 处误报）。
//   2. 只扫跟踪文件（git ls-files，Process 调用），与 SE2 口径一致。
//   3. 输出不含凭据明文——仅打印 文件:行号:模式名（P0 #1 防二次泄露）。
//
// 双模式（与原 bash 逐项对应）：
//   模式 1：已知云密钥/令牌前缀格式（AWS AKIA/ASIA+16 位、GitHub ghp_/
//           github_pat_、Slack xox[baprs]-、OpenAI sk-、PEM 私钥块头）
//   模式 2：连接串内嵌真实密码（同行含 主机键+密码键 且密码值不在
//           三层白名单：占位词整词/示例语义子串 → 示例密码形态 → 无数字且短值）
//
// 迁移说明：仓库根定位同 verify-ai.cs（CWD 向上找 PalDDD.slnx 兜底
// BaseDirectory）；零 package 依赖；正则全 ASCII 与 POSIX ERE 兼容。
// ============================================================================

// Justification: CA1303 要求 UI 文字走资源表本地化；本脚本输出是 CI 门禁的
// 固定协议行（PASS/SUSPECT 被 grep 消费），固定中文非用户可配文案——沿
// vuln-scan.cs / verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文输出会乱码——对齐 bash printf UTF-8
Console.OutputEncoding = Encoding.UTF8;

// ─── 仓库根定位 ───
var root = FindRepoRoot();

// ─── git ls-files 收集跟踪文件（Process 调用），按扩展名过滤（大小写不敏感）───
var trackedFiles = GitLsFiles(root)
    .Where(f => Regex.IsMatch(f, "\\.(json|cs|sh|py|yml|yaml|props|config|toml|env|xml|ps1|txt|md|csproj)$",
        RegexOptions.IgnoreCase))
    .ToList();
if (trackedFiles.Count == 0)
{
    Console.WriteLine("PASS 无可扫描的跟踪文件");
    return 0;
}

var suspects = new List<string>();

// ─── 模式 1：已知云密钥/令牌前缀格式（极高信度，零误报）───
var keyPattern = new Regex(
    "AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,}"
    + "|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----");

// ─── 模式 2：连接串内嵌真实密码（SE2 原始场景）───
// 同一行同时含 主机键(Host=/Server=/Data Source=/数据源) 与 密码键(Password=/Pwd=)
var connPattern = new Regex("(Host|Server|Data Source|数据源)\\s*=.*(Password|Pwd)\\s*=",
    RegexOptions.IgnoreCase);
// 密码值提取（= 后到 ; 或 " 或空白前；可选起始引号）
var pwdPattern = new Regex("(password|pwd)\\s*=\\s*\"?[^;\", ]+", RegexOptions.IgnoreCase);
// 白名单 1：占位词整词 / 示例语义子串（大小写不敏感）
var allow1 = new Regex(
    "^(test|postgres|root|guest|password|pass|pwd|secret|example|changeme|yourpassword|probe|localhost|none|default)$"
    + "|example|sample|demo|placeholder|dummy|fake|mock", RegexOptions.IgnoreCase);
// 白名单 2：示例密码形态（<词>-pass / secret123 / <占位词>NNN）
var allow2 = new Regex("^([a-z0-9]+-)?pass$|^secret[0-9]+$|^[a-z]+-pass$", RegexOptions.IgnoreCase);
var digitPattern = new Regex("[0-9]");

foreach (var file in trackedFiles)
{
    // 容错 UTF-8 读（等价 grep 字节匹配语义；含 NUL 视为二进制跳过 = grep -I）
    string text;
    try { text = File.ReadAllText(Path.Combine(root, file), new UTF8Encoding(false, false)); }
    catch (IOException) { continue; }
    catch (UnauthorizedAccessException) { continue; }
    if (text.Contains('\0')) continue;

    var lineno = 0;
    foreach (var line in text.Split('\n'))
    {
        lineno++;
        var content = line.TrimEnd('\r');
        // 两模式独立判定（原 bash 为两遍独立循环：同一行可各报一次，不短路）
        if (keyPattern.IsMatch(content))
            suspects.Add($"SUSPECT [已知密钥格式] {file}:{lineno}");
        if (!connPattern.IsMatch(content)) continue;

        // 提取 Password/Pwd 的值（第一个匹配；去掉前缀与可选起始引号）
        var m = pwdPattern.Match(content);
        if (!m.Success) continue;
        var value = Regex.Replace(m.Value, "^[^=]*=\\s*\"?", "");
        if (value.Length == 0) continue;
        // 三层白名单：占位词/示例语义 → 示例密码形态 → 无数字且长度 <10
        if (allow1.IsMatch(value)) continue;
        if (allow2.IsMatch(value)) continue;
        if (!digitPattern.IsMatch(value) && value.Length < 10) continue;
        suspects.Add($"SUSPECT [连接串内嵌密码] {file}:{lineno}");
    }
}

if (suspects.Count > 0)
{
    foreach (var s in suspects) Console.WriteLine(s);
    Console.WriteLine();
    Console.WriteLine($"发现 {suspects.Count} 处疑似硬编码凭据（上方仅列位置，未打印明文）。");
    Console.WriteLine("请改用环境变量或用户机密（User Secrets / CI Secrets），勿入库。");
    return 1;
}

Console.WriteLine($"PASS 受跟踪文件无高置信硬编码凭据（{trackedFiles.Count} 文件已扫描）");
return 0;

// ══════════════ static 局部函数 ══════════════

// 仓库根定位：CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底（file-based app
// 的 BaseDirectory 实测指向 %TEMP%\dotnet\runfile\...，向上不可达仓库根）
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

// git ls-files（Process 调用；非零退出码返回空——非 git 仓库/异常等同无可扫文件）
static List<string> GitLsFiles(string workingDir)
{
    var psi = new ProcessStartInfo("git", "ls-files")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = workingDir,
    };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode == 0
        ? output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList()
        : [];
}
