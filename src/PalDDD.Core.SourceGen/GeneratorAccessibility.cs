using Microsoft.CodeAnalysis;

namespace PalDDD.Core.SourceGen;

// ─────────────────────────────────────────────────────────────
// 🔒 生成物可见性判定 — 三生成器（Enum/Identity/MessageRegistry）共享
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 生成物可访问性链检查（v34 P2）：生成物（namespace 级 converter/catalog/[ModuleInitializer]）
/// 引用目标类型的<b>完整 ContainingType 链</b>——链上任一层不可见即阻断
/// （CS0122 落在 auto-generated 文件）。仅查目标自身 DeclaredAccessibility 会漏中间层
/// （如 public Outer → private Mid → internal Inner，Inner 自身 internal 通过但 Mid 阻断）。
/// </summary>
/// <remarks>
/// v35 P3 裁决：原实现把 ProtectedOrInternal 一刀切为阻断，过严——protected internal
/// 的 internal 腿在同一程序集内可见，而生成物 emitted 到用户程序集（与目标类型同
/// 程序集），internal 腿成立 ⇒ 不阻断。protected 半边（跨程序集派生类可见）与生成物
/// 无关：生成物不是目标类型的派生类，不依赖该腿。仍拦截：Protected（仅派生类可见，
/// 生成物不可见）、Private、ProtectedAndInternal（private protected——internal 腿
/// 不存在，protected 腿生成物同样不可见）。
/// </remarks>
internal static class GeneratorAccessibility
{
    /// <summary>
    /// 返回阻断生成物可见性的那层类型的可访问性（沿 ContainingType 链由内向外找第一个
    /// 生成物不可见的声明层）；null 表示整链可见。
    /// </summary>
    internal static Accessibility? GetBlockingAccessibility(INamedTypeSymbol type)
    {
        for (var current = (INamedTypeSymbol?)type; current is not null; current = current.ContainingType)
        {
            // v35 P3：放行 Public/Internal/ProtectedOrInternal——后者在同程序集 internal 腿成立
            if (current.DeclaredAccessibility is not (Accessibility.Public
                    or Accessibility.Internal
                    or Accessibility.ProtectedOrInternal))
                return current.DeclaredAccessibility;
        }

        return null;
    }

    /// <summary>
    /// v35 P3（DA1）：Accessibility 枚举 → C# 修饰符文本——诊断消息 '{1}' 显示阻断层
    /// 修饰符用（枚举 ToString 的 "ProtectedAndInternal" 非合法修饰符写法，映射为源码
    /// 等价文本）。原为 EnumGenerator 私有副本（PALENUM007 专用），v35 P3 链阻断消息
    /// 勘正后三生成器（PALENUM007/PALID006/PALMSG007）统一消费，提取至此共享。
    /// </summary>
    internal static string AccessibilityToModifierText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        // v36 P3 勘误（N2）：计划中的 Accessibility.File => "file" 映射经编译证伪未加入——
        // 本项目锁定的 Microsoft.CodeAnalysis.CSharp 5.9.0（Directory.Packages.props）的
        // Accessibility 枚举无 File 成员（编译报 CS0117；本机 nuget 缓存 5.10 预览版同样无），
        // file 类型在该版本不产生 File 可访问性值，"fallback ToString 输出 "File"" 的场景
        // 不存在。未来 CodeAnalysis 引入 File 成员时再补 "file" 映射
        _ => accessibility.ToString(),
    };
}
