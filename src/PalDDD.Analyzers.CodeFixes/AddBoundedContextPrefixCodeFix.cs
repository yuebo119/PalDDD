using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Immutable;
using System.Composition;

namespace PalDDD.Analyzers;

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
