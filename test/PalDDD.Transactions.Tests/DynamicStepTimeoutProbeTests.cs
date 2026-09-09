using PalDDD.Core.Logging;
using PalDDD.Testing;

namespace PalDDD.Transactions.Tests;

// ══════════════════════════════════════════════════════════════
// V25 探针 c — Saga Dynamic 路由步骤 Timeout 的消费方式
// ══════════════════════════════════════════════════════════════
// 事实探针：DynamicStep 构造函数不接收 timeout（SagaStep.Timeout 为 init
// 属性，经对象初始化器配置）。本文件构造超期场景并记录事实行为：
// 超时是否触发补偿/状态迁移，还是静默滞留。
// 超期构造方式对齐既有 SagaTimeoutProcessor 测试先例
// （CheckTimeouts_CompensatedInterrupt_LateDecisionFailsVisibly）：
// ProcessEventAsync 后回拨 SagaState.StepStartedAt 时间戳。
// 本文件不对"应该怎样"下结论——断言均为事实断言。

public sealed class DynamicProbeState : SagaState
{
}

public sealed record DynamicProbeStartEvent;

public sealed class DynamicStepTimeoutProbeTests
{
    /// <summary>
    /// 探针用 Saga：Start 状态下 Dynamic 步骤路由到通配 work 步骤（key "Start"）。
    /// Dynamic 步骤注册键为 "Start|DynamicProbeStartEvent"（事件精确匹配）。
    /// </summary>
    private sealed class DynamicTimeoutSaga : Saga<DynamicProbeState>
    {
        /// <summary>补偿动作调用记录（按调用顺序）</summary>
        public List<string> CompensationLog { get; } = [];

        public DynamicTimeoutSaga(TimeSpan? dynamicTimeout, TimeSpan? workTimeout, bool workMovesStateAway)
        {
            var dyn = new DynamicStep(
                "dyn",
                router: _ => "Start",
                compensate: (_, _) =>
                {
                    CompensationLog.Add("dyn");
                    return ValueTask.CompletedTask;
                })
            { Timeout = dynamicTimeout };
            WhenDynamic<DynamicProbeStartEvent>("Start", dyn);

            var work = new SagaStep(
                "work",
                execute: (s, _, _) =>
                {
                    if (workMovesStateAway)
                        s.CurrentState = "Next";
                    return new ValueTask<SagaState>(s);
                },
                compensate: (_, _) =>
                {
                    CompensationLog.Add("work");
                    return ValueTask.CompletedTask;
                },
                timeout: workTimeout);
            When("Start", work);
        }
    }

