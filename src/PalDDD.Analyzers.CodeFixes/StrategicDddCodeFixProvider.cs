// ─────────────────────────────────────────────────────────────
// 🔧 StrategicDddCodeFixProvider — 战略 DDD 分析器快速修复
// ─────────────────────────────────────────────────────────────
// 覆盖 4 条命名规则：
//   PDDD008 — 消息名前缀自动补全（bounded context.）
//   PDDD010 — 消息名版本后缀自动补全（.v{schemaVersion}）
//   PDDD013 — 投影名前缀自动补全
//   PDDD015 — EventName 自动匹配生成的消息名
// ─────────────────────────────────────────────────────────────

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;

namespace PalDDD.Analyzers;

// ─────────────────────────────────────────────────────────────
// 共享辅助
// ─────────────────────────────────────────────────────────────

internal static class CodeFixHelpers
{
    public static bool TryGetNamedArgument(
        AttributeSyntax attr, string name, out AttributeArgumentSyntax argument)
    {
        if (attr.ArgumentList is null) { argument = null!; return false; }
        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals?.Name.Identifier.Text == name)
            {
                argument = arg;
                return true;
            }
        }
        argument = null!;
        return false;
    }
}

// ═══════════════════════════════════════════════════════════════
// PDDD010：消息名版本后缀自动补全
// ═══════════════════════════════════════════════════════════════

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddVersionSuffixCodeFix))]
[Shared]
public sealed class AddVersionSuffixCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        [StrategicDddAnalyzer.MessageNameVersionMismatchId];

    public override sealed FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override sealed async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;
        var node = root.FindNode(diagnosticSpan);

        // 诊断位置可能是整个 AttributeSyntax 或 AttributeArgumentSyntax
        var attr = node as AttributeSyntax ?? node.FirstAncestorOrSelf<AttributeSyntax>();
        if (attr is null) return;

        // 从诊断属性中获取 SchemaVersion
        if (!diagnostic.Properties.TryGetValue("SchemaVersion", out var versionText)) return;
        var schemaVersion = int.Parse(versionText, CultureInfo.InvariantCulture);

        // 找到 Name 参数
        if (!TryGetNamedArgument(attr, "Name", out var nameArg) || nameArg.Expression is not LiteralExpressionSyntax literal)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                $"添加版本后缀 .v{schemaVersion}",
                ct => AddVersionSuffixAsync(context.Document, literal, schemaVersion, ct),
                equivalenceKey: "AddVersionSuffix"),
            diagnostic);
    }

    private static async Task<Document> AddVersionSuffixAsync(
        Document document,
        LiteralExpressionSyntax literal,
        int schemaVersion,
        CancellationToken ct)
    {
        var editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);

        var oldValue = literal.Token.ValueText;
        var newValue = oldValue.EndsWith($".v{schemaVersion}", StringComparison.Ordinal)
            ? oldValue
            : oldValue + $".v{schemaVersion}";

        editor.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(newValue)));
        return editor.GetChangedDocument();
    }

    private static bool TryGetNamedArgument(
        AttributeSyntax attr, string name, out AttributeArgumentSyntax argument)
    {
        if (attr.ArgumentList is null) { argument = null!; return false; }
        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals?.Name.Identifier.Text == name)
            {
                argument = arg;
                return true;
            }
        }
        argument = null!;
        return false;
    }
}

// ═══════════════════════════════════════════════════════════════
// PDDD008：消息名前缀自动补全（bounded context.）
// ═══════════════════════════════════════════════════════════════

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddBoundedContextPrefixCodeFix))]
[Shared]
public sealed class AddBoundedContextPrefixCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        [StrategicDddAnalyzer.MessageNameContextMismatchId];

    public override sealed FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override sealed async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;
        var node = root.FindNode(diagnosticSpan);

        // 诊断位置可能是整个 AttributeSyntax
        var attr = node as AttributeSyntax ?? node.FirstAncestorOrSelf<AttributeSyntax>();
        if (attr is null) return;

        // 从诊断属性获取 bounded context
        if (!diagnostic.Properties.TryGetValue("BoundedContext", out var boundedContext)) return;

        // 找到 Name 参数
        if (!CodeFixHelpers.TryGetNamedArgument(attr, "Name", out var nameArg)) return;
        if (nameArg.Expression is not LiteralExpressionSyntax literal) return;

        // boundedContext 来自诊断属性，非 null
        var contextBc = boundedContext!;
        context.RegisterCodeFix(
            CodeAction.Create(
                $"添加前缀 '{contextBc}.'",
                ct => AddPrefixAsync(context.Document, literal, contextBc, ct),
                equivalenceKey: "AddBoundedContextPrefix"),
            diagnostic);
    }

    private static async Task<Document> AddPrefixAsync(
        Document document,
        LiteralExpressionSyntax literal,
        string prefix,
        CancellationToken ct)
    {
        var editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);

        var oldValue = literal.Token.ValueText;
        if (oldValue.StartsWith(prefix + ".", StringComparison.Ordinal)) return document;

        var newValue = prefix + "." + oldValue;
        editor.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(newValue)));
        return editor.GetChangedDocument();
    }
}

