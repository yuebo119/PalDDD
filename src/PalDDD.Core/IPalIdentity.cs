// ─────────────────────────────────────────────────────────────
// 🆔 IPalIdentity<T> — 强类型 ID 契约（IdentityGenerator 实现）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Core;

// ─────────────────────────────────────────────────────────────
// 强类型 ID 标记接口 — 源码生成器类型检测契约
// 💡 保留理由：IdentityGenerator 源码生成器契约 + EF Core/JSON 集成点。
//    详见 docs/decisions/004-core-type-retention.md
// ─────────────────────────────────────────────────────────────

/// <summary>强类型ID标记接口。为源码生成器提供类型检测契约。</summary>
public interface IPalIdentity<TKey> where TKey : notnull, IEquatable<TKey>
{
    TKey Value { get; }
}
