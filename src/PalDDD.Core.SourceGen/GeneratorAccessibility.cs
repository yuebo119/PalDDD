using Microsoft.CodeAnalysis;

namespace PalDDD.Core.SourceGen;

// ─────────────────────────────────────────────────────────────
// 🔒 生成物可见性判定 — 三生成器（Enum/Identity/MessageRegistry）共享
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 生成物可访问性链检查（v34 P2）：生成物（namespace 级 converter/catalog/[ModuleInitializer]）
/// 引用目标类型的<b>完整 ContainingType 链</b>——链上任一层 private/protected 即不可见
/// （CS0122 落在 auto-generated 文件）。仅查目标自身 DeclaredAccessibility 会漏中间层
/// （如 public Outer → private Mid → internal Inner，Inner 自身 internal 通过但 Mid 阻断）。
/// </summary>
internal static class GeneratorAccessibility
{
    /// <summary>
    /// 返回阻断生成物可见性的那层类型的可访问性（沿 ContainingType 链由内向外找第一个
    /// 非 Public/Internal 的声明层）；null 表示整链可见。
    /// </summary>
    internal static Accessibility? GetBlockingAccessibility(INamedTypeSymbol type)
    {
        for (var current = (INamedTypeSymbol?)type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return current.DeclaredAccessibility;
        }

        return null;
    }
}
