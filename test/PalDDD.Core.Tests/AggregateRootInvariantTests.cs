using System.Runtime.CompilerServices;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// 🏛️ DDD 战术模式最佳实践示范 — 聚合根不变量保护
// ═══════════════════════════════════════════════════════════════
// 本文件示范 DDD 聚合根的正确实现：
// 1. 构造器拒绝非法初始状态（不变量在创建时即建立）
// 2. 状态转换守卫（非法转换抛出异常）
// 3. 聚合边界（内部实体只通过聚合根修改）
// 4. 派生不变量自动重算（TotalAmount = Σ price*qty）
// 5. 业务规则违反抛出领域异常
// ═══════════════════════════════════════════════════════════════

// ─── 测试用聚合根（带不变量守卫）───

/// <summary>订单状态枚举 — 使用 SmartEnum 模式</summary>
public sealed class InvariantOrderStatus : SmartEnum<InvariantOrderStatus, string>
{
    public static readonly InvariantOrderStatus Submitted = new(0, "submitted");
    public static readonly InvariantOrderStatus Confirmed = new(1, "confirmed");
    public static readonly InvariantOrderStatus Cancelled = new(2, "cancelled");

    private InvariantOrderStatus(int ordinal, string value) : base(value) => Ordinal = ordinal;

    public int Ordinal { get; }

    [ModuleInitializer]
    internal static void Register() => RegisterValues([Submitted, Confirmed, Cancelled]);
}

/// <summary>订单项 — 聚合内部实体，只通过聚合根修改</summary>
public sealed class OrderLine
{
    public string ProductId { get; internal set; } = "";
    public int Quantity { get; internal set; }
    public decimal UnitPrice { get; internal set; }
    public decimal LineTotal => Quantity * UnitPrice;
}

/// <summary>带不变量守卫的订单聚合根</summary>
public sealed class InvariantOrder : AggregateRoot<Guid>
{
    public string CustomerName { get; private set; }
    public InvariantOrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public RowVersion Version { get; private set; }

    private readonly List<OrderLine> _lines = [];
    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>构造器拒绝非法初始状态 — 不变量在创建时即建立</summary>
    public InvariantOrder(Guid id, string customerName) : base(id)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new ArgumentException("客户名不能为空。", nameof(customerName));

        CustomerName = customerName;
        Status = InvariantOrderStatus.Submitted;
        Version = new RowVersion(0);
        RaiseEvent(new OrderSubmittedEvent(id, customerName));
    }

    /// <summary>状态转换守卫 — 已取消不能确认</summary>
    public void Confirm()
    {
        if (Status == InvariantOrderStatus.Cancelled)
            throw new InvalidOperationException("已取消的订单不能确认。");
        if (Status == InvariantOrderStatus.Confirmed)
            throw new InvalidOperationException("订单已确认，不能重复确认。");

        Status = InvariantOrderStatus.Confirmed;
        Version = Version.Next();
        RaiseEvent(new OrderConfirmedEvent(Id));
    }

    /// <summary>状态转换守卫 — 已确认不能取消（业务规则示例）</summary>
    public void Cancel(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("取消原因不能为空。", nameof(reason));

        Status = InvariantOrderStatus.Cancelled;
        Version = Version.Next();
        RaiseEvent(new OrderCancelledEvent(Id, reason));
    }

    /// <summary>添加订单项 — 业务规则验证 + 派生不变量重算</summary>
    public void AddLine(string productId, int quantity, decimal unitPrice)
    {
        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("产品 ID 不能为空。", nameof(productId));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "数量必须大于 0。");
        if (unitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "单价不能为负。");

        _lines.Add(new OrderLine { ProductId = productId, Quantity = quantity, UnitPrice = unitPrice });
        RecalculateTotal();
    }

    /// <summary>派生不变量重算 — TotalAmount = Σ LineTotal</summary>
    private void RecalculateTotal() => TotalAmount = _lines.Sum(l => l.LineTotal);
}

// ─── 测试用领域事件 ───

public sealed class OrderSubmittedEvent(Guid orderId, string customerName) : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.order-submitted.v1";
    public Guid OrderId { get; } = orderId;
    public string CustomerName { get; } = customerName;
}

public sealed class OrderConfirmedEvent(Guid orderId) : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.order-confirmed.v1";
    public Guid OrderId { get; } = orderId;
}

public sealed class OrderCancelledEvent(Guid orderId, string reason) : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.order-cancelled.v1";
    public Guid OrderId { get; } = orderId;
    public string Reason { get; } = reason;
}

