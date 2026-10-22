using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔔 Outbox 唤醒信号（2026-10-22 性能轮）
// ═══════════════════════════════════════════════════════════════
// 覆盖三层：
// 1. 信号原语语义（通知/超时/合并/计时器上限钳制）
// 2. InMemory Store 写入后通知
// 3. AddPalOutbox 注册信号单例（DI 可替换）
// 循环层行为（信号唤醒不需要等间隔、空租自愈）见 OutboxProcessorTests。
// ═══════════════════════════════════════════════════════════════

public sealed class OutboxWakeUpSignalTests
{
    [Test]
    public async Task Notify_ThenWait_ReturnsTrue()
    {
        var signal = new OutboxWakeUpSignal();
        signal.Notify();

        var signalled = await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(signalled).IsTrue();
    }

    [Test]
    public async Task NoNotify_WaitTimesOut_ReturnsFalse()
    {
        var signal = new OutboxWakeUpSignal();

        var signalled = await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        await Assert.That(signalled).IsFalse();
    }

    [Test]
    public async Task NotifyTwice_MergesIntoOneSignal()
    {
        // 容量 1 的合并语义：消费一次后不应再有第二枚信号残留。
        var signal = new OutboxWakeUpSignal();
        signal.Notify();
        signal.Notify();

        await Assert.That(await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).IsTrue();
        await Assert.That(await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task WaitAsync_HugeDuration_IsClampedNotThrowing()
    {
        // 计时器上限钳制：TimeSpan.MaxValue 超出 SemaphoreSlim 的 int 毫秒上限，
        // 未钳制时抛 ArgumentOutOfRangeException；已取消令牌应立即以 OCE 返回。
        var signal = new OutboxWakeUpSignal();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.That(async () => await signal.WaitAsync(TimeSpan.MaxValue, cts.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task InMemoryStore_AddMessage_NotifiesSignal()
    {
        var sink = new CountingWakeUpSignal();
        var store = new InMemoryOutboxStore(wakeUpSignal: sink);

        await store.AddMessageAsync(new OutboxMessage());

        await Assert.That(Volatile.Read(ref sink.NotifyCount)).IsEqualTo(1);
    }

    [Test]
    public async Task InMemoryStore_AddMessages_NotifiesSignalOnce()
    {
        var sink = new CountingWakeUpSignal();
        var store = new InMemoryOutboxStore(wakeUpSignal: sink);

        await store.AddMessagesAsync([new OutboxMessage(), new OutboxMessage()]);

        await Assert.That(Volatile.Read(ref sink.NotifyCount)).IsEqualTo(1);
    }

    [Test]
    public async Task InMemoryStore_WithoutSignal_StillWorks()
    {
        // 未注入信号（直构路径）时写入行为不变。
        var store = new InMemoryOutboxStore();

        await store.AddMessageAsync(new OutboxMessage());
        var pending = await store.GetPendingMessagesAsync(10, 10, CancellationToken.None);

        await Assert.That(pending.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AddPalOutbox_RegistersWakeUpSignalSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalOutbox();
        await using var provider = services.BuildServiceProvider();

        // GetRequiredService：未注册即抛（存在性由容器契约表达，不用弱断言占棘轮配额）。
        var first = provider.GetRequiredService<IOutboxWakeUpSignal>();
        var second = provider.GetRequiredService<IOutboxWakeUpSignal>();

        await Assert.That(second).IsSameReferenceAs(first);
        // 单例语义可观测：通知后同一实例等待返回 true。
        first.Notify();
        await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).IsTrue();
    }

    private sealed class CountingWakeUpSignal : IOutboxWakeUpSignal
    {
        public int NotifyCount;

        public void Notify() => Interlocked.Increment(ref NotifyCount);

        public ValueTask<bool> WaitAsync(TimeSpan maxWait, CancellationToken ct) => new(false);
    }
}
