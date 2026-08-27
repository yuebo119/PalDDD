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

        // v28 P3：字面量选取镜像 analyzer 侧 StrategicDddAnalyzer.SymbolHelpers 的
        // TryGetLiteralFromTypeMembers 优先级（表达式体→初始化器→getter 表达式体→getter 体
        // 首个 return 字面量）——原 DescendantNodes().FirstOrDefault(StringLiteral) 与 analyzer
        // 定位点分歧：attribute 参数（[Obsolete("...")]）、getter 体内先于 return 的辅助字面量
        //（日志前缀等）会被首选命中，fix 改写无辜字符串而真正的 EventName 字面量未动。
        // analyzer 侧算法为 private（无 InternalsVisibleTo），此处为等价复刻（语法层）
        var literal = TryGetEventNameLiteral(eventNameDecl);
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

    /// <summary>
    /// v28 P3：EventName 属性声明的定位式字面量选取——按 analyzer 侧
    /// TryGetLiteralFromTypeMembers 同款优先级（表达式体→初始化器→getter 表达式体→
    /// getter 体首个 return 字面量）。仅接受字符串字面量（analyzer 侧经属性类型符号
    /// 过滤 string，此处以 StringLiteralExpression kind 等价判定）；无匹配形式返回 null
    /// （fix 不注册，与 analyzer"找到声明但非字面量"时报 declaration 定位的语义衔接）。
    /// </summary>
    private static LiteralExpressionSyntax? TryGetEventNameLiteral(PropertyDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody?.Expression is LiteralExpressionSyntax { } expressionLiteral
            && expressionLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            return expressionLiteral;

        if (declaration.Initializer?.Value is LiteralExpressionSyntax { } initializerLiteral
            && initializerLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            return initializerLiteral;

        foreach (var accessor in declaration.AccessorList?.Accessors ?? [])
        {
            if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                continue;

            if (accessor.ExpressionBody?.Expression is LiteralExpressionSyntax { } getterLiteral
                && getterLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                return getterLiteral;

            if (accessor.Body is null)
                continue;

            foreach (var statement in accessor.Body.Statements)
            {
                if (statement is ReturnStatementSyntax { Expression: LiteralExpressionSyntax { } returnLiteral }
                    && returnLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                    return returnLiteral;
            }
        }

        return null;
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
