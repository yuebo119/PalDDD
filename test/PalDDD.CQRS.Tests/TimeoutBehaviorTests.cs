namespace PalDDD.CQRS.Tests;

// ═══════════════════════════════════════════════════════════════
// TimeoutBehavior 契约：
//   1. 快路径（next 同步完成）零等待透传
//   2. 超时转 TimeoutException，消息含请求类型名
//   3. 外部取消透传 OperationCanceledException，不误判为超时
//   4. 非泛型桥接路径语义一致
//   5. 构造守卫拒绝非正超时
// 计时仅用于制造"慢于超时"的条件，不作为断言对象（T21 禁墙钟断言）。
// ═══════════════════════════════════════════════════════════════

public sealed class TimeoutBehaviorTests
{
    private static TimeoutBehavior<CreateOrderCommand, Guid> CreateBehavior(TimeSpan timeout)
        => new(new PalTimeoutBehaviorOptions(timeout));

    private static ValueTask<Guid> SlowNext(CancellationToken ct = default)
        => SlowNextAsync(TimeSpan.FromSeconds(2), ct);

    private static async ValueTask<Guid> SlowNextAsync(TimeSpan delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
        return Guid.Empty;
    }

    [Test]
    public async Task FastNext_ReturnsResultWithoutTimeout()
    {
        var behavior = CreateBehavior(TimeSpan.FromMilliseconds(50));
        var expected = Guid.NewGuid();

        var result = await behavior.HandleAsync(
            new CreateOrderCommand("fast", 1m), CancellationToken.None, () => ValueTask.FromResult(expected));

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task SlowNext_ThrowsTimeoutException()
    {
        var behavior = CreateBehavior(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await behavior.HandleAsync(
                new CreateOrderCommand("slow", 1m), CancellationToken.None,
                () => SlowNext()));

        // 消息必须点名请求类型，否则并发排障时分不清哪条命令超时
        await Assert.That(ex?.Message ?? string.Empty).Contains(nameof(CreateOrderCommand));
    }

    [Test]
    public async Task SlowNext_ExternalCancellation_PropagatesOperationCanceled()
    {
        var behavior = CreateBehavior(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await behavior.HandleAsync(
                new CreateOrderCommand("cancel", 1m), cts.Token, () => SlowNext()));

        // 外部取消不能被包装成 TimeoutException——两者在上层是不同语义（499 vs 504）。
        // TimeoutException 不继承 OperationCanceledException，能进入本分支即已排除超时路径。
        await Assert.That(ex?.Message ?? string.Empty).IsNotEmpty();
    }

    [Test]
    public async Task NonGenericBridge_FastNext_ReturnsBoxedResult()
    {
        var behavior = CreateBehavior(TimeSpan.FromMilliseconds(50));
        var expected = Guid.NewGuid();
        IPipelineBehavior nonGeneric = behavior;

        var result = await nonGeneric.HandleAsync(
            new CreateOrderCommand("fast", 1m), CancellationToken.None, () => ValueTask.FromResult<object?>(expected));

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task NonGenericBridge_SlowNext_ThrowsTimeoutException()
    {
        var behavior = CreateBehavior(TimeSpan.FromMilliseconds(50));
        IPipelineBehavior nonGeneric = behavior;

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await nonGeneric.HandleAsync(
                new CreateOrderCommand("slow", 1m), CancellationToken.None,
                () => new ValueTask<object?>(SlowNextAsObjectAsync())));
    }

    [Test]
    public void Constructor_ZeroTimeout_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new TimeoutBehavior<CreateOrderCommand, Guid>(new PalTimeoutBehaviorOptions(TimeSpan.Zero)));

    [Test]
    public void Constructor_NegativeTimeout_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new TimeoutBehavior<CreateOrderCommand, Guid>(
                new PalTimeoutBehaviorOptions(TimeSpan.FromSeconds(-1))));

    /// <summary>
    /// v4 批 B（B4 阶段 1）：504 时序必须不变——超时抛出不得等待 abandoned handler 收尾。
    /// 这是阶段 1 的「不修根因」契约边界：任何后续尝试在管道内等 abandoned 收尾都会
    /// 把该断言推红（v3 形态实测 118ms → 505ms）。
    /// </summary>
    /// <remarks>
    /// 计时只用于上界守卫（≤ timeout + 50ms 余量），不作精确断言（T21 禁墙钟断言）。
    /// </remarks>
    [Test]
    public async Task SlowNext_TimeoutDoesNotWaitForAbandonedHandler()
    {
        var timeout = TimeSpan.FromMilliseconds(50);
        var behavior = CreateBehavior(timeout);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await behavior.HandleAsync(
                new CreateOrderCommand("slow", 1m), CancellationToken.None,
                async () =>
                {
                    // abandoned handler 远慢于超时：若管道等它收尾，上界断言必红
                    await abandoned.Task.ConfigureAwait(false);
                    return Guid.Empty;
                }));
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);

        await Assert.That(ex).IsNotNull();
        await Assert.That(elapsed).IsLessThanOrEqualTo(timeout + TimeSpan.FromMilliseconds(50));

        abandoned.TrySetResult();
    }

    /// <summary>
    /// v4 批 B（独立 P3 可观测项）：abandoned handler 失败必须记
    /// <c>pal.timeout.abandoned-handler-fault</c> Activity 事件——修复前完全静默。
    /// 该用例只锁「可观测」，不锁任何行为变更。
    /// </summary>
    [Test]
    public async Task SlowNext_AbandonedHandlerFault_IsRecordedAsActivityEvent()
    {
        var behavior = CreateBehavior(TimeSpan.FromMilliseconds(50));
        var faultRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == PalDDD.Core.Diagnostics.PalActivitySource.Name,
            Sample = static (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var activity = new System.Diagnostics.Activity("test-root").SetIdFormat(
            System.Diagnostics.ActivityIdFormat.W3C).Start();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await behavior.HandleAsync(
                new CreateOrderCommand("slow", 1m), CancellationToken.None,
                async () =>
                {
                    // 必须晚于 timeout 才失败：否则异常会在 WaitAsync 之前就逃逸，
                    // 走不到超时分支，ObserveFault 也不会被调用
                    await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
                    faultRaised.TrySetResult();
                    throw new InvalidOperationException("abandoned handler fault (test)");
                }));

        // ObserveFault 的续体在异常任务上同步执行；等待事件落地给足余量
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var recorded = false;
        while (DateTime.UtcNow < deadline && !recorded)
        {
            recorded = activity.Events.Any(static e => e.Name == "pal.timeout.abandoned-handler-fault");
            if (!recorded)
                await Task.Delay(10);
        }

        await faultRaised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(recorded).IsTrue();
    }

    private static async Task<object?> SlowNextAsObjectAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return Guid.Empty;
    }
}
