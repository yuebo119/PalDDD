using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔁 RetryExecutor 回归锁（2026-11-01）
// ═══════════════════════════════════════════════════════════════
// 语义边界：总尝试 = MaxRetries + 1；耗尽原样重抛最后一次异常；
// 调用方取消不计入重试；委托自身超时（ct 未取消）可重试；
// 退避策略缺陷降级 1 秒后继续。与 Outbox/Saga 的持久化重试是两套语义，
// 本组只锁进程内行为。

public sealed class RetryExecutorTests
{
    private static RetryOptions FastOptions(int maxRetries = 3) => new()
    {
        MaxRetries = maxRetries,
        BackoffPolicy = new FixedBackoffPolicy(TimeSpan.Zero),
    };

    [Test]
    public async Task ExecuteAsync_FirstAttemptSucceeds_NoRetry()
    {
        var calls = 0;
        var result = await RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return ValueTask.FromResult(42);
            },
            FastOptions());

        await Assert.That(result).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_TransientFailures_RetriesThenSucceeds()
    {
        var calls = 0;
        var retried = new List<int>();
        var options = FastOptions(maxRetries: 3);
        options.OnRetry = (_, attempt) => retried.Add(attempt);

        var result = await RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls < 3
                    ? ValueTask.FromException<int>(new TimeoutException($"try {calls}"))
                    : ValueTask.FromResult(7);
            },
            options);

        await Assert.That(result).IsEqualTo(7);
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(retried.Count).IsEqualTo(2);
        await Assert.That(retried[0]).IsEqualTo(1);
        await Assert.That(retried[1]).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_Exhausted_RethrowsLastException()
    {
        // MaxRetries=2 → 总尝试 3 次；耗尽后应原样重抛最后一次异常（不包装）。
        var calls = 0;
        var caught = await CaptureAsync(() => RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return ValueTask.FromException(new TimeoutException($"try {calls}"));
            },
            FastOptions(maxRetries: 2)).AsTask());

        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.GetType()).IsEqualTo(typeof(TimeoutException));
        await Assert.That(caught.Message).IsEqualTo("try 3");
    }

    [Test]
    public async Task ExecuteAsync_ShouldRetryFalse_StopsImmediately()
    {
        var calls = 0;
        var options = FastOptions(maxRetries: 5);
        options.ShouldRetry = _ => false;

        var caught = await CaptureAsync(() => RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return ValueTask.FromException(new ArgumentException("business"));
            },
            options).AsTask());

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(caught is ArgumentException).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_CallerCancellation_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = 0;

        var caught = await CaptureAsync(() => RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return ValueTask.CompletedTask;
            },
            FastOptions(),
            cancellationToken: cts.Token).AsTask());

        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(caught is OperationCanceledException).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_TimeoutWithoutCallerCancellation_IsRetried()
    {
        // 委托自身超时抛 TaskCanceledException（派生 OCE），调用方未取消 → 属瞬时失败，应重试。
        var calls = 0;
        var result = await RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls == 1
                    ? ValueTask.FromException<int>(new TaskCanceledException("delegate timeout"))
                    : ValueTask.FromResult(11);
            },
            FastOptions(maxRetries: 1));

        await Assert.That(result).IsEqualTo(11);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_BackoffPolicyThrows_FallsBackAndContinues()
    {
        // 退避策略缺陷不得中断重试路径：降级 1 秒后继续（本用例真实等待 1 秒）。
        var calls = 0;
        var options = new RetryOptions
        {
            MaxRetries = 1,
            BackoffPolicy = new ThrowingBackoffPolicy(),
        };

        var result = await RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls == 1
                    ? ValueTask.FromException<int>(new TimeoutException())
                    : ValueTask.FromResult(9);
            },
            options);

        await Assert.That(result).IsEqualTo(9);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_MaxRetriesZero_SingleAttempt()
    {
        var calls = 0;
        var caught = await CaptureAsync(() => RetryExecutor.ExecuteAsync(
            _ =>
            {
                calls++;
                return ValueTask.FromException(new TimeoutException());
            },
            FastOptions(maxRetries: 0)).AsTask());

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(caught is TimeoutException).IsTrue();
    }

    /// <summary>捕获任意异常（重试执行器的失败语义是异常原样传播，断言需要看到具体实例）。</summary>
    [SuppressMessage("Design", "CA1031",
        Justification = "测试断言必须捕获任意异常以验证原样传播与耗尽语义；捕获后不吞掉，返回给调用方断言。")]
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class ThrowingBackoffPolicy : IRetryBackoffPolicy
    {
        public TimeSpan ComputeDelay(int attempt)
            => throw new InvalidOperationException("backoff policy defect");
    }
}
