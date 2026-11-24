// ─────────────────────────────────────────────────────────────
// 🧪 Saga 超时告警单一发射点（R17 审计发现 D3）
// ─────────────────────────────────────────────────────────────
// 修复前 SagaProcessor.CheckTimeoutsAsync 按 timedOutSteps 逐条记 SagaTimedOut
// 告警后，又把同一列表传入 CompensateTimedOutSagaAsync——后者 ITM-709 抽取时复制了
// 同一循环（原文自称「逐字迁移」，实为复制未删调用方），N 个超时步发 2N 条告警。
// 本测试用记录 logger 断言「单步超时恰好 1 条 SagaTimedOut」。

using Microsoft.Extensions.Logging;
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using PalDDD.Testing;
using PalDDD.Transactions;

namespace PalDDD.Transactions.Tests;

/// <summary>记录 SagaTimedOut 告警条数的 logger 替身。</summary>
internal sealed class TimeoutWarningRecordingLogger : IPalLogger<SagaTimeoutProcessor<OrderSagaState>>
{
    public int TimedOutWarningCount { get; private set; }

    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message)
    {
        if (message.Contains("timed out at state", StringComparison.Ordinal))
            TimedOutWarningCount++;
    }
    public void Error(Exception exception, string message) { }
    public bool IsEnabled(LogLevel level) => true;
}

public sealed class SagaTimeoutWarningTests
{
    /// <summary>
    /// 单步超时恰好发 1 条 SagaTimedOut 告警（告警按 EventId 过滤的规则计数翻倍问题）。
    /// 复用 SagaCompensatedMetricTests 的 state 构造（ExecutedStepKeys 非空使补偿真正执行）。
    /// </summary>
    [Test]
    public async Task SingleTimedOutStep_EmitsExactlyOneWarning()
    {
        var state = new OrderSagaState { CurrentState = "Waiting" };
        state.StepStartedAt["Waiting|OrderPlacedSagaEvent"] = DateTimeOffset.UnixEpoch;
        state.ExecutedStepKeys.Add("Waiting|OrderPlacedSagaEvent");

        var store = new SagaTimeoutProcessorTests.RecordingSagaStateStore([state]);
        var logger = new TimeoutWarningRecordingLogger();
        var processor = new SagaTimeoutProcessor<OrderSagaState>(
            store,
            new SagaTimeoutProcessorTests.TimedOutSaga(),
            logger,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions()),
            TimeProvider.System);

        await processor.CheckTimeoutsAsync(CancellationToken.None);

        await Assert.That(logger.TimedOutWarningCount).IsEqualTo(1)
            .Because("单步超时只能发 1 条 SagaTimedOut（修前调用方 + 抽取方法双发，2 条）");
    }
}
