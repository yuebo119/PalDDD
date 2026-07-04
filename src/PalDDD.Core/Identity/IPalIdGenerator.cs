// ═══════════════════════════════════════════════════════════════
// 🆔 IPalIdGenerator — 框架统一 ID 生成器抽象
// ═══════════════════════════════════════════════════════════════
//
// 💡 设计原则：
//   ｜ 框架内所有 ID 生成统一通过此接口，屏蔽具体实现（当前为 ByteAether.Ulid）。
//   ｜ Ulid 相比 Guid 的优势：时间排序友好、数据库索引友好、URL 安全。
//   ｜ 接口抽象允许未来替换为其他 ID 方案而无需修改调用方。
// ═══════════════════════════════════════════════════════════════

using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Core.Identity;

/// <summary>框架统一 ID 生成器——当前实现为 ByteAether.Ulid，未来可替换。</summary>
public interface IPalIdGenerator
{
    /// <summary>生成新的全局唯一、时间排序 ID。</summary>
    PalUlid NewId();

    /// <summary>从指定时间戳生成新的 ID。</summary>
    PalUlid NewId(DateTimeOffset timestamp);
}
