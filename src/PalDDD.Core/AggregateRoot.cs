namespace PalDDD.Core;

// ─────────────────────────────────────────────────────────────
// 🏛️ AggregateRoot（聚合根） — DDD 中最核心的战术模式
// ─────────────────────────────────────────────────────────────
//
// 💡 聚合根是什么？
//   ｜ 一个聚合的入口实体。所有对聚合内部实体的访问必须通过聚合根。
//   ｜ 例如：Order 是聚合根，OrderItem 是它内部的实体——你不能直接修改 OrderItem，
//   ｜ 必须通过 Order.AddItem() 来操作。
//
// 💡 为什么 AggregateRoot 这么简单？
//   ｜ Entity<TId> 基类已经提供事件存储和身份管理。
//   ｜ Source Generator 通过继承层次检测聚合根并生成元数据。
//   ｜ 聚合内部的业务规则和不变性由子类自己维护。
//   ｜ 这种"薄基类"设计符合组合优于继承的原则。
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 聚合根基类 —— DDD 聚合设计模式的核心抽象。
/// <para>
/// 聚合根的设计契约：<br/>
/// 1. 拥有全局唯一标识<br/>
/// 2. 是聚合的唯一入口 —— 外部只能通过聚合根访问内部实体<br/>
/// 3. 负责维护聚合内所有的业务不变性（invariants）<br/>
/// 4. Source Generator 通过此类型层次检测聚合
/// </para>
/// <para>✅ AOT 安全：泛型参数在编译期完全实例化，无运行时反射。</para>
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    /// <summary>构造聚合根，指定其唯一标识</summary>
    protected AggregateRoot(TId id) : base(id) { }
}
