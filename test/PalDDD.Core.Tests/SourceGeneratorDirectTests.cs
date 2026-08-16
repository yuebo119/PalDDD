using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;

namespace PalDDD.Core.Tests;

/// <summary>
/// EnumGenerator + IdentityGenerator 直接测试 — 用 CSharpGeneratorDriver 传源码，
/// 验证诊断输出和生成代码内容（补充消费端间接测试的盲区）。
/// </summary>
public sealed class SourceGeneratorDirectTests
{
    // ── EnumGenerator 测试 ──

    [Test]
    public async Task EnumGenerator_WithFields_GeneratesRegistrationCode()
    {
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial class OrderStatus : SmartEnum<OrderStatus, string>
            {
                public static readonly OrderStatus Pending = new("pending", "待处理");
                public static readonly OrderStatus Shipped = new("shipped", "已发货");
            }
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "OrderStatus.g.cs");
        await Assert.That(source).Contains("RegisterValues");
        await Assert.That(source).Contains("Pending");
        await Assert.That(source).Contains("Shipped");
    }

    [Test]
    public async Task EnumGenerator_NoFields_ReportsWarning()
    {
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial class EmptyStatus : SmartEnum<EmptyStatus, string>
            {
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM001")).IsTrue();
    }

    [Test]
    public async Task EnumGenerator_ClassWithoutAttribute_DoesNotGenerate()
    {
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public partial class NotGenerated : SmartEnum<NotGenerated, string>
            {
                public static readonly NotGenerated A = new("a", "A");
            }
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var hasGenerated = result.Compilation.SyntaxTrees.Any(t => t.FilePath.EndsWith("NotGenerated.g.cs", StringComparison.Ordinal));
        await Assert.That(hasGenerated).IsFalse();
    }

    // ── IdentityGenerator 测试 ──

    [Test]
    public async Task IdentityGenerator_GuidType_GeneratesFromAndNew()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            [GenerateId(typeof(Guid))]
            public readonly partial record struct TestOrderId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestOrderId.g.cs");
        await Assert.That(source).Contains("public static TestOrderId New()");
        await Assert.That(source).Contains("public static TestOrderId From(Guid value)");
        await Assert.That(source).Contains("ISpanParsable<TestOrderId>");
    }

    [Test]
    public async Task IdentityGenerator_IntType_GeneratesNumericOperators()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(int))]
            public readonly partial record struct TestIntId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestIntId.g.cs");
        // 数值类型应有显式/隐式转换运算符
        await Assert.That(source).Contains("operator");
        // 数值类型 New() 应抛 NotSupportedException（服务端分配）
        await Assert.That(source).Contains("NotSupportedException");
    }

    [Test]
    public async Task IdentityGenerator_UlidType_GeneratesWithoutPalid001()
    {
        // P1 回归（十七轮）：白名单 case 曾写 "ByteAether.Ulid" 而 ToDisplayString() 返回
        // "ByteAether.Ulid.Ulid"——[GenerateId(typeof(Ulid))] 恒报 PALID001（框架主推类型不可用）
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using ByteAether.Ulid;

            namespace TestDomain;

            [GenerateId(typeof(Ulid))]
            public readonly partial record struct TestUlidId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestUlidId.g.cs");
        await Assert.That(source).Contains("Ulid.New()");
    }

    [Test]
    public async Task IdentityGenerator_LongType_GeneratesWithoutPalid001()
    {
        // P1 回归（十七轮）附：long 白名单十六轮零覆盖（靠巧合通过）——锁定
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(long))]
            public readonly partial record struct TestLongId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestLongId.g.cs");
        await Assert.That(source).Contains("operator");
    }

    [Test]
    public async Task IdentityGenerator_StringType_GeneratesNullGuard()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(string))]
            public readonly partial record struct TestStringId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestStringId.g.cs");
        // string 类型 From(null) 应抛 ArgumentException
        await Assert.That(source).Contains("ArgumentException");
    }

    [Test]
    public async Task IdentityGenerator_StructWithoutAttribute_DoesNotGenerate()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public readonly partial record struct NotGeneratedId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    // ── 八轮评审 P3：白名单诊断 / record struct-only / 全局命名空间 / record 声明诊断 ──

    [Test]
    public async Task IdentityGenerator_UnsupportedSourceType_ReportsPalid001()
    {
        // decimal 不在白名单（Guid/Ulid/int/long/string）——编译期报 PALID001 而非生成恒失败 TryParse
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(decimal))]
            public readonly partial record struct TestDecimalId;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID001")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task IdentityGenerator_PlainStructDeclaration_ReportsPalid002()
    {
        // P3（九轮）：普通 partial struct 挂 [GenerateId] 报 PALID002——静默跳过让错误
        // 延迟到使用点 CS0117（无指向性）；诊断后不生成代码
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            [GenerateId(typeof(Guid))]
            public readonly partial struct TestPlainStructId;
            """);

        await Assert.That(result.Diagnostics).HasSingleItem();
        await Assert.That(result.Diagnostics[0].Id).IsEqualTo("PALID002");
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task IdentityGenerator_GlobalNamespace_GeneratesWithoutNamespaceDeclaration()
    {
        // 全局命名空间：不再产出 "namespace _;"（旧 fallback 使生成物落入 _ 命名空间不合并）
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            [GenerateId(typeof(Guid))]
            public readonly partial record struct TestGlobalId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestGlobalId.g.cs");
        await Assert.That(source).DoesNotContain("namespace _;");
        await Assert.That(source).DoesNotContain("namespace TestGlobalId");
        await Assert.That(source).Contains("public readonly partial record struct TestGlobalId");
    }

    [Test]
    public async Task EnumGenerator_RecordDeclaration_ReportsPalenum003()
    {
        // record 声明此前被 predicate 静默跳过——现在报 PALENUM003 引导改用 class
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial record OrderStatusRecord : SmartEnum<OrderStatusRecord, string>
            {
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM003")).IsTrue();
    }

    // ── 辅助方法（参照 MessageRegistryGeneratorTests 的模式）──

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunEnumGenerator(string source)
        => RunGenerator<EnumGeneratorProxy>(source);

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunIdentityGenerator(string source)
        => RunGenerator<IdentityGeneratorProxy>(source);

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunGenerator<TProxy>(string source)
        where TProxy : IGeneratorProxy, new()
    {
        var proxy = new TProxy();
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "PalDDD.SourceGen.DirectTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = proxy.LoadGenerator();
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var diagnostics);
        return (updatedCompilation, diagnostics);
    }

    private static string GetGeneratedSource((Compilation Compilation, ImmutableArray<Diagnostic> _) result, string fileNameEndsWith)
    {
        var tree = result.Compilation.SyntaxTrees.Single(
            t => t.FilePath.EndsWith(fileNameEndsWith, StringComparison.Ordinal));
        return tree.ToString();
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
        // P1 回归（十七轮）：[GenerateId(typeof(Ulid))] 测试需要 ByteAether.Ulid 程序集引用
        yield return MetadataReference.CreateFromFile(typeof(ByteAether.Ulid.Ulid).Assembly.Location);
    }

    // ── Generator 代理（从编译后的 DLL 加载，避免 analyzer 引用问题）──

    private interface IGeneratorProxy
    {
        IIncrementalGenerator LoadGenerator();
    }

    private sealed class EnumGeneratorProxy : IGeneratorProxy
    {
        public IIncrementalGenerator LoadGenerator()
            => LoadFromAssembly("PalDDD.Core.SourceGen.EnumGenerator");
    }

    private sealed class IdentityGeneratorProxy : IGeneratorProxy
    {
        public IIncrementalGenerator LoadGenerator()
            => LoadFromAssembly("PalDDD.Core.SourceGen.IdentityGenerator");
    }

    private static IIncrementalGenerator LoadFromAssembly(string typeName)
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDirectory.Parent?.Name ?? "Debug";
        var generatorPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PalDDD.Core.SourceGen", "bin", configuration, "netstandard2.0",
            "PalDDD.Core.SourceGen.dll"));

        var assembly = System.Reflection.Assembly.LoadFrom(generatorPath);
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return (IIncrementalGenerator)Activator.CreateInstance(type)!;
    }
}
