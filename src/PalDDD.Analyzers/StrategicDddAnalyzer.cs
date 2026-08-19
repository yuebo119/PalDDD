using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Globalization;

namespace PalDDD.Analyzers;

// ─────────────────────────────────────────────────────────────
// 策略式 DDD 分析器（15 条诊断规则）
// ─────────────────────────────────────────────────────────────
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StrategicDddAnalyzer : DiagnosticAnalyzer
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

    private const string BoundedContextAttributeName = "PalDDD.Core.BoundedContextAttribute";
    private const string ProcessManagerAttributeName = "PalDDD.Core.ProcessManagerAttribute";
    private const string GenerateMessageAttributeName = "PalDDD.Core.GenerateMessageAttribute";
    private const string DomainEventName = "PalDDD.Core.DomainEvent";
    private const string EntityName = "PalDDD.Core.Entity";
    private const string AggregateRootName = "PalDDD.Core.AggregateRoot`1";
    private const string DomainEventInterfaceName = "PalDDD.Core.IDomainEvent";
    private const string EventHandlerInterfaceName = "PalDDD.Messaging.IEventHandler`1";
    private const string ProjectionHandlerInterfaceName = "PalDDD.Projections.IProjectionHandler`1";

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
        // ITM-123 修复：struct 实现 IDomainEvent 时 [GenerateMessage]/[BoundedContext] 均为
        // AttributeTargets.Class 不可消解——领域事件契约诊断（PDDD001/005）仅对 class 生效，
        // struct 事件与 static abstract 组合属不支持形态（编译期无对应可消解诊断，排除误报）
        if (type.TypeKind == TypeKind.Class
            && IsDomainModelType(type)
            && chainBoundedContext is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingBoundedContext,
                type.Locations[0],
                type.Name));
        }

        var generateMessage = IsDomainEventType(type)
            ? TryGetAttribute(type, GenerateMessageAttributeName)
            : null;
        // ITM-123 修复：PDDD005 仅对 class 生效——[GenerateMessage] 为 AttributeTargets.Class，
        // struct 事件不可消解（与上方 PDDD001 的 struct 排除对称）
        if (type.TypeKind == TypeKind.Class
            && IsDomainEventType(type)
            && generateMessage is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingGeneratedMessageContract,
                type.Locations[0],
                type.Name));
        }

        if (IsDomainEventType(type)
            && type.TypeKind == TypeKind.Class
            && !type.IsSealed)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsealedDomainEvent,
                type.Locations[0],
                type.Name));
        }

        if (generateMessage is not null)
        {
            var messageName = TryGetNamedStringArgument(generateMessage, "Name");
            if (IsStableName(messageName))
            {
                var eventName = TryGetStaticStringProperty(type, "EventName", context.CancellationToken);
                // ITM-124 修复：EventName 声明为非字面量（const 拼接/计算）时
                // TryGetStaticStringProperty 返回 (Name=null, Location=声明位置)——原实现以 null
                // 比对 messageName 恒报 PDDD015 误报；非字面量无法静态判定，跳过比对。
                // 注意区分：完全缺失声明时返回 (null, null)——Location 为 null，保留
                // 原"缺失声明同样报 PDDD015"行为（事件契约诊断不因缺失而静默）。
                if (eventName is not { Name: null, Location: not null }
                    && !StringComparer.Ordinal.Equals(eventName.Name, messageName))
                {
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add("ExpectedMessageName", messageName);
                    context.ReportDiagnostic(Diagnostic.Create(
                        DomainEventNameMismatch,
                        eventName.Location ?? type.Locations[0],
                        properties,
                        eventName.Name,
                        messageName));
                }
            }
        }

        if (boundedContext is not null)
        {
            var name = TryGetStringConstructorArgument(boundedContext);
            if (!IsStableName(name))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidBoundedContextName,
                    boundedContext.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0],
                    name ?? string.Empty));
            }
        }

        // ITM-221 修复（三十二轮）：PDDD009/010/011 是消息名/版本校验——不依赖 BoundedContext
        // 存在。原 `chainBoundedContext is not null && generateMessage is not null` 条件让
        // 无 [BoundedContext] 的 [GenerateMessage] 完全跳过这三个诊断（漏报）。
        // 只有 PDDD008（上下文前缀不匹配）需要 BoundedContext。
        if (generateMessage is not null)
        {
            var contextName = chainBoundedContext is null ? null : TryGetStringConstructorArgument(chainBoundedContext);
            var messageName = TryGetNamedStringArgument(generateMessage, "Name");
            var schemaVersion = TryGetNamedIntArgument(generateMessage, "SchemaVersion") ?? 1;
            if (schemaVersion < 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidMessageSchemaVersion,
                    generateMessage.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0],
                    type.Name));
            }

            if (!IsStableName(messageName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidMessageName,
                    generateMessage.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0],
                    messageName ?? string.Empty));
            }

            if (IsStableName(messageName)
                && schemaVersion >= 1
                && !HasVersionSuffix(messageName!, schemaVersion))
            {
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add("SchemaVersion", schemaVersion.ToString(CultureInfo.InvariantCulture));
                context.ReportDiagnostic(Diagnostic.Create(
                    MessageNameVersionMismatch,
                    generateMessage.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0],
                    properties,
                    messageName,
                    schemaVersion));
            }

            // PDDD008 需要 BoundedContext——消息名前缀校验（上下文归属）
            if (chainBoundedContext is not null
                && IsStableName(contextName)
                && IsStableName(messageName)
                && !BelongsToBoundedContext(messageName!, contextName!))
            {
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add("BoundedContext", contextName);
                context.ReportDiagnostic(Diagnostic.Create(
                    MessageNameContextMismatch,
                    generateMessage.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0],
                    properties,
                    messageName,
                    contextName));
            }
        }

        var processManager = TryGetAttribute(type, ProcessManagerAttributeName);
        // ITM-122 修复：PDDD003 shape 检查用链式 BoundedContext——[ProcessManager] 与
        // [BoundedContext] 均 Inherited=true，派生 ProcessManager 继承基类 [BoundedContext]
        // 时直接声明查不到（boundedContext 仅本类型），误报"未声明 [BoundedContext]"；
        // 与 PDDD001（chainBoundedContext）及 PDDD004（HasAttributeAlongBaseChain）口径对齐
        if (processManager is not null
            && (!type.IsSealed || chainBoundedContext is null || !ImplementsGenericInterface(type, EventHandlerInterfaceName)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidProcessManagerShape,
                type.Locations[0],
                type.Name));
        }

        if (processManager is not null)
        {
            var name = TryGetStringConstructorArgument(processManager);
            if (!IsStableName(name))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidProcessManagerName,
                    processManager.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0],
                    name ?? string.Empty));
            }

            // P2 修复（二十一轮）：PDDD014 的 contextName 沿基类链取最近声明
            var contextName = chainBoundedContext is null ? null : TryGetStringConstructorArgument(chainBoundedContext);
            if (IsStableName(name)
                && IsStableName(contextName)
                && !BelongsToBoundedContext(name!, contextName!))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ProcessManagerNameContextMismatch,
                    processManager.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0],
                    name,
                    contextName));
            }
        }

        if (ImplementsGenericInterface(type, ProjectionHandlerInterfaceName))
        {
            // P2 修复（十七轮）：[BoundedContext] 沿基类链查找——GetAttributes 仅返回本类型
            // 直接声明的 attribute，而该 attribute 的 AttributeUsage.Inherited=true（未显式
            // 设置，默认继承），运行时反射在派生类可见基类声明；仅查直接声明会误报 PDDD004。
            // P3 修复（二十一轮）：abstract 投影基类不再报 sealed 缺失——sealed 与 abstract
            // 互斥（组合声明非法），shape 由最终 sealed 派生类消解（镜像 IsDomainEventType
            // 的 abstract 排除）；上下文缺失仍在 abstract 上可消解，保持链检查。
            if ((!type.IsSealed && !type.IsAbstract) || !HasAttributeAlongBaseChain(type, BoundedContextAttributeName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidProjectionHandlerShape,
                    type.Locations[0],
                    type.Name));
            }

            var projectionName = TryGetProjectionName(type, context.CancellationToken);
            if (!IsStableName(projectionName.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidProjectionName,
                    projectionName.Location ?? type.Locations[0],
                    projectionName.Name ?? string.Empty));
            }

            // P2 修复（二十一轮）：PDDD013 的 contextName 沿基类链取最近声明
            var contextName = chainBoundedContext is null ? null : TryGetStringConstructorArgument(chainBoundedContext);
            if (IsStableName(projectionName.Name)
                && IsStableName(contextName)
                && !BelongsToBoundedContext(projectionName.Name!, contextName!))
            {
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add("BoundedContext", contextName);
                context.ReportDiagnostic(Diagnostic.Create(
                    ProjectionNameContextMismatch,
                    projectionName.Location ?? type.Locations[0],
                    properties,
                    projectionName.Name,
                    contextName));
            }
        }
    }

    private static bool IsDomainModelType(INamedTypeSymbol type)
        => InheritsFrom(type, DomainEventName)
           || InheritsFrom(type, EntityName)
           || InheritsFrom(type, AggregateRootName)
           || ImplementsInterface(type, DomainEventInterfaceName);

    // P3 修复（二十一轮）：abstract 事件基类排除——PDDD005（缺 [GenerateMessage]）与
    // PDDD012（未 sealed）在 abstract 上不可消解（sealed 与 abstract 互斥、契约由
    // 最终 sealed 派生类声明），原实现误报；IsDomainModelType 不变（PDDD001 的
    // abstract 基类由基类链查找消解，见 chainBoundedContext）
    private static bool IsDomainEventType(INamedTypeSymbol type)
        => !type.IsAbstract
           && (InheritsFrom(type, DomainEventName)
               || ImplementsInterface(type, DomainEventInterfaceName));

    private static bool InheritsFrom(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (MetadataNameEquals(current, metadataName))
                return true;
        }

        return false;
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, string metadataName)
    {
        foreach (var @interface in type.AllInterfaces)
        {
            if (MetadataNameEquals(@interface, metadataName))
                return true;
        }

        return false;
    }

    private static bool ImplementsGenericInterface(INamedTypeSymbol type, string metadataName)
    {
        foreach (var @interface in type.AllInterfaces)
        {
            if (@interface.OriginalDefinition is { } original && MetadataNameEquals(original, metadataName))
                return true;
        }

        return false;
    }

    private static AttributeData? TryGetAttribute(INamedTypeSymbol type, string metadataName)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is not null && MetadataNameEquals(attribute.AttributeClass, metadataName))
                return attribute;
        }

        return null;
    }

    // P2 修复（十七轮）：GetAttributes 不含继承（AttributeUsage.Inherited=true 的 attribute
    // 在运行时反射对派生类可见，编译符号模型不可见）——沿 BaseType 链查找存在性，
    // 与运行时反射语义对齐。仅用于 shape 检查的存在性判断；具体参数提取（如
    // TryGetStringConstructorArgument）仍走直接声明路径，避免派生类重复报 PDDD002。
    private static bool HasAttributeAlongBaseChain(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass is not null && MetadataNameEquals(attribute.AttributeClass, metadataName))
                    return true;
            }
        }

        return false;
    }

    // P2 修复（二十一轮）：沿 BaseType 链取最近声明的 attribute 实例——派生类未直接
    // 声明时继承基类值（AttributeUsage.Inherited=true），多级链取离派生类最近的声明
    // （镜像运行时 Attribute.GetCustomAttribute 的"最近声明胜出"语义）。供 PDDD001
    // 存在性判断与 PDDD008/013/014 的 contextName 提取使用。
    private static AttributeData? TryGetAttributeAlongBaseChain(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass is not null && MetadataNameEquals(attribute.AttributeClass, metadataName))
                    return attribute;
            }
        }

        return null;
    }

    private static int? TryGetNamedIntArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is int value)
                return value;
        }

        return null;
    }

    private static string? TryGetStringConstructorArgument(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
            return null;

        return attribute.ConstructorArguments[0].Value as string;
    }

    private static string? TryGetNamedStringArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name)
                return argument.Value.Value as string;
        }

        return null;
    }

    private static (string? Name, Location? Location) TryGetProjectionName(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        // P2 修复（十七轮）：GetMembers 仅返回本类型声明成员（不含继承）——
        // ProjectionName 声明在投影基类（统一命名模板）时原实现查不到，误报
        // PDDD007。沿 BaseType 链逐层查找（镜像 TryGetStaticStringProperty 的八轮修复）。
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers("ProjectionName"))
            {
                if (member is not IPropertySymbol property
                    || property.Type.SpecialType != SpecialType.System_String)
                {
                    continue;
                }

                foreach (var syntaxReference in property.DeclaringSyntaxReferences)
                {
                    var syntax = syntaxReference.GetSyntax(cancellationToken);
                    if (syntax is not PropertyDeclarationSyntax declaration)
                        continue;

                    if (declaration.ExpressionBody?.Expression is LiteralExpressionSyntax expressionLiteral)
                        return (expressionLiteral.Token.Value as string, expressionLiteral.GetLocation());

                    if (declaration.Initializer?.Value is LiteralExpressionSyntax initializerLiteral)
                        return (initializerLiteral.Token.Value as string, initializerLiteral.GetLocation());

                    foreach (var accessor in declaration.AccessorList?.Accessors ?? [])
                    {
                        if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                            continue;

                        if (accessor.ExpressionBody?.Expression is LiteralExpressionSyntax getterLiteral)
                            return (getterLiteral.Token.Value as string, getterLiteral.GetLocation());

                        if (accessor.Body is null)
                            continue;

                        foreach (var statement in accessor.Body.Statements)
                        {
                            if (statement is ReturnStatementSyntax
                                {
                                    Expression: LiteralExpressionSyntax returnLiteral
                                })
                            {
                                return (returnLiteral.Token.Value as string, returnLiteral.GetLocation());
                            }
                        }
                    }

                    return (null, declaration.GetLocation());
                }
            }
        }

        return (null, null);
    }

    private static (string? Name, Location? Location) TryGetStaticStringProperty(
        INamedTypeSymbol type,
        string propertyName,
        CancellationToken cancellationToken)
    {
        // P3 修复（八轮评审）：GetMembers 仅返回本类型声明成员（不含继承）——
        // EventName 声明在基类（含 static abstract 继承链，如 GenerateMessage 契约的
        // 基类事件）时原实现查不到，误报 NameMismatch。沿 BaseType 链逐层查找。
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (TryGetLiteralFromTypeMembers(current, propertyName, cancellationToken) is { } fromBase)
                return fromBase;
        }

        // P3 修复（二十一轮）：接口默认实现（static virtual 成员带默认体）——类未重写
        // EventName 时声明语法挂在接口上，BaseType 链查不到，原实现误报 PDDD015；
        // 补 AllInterfaces 遍历（IDomainEvent 的 static abstract 声明来自元数据引用、
        // 无 DeclaringSyntaxReferences，自动跳过）
        foreach (var @interface in type.AllInterfaces)
        {
            if (TryGetLiteralFromTypeMembers(@interface, propertyName, cancellationToken) is { } fromInterface)
                return fromInterface;
        }

        return (null, null);
    }

    // P3 修复（二十一轮）：从 TryGetStaticStringProperty 提取的单类型扫描——
    // 返回 null 表示该类型无可识别声明（继续沿链/接口查找），非 null 表示找到
    // （含"找到声明但非字面量"的 (null, location) 形态，调用方据此停止查找）
    private static (string? Name, Location? Location)? TryGetLiteralFromTypeMembers(
        INamedTypeSymbol type,
        string propertyName,
        CancellationToken cancellationToken)
    {
        foreach (var member in type.GetMembers(propertyName))
        {
            if (member is not IPropertySymbol property
                || !property.IsStatic
                || property.Type.SpecialType != SpecialType.System_String)
            {
                continue;
            }

            foreach (var syntaxReference in property.DeclaringSyntaxReferences)
            {
                var syntax = syntaxReference.GetSyntax(cancellationToken);
                if (syntax is not PropertyDeclarationSyntax declaration)
                    continue;

                if (declaration.ExpressionBody?.Expression is LiteralExpressionSyntax expressionLiteral)
                    return (expressionLiteral.Token.Value as string, expressionLiteral.GetLocation());

                if (declaration.Initializer?.Value is LiteralExpressionSyntax initializerLiteral)
                    return (initializerLiteral.Token.Value as string, initializerLiteral.GetLocation());

                foreach (var accessor in declaration.AccessorList?.Accessors ?? [])
                {
                    if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                        continue;

                    if (accessor.ExpressionBody?.Expression is LiteralExpressionSyntax getterLiteral)
                        return (getterLiteral.Token.Value as string, getterLiteral.GetLocation());

                    if (accessor.Body is null)
                        continue;

                    foreach (var statement in accessor.Body.Statements)
                    {
                        if (statement is ReturnStatementSyntax
                        {
                            Expression: LiteralExpressionSyntax returnLiteral
                        })
                        {
                            return (returnLiteral.Token.Value as string, returnLiteral.GetLocation());
                        }
                    }
                }

                return (null, declaration.GetLocation());
            }
        }

        return null;
    }

    private static bool MetadataNameEquals(INamedTypeSymbol type, string metadataName)
        => GetFullMetadataName(type) == metadataName;

    private static string GetFullMetadataName(INamedTypeSymbol type)
    {
        var containingNamespace = type.ContainingNamespace;
        if (containingNamespace is null || containingNamespace.IsGlobalNamespace)
            return type.MetadataName;

        return containingNamespace.ToDisplayString() + "." + type.MetadataName;
    }

    private static bool IsStableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var ch in value!)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')
                continue;

            return false;
        }

        return true;
    }

    private static bool BelongsToBoundedContext(string messageName, string boundedContext)
        => StringComparer.Ordinal.Equals(messageName, boundedContext)
           || messageName.StartsWith(boundedContext + ".", StringComparison.Ordinal);

    private static bool HasVersionSuffix(string messageName, int schemaVersion)
        => messageName.EndsWith(".v" + schemaVersion, StringComparison.Ordinal);
}
