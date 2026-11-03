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

    private static async Task<object?> SlowNextAsObjectAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return Guid.Empty;
    }
}
