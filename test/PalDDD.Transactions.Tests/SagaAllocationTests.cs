using PalDDD.Testing;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ EPSL 地毯审计 2026-10-16 A 批 #12/#13：Saga 常态路径零/低分配
// #13 SagaTimeoutDetector.IsTimedOut 无超时路径：旧码每调用三笔分配（空 List 32B +
//     `[]` 集合表达式 32B + 接口枚举器 32B ≈ 96B/次 × 每 tick 每 saga，红证明实测），
//     新码下标循环 + 惰性 List + Array.Empty 单例，零堆分配。
// #12 Saga.cs Dynamic 车道 routeFailures 原在 try 前无条件建空 List（~32B/步，
//     R3 四车道共享骨架惰性化的漏格姊妹），新码首个失败才建。
// 行为契约不变（失败/超时列表内容与顺序一致），分配断言锁定回归。
// ═══════════════════════════════════════════════════════════════

internal sealed class SagaAllocState : SagaState;

internal sealed record SagaAllocStartEvent;

internal sealed class SagaAllocSaga : Saga<SagaAllocState>
{
    public SagaAllocSaga()
    {
        // Dynamic 步骤路由到同状态通配 work 步骤——成功路径每步必经路由分发
        var dyn = new DynamicStep("dyn", router: _ => "Initial");
        WhenDynamic<SagaAllocStartEvent>("Initial", dyn);

        When("Initial", new SagaStep("work",
            execute: (s, _, _) => ValueTask.FromResult(s),
            compensate: (_, _) => ValueTask.CompletedTask));
    }
}

/// <summary>对照组：同状态同步骤的普通（非 Dynamic）派发——吸收平台/配置相关基线
///（CI Release 与本地 Debug 的 async 状态机形态差异），使断言与运行配置无关。</summary>
internal sealed class SagaAllocPlainSaga : Saga<SagaAllocState>
{
    public SagaAllocPlainSaga()
    {
        When("Initial", new SagaStep("work",
            execute: (s, _, _) => ValueTask.FromResult(s),
            compensate: (_, _) => ValueTask.CompletedTask));
    }
}

public sealed class SagaAllocationTests
{
    private const int AllocationIterations = 10_000;

    // 逃生槽（CI run 38149487218 实证，2026-10-11）：`_ = new byte[64]` 的丢弃分配在
    // Linux Release + 分层 PGO 下可被 JIT 死存储消除——对照组测得 0 触发下限断言红
    //（Windows 不消，本地绿 CI 红的平台分叉）。静态槽写入强制分配可观测，与
    // MessageConsumeContextTests / Core.Tests AllocationContractTests 两处先例同款。
    private static byte[]? s_allocSink;

    private static long MeasureAllocation(Action action)
    {
        action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationIterations; i++)
            action();
        return GC.GetAllocatedBytesForCurrentThread() - baseline;
    }

    [Test]
    public async Task MeasureAllocation_PositiveControl_ReportsNonZero()
    {
        // 仪器自证（对齐 AllocationContractTests 先例）：裁判先证明能测出分配，
        // 否则下方零分配断言可能恒真
        var alloc = MeasureAllocation(static () => s_allocSink = new byte[64]);
        await Assert.That(alloc >= 32L * AllocationIterations).IsTrue();
    }

    [Test]
    public async Task IsTimedOut_NoTimeoutStep_ZeroAllocation()
    {
        // 无 Timeout 步骤 + Active 态（非 AWD）——超时判据首条件即短路，常态扫描路径
        var step = new SagaStep("work",
            execute: (s, _, _) => ValueTask.FromResult(s),
            compensate: (_, _) => ValueTask.CompletedTask);
        var detector = new SagaTimeoutDetector<SagaAllocState>(
            new[] { new SagaStepRegistration("Initial", EventName: null, "Initial", step) });
        var state = new SagaAllocState();
        var now = DateTimeOffset.UtcNow;

        // 闭包在 MeasureAllocation 实参求值时一次性创建（基线之前），不污染循环内计数
        var alloc = MeasureAllocation(() => _ = detector.IsTimedOut(state, now, out _));

        // 允许微量 GC 噪声（< 100B 总计）；旧码三笔分配（~96B/次 × 10k ≈ 960KB）在此
        // 预算下红——锁定的回归敏感点即常态（无超时）扫描路径分配的重引入。
        await Assert.That(alloc <= 100).IsTrue();
    }

    [Test]
    public async Task ProcessEventAsync_DynamicStep_SuccessPath_NoRouteFailureListAllocation()
    {
        var dynamicSaga = new SagaAllocSaga();
        var plainSaga = new SagaAllocPlainSaga();
        var ct = CancellationToken.None;

        // 预热（JIT + saga 内部 FrozenDictionary 成型）
        for (var i = 0; i < 50; i++)
        {
            _ = await dynamicSaga.ProcessEventAsync(new SagaAllocState(), new SagaAllocStartEvent(), ct);
            _ = await plainSaga.ProcessEventAsync(new SagaAllocState(), new SagaAllocStartEvent(), ct);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 5_000;
        for (var i = 0; i < iterations; i++)
        {
            _ = await dynamicSaga.ProcessEventAsync(new SagaAllocState(), new SagaAllocStartEvent(), ct);
            _ = await plainSaga.ProcessEventAsync(new SagaAllocState(), new SagaAllocStartEvent(), ct);
        }
        var bothAlloc = GC.GetAllocatedBytesForCurrentThread() - baseline;

        baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            _ = await plainSaga.ProcessEventAsync(new SagaAllocState(), new SagaAllocStartEvent(), ct);
        var plainAlloc = GC.GetAllocatedBytesForCurrentThread() - baseline;

        // 对照组增量设计：Dynamic 路径分配 − 普通路径分配。新码差 = 8.05B/迭代（实测标定
        // 2026-10-16：路由查表主体零分配，残余 8B 为 Dynamic 分发形态固有）；旧码差 =
        // 8.05 + 32 = 40.05B/迭代（try 前无条件空 List ×5000 = +160KB）。对照组吸收配置
        // 相关基线（CI Release 的 async 状态机为 struct、本地 Debug 为类），断言与运行
        // 配置无关。预算 = 新码实测差 +16B/迭代余量——旧码超预算判红，锁定的回归敏感点
        // 即 Dynamic 成功路径 routeFailures 空 List 的重引入。
        var dynamicAlloc = bothAlloc - plainAlloc;
        await Assert.That(dynamicAlloc - plainAlloc).IsLessThanOrEqualTo(24L * iterations);
    }
}