    private static SagaTimeoutProcessor<DynamicProbeState> BuildProcessor(
        InMemorySagaStateStore<DynamicProbeState> store, Saga<DynamicProbeState> saga)
        => new(
            store,
            saga,
            NullPalLogger<SagaTimeoutProcessor<DynamicProbeState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions { TimeoutScanBatchSize = 64 }),
            TimeProvider.System);

    private static void BackdateStepStartedAt(DynamicProbeState state)
    {
        foreach (var key in state.StepStartedAt.Keys.ToList())
            state.StepStartedAt[key] = DateTimeOffset.UtcNow.AddMinutes(-5);
    }

    // 补偿成功路径会递增进程级 Counter paldd.saga.compensated——对齐项目约定
    // （TransactionsTests 指标断言均为 [NotInParallel] 串行独占测量流）
    [Test]
    [NotInParallel]
    public async Task CheckTimeouts_DynamicStepTimeoutElapsedInSameState_TriggersCompensation(CancellationToken ct)
    {
        // 场景：Dynamic 步骤配 Timeout，路由目标执行后 Saga 停留在原状态
        // （StepStartedAt 残留时间戳超期）——记录快照滞留检测的命中事实。
        var saga = new DynamicTimeoutSaga(
            dynamicTimeout: TimeSpan.FromMinutes(1), workTimeout: null, workMovesStateAway: false);
        var state = new DynamicProbeState { CurrentState = "Start" };

        await saga.ProcessEventAsync(state, new DynamicProbeStartEvent(), ct);

        // 前置事实：派发完成后两个步骤键均被记录（超时检测的输入）
        await Assert.That(state.Status).IsEqualTo(SagaStatus.Active);
        await Assert.That(state.CurrentState).IsEqualTo("Start");
        await Assert.That(saga.CompensationLog).IsEmpty();
        await Assert.That(state.StepStartedAt.ContainsKey("Start|DynamicProbeStartEvent")).IsTrue();
        await Assert.That(state.StepStartedAt.ContainsKey("Start")).IsTrue();
        await Assert.That(state.ExecutedStepKeys.Contains("Start|DynamicProbeStartEvent")).IsTrue();
        await Assert.That(state.ExecutedStepKeys.Contains("Start")).IsTrue();

        // 回拨时间戳构造超期
        BackdateStepStartedAt(state);
        var probeStart = DateTimeOffset.UtcNow;
        var timedOut = saga.IsTimedOut(state, DateTimeOffset.UtcNow, out var timedOutSteps);

        // 事实断言：检测器命中，且命中的是 Dynamic 步骤本身
        await Assert.That(timedOut).IsTrue();
        await Assert.That(timedOutSteps.Count).IsEqualTo(1);
        await Assert.That(timedOutSteps[0].Name).IsEqualTo("dyn");

        // 端到端事实：经 SagaTimeoutProcessor 走真路径——补偿执行 + 终态迁移
        var store = new InMemorySagaStateStore<DynamicProbeState>();
        store.Add(state);
        await BuildProcessor(store, saga).CheckTimeoutsAsync(ct);

        var persisted = await store.GetByIdAsync(state.SagaId, ct);
        await Assert.That(persisted!.Status).IsEqualTo(SagaStatus.Compensated);
        await Assert.That(persisted.CurrentState).IsEqualTo(SagaState.CompensatedStateName);
        // 行为断言：终态时间戳被真实写入且不早于探针起点（替代 IsNotNull 弱断言）
        await Assert.That(persisted.CompletedAt!.Value).IsGreaterThan(probeStart);
        // 事实断言：补偿按 ExecutedStepKeys 执行序逆序回放（Backward 默认）——
        // 后执行的 work 先补偿，Dynamic 入口后补偿
        await Assert.That(string.Join(",", saga.CompensationLog)).IsEqualTo("work,dyn");
    }

    [Test]
    public async Task CheckTimeouts_DynamicStepTimeoutElapsedAfterStateMoved_LingersSilently(CancellationToken ct)
    {
        // 场景：同上，但路由目标把 CurrentState 移到 "Next"——记录
        // 残留时间戳与状态名不再匹配时检测器的行为事实。
        var saga = new DynamicTimeoutSaga(
            dynamicTimeout: TimeSpan.FromMinutes(1), workTimeout: null, workMovesStateAway: true);
        var state = new DynamicProbeState { CurrentState = "Start" };

        await saga.ProcessEventAsync(state, new DynamicProbeStartEvent(), ct);
        await Assert.That(state.CurrentState).IsEqualTo("Next");

        BackdateStepStartedAt(state);
        var timedOut = saga.IsTimedOut(state, DateTimeOffset.UtcNow, out var timedOutSteps);

        // 事实断言：状态迁移后 Dynamic 步骤的残留超期时间戳不命中检测
        await Assert.That(timedOut).IsFalse();
        await Assert.That(timedOutSteps.Count).IsEqualTo(0);

        // 端到端事实：扫描器不补偿、状态滞留原样
        var store = new InMemorySagaStateStore<DynamicProbeState>();
        store.Add(state);
        await BuildProcessor(store, saga).CheckTimeoutsAsync(ct);

        var persisted = await store.GetByIdAsync(state.SagaId, ct);
        await Assert.That(persisted!.Status).IsEqualTo(SagaStatus.Active);
        await Assert.That(persisted.CurrentState).IsEqualTo("Next");
        await Assert.That(saga.CompensationLog).IsEmpty();
    }

    [Test]
    public async Task IsTimedOut_RoutedStepOwnTimeoutElapsed_DetectedOnRoutedStepKey(CancellationToken ct)
    {
        // 探针自证（控制组）：Dynamic 步骤不配 Timeout、路由目标自配 Timeout——
        // 同一装置下检测器命中路由目标步骤，证明检测器装置有效，
        // 且路由步骤的 Timeout 经其自身注册键独立消费。
        var saga = new DynamicTimeoutSaga(
            dynamicTimeout: null, workTimeout: TimeSpan.FromMinutes(1), workMovesStateAway: false);
        var state = new DynamicProbeState { CurrentState = "Start" };

        await saga.ProcessEventAsync(state, new DynamicProbeStartEvent(), ct);
        await Assert.That(state.CurrentState).IsEqualTo("Start");

        BackdateStepStartedAt(state);
        var timedOut = saga.IsTimedOut(state, DateTimeOffset.UtcNow, out var timedOutSteps);

        // 事实断言：命中路由目标步骤（"work"），而非 Dynamic 入口步骤
        await Assert.That(timedOut).IsTrue();
        await Assert.That(timedOutSteps.Count).IsEqualTo(1);
        await Assert.That(timedOutSteps[0].Name).IsEqualTo("work");
    }
}
