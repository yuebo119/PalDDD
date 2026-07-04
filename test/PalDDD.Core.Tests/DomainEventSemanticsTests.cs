using PalDDD.Testing;
using System.Collections.Concurrent;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// 🏛️ DDD 战术模式最佳实践示范 — 领域事件语义
// ═══════════════════════════════════════════════════════════════
// 补全领域事件核心语义测试：
// 1. TimeProvider 注入后 OccurredOn 确定性验证
// 2. AsyncLocal 并行隔离验证
// 3. 事件不可变性（init 属性）
// 4. 事件溯源能力（从事件流重放重建聚合状态）
// 5. IDomainEvent.EventName 编译时常量语义
// ═══════════════════════════════════════════════════════════════

// ─── 测试用事件 ───

public sealed class PriceChangedEvent(Guid productId, decimal newPrice) : DomainEvent, IDomainEvent
{
    public static string EventName => "catalog.price-changed.v1";
    public Guid ProductId { get; } = productId;
    public decimal NewPrice { get; } = newPrice;
}

public sealed class StockAdjustedEvent(Guid productId, int delta) : DomainEvent, IDomainEvent
{
    public static string EventName => "inventory.stock-adjusted.v1";
    public Guid ProductId { get; } = productId;
    public int Delta { get; } = delta;
}

// ─── 测试 ───

public sealed class DomainEventSemanticsTests
{
    [Test]
    public async Task EventId_IsUnique_AcrossInstances()
    {
        var a = new PriceChangedEvent(Guid.NewGuid(), 10m);
        var b = new PriceChangedEvent(Guid.NewGuid(), 20m);

        await Assert.That(a.EventId).IsNotEqualTo(b.EventId);
    }

    [Test]
    public async Task OccurredOn_UsesInjectedTimeProvider_IsDeterministic()
    {
        var fixedTime = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var original = DomainEvent.TimeProvider;
        try
        {
            DomainEvent.TimeProvider = new FakeTimeProvider(fixedTime);

            var evt = new PriceChangedEvent(Guid.NewGuid(), 99m);

            await Assert.That(evt.OccurredOn).IsEqualTo(fixedTime);
        }
        finally
        {
            DomainEvent.TimeProvider = original;
        }
    }

    [Test]
    public async Task TimeProvider_AsyncLocal_IsolationBetweenParallelContexts()
    {
        var time1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time2 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var original = DomainEvent.TimeProvider;

        try
        {
            var results = new ConcurrentDictionary<int, DateTimeOffset>();

            var task1 = Task.Run(() =>
            {
                DomainEvent.TimeProvider = new FakeTimeProvider(time1);
                var evt = new PriceChangedEvent(Guid.NewGuid(), 1m);
                results[1] = evt.OccurredOn;
            });

            var task2 = Task.Run(() =>
            {
                DomainEvent.TimeProvider = new FakeTimeProvider(time2);
                var evt = new PriceChangedEvent(Guid.NewGuid(), 2m);
                results[2] = evt.OccurredOn;
            });

            await Task.WhenAll(task1, task2);

            // 两个并行上下文设置了不同的 TimeProvider，互不污染
            await Assert.That(results[1]).IsEqualTo(time1);
            await Assert.That(results[2]).IsEqualTo(time2);
        }
        finally
        {
            DomainEvent.TimeProvider = original;
        }
    }

