using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.Core.SourceGen;


// ─────────────────────────────────────────────────────────────
// 源码生成器 — 强类型 ID
// ─────────────────────────────────────────────────────────────

[Generator(LanguageNames.CSharp)]
public sealed class IdentityGenerator : IIncrementalGenerator
{
    private const string AttributeName = "PalDDD.Core.GenerateIdAttribute";

    // P3 修复（八轮评审）：非白名单 IdType 从"生成永不成功的 TryParse"改为编译期诊断，
    // 仿 MessageRegistryGenerator/EnumGenerator 的 PALMSG/PALENUM 诊断模式
    private static readonly DiagnosticDescriptor UnsupportedIdSourceType = new(
        "PALID001",
        "GenerateId source type is not supported",
        "Type '{0}' uses [GenerateId] with unsupported source type '{1}'. Supported source types: System.Guid, ByteAether.Ulid.Ulid, int (Int32), long (Int64), string.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P3 修复（九轮评审）：非 partial record struct 声明从"静默跳过"改为编译期诊断——
    // 静默让错误延迟到使用点 CS0117（无指向性），与 PALENUM003 的反馈哲学对齐
    private static readonly DiagnosticDescriptor NonPartialRecordStructDeclaration = new(
        "PALID002",
        "GenerateId target must be a partial record struct",
        "Type '{0}' uses [GenerateId] but is not declared as a partial record struct. Declare it as 'partial record struct' so the generator can merge generated members.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            predicate: static (node, _) => IsStructKindDeclaration(node),
            transform: static (context, ct) =>
            {
                var structSymbol = (INamedTypeSymbol)context.TargetSymbol;
                var attrData = context.Attributes[0];
                var sourceType = (INamedTypeSymbol)attrData.ConstructorArguments[0].Value!;

                // P3 修复（九轮评审）：非 partial record struct 声明报 PALID002——
                // 生成物恒为 partial record struct，普通 struct / 非 partial 声明无法合并
                var isPartialRecordStruct = structSymbol.IsRecord
                    && structSymbol.DeclaringSyntaxReferences.Any(static r =>
                        r.GetSyntax() is TypeDeclarationSyntax d
                        && d.Modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword)));
                if (!isPartialRecordStruct)
                {
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID002",
                        Location: context.TargetNode.GetLocation());
                }

                // P2 修复：嵌套类型——ContainingNamespace 不含类型层级，生成物需按
                // ContainingType 链包 partial 声明；否则 namespace 级平铺的同名类型
                // 与用户声明的嵌套 partial 不合并（Outer.Inner 得不到 IPalIdentity 实现）。
                var containingDeclarations = new List<string>();
                var containingNames = new List<string>();
                for (var t = structSymbol.ContainingType; t is not null; t = t.ContainingType)
                {
                    var kind = t.IsRecord
                        ? (t.TypeKind == TypeKind.Struct ? "partial record struct" : "partial record")
                        : t.TypeKind == TypeKind.Struct ? "partial struct" : "partial class";
                    var arity = t.Arity > 0
                        ? $"<{string.Join(", ", t.TypeParameters.Select(p => p.Name))}>"
                        : "";
                    containingDeclarations.Insert(0, $"{kind} {t.Name}{arity}");
                    containingNames.Insert(0, t.Name);
                }

                // P3 修复（八轮评审）：全局命名空间不再 fallback "_"——旧值产出
                // "namespace _;" 使生成物落入 _ 命名空间，与用户的全局类型不合并；
                // null 时 emit 侧不生成 namespace 声明
                var namespaceName = structSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNs
                    ? containingNs.ToDisplayString()
                    : null;

                // P3 修复（八轮评审）：白名单外 IdType 报 PALID001——原实现静默生成
                // "result = default; return false;" 的恒失败 TryParse，用户无编译期反馈
                var normalizedSourceType = sourceType.ToDisplayString().Replace("global::", "") switch
                {
                    "System.Guid" => "Guid",
                    "int" => "int",
                    "long" => "long",
                    "string" => "string",
                    "ByteAether.Ulid" => "Ulid",
                    _ => null
                };
                if (normalizedSourceType is null)
                {
                    return new IdGenInfo(
                        Namespace: namespaceName,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [.. containingDeclarations],
                        ContainingNames: [.. containingNames],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID001",
                        Location: context.TargetNode.GetLocation());
                }

