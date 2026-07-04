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
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
            return;

        var boundedContext = TryGetAttribute(type, BoundedContextAttributeName);
        if (IsDomainModelType(type) && boundedContext is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingBoundedContext,
                type.Locations[0],
                type.Name));
        }

        var generateMessage = IsDomainEventType(type)
            ? TryGetAttribute(type, GenerateMessageAttributeName)
            : null;
        if (IsDomainEventType(type) && generateMessage is null)
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
                if (!StringComparer.Ordinal.Equals(eventName.Name, messageName))
                {
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add("ExpectedMessageName", messageName);
                    context.ReportDiagnostic(Diagnostic.Create(
                        DomainEventNameMismatch,
                        eventName.Location ?? type.Locations[0],
                        properties,
                        eventName.Name ?? string.Empty,
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

        if (boundedContext is not null && generateMessage is not null)
        {
            var contextName = TryGetStringConstructorArgument(boundedContext);
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

            if (IsStableName(contextName)
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
        if (processManager is not null
            && (!type.IsSealed || boundedContext is null || !ImplementsGenericInterface(type, EventHandlerInterfaceName)))
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

            var contextName = boundedContext is null ? null : TryGetStringConstructorArgument(boundedContext);
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
            if (!type.IsSealed || boundedContext is null)
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

            var contextName = boundedContext is null ? null : TryGetStringConstructorArgument(boundedContext);
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

    private static bool IsDomainEventType(INamedTypeSymbol type)
        => InheritsFrom(type, DomainEventName)
           || ImplementsInterface(type, DomainEventInterfaceName);

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
        foreach (var member in type.GetMembers("ProjectionName"))
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

        return (null, null);
    }

    private static (string? Name, Location? Location) TryGetStaticStringProperty(
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

        return (null, null);
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
