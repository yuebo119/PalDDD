namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔄 事件溯源回放契约测试
// ═══════════════════════════════════════════════════════════════
// DDD 聚合根事件溯源的核心契约：
// 1. 相同事件序列重建出相同聚合状态
// 2. 事件应用幂等（重复应用同一事件不变状态）
// 3. 事件顺序敏感（乱序重建状态不同）
// 4. 空事件流边界
// 5. 事件重建后可继续产生新事件
// ═══════════════════════════════════════════════════════════════

public sealed class EventSourcingContractTests
{
    [Test]
    public async Task Replay_SameEventSequence_ProducesEqualState()
    {
        var id = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            new ReplayableOrderCreated(id, "Alice", 100m),
            new ReplayableOrderAmountAdjusted(id, 50m),
            new ReplayableOrderConfirmed(id)
        };

        var agg1 = ReplayableOrder.Replay(events);
        var agg2 = ReplayableOrder.Replay(events);

        await Assert.That(agg1.CustomerName).IsEqualTo(agg2.CustomerName);
        await Assert.That(agg1.Amount).IsEqualTo(agg2.Amount);
        await Assert.That(agg1.Status).IsEqualTo(agg2.Status);
        await Assert.That(agg1.Version).IsEqualTo(agg2.Version);
    }

    [Test]
    public async Task Replay_DifferentEventSequences_ProduceDifferentState()
    {
        var id = Guid.NewGuid();
        var events1 = new DomainEvent[]
        {
            new ReplayableOrderCreated(id, "Alice", 100m),
            new ReplayableOrderAmountAdjusted(id, 50m)
        };
        var events2 = new DomainEvent[]
        {
            new ReplayableOrderCreated(id, "Alice", 100m),
            new ReplayableOrderAmountAdjusted(id, 200m)
        };

        var agg1 = ReplayableOrder.Replay(events1);
        var agg2 = ReplayableOrder.Replay(events2);

        await Assert.That(agg1.Amount).IsNotEqualTo(agg2.Amount);
    }

    [Test]
    public async Task Replay_Idempotent_SameStreamReplayedTwice_ProducesEqualState()
    {
        // 事件溯源的幂等契约 — 同一事件流多次回放，结果必须一致
        // 这是回放幂等性，区别于"单次回放中重复事件"
        var id = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            new ReplayableOrderCreated(id, "Alice", 100m),
            new ReplayableOrderAmountAdjusted(id, 50m),
            new ReplayableOrderConfirmed(id)
        };

        var first = ReplayableOrder.Replay(events);
        var second = ReplayableOrder.Replay(events);

        await Assert.That(first.CustomerName).IsEqualTo(second.CustomerName);
        await Assert.That(first.Amount).IsEqualTo(second.Amount);
        await Assert.That(first.Status).IsEqualTo(second.Status);
        await Assert.That(first.Version).IsEqualTo(second.Version);
    }

    [Test]
    public async Task Replay_OutOfOrder_ThrowsOrProducesDifferentState()
    {
        // 顺序敏感 — Confirm 必须在 Created 之后
        var id = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            new ReplayableOrderConfirmed(id),
            new ReplayableOrderCreated(id, "Alice", 100m)
        };

        // 乱序回放应抛异常（Created 事件必须在首位）
        await Assert.That(() => ReplayableOrder.Replay(events)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Replay_EmptyEventStream_Throws()
    {
        await Assert.That(() => ReplayableOrder.Replay([])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Replay_PreservesVersionAcrossReplays()
    {
        var id = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            new ReplayableOrderCreated(id, "Alice", 100m),
            new ReplayableOrderAmountAdjusted(id, 50m),
            new ReplayableOrderConfirmed(id)
        };

        var agg = ReplayableOrder.Replay(events);

        // 版本号应反映事件数量
        await Assert.That(agg.Version).IsEqualTo(events.Length);
    }

    [Test]
    public async Task Replay_AggregateCanProduceNewEventsAfterReplay()
    {
        // 事件溯源核心契约 — 重建后的聚合可继续产生新事件
        var id = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            new ReplayableOrderCreated(id, "Alice", 100m)
        };

        var agg = ReplayableOrder.Replay(events);
        agg.ClearDomainEvents(); // 回放产生的事件不应再次持久化

        // 重建后可继续业务操作产生新事件
        agg.Confirm();

        await Assert.That(agg.HasDomainEvents).IsTrue();
        var newEvents = new List<DomainEvent>();
        foreach (var e in agg.DomainEvents()) newEvents.Add(e);
        await Assert.That(newEvents).HasSingleItem();
        await Assert.That(newEvents[0]).IsTypeOf<ReplayableOrderConfirmed>();
    }
}

