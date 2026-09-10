namespace PalDDD.Shared;

/// <summary>
/// 稳定名称格式校验（小写字母 / 数字 / '-' / '.'）。
/// <para>
/// 由 <c>PalDDD.Analyzers</c> 与 <c>PalDDD.Core.SourceGen</c> 以链接源码方式共享——
/// 两个包各自编译本文件（内部类型），彼此无程序集依赖。
/// </para>
/// <para>
/// 提取原因：此前两处逐字重复的私有 <c>IsStableName</c> 实现存在漂移风险
/// （审计实证：分析器版含空白守卫，生成器版无——行为等价但实现分叉）。
/// 两侧对同一谓词的约束必须一致，否则同一名称在诊断层与生成层判定不同。
/// </para>
/// </summary>
internal static class StableNameValidation
{
    /// <summary>名称非空、非空白且仅含小写字母、数字、连字符与点。</summary>
    internal static bool IsStable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var ch in value!)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')
                continue;

            return false;
        }

        return true;
    }
}
