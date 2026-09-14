// ============================================================================
// xml-guard.cs——XML 良构性守卫（ITM：2026-09-13 实证缺口）
//
// 动机（实证）：提交 21549d3「卫生:bench 项目分析器豁免补齐」在注释里写入
// `<!-- CA1031:--verify-persist … -->`——XML 注释禁止出现 `--`，导致
// bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj 无法加载（MSB4025），
// **全仓构建失败**。而既有门禁（secret-scan / encoding-gate / guard）均不校验
// XML 良构性：encoding-gate 只看字节编码（CR/BOM/mojibake），guard 只在 .cs 入
// 暂存集时触发。.csproj/.props 的 XML 合法性此前只由 CI 的 build 步骤事后兜底。
// 本脚本把该检查前移到提交环节。
//
// 覆盖扩展名：.csproj .props .slnx .targets（构建输入——错则全仓不可构建）
//            .xml（数据文件——错则运行时才炸）
//
// 用法（在仓库根执行）：
//   dotnet run scripts/xml-guard.cs              校验暂存集（pre-commit 用）
//   dotnet run scripts/xml-guard.cs -- --all     校验全仓（审计用）
//   dotnet run scripts/xml-guard.cs -- --selftest  自测（解析判定单元验证）
//
// 退出码：0=全部良构；1=有文件非良构；2=仓库根定位失败。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是门禁固定协议行
// （PASS/FAIL 被人读与 grep 消费），固定中文非用户可配文案——沿 secret-scan.cs
// / verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

// Justification: CA1031 禁止宽泛 catch；自测临时目录的清理失败不得翻转自测结论
// （清理是收尾动作，与判定无关）。限定在自测 finally 内。
#pragma warning disable CA1031

using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest"))
{
    return SelfTest();
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

string[] extensions = [".csproj", ".props", ".slnx", ".targets", ".xml"];

var targets = args.Contains("--all")
    ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal))
        .Where(f => !IsUnderExcludedDir(ToPosix(f)))
        .Where(f => !ToPosix(f).Contains("/bin/") && !ToPosix(f).Contains("/obj/"))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList()
    : GitStaged().Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal)).ToList();

if (targets.Count == 0)
{
    Console.WriteLine("PASS 无待校验的 XML 文件");
    return 0;
}

var bad = new List<string>();
foreach (var f in targets)
{
    var (ok, error) = ValidateXml(f);
    if (!ok) bad.Add($"{ToPosix(f)} —— {error}");
}

if (bad.Count > 0)
{
    Console.Error.WriteLine($"FAIL {bad.Count} 个 XML 文件非良构（常见根因：注释内出现 `--` 或注释以 `-` 结尾）：");
    foreach (var b in bad) Console.Error.WriteLine($"  {b}");
    return 1;
}

Console.WriteLine($"PASS {targets.Count} 个 XML 文件全部良构（{string.Join(" ", extensions)}）");
return 0;

// ══════════════ 判定 ══════════════

// 良构性判定：解析失败即非良构。返回可读错误（含行列），供提交者直接定位。
static (bool Ok, string Error) ValidateXml(string path)
{
    try
    {
        XDocument.Load(path);
        return (true, "");
    }
    catch (XmlException ex)
    {
        return (false, $"{ex.Message}（行 {ex.LineNumber} 列 {ex.LinePosition}）");
    }
    catch (IOException ex)
    {
        return (false, $"读取失败：{ex.Message}");
    }
    catch (UnauthorizedAccessException ex)
    {
        return (false, $"读取失败：{ex.Message}");
    }
}

// 排除 .ai（独立 git 仓库）与 samples 下的第三方产物目录
static bool IsUnderExcludedDir(string posixPath) =>
    posixPath.Contains("/.ai/", StringComparison.Ordinal) ||
    posixPath.StartsWith(".ai/", StringComparison.Ordinal);

static List<string> GitStaged()
{
    var psi = new ProcessStartInfo("git", "diff --cached --name-only --diff-filter=ACMR")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false,
    };
    try
    {
        using var p = Process.Start(psi);
        if (p is null) return [];
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0
            ? output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList()
            : [];
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return []; // git 不可用——让行，不阻塞提交
    }
}

static string ToPosix(string path) => path.Replace('\\', '/');

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

    var tmp = Path.Combine(Path.GetTempPath(), "xml-guard-selftest-" + Guid.NewGuid().ToString("N"));

    try
    {
        Directory.CreateDirectory(tmp);

        // 坏例：注释内含 `--`（本轮实证的真实形态）
        var badComment = Path.Combine(tmp, "bad-comment.csproj");
        File.WriteAllText(badComment, "<Project>\n  <!-- CA1031:--verify-persist -->\n</Project>\n");
        Case("识别注释内含 `--` 的非良构文件", !ValidateXml(badComment).Ok);

        // 坏例：注释以 `-` 结尾
        var badTail = Path.Combine(tmp, "bad-tail.csproj");
        File.WriteAllText(badTail, "<Project>\n  <!-- 结尾破折号 --->\n</Project>\n");
        Case("识别注释以 `-` 结尾的非良构文件", !ValidateXml(badTail).Ok);

        // 坏例：标签未闭合
        var badTag = Path.Combine(tmp, "bad-tag.props");
        File.WriteAllText(badTag, "<Project><PropertyGroup></Project>\n");
        Case("识别标签未闭合的非良构文件", !ValidateXml(badTag).Ok);

        // 好例：合法注释（单破折号，无 `--`）
        var good = Path.Combine(tmp, "good.csproj");
        File.WriteAllText(good, "<Project>\n  <!-- verify-persist 的 catch 是脚本语义 -->\n  <PropertyGroup />\n</Project>\n");
        Case("放行合法 XML（负向对照——防判定恒为 false）", ValidateXml(good).Ok);

        // 好例：含 CDATA 与转义实体
        var good2 = Path.Combine(tmp, "good2.xml");
        File.WriteAllText(good2, "<root><a><![CDATA[--raw--]]></a></root>\n");
        Case("放行 CDATA 中的 `--`（CDATA 不受注释限制）", ValidateXml(good2).Ok);
    }
    finally
    {
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* 清理失败不翻转结论 */ }
    }

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
