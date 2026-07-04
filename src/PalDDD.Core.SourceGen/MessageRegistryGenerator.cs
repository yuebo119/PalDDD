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
        "Message name '{0}' is used by more than one generated message type",
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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (context, ct) =>
            {
                var type = (INamedTypeSymbol)context.TargetSymbol;
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

    private sealed record MessageInfo(
        string TypeName,
        string Name,
        int SchemaVersion,
        bool HasExplicitName,
        LocationInfo Location);

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
