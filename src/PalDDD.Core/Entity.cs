// ═══════════════════════════════════════════════════════════════
// 👑 Entity（实体） — DDD 核心抽象
// ═══════════════════════════════════════════════════════════════
//
// 💡 设计原则：
//   ｜ 实体由"身份"（Identity）定义 —— 两个实体即使所有属性相同，只要 Id 不同，就是不同的实体。
//   ｜ 领域事件用单链表存储 —— O(1) 追加、零容器分配、零扩容。
//   ｜ 为什么不⽤ List&lt;T&gt;？因为 List 在无事件时仍分配内部数组，每个 Add 可能触发扩容。
//   ｜ 单链表在无事件时只占 16B（两个 null 指针），有事件时 88B/事件。
// ═══════════════════════════════════════════════════════════════

using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Core;

/// <summary>
/// 实体基类（无泛型 Id）—— 只负责领域事件的存储和清理。
/// <para>
/// 💡 <b>领域事件如何存储？</b><br/>
/// 通过单链表存储：_head 指向第一个事件，_tail 指向最后一个事件。<br/>
/// 性能特性：追加 O(1)，清空 O(1)，遍历 O(n)，无额外堆分配。
/// </para>
/// </summary>
public abstract class Entity
{
    private DomainEvent? _head;
    private DomainEvent? _tail;

    /// <summary>是否有未处理的领域事件</summary>
    public bool HasDomainEvents => _head is not null;

    /// <summary>
    /// 添加领域事件到链表尾部。
    /// <para>
    /// 💡 性能优化：O(1) 追加，零堆分配。用 _tail 指针避免每次追加都遍历整个链表。
    /// </para>
    /// </summary>
    protected void RaiseEvent(DomainEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (_head is null) { _head = _tail = @event; }
        else { _tail!.Next = @event; _tail = @event; }
    }

    /// <summary>获取所有领域事件的只读视图（ref struct 枚举器，零分配 foreach）</summary>
    public DomainEventEnumerable DomainEvents() => new(_head);

    /// <summary>清空所有领域事件 —— 通常在 SaveChanges 成功后调用</summary>
    public void ClearDomainEvents()
    { _head = _tail = null; }
}

/// <summary>
/// 带强类型主键的实体 —— 用 DDD 身份模式替代基础类型 Id。
/// <para>
/// 💡 <b>通俗解释 —— 什么是实体？</b><br/>
/// 实体是"有身份"的对象。它的身份（Id）在生命周期中保持不变，即使其他属性变了。<br/>
/// 比如你换手机号后还是同一个人（身份证号不变）。
/// </para>
/// <para>
/// 💡 <b>为什么用强类型 Id 而不是 Guid？</b><br/>
/// Guid userId 和 Guid orderId 在编译期无法区分，容易写错参数顺序。<br/>
/// UserId id 和 OrderId id 是不同类型，编译器会阻止错误赋值。
/// </para>
/// </summary>
/// <typeparam name="TId">强类型 Id，如 UserId、OrderId</typeparam>
public abstract class Entity<TId> : Entity
    where TId : notnull, IEquatable<TId>
{
    /// <summary>实体唯一标识</summary>
    /// <remarks>
    /// Id 使用 <c>init</c> setter，要求 ORM 支持 init 属性设置。<br/>
    /// EF Core 8+ 兼容；Dapper 通过直接 SQL 映射不依赖此 setter。
    /// </remarks>
    public TId Id { get; init; }

    protected Entity(TId id) => Id = id;

    /// <summary>判断实体是否为瞬时状态（尚未持久化，Id 为默认值）</summary>
    public bool IsTransient() => EqualityComparer<TId>.Default.Equals(Id, default);

    /// <summary>
    /// 判断两个实体是否相等 —— 基于 Id 的值相等性。
    /// <para>
    /// 💡 关键设计：类型必须完全匹配、两个瞬时实体视为不同、持久化实体只比较 Id。
    /// </para>
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other || GetType() != other.GetType()) return false;
        if (IsTransient() || other.IsTransient()) return false;
        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    [SuppressMessage("Sonar", "S3249", Justification = "瞬态实体（未持久化）以引用相等性为准，base.GetHashCode() 是正确选择。")]
    [SuppressMessage("Sonar", "S3875", Justification = "== 运算符是实体值相等性 API 的一部分，对下游调用方更自然。")]
    public override int GetHashCode()
        => IsTransient() ? base.GetHashCode() : EqualityComparer<TId>.Default.GetHashCode(Id);

    public override string ToString() => $"{GetType().Name} [Id={Id}]";

    [SuppressMessage("Sonar", "S3875", Justification = "== 运算符是实体值相等性 API 的一部分，对下游调用方更自然。")]
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right)
        => left?.Equals(right) ?? right is null;

    [SuppressMessage("Sonar", "S3875", Justification = "!= 与 == 配套，保持相等性 API 完整性。")]
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right)
        => !(left == right);
}
