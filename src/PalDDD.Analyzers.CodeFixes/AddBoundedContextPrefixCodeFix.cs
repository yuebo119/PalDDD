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

        // v66 P3 边界声明（错前缀叠加）：oldValue 首段已是其它 BC 前缀（类型跨上下文迁移
        // 后 Name 未改，如 [BoundedContext("billing")] + Name="orders.submit.v1"）时，本 fix
        // 叠加产出 "billing.orders.submit.v1" 双前缀（旧前缀残留）。不实现"替换首段"的理由：
        // ① 判定"首段是 BC 名"需要解决方案内 BC 名全集——纯语法扫描有 using alias/跨项目
        // 遗漏两类误判（>30 行仍不正确），语义扫描（Compilation.GetSymbolsWithName 级）成本
        // 不适配 fix 的交互时延；② 误判代价不对称：叠加双前缀是显性错误（PDDD008 复检仍
        // 报错，肉眼可见），而启发式误把名称主体当 BC 前缀替换会静默丢失名称主体且可能
        // 通过全部校验产出静默错误名。跨上下文迁移的正确路径是手动改写 Name 值。
        var newValue = prefix + "." + oldValue;
        editor.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(newValue)).WithTriviaFrom(literal)); // v22 D2：trivia 对齐 AddVersionSuffix ITM-221
        return editor.GetChangedDocument();
    }
}
