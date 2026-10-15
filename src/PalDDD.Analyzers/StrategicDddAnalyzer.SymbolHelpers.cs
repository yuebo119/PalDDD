using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PalDDD.Shared;

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
                    || HasExplicitInterfaceImplementationNamed(property, "ProjectionName");
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

                    // v66 P3：姊妹补齐（v65 只修 ExpressionBody 腿——对齐 TryGetLiteralFromTypeMembers 四形态）
                    if (declaration.Initializer?.Value is LiteralExpressionSyntax initializerLiteral)
                    {
                        if (initializerLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                            || initializerLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                            return (null, null);
                        return (initializerLiteral.Token.Value as string, initializerLiteral.GetLocation());
                    }

                    foreach (var accessor in declaration.AccessorList?.Accessors ?? [])
                    {
                        if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                            continue;

                        // v66 P3：姊妹补齐（null/default 视为缺失——对齐四形态）
                        if (accessor.ExpressionBody?.Expression is LiteralExpressionSyntax getterLiteral)
                        {
                            if (getterLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                                || getterLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                                return (null, null);
                            return (getterLiteral.Token.Value as string, getterLiteral.GetLocation());
                        }

                        if (accessor.Body is null)
                            continue;

                        foreach (var statement in accessor.Body.Statements)
                        {
                            if (statement is ReturnStatementSyntax
                                {
                                    Expression: LiteralExpressionSyntax returnLiteral
                                })
                            {
                                // v70 P3：null/default 视为缺失（v66 四形态最后一腿）。
                                // v72 勘正：此侧消费方对 (null,loc)/(null,null) 均报 PDDD007——
                                // 修复无行为可观察差异（仅 EventName 侧 ITM-124 分支有差异），
                                // 属防御性形态一致化，黑盒不可锁定
                                if (returnLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                                    || returnLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                                    return (null, null);
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
                || HasExplicitInterfaceImplementationNamed(property, propertyName);
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
                    if (expressionLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                        || expressionLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                        return (null, null);
                    return (expressionLiteral.Token.Value as string, expressionLiteral.GetLocation());
                }

                // v66 P3：null/default 字面量在全部四形态视为缺失（v64 只修了 ExpressionBody 腿——
                // Initializer/getter 表达式体/getter return 三腿仍被 Token.Value as string 的 null
                // 误判"找到但非字面量"静默跳过；实证 { get; } = null 与 get => null 均零诊断）
                if (declaration.Initializer?.Value is LiteralExpressionSyntax initializerLiteral)
                {
                    if (initializerLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                        || initializerLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                        return (null, null);
                    return (initializerLiteral.Token.Value as string, initializerLiteral.GetLocation());
                }

                foreach (var accessor in declaration.AccessorList?.Accessors ?? [])
                {
                    if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                        continue;

                    if (accessor.ExpressionBody?.Expression is LiteralExpressionSyntax getterLiteral)
                    {
                        if (getterLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                            || getterLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                            return (null, null);
                        return (getterLiteral.Token.Value as string, getterLiteral.GetLocation());
                    }

                    if (accessor.Body is null)
                        continue;

                    foreach (var statement in accessor.Body.Statements)
                    {
                        if (statement is ReturnStatementSyntax
                            {
                                Expression: LiteralExpressionSyntax returnLiteral
                            })
                        {
                            if (returnLiteral.IsKind(SyntaxKind.NullLiteralExpression)
                                || returnLiteral.IsKind(SyntaxKind.DefaultLiteralExpression))
                                return (null, null);
                            return (returnLiteral.Token.Value as string, returnLiteral.GetLocation());
                        }
                    }
                }

                return (null, declaration.GetLocation());
            }
        }

        return null;
    }

    // R10（D-F5，2026-10-15 性能实施轮）：零分配元数据名比对——IDE 增量分析热路径。
    // 原实现每次比对经 GetFullMetadataName 构造全名串（命名空间 ToDisplayString 分配 +
    // ContainingType 链逐级 '+' 拼接分配），而该比对在每类型分析中发生数十次
    //（基类链 × 接口集 × attribute 集逐候选比对）；现按符号结构逐段对照常量
    // metadataName，全程零字符串构造。语义与旧全名串比对逐字等价：命名空间链
    //（自外向内，'.' 分隔）+ ContainingType 链（自外向内，'+' 分隔——ITM-220 嵌套名
    // 防顶层同名碰撞）+ 自身 MetadataName（含泛型元数 `n）；global 命名空间无前缀段
    //（v26 P3 分支，含 ContainingType 链照拼）。对照矩阵（嵌套/顶层碰撞/泛型/global）
    // 由 AnalyzerAllocationTests 锁定。
    internal static bool MetadataNameEquals(INamedTypeSymbol type, string metadataName)
    {
        var pos = 0;
        return MatchNamespaceTree(type.ContainingNamespace, metadataName, ref pos)
            && MatchTypeTree(type, metadataName, ref pos)
            && pos == metadataName.Length;
    }

    /// <summary>自最外层命名空间向内逐段匹配（段间 '.'）。</summary>
    private static bool MatchNamespaceTree(INamespaceSymbol? ns, string expected, ref int pos)
    {
        if (ns is null || ns.IsGlobalNamespace)
            return true;

        return MatchNamespaceTree(ns.ContainingNamespace, expected, ref pos)
            && MatchSegment(ns.Name, expected, ref pos)
            && ConsumeSeparator(expected, ref pos, '.');
    }

    /// <summary>自最外层 ContainingType 向内逐段匹配（段间 '+'，最内层为自身）。</summary>
    private static bool MatchTypeTree(INamedTypeSymbol type, string expected, ref int pos)
    {
        if (type.ContainingType is { } containing)
        {
            if (!MatchTypeTree(containing, expected, ref pos)
                || !ConsumeSeparator(expected, ref pos, '+'))
            {
                return false;
            }
        }

        // MetadataName 等价形态（零分配）：Name + arity>0 时的 "`{Arity}"——Roslyn 的
        // MetadataName 对泛型源符号每次访问重新构造 arity 串（探针实测 40B/次，本方法的
        // 零分配断言最初正被该属性判红），故逐段比对不经该属性
        if (!MatchSegment(type.Name, expected, ref pos))
            return false;
        if (type.Arity == 0)
            return true;
        return ConsumeSeparator(expected, ref pos, '`') && MatchArity(type.Arity, expected, ref pos);
    }

    /// <summary>匹配十进制元数（"`n" 的 n 段，前向逐位）。</summary>
    private static bool MatchArity(int arity, string expected, ref int pos)
    {
        var digits = 1;
        var divisor = 1;
        for (var value = arity; value >= 10; value /= 10)
        {
            digits++;
            divisor *= 10;
        }

        if (expected.Length - pos < digits)
            return false;

        for (var place = divisor; place > 0; place /= 10)
        {
            if (expected[pos] != (char)('0' + arity / place % 10))
                return false;
            pos++;
        }

        return true;
    }

    private static bool MatchSegment(string segment, string expected, ref int pos)
    {
        if (expected.Length - pos < segment.Length)
            return false;

        for (var i = 0; i < segment.Length; i++)
        {
            if (expected[pos + i] != segment[i])
                return false;
        }

        pos += segment.Length;
        return true;
    }

    private static bool ConsumeSeparator(string expected, ref int pos, char separator)
    {
        if (pos < expected.Length && expected[pos] == separator)
        {
            pos++;
            return true;
        }

        return false;
    }

    // R10（D-F5）：显式接口实现按名查找——原调用点（TryGetProjectionName /
    // TryGetLiteralFromTypeMembers）用 ExplicitInterfaceImplementations.Any(闭包)，
    // 每方法调用各分配一个闭包对象；手写循环零分配（语义逐字等价：任一显式实现的
    // 简单名命中即真——v57 P1/v58 的显式实现匹配语义不变）。
    internal static bool HasExplicitInterfaceImplementationNamed(IPropertySymbol property, string name)
    {
        foreach (var implementation in property.ExplicitInterfaceImplementations)
        {
            if (implementation.Name == name)
                return true;
        }

        return false;
    }

    // ADR 022：实现收敛至 PalDDD.Shared/StableNameValidation.cs（与 PalDDD.Core.SourceGen
    // 链接编译同一份源码）——原两处逐字重复副本存在漂移风险，本方法仅作调用点适配。
    private static bool IsStableName(string? value) => StableNameValidation.IsStable(value);

    // R10（D-F5）：零分配上下文归属判定——原实现 boundedContext + "." 每次调用分配一个
    // 前缀串（每消息类型比对路径必过）；语义逐字等价：序数相等，或后随 '.' 的序数前缀
    //（拼接形态 StartsWith(ctx + ".") 拆为 StartsWith(ctx) + 点字符对照）。
    internal static bool BelongsToBoundedContext(string messageName, string boundedContext)
        => StringComparer.Ordinal.Equals(messageName, boundedContext)
           || (messageName.Length > boundedContext.Length
               && messageName.StartsWith(boundedContext, StringComparison.Ordinal)
               && messageName[boundedContext.Length] == '.');

    // R10（D-F5）：零分配版本后缀判定——原实现 ".v" + schemaVersion 每次调用分配一个
    // 后缀串；改为尾段逐位对照十进制形态（含负号腿——int 全域与字符串拼接形态等价，
    // 调用方已门控 schemaVersion >= 1，负形态为防御性等价保留）。前导零形态天然拒绝
    //（"x.v01" 与版本 1 不等价，与拼接形态一致——由 AnalyzerAllocationTests 锁定）。
    internal static bool HasVersionSuffix(string messageName, int schemaVersion)
    {
        var isNegative = schemaVersion < 0;
        var magnitude = isNegative ? -(long)schemaVersion : schemaVersion;
        var digits = 1;
        for (var value = magnitude; value >= 10; value /= 10)
            digits++;

        var numberStart = messageName.Length - digits;
        if (numberStart < 0)
            return false;

        // 数字段之前：负号腿先吃 '-'，随后（两腿同款）必须恰为 '.' 'v'
        var prefixEnd = numberStart;
        if (isNegative)
        {
            prefixEnd = numberStart - 1;
            if (prefixEnd < 0 || messageName[prefixEnd] != '-')
                return false;
        }

        if (prefixEnd < 2 || messageName[prefixEnd - 1] != 'v' || messageName[prefixEnd - 2] != '.')
            return false;

        var remaining = magnitude;
        for (var i = messageName.Length - 1; i >= numberStart; i--)
        {
            if (messageName[i] != (char)('0' + remaining % 10))
                return false;
            remaining /= 10;
        }

        return true;
    }
}