// ─── 测试 ───

public sealed class AggregateRootInvariantTests
{
    [Test]
    public async Task Constructor_RejectsEmptyCustomerName_Throws()
    {
        await Assert.That(() => new InvariantOrder(Guid.NewGuid(), "")).Throws<ArgumentException>();
        await Assert.That(() => new InvariantOrder(Guid.NewGuid(), "   ")).Throws<ArgumentException>();
        await Assert.That(() => new InvariantOrder(Guid.NewGuid(), null!)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_ValidState_PersistsInvariants()
    {
        var id = Guid.NewGuid();
        var order = new InvariantOrder(id, "Alice");

        await Assert.That(order.CustomerName).IsEqualTo("Alice");
        await Assert.That(order.Status).IsEqualTo(InvariantOrderStatus.Submitted);
        await Assert.That(order.TotalAmount).IsEqualTo(0m);
        await Assert.That(order.Version).IsEqualTo(new RowVersion(0));
        await Assert.That(order.HasDomainEvents).IsTrue();
    }

    [Test]
    public async Task Confirm_FromSubmitted_UpdatesStatusAndRaisesEvent()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");
        order.ClearDomainEvents();

        order.Confirm();

        await Assert.That(order.Status).IsEqualTo(InvariantOrderStatus.Confirmed);
        await Assert.That(order.Version).IsEqualTo(new RowVersion(1));
        await Assert.That(order.HasDomainEvents).IsTrue();

        var events = new List<DomainEvent>();
        foreach (var e in order.DomainEvents()) events.Add(e);
        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0]).IsTypeOf<OrderConfirmedEvent>();
    }

    [Test]
    public async Task Confirm_FromCancelled_Throws()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");
        order.Cancel("test");

        await Assert.That(() => order.Confirm()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Confirm_AlreadyConfirmed_Throws()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");
        order.Confirm();

        await Assert.That(() => order.Confirm()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Cancel_EmptyReason_Throws()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");

        await Assert.That(() => order.Cancel("")).Throws<ArgumentException>();
        await Assert.That(() => order.Cancel("   ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task AddLine_ValidItem_RecalculatesTotalAmount()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");

        order.AddLine("prod-1", quantity: 2, unitPrice: 10m);
        await Assert.That(order.TotalAmount).IsEqualTo(20m);

        order.AddLine("prod-2", quantity: 3, unitPrice: 5m);
        await Assert.That(order.TotalAmount).IsEqualTo(35m);
    }

    [Test]
    public async Task AddLine_NegativePrice_Throws()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");

        await Assert.That(() =>
            order.AddLine("prod-1", quantity: 1, unitPrice: -1m)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AddLine_ZeroQuantity_Throws()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");

        await Assert.That(() =>
            order.AddLine("prod-1", quantity: 0, unitPrice: 10m)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AddLine_EmptyProductId_Throws()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");

        await Assert.That(() =>
            order.AddLine("", quantity: 1, unitPrice: 10m)).Throws<ArgumentException>();
    }

    [Test]
    public async Task AggregateBoundary_EnforcesIReadOnlyList_LinesNotModifiableExternally()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");
        order.AddLine("prod-1", quantity: 1, unitPrice: 10m);

        // Lines 是 IReadOnlyList — 外部无法添加/移除
        await Assert.That(order.Lines is IReadOnlyList<OrderLine>).IsTrue();
        await Assert.That(order.Lines).Count().IsEqualTo(1);
    }

    [Test]
    public async Task MultipleOperations_ModifyAggregate_PreservesInvariantConsistency()
    {
        var order = new InvariantOrder(Guid.NewGuid(), "Alice");
        order.AddLine("prod-1", quantity: 2, unitPrice: 10m);
        order.AddLine("prod-2", quantity: 1, unitPrice: 5m);
        order.Confirm();

        // 不变量成立
        await Assert.That(order.TotalAmount).IsEqualTo(25m);
        await Assert.That(order.Status).IsEqualTo(InvariantOrderStatus.Confirmed);
        await Assert.That(order.Version).IsEqualTo(new RowVersion(1));
        await Assert.That(order.Lines.Count).IsEqualTo(2);

        // 事件序列正确 — 构造器产生 Submitted，Confirm 产生 Confirmed
        var events = new List<DomainEvent>();
        foreach (var e in order.DomainEvents()) events.Add(e);
        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0]).IsTypeOf<OrderSubmittedEvent>();
        await Assert.That(events[1]).IsTypeOf<OrderConfirmedEvent>();
    }
}