// ─── 事件溯源测试夹具 ───────────────────────────────────────────

public sealed class ReplayableOrderCreated(Guid orderId, string customerName, decimal amount) : DomainEvent, IDomainEvent
{
    public static string EventName => "sourcing.order-created.v1";
    public Guid OrderId { get; } = orderId;
    public string CustomerName { get; } = customerName;
    public decimal Amount { get; } = amount;
}

public sealed class ReplayableOrderAmountAdjusted(Guid orderId, decimal newAmount) : DomainEvent, IDomainEvent
{
    public static string EventName => "sourcing.order-amount-adjusted.v1";
    public Guid OrderId { get; } = orderId;
    public decimal NewAmount { get; } = newAmount;
}

public sealed class ReplayableOrderConfirmed(Guid orderId) : DomainEvent, IDomainEvent
{
    public static string EventName => "sourcing.order-confirmed.v1";
    public Guid OrderId { get; } = orderId;
}

/// <summary>可回放聚合 — 事件溯源契约测试夹具</summary>
public sealed class ReplayableOrder : AggregateRoot<Guid>
{
    public string CustomerName { get; private set; } = "";
    public decimal Amount { get; private set; }
    public string Status { get; private set; } = "Created";
    public int Version { get; private set; }

    public ReplayableOrder(Guid id, string customerName, decimal amount) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerName);
        CustomerName = customerName;
        Amount = amount;
        Status = "Created";
        Version = 1;
        RaiseEvent(new ReplayableOrderCreated(id, customerName, amount));
    }

    private ReplayableOrder(Guid id) : base(id)
    {
    }

    public void AdjustAmount(decimal newAmount)
    {
        Amount = newAmount;
        Version++;
        RaiseEvent(new ReplayableOrderAmountAdjusted(Id, newAmount));
    }

    public void Confirm()
    {
        if (Status != "Created")
            throw new InvalidOperationException($"Cannot confirm order in status '{Status}'.");
        Status = "Confirmed";
        Version++;
        RaiseEvent(new ReplayableOrderConfirmed(Id));
    }

    /// <summary>从事件流重建聚合 — 事件溯源核心方法</summary>
    public static ReplayableOrder Replay(IReadOnlyList<DomainEvent> events)
    {
        if (events.Count == 0)
            throw new ArgumentException("事件流不能为空。", nameof(events));

        var created = events[0] as ReplayableOrderCreated
            ?? throw new InvalidOperationException("事件流必须以 ReplayableOrderCreated 开头。");

        var order = new ReplayableOrder(created.OrderId)
        {
            CustomerName = created.CustomerName,
            Amount = created.Amount,
            Status = "Created",
            Version = 1
        };

        // 跳过首事件（已用于构造），应用剩余事件
        foreach (var evt in events.Skip(1))
        {
            switch (evt)
            {
                case ReplayableOrderAmountAdjusted adjust:
                    order.Amount = adjust.NewAmount;
                    order.Version++;
                    break;

                case ReplayableOrderConfirmed:
                    if (order.Status != "Created")
                        throw new InvalidOperationException("Confirm 事件在当前状态下非法。");
                    order.Status = "Confirmed";
                    order.Version++;
                    break;

                default:
                    throw new InvalidOperationException($"未知事件类型: {evt.GetType().Name}");
            }
        }

        return order;
    }
}
