using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalDDD.Analyzers;

/// <summary>规则族：限界上下文（PDDD001 存在性 / PDDD002 命名）。
/// 精炼重组（2026-08-26）自 AnalyzeNamedType，逻辑与注释逐字保留。</summary>
public sealed partial class StrategicDddAnalyzer
{
    private static void AnalyzeBoundedContextRules(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        AttributeData? boundedContext,
        AttributeData? chainBoundedContext)
    {
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
    }
}
