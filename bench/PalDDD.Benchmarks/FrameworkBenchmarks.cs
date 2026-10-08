using BenchmarkDotNet.Attributes;
using PalDDD.Core;

namespace PalDDD.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 实体领域事件存储基准 — 单链表领域事件（追加/遍历/清理）
// 注：标题不再声称 "vs List"——本组只测单链表实现，未设 List 对照基准。
// SMP-204 口径声明：四个基准的 ns/op 均含实体构造与 ID 生成固定成本
// （new Order + Guid.NewGuid）——即"消息发布管线最小路径"口径，
// 不是纯 RaiseEvent/遍历开销；纯事件操作需另行外置构造后测量。
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// ⚠️ 无 [ShortRunJob]：--framework 入口传显式 MediumRun InProcess ManualConfig，类级 job
// attribute 会与之 union 叠加出双 job——CsProj 工具链 job 在 net11-rc moniker 上触发
// DotNetSdkValidator 崩溃（--persist/--compression 修复先例同款，见 Program.cs 注释）
public class EntityDomainEventBenchmarks
{
    [Benchmark(Baseline = true)]
    public int AddSingleEvent_LinkedList()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        // 返回可观测值防 DCE：JIT 必须保留 RaiseEvent 的副作用
        return order.HasDomainEvents ? 1 : 0;
    }

    [Benchmark]
    public int AddMultipleEvents_LinkedList()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("reason");
        order.Complete();
        order.Cancel("again");
        order.Complete();
        // 返回可观测值防 DCE：JIT 必须保留 5 次 RaiseEvent 的副作用
        return order.HasDomainEvents ? 1 : 0;
    }

    [Benchmark]
    public int IterateEvents_RefStructEnumerator()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("reason");

        // ITM-154 修复：返回 count 供 BDN 消费，防止死计数被 JIT 整体消除（DCE）
        var count = 0;
        foreach (var _ in order.DomainEvents())
            count++;
        return count;
    }

    [Benchmark]
    public int ClearEvents()
    {
        var order = new Order(Guid.NewGuid(), "Test");
        order.Complete();
        order.Cancel("reason");
        order.ClearDomainEvents();
        // 返回可观测值防 DCE：结果依赖 ClearDomainEvents 执行
        return order.HasDomainEvents ? 1 : 0;
    }

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
// ⚠️ 无 [ShortRunJob]：--framework 入口传显式 MediumRun InProcess ManualConfig，类级 job
// attribute 会与之 union 叠加出双 job——CsProj 工具链 job 在 net11-rc moniker 上触发
// DotNetSdkValidator 崩溃（--persist/--compression 修复先例同款，见 Program.cs 注释）
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
// ⚠️ 无 [ShortRunJob]：--framework 入口传显式 MediumRun InProcess ManualConfig，类级 job
// attribute 会与之 union 叠加出双 job——CsProj 工具链 job 在 net11-rc moniker 上触发
// DotNetSdkValidator 崩溃（--persist/--compression 修复先例同款，见 Program.cs 注释）
public class SmartEnumBenchmarks
{
    // ITM-151 补丁的补丁（2026-10-08）：静态构造器注册仍不够——FromValue/TryFromValue/All
    // 是基类继承的静态成员，调用它们只触发 SmartEnum<,> 基类的 cctor，不触发本类型 cctor
    // （cctor 语义：仅静态字段访问/实例创建/本类型声明的静态成员触发），故三个方法在
    // RegisterValues 前运行必抛「未注册任何值」（EqualsCheck 因访问 Pending 字段偶然幸免，
    // 历史产物 NA 根因）。GlobalSetup 字段访问强制触发注册，确定性先于全部基准。
    [GlobalSetup]
    public static void TouchRegistration()
        => _ = OrderStatusBench.Pending;

    [Benchmark]
    public OrderStatusBench FromValue_Known()
        => OrderStatusBench.FromValue("shipped");

    [Benchmark]
    public bool TryFromValue_Valid()
        => OrderStatusBench.TryFromValue("pending", out _);

    // 仅测量 All 属性（FrozenDictionary.Values）的访问开销——不枚举值集合；
    // 若要测量完整枚举开销，需另行对 All 执行 foreach。
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

        // ITM-151 修复：显式注册五个值——否则 FromValue/TryFromValue/All 会触基类
        // "未注册任何值"异常，四个 SmartEnum 基准必败。
        static OrderStatusBench()
        {
            RegisterValues([Pending, Shipped, Delivered, Cancelled, Refunded]);
        }

        public OrderStatusBench(string value) : base(value)
        {
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 验证结果基准 — readonly struct 零分配
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// ⚠️ 无 [ShortRunJob]：--framework 入口传显式 MediumRun InProcess ManualConfig，类级 job
// attribute 会与之 union 叠加出双 job——CsProj 工具链 job 在 net11-rc moniker 上触发
// DotNetSdkValidator 崩溃（--persist/--compression 修复先例同款，见 Program.cs 注释）
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
// ⚠️ 无 [ShortRunJob]：--framework 入口传显式 MediumRun InProcess ManualConfig，类级 job
// attribute 会与之 union 叠加出双 job——CsProj 工具链 job 在 net11-rc moniker 上触发
// DotNetSdkValidator 崩溃（--persist/--compression 修复先例同款，见 Program.cs 注释）
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
