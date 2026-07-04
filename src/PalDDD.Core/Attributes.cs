// ─────────────────────────────────────────────────────────────
// 🏷️ 属性 — SourceGen + Analyzer 的编译期契约
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Core;

// ─────────────────────────────────────────────────────────────
// 源码生成器与战略 DDD 标记属性集合
// ─────────────────────────────────────────────────────────────
//
// 💡 战略 DDD vs 战术 DDD：
//   ｜ 战术 DDD = 代码级别的模式（实体、值对象、聚合根、领域事件）
//   ｜ 战略 DDD = 更高层次的组织（限界上下文、领域能力、流程管理器）
//   ｜ 这些 Attribute 将战略分析的结果直接编码进代码，让架构决策可见、可验证。

/// <summary>
/// 标记强类型 ID 生成目标 —— 源码生成器据此生成 Identity 结构体。
/// <para>💡 使用示例：<c>[GenerateId(typeof(Guid))] public partial record struct UserId;</c></para>
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class GenerateIdAttribute(Type idType) : Attribute
{
    /// <summary>原始键类型，如 typeof(Guid)、typeof(int)、typeof(string)</summary>
    public Type IdType { get; } = idType;
}

/// <summary>标记智能枚举生成目标 —— 源码生成器据此生成 Enum 注册代码</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GenerateEnumAttribute : Attribute;

/// <summary>
/// 标记消息类型生成目标 —— 源码生成器据此生成 MessageRegistry 注册。
/// <para>💡 通俗解释 —— 什么是消息注册？<br/>
/// 在分布式系统中，消息需要被唯一标识以便序列化/反序列化。<br/>
/// MessageRegistry 维护消息名 -> 消息类型的映射，使得消息可以从字节流中正确还原。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GenerateMessageAttribute : Attribute
{
    /// <summary>稳定的线缆名称（wire name），如 "orders.order-submitted.v1" —— 用于消息序列化标识</summary>
    public string? Name { get; init; }

    /// <summary>线缆协议版本号，默认为 1 —— 用于消息格式演化管理</summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>
/// 标记聚合所属的限界上下文 —— 战略 DDD 分析的核心概念。
/// <para>
/// 💡 通俗解释 —— 什么是限界上下文（Bounded Context）？<br/>
/// 一个大系统中，同一个词在不同子系统可能含义不同。<br/>
/// 比如"客户"在订单上下文指买家，在配送上下文指收货人。<br/>
/// 限界上下文划清了每个子系统的边界，让每个上下文的模型保持精炼和一致。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class BoundedContextAttribute(string name) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Name cannot be blank", nameof(name))
        : name;
}

/// <summary>
/// 标记聚合所属的领域能力 —— 战略 DDD 分析用，表示聚合提供的业务能力。
/// <para>💡 一个限界上下文中可以有多个领域能力，一个聚合可能实现一个或多个能力。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DomainCapabilityAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// 标记聚合根为流程管理器（Saga / Process Manager）。
/// <para>
/// 💡 通俗解释 —— 什么是流程管理器（Saga）？<br/>
/// Saga 是一种长生命周期的业务流程协调器。它跨越多个聚合和限界上下文，<br/>
/// 处理分布式事务的最终一致性。例如"下单流程"包括：<br/>
/// 1. 创建订单 -> 2. 预留库存 -> 3. 扣款 -> 4. 发货<br/>
/// 如果某步失败，Saga 负责触发补偿操作（退款、释放库存等）。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ProcessManagerAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// 标记聚合根的领域名称 —— 供 Source Generator 和运行时诊断使用。
/// <para>使用 Attribute 而非抽象属性将框架元数据从领域基类中解耦，保持领域层纯净。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AggregateNameAttribute(string name) : Attribute
{
    /// <summary>聚合的业务名称</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Name cannot be blank", nameof(name))
        : name;
}
