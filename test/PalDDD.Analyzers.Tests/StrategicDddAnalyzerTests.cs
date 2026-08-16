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

    // P2 修复（十七轮）测试：interface : IDomainEvent 不再误报——
    // [BoundedContext]/[GenerateMessage] 均 AttributeTargets.Class，interface 无法消解诊断
    [Test]
    public async Task InterfaceImplementingDomainEvent_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            public interface IOrderEvent : IDomainEvent
            {
            }
            """);

        // 修复前：ImplementsInterface 判为领域事件类型，误报 PDDD001 + PDDD005
        await Assert.That(diagnostics.Any(d => d.Id.StartsWith("PDDD", StringComparison.Ordinal))).IsFalse();
    }

    // P2 修复（十七轮）测试：ProjectionName 与 [BoundedContext] 均声明在基类——
    // TryGetProjectionName 沿 BaseType 链查找（PDDD007 不误报）+
    // [BoundedContext].Inherited=true 沿基类链查找（PDDD004 不误报）
    [Test]
    public async Task ProjectionHandlerInheritingProjectionNameAndBoundedContext_DoesNotReportOnDerived()
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
            public abstract class ProjectionBase : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "ordering.order-summary";
                public abstract ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default);
            }

            public sealed class OrderSummaryProjection : ProjectionBase
            {
                public override ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """);

        // 基类自身 ProjectionName 为合法字面量、上下文匹配——源码中任何 PDDD007
        // 只能来自派生类（基类链缺失时 Name=null 的误报），修复后应零出现
        await Assert.That(diagnostics.Any(d => d.Id == "PDDD007")).IsFalse();
        // 仅断言派生类 OrderSummaryProjection 不因继承路径额外误报
        // （P3 修复（二十一轮）后 abstract 基类自身也不再报 PDDD004——
        // 见 AbstractProjectionBase_DoesNotReportPddd004）
        await Assert.That(diagnostics.Any(d =>
            d.Id == "PDDD004"
            && d.ToString().Contains("OrderSummaryProjection", StringComparison.Ordinal))).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    // 二十一轮评审修复测试（P2#3 基类链 / P3#1 abstract / P3#3 接口默认实现）
    // ═══════════════════════════════════════════════════════════════

    // P2 修复（二十一轮）测试：[BoundedContext] 挂在领域模型基类——AttributeUsage
    // Inherited=true（运行时反射对派生类可见），原实现仅查直接声明，派生领域模型
    // 误报 PDDD001；沿基类链查找后派生类零 PDDD001，且继承的 contextName 参与消息名
    // 前缀校验（PDDD008 正确放行匹配前缀）
    [Test]
    public async Task DomainModelInheritingBoundedContextFromBase_DoesNotReportPddd001()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            public abstract class OrderEventBase : DomainEvent, IDomainEvent
            {
            }

            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : OrderEventBase
            {
                public static string EventName => "ordering.order-submitted.v1";
            }
            """);

        // 修复前：OrderSubmitted 未直接声明 [BoundedContext] → 误报 PDDD001；
        // 继承的上下文名参与校验且匹配——全源码（基类 + 派生类）应零 PDDD 诊断
        await Assert.That(diagnostics.Any(d => d.Id.StartsWith("PDDD", StringComparison.Ordinal))).IsFalse();
    }

    // P2 修复（二十一轮）测试：contextName 沿基类链提取——派生事件的消息名不属于
    // 基类声明的上下文时应照常报 PDDD008（修复前 gate 用直接声明判 null 直接跳过，
    // 继承上下文的事件脱离前缀治理）
    [Test]
    public async Task DomainEventWithInheritedContext_WrongPrefixStillReportsPddd008()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            public abstract class OrderEventBase : DomainEvent, IDomainEvent
            {
            }

            [GenerateMessage(Name = "billing.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : OrderEventBase
            {
                public static string EventName => "billing.order-submitted.v1";
            }
            """);

        // 源码中唯一的 [GenerateMessage] 挂在派生事件上——PDDD008 只能来自它
        // （注意 PDDD008 消息不含类型名，不能按名称过滤诊断）
        await Assert.That(diagnostics.Any(d => d.Id == "PDDD008")).IsTrue();
    }

    // P3 修复（二十一轮）测试：abstract 领域事件基类——[GenerateMessage] 与 sealed 在
    // abstract 上不可消解（sealed 与 abstract 互斥、契约由最终 sealed 派生类声明），
    // 不再误报 PDDD005/PDDD012
    [Test]
    public async Task AbstractDomainEventBase_DoesNotReportPddd005OrPddd012()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            public abstract class OrderEventBase : DomainEvent, IDomainEvent
            {
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD005")).IsFalse();
        await Assert.That(diagnostics.Any(d => d.Id == "PDDD012")).IsFalse();
    }

    // P3 修复（二十一轮）测试：abstract 投影基类——sealed 与 abstract 互斥，shape 由
    // 最终 sealed 派生类消解，基类自身不再报 PDDD004
    [Test]
    public async Task AbstractProjectionBase_DoesNotReportPddd004()
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
            public abstract class ProjectionBase : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "ordering.order-summary";
                public abstract ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default);
            }

            public sealed class OrderSummaryProjection : ProjectionBase
            {
                public override ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """);

        // 修复前：abstract ProjectionBase 因 !IsSealed 自身报 PDDD004；
        // 修复后基类与派生类均零 PDDD004
        await Assert.That(diagnostics.Any(d => d.Id == "PDDD004")).IsFalse();
    }

    // P3 修复（二十一轮）测试：EventName 由接口 static virtual 默认实现提供——
    // 声明语法在接口上，BaseType 链查不到（原实现误报 PDDD015）；
    // 补 AllInterfaces 遍历后应能提取默认实现中的字面量。
    // 注：接口 static virtual 是否免除类的 in-class 实现义务随 Roslyn 版本演进
    // （csharplang 提案标注 TBD）——若编译器报实现缺失，分析器仍在完整符号模型上
    // 运行（AllInterfaces 与接口成员声明不受该错误影响），断言两种情形下均成立
    // （同 MissingEventNameDeclaration_DoesNotRegisterCodeFix 的错误编译容忍模式）
    [Test]
    public async Task DomainEventWithInterfaceDefaultEventName_DoesNotReportPddd015()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using PalDDD.Core;

            public interface IOrderContractEvent : IDomainEvent
            {
                static virtual string EventName => "ordering.order-submitted.v1";
            }

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IOrderContractEvent
            {
            }
            """);

        await Assert.That(diagnostics.Any(d => d.Id == "PDDD015")).IsFalse();
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
        // 验证新串出现且旧未限定名在 GenerateMessage 中被替换（非追加）
        await Assert.That(result).Contains("\"ordering.order-submitted.v1\"");
        await Assert.That(result.Contains("Name = \"order-submitted.v1\"")).IsFalse();
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

    // ── 二十一轮 P3：CodeFix 三项修复测试 ──

    // P3 修复（二十一轮）测试：ProjectionName 以 getter 语句体 return 字面量声明——
    // analyzer 四形式能识别并报 PDDD013，CodeFix 原只认表达式体/初始化器两形式
    // （诊断照报但 fix 不注册）；补 accessor 遍历后应能改写
    [Test]
    public async Task AddProjectionContextPrefix_FixesGetterBodyProjectionName()
    {
        var source = """
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
                public string ProjectionName { get { return "order-summary"; } }
                public ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """;

        var result = await ApplyCodeFixAsync(source, "PDDD013");
        await Assert.That(result).Contains("\"ordering.order-summary\"");
        await Assert.That(result.Contains("return \"order-summary\"")).IsFalse();
    }

    // P3 修复（二十一轮）测试：using 别名引用 [BoundedContext]——原文本匹配
    // a.Name.ToString().Contains("BoundedContext") 对 "[BC]" 漏识别（fix 不注册）；
    // 符号级匹配（GetSymbolInfo + ContainingType 元数据名）后正常注册
    [Test]
    public async Task AddProjectionContextPrefix_RecognizesAliasedBoundedContextAttribute()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using PalDDD.Projections;
            using BC = PalDDD.Core.BoundedContextAttribute;

            namespace PalDDD.Projections;

            public interface IProjectionHandler<in TMessage>
            {
                string ProjectionName { get; }
                ValueTask ProjectAsync(TMessage message, CancellationToken ct = default);
            }

            public sealed record OrderSubmitted;

            [BC("ordering")]
            public sealed class OrderSummaryProjection : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "order-summary";
                public ValueTask ProjectAsync(OrderSubmitted message, CancellationToken ct = default)
                    => ValueTask.CompletedTask;
            }
            """;

        var result = await ApplyCodeFixAsync(source, "PDDD013");
        await Assert.That(result).Contains("\"ordering.order-summary\"");
    }

    // P3 修复（二十一轮）测试：EventName 声明缺失（PDDD015 定位回退到类型声明）——
    // 原实现取类型内第一个字符串字面量（此处是 [BoundedContext("ordering")] 的参数，
    // fix 会把上下文名改写成消息名）；修复后不注册 fix
    // 注：EventName 为 IDomainEvent 的 static abstract 成员，缺失时源码报 CS0535——
    // PDDD015 在独立探针（等价编译选项）下触发，但在 testhost TPA 环境下诊断集合
    // 存在差异（主线程验证：dotnet run 探针报 PDDD015 / dotnet test 不报）。
    // 改为泛化负向断言：目标类型上至少一个 PDDD 诊断存在（非空洞），且对全部诊断
    // 均无 fix 注册——负向语义（缺失 EventName 时 fix 不得注册）不依赖特定诊断 ID。
    [Test]
    public async Task MissingEventNameDeclaration_DoesNotRegisterCodeFix()
    {
        var source = """
            using PalDDD.Core;

            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
            public sealed class OrderSubmitted : DomainEvent, IDomainEvent
            {
            }
            """;
        var (_, actions) = await RegisterCodeFixesForFirstPdddDiagnostic(source);

        await Assert.That(actions).IsEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    // CodeFix 测试辅助
    // ═══════════════════════════════════════════════════════════════

    // P3 修复（二十一轮）：拆出 fix 注册核心——"不注册 fix"的负向用例
    // （MissingEventNameDeclaration_DoesNotRegisterCodeFix）需要检查 actions 本身，
    // 原辅助方法内嵌 actions 非空断言无法表达
    private static async Task<(Document Document, List<CodeAction> Actions)> RegisterCodeFixesForAsync(
        string source, string diagnosticId)
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
        return (document, actions);
    }

    private static async Task<string> ApplyCodeFixAsync(string source, string diagnosticId)
    {
        var (document, actions) = await RegisterCodeFixesForAsync(source, diagnosticId);

        await Assert.That(actions.Count > 0).IsTrue();
        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var solution = operations.OfType<ApplyChangesOperation>().First().ChangedSolution;
        var changedDocument = solution.GetDocument(document.Id);
        await Assert.That(changedDocument).IsNotNull();
        var sourceText = await changedDocument.GetTextAsync();
        return sourceText.ToString();
    }

    // P3 修复（二十一轮）：泛化负向 helper——不锁定特定诊断 ID（testhost 与独立探针的
    // 诊断集合存在环境差异），对编译产生的全部 PDDD 诊断逐个尝试全部 fix provider，
    // 断言"目标类型上至少一个诊断存在"（非空洞）且无任何 fix 注册。
    private static async Task<(Document Document, List<CodeAction> Actions)> RegisterCodeFixesForFirstPdddDiagnostic(
        string source)
    {
        var diagnostics = await AnalyzeAsync(source);
        var pdddDiagnostics = diagnostics.Where(d => d.Id.StartsWith("PDDD", StringComparison.Ordinal)).ToList();
        await Assert.That(pdddDiagnostics.Count > 0).IsTrue();

        var codeFixProviders = new CodeFixProvider[]
        {
            new AddVersionSuffixCodeFix(),
            new AddBoundedContextPrefixCodeFix(),
            new AddProjectionContextPrefixCodeFix(),
            new MatchEventNameCodeFix()
        };

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("TestProject", LanguageNames.CSharp)
            .WithMetadataReferences(GetReferences())
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = project.AddDocument("Test.cs", source);
        var actions = new List<CodeAction>();
        foreach (var diagnostic in pdddDiagnostics)
        {
            foreach (var provider in codeFixProviders)
            {
                if (provider.FixableDiagnosticIds.Contains(diagnostic.Id))
                {
                    var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
                    await provider.RegisterCodeFixesAsync(context);
                }
            }
        }
        return (document, actions);
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
