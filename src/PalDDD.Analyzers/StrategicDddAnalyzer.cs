using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

namespace PalDDD.Analyzers;

// ─────────────────────────────────────────────────────────────
// 策略式 DDD 分析器（15 条诊断规则）
// 精炼重组（2026-08-26）：原 738 行单文件按规则族拆分——
//   本文件：规则 ID/descriptor 表 + Initialize + AnalyzeNamedType 调度骨架
//   StrategicDddAnalyzer.BoundedContext.cs：PDDD001/002（上下文存在性/命名）
//   StrategicDddAnalyzer.MessageContracts.cs：PDDD005/008/009/010/011/012/015（消息契约）
//   StrategicDddAnalyzer.Handlers.cs：PDDD003/004/006/007/013/014（PM/投影形状与命名）
//   StrategicDddAnalyzer.SymbolHelpers.cs：符号/语法树辅助
// 拆分是纯搬运（逻辑与注释逐字保留），行为由 Analyzers.Tests 36 测试锁定。
// ─────────────────────────────────────────────────────────────
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class StrategicDddAnalyzer : DiagnosticAnalyzer
{
    public const string MissingBoundedContextId = "PDDD001";
    public const string InvalidBoundedContextNameId = "PDDD002";
    public const string InvalidProcessManagerShapeId = "PDDD003";
    public const string InvalidProjectionHandlerShapeId = "PDDD004";
    public const string MissingGeneratedMessageContractId = "PDDD005";
    public const string InvalidProcessManagerNameId = "PDDD006";
    public const string InvalidProjectionNameId = "PDDD007";
    public const string MessageNameContextMismatchId = "PDDD008";
    public const string InvalidMessageNameId = "PDDD009";
    public const string MessageNameVersionMismatchId = "PDDD010";
    public const string InvalidMessageSchemaVersionId = "PDDD011";
    public const string UnsealedDomainEventId = "PDDD012";
    public const string ProjectionNameContextMismatchId = "PDDD013";
    public const string ProcessManagerNameContextMismatchId = "PDDD014";
    public const string DomainEventNameMismatchId = "PDDD015";

    private static readonly DiagnosticDescriptor MissingBoundedContext = new(
        MissingBoundedContextId,
        "Domain model types must declare a bounded context",
        "Domain model type '{0}' must declare [BoundedContext]",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidBoundedContextName = new(
        InvalidBoundedContextNameId,
        "Bounded context names must be stable lowercase names",
        "Bounded context name '{0}' must use lowercase letters, digits, '-' or '.'",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidProcessManagerShape = new(
        InvalidProcessManagerShapeId,
        "Process managers must be sealed bounded event handlers",
        "Process manager '{0}' must be sealed, declare [BoundedContext], and implement IEventHandler<TEvent>",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidProjectionHandlerShape = new(
        InvalidProjectionHandlerShapeId,
        "Projection handlers must be sealed bounded context components",
        "Projection handler '{0}' must be sealed and declare [BoundedContext]",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingGeneratedMessageContract = new(
        MissingGeneratedMessageContractId,
        "Domain events must declare generated message contracts",
        "Domain event '{0}' must declare [GenerateMessage] so outbox and replay paths have a stable descriptor",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidProcessManagerName = new(
        InvalidProcessManagerNameId,
        "Process manager names must be stable lowercase names",
        "Process manager name '{0}' must use lowercase letters, digits, '-' or '.'",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidProjectionName = new(
        InvalidProjectionNameId,
        "Projection names must be stable lowercase names",
        "Projection name '{0}' must be a string literal using lowercase letters, digits, '-' or '.'",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MessageNameContextMismatch = new(
        MessageNameContextMismatchId,
        "Domain event message names must belong to the bounded context",
        "Domain event message name '{0}' must start with bounded context '{1}.'",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMessageName = new(
        InvalidMessageNameId,
        "Domain event message names must be stable lowercase names",
        "Domain event message name '{0}' must use lowercase letters, digits, '-' or '.'",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MessageNameVersionMismatch = new(
        MessageNameVersionMismatchId,
        "Domain event message names must include the schema version suffix",
        "Domain event message name '{0}' must end with '.v{1}' to match SchemaVersion {1}",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMessageSchemaVersion = new(
        InvalidMessageSchemaVersionId,
        "Domain event message schema versions must be positive",
        "Domain event '{0}' must use SchemaVersion greater than or equal to 1",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsealedDomainEvent = new(
        UnsealedDomainEventId,
        "Domain events must be sealed",
        "Domain event '{0}' must be sealed to keep event contracts closed for replay and serialization",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ProjectionNameContextMismatch = new(
        ProjectionNameContextMismatchId,
        "Projection names must belong to the bounded context",
        "Projection name '{0}' must start with bounded context '{1}.'",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ProcessManagerNameContextMismatch = new(
        ProcessManagerNameContextMismatchId,
        "Process manager names must belong to the bounded context",
        "Process manager name '{0}' must start with bounded context '{1}.'",
        "PalDDD.StrategicDDD",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DomainEventNameMismatch = new(
        DomainEventNameMismatchId,
        "Domain event names must match generated message names",
        "Domain event EventName '{0}' must be a string literal matching generated message name '{1}'",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        MissingBoundedContext,
        InvalidBoundedContextName,
        InvalidProcessManagerShape,
        InvalidProjectionHandlerShape,
        MissingGeneratedMessageContract,
        InvalidProcessManagerName,
        InvalidProjectionName,
        MessageNameContextMismatch,
        InvalidMessageName,
        MessageNameVersionMismatch,
        InvalidMessageSchemaVersion,
        UnsealedDomainEvent,
        ProjectionNameContextMismatch,
        ProcessManagerNameContextMismatch,
        DomainEventNameMismatch
    ];

    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    /// <summary>
    /// 调度骨架：计算族间共享的符号事实（boundedContext/链式上下文/GenerateMessage），
    /// 按规则族分发到 partial 文件的族方法。各规则的判断逻辑与修复史注释在其族文件内。
    /// </summary>
    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        // P2 修复（十七轮）：interface 直接跳过——[BoundedContext]/[GenerateMessage] 均为
        // AttributeTargets.Class，interface 上无法出现；而 IDomainEvent 可被 interface 继承
        // （interface IFoo : IDomainEvent），原实现经 ImplementsInterface 判为领域事件类型，
        // 误报 PDDD001/PDDD005（PDDD012 已有 Class 专属条件不受影响）。
        // Class 专属契约诊断在 interface 上无法消解，整体短路。
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return;

        var boundedContext = TryGetAttribute(type, BoundedContextAttributeName);
        // P2 修复（二十一轮）：PDDD001 存在性检查沿基类链——[BoundedContext].Inherited=true
        // （运行时反射对派生类可见），仅查直接声明时基类挂 attribute 的派生领域模型误报；
        // 链上最近声明同时供 PDDD008/013/014 的 contextName 提取（PDDD002 仍只验直接声明，
        // 派生类不重复报命名错误）
        var chainBoundedContext = TryGetAttributeAlongBaseChain(type, BoundedContextAttributeName);
        var generateMessage = IsDomainEventType(type)
            ? TryGetAttribute(type, GenerateMessageAttributeName)
            : null;

        AnalyzeBoundedContextRules(context, type, boundedContext, chainBoundedContext);
        AnalyzeMessageContractRules(context, type, chainBoundedContext, generateMessage);
        AnalyzeHandlerRules(context, type, chainBoundedContext);
    }
}
