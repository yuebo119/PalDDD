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

    // EPSL 地毯审计 2026-10-16 A 批 #18：零分配前缀判定——原 StartsWith(prefix + ".")
    // 每次执行拼接一个中间串；改为长度门控 + StartsWith(prefix) + 分隔符位判定，与
    // StrategicDddAnalyzer.SymbolHelpers.BelongsToBoundedContext 的零分配 idiom 同构
    //（R10 D-F5 同族）。交互路径（用户点击才跑）非每符号回调，收益小但形态对齐。
    public static bool StartsWithPrefix(string value, string prefix)
        => value.Length > prefix.Length
           && value.StartsWith(prefix, StringComparison.Ordinal)
           && value[prefix.Length] == '.';
}
