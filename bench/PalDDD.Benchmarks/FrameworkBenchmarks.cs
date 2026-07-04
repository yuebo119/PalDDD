using BenchmarkDotNet.Attributes;
using PalDDD.Core;

namespace PalDDD.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 实体领域事件存储基准 — 单链表 vs List
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[ShortRunJob]
public class EntityDomainEventBenchmarks
{
    [Benchmark(Baseline = true)]
    public void AddSingleEvent_LinkedList()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
    }

    [Benchmark]
    public void AddMultipleEvents_LinkedList()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("reason");
        order.Complete();
        order.Cancel("again");
        order.Complete();
    }

    [Benchmark]
    public void IterateEvents_RefStructEnumerator()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("reason");

        var count = 0;
        foreach (var _ in order.DomainEvents())
            count++;
    }

    [Benchmark]
    public void ClearEvents()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("reason");
        order.ClearDomainEvents();
    }

    [AggregateName("Order")]
    private sealed class Order : AggregateRoot<Guid>
    {
        public Order(Guid id, string name) : base(id) => CustomerName = name;

        public string CustomerName { get; private set; }

        public void Complete() => RaiseEvent(new OrderCompleted(Id));

        public void Cancel(string r) => RaiseEvent(new OrderCancelled(Id, r));
    }

    private sealed class OrderCompleted(Guid orderId) : DomainEvent
    {
        public Guid OrderId { get; } = orderId;
    }

    private sealed class OrderCancelled(Guid orderId, string reason) : DomainEvent
    {
        public Guid OrderId { get; } = orderId;
        public string Reason { get; } = reason;
    }
}

// ═══════════════════════════════════════════════════════════════
// 值对象基准 — readonly record struct 性能
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[ShortRunJob]
public class ValueObjectBenchmarks
{
    [Benchmark]
    public ValueObject<int> Create_Int()
        => new(42);

    [Benchmark]
    public int ImplicitConversion()
    {
        ValueObject<int> vo = new(99);
        return vo; // 隐式转换
    }

    [Benchmark]
    public RowVersion Next_RowVersion()
    {
        var v = new RowVersion(1);
        return v.Next();
    }
}

// ═══════════════════════════════════════════════════════════════
// 智能枚举基准 — FrozenDictionary 查找
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[ShortRunJob]
public class SmartEnumBenchmarks
{
    [Benchmark]
    public OrderStatusBench FromValue_Known()
        => OrderStatusBench.FromValue("shipped");

    [Benchmark]
    public bool TryFromValue_Valid()
        => OrderStatusBench.TryFromValue("pending", out _);

    [Benchmark]
    public IReadOnlyCollection<OrderStatusBench> AllValues()
        => OrderStatusBench.All;

    [Benchmark]
    public bool EqualsCheck()
        => OrderStatusBench.Pending.Equals(OrderStatusBench.FromValue("pending"));

    public sealed class OrderStatusBench : SmartEnum<OrderStatusBench, string>
    {
        public static readonly OrderStatusBench Pending = new("pending");
        public static readonly OrderStatusBench Shipped = new("shipped");
        public static readonly OrderStatusBench Delivered = new("delivered");
        public static readonly OrderStatusBench Cancelled = new("cancelled");
        public static readonly OrderStatusBench Refunded = new("refunded");

        public OrderStatusBench(string value) : base(value)
        {
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 验证结果基准 — readonly struct 零分配
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[ShortRunJob]
public class ValidationBenchmarks
{
    [Benchmark]
    public PalValidationResult Success()
        => PalValidationResult.Success();

    [Benchmark]
    public PalValidationResult Failed_SingleError()
        => PalValidationResult.Failed("Prop", "Error message");

    [Benchmark]
    public bool EqualsCheck()
    {
        var a = PalValidationResult.Success();
        var b = PalValidationResult.Success();
        return a == b;
    }
}

// ═══════════════════════════════════════════════════════════════
// 实体创建和相等性基准
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[ShortRunJob]
public class EntityCreationBenchmarks
{
    private static readonly Guid _id = Guid.NewGuid();

    [Benchmark]
    public TestEntity Create()
        => new(_id, "test");

    [Benchmark]
    public bool EqualsCheck()
    {
        var a = new TestEntity(_id, "a");
        var b = new TestEntity(_id, "b");
        return a == b;
    }

    [Benchmark]
    public int GetHashCode_Compute()
        => new TestEntity(_id, "test").GetHashCode();

    [Benchmark]
    public bool IsTransient_Check()
    {
        var entity = new TestEntity(Guid.Empty, "test");
        return entity.IsTransient();
    }

    public sealed class TestEntity : Entity<Guid>
    {
        public TestEntity(Guid id, string name) : base(id) => Name = name;

        public string Name { get; }
    }
}
