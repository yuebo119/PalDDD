using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace PalDDD.Analyzers;

/// <summary>规则族：消息契约（PDDD005 契约缺失 / PDDD008 上下文归属 / PDDD009 命名 /
/// PDDD010 版本后缀 / PDDD011 版本正数 / PDDD012 sealed / PDDD015 EventName 一致）。
/// 精炼重组（2026-08-26）自 AnalyzeNamedType，逻辑与注释逐字保留。</summary>
public sealed partial class StrategicDddAnalyzer
{
    private static void AnalyzeMessageContractRules(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        AttributeData? chainBoundedContext,
        AttributeData? generateMessage)
    {
        // ITM-123 修复：PDDD005 仅对 class 生效——[GenerateMessage] 为 AttributeTargets.Class，
        // struct 事件不可消解（与 PDDD001 的 struct 排除对称）
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
                // v40 P3：完全缺失腿（(null, null)）单列专用消息 DomainEventNameMissing——
                // 原实现该腿落入比对消息，EventName 值占位符格式化 null 为空串，诊断不可读；
                // 专用诊断带同款 ExpectedMessageName 属性（code fix 侧经类声明定位找不到
                // EventName 属性仍不注册 fix，MissingEventNameDeclaration 测试锁定的行为不变）
                if (eventName is { Name: null, Location: null })
                {
                    var missingProperties = ImmutableDictionary<string, string?>.Empty
                        .Add("ExpectedMessageName", messageName);
                    context.ReportDiagnostic(Diagnostic.Create(
                        DomainEventNameMissing,
                        type.Locations[0],
                        missingProperties,
                        type.Name,
                        messageName));
                }
                else if (eventName is not { Name: null, Location: not null }
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
    }
}
