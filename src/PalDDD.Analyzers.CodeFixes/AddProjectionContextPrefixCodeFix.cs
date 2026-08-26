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

        // P3 修复（二十一轮）：attribute 识别改符号级——原 a.Name.ToString().Contains(
        // "BoundedContext") 文本匹配对 using 别名（[BC]）漏识别（fix 不注册）、对含
        // 同名后缀的其他 attribute 误识别；改 GetSymbolInfo 解析 attribute 构造器符号
        // 后按 ContainingType 的命名空间 + MetadataName 匹配（镜像 analyzer 的
        // MetadataNameEquals 语义）
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel is null) return;

        AttributeSyntax? boundedContextAttr = null;
        foreach (var attribute in typeDecl.AttributeLists.SelectMany(al => al.Attributes))
        {
            if (semanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol ctor)
                continue;
            if (ctor.ContainingType is { MetadataName: "BoundedContextAttribute" } attrType
                && attrType.ContainingNamespace?.ToDisplayString() == "PalDDD.Core")
            {
                boundedContextAttr = attribute;
                break;
            }
        }
        if (boundedContextAttr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax ctxLiteral)
            return;

        var boundedContext = ctxLiteral.Token.ValueText;

        // 找到 ProjectionName 属性定义
        // v13 基类链对齐 analyzer：ProjectionName 声明在投影基类时诊断照报（analyzer 沿
        // BaseType 链查），fix 此前只扫本类型导致不注册——语义模型沿链取各层的字面量声明
        var chainModel = context.Document.GetSemanticModelAsync(context.CancellationToken).GetAwaiter().GetResult();
        var typeSymbol = chainModel.GetDeclaredSymbol(typeDecl, context.CancellationToken);
        var projectionNameLiteral = typeSymbol is null ? FindProjectionNameLiteral(typeDecl) : FindProjectionNameLiteralAlongChain(typeSymbol, context.CancellationToken);
        if (projectionNameLiteral is null) return;

        var capturedContext = boundedContext;
        context.RegisterCodeFix(
            CodeAction.Create(
                $"添加前缀 '{capturedContext}.'",
                ct => FixProjectionNameAsync(context.Document, projectionNameLiteral, capturedContext, ct),
                equivalenceKey: "AddProjectionContextPrefix"),
            diagnostic);
    }

    /// <summary>沿语义基类链查找 ProjectionName 字面量声明（v13——对齐 analyzer 的链式查找）。</summary>
    private static LiteralExpressionSyntax? FindProjectionNameLiteralAlongChain(
        Microsoft.CodeAnalysis.INamedTypeSymbol type, CancellationToken ct)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var syntaxRef in current.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax(ct) is not TypeDeclarationSyntax typeDecl) continue;
                var found = FindProjectionNameLiteral(typeDecl);
                if (found is not null) return found;
            }
        }
        return null;
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
            // P3 修复（二十一轮）：镜像 analyzer TryGetProjectionName 的四形式——getter
            // 表达式体 / getter 语句体 return 字面量此前查不到（诊断照报但 fix 不注册，
            // 用户无法快速修复）；补 accessor 遍历后四种声明形式均可自动补前缀
            foreach (var accessor in prop.AccessorList?.Accessors ?? [])
            {
                if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                    continue;

                if (accessor.ExpressionBody?.Expression is LiteralExpressionSyntax getterLiteral)
                    return getterLiteral;
                if (accessor.Body is null)
                    continue;

                foreach (var statement in accessor.Body.Statements)
                {
                    if (statement is ReturnStatementSyntax { Expression: LiteralExpressionSyntax returnLiteral })
                        return returnLiteral;
                }
            }
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