                return new IdGenInfo(
                    namespaceName,
                    structSymbol.Name,
                    [.. containingDeclarations],
                    [.. containingNames],
                    normalizedSourceType,
                    sourceType.Name is "Int32" or "Int64" && sourceType.ContainingNamespace?.ToDisplayString() == "System");
            })
            .WithTrackingName("IdentityGenerator_Candidates")
            .Where(static info => info is not null)!;

        context.RegisterSourceOutput(candidates, static (spc, info) =>
        {
            if (info.DiagnosticId is not null)
            {
                // 诊断分派（九轮）：PALID001=源类型白名单外；PALID002=声明形式非 partial record struct
                if (info.DiagnosticId == "PALID002")
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        NonPartialRecordStructDeclaration,
                        info.Location ?? Location.None,
                        info.TypeName));
                }
                else
                {
                    // P3 修复（八轮评审）：PALID001——非白名单 IdType 编译期报错，不生成代码
                    spc.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedIdSourceType,
                        info.Location ?? Location.None,
                        info.TypeName,
                        info.SourceType));
                }
                return;
            }

            var src = GenerateIdentityCode(info);
            // P3 修复（八轮评审）：全局命名空间（Namespace == null）用 "_" 仅作 hint 名保底，
            // 生成代码本身不再含 namespace 声明
            spc.AddSource($"{info.Namespace ?? "_"}.{string.Join("_", info.ContainingNames)}_{info.TypeName}.g.cs", src);
        });
    }

    // P3 修复（九轮评审）：predicate 放宽到全部 struct 类声明（普通 struct + record struct），
    // 非 partial record struct 由 transform 报 PALID002——静默跳过让错误延迟到使用点 CS0117
    private static bool IsStructKindDeclaration(SyntaxNode node)
        => node is StructDeclarationSyntax
           || (node is RecordDeclarationSyntax r
               && r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));

    private static string GenerateIdentityCode(IdGenInfo info)
    {
        var name = info.TypeName;
        var srcType = info.SourceType;

        // P2 修复：嵌套类型——转换器类保持命名空间级，引用需带类型链限定名；
        // struct 本体按 ContainingType 链包 partial 声明（零嵌套时 open/close 为空，输出与旧版一致）
        var fullName = info.ContainingNames.Length > 0
            ? string.Join(".", info.ContainingNames) + "." + name
            : name;
        // P1 修复（五轮评审）：C# 类声明名不允许含点——转换器类名用下划线连接
        var converterName = info.ContainingNames.Length > 0
            ? string.Join("_", info.ContainingNames) + "_" + name
            : name;
        var open = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(d => $"{d}\n{{")) + "\n"
            : "";
        var close = info.ContainingDeclarations.Length > 0
            ? "\n" + string.Join("\n", info.ContainingDeclarations.Select(_ => "}")) + "\n"
            : "";

        var ulidUsing = srcType == "Ulid" ? "\r\nusing PalUlid = ByteAether.Ulid.Ulid;" : "";

        // P3 修复（八轮评审）：全局命名空间（Namespace == null）不生成 namespace 声明——
        // 旧 fallback "_" 产出 "namespace _;" 使生成物落入 _ 命名空间与用户类型不合并
        var nsDecl = info.Namespace is null ? "" : $"namespace {info.Namespace};\n";

        return $$"""
// <auto-generated/>
#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalDDD.Core;{{ulidUsing}}

{{nsDecl}}{{open}}
[TypeConverter(typeof({{converterName}}TypeConverter))]
[JsonConverter(typeof({{converterName}}JsonConverter))]
public readonly partial record struct {{name}} : IPalIdentity<{{srcType}}>, ISpanParsable<{{name}}>
{
    public {{srcType}} Value { get; init; }

    public static {{name}} New() => {{NewBody(srcType)}};
    public static {{name}} From({{srcType}} value) => {{FromBody(srcType)}};
    public override string ToString() => Value.ToString()!;
    public static bool TryParse(string? input, out {{name}} result)
    {
{{TryParseBody(srcType)}}
    }

    // ── ISpanParsable<T> 实现（AOT 安全，ASP.NET Core Minimal API 绑定兼容）──

    public static {{name}} Parse(string s, IFormatProvider? provider)
        => TryParse(s, out var r) ? r : throw new FormatException("Cannot parse '" + s + "' as " + typeof({{name}}).Name + ".");

    public static bool TryParse(string? s, IFormatProvider? provider, out {{name}} result)
        => TryParse(s, out result);

    public static {{name}} Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, provider, out var r) ? r : throw new FormatException("Cannot parse '" + s.ToString() + "' as " + typeof({{name}}).Name + ".");

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out {{name}} result)
    {
{{TryParseSpanBody(srcType)}}
    }
{{(info.IsNumeric ? NumericOperators(name, srcType) : "")}}
}{{close}}

internal sealed class {{converterName}}JsonConverter : JsonConverter<{{fullName}}>
{
    public override {{fullName}} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
{{JsonReadBody(srcType, fullName)}}
    }

    public override void Write(Utf8JsonWriter writer, {{fullName}} value, JsonSerializerOptions options)
    {
{{JsonWriteBody(srcType)}}
    }
}

internal sealed class {{converterName}}TypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || sourceType == typeof({{srcType}});

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value switch
        {
            string s when {{fullName}}.TryParse(s, out var parsed) => parsed,
            {{srcType}} v => {{fullName}}.From(v),
            _ => throw new NotSupportedException()
        };
}
""";
    }

    private static string NewBody(string srcType) => srcType switch
    {
        "Guid" => "new() { Value = Guid.NewGuid() }",
        "Ulid" => "new() { Value = PalUlid.New() }",
        // 数值/字符串类型 Id 由数据库或服务端分配，客户端 New() 无意义 —— 明确报错而非静默返回 default。
        _ => "throw new NotSupportedException(\"Numeric/string identities are assigned by the store; use From(value) instead.\")"
    };

    private static string FromBody(string srcType) => srcType switch
    {
        "string" => "!string.IsNullOrEmpty(value) ? new() { Value = value } : throw new ArgumentException(\"String identity value cannot be null or empty.\", nameof(value))",
        _ => "new() { Value = value }"
    };

    private static string TryParseBody(string srcType) => srcType switch
    {
        "Guid" => "        if (Guid.TryParse(input, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "Ulid" => "        if (PalUlid.TryParse(input, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "int" => "        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "long" => "        if (long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "string" => "        if (!string.IsNullOrEmpty(input)) { result = new() { Value = input }; return true; } result = default; return false;",
        _ => "        result = default; return false;"
    };

    private static string TryParseSpanBody(string srcType) => srcType switch
    {
        "Guid" => "        if (Guid.TryParse(s, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "Ulid" => "        if (PalUlid.TryParse(s, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "int" => "        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "long" => "        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { result = new() { Value = v }; return true; } result = default; return false;",
        "string" => "        if (!s.IsEmpty) { result = new() { Value = s.ToString() }; return true; } result = default; return false;",
        _ => "        result = default; return false;"
    };

    private static string JsonReadBody(string srcType, string name) => srcType switch
    {
        "Guid" => $"        return {name}.From(reader.GetGuid());",
        "Ulid" => $"        return {name}.From(PalUlid.Parse(reader.GetString() ?? throw new JsonException(\"Ulid identity JSON value cannot be null.\"), CultureInfo.InvariantCulture));",
        "int" => $"        return {name}.From(reader.GetInt32());",
        "long" => $"        return {name}.From(reader.GetInt64());",
        "string" => $"        return {name}.From(reader.GetString() ?? throw new JsonException(\"String identity JSON value cannot be null.\"));",
        _ => "        throw new JsonException(\"Unsupported identity source type.\");"
    };

    private static string JsonWriteBody(string srcType) => srcType switch
    {
        "Guid" => "        writer.WriteStringValue(value.Value);",
        "Ulid" => "        writer.WriteStringValue(value.Value.ToString());",
        "int" => "        writer.WriteNumberValue(value.Value);",
        "long" => "        writer.WriteNumberValue(value.Value);",
        "string" => "        writer.WriteStringValue(value.Value);",
        _ => "        throw new JsonException(\"Unsupported identity source type.\");"
    };

    private static string NumericOperators(string name, string srcType) => $$"""

    public static {{name}} operator ++({{name}} value) => new() { Value = ({{srcType}})(value.Value + 1) };
    public static {{name}} operator --({{name}} value) => new() { Value = ({{srcType}})(value.Value - 1) };
""";

    private sealed record IdGenInfo(
        string? Namespace,
        string TypeName,
        string[] ContainingDeclarations,
        string[] ContainingNames,
        string SourceType,
        bool IsNumeric,
        string? DiagnosticId = null,
        Location? Location = null)
    {
        // P3 修复（八轮评审）：数组字段默认引用相等破坏增量管线缓存（每次编译新数组实例
        // → 引用不等 → 缓存恒 miss）——逐元素比较实现值等价，镜像 MessageRegistryGenerator
        // 的 LocationInfo value-equatable 范式。Location/DiagnosticId 不参与相等：
        // 诊断分支本身不产出缓存内容。
        public bool Equals(IdGenInfo? other) =>
            other is not null
            && Namespace == other.Namespace
            && TypeName == other.TypeName
            && ContainingDeclarations.SequenceEqual(other.ContainingDeclarations)
            && ContainingNames.SequenceEqual(other.ContainingNames)
            && SourceType == other.SourceType
            && IsNumeric == other.IsNumeric;

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + (Namespace?.GetHashCode() ?? 0);
                hash = hash * 31 + TypeName.GetHashCode();
                foreach (var declaration in ContainingDeclarations) hash = hash * 31 + declaration.GetHashCode();
                foreach (var containingName in ContainingNames) hash = hash * 31 + containingName.GetHashCode();
                hash = hash * 31 + SourceType.GetHashCode();
                hash = hash * 31 + IsNumeric.GetHashCode();
                return hash;
            }
        }
    }
}
