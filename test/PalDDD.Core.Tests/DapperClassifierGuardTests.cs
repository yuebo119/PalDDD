using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// R14 F1 锁定：Dapper 唯一冲突分类器的三个 provider 判定必须留在
// NoInlining 独立方法内。
//
// 背景：三驱动是 PrivateAssets=all 的实现私有依赖，消费方只引用自己的 provider。
// 若主方法直接写 `inner is MySqlConnector.MySqlException`，主方法 JIT 时需解析全部
// provider 程序集；缺失时异常过滤器内抛 FileNotFoundException → 判定 false →
// 唯一冲突分类整体失效（PG-only / MySQL-only / SQLite-only 消费方）。
// ═══════════════════════════════════════════════════════════════
public sealed class DapperClassifierGuardTests
{
    private static readonly string[] ProviderTypeReferences =
    [
        "Npgsql.PostgresException",
        "MySqlConnector.MySqlException",
        "Microsoft.Data.Sqlite.SqliteException",
    ];

    private static readonly string[] HelperNames =
    [
        "IsPostgresUniqueViolation",
        "IsMySqlUniqueViolation",
        "IsSqliteUniqueViolation",
    ];

    [Test]
    public async Task Classifier_MainMethod_DoesNotReferenceProviderTypesDirectly()
    {
        var methods = ClassifierMethods();

        // 只看类型位置（限定名节点）：注释与字符串字面量里的类型名（方法内部做类型全名比较）
        // 不算「直接引用」——它们不会让 JIT 解析 provider 程序集。
        var qualifiedNames = methods["IsUniqueConstraintViolation"]
            .DescendantNodes()
            .OfType<QualifiedNameSyntax>()
            .Select(static q => q.ToString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var reference in ProviderTypeReferences)
        {
            await Assert.That(qualifiedNames.Contains(reference)).IsFalse();
        }
    }

    [Test]
    public async Task Classifier_ProviderHelpers_AreMarkedNoInlining()
    {
        var methods = ClassifierMethods();

        foreach (var helper in HelperNames)
        {
            await Assert.That(methods.ContainsKey(helper)).IsTrue();
            var text = TokenText(methods[helper]);
            await Assert.That(text.Contains("MethodImplOptions.NoInlining", StringComparison.Ordinal)).IsTrue();
        }
    }

    private static string TokenText(MethodDeclarationSyntax method)
        => string.Concat(method.DescendantTokens().Select(static t => t.Text));

    private static Dictionary<string, MethodDeclarationSyntax> ClassifierMethods()
    {
        var file = FindClassifierFile();
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Latest))
            .GetRoot();
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToDictionary(m => m.Identifier.ValueText, StringComparer.Ordinal);
    }

    private static string FindClassifierFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PalDDD.Dapper", "DapperSqlErrorClassifier.cs");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("未找到 src/PalDDD.Dapper/DapperSqlErrorClassifier.cs——仓库根定位失败。");
    }
}
