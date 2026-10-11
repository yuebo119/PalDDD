namespace PalDDD.Messaging.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ B-6（EPSL 地毯审计 2026-10-11）：单事件派发零容器分配
// 旧码每次 DispatchAsync 都 new Queue(events) 整份拷贝 + 去重 HashSet（~250B/次）；
// 语义声明（Dispatcher remarks：Handler 不产生新入队事件）下按下标迭代等价，单事件
// 直派免两笔容器分配。多事件路径的去重行为由 MessagingTests.DuplicateEvents_Deduplicates 锁定。
// ═══════════════════════════════════════════════════════════════

public sealed class DomainEventDispatcherAllocationTests
{
    // ActivityListener 是进程级状态：并行测试的 RecordingActivityListener 会让
    // PalActivitySource 的无监听快路径失效（每次 StartActivity 真建 Activity），
    // 污染线程分配计数——串行独占（对齐 Transactions.Tests 指标测试的 NotInParallel 先例）
    [Test]
    [NotInParallel]
    public async Task DispatchAsync_SingleEvent_NoContainerAllocation()
    {
        var dispatcher = new IterativeDomainEventDispatcher(Array.Empty<IEventHandler>());
        // 事件类型无注册 handler → DispatchSingleAsync 只做 O(1) 字典 miss，无 handler 副作用
        var evt = new OrderPlaced(Guid.NewGuid(), 1m);
        Core.DomainEvent[] batch = [evt];

        dispatcher.DispatchAsync(batch).AsTask().Wait(); // 预热（JIT/状态机形态稳定）
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 10_000;
        for (var i = 0; i < iterations; i++)
        {
            // CA2012 安全形态：同步完成路径不进入阻塞等待；DispatchAsync 无 I/O 恒同步完成
            var pending = dispatcher.DispatchAsync(batch);
            if (!pending.IsCompleted)
                pending.AsTask().GetAwaiter().GetResult();
        }

        var alloc = GC.GetAllocatedBytesForCurrentThread() - baseline;
        _ = evt; // 消费事件引用防整链被 JIT 消除

        // 预算构成（分解探针实测，2026-10-11）：新码 ~580B/迭代 = 测试宿主 ActivityListener
        // 下的真实 Activity ~440B（MTP 平台监听器进程级存在；生产无监听路径为零——bench app
        // 车道 Dispatcher 154.6ns/160B 严格排除了该量级）+ Debug 构建 async 状态机类 ~140B
        //（编译器行为，Release 为 struct）。预算 700B 对新码留 ~17% 余量；旧码（+Queue 拷贝
        // +HashSet ~250B ≈ 830B）在此预算下红——锁定的回归敏感点即容器分配的重引入。
        await Assert.That(alloc).IsLessThanOrEqualTo(700L * iterations);
    }
}
