// ============================================================================
// verify-action-items.cs——行动项标识符/路径存在性验证（MIG-012-B2 双镜像合一，2026-09-11）
// 由 scripts/verify-action-items.sh 与 .ai/scripts/verify-action-items.sh 等价迁移
// （两份 93 行 bash 逐字节一致、仅脚本位置不同——合一后从单一 .cs 位置出发，
//  根/.ai 双镜像不再需要两份维护）。
//
// 用法：dotnet run scripts/verify-action-items.cs -- <action-items-file>
// 退出码：0=全部找到；1=有缺失（FAIL 行逐一列出）；2=用法错误。
//
// 三层 token 分类（与 bash 版逐条对照）：
//   1) 忽略词：P0-P3 / AUD-n / ITM(-n) / PASS / FAIL / WARN / SKIP /
//      urgent / near / future / assess（评审状态词不是标识符）
//   2) 散文（跳过）：
//      a. 含 () < > = ? * { } " 或空白字符——SQL 片段/表达式/带引号值
//         （2026-08-15 实践教训：散文引用当标识符 grep 产生 9 处误报）
//      b. 纯十六进制 commit hash（7-40 位）
//      c. File.cs:NN 行号引用（2026-08-16 二轮优化）
//      d. --flag 命令行开关
//      e. 含 / 但既无已知扩展名也不以已知目录开头（如 Xxx/Yyy 方法对）
//   3) 其余按路径/标识符验证：
//      路径（含 / 或已知扩展名）→ 文件存在；不存在且无 / 时 git grep 兜底
//      （无目录前缀的模板名，正文有引用则放行——元审计脚本#30 口径，含 .ai）
//      标识符 → git grep -F 存在性（src test scripts docs .github .ai 五路径）
//
// 与 bash 版的口径差异（仅一处，仓库内运行无影响）：
//   bash _ai_root_find 从脚本自身位置向上找 PalDDD.slnx；file-based app 编译进
//   用户临时缓存（Temp/dotnet/runfile/<hash>），运行时取不到 .cs 源路径——
//   本版从当前目录向上查找。仓库内任意子目录运行两者行为一致
//   （bash 版同样只支持仓库内运行；仓外运行报错退出 2 而非 bash 的隐式错乱）。
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Justification: CA1303 要求 UI 文案走资源表；本工具输出是 CI 控制台关键字
// （FAIL/中文计数行被 bash grep 与人眼消费），字面量必要——沿 sibling-map.cs 先例
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文/制表线输出乱码——对齐 bash UTF-8
Console.OutputEncoding = Encoding.UTF8;

// 仓库根发现：向上找含 PalDDD.slnx 的目录（等价 bash _ai_root_find，锚点为 cwd——见文件头）
var root = FindRepoRoot();
Directory.SetCurrentDirectory(root);

if (args.Length != 1)
{
    Console.Error.WriteLine("用法：dotnet run scripts/verify-action-items.cs -- <action-items-file>");
    return 2;
}

var actionFile = args[0];
if (!File.Exists(actionFile))
{
    Console.Error.WriteLine($"错误：文件不存在：{actionFile}");
    return 2;
}

Console.WriteLine("═══════ Action Items 验证 ═══════");
Console.WriteLine($"文件：{actionFile}");
Console.WriteLine();

var found = 0;
var missing = 0;
var skipped = 0;

// 反引号 token 提取：逐行匹配 `...`（grep -oE 按行工作，不跨换行——C# Regex
// 默认跨行，必须逐行提取才能对齐），去引号后 sort -u（MSYS 实测序等价
// StringComparer.Ordinal，对照记录见 MIG-012-B2 双跑留档）
var tokens = File.ReadLines(actionFile)
    .SelectMany(line => Regex.Matches(line, "`[^`]+`"))
    .Select(m => m.Value[1..^1])
    .Distinct()
    .OrderBy(t => t, StringComparer.Ordinal)
    .ToList();

