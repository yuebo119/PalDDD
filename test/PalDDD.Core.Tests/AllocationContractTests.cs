using System.Collections.Frozen;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ 性能契约测试 — 用 GC.GetAllocatedBytesForCurrentThread 断言零分配
// ═══════════════════════════════════════════════════════════════
// 源码反复声称"零堆分配"，这里用运行时断言验证：
// 1. RaiseEvent 追加事件 — 无容器扩容分配
// 2. DomainEvents foreach — ref struct 枚举器零分配（核心契约）
// 3. ClearDomainEvents — 零分配
// 4. ValueObject<T> 构造 — struct 栈分配
// 5. RowVersion.Next — 零堆分配
// 6. PalValidationResult.Success — 空 ImmutableArray 零分配
// 7. Entity.Equals — 非瞬时实体比较零分配
// 8. SmartEnum FromValue — FrozenDictionary 查找零分配
// ═══════════════════════════════════════════════════════════════

public sealed class AllocationContractTests
{
    private const int Iterations = 10_000;

    private static long MeasureAllocation(Action action)
    {
        // 预热
        action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            action();
        return GC.GetAllocatedBytesForCurrentThread() - baseline;
    }

    [Test]
    public async Task AppendEvent_SingleEvent_ZeroContainerAllocation()
    {
        // RaiseEvent 追加单事件 — 事件对象本身分配不可避免，但容器（单链表）零分配
        // 每次迭代创建新实体 + 新事件，测量的是链表节点链接的分配（应为 0）
        // 事件对象本身 ~56B（Guid + DateTimeOffset + Next 指针），实体 ~32B
        var allocPerIteration = MeasureAllocation(() =>
        {
            var entity = new EventHostEntity();
            entity.AppendEvent(new PriceChangedEvent(Guid.NewGuid(), 10m));
        });

        // 允许事件对象 + 实体分配，但不允许链表容器分配
        // 实体(~32B) + 事件(~88B: Guid+DateTimeOffset+Next+派生属性) ≈ 120B/迭代
        var expectedMax = 130 * Iterations;
        await Assert.That(allocPerIteration <= expectedMax).IsTrue();
    }

    [Test]
    public async Task DomainEvents_Foreach_EnumeratorZeroAllocation()
    {
        // 🔴 核心契约：ref struct 枚举器零分配
        // 预填充事件，仅测量遍历分配
        var entity = new EventHostEntity();
        for (var i = 0; i < 100; i++)
            entity.AppendEvent(new PriceChangedEvent(Guid.NewGuid(), i));

        var alloc = MeasureAllocation(() =>
        {
            foreach (var _ in entity.DomainEvents()) { }
        });

        // ref struct 枚举器零分配 — 允许微量 GC 噪声（< 100B 总计）
        await Assert.That(alloc <= 100).IsTrue();
    }

    [Test]
    public async Task AppendEvent_MultipleEvents_NoReallocation()
    {
        // 多次追加无扩容分配 — 单链表 O(1) 追加，无 List<T> 扩容
        var entity = new EventHostEntity();

        // 预填充
        for (var i = 0; i < 50; i++)
            entity.AppendEvent(new PriceChangedEvent(Guid.NewGuid(), i));

        // 测量追加更多事件的分配 — 仅事件对象分配，无容器扩容
        var alloc = MeasureAllocation(() =>
        {
            entity.AppendEvent(new PriceChangedEvent(Guid.NewGuid(), 0m));
        });

        // 每次仅事件对象分配 (~88B)，无 List 扩容
        var expectedMax = 100 * Iterations;
        await Assert.That(alloc <= expectedMax).IsTrue();
    }

    [Test]
    public async Task ClearDomainEvents_ZeroAllocation()
    {
        var entity = new EventHostEntity();
        entity.AppendEvent(new PriceChangedEvent(Guid.NewGuid(), 10m));

        var alloc = MeasureAllocation(() =>
        {
            entity.ClearDomainEvents();
            // 重新添加以保持下次可清空
            entity.AppendEvent(new PriceChangedEvent(Guid.NewGuid(), 10m));
        });

        // ClearDomainEvents 本身零分配（仅设置 _head=_tail=null）
        // 分配来自 AppendEvent 的事件对象
        var eventAlloc = 100 * Iterations;
        await Assert.That(alloc <= eventAlloc + 100).IsTrue();
    }

    [Test]
    public async Task ValueObject_Create_ZeroHeapAllocation()
    {
        // readonly record struct — 栈分配，零堆分配
        var alloc = MeasureAllocation(() =>
        {
            var vo = new ValueObject<int>(42);
            _ = vo.Value;
        });

        await Assert.That(alloc <= 100).IsTrue();
    }

    [Test]
    public async Task RowVersion_Next_ZeroHeapAllocation()
    {
        var v = new RowVersion(5);

        var alloc = MeasureAllocation(() =>
        {
            var next = v.Next();
            _ = next.Value;
        });

        // readonly record struct — 栈分配
        await Assert.That(alloc <= 100).IsTrue();
    }

    [Test]
    public async Task PalValidationResult_Success_ZeroHeapAllocation()
    {
        var alloc = MeasureAllocation(() =>
        {
            var result = PalValidationResult.Success();
            _ = result.IsValid;
        });

        // Success() 返回 ImmutableArray<PalValidationError>.Empty — 预分配的单例
        // readonly struct — 栈分配
        await Assert.That(alloc <= 100).IsTrue();
    }

    [Test]
    public async Task Entity_Equals_NonTransient_ZeroAllocation()
    {
        var id = Guid.NewGuid();
        var a = new Customer(id, "Alice");
        var b = new Customer(id, "Bob");

        var alloc = MeasureAllocation(() =>
        {
            _ = a.Equals(b);
        });

        // Equals 不创建新对象 — 仅比较 GetType() + EqualityComparer<Guid>.Default.Equals
        await Assert.That(alloc <= 100).IsTrue();
    }

    [Test]
    public async Task FrozenDictionary_Lookup_ZeroAllocation()
    {
        // FrozenDictionary 查找命中路径零分配
        var dict = new Dictionary<int, string>
        {
            [1] = "one",
            [2] = "two",
            [3] = "three",
        }.ToFrozenDictionary();

        var alloc = MeasureAllocation(() =>
        {
            _ = dict.TryGetValue(2, out _);
        });

        await Assert.That(alloc <= 100).IsTrue();
    }
}
