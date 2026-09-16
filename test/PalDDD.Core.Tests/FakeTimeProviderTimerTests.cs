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

    /// <summary>ITM-282（R44）：Change 从"恒 false"改为总是抛 NotSupportedException（TST-114）——
    /// 公共契约行为变更零锁定，本测试防止未来被改回。</summary>
    [Test]
    public async Task Change_Always_Throws_NotSupported()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var timer = time.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        await Assert.That(() => timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan)).Throws<NotSupportedException>();
    }

    /// <summary>全仓扫描修复回归：dueTime 为 <see cref="Timeout.InfiniteTimeSpan"/> 时
    /// **永不触发**（真实 Timer 语义）。修复前 `_now + (-1ms)` 得到已过期时刻 → 下一次快进
    /// 即使增量为零也会立即触发——租约/取消路径的 `Task.Delay(Timeout.Infinite, …)` 即该形态。</summary>
    [Test]
    public async Task CreateTimer_InfiniteDueTime_NeverFires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var fired = 0;
        time.CreateTimer(_ => Interlocked.Increment(ref fired), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        time.AdvanceNowAndTriggerTimers(TimeSpan.Zero);
        time.AdvanceNowAndTriggerTimers(TimeSpan.FromDays(365));

        await Assert.That(Volatile.Read(ref fired)).IsEqualTo(0);
    }

    /// <summary>全仓扫描修复回归：单个回调抛出不得中断批次——其余到期计时器仍须触发，
    /// 且时钟必须推进（修复前两者皆被跳过，把一处断言失败放大成"计时器再不上场 + 时钟
    /// 不走"的难查状态）。</summary>
    [Test]
    public async Task AdvanceNowAndTriggerTimers_CallbackThrows_BatchCompletesAndClockAdvances()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var fired = 0;
        time.CreateTimer(_ => throw new InvalidOperationException("boom"), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        time.CreateTimer(_ => Interlocked.Increment(ref fired), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        var ex = await Assert.That(() => time.AdvanceNowAndTriggerTimers(TimeSpan.FromSeconds(1)))
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).IsEqualTo("boom");                    // 原始异常按调用栈重抛
        await Assert.That(Volatile.Read(ref fired)).IsEqualTo(1);           // 另一到期计时器仍触发
        await Assert.That(time.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(1));  // 时钟已推进
    }
}