foreach (var identifier in tokens)
{
    if (identifier.Length == 0) continue; // bash [ -z ] 同款守卫（正则 1+ 字符下实际不可达，防御保留）
    if (IsIgnoredToken(identifier) || IsProseToken(identifier))
    {
        skipped++;
        continue;
    }

    if (IsPath(identifier))
    {
        if (File.Exists(identifier) || Directory.Exists(identifier))
            found++;
        else if (!identifier.Contains('/') && GitGrepExists(identifier))
            // 无目录前缀的相对文件名（如模板名）——文件不存在但正文有引用则放行
            found++;
        else
        {
            Console.WriteLine($"FAIL 文件不存在：{identifier}");
            missing++;
        }
        continue;
    }

    if (GitGrepExists(identifier))
        found++;
    else
    {
        Console.WriteLine($"FAIL 标识符未找到：{identifier}");
        missing++;
    }
}

Console.WriteLine($"\n找到：{found}  缺失：{missing}  跳过：{skipped}");
Console.WriteLine("═══════ 验证完成 ═══════");
return missing == 0 ? 0 : 1;

// ─── 分类谓词（正则与 bash 版逐字对应）───

static bool IsPath(string t)
    => t.Contains('/') || PathExt().IsMatch(t);

static bool IsIgnoredToken(string t)
    => Ignored().IsMatch(t);

static bool IsProseToken(string t)
{
    // 含标识符/路径中不可能出现的字符（() < > = ? * { } " 与空白）→ 散文。
    // 空白取 bash [:space:] 精确六元（\t \n \v \f \r 与空格），不用 char.IsWhiteSpace
    // （后者含 Unicode 宽空白，会多跳过 bash 版会 grep 的 token）
    if (t.Any(ch => ch is '(' or ')' or '<' or '>' or '=' or '?' or '*' or '{' or '}' or '"'
        || ch is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')) return true;
    if (Hash().IsMatch(t)) return true;      // 纯十六进制 commit hash（7-40 位）
    if (LineNo().IsMatch(t)) return true;    // File.cs:NN 行号引用
    if (t.StartsWith("--", StringComparison.Ordinal)) return true; // --flag 命令行开关
    // 含 / 但既无已知扩展名（此处多一个 sql）也不以已知目录开头（如 Xxx/Yyy 方法对）→ 散文
    if (t.Contains('/') && !PathExtSql().IsMatch(t) && !KnownDir().IsMatch(t)) return true;
    return false;
}

// ─── git grep 存在性（Process 调用，stderr 丢弃等价 2>/dev/null）───

static bool GitGrepExists(string token)
{
    var psi = new ProcessStartInfo("git")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var a in (string[])["grep", "-F", "-q", "--", token,
             "src", "test", "scripts", "docs", ".github", ".ai"])
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode == 0;
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(Environment.CurrentDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "PalDDD.slnx")))
        d = d.Parent!;
    if (d is null)
    {
        Console.Error.WriteLine("错误：未找到仓库根（向上未发现 PalDDD.slnx）——请在仓库内运行");
        Environment.Exit(2);
    }
    return d.FullName;
}

// ─── 惰性正则（单次脚本生命周期，无需 Compiled）───

static Regex PathExt() => new(@"\.(cs|csproj|slnx|md|sh|yml|yaml|props|targets|json|xml)$");
static Regex PathExtSql() => new(@"\.(cs|csproj|slnx|md|sh|yml|yaml|props|targets|json|xml|sql)$");
static Regex Ignored() => new(@"^(P[0-3]|AUD-[0-9]+|ITM(-[0-9]+)?|PASS|FAIL|WARN|SKIP|urgent|near|future|assess)$");
static Regex Hash() => new(@"^[0-9a-f]{7,40}$");
static Regex LineNo() => new(@":[0-9]+$");
static Regex KnownDir() => new(@"^(src|test|docs|scripts|bench|samples|\.ai|\.github|nupkgs)/");
