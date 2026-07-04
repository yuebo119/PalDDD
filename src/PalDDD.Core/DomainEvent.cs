// ═══════════════════════════════════════════════════════════════
// 📬 DomainEvent（领域事件） — 既是事件容器，也是单链表节点
// ═══════════════════════════════════════════════════════════════
//
// 💡 双角色设计：
//   ｜ 角色 1（事件容器）：每个 DomainEvent 承载 EventId（全局唯一）和 OccurredOn（发生时间）
//   ｜ 角色 2（链表节点）：通过 Next 属性串联，Entity 内部用 O(1) 追加、零容器分配
//   ｜
//   ｜ 为什么不用 Ulid.New() 和 DateTimeOffset.UtcNow 直接写死？
//   ｜   → 因为测试需要确定性时间！TimeProvider 抽象让测试可以注入 FakeTimeProvider。
//   ｜
//   ｜ TimeProvider 为什么用 AsyncLocal 而不是 static？
//   ｜   → 因为 static 全局共享，并行测试会互相干扰。
//   ｜   → AsyncLocal 为每个执行上下文提供独立的 TimeProvider，测试间完全隔离。
// ═══════════════════════════════════════════════════════════════

using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Core;

/// <summary>
/// 领域事件标记接口。
/// <para>
/// 💡 <b>通俗解释 —— 什么是领域事件？</b><br/>
/// 领域事件是"已经发生的业务事实"的记录。比如"订单已提交"、"用户已注册"。
/// 一个事件一旦发生就不再改变（不可变），其他业务模块可以订阅并响应这些事件。
/// </para>
/// <para>
/// EventName 是 static abstract 属性，编译时常量，✅ AOT 安全 —— 不需要运行时反射即可确定事件名。
/// </para>
/// </summary>
public interface IDomainEvent
{
    /// <summary>事件线名称（如 "ordering.order-submitted.v1"）—— 编译时常量，✅ AOT 安全</summary>
    static abstract string EventName { get; }
}

/// <summary>
/// 领域事件抽象基类 —— 同时扮演两个角色：<br/>
/// <b>1. 事件容器</b>：提供 EventId（全局唯一标识）和 OccurredOn（发生时间）<br/>
/// <b>2. 单链表节点</b>：通过 Next 属性串联，Entity 内部用 O(1) 追加、零容器分配<br/>
/// <para>
/// 💡 <b>为什么用单链表而不是 List&lt;DomainEvent&gt;？</b><br/>
/// List 在无事件时仍会分配内部数组，每次 Add 可能触发扩容复制。<br/>
/// 单链表在无事件时只占 16 字节（两个 null 指针），有事件时每个事件 88 字节，无额外容器开销。
/// </para>
/// <para>具体事件类需同时继承此基类并实现 IDomainEvent 接口。</para>
/// </summary>
public abstract class DomainEvent
{
    /// <summary>
    /// 框架级时间提供者 —— 基于 AsyncLocal 实现测试隔离。
    /// <para>
    /// 💡 默认值为 TimeProvider.System（使用真实 UTC 时间）。
    /// </para>
    /// <para>
    /// 📐 <b>设计决策 — 为什么是 internal 而非 public/依赖注入？</b><br/>
    /// 1. <b>internal 可见性</b>：DomainEvent 是领域原语，时间戳生成是框架内部关注点。
    ///    暴露为 public 会让领域层承担时间策略配置责任，违反 DDD 层次分离。<br/>
    /// 2. <b>AsyncLocal 而非 DI</b>：DomainEvent 构造发生在聚合方法深处（如 Order.Submit），
    ///    通过构造函数注入 TimeProvider 会污染所有领域方法的签名。AsyncLocal 提供隐式上下文流，
    ///    保持领域方法纯净，同时支持并行测试隔离。<br/>
    /// 3. <b>测试访问</b>：测试项目通过 InternalsVisibleTo 设置 internal 访问权，
    ///    可注入 FakeTimeProvider 实现确定性时间。第三方扩展若需控制时间，
    ///    应在应用层通过 OutboxProcessor/InboxProcessor 的 TimeProvider 参数注入，
    ///    而非修改领域事件的时间戳生成。<br/>
    /// 4. <b>线程安全</b>：AsyncLocal 按执行上下文隔离，无需同步原语。<br/>
    /// 5. <b>流动边界</b>：AsyncLocal 沿异步执行上下文流动（await/ContinueWith 自动传播）。
    ///    在 <c>Task.Run</c> 不捕获当前上下文的边界场景可能不流动，但当前框架使用场景
    ///    （聚合方法内构造 DomainEvent）均在调用方上下文内完成，不受此限制。
    /// </para>
    /// </summary>
    internal static TimeProvider TimeProvider
    {
        get => s_timeProvider.Value ?? TimeProvider.System;
        set => s_timeProvider.Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static readonly AsyncLocal<TimeProvider> s_timeProvider = new();

    /// <summary>
    /// 链表下一节点的引用。Next 为 null 表示这是链表尾部（最后一个事件）。
    /// </summary>
    /// <remarks>
    /// 该属性只用于 <see cref="Entity"/> 内部领域事件单链表：<see cref="Entity"/> 在 RaiseEvent 时写入，
    /// <see cref="DomainEventEnumerable"/> 在枚举时读取。它不是事件业务状态，外部代码不应依赖或修改。
    /// </remarks>
    internal DomainEvent? Next { get; set; }

    /// <summary>
    /// 事件全局唯一 ID。每次 new DomainEvent 时自动生成，
    /// 用于幂等处理、事件去重、以及分布式追踪中的事件关联。
    /// </summary>
    public PalUlid EventId { get; init; } = PalUlid.New();

    /// <summary>
    /// 事件发生时间（UTC）。构造时自动从 TimeProvider 获取当前时间。
    /// 如果测试中设置了 FakeTimeProvider，则使用测试提供的固定时间。
    /// </summary>
    public DateTimeOffset OccurredOn { get; init; } = TimeProvider.GetUtcNow();
}
