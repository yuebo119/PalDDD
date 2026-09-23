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

// 2026-09-13 增：本门禁有已证实的空转史（CI 注释记「ITM-648 凭据门禁实为 no-op」），
// 且其三层白名单写松一点就会静默放过真实凭据——漏报与「确实没有凭据」输出一致。
// 故补自证能力，覆盖两模式判定与白名单的每一条边界（--selftest 不依赖仓库与 git）。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

// ─── 仓库根定位 ───
var root = FindRepoRoot();

// ─── git ls-files 收集跟踪文件（Process 调用），按扩展名过滤（大小写不敏感）───
// 全仓扫描修复（fail-open）：git 调用失败原本返回空列表，而调用方把空列表渲染成
// 「PASS 无可扫描的跟踪文件」——git 缺失/非 git 仓库时门禁静默失效却报绿。
// 现在失败返回 null，调用方据此 fail-closed。
var trackedAll = GitLsFiles(root);
if (trackedAll is null)
{
    Console.WriteLine("FAIL 无法枚举跟踪文件（git 不可用或 ls-files 非零退出）——凭据扫描未真正执行，拒绝报绿");
    return 1;
}
// 可扫描扩展名白名单——审计 2026-09-20 S1：原白名单只覆盖 14 类，`.slnx`/`.targets`/
// `.cake`/`.http`/`.jsonc`/`.psm1`/`.psd1`/`.sln`/`.editorconfig` 等配置与脚本载体不在
// 扫描面——真实凭据若写进 `.http` 请求文件的 Authorization 头或 `.cake` 构建脚本的连接串，
// 门禁输出与"确实没有凭据"完全一致（静默漏报）。本轮补齐配置与脚本载体。
// 注意：本白名单只决定"哪些文件被读"，判定强度仍由 BuildPatterns() 的三层白名单
// （占位符/示例值/文档措辞）决定——扩展名放宽不会把占位符误判为泄露。
var trackedFiles = trackedAll
    .Where(f => Regex.IsMatch(f, "\\.(json|jsonc|cs|sh|py|yml|yaml|props|targets|config|toml|env|xml|ps1|psm1|psd1|txt|md|csproj|slnx|sln|cake|http|editorconfig)$",
        RegexOptions.IgnoreCase))
    .ToList();
if (trackedFiles.Count == 0)
{
    // 空输入假绿同类修复：git 成功但一个可扫描文件都没有，说明仓库/过滤条件异常，
    // 此时「0 命中」不构成任何安全结论。协议行走 stdout（同 PASS/SUSPECT，被 grep 消费）。
    Console.WriteLine("FAIL 受跟踪文件中无任何可扫描扩展名的文件——输入为空，扫描无意义（fail-closed）");
    return 1;
}

var suspects = new List<string>();
var patterns = BuildPatterns();

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
        foreach (var mode in JudgeLine(content, patterns))
            suspects.Add($"SUSPECT [{mode}] {file}:{lineno}");
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

// git ls-files（Process 调用）。失败返回 null——与「成功但无文件」的空列表严格区分，
// 调用方据此 fail-closed（全仓扫描修复，见调用点注释）。
// 不重定向 stderr：重定向却不排空时，子进程写满 stderr 管道会与父进程的
// stdout ReadToEnd 互等（经典死锁）；git 的 stderr 是进度/诊断信息，直出更可观察
// （同 changelog-facts.cs 的 ITM-663 取舍）。
static List<string>? GitLsFiles(string workingDir)
{
    var psi = new ProcessStartInfo("git", "ls-files")
    {
        RedirectStandardOutput = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = workingDir,
    };
    Process p;
    try
    {
        p = Process.Start(psi)!;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        Console.Error.WriteLine($"  git 启动失败：{ex.Message}");
        return null;
    }
    using (p)
    {
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0
            ? output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList()
            : null;
    }
}

// ══════════════ 判定（纯函数，供 --selftest 覆盖）══════════════

