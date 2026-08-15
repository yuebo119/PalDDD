using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.Core.SourceGen;


// ─────────────────────────────────────────────────────────────
// 源码生成器 — 智能枚举
// ─────────────────────────────────────────────────────────────

/// <summary>智能枚举增量源码生成器 — 编译时 walk 语法树，产出硬编码字段引用，完全 AOT 兼容</summary>
/// <remarks>
/// 生成代码通过调用 <c>RegisterValues([Field1, Field2, ...])</c> 在静态构造函数中注入编译时已知的值。<br/>
/// 运行时零反射——所有字段名在编译时已确定。<br/>
/// 用法：<c>[GenerateEnum] public partial class OrderStatus : SmartEnum&lt;OrderStatus, string&gt; { ... }</c>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EnumGenerator : IIncrementalGenerator
{
    private const string AttrName = "PalDDD.Core.GenerateEnumAttribute";

    private static readonly DiagnosticDescriptor NoFieldsWarning = new(
        "PALENUM001",
        "Generated enum has no static fields to register",
        "Type '{0}' is marked with [GenerateEnum] but has no public static readonly fields. Add at least one field or remove the attribute.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotDirectInheritanceError = new(
        "PALENUM002",
        "GenerateEnum requires direct SmartEnum inheritance",
        "Type '{0}' is marked with [GenerateEnum] but does not directly inherit SmartEnum<TSelf, TValue> (found '{1}'). GenerateEnum only supports direct inheritance.",
        "PalDDD.EnumGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 步骤 1：收集所有标记了 [GenerateEnum] 的 partial class 及其静态字段
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttrName,
            predicate: static (node, _) =>
                node is ClassDeclarationSyntax c
                && c.Modifiers.Any(SyntaxKind.PartialKeyword),
            transform: static (context, ct) =>
            {
                var classSymbol = (INamedTypeSymbol)context.TargetSymbol;

                // 从基类 SmartEnum<TSelf, TValue> 提取 TValue
                var baseType = classSymbol.BaseType;
                if (baseType is not INamedTypeSymbol { TypeArguments.Length: 2 } namedBase)
                {
                    // P2 修复：隔层继承不再静默跳过——报 PALENUM002（与 PALENUM001 对称）
                    return new EnumGenInfo(
                        Namespace: classSymbol.ContainingNamespace?.ToDisplayString() ?? "_",
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ValueType: baseType?.ToDisplayString() ?? "?",
                        Fields: [],
                        HasFields: false,
                        DiagnosticId: "PALENUM002",
                        DiagnosticMessage: $"Type '{classSymbol.Name}' is marked with [GenerateEnum] but does not directly inherit SmartEnum<TSelf, TValue> (found '{baseType?.ToDisplayString() ?? "none"}').",
                        Location: context.TargetNode.GetLocation());
                }

                var valueType = namedBase.TypeArguments[1];

                // P2 修复（嵌套类型）：镜像 IdentityGenerator——ContainingNamespace 不含
                // 类型层级，生成物需按 ContainingType 链包 partial 声明，否则 namespace 级
                // 平铺的同名类型与用户声明的嵌套 partial 不合并（平行类型）。
                var containingDeclarations = new List<string>();
                for (var t = classSymbol.ContainingType; t is not null; t = t.ContainingType)
                {
                    var kind = t.IsRecord
                        ? (t.TypeKind == TypeKind.Struct ? "partial record struct" : "partial record")
                        : t.TypeKind == TypeKind.Struct ? "partial struct" : "partial class";
                    var arity = t.Arity > 0
                        ? $"<{string.Join(", ", t.TypeParameters.Select(pr => pr.Name))}>"
                        : "";
                    containingDeclarations.Insert(0, $"{kind} {t.Name}{arity}");
                }

                // 编译时 walk 语法树：收集所有 partial 声明中的 public/internal static 字段
                // ITM-070：字段可能拆分在多个 partial 文件——用 Symbol 的全部声明引用遍历，
                // 而非仅 TargetNode（带 attribute 的那个声明），否则跨文件字段被遗漏，
                // 触发 PALENUM001 且不生成注册代码。
                var fields = ImmutableArray.CreateBuilder<string>();
                foreach (var reference in classSymbol.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(ct) is not ClassDeclarationSyntax partialDecl)
                        continue;

                    foreach (var member in partialDecl.Members)
                    {
                        if (member is FieldDeclarationSyntax fds
                            && fds.Modifiers.Any(SyntaxKind.StaticKeyword)
                            && (fds.Modifiers.Any(SyntaxKind.PublicKeyword) || fds.Modifiers.Any(SyntaxKind.InternalKeyword)))
                        {
                            foreach (var variable in fds.Declaration.Variables)
                                fields.Add(variable.Identifier.Text);
                        }
                    }
                }

                if (fields.Count == 0)
                {
                    // 有 [GenerateEnum] 但无静态字段 — 返回空字段信息以触发警告
                    return new EnumGenInfo(
                        Namespace: classSymbol.ContainingNamespace?.ToDisplayString() ?? "_",
                        TypeName: classSymbol.Name,
                        ContainingDeclarations: [],
                        ValueType: valueType.ToDisplayString(),
                        Fields: [],
                        HasFields: false);
                }

                return new EnumGenInfo(
                    Namespace: classSymbol.ContainingNamespace?.ToDisplayString() ?? "_",
                    TypeName: classSymbol.Name,
                    ContainingDeclarations: [.. containingDeclarations],
                    ValueType: valueType.ToDisplayString(),
                    Fields: fields.ToImmutable(),
                    HasFields: true);
            })
            .WithTrackingName("EnumGenerator_Candidates")
            .Where(static info => info is not null)!;

        // 步骤 2：有字段时生成，无字段时报告警告
        context.RegisterSourceOutput(candidates, static (spc, info) =>
        {
            if (info!.DiagnosticId is not null)
            {
                // P2 修复：隔层继承报 PALENUM002（Error 级）
                spc.ReportDiagnostic(Diagnostic.Create(
                    NotDirectInheritanceError,
                    info.Location ?? Location.None,
                    info.TypeName,
                    info.ValueType));
                return;
            }
            if (!info!.HasFields)
            {
                // 报告警告而非静默跳过
                spc.ReportDiagnostic(Diagnostic.Create(
                    NoFieldsWarning,
                    Location.None,
                    info.TypeName));
                return;
            }
            spc.AddSource(
                $"{info!.Namespace}.{info.TypeName}.g.cs",
                GenerateEnumCode(info));
        });
    }

    /// <summary>生成硬编码字段引用的静态构造函数——零反射，100% AOT 兼容</summary>
    private static string GenerateEnumCode(EnumGenInfo info)
    {
        // 构建字段列表：Field1, Field2, Field3
        var fieldList = string.Join(",\n            ", info.Fields);
        // P2 修复：嵌套类型按 ContainingType 链包 partial 声明（零嵌套时 open/close 为空，输出与旧版一致）
        var open = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(d => $"{d}\n{{")) + "\n"
            : "";
        var close = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(_ => "}")) + "\n"
            : "";

        return $$"""
// <auto-generated/>
using System.Runtime.CompilerServices;

namespace {{info.Namespace}};{{open}}
partial class {{info.TypeName}}
{
    /// <summary>编译时值注册——所有字段引用均为硬编码，零反射，完全 AOT 兼容</summary>
    [ModuleInitializer]
    internal static void __PalRegisterValues()
    {
        RegisterValues([
            {{fieldList}}
        ]);
    }
}{{close}}
""";
    }

    private sealed record EnumGenInfo(
        string Namespace,
        string TypeName,
        string[] ContainingDeclarations,
        string ValueType,
        ImmutableArray<string> Fields,
        bool HasFields = true,
        string? DiagnosticId = null,
        string? DiagnosticMessage = null,
        Location? Location = null);
}
