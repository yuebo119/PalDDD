using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;

namespace PalDDD.Core.Tests;

public sealed class MessageRegistryGeneratorTests
{
    [Test]
    public async Task GenerateMessage_WithoutExplicitName_ReportsStableNameDiagnostic()
    {
        var diagnostics = RunGenerator(
            """
            using PalDDD.Core;

            [GenerateMessage]
            public sealed record OrderSubmitted;
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PALMSG001")).IsTrue();
    }

    [Test]
    public async Task GenerateMessage_WithInvalidSchemaVersion_ReportsSchemaDiagnostic()
    {
        var diagnostics = RunGenerator(
            """
            using PalDDD.Core;

            [GenerateMessage(Name = "orders.order-submitted.v1", SchemaVersion = 0)]
            public sealed record OrderSubmitted;
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PALMSG002")).IsTrue();
    }

    [Test]
    public async Task GenerateMessage_WithDuplicateName_ReportsDuplicateNameDiagnostic()
    {
        var diagnostics = RunGenerator(
            """
            using PalDDD.Core;

            [GenerateMessage(Name = "orders.order-submitted.v1")]
            public sealed record OrderSubmitted;

            [GenerateMessage(Name = "orders.order-submitted.v1")]
            public sealed record OrderSubmittedAgain;
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PALMSG003")).IsTrue();
    }

    [Test]
    public async Task GenerateMessage_WithUnstableName_ReportsStableWireNameDiagnostic()
    {
        var diagnostics = RunGenerator(
            """
            using PalDDD.Core;

            [GenerateMessage(Name = "Orders.Order_Submitted.v1")]
            public sealed record OrderSubmitted;
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PALMSG004")).IsTrue();
    }

    [Test]
    public async Task GenerateMessage_WithMismatchedNameVersion_ReportsVersionSuffixDiagnostic()
    {
        var diagnostics = RunGenerator(
            """
            using PalDDD.Core;

            [GenerateMessage(Name = "orders.order-submitted.v2", SchemaVersion = 1)]
            public sealed record OrderSubmitted;
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PALMSG005")).IsTrue();
    }

    [Test]
    public async Task GenerateMessage_WithMatchingNameVersion_DoesNotReportVersionSuffixDiagnostic()
    {
        var diagnostics = RunGenerator(
            """
            using PalDDD.Core;

            [GenerateMessage(Name = "orders.order-submitted.v2", SchemaVersion = 2)]
            public sealed record OrderSubmitted;
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PALMSG005")).IsFalse();
    }

    [Test]
    public async Task Diagnostics_PointToUserSourceLocation_NotLocationNone()
    {
        // 诊断应定位到用户代码（GenerateMessage 标记的类型），而非 Location.None。
        // Location.None 会导致 IDE 无法高亮错误位置，开发体验差。
        var diagnostics = RunGenerator(
            """
            using PalDDD.Core;

            [GenerateMessage]
            public sealed record OrderSubmitted;
            """);

        var diagnostic = diagnostics.Single(d => d.Id == "PALMSG001");
        await Assert.That(diagnostic.Location).IsNotEqualTo(Location.None);
        // SourceSpan 非零表示 Location 指向源文本中具体的字符范围 —— 即 IDE 能高亮的位置。
        await Assert.That(diagnostic.Location.SourceSpan.Length > 0).IsTrue();
    }

    [Test]
    public async Task GenerateMessage_WithOneInvalidMessage_StillGeneratesValidMessages()
    {
        var result = RunGeneratorWithCompilation(
            """
            using PalDDD.Core;

            [GenerateMessage]
            public sealed record MissingName;

            [GenerateMessage(Name = "orders.order-submitted.v1")]
            public sealed record OrderSubmitted;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALMSG001")).IsTrue();
        var generatedTree = result.Compilation.SyntaxTrees.Single(
            tree => tree.FilePath.EndsWith("PalDDD.Generated.MessageCatalog.g.cs", StringComparison.Ordinal));
        var source = generatedTree.ToString();
        await Assert.That(source).Contains("orders.order-submitted.v1");
        await Assert.That(source).DoesNotContain("MissingName");
    }

    [Test]
    public async Task GenerateMessage_WithDuplicateName_StillGeneratesNonDuplicateMessages()
    {
        var result = RunGeneratorWithCompilation(
            """
            using PalDDD.Core;

            [GenerateMessage(Name = "orders.duplicate.v1")]
            public sealed record FirstDuplicate;

            [GenerateMessage(Name = "orders.duplicate.v1")]
            public sealed record SecondDuplicate;

            [GenerateMessage(Name = "orders.unique.v1")]
            public sealed record UniqueMessage;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALMSG003")).IsTrue();
        var generatedTree = result.Compilation.SyntaxTrees.Single(
            tree => tree.FilePath.EndsWith("PalDDD.Generated.MessageCatalog.g.cs", StringComparison.Ordinal));
        var source = generatedTree.ToString();
        await Assert.That(source).Contains("orders.unique.v1");
        await Assert.That(source).DoesNotContain("orders.duplicate.v1");
    }

    private static ImmutableArray<Diagnostic> RunGenerator(string source)
        => RunGeneratorWithCompilation(source).Diagnostics;

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunGeneratorWithCompilation(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "PalDDD.Core.SourceGen.Tests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = LoadMessageRegistryGenerator();
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var diagnostics);
        return (updatedCompilation, diagnostics);
    }

    private static IIncrementalGenerator LoadMessageRegistryGenerator()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDirectory.Parent?.Name ?? "Debug";
        var generatorPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "PalDDD.Core.SourceGen",
            "bin",
            configuration,
            "netstandard2.0",
            "PalDDD.Core.SourceGen.dll"));

        var assembly = System.Reflection.Assembly.LoadFrom(generatorPath);
        var type = assembly.GetType("PalDDD.Core.SourceGen.MessageRegistryGenerator", throwOnError: true)!;
        return (IIncrementalGenerator)Activator.CreateInstance(type)!;
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trustedPlatformAssemblies is not null)
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
                yield return MetadataReference.CreateFromFile(path);
        }

        yield return MetadataReference.CreateFromFile(typeof(GenerateMessageAttribute).Assembly.Location);
    }
}
