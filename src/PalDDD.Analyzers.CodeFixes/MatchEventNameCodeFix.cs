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

        // 找到 EventName 属性声明中的字符串字面量
        // P3 修复（二十一轮）：诊断定位回退到类型声明（EventName 声明缺失/不可解析）时，
        // 原实现取类型内第一个字符串字面量——可能是 [BoundedContext]/[GenerateMessage]
        // 的参数或无关成员的字面量，fix 会改写无辜字符串；仅在定位点确实落在
        // EventName 属性声明内时才注册 fix
        if (node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is not { Identifier.Text: "EventName" } eventNameDecl)
            return;

        var literal = eventNameDecl.DescendantNodes()
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
        // v25 P3 生成器族（D4）：补 .WithTriviaFrom——三个姊妹 fix（AddBoundedContextPrefix/
        // AddProjectionContextPrefix（v22 D2）/AddVersionSuffix（ITM-221））均保留 trivia，
        // 裸替换丢失字面量前后注释与换行格式
        editor.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(messageName)).WithTriviaFrom(literal));
        return editor.GetChangedDocument();
    }
}
