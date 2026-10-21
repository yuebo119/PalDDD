// ═══════════════════════════════════════════════════════════════
// 🆔 IPalIdGenerator — 框架统一 ID 生成器抽象
// ═══════════════════════════════════════════════════════════════
//
// 💡 设计原则：
//   ｜ 框架内<b>无核心消费方</b>（DomainEvent.EventId 与 IdentityGenerator 的 Guid/Ulid 分支
//   ｜ 直接调用实现）；本接口供应用层按需替换 ID 方案，不承担框架内统一入口职责。
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
