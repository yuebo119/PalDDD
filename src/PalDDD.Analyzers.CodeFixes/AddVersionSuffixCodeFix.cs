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
        // ITM-095 修复：SchemaVersion 来自外部诊断数据，格式不可信——TryParse 失败
        // 直接跳过本修复，不再让 int.Parse 抛 FormatException 中断修复流程
        if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var schemaVersion)) return;

        // 找到 Name 参数
        if (!CodeFixHelpers.TryGetNamedArgument(attr, "Name", out var nameArg) || nameArg.Expression is not LiteralExpressionSyntax literal)
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

        // ITM-221 修复（三十二轮）：替换已有 .v{N} 后缀而非叠加——
        // 原实现只检查目标版本结尾，x.v1 + v2 生成 x.v1.v2（字面通过校验但语义错误）。
        // v41 P3：循环剥离所有 .vN 后缀段——Regex.Replace 带 $ 锚单次调用只剥末尾一段，
        // 双后缀输入（x.v1.v2）残留 x.v1.v{N}（产物仍含两个后缀段，字面通过结尾校验
        // 但语义错误）；循环至无匹配后再拼唯一新后缀
        var oldValue = literal.Token.ValueText;
        while (System.Text.RegularExpressions.Regex.IsMatch(oldValue, @"\.v\d+$"))
            oldValue = System.Text.RegularExpressions.Regex.Replace(oldValue, @"\.v\d+$", "");
        var newValue = oldValue + $".v{schemaVersion}";

        editor.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(newValue)).WithTriviaFrom(literal));
        return editor.GetChangedDocument();
    }
}