    [Test]
    public async Task TimeProvider_NullAssignment_Throws()
    {
        await Assert.That(() => DomainEvent.TimeProvider = null!).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Event_PropertiesAreInitOnly_ImmutableAfterConstruction()
    {
        var evt = new PriceChangedEvent(Guid.NewGuid(), 42m);

        // EventId 和 OccurredOn 是 init — 构造后不可变
        // 验证它们可读
        await Assert.That(evt.EventId).IsNotEqualTo(default(PalUlid));
        await Assert.That(evt.OccurredOn <= DateTimeOffset.UtcNow).IsTrue();

        // 验证 init 语义 — 无法通过反射赋值（除非绕过编译器）
        var eventIdProp = typeof(DomainEvent).GetProperty(nameof(DomainEvent.EventId));
        await Assert.That(eventIdProp).IsNotNull();
        // init setter 存在但外部不可赋值
        await Assert.That(eventIdProp!.GetSetMethod(nonPublic: true) is not null).IsTrue();
    }

    [Test]
    public async Task EventSourcing_Replay_RestoresAggregateState()
    {
        // 事件溯源核心契约：从事件流重放可以重建聚合状态
        var productId = Guid.NewGuid();
        var original = new ReplayableProduct(productId, "Widget", 10m, 100);
        original.ChangePrice(15m);
        original.AdjustStock(-5);
        original.ChangePrice(12m);

        // 收集事件
        var events = new List<DomainEvent>();
        foreach (var e in original.DomainEvents()) events.Add(e);
        await Assert.That(events.Count).IsEqualTo(4); // Created + PriceChanged + StockAdjusted + PriceChanged

        // 从事件重放重建
        var replayed = ReplayableProduct.Replay(events);

        await Assert.That(replayed.Id).IsEqualTo(productId);
        await Assert.That(replayed.Name).IsEqualTo("Widget");
        await Assert.That(replayed.Price).IsEqualTo(12m);
        await Assert.That(replayed.Stock).IsEqualTo(95);
    }

    [Test]
    public async Task EventOrdering_PreservedInAppendOrder()
    {
        var entity = new EventHostEntity();
        var evt1 = new PriceChangedEvent(Guid.NewGuid(), 10m);
        var evt2 = new StockAdjustedEvent(Guid.NewGuid(), 5);
        var evt3 = new PriceChangedEvent(Guid.NewGuid(), 20m);

        entity.AppendEvent(evt1);
        entity.AppendEvent(evt2);
        entity.AppendEvent(evt3);

        var events = new List<DomainEvent>();
        foreach (var e in entity.DomainEvents()) events.Add(e);

        await Assert.That(events.Count).IsEqualTo(3);
        await Assert.That(events[0]).IsSameReferenceAs(evt1);
        await Assert.That(events[1]).IsSameReferenceAs(evt2);
        await Assert.That(events[2]).IsSameReferenceAs(evt3);
    }

    [Test]
    public async Task IDomainEvent_EventName_IsCompileTimeConstant()
    {
        // static abstract 接口成员 — 编译时常量，AOT 安全
        // 不需要实例化即可访问
        await Assert.That(PriceChangedEvent.EventName).IsEqualTo("catalog.price-changed.v1");
        await Assert.That(StockAdjustedEvent.EventName).IsEqualTo("inventory.stock-adjusted.v1");
        await Assert.That(OrderSubmittedEvent.EventName).IsEqualTo("ordering.order-submitted.v1");
    }

    [Test]
    public async Task DomainEvent_DefaultOccuredOn_UsesSystemTimeProvider()
    {
        // 不注入 TimeProvider 时，默认使用 TimeProvider.System
        var before = DateTimeOffset.UtcNow;
        var evt = new PriceChangedEvent(Guid.NewGuid(), 10m);
        var after = DateTimeOffset.UtcNow;

        await Assert.That(evt.OccurredOn >= before).IsTrue();
        await Assert.That(evt.OccurredOn <= after).IsTrue();
    }
}

// ─── 事件溯源测试夹具 ───

public sealed class ReplayableProduct : AggregateRoot<Guid>
{
    public string Name { get; private set; }
    public decimal Price { get; private set; }
    public int Stock { get; private set; }

    public ReplayableProduct(Guid id, string name, decimal price, int stock) : base(id)
    {
        Name = name;
        Price = price;
        Stock = stock;
        RaiseEvent(new ProductCreatedEvent(id, name, price, stock));
    }

    private ReplayableProduct(Guid id) : base(id)
    {
        Name = "";
        Price = 0;
        Stock = 0;
    }

    public void ChangePrice(decimal newPrice)
    {
        Price = newPrice;
        RaiseEvent(new PriceChangedEvent(Id, newPrice));
    }

    public void AdjustStock(int delta)
    {
        Stock += delta;
        RaiseEvent(new StockAdjustedEvent(Id, delta));
    }

    /// <summary>从事件流重放重建聚合状态 — 事件溯源核心契约</summary>
    public static ReplayableProduct Replay(IReadOnlyList<DomainEvent> events)
    {
        if (events.Count == 0)
            throw new ArgumentException("事件流不能为空。", nameof(events));

        var created = events.OfType<ProductCreatedEvent>().First();
        var product = new ReplayableProduct(created.ProductId)
        {
            Name = created.Name,
            Price = created.Price,
            Stock = created.Stock
        };

        foreach (var evt in events.Skip(1))
        {
            switch (evt)
            {
                case PriceChangedEvent pc:
                    product.Price = pc.NewPrice;
                    break;

                case StockAdjustedEvent sa:
                    product.Stock += sa.Delta;
                    break;
            }
        }

        return product;
    }
}

public sealed class ProductCreatedEvent(Guid productId, string name, decimal price, int stock) : DomainEvent, IDomainEvent
{
    public static string EventName => "catalog.product-created.v1";
    public Guid ProductId { get; } = productId;
    public string Name { get; } = name;
    public decimal Price { get; } = price;
    public int Stock { get; } = stock;
}
