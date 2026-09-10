using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
/// 判定基于 <b>Roslyn 语法树</b>而非文本正则——注释、字符串字面量、条件编译禁用块
/// 在语法层天然不是表达式节点，跨行调用链也天然成立（见边界矩阵测试）。
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

    /// <summary>
    /// HasAssertion 判定边界矩阵——固化"真断言 vs 伪覆盖"的六类形态，防未来改动引入回归。
    /// <para>2026-09-10 背景（实跑审计第五轮「验证验证者」）：上一版**行级文本匹配**实测出
    /// 2 处回归（跨行链式断言被判未覆盖 = 假红；同行字符串含 <c>//</c> 时断言被误剥 = 假红）
    /// 与 3 处未处理边界（块注释 / raw string / <c>#if false</c> 内的 <c>Id == "X"</c> 被判
    /// 为覆盖 = 假绿）。改用 Roslyn 语法树后五者全部消除，本测试锁定该行为——门禁自身的
    /// 判定精度从此有回归网。</para>
    /// </summary>
    [Test]
    public async Task HasAssertion_BoundaryMatrix_DistinguishesRealFromFakeCoverage()
    {
        const string id = "PALENUM999";

        // ── 真断言：必须判为覆盖（含跨行链式，上一版假红形态）──
        await Assert.That(HasAssertion($"await Assert.That(d.Id == \"{id}\").IsTrue();", id)).IsTrue();
        await Assert.That(HasAssertion($"await Assert.That(diagnostics[0].Id).IsEqualTo(\"{id}\");", id)).IsTrue();
        await Assert.That(HasAssertion($"await Assert.That(diagnostics[0].Id){'\n'}    .IsEqualTo(\"{id}\");", id)).IsTrue();
        await Assert.That(HasAssertion($"await Assert.That(result.Diagnostics[0].Id).IsEquivalentTo(\"{id}\");", id)).IsTrue();
        await Assert.That(HasAssertion($"HasId(\"{id}\");", id)).IsTrue();
        // 同行含 // 的字符串（上一版假红形态）：断言在字符串之后，剥注释不得伤及断言
        await Assert.That(HasAssertion($"var u = \"http://x\"; await Assert.That(d.Id == \"{id}\").IsTrue();", id)).IsTrue();

        // ── 伪覆盖：必须拒绝（注释 / 文件级字符串 / 条件编译 / 否定）──
        await Assert.That(HasAssertion($"// Id == \"{id}\"", id)).IsFalse();
        await Assert.That(HasAssertion($"/* Id == \"{id}\" */", id)).IsFalse();
        await Assert.That(HasAssertion($"/// <summary>覆盖 Id == \"{id}\"</summary>", id)).IsFalse();
        await Assert.That(HasAssertion($"var s = \"\"\"{'\n'}Id == \"{id}\"{'\n'}\"\"\";", id)).IsFalse();
        await Assert.That(HasAssertion($"#if false{'\n'}await Assert.That(d.Id == \"{id}\").IsTrue();{'\n'}#endif", id)).IsFalse();
        await Assert.That(HasAssertion($"await Assert.That(d.Id == \"{id}\").IsFalse();", id)).IsFalse();
        await Assert.That(HasAssertion($"await Assert.That(d.Id).IsNotEqualTo(\"{id}\");", id)).IsFalse();
        // 不相关成员访问：不得因 "Id" 子串误判
        await Assert.That(HasAssertion($"await Assert.That(other.Value).IsEqualTo(\"{id}\");", id)).IsFalse();
        // 非诊断对象的 .Id 断言：ID 字面量虽匹配，接收者非诊断集合（名不含 Diag）——不得计入覆盖
        // （P3 收紧：原 MentionsId 只查任意 .Id，order.Id 这类无关断言会被误判为覆盖）
        await Assert.That(HasAssertion($"await Assert.That(order.Id).IsEqualTo(\"{id}\");", id)).IsFalse();
    }

    /// <summary>
    /// 断言级覆盖判定（Roslyn 语法树，2026-09-10 重写）——注释/字符串/条件编译禁用块
    /// 天然不是表达式节点，跨行调用链天然成立；否定断言（证明"不匹配"）不算覆盖。
    /// </summary>
    private static bool HasAssertion(string testSource, string id)
    {
        var root = CSharpSyntaxTree.ParseText(testSource).GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            if (IsNegated(node))
            {
                continue;
            }

            switch (node)
            {
                // 形态①：d.Id == "X"（lambda/断言内的相等比较）
                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.EqualsExpression)
                         && IsIdMemberAccess(binary.Left)
                         && IsStringLiteral(binary.Right, id):
                    return true;

                // 形态②：Assert.That(d.Id).IsEqualTo("X") / .IsEquivalentTo("X")（含跨行链式）
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax access
                         && access.Name.Identifier.Text is "IsEqualTo" or "IsEquivalentTo"
                         && invocation.ArgumentList.Arguments.Count == 1
                         && IsStringLiteral(invocation.ArgumentList.Arguments[0].Expression, id)
                         && MentionsId(access.Expression):
                    return true;

                // 形态③：HasId("X")（包装式断言辅助）
                case InvocationExpressionSyntax hasIdInvocation
                    when hasIdInvocation.Expression is IdentifierNameSyntax identifier
                         && identifier.Identifier.Text == "HasId"
                         && hasIdInvocation.ArgumentList.Arguments.Count == 1
                         && IsStringLiteral(hasIdInvocation.ArgumentList.Arguments[0].Expression, id):
                    return true;
            }
        }

        return false;
    }

    /// <summary>表达式是否为 <c>某对象.Id</c> 成员访问。</summary>
    private static bool IsIdMemberAccess(ExpressionSyntax expression)
        => expression is MemberAccessExpressionSyntax access
           && access.Name.Identifier.Text == "Id";

    /// <summary>
    /// 子树内是否出现"诊断对象"的 <c>.Id</c> 成员访问（覆盖 <c>Assert.That(x.Id)</c> 链式起点形态）。
    /// <para>
    /// P3 收紧（2026-09-10）：原实现只要子树含任意 <c>.Id</c> 即判覆盖——非诊断对象断言
    /// （如 <c>Assert.That(order.Id).IsEqualTo("PALENUM004")</c>）会被误判为覆盖，削弱门禁召回。
    /// 现要求 <c>.Id</c> 的接收者标识符链根植于诊断集合（标识符名含 Diag，如
    /// <c>result.Diagnostics[0].Id</c>）；无关对象的 <c>.Id</c> 不再计入。
    /// </para>
    /// </summary>
    private static bool MentionsId(SyntaxNode node)
        => node.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Any(access => access.Name.Identifier.Text == "Id"
                           && IsDiagnosticRooted(access.Expression));

    /// <summary>被 <c>.Id</c> 访问的接收者标识符链是否根植于诊断集合（标识符名含 Diag，忽略大小写）。</summary>
    private static bool IsDiagnosticRooted(ExpressionSyntax receiver)
        => receiver.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => identifier.Identifier.Text.Contains("diag", StringComparison.OrdinalIgnoreCase));

    /// <summary>表达式是否为值等于 <paramref name="value"/> 的字符串字面量。</summary>
    private static bool IsStringLiteral(ExpressionSyntax expression, string value)
        => expression is LiteralExpressionSyntax literal
           && literal.IsKind(SyntaxKind.StringLiteralExpression)
           && literal.Token.ValueText == value;

    /// <summary>节点或其祖先链是否处于否定断言（<c>.IsFalse()</c>/<c>.IsNotEqualTo()</c>/<c>!=</c>）之内。</summary>
    private static bool IsNegated(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                return true;
            }

            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberAccessExpressionSyntax access
                && access.Name.Identifier.Text is "IsFalse" or "IsNotEqualTo")
            {
                return true;
            }
        }

        return false;
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
