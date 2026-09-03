using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.Analyzers;

/// <summary>符号/语法树辅助（精炼重组 2026-08-26 自 AnalyzeNamedType 尾段，逻辑与注释逐字保留）。</summary>
// v60 P3-8 边界声明：TryGetProjectionName/TryGetStaticStringProperty 依赖
// DeclaringSyntaxReferences 提取字面量——字面量声明在外部程序集基类（NuGet 包内）时
// 语法引用为空，合规代码会报 PDDD007/PDDD015 且无仓内消解路径；投影/事件基类需仓内源码。
public sealed partial class StrategicDddAnalyzer
{
    private const string BoundedContextAttributeName = "PalDDD.Core.BoundedContextAttribute";
    private const string ProcessManagerAttributeName = "PalDDD.Core.ProcessManagerAttribute";
    private const string GenerateMessageAttributeName = "PalDDD.Core.GenerateMessageAttribute";
    private const string DomainEventName = "PalDDD.Core.DomainEvent";
    private const string EntityName = "PalDDD.Core.Entity";
    private const string AggregateRootName = "PalDDD.Core.AggregateRoot`1";
    private const string DomainEventInterfaceName = "PalDDD.Core.IDomainEvent";
    private const string EventHandlerInterfaceName = "PalDDD.Messaging.IEventHandler`1";
    private const string ProjectionHandlerInterfaceName = "PalDDD.Projections.IProjectionHandler`1";

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
    // v35 P3（DA5）：原实现与 TryGetAttributeAlongBaseChain 逐字重复——改为其非空判断
    //（单一实现），链遍历与"最近声明胜出"语义由 TryGet 单点维护
    private static bool HasAttributeAlongBaseChain(INamedTypeSymbol type, string metadataName)
        => TryGetAttributeAlongBaseChain(type, metadataName) is not null;

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
            // v58：与 TryGetLiteralFromTypeMembers 同款显式实现盲区修复——GetMembers(name)
            // 按名查找不含显式接口实现（IProjectionHandler.ProjectionName 显式实现形态漏检 PDDD013）
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol property
                    || property.Type.SpecialType != SpecialType.System_String)
                {
                    continue;
                }
                var matchesProjection = property.Name == "ProjectionName"
                    || property.ExplicitInterfaceImplementations.Any(p => p.Name == "ProjectionName");
                if (!matchesProjection)
                    continue;

                foreach (var syntaxReference in property.DeclaringSyntaxReferences)
                {
                    var syntax = syntaxReference.GetSyntax(cancellationToken);
                    if (syntax is not PropertyDeclarationSyntax declaration)
                        continue;

                    // v65：null/default 字面量视为缺失（对齐 TryGetLiteralFromTypeMembers v64——
                    // Token.Value as string 对 null 字面量产 null，被误判"找到但值为 null"分支跳过比对）
                    if (declaration.ExpressionBody?.Expression is LiteralExpressionSyntax expressionLiteral)
                    {
                        if (expressionLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                            || expressionLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                            return (null, null);
                        return (expressionLiteral.Token.Value as string, expressionLiteral.GetLocation());
                    }

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
        // v57 P1（analyzer 真实缺陷实证修复）：GetMembers(name) 按名查找**不含显式接口实现**——
        // IDomainEvent.EventName 是 static abstract（C# 强制显式实现），框架合规写法
        // `static string IDomainEvent.EventName => "..."` 被按名查找漏掉 → PDDD015 假报
        // missing（真实项目 TreatWarningsAsErrors 下合规用户编译失败）。改无参枚举 +
        // 简单名/显式实现链双匹配（探针环境 GetMembers("EventName") 返回空的直证）
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol property
                || !property.IsStatic
                || property.Type.SpecialType != SpecialType.System_String)
            {
                continue;
            }
            var matchesTarget = property.Name == propertyName
                || property.ExplicitInterfaceImplementations.Any(p => p.Name == propertyName);
            if (!matchesTarget)
                continue;

            foreach (var syntaxReference in property.DeclaringSyntaxReferences)
            {
                var syntax = syntaxReference.GetSyntax(cancellationToken);
                if (syntax is not PropertyDeclarationSyntax declaration)
                    continue;

                if (declaration.ExpressionBody?.Expression is LiteralExpressionSyntax expressionLiteral)
                {
                    // v64 P3：null/default 字面量视为缺失（EventName => null 与 Name 必不等，
                    // 原被 Token.Value as string 的 null 误判为"非字面量"而静默跳过——
                    // 现返回 (null, null) 走 Missing 腿报告）
                    if (expressionLiteral.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression)
                        || expressionLiteral.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.DefaultLiteralExpression))
                        return (null, null);
                    return (expressionLiteral.Token.Value as string, expressionLiteral.GetLocation());
                }

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
        // ITM-220 修复（三十二轮）：嵌套类型拼 ContainingType 链（Ns.Outer+Inner）——
        // MetadataName 仅返回最内层简名，原实现使嵌套 Inner 与顶层 Ns.Inner 元数据名碰撞
        // v26 P3：global 命名空间分支同样拼 ContainingType 链——原实现直接 return
        // type.MetadataName 简名，global 嵌套类型（global::Outer.Inner）返回 "Inner"，
        // 与 MetadataNameEquals 的全名比对及嵌套类型语义均失真；拼接逻辑前移为两分支共用
        var name = type.MetadataName;
        var containing = type.ContainingType;
        while (containing is not null)
        {
            name = containing.MetadataName + "+" + name;
            containing = containing.ContainingType;
        }

        var containingNamespace = type.ContainingNamespace;
        if (containingNamespace is null || containingNamespace.IsGlobalNamespace)
            return name;

        return containingNamespace.ToDisplayString() + "." + name;
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
