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

    // P3 修复（十七轮）：泛型声明（自身带类型参数或嵌套于泛型包含类型）生成坏代码——
    // 生成物中 namespace 级 TypeConverter/JsonConverter 以裸名引用嵌套 ID（泛型外层
    // 无类型参数可用，typeof(Outer.Foo) 编译失败）；自身泛型时生成物裸名声明与用户
    // partial record struct Foo<T> 同名冲突。编译期报 PALID003 引导移出泛型声明。
    private static readonly DiagnosticDescriptor GenericDeclarationNotSupported = new(
        "PALID003",
        "GenerateId does not support generic declarations",
        "Type '{0}' uses [GenerateId] within a generic declaration. Generic identities are not supported; move the target out of the generic type or remove its type parameters.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // P2 修复（二十一轮）：同一类型的多个 partial 声明均挂 [GenerateId]——
    // ForAttributeWithMetadataName 每声明触发一次 transform，两个 candidate 算出
    // 相同 hint，AddSource 同 hint 第二次调用抛 ArgumentException 使整个生成器
    // 崩溃（连带丢失本生成器全部生成物）。重复声明报 PALID004，仅首个声明生成代码。
    private static readonly DiagnosticDescriptor DuplicatePartialDeclaration = new(
        "PALID004",
        "GenerateId attribute declared on multiple partial declarations",
        "Type '{0}' has [GenerateId] applied to multiple partial declarations. Apply the attribute to only one partial declaration.",
        "PalDDD.IdentityGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ITM-074 修复：构造参数为 null（[GenerateId(null)]）时 transform 直接 NRE——
    // NRE 从增量生成器冒泡会毁掉整个编译的全部生成物（与 PALID004 崩溃同害）。
    // 编译期报 PALID005 引导修正，不崩溃。
    private static readonly DiagnosticDescriptor NullSourceType = new(
        "PALID005",
        "GenerateId source type must not be null",
        "Type '{0}' uses [GenerateId] with a null source type. Pass a supported type: System.Guid, ByteAether.Ulid.Ulid, int (Int32), long (Int64), string.",
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
                // ITM-074 修复：构造参数缺失/为 null（[GenerateId(null)]）时
                // 原代码 (INamedTypeSymbol)null! 在下方 ToDisplayString() 处 NRE，
                // 从增量生成器冒泡毁掉整个编译的全部生成物。先判 null 报 PALID005。
                if (attrData.ConstructorArguments.Length == 0
                    || attrData.ConstructorArguments[0].Value is not INamedTypeSymbol sourceType)
                {
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: "",
                        IsNumeric: false,
                        DiagnosticId: "PALID005",
                        Location: context.TargetNode.GetLocation());
                }

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

                // P3 修复（十七轮）：泛型声明（自身带类型参数或嵌套于泛型包含类型）暂不支持
                // （见 GenericDeclarationNotSupported 注释）——编译期报 PALID003，不生成坏代码
                if (structSymbol.Arity > 0 || IsWithinGenericContainingType(structSymbol))
                {
                    return new IdGenInfo(
                        Namespace: null,
                        TypeName: structSymbol.Name,
                        ContainingDeclarations: [],
                        ContainingNames: [],
                        SourceType: sourceType.ToDisplayString(),
                        IsNumeric: false,
                        DiagnosticId: "PALID003",
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
                // P1 修复（十七轮）：Ulid case 此前写 "ByteAether.Ulid" 永不匹配——ToDisplayString()
                // 返回 "ByteAether.Ulid.Ulid"（命名空间 ByteAether.Ulid + 类型名 Ulid），
                // [GenerateId(typeof(Ulid))] 恒报 PALID001（诊断消息声称支持的正是它拒绝的类型）。
                // 编译探针实证；十六轮未发现因测试零 Ulid/long 用例。
                var normalizedSourceType = sourceType.ToDisplayString().Replace("global::", "") switch
                {
                    "System.Guid" => "Guid",
                    "int" => "int",
                    "long" => "long",
                    "string" => "string",
                    "ByteAether.Ulid.Ulid" => "Ulid",
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
                    sourceType.Name is "Int32" or "Int64" && sourceType.ContainingNamespace?.ToDisplayString() == "System",
                    // P2 修复（二十一轮）：携带定位供 PALID004（重复 partial 声明）指示
                    // 重复挂 attribute 的具体声明——不参与相等（位置随编辑漂移）
                    Location: context.TargetNode.GetLocation());
            })
            .WithTrackingName("IdentityGenerator_Candidates")
            .Where(static info => info is not null)!;

        // P2 修复（二十一轮）：双 partial 声明崩溃——同一类型的两个 partial 声明均挂
        // [GenerateId] 时 ForAttributeWithMetadataName 每声明触发一次 transform，两个
        // candidate 计算出相同 hint，AddSource 同 hint 第二次调用抛 ArgumentException
        // 使整个生成器崩溃。方案分支：评审建议闭包 HashSet，但 RegisterSourceOutput
        // 回调在后续增量 pass 会对"值变化而 hint 不变"的条目重入（如 SourceType 从
        // Guid 改 int），跨 pass 残留的 HashSet 会误报 PALID004 并漏生成代码；改用
        // Collect() 后在单次回调内局部去重——去重状态只存在于一次调用内，无跨 pass 污染。
        context.RegisterSourceOutput(candidates.Collect(), static (spc, infos) =>
        {
            var seenHints = new HashSet<string>();
            foreach (var info in infos)
            {
                if (info.DiagnosticId is not null)
                {
                    // 诊断分派（九轮）：PALID001=源类型白名单外；PALID002=声明形式非 partial record struct
                    // P3 修复（十七轮）：PALID003=泛型声明（自身或包含类型）暂不支持
                    // ITM-074 修复：PALID005=构造参数为 null（[GenerateId(null)]）
                    switch (info.DiagnosticId)
                    {
                        case "PALID002":
                            spc.ReportDiagnostic(Diagnostic.Create(
                                NonPartialRecordStructDeclaration,
                                info.Location ?? Location.None,
                                info.TypeName));
                            break;
                        case "PALID003":
                            spc.ReportDiagnostic(Diagnostic.Create(
                                GenericDeclarationNotSupported,
                                info.Location ?? Location.None,
                                info.TypeName));
                            break;
                        case "PALID005":
                            spc.ReportDiagnostic(Diagnostic.Create(
                                NullSourceType,
                                info.Location ?? Location.None,
                                info.TypeName));
                            break;
                        default:
                            // P3 修复（八轮评审）：PALID001——非白名单 IdType 编译期报错，不生成代码
                            spc.ReportDiagnostic(Diagnostic.Create(
                                UnsupportedIdSourceType,
                                info.Location ?? Location.None,
                                info.TypeName,
                                info.SourceType));
                            break;
                    }
                    continue;
                }

                var src = GenerateIdentityCode(info);
                // P3 修复（八轮评审）：全局命名空间（Namespace == null）用 "_" 仅作 hint 名保底，
                // 生成代码本身不再含 namespace 声明
                // ITM-098 修复（验证轮返工）：hint 拼接用 "+" 显式编码嵌套层级——命名空间内的
                // "." 保留原样（"A.B+C" 与 "A+B.C" 不再碰撞），"+" 非 C# 标识符字符，
                // 命名空间/类型名不可能包含，彻底消除同 hint 碰撞（PALID004 误报/AddSource 冲突）。
                // hint 仅作生成物文件名与去重键，变更不影响编译（测试均按 ".g.cs" 后缀匹配，
                // 见 SourceGeneratorDirectTests.GetGeneratedSource）；转换器类名仍用 "_" 拼接
                // （C# 标识符不允许 "+"，下划线边界撞名的 CS0101 属已声明限制）。
                var hint = info.ContainingNames.Length > 0
                    ? $"{info.Namespace ?? "_"}+{string.Join("+", info.ContainingNames)}.{info.TypeName}.g.cs"
                    : $"{info.Namespace ?? "_"}.{info.TypeName}.g.cs";
                if (!seenHints.Add(hint))
                {
                    // P2 修复（二十一轮）：重复 [GenerateId] 声明——仅首个声明生成代码，
                    // 其余报 PALID004 而非让 AddSource 抛 ArgumentException 崩溃
                    spc.ReportDiagnostic(Diagnostic.Create(
                        DuplicatePartialDeclaration,
                        info.Location ?? Location.None,
                        info.TypeName));
                    continue;
                }

                spc.AddSource(hint, src);
            }
        });
    }

    // P3 修复（九轮评审）：predicate 放宽到全部 struct 类声明（普通 struct + record struct），
    // 非 partial record struct 由 transform 报 PALID002——静默跳过让错误延迟到使用点 CS0117
    private static bool IsStructKindDeclaration(SyntaxNode node)
        => node is StructDeclarationSyntax
           || (node is RecordDeclarationSyntax r
               && r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));

    // P3 修复（十七轮）：沿 ContainingType 链检测泛型包含类型——
    // 生成物的 namespace 级 converter 无法以裸名引用泛型外层内的嵌套 ID
    private static bool IsWithinGenericContainingType(INamedTypeSymbol symbol)
    {
        for (var t = symbol.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.Arity > 0)
                return true;
        }

        return false;
    }

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

        // Medium 修复（二十六轮验证轮 W3）：同时 emit 命名空间 using 与别名——模板以裸 Ulid 作
        // srcType（IPalIdentity<Ulid>/public Ulid Value），仅别名时无 global using 的真实消费方
        // 编译失败（7 个 CS0246/CS1503，harness 实测；仓内零真实 Ulid Id 类型故从未暴露）
        var ulidUsing = srcType == "Ulid" ? "\r\nusing ByteAether.Ulid;\r\nusing PalUlid = ByteAether.Ulid.Ulid;" : "";

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
        // 优化（二十五轮 API 扫描 B2）：读路径原为 GetString()（必然堆分配）+ Parse(string)——
        // 非转义字符串（绝大多数）直接 reader.ValueSpan（UTF-8 原始切片）TryParse 零分配；
        // 转义字符串回退 GetString()+Parse(string)。token 守卫保留原 JsonException-for-null
        // 语义（ValueSpan/ValueIsEscaped 仅对 String/PropertyName token 有效，Null token 原靠
        // GetString() 返 null 触发 JsonException，不守卫会退化成 InvalidOperationException）。
        // TryParse(ReadOnlySpan<byte>, IFormatProvider?, out Ulid) 已在 ByteAether.Ulid 1.4.0
        // net10 XML 证实。Read 无显式 TokenType 分支——S.T.J converter 契约保证 reader 位于
        // 本类型的 JSON 值 token 上，坏 JSON 不会进入本方法。
        "Ulid" => $"""
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("Ulid identity JSON value cannot be null.");
                if (reader.ValueIsEscaped)
                    return {name}.From(PalUlid.Parse(reader.GetString()!, CultureInfo.InvariantCulture));
                if (PalUlid.TryParse(reader.ValueSpan, null, out var ulid))
                    return {name}.From(ulid);
                throw new JsonException("Ulid identity JSON value is not a valid Ulid.");
        """,
        "int" => $"        return {name}.From(reader.GetInt32());",
        "long" => $"        return {name}.From(reader.GetInt64());",
        "string" => $"        return {name}.From(reader.GetString() ?? throw new JsonException(\"String identity JSON value cannot be null.\"));",
        _ => "        throw new JsonException(\"Unsupported identity source type.\");"
    };

    private static string JsonWriteBody(string srcType) => srcType switch
    {
        "Guid" => "        writer.WriteStringValue(value.Value);",
        // 优化（二十五轮 API 扫描 B1）：Ulid 写路径原为 value.Value.ToString()——每写一个 Id
        // 堆分配一个 26 字符字符串；Ulid 恒 26 字符（Crockford Base32），stackalloc 栈缓冲
        // TryFormat 后以 span 直接写出，零堆分配。TryFormat(Span<char>, out int,
        // ReadOnlySpan<char>, IFormatProvider) 已在 ByteAether.Ulid 1.4.0 net10 XML 证实；
        // 本生成器 netstandard2.0 仅 emit 文本，模板引用的 API 在用户项目（引用 Ulid 包）
        // 编译时解析——SourceGen 项目无需引用 Ulid。else 为防御回退（理论不可达）。
        "Ulid" => """
                Span<char> buffer = stackalloc char[26];
                if (value.Value.TryFormat(buffer, out int written, default, null))
                    writer.WriteStringValue(buffer[..written]);
                else
                    writer.WriteStringValue(value.Value.ToString()); // 理论不可达（26 字符恒足够）——防御回退
        """,
        "int" => "        writer.WriteNumberValue(value.Value);",
        "long" => "        writer.WriteNumberValue(value.Value);",
        "string" => "        writer.WriteStringValue(value.Value);",
        _ => "        throw new JsonException(\"Unsupported identity source type.\");"
    };

    // ITM-099 修复：++/-- 的加减法改 checked——原 unchecked 下 int.MaxValue 自增静默
    // 回绕为 int.MinValue（数据损坏无感知）；checked 抛 OverflowException 显式化。
    // 生成代码文本变化（+checked 包裹）不影响编译；测试仅断言含 "operator"。
    private static string NumericOperators(string name, string srcType) => $$"""

    public static {{name}} operator ++({{name}} value) => new() { Value = checked(({{srcType}})(value.Value + 1)) };
    public static {{name}} operator --({{name}} value) => new() { Value = checked(({{srcType}})(value.Value - 1)) };
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
        // 的 LocationInfo value-equatable 范式。
        // P3 修复（十轮）：DiagnosticId 纳入相等——诊断也是输出，PALID001/PALID002 两态
        // 翻转而其余字段相等时，排除会使 IDE 增量编译回放过期诊断（Location 仍不参与：
        // 同一节点的位置随编辑漂移，参与相等会造成缓存 miss）。
        public bool Equals(IdGenInfo? other) =>
            other is not null
            && Namespace == other.Namespace
            && TypeName == other.TypeName
            && ContainingDeclarations.SequenceEqual(other.ContainingDeclarations)
            && ContainingNames.SequenceEqual(other.ContainingNames)
            && SourceType == other.SourceType
            && IsNumeric == other.IsNumeric
            && DiagnosticId == other.DiagnosticId;

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
                hash = hash * 31 + (DiagnosticId?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
}
