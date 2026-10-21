using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace PalDDD.Core.SourceGen;

/// <summary>
/// 三个生成器（Identity/Enum/MessageRegistry）共用的符号判定。
/// 2026-10-21 评审 F15（C3）：原 8 行谓词在三个生成器各存一份；修改 Arity 判定时
/// 漏改一处会让三生成器的诊断口径分叉。
/// </summary>
internal static class GeneratorSymbols
{
    /// <summary>沿 ContainingType 链检测泛型包含类型——生成物无法以裸名引用泛型外层内的嵌套类型。</summary>
    internal static bool IsWithinGenericContainingType(INamedTypeSymbol symbol)
    {
        for (var t = symbol.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.Arity > 0)
                return true;
        }

        return false;
    }
}

/// <summary>
/// <see cref="ImmutableDictionary{TKey,TValue}"/> 的内容相等比较器。
/// 2026-10-21 评审（IdentityGenerator:448 族）：ImmutableDictionary 只实现引用相等，
/// 任一候选变化重建字典即令 Combine 对全部候选重跑模板渲染；内容不变时本比较器让
/// 增量管线命中缓存。哈希按 key 排序计算，保证跨实例确定。
/// </summary>
internal sealed class ImmutableStringDictionaryComparer : IEqualityComparer<ImmutableDictionary<string, string>>
{
    internal static readonly ImmutableStringDictionaryComparer Instance = new();

    public bool Equals(ImmutableDictionary<string, string>? x, ImmutableDictionary<string, string>? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null || x.Count != y.Count)
            return false;
        foreach (var pair in x)
        {
            if (!y.TryGetValue(pair.Key, out var value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(ImmutableDictionary<string, string> obj)
    {
        // netstandard2.0 生成器工程无 ArgumentNullException.ThrowIfNull；字典非 null 是
        // IEqualityComparer 契约前提。
        var hash = 17;
        foreach (var key in obj.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(key);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(obj[key]);
        }

        return hash;
    }
}
