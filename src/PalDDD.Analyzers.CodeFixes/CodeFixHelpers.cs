// 共享辅助（精炼拆分 2026-08-26 自 StrategicDddCodeFixProvider.cs 单文件 5 类型——对齐一个诊断一文件的 Roslyn 惯例）

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Immutable;
using System.Composition;

namespace PalDDD.Analyzers;

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
