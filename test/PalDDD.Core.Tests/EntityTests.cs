namespace PalDDD.Core.Tests;

// ─── 测试用实体 ───

public sealed class Order : AggregateRoot<Guid>
{
    public string CustomerName { get; private set; }

    public Order(Guid id, string customerName) : base(id) => CustomerName = customerName;

    public void ChangeName(string name) => CustomerName = name;

    public void Complete()
    {
        RaiseEvent(new OrderCompleted(Id));
    }

    public void Cancel(string reason)
    {
        RaiseEvent(new OrderCancelled(Id, reason));
    }
}

public sealed class OrderCompleted(Guid orderId) : DomainEvent
{
    public Guid OrderId { get; } = orderId;
}

public sealed class OrderCancelled(Guid orderId, string reason) : DomainEvent
{
    public Guid OrderId { get; } = orderId;
    public string Reason { get; } = reason;
}

// ─── 实体基础测试 ───

public sealed class EntityTests
{
    [Test]
    public async Task NewEntity_IsTransient()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        await Assert.That(order.IsTransient()).IsFalse();
    }

    [Test]
    public async Task SameId_AreEqual()
    {
        var id = Guid.NewGuid();
        var a = new Order(id, "A");
        var b = new Order(id, "B");
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task DifferentId_AreNotEqual()
    {
        var a = new Order(Guid.NewGuid(), "A");
        var b = new Order(Guid.NewGuid(), "B");
        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task Null_IsNotEqual()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        await Assert.That(order.Equals(null)).IsFalse();
    }

    [Test]
    public async Task SameReference_IsEqual()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        await Assert.That(order).IsEqualTo(order);
    }

    [Test]
    public async Task HashCode_IsConsistent()
    {
        var id = Guid.NewGuid();
        var a = new Order(id, "A");
        var b = new Order(id, "B");
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task ToString_ContainsTypeName()
    {
        var id = Guid.NewGuid();
        var order = new Order(id, "Test");
        var str = order.ToString();
        await Assert.That(str).Contains("Order");
        await Assert.That(str).Contains(id.ToString());
    }
}

// ─── 领域事件链表存储测试 ───

public sealed class DomainEventStorageTests
{
    [Test]
    public async Task NoEvents_HasDomainEvents_IsFalse()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        await Assert.That(order.HasDomainEvents).IsFalse();
    }

    [Test]
    public async Task SingleEvent_HasDomainEvents_IsTrue()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        await Assert.That(order.HasDomainEvents).IsTrue();
    }

    [Test]
    public async Task MultipleEvents_AreStoredInOrder()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("Test reason");

        var events = new List<DomainEvent>();
        foreach (var evt in order.DomainEvents())
            events.Add(evt);

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0]).IsTypeOf<OrderCompleted>();
        await Assert.That(events[1]).IsTypeOf<OrderCancelled>();
    }

    [Test]
    public async Task ClearDomainEvents_EmptiesList()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        await Assert.That(order.HasDomainEvents).IsTrue();

        order.ClearDomainEvents();
        await Assert.That(order.HasDomainEvents).IsFalse();

        var count = 0;
        foreach (var _ in order.DomainEvents()) count++;
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task Events_HaveUniqueIds()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("reason");

        var ids = new HashSet<Guid>();
        foreach (var evt in order.DomainEvents())
            ids.Add(evt.EventId);

        await Assert.That(ids.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Events_HaveOccurredOnSet()
    {
        var before = DateTimeOffset.UtcNow;
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        var after = DateTimeOffset.UtcNow;

        foreach (var evt in order.DomainEvents())
        {
            await Assert.That(evt.OccurredOn >= before).IsTrue();
            await Assert.That(evt.OccurredOn <= after).IsTrue();
        }
    }

    [Test]
    public async Task ClearDomainEvents_DoesNotMutateEventNodeLinks()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("Test reason");

        var enumerator = order.DomainEvents().GetEnumerator();
        await Assert.That(enumerator.MoveNext()).IsTrue();
        var first = enumerator.Current;
        await Assert.That(enumerator.MoveNext()).IsTrue();
        var second = enumerator.Current;

        order.ClearDomainEvents();

        await Assert.That(second).IsSameReferenceAs(first.Next);
        await Assert.That(second.Next).IsNull();
    }

    [Test]
    public async Task DomainEvent_Null_ThrowsArgumentNullException()
    {
        var host = new EventHostEntity();
        await Assert.That(() => host.AppendEvent(null!)).Throws<ArgumentNullException>();
    }
}

/// <summary>测试用实体 — 直接暴露 RaiseEvent 以测试基类边界条件。</summary>
public sealed class EventHostEntity : Entity
{
    public void AppendEvent(DomainEvent @event) => RaiseEvent(@event);
}

// ─── ref struct 枚举器测试 ───

public sealed class DomainEventEnumerableTests
{
    [Test]
    public async Task EmptyEnumerable_NoIteration()
    {
        var enumerable = new DomainEventEnumerable(null);
        var count = 0;
        foreach (var _ in enumerable) count++;
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task SingleEvent_IteratesOnce()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();

        var count = 0;
        foreach (var _ in order.DomainEvents()) count++;
        await Assert.That(count).IsEqualTo(1);
    }
}
