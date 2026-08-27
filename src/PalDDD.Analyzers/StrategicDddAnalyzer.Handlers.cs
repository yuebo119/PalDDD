using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace PalDDD.Analyzers;

/// <summary>规则族：处理器形状与命名（PDDD003 ProcessManager 形状 / PDDD004 投影形状 /
/// PDDD006 PM 命名 / PDDD007 投影命名 / PDDD013 投影上下文 / PDDD014 PM 上下文）。
/// 精炼重组（2026-08-26）自 AnalyzeNamedType，逻辑与注释逐字保留。</summary>
public sealed partial class StrategicDddAnalyzer
{
    private static void AnalyzeHandlerRules(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        AttributeData? chainBoundedContext)
    {
        var processManager = TryGetAttribute(type, ProcessManagerAttributeName);
        // ITM-122 修复：PDDD003 shape 检查用链式 BoundedContext——[ProcessManager] 与
        // [BoundedContext] 均 Inherited=true，派生 ProcessManager 继承基类 [BoundedContext]
        // 时直接声明查不到（boundedContext 仅本类型），误报"未声明 [BoundedContext]"；
        // 与 PDDD001（chainBoundedContext）及 PDDD004（HasAttributeAlongBaseChain）口径对齐
        // P3 修复（二十四轮）：sealed 轴豁免 abstract 基类——sealed 与 abstract 互斥，
        // shape 由最终 sealed 派生类消解（镜像 PDDD004 二十一轮修复，姊妹漏网）；
        // BoundedContext 链与 IEventHandler 接口轴对 abstract 仍检查（与 PDDD004 一致）
        if (processManager is not null
            && ((!type.IsSealed && !type.IsAbstract) || chainBoundedContext is null || !ImplementsGenericInterface(type, EventHandlerInterfaceName)))
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
}