// ═══════════════════════════════════════════════════════════════
// PDDD013：投影名前缀自动补全
// ═══════════════════════════════════════════════════════════════

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddProjectionContextPrefixCodeFix))]
[Shared]
public sealed class AddProjectionContextPrefixCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        [StrategicDddAnalyzer.ProjectionNameContextMismatchId];

    public override sealed FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override sealed async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;
        var node = root.FindNode(diagnosticSpan);

        // 投影名可能在属性声明或属性语法中
        var typeDecl = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (typeDecl is null) return;

        var boundedContextAttr = typeDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString().Contains("BoundedContext"));
        if (boundedContextAttr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax ctxLiteral)
            return;

        var boundedContext = ctxLiteral.Token.ValueText;

        // 找到 ProjectionName 属性定义
        var projectionNameLiteral = FindProjectionNameLiteral(typeDecl);
        if (projectionNameLiteral is null) return;

        var capturedContext = boundedContext;
        context.RegisterCodeFix(
            CodeAction.Create(
                $"添加前缀 '{capturedContext}.'",
                ct => FixProjectionNameAsync(context.Document, projectionNameLiteral, capturedContext, ct),
                equivalenceKey: "AddProjectionContextPrefix"),
            diagnostic);
    }

    private static LiteralExpressionSyntax? FindProjectionNameLiteral(TypeDeclarationSyntax typeDecl)
    {
        foreach (var member in typeDecl.Members)
        {
            if (member is not PropertyDeclarationSyntax prop) continue;
            if (prop.Identifier.Text != "ProjectionName") continue;

            if (prop.ExpressionBody?.Expression is LiteralExpressionSyntax exprLiteral)
                return exprLiteral;
            if (prop.Initializer?.Value is LiteralExpressionSyntax initLiteral)
                return initLiteral;
        }
        return null;
    }

    private static async Task<Document> FixProjectionNameAsync(
        Document document,
        LiteralExpressionSyntax literal,
        string prefix,
        CancellationToken ct)
    {
        var editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);
        var oldValue = literal.Token.ValueText;
        if (oldValue.StartsWith(prefix + ".", StringComparison.Ordinal)) return document;

        editor.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(prefix + "." + oldValue)));
        return editor.GetChangedDocument();
    }
}

// ═══════════════════════════════════════════════════════════════
// PDDD015：EventName 自动匹配生成的消息名
// ═══════════════════════════════════════════════════════════════

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MatchEventNameCodeFix))]
[Shared]
public sealed class MatchEventNameCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        [StrategicDddAnalyzer.DomainEventNameMismatchId];

    public override sealed FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override sealed async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue("ExpectedMessageName", out var messageName)) return;

        var diagnosticSpan = diagnostic.Location.SourceSpan;
        var node = root.FindNode(diagnosticSpan);

        // 找到 EventName 属性中的字符串字面量
        var literal = node.DescendantNodesAndSelf()
            .OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));
        if (literal is null) return;

        // messageName 来自诊断属性，已在上方 TryGetValue 保证非 null
        var expectedName = messageName!;
        context.RegisterCodeFix(
            CodeAction.Create(
                $"匹配消息名 '{expectedName}'",
                ct => FixEventNameAsync(context.Document, literal, expectedName, ct),
                equivalenceKey: "MatchEventName"),
            diagnostic);
    }

    private static async Task<Document> FixEventNameAsync(
        Document document,
        LiteralExpressionSyntax literal,
        string messageName,
        CancellationToken ct)
    {
        var editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);
        editor.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(messageName)));
        return editor.GetChangedDocument();
    }

    private static string? GetNamedArgumentValue(AttributeSyntax attr, string name)
    {
        if (attr.ArgumentList is null) return null;
        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals?.Name.Identifier.Text == name
                && arg.Expression is LiteralExpressionSyntax lit)
                return lit.Token.ValueText;
        }
        return null;
    }
}
