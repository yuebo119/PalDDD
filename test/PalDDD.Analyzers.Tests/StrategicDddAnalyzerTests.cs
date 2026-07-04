namespace PalDDD.Analyzers.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using PalDDD.Analyzers;
using PalDDD.Core;
using PalDDD.Messaging;
using System.Collections.Immutable;

public sealed class StrategicDddAnalyzerTests
{
    [Test]
    public async Task DomainEventWithoutBoundedContext_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "ordering.order-submitted.v1";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD001")).IsTrue();
    }

    [Test]
    public async Task BoundedContextWithUppercaseName_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("Ordering")]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD002")).IsTrue();
    }

    [Test]
    public async Task ProcessManagerWithoutEventHandlerShape_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [ProcessManager("order-fulfillment")]
            public class OrderProcessManager
            {
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD003")).IsTrue();
    }

    [Test]
    public async Task ProcessManagerWithUnstableName_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;
            using PalDDD.Messaging;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "ordering.order-submitted.v1";
            }

            [BoundedContext("ordering")]
            [ProcessManager("Order_Fulfillment")]
            public sealed class OrderProcessManager : IEventHandler<OrderSubmitted>
            {
                public ValueTask HandleAsync(OrderSubmitted @event, CancellationToken ct)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD006")).IsTrue();
    }

    [Test]
    public async Task ValidProcessManager_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;
            using PalDDD.Messaging;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "ordering.order-submitted.v1";
            }

            [BoundedContext("ordering")]
            [ProcessManager("ordering.order-fulfillment")]
            public sealed class OrderProcessManager : IEventHandler<OrderSubmitted>
            {
                public ValueTask HandleAsync(OrderSubmitted @event, CancellationToken ct)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id.StartsWith("PDDD", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ProcessManagerWithDifferentContextName_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;
            using PalDDD.Messaging;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }

            [BoundedContext("ordering")]
            [ProcessManager("billing.order-fulfillment")]
            public sealed class OrderProcessManager : IEventHandler<OrderSubmitted>
            {
                public ValueTask HandleAsync(OrderSubmitted @event, CancellationToken ct)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD014")).IsTrue();
    }

    [Test]
    public async Task DomainEventWithoutGenerateMessage_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD005")).IsTrue();
    }

    [Test]
    public async Task UnsealedDomainEvent_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD012")).IsTrue();
    }

    [Test]
    public async Task DomainEventWithGenerateMessage_DoesNotReportMessageContractDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "ordering.order-submitted.v1";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD005")).IsFalse();
    }

    [Test]
    public async Task DomainEventWithDifferentEventName_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD015")).IsTrue();
    }

    [Test]
    public async Task DomainEventWithDifferentMessageContext_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "billing.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD008")).IsTrue();
    }

    [Test]
    public async Task DomainEventWithMatchingMessageContext_DoesNotReportContextDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD008")).IsFalse();
    }

    [Test]
    public async Task DomainEventWithUnstableMessageName_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.OrderSubmitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD009")).IsTrue();
    }

    [Test]
    public async Task DomainEventWithMismatchedMessageVersionSuffix_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v2", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD010")).IsTrue();
    }

    [Test]
    public async Task DomainEventWithInvalidSchemaVersion_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v0", SchemaVersion = 0)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OrderSubmitted";
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD011")).IsTrue();
    }

    [Test]
    public async Task ProjectionHandlerWithoutBoundedContext_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using PalDDD.Projections;

            namespace PalDDD.Projections;

            public interface IProjectionHandler<in TMessage>
            {
                string ProjectionName { get; }
                ValueTask ProjectAsync(TMessage message, CancellationToken ct = default);
            }

            public sealed record OrderSubmitted;

            public class OrderSummaryProjection : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "order-summary";
                public ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD004")).IsTrue();
    }

    [Test]
    public async Task ValidProjectionHandler_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using PalDDD.Core;
            using PalDDD.Projections;

            namespace PalDDD.Projections;

            public interface IProjectionHandler<in TMessage>
            {
                string ProjectionName { get; }
                ValueTask ProjectAsync(TMessage message, CancellationToken ct = default);
            }

            public sealed record OrderSubmitted;

            [BoundedContext("ordering")]
            public sealed class OrderSummaryProjection : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "ordering.order-summary";
                public ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id.StartsWith("PDDD", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ProjectionHandlerWithUnstableProjectionName_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using PalDDD.Core;
            using PalDDD.Projections;

            namespace PalDDD.Projections;

            public interface IProjectionHandler<in TMessage>
            {
                string ProjectionName { get; }
                ValueTask ProjectAsync(TMessage message, CancellationToken ct = default);
            }

            public sealed record OrderSubmitted;

            [BoundedContext("ordering")]
            public sealed class OrderSummaryProjection : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "Order_Summary";
                public ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD007")).IsTrue();
    }

    [Test]
    public async Task ProjectionHandlerWithDifferentProjectionContext_ReportsDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using PalDDD.Core;
            using PalDDD.Projections;

            namespace PalDDD.Projections;

            public interface IProjectionHandler<in TMessage>
            {
                string ProjectionName { get; }
                ValueTask ProjectAsync(TMessage message, CancellationToken ct = default);
            }

            public sealed record OrderSubmitted;

            [BoundedContext("ordering")]
            public sealed class OrderSummaryProjection : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "billing.order-summary";
                public ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD013")).IsTrue();
    }

    [Test]
    public async Task ProjectionHandlerWithStableProjectionName_DoesNotReportProjectionNameDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using PalDDD.Core;
            using PalDDD.Projections;

            namespace PalDDD.Projections;

            public interface IProjectionHandler<in TMessage>
            {
                string ProjectionName { get; }
                ValueTask ProjectAsync(TMessage message, CancellationToken ct = default);
            }

            public sealed record OrderSubmitted;

            [BoundedContext("ordering")]
            public sealed class OrderSummaryProjection : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "ordering.order-summary";
                public ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD007")).IsFalse();
        await Assert.That(diagnostics.Any(d => d.Id == "PDDD013")).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    // CodeFix 测试
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddVersionSuffix_FixesMessageNameVersionMismatch()
    {
        var source = """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted", SchemaVersion = 2)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "ordering.order-submitted.v2";
            }
            """;

        var fixedSource = """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v2", SchemaVersion = 2)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "ordering.order-submitted.v2";
            }
            """;

        var result = await ApplyCodeFixAsync(source, "PDDD010");
        await Assert.That(result).IsEqualTo(fixedSource);
    }

    [Test]
    public async Task AddBoundedContextPrefix_FixesMessageNameContextMismatch()
    {
        var source = """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "order-submitted.v1")]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "order-submitted.v1";
            }
            """;

        var result = await ApplyCodeFixAsync(source, "PDDD008");
        await Assert.That(result).Contains("\"ordering.order-submitted.v1\"");
    }

    [Test]
    public async Task MatchEventName_FixesDomainEventNameMismatch()
    {
        var source = """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1")]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public static string EventName => "OldWrongName";
            }
            """;

        var result = await ApplyCodeFixAsync(source, "PDDD015");
        await Assert.That(result).Contains("\"ordering.order-submitted.v1\"");
        await Assert.That(result.Contains("OldWrongName")).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    // CodeFix 测试辅助
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> ApplyCodeFixAsync(string source, string diagnosticId)
    {
        var compilation = CSharpCompilation.Create(
            "PalDDD.Analyzers.Tests.Target",
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview))],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new StrategicDddAnalyzer());
        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);
        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        var targetDiagnostic = diagnostics.FirstOrDefault(d => d.Id == diagnosticId);
        await Assert.That(targetDiagnostic != default).IsTrue();

        // 查找匹配的 CodeFixProvider
        var codeFixProviders = new CodeFixProvider[]
        {
            new AddVersionSuffixCodeFix(),
            new AddBoundedContextPrefixCodeFix(),
            new AddProjectionContextPrefixCodeFix(),
            new MatchEventNameCodeFix()
        };

        CodeFixProvider? matchingProvider = null;
        foreach (var provider in codeFixProviders)
        {
            if (provider.FixableDiagnosticIds.Contains(diagnosticId))
            {
                matchingProvider = provider;
                break;
            }
        }
        await Assert.That(matchingProvider is not null).IsTrue();

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("TestProject", LanguageNames.CSharp)
            .WithMetadataReferences(GetReferences())
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = project.AddDocument("Test.cs", source);
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, targetDiagnostic!, (action, _) => actions.Add(action), CancellationToken.None);

        await matchingProvider!.RegisterCodeFixesAsync(context);

        await Assert.That(actions.Count > 0).IsTrue();
        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var solution = operations.OfType<ApplyChangesOperation>().First().ChangedSolution;
        var changedDocument = solution.GetDocument(document.Id);
        await Assert.That(changedDocument).IsNotNull();
        var sourceText = await changedDocument.GetTextAsync();
        return sourceText.ToString();
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            "PalDDD.Analyzers.Tests.Target",
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview))],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new StrategicDddAnalyzer());
        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);
        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trustedPlatformAssemblies is not null)
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
                yield return MetadataReference.CreateFromFile(path);
        }

        yield return MetadataReference.CreateFromFile(typeof(BoundedContextAttribute).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(IEventHandler).Assembly.Location);
    }
}