// 模式集（构造一次，供主循环与自测共用）
static ScanPatterns BuildPatterns() => new(
    // 模式 1：已知云密钥/令牌前缀格式（极高信度，零误报）
    // 审计 2026-09-20 S1 补充：Bearer/JWT 形态。`.http` 请求文件的 Authorization 头是
    // 真实凭据的高发载体（原白名单既不含 .http 扩展名也不含该形态，双重漏报）；
    // JWT 的 eyJ 前缀 + 三段 base64url 结构信度足够高，且文档/示例极少长这样。
    Key: new Regex(
        "AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,}"
        + "|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----"
        + "|eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}"),
    // 模式 2：连接串内嵌真实密码——同行同时含 主机键 与 密码键
    Conn: new Regex("(Host|Server|Data Source|数据源)\\s*=.*(Password|Pwd)\\s*=", RegexOptions.IgnoreCase),
    // 密码值提取（= 后到 ; 或 " 或空白前；可选起始引号）
    Pwd: new Regex("(password|pwd)\\s*=\\s*\"?[^;\", ]+", RegexOptions.IgnoreCase),
    // 白名单 1：占位词整词 / 示例语义子串（大小写不敏感）
    Allow1: new Regex(
        "^(test|postgres|root|guest|password|pass|pwd|secret|example|changeme|yourpassword|probe|localhost|none|default)$"
        + "|example|sample|demo|placeholder|dummy|fake|mock", RegexOptions.IgnoreCase),
    // 白名单 2：示例密码形态（<词>-pass / secret123 / <占位词>NNN）
    Allow2: new Regex("^([a-z0-9]+-)?pass$|^secret[0-9]+$|^[a-z]+-pass$", RegexOptions.IgnoreCase),
    Digit: new Regex("[0-9]"));

// 判定单行（纯函数）：返回命中的模式名列表，空列表=放行。
// 两模式独立判定（原 bash 为两遍独立循环：同一行可各报一次，不短路）。
static List<string> JudgeLine(string content, ScanPatterns p)
{
    var hits = new List<string>();
    if (p.Key.IsMatch(content)) hits.Add("已知密钥格式");
    if (!p.Conn.IsMatch(content)) return hits;

    // 提取 Password/Pwd 的值（第一个匹配；去掉前缀与可选起始引号）
    var m = p.Pwd.Match(content);
    if (!m.Success) return hits;
    var value = Regex.Replace(m.Value, "^[^=]*=\\s*\"?", "");
    if (value.Length == 0) return hits;
    // 三层白名单：占位词/示例语义 → 示例密码形态 → 无数字且长度 <10
    if (p.Allow1.IsMatch(value)) return hits;
    if (p.Allow2.IsMatch(value)) return hits;
    if (!p.Digit.IsMatch(value) && value.Length < 10) return hits;
    hits.Add("连接串内嵌密码");
    return hits;
}

