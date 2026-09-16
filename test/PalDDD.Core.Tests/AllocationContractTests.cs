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
// （TST-111：原第 8 项 FrozenDictionary 查找零分配测的是 BCL 行为，
// 与产品路径零关联——已删；SmartEnum FrozenDictionary 零反射由 AotContractTests 承载）
// ═══════════════════════════════════════════════════════════════

public sealed class AllocationContractTests
{
    private const int Iterations = 10_000;

    /// <summary>遍历计数落点（观测点）：使 foreach 的结果可被循环外的断言读取，
    /// 从而切断 JIT 消除整段遍历的路径——见 DomainEvents_Foreach_EnumeratorZeroAllocation。</summary>
    private static volatile int s_enumeratedSink;

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

    /// <summary>仪器自证（全仓扫描修复）：<see cref="MeasureAllocation"/> 是本文件**所有**断言的
    /// 唯一裁判，却从未被验证过——若它因任何原因恒返回 0（运行时/GC 模式不符、计数器被重置、
    /// 动作被搬到别的线程），下面每一条"零分配"断言都会变成**恒真**，整个套件在真实回归上仍全绿。
    /// 本用例要求它对"必然分配"的动作给出非零且量级正确的读数——仪器能测出坏输入，其读数才可信。</summary>
    [Test]
    public async Task MeasureAllocation_PositiveControl_ReportsNonZero()
    {
        var alloc = MeasureAllocation(static () => _ = new byte[64]);

        await Assert.That(alloc > 0).IsTrue();
        // 量级校验：64B × Iterations 的下界（防把"读到一个极小噪声值"误当成通过）
        await Assert.That(alloc >= 32L * Iterations).IsTrue();
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
        // 预算语义（刻意）：上界含安全余量（预估 120B/预算 130B 形态）——只拦截大幅回归
        // （如误引入 List 扩容或装箱），精确预算由 benchmark（bench/）承载，不在此收紧
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
            var seen = 0;
            foreach (var _ in entity.DomainEvents())
                seen++;
            s_enumeratedSink = seen;
        });

        // 仪器自证（全仓扫描修复）：原循环体为空（`{ }`），没有可观测效果——而
        // DomainEventEnumerable/枚举器正是易被内联的 ref struct 形态，JIT 有权把整段遍历
        // 消除，于是 `alloc <= 100` 在**从未真正遍历**的情况下也恒真（核心契约形同未测）。
        // 现改为：循环体计数并经静态观测点写出，再由本断言读回——遍历结果成为可观测值，
        // 消除路径被切断，且"确实遍历了全部 100 个事件"被显式证明。
        await Assert.That(s_enumeratedSink).IsEqualTo(100);

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
}
