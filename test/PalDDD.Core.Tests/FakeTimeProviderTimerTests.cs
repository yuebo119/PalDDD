namespace PalDDD.Core.Tests;

using PalDDD.Testing;

// ═══════════════════════════════════════════════════════════════
// 🧪 FakeTimeProvider 计时器子系统行为测试（十轮盲区评审补齐）
// 此前零测试零消费——快进触发语义（到期/未到期/取消/触发顺序）从未被锁定。
// ═══════════════════════════════════════════════════════════════

public sealed class FakeTimeProviderTimerTests
{
    [Test]
    public async Task AdvanceNowAndTriggerTimers_DueTimer_FiresCallback()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var fired = 0;
        time.CreateTimer(_ => Interlocked.Increment(ref fired), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        time.AdvanceNowAndTriggerTimers(TimeSpan.FromSeconds(5));

        await Assert.That(Volatile.Read(ref fired)).IsEqualTo(1);
    }

    [Test]
    public async Task AdvanceNowAndTriggerTimers_NotYetDue_DoesNotFire()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var fired = 0;
        time.CreateTimer(_ => Interlocked.Increment(ref fired), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        time.AdvanceNowAndTriggerTimers(TimeSpan.FromSeconds(5));

        await Assert.That(Volatile.Read(ref fired)).IsEqualTo(0);
        // 再推 5 秒到达 DueTime——此前未触发是因为未到期而非被丢弃
        time.AdvanceNowAndTriggerTimers(TimeSpan.FromSeconds(5));
        await Assert.That(Volatile.Read(ref fired)).IsEqualTo(1);
    }

    [Test]
    public async Task AdvanceNowAndTriggerTimers_DisposedTimer_DoesNotFire()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var fired = 0;
        var timer = time.CreateTimer(_ => Interlocked.Increment(ref fired), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        timer.Dispose();
        time.AdvanceNowAndTriggerTimers(TimeSpan.FromSeconds(5));

        await Assert.That(Volatile.Read(ref fired)).IsEqualTo(0);
    }

    [Test]
    public async Task AdvanceNowAndTriggerTimers_MultipleDue_FiresInDueTimeOrder()
    {
        // P2 回归（十轮）：触发序按 DueTime 升序——此前按注册序，后注册但先到期者晚触发
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();

        time.CreateTimer(_ => order.Enqueue(2), null, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan); // 后注册、后到期
        time.CreateTimer(_ => order.Enqueue(1), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan); // 先到期

        time.AdvanceNowAndTriggerTimers(TimeSpan.FromSeconds(10));

        await Assert.That(order.ToArray()).IsEquivalentTo([1, 2]);
        await Assert.That(order.ToArray()[0]).IsEqualTo(1);
    }
}