// ══════════════ 自测 ══════════════

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

    var p = BuildPatterns();

    // ─── 「自指陷阱」规避（与 encoding-gate 的 \uXXXX 转义同源）───
    // 本文件本身就在 secret-scan 的扫描面内（scripts/*.cs）。因此**所有应被检出的
    // 病态样本必须运行期拼装**，否则本文件源码自身命中——实测：首版用字面量写入
    // 后，真实运行报 8 处命中全部落在 scripts/secret-scan.cs（本门禁拦住了自己的
    // 测试数据）。因此密钥值拆前缀、连接串破坏 Host=/Password= 的同行相邻性。
    static string Join(params string[] parts) => string.Concat(parts);
    static string ConnLine(string host, string pwd) => Join(host, ";Pass", "word=", pwd);

    var fakeAwsKey = Join("AK", "IA", "IOSFODNN7EXAMPLE");
    var longPwd = Join("Xk9m", "Q2vB7pLw");

    // 模式 1：已知密钥前缀（各自独立，防某条分支被摘掉）
    Case("模式1 AWS AKIA", JudgeLine($"var k = \"{fakeAwsKey}\";", p) is ["已知密钥格式"]);
    Case("模式1 GitHub ghp_", JudgeLine("token = ghp_" + new string('a', 36), p) is ["已知密钥格式"]);
    Case("模式1 PEM 私钥块头", JudgeLine(Join("-----BEGIN ", "RSA ", "PRIVATE KEY-----"), p) is ["已知密钥格式"]);
    // JWT 形态（2026-09-20 S1）：三段 base64url 结构。自指陷阱规避同上方——头两段
    // 运行期拼装（eyJ 是 base64('{"') 的固定前缀，写进本文件会被自身门禁命中）。
    Case("模式1 JWT 三段结构", JudgeLine(
        $"Authorization: Bearer {Join("ey", "JhbGciOiJIUzI1NiJ9")}.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N", p)
        is ["已知密钥格式"]);
    Case("模式1 JWT 两段不命中", JudgeLine(
        Join("ey", "JhbGciOiJIUzI1NiJ9") + ".eyJzdWIiOiIxMjM0NTY3ODkwIn0", p).Count == 0);
    Case("模式1 不误报普通文本", JudgeLine("这是一个普通的领域事件注释", p).Count == 0);
    Case("模式1 不误报短 AKIA 前缀", JudgeLine(Join("AK", "IA", "123"), p).Count == 0);

    // 模式 2：命中与白名单三条边界
    Case("模式2 命中：连接串 + 长密码", JudgeLine(ConnLine("Host=db", longPwd), p) is ["连接串内嵌密码"]);
    Case("白名单1 占位词整词放行", JudgeLine(ConnLine("Host=db", "test"), p).Count == 0);
    Case("白名单1 示例语义子串放行", JudgeLine(ConnLine("Host=db", "myExamplePwd"), p).Count == 0);
    Case("白名单2 示例密码形态放行", JudgeLine(ConnLine("Host=db", Join("secret", "123")), p).Count == 0);
    Case("白名单3 短且无数字放行", JudgeLine(ConnLine("Host=db", "abc"), p).Count == 0);
    // 关键边界：短但有数字 → 三层白名单都不覆盖 → 必须命中（写松此处即静默漏报）
    Case("白名单3 边界：短但有数字则命中", JudgeLine(ConnLine("Host=db", Join("a", "1")), p) is ["连接串内嵌密码"]);

    // 模式 2 前置条件：主机键与密码键必须同行共存
    Case("仅主机键不命中", JudgeLine("Host=db;User=sa", p).Count == 0);
    Case("仅密码键不命中", JudgeLine(Join("Pass", "word=", longPwd), p).Count == 0);
    Case("Data Source 亦识别", JudgeLine(ConnLine("Data Source=s", longPwd), p) is ["连接串内嵌密码"]);

    // 两模式独立：同一行可各报一次（不短路）
    Case("两模式可同时命中", JudgeLine(ConnLine("Host=db", longPwd) + ";Key=" + fakeAwsKey, p).Count == 2);

    // 大小写不敏感（原实现含 RegexOptions.IgnoreCase）
    Case("主机键大小写不敏感", JudgeLine(ConnLine("host=db", longPwd), p) is ["连接串内嵌密码"]);

    // 全仓扫描修复：git 调用失败必须返回 null（区别于「成功但无文件」的空列表）——
    // 否则调用方把「门禁没跑」渲染成「扫描干净」。用不存在的工作目录触发启动失败。
    Case("GitLsFiles 启动失败返回 null 而非空列表",
        GitLsFiles(Path.Combine(Path.GetTempPath(), "secret-scan-no-such-" + Guid.NewGuid().ToString("N"))) is null);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}

// ══════════════ 类型 ══════════════

internal sealed record ScanPatterns(
    Regex Key, Regex Conn, Regex Pwd, Regex Allow1, Regex Allow2, Regex Digit);
