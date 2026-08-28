using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PalDDD.Core.SourceGen;


// ─────────────────────────────────────────────────────────────
// 源码生成器 — 消息注册
// ─────────────────────────────────────────────────────────────

[Generator(LanguageNames.CSharp)]
public sealed class MessageRegistryGenerator : IIncrementalGenerator
{
    private const string AttributeName = "PalDDD.Core.GenerateMessageAttribute";
    private static readonly DiagnosticDescriptor StableNameRequired = new(
        "PALMSG001",
        "Generated messages require an explicit stable name",
        "Message type '{0}' must set GenerateMessageAttribute.Name to a stable wire name",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSchemaVersion = new(
        "PALMSG002",
        "Generated messages require a positive schema version",
        "Message type '{0}' must use SchemaVersion greater than or equal to 1",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateMessageName = new(
        "PALMSG003",
        "Generated message names must be unique",
        "Message name '{0}' is registered more than once (duplicate [GenerateMessage] declaration on the same or multiple partial declarations)",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMessageName = new(
        "PALMSG004",
        "Generated message names must be stable wire names",
        "Message name '{0}' must use lowercase letters, digits, '-' or '.'",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MessageNameVersionMismatch = new(
        "PALMSG005",
        "Generated message names must include the schema version suffix",
        "Message name '{0}' must end with '.v{1}' to match SchemaVersion {1}",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v25 P3 生成器族：泛型消息类型（partial class Foo<T> 挂 [GenerateMessage]）此前无
    // 编译期拦截——emit typeof(global::Ns.Foo<T>) 生成不可编译代码（CS0246 落在
    // auto-generated 文件）。镜像姊妹拦截（EnumGenerator PALENUM004 / IdentityGenerator
    // PALID003）编译期报 PALMSG006，不生成坏代码。
    private static readonly DiagnosticDescriptor GenericMessageNotSupported = new(
        "PALMSG006",
        "Generated messages do not support generic declarations",
        "Message type '{0}' is marked with [GenerateMessage] within a generic declaration. Generic messages are not supported; move the target out of the generic type or remove its type parameters.",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // v33 P3：private/protected nested 类型挂 [GenerateMessage] 时，生成物
    // PalMessageCatalog 的 typeof 引用该类型——可访问性低于 internal 的嵌套类型对
    // 生成物不可见（CS0122 落在 auto-generated 文件，排障困难）。编译期报 PALMSG007
    // 不生成坏代码（顶层类型恒 public/internal 不受影响；镜像 PALMSG006 姊妹拦截）
    private static readonly DiagnosticDescriptor NonAccessibleMessageNotSupported = new(
        "PALMSG007",
        "Generated messages do not support inaccessible declarations",
        "Message type '{0}' is marked with [GenerateMessage] but is not at least internal (private or protected nested types are invisible to the generated catalog). Raise the declaration to internal or public.",
        "PalDDD.MessageContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (context, ct) =>
            {
                var type = (INamedTypeSymbol)context.TargetSymbol;

                // v25 P3 生成器族：泛型声明（自身带类型参数或嵌套于泛型包含类型）拦截——
                // 提前返回携带 DiagnosticId，RegisterSourceOutput 报 PALMSG006 并剔除出
                // 生成物（镜像 EnumGenerator PALENUM004 的 transform 形态）
                if (type.Arity > 0 || IsWithinGenericContainingType(type))
                {
                    return new MessageInfo(
                        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        type.Name,
                        1,
                        HasExplicitName: false,
                        LocationInfo.From(context.TargetNode.GetLocation()),
                        DiagnosticId: "PALMSG006");
                }

                // v33 P3：可访问性拦截——可访问性低于 internal（private/protected 等）的
                // nested 类型对生成物 PalMessageCatalog 的 typeof 引用不可见。编译期报
                // PALMSG007 不生成坏代码（镜像 EnumGenerator PALENUM007 / IdentityGenerator
                // PALID006）
                if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                {
                    return new MessageInfo(
                        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        type.Name,
                        1,
                        HasExplicitName: false,
                        LocationInfo.From(context.TargetNode.GetLocation()),
                        DiagnosticId: "PALMSG007");
                }

                var attr = context.Attributes[0];
                string? name = null;
                var hasExplicitName = false;
                var schemaVersion = 1;

                foreach (var arg in attr.NamedArguments)
                {
                    if (arg is { Key: "Name", Value.Value: string n })
                    {
                        name = n;
                        hasExplicitName = true;
                    }
                    else if (arg is { Key: "SchemaVersion", Value.Value: int v })
                        schemaVersion = v;
                }

                // 提取诊断 Location：优先指向 [GenerateMessage(...)] 特性，否则回退到类型声明。
                // 用 LocationInfo（值类型）保存以兼容增量生成器缓存比较。
                var syntax = attr.ApplicationSyntaxReference?.GetSyntax(ct);
                var location = syntax?.GetLocation() ?? context.TargetNode.GetLocation();
                var locationInfo = LocationInfo.From(location);

                return new MessageInfo(
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    name ?? type.Name,
                    schemaVersion,
                    hasExplicitName,
                    locationInfo);
            })
            .WithTrackingName("MessageRegistryGenerator_Candidates");

        context.RegisterSourceOutput(candidates.Collect(), static (spc, messages) =>
        {
            if (messages.IsDefaultOrEmpty)
                return;

            var validMessages = ImmutableArray.CreateBuilder<MessageInfo>(messages.Length);
            foreach (var message in messages)
            {
                // v25 P3 生成器族：泛型声明报 PALMSG006（定位到类型声明）并剔除出生成物
                // v33 P3：按 DiagnosticId 分派——原实现假定 DiagnosticId 非 null 即
                // PALMSG006；新增 PALMSG007 后改 switch 分派（镜像 EnumGenerator/
                // IdentityGenerator 的 descriptor switch 形态）
                if (message.DiagnosticId is not null)
                {
                    DiagnosticDescriptor declarationDiagnostic = message.DiagnosticId switch
                    {
                        "PALMSG007" => NonAccessibleMessageNotSupported,
                        _ => GenericMessageNotSupported,
                    };
                    spc.ReportDiagnostic(Diagnostic.Create(declarationDiagnostic, message.Location.ToLocation(), message.TypeName));
                    continue;
                }

                var hasMessageErrors = false;
                var location = message.Location.ToLocation();
                if (!message.HasExplicitName || string.IsNullOrWhiteSpace(message.Name))
                {
                    hasMessageErrors = true;
                    spc.ReportDiagnostic(Diagnostic.Create(StableNameRequired, location, message.TypeName));
                }
                else if (!IsStableName(message.Name))
                {
                    hasMessageErrors = true;
                    spc.ReportDiagnostic(Diagnostic.Create(InvalidMessageName, location, message.Name));
                }
                else if (message.SchemaVersion >= 1 && !HasVersionSuffix(message.Name, message.SchemaVersion))
                {
                    hasMessageErrors = true;
                    spc.ReportDiagnostic(Diagnostic.Create(
                        MessageNameVersionMismatch,
                        location,
                        message.Name,
                        message.SchemaVersion));
                }

                if (message.SchemaVersion < 1)
                {
                    hasMessageErrors = true;
                    spc.ReportDiagnostic(Diagnostic.Create(InvalidSchemaVersion, location, message.TypeName));
                }

                if (!hasMessageErrors)
                    validMessages.Add(message);
            }

            var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in validMessages.GroupBy(static message => message.Name))
            {
                if (group.Count() > 1)
                {
                    duplicateNames.Add(group.Key);
                    // 重复名诊断同时报告在每个冲突点上，便于 IDE 高亮所有重复项。
                    foreach (var duplicate in group)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            DuplicateMessageName,
                            duplicate.Location.ToLocation(),
                            group.Key));
                    }
                }
            }

            var generatedMessages = validMessages
                .Where(message => !duplicateNames.Contains(message.Name))
                .ToImmutableArray();

            if (!generatedMessages.IsDefaultOrEmpty)
                spc.AddSource("PalDDD.Generated.MessageCatalog.g.cs", Generate(generatedMessages));
        });
    }

    private static string Generate(ImmutableArray<MessageInfo> messages)
    {
        var registrations = new StringBuilder();

        foreach (var message in messages)
        {
            registrations.Append("            builder.Add(new MessageDescriptor(\"")
                .Append(Escape(message.Name))
                .Append("\", typeof(")
                .Append(message.TypeName)
                .Append("), jsonContext.GetTypeInfo(typeof(")
                .Append(message.TypeName)
                .Append(")) ?? throw new InvalidOperationException(\"Missing JsonTypeInfo for ")
                .Append(Escape(message.TypeName))
                .Append("\"), ")
                .Append(message.SchemaVersion)
                .AppendLine("));");
        }

        return $$"""
// <auto-generated/>
#nullable enable
using System;
using System.Text.Json.Serialization;
using PalDDD.Serialization;

namespace PalDDD.Generated;

public static class PalMessageCatalog
{
    public static void AddGeneratedMessages(MessageCatalogBuilder builder, JsonSerializerContext jsonContext)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(jsonContext);

{{registrations}}    }
}
""";
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static bool IsStableName(string value)
    {
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')
                continue;

            return false;
        }

        return true;
    }

    private static bool HasVersionSuffix(string name, int schemaVersion)
        => name.EndsWith(".v" + schemaVersion, StringComparison.Ordinal);

    // v25 P3 生成器族：沿 ContainingType 链检测泛型包含类型——嵌套于泛型外层内的消息
    // 无法在生成物中以裸名 typeof 引用（镜像 EnumGenerator/IdentityGenerator 同名方法）
    private static bool IsWithinGenericContainingType(INamedTypeSymbol symbol)
    {
        for (var t = symbol.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.Arity > 0)
                return true;
        }

        return false;
    }

