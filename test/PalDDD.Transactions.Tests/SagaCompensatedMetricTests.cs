using PalDDD.Core.Logging;
using PalDDD.Testing;
using PalDDD.Transactions;

namespace PalDDD.Transactions.Tests;

// ─────────────────────────────────────────────────────────────
// 2026-11-07 审计 P2-A：SagaCompensated 计数单一发射点锁定
// ─────────────────────────────────────────────────────────────
//
// 机制：SagaCompensation.RunAsync 在补偿成功时计一次（重试耗尽/故障车道走此点）；
// 超时车道经 CompensateAllAsync → RunAsync 也走该点，随后 SagaProcessor.LogTerminalResult
// 再按 compensationSucceeded 计一次——同一补偿双重计数。重试耗尽车道不经 LogTerminalResult，
// 故两条车道口径分叉（修复前超时车道 2、重试耗尽车道 1）。
// 修复：LogTerminalResult 移除计数，计数单一发射点留在 RunAsync。
//
// 两个用例都断言「一次补偿 = 一次计数」：超时车道要求 ExecutedStepKeys 非空（否则
// CompensateAllAsync 的 targets 为空，RunAsync 直接返回，两车道都不会计数）。

/// <summary>SagaCompensated 指标的两车道计数口径锁定（审计 P2-A）。</summary>
public sealed class SagaCompensatedMetricTests
{
    // 指标断言依赖进程级 Meter 广播——串行运行以独占测量流，精确计数不被并行测试污染。
    [Test]
    [NotInParallel]
    public async Task TimeoutLaneWithExecutedSteps_CountsOnce()
    {
        using var listener = new RecordingMeterListener("paldd.saga.compensated");
        var state = new OrderSagaState
        {
            CurrentState = "Waiting"
        };
        state.StepStartedAt["Waiting|OrderPlacedSagaEvent"] = DateTimeOffset.UnixEpoch;
        // 非空 ExecutedStepKeys：超时补偿才会真正执行步骤补偿并落到 RunAsync 计数点。
        state.ExecutedStepKeys.Add("Waiting|OrderPlacedSagaEvent");
        var store = new SagaTimeoutProcessorTests.RecordingSagaStateStore([state]);
        var processor = new SagaTimeoutProcessor<OrderSagaState>(
            store,
            new SagaTimeoutProcessorTests.TimedOutSaga(),
            NullPalLogger<SagaTimeoutProcessor<OrderSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions()),
            TimeProvider.System);

        await processor.CheckTimeoutsAsync(CancellationToken.None);

        await Assert.That(listener.Measurements).Count().IsEqualTo(1)
            .Because("一次超时补偿只能计一次 SagaCompensated——RunAsync 与 LogTerminalResult 不得双重计数");
    }

    [Test]
    [NotInParallel]
    public async Task RetryExhaustedLane_CountsOnce()
    {
        using var listener = new RecordingMeterListener("paldd.saga.compensated");
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Step1");
        saga.PublicWhen("Step1", "FailingStep", shouldFail: true);

        var state = new TestSagaState { CurrentState = "Initial" };
        state = await saga.ProcessEventAsync(state, new object());

        // Step1 成功后 FailingStep 重试耗尽 → 补偿 Step1（走 RunAsync 计数点）。
        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(listener.Measurements).Count().IsEqualTo(1)
            .Because("一次重试耗尽补偿只能计一次 SagaCompensated");
    }
}
