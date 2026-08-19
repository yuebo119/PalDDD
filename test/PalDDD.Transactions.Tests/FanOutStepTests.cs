// ─────────────────────────────────────────────────────────────
// FanOutStep 专项测试 — 超时/异常/并发路径独立验证
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Transactions.Tests;

/// <summary>
/// FanOutStep 超时与异常收集行为测试。
/// 覆盖 ITM-001（PerItemTimeout 超时子任务静默丢失缺陷）的复现与修复验证。
/// </summary>
public class FanOutStepTimeoutTests
{
    /// <summary>
    /// 复现 ITM-001：配置 PerItemTimeout 且子任务超时，
    /// 超时的子任务应出现在 Failed 中（而非静默丢失）。
    /// 修复前此测试失败（Failed 为空，超时项丢失）。
    /// </summary>
    [Test]
    public async Task PerItemTimeout_SlowTask_AppearsInFailedNotSilentlyDropped()
    {
        // Arrange：构造一个 FanOutStep，其中一项故意慢于 PerItemTimeout
        var step = new FanOutStep<string, string>(
            key: "fanout-timeout-test",
            selector: _ => new List<string> { "fast", "slow", "fast2" },
            executor: async (item, ct) =>
            {
                if (item == "slow")
                {
                    // 模拟慢任务：等待远超 PerItemTimeout
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }
                return item + "-done";
            },
            compensate: null,
            timeout: null)
        {
            MaxConcurrency = 4,
            PerItemTimeout = TimeSpan.FromMilliseconds(50)
        };

        var sagaState = new TestFanOutSagaState();

        // Act
        var result = await step.ExecuteFanOutAsync(sagaState, CancellationToken.None);

        // Assert：超时项必须在 Failed 中（修复前会被静默丢弃）
        await Assert.That(result.Failed.Count).IsEqualTo(1);
        await Assert.That(result.Failed[0].Item).IsNull();
        // 超时转化的异常类型应为 TimeoutException
        await Assert.That(result.Failed[0].Error).IsTypeOf<TimeoutException>();

        // 快任务正常完成
        await Assert.That(result.Completed.Count).IsEqualTo(2);
    }

    /// <summary>
    /// 正常路径：无 PerItemTimeout 时所有子任务完成。
    /// </summary>
    [Test]
    public async Task NoTimeout_AllItems_CompleteSuccessfully()
    {
        var step = new FanOutStep<int, int>(
            key: "fanout-normal",
            selector: _ => new List<int> { 1, 2, 3 },
            executor: (item, _) => ValueTask.FromResult(item * 2))
        {
            MaxConcurrency = 4
        };

        var sagaState = new TestFanOutSagaState();
        var result = await step.ExecuteFanOutAsync(sagaState, CancellationToken.None);

        await Assert.That(result.Completed.Count).IsEqualTo(3);
        await Assert.That(result.AllSucceeded).IsTrue();
    }

    /// <summary>
    /// 业务异常（非取消）正确收集到 Failed。
    /// </summary>
    [Test]
    public async Task ExecutorThrows_ExceptionCollectedInFailed()
    {
        var step = new FanOutStep<string, string>(
            key: "fanout-throw",
            selector: _ => new List<string> { "ok", "bad" },
            executor: (item, _) =>
            {
                if (item == "bad")
                    throw new InvalidOperationException("business error");
                return ValueTask.FromResult(item);
            })
        { MaxConcurrency = 4 };

        var sagaState = new TestFanOutSagaState();
        var result = await step.ExecuteFanOutAsync(sagaState, CancellationToken.None);

        await Assert.That(result.Failed.Count).IsEqualTo(1);
        await Assert.That(result.Failed[0].Error).IsTypeOf<InvalidOperationException>();
        await Assert.That(result.Completed.Count).IsEqualTo(1);
    }

    /// <summary>
    /// 外部取消（非 PerItemTimeout）正确传播，不转为 TimeoutException。
    /// </summary>
    [Test]
    public async Task ExternalCancellation_PropagatesNotAsTimeout()
    {
        using var cts = new CancellationTokenSource();
        var step = new FanOutStep<string, string>(
            key: "fanout-cancel",
            selector: _ => new List<string> { "a", "b", "c" },
            executor: async (item, ct) =>
            {
                if (item == "b")
                {
                    cts.Cancel(); // 外部取消
                    await Task.Delay(100, ct);
                }
                return item;
            })
        {
            MaxConcurrency = 1, // 串行确保 b 在 a 之后
            PerItemTimeout = TimeSpan.FromMilliseconds(2000) // 远大于测试时间
        };

        var sagaState = new TestFanOutSagaState();

        // 外部取消应正常传播 OperationCanceledException，不静默也不转 TimeoutException
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await step.ExecuteFanOutAsync(sagaState, cts.Token));
    }
}

internal sealed class TestFanOutSagaState : SagaState
{
    public string Payload { get; set; } = "";
}