    private sealed record MessageInfo(
        string TypeName,
        string Name,
        int SchemaVersion,
        bool HasExplicitName,
        LocationInfo Location,
        string? DiagnosticId = null)
    {
        // ITM-220 修复（三十二轮）：Location 不参与相等比较——record 默认全字段相等使
        // 位置漂移（如上方插入空行）令增量管线缓存 miss；与 EnumGenerator.EnumGenInfo /
        // IdentityGenerator.IdGenInfo 手写 Equals 排除 Location 的缓存策略对齐。
        // Location 仅用于诊断输出，不影响生成物。
        // v25 P3 生成器族：DiagnosticId 纳入相等——ReportDiagnostic 也是管线输出，
        // PALMSG006 翻转而其余字段相等时缓存命中会残留 IDE 僵尸诊断（镜像
        // EnumGenInfo 十八轮修法）。
        public bool Equals(MessageInfo? other) =>
            other is not null
            && TypeName == other.TypeName
            && Name == other.Name
            && SchemaVersion == other.SchemaVersion
            && HasExplicitName == other.HasExplicitName
            && DiagnosticId == other.DiagnosticId;

        public override int GetHashCode()
        {
            // netstandard2.0 无 System.HashCode——与 EnumGenerator.EnumGenInfo /
            // IdentityGenerator.IdGenInfo 的 17/31 手写哈希策略对齐
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + TypeName.GetHashCode();
                hash = hash * 31 + Name.GetHashCode();
                hash = hash * 31 + SchemaVersion;
                hash = hash * 31 + HasExplicitName.GetHashCode();
                hash = hash * 31 + (DiagnosticId?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// 增量生成器友好的 Location 表示：value-equatable，可参与缓存键比较。
    /// 使用时通过 <see cref="ToLocation"/> 重建 <see cref="Microsoft.CodeAnalysis.Location"/>。
    /// </summary>
    private sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
    {
        public static LocationInfo From(Location location)
        {
            var lineSpan = location.GetLineSpan();
            return new LocationInfo(
                lineSpan.Path ?? string.Empty,
                location.SourceSpan,
                lineSpan.Span);
        }

        public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);
    }
}
