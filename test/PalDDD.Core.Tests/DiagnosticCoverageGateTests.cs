using System.Text.RegularExpressions;

namespace PalDDD.Core.Tests;

/// <summary>
/// 诊断覆盖门禁 — 每条编译期诊断（PDDD/PALMSG/PALENUM/PALID）都必须有断言级测试。
/// <para>
/// 背景（mutation 实证）：PALENUM004/PALID003 长期只有一行"镜像 PALMSG006"注释而无断言，
/// 破坏其检测实现后 289/289 全绿——诊断行为处于零守护状态。本门禁防止同类缺口再生：
/// 新增诊断若未补断言测试，此测试直接失败。
/// </para>
/// <para>
/// 判据：诊断 ID 出现在测试源码的断言表达式中（<c>Id == "X"</c> /
/// <c>Id).IsEqualTo("X")</c> / <c>HasId("X")</c>），仅出现在注释里不算覆盖。
/// </para>
/// </summary>
public sealed class DiagnosticCoverageGateTests
{
    private static readonly string Root = FindRepositoryRoot();

    /// <summary>诊断定义所在源码目录（目录级扫描，新增文件自动纳入）。</summary>
    private static readonly string[] s_diagnosticSourceDirs =
    [
        "src/PalDDD.Analyzers",
        "src/PalDDD.Core.SourceGen",
    ];

    /// <summary>诊断 ID 数量下限——提取逻辑失效（正则失配返回空集）时此断言兜底。</summary>
    private const int MinimumDiagnosticCount = 38;

    [Test]
    public async Task EveryDiagnostic_HasAssertionLevelTest()
    {
        var ids = s_diagnosticSourceDirs
            .SelectMany(dir => EnumerateSourceFiles(Path.Combine(Root, dir)))
            .SelectMany(path => ExtractDiagnosticIds(File.ReadAllText(path)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        await Assert.That(ids.Count).IsGreaterThanOrEqualTo(MinimumDiagnosticCount);

        var testSources = EnumerateSourceFiles(Path.Combine(Root, "test"))
            .Where(path => !Path.GetFileName(path).Contains("DiagnosticCoverageGate", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        var uncovered = ids
            .Where(id => !testSources.Any(source => HasAssertion(source, id)))
            .ToList();

        await Assert.That(uncovered).IsEmpty();
    }

    /// <summary>诊断定义源文件（排除 obj/bin 生成物）。</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string directory)
        => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    /// <summary>提取源码中的诊断 ID 字面量（如 "PALENUM004"）。</summary>
    private static IEnumerable<string> ExtractDiagnosticIds(string source)
        => Regex.Matches(source, @"""(PDDD|PALMSG|PALENUM|PALID)\d{3}""")
            .Select(match => match.Value.Trim('"'));

    /// <summary>
    /// 断言级覆盖判定——剥注释后逐行匹配：注释/XML 文档提及不算，否定形态不算。
    /// <para>2026-09-10 加固（实跑审计「验证验证者」）：原实现直接对整个源文件跑正则，
    /// 注释行 / XML doc / 否定断言（<c>.IsFalse()</c>）中的 <c>Id == "X"</c> 均被判为覆盖——
    /// 与本门禁"仅注释提及不算"的自述判据矛盾（假绿）。现改为逐行剥 <c>//</c> 注释 +
    /// 排除否定标记后再匹配；白名单补 <c>IsEquivalentTo</c>（等价断言此前被误判为未覆盖）。</para>
    /// </summary>
    private static bool HasAssertion(string testSource, string id)
    {
        foreach (var rawLine in testSource.Split('\n'))
        {
            var line = StripLineComment(rawLine);

            // 否定形态排除：证明"不匹配"的断言不构成覆盖
            if (line.Contains(".IsFalse()", StringComparison.Ordinal)
                || line.Contains("IsNotEqualTo", StringComparison.Ordinal))
            {
                continue;
            }

            if (Regex.IsMatch(line, $@"Id\s*==\s*""{id}""")
                || Regex.IsMatch(line, $@"Id\s*\)\s*\.Is(?:EquivalentTo|EqualTo)\(\s*""{id}""")
                || Regex.IsMatch(line, $@"HasId\(\s*""{id}"""))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>剥除行内 <c>//</c> 注释（含 <c>///</c> XML 文档）——注释提及不计为覆盖。</summary>
    private static string StripLineComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PalDDD.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate PalDDD.slnx.");
    }
}
