// 共享辅助（精炼拆分 2026-08-26 自 StrategicDddCodeFixProvider.cs 单文件 5 类型——对齐一个诊断一文件的 Roslyn 惯例）

using Microsoft.CodeAnalysis.CSharp.Syntax; // v13 勘正：本 helper 仅用 AttributeSyntax 族——其余 7 个 using 为单文件拆分时继承残留

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
