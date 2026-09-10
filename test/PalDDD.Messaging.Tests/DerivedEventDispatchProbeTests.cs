using PalDDD.Core;
using PalDDD.Testing;

namespace PalDDD.Messaging.Tests;

// ══════════════════════════════════════════════════════════════
// V25 探针 b — DomainEventDispatcher 派生类型派发行为
// ══════════════════════════════════════════════════════════════
// 事实探针：注册 Base 事件的 handler，向 IterativeDomainEventDispatcher
// 发布 Derived 事件实例（DomainEventDispatcher.cs DispatchSingleAsync 用
// @event.GetType() 做 FrozenDictionary 精确 Type 键匹配）。本文件只记录
// 事实行为（handler 是否被调用 / 是否抛异常 / 指标是否计数），不对
// "应该怎样"下结论。
//
// [NotInParallel]：零记录断言依赖 PalMetrics 全局 Counter，镜像
// HandlerCancellation_DoesNotRecordEventHandlerFailedMetric 的隔离先例。

/// <summary>探针用基类领域事件（非密封，供派生）</summary>
public class ProbeBaseEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "probe.base.v1";
}

/// <summary>探针用派生领域事件——未注册任何 handler，仅 Base 有 handler</summary>
public sealed class ProbeDerivedEvent : ProbeBaseEvent
{
}

public sealed class DerivedEventDispatchProbeTests
{

    /// <summary>注册在 Base 事件上的计数 handler</summary>
    private sealed class CountingBaseEventHandler : IEventHandler<ProbeBaseEvent>
    {
        public int HandleCount { get; private set; }

        public ValueTask HandleAsync(ProbeBaseEvent @event, CancellationToken ct)
        {
            HandleCount++;
            return ValueTask.CompletedTask;
        }
    }

    // 本测试派发领域事件会向进程级 ActivitySource 广播——既有
    // IterativeDomainEventDispatcherTests.SingleEvent_StartsEventDispatchActivity
    // 以 First() 断言全局监听流，[DependsOn] 使本测试排在该类全部测试
    // 成功完成之后执行，消除并行竞争。
    [Test]
    [DependsOn(typeof(IterativeDomainEventDispatcherTests))]
    public async Task Dispatch_DerivedEventInstance_HandlerNotInvokedSilentSkip()
    {
        var handler = new CountingBaseEventHandler();
        var dispatcher = new IterativeDomainEventDispatcher([handler]);

        // 事实断言（无异常信号）：派发正常完成不抛——DispatchAsync 抛任何异常
        // 都会使本测试失败，完成即"无异常"事实。
        await dispatcher.DispatchAsync([new ProbeDerivedEvent()]);

        // 事实断言：精确 Type 键匹配下 Base handler 未被派生事件命中
        await Assert.That(handler.HandleCount).IsEqualTo(0);
    }

    // 本测试派发领域事件会向进程级 ActivitySource 广播——既有
    // IterativeDomainEventDispatcherTests.SingleEvent_StartsEventDispatchActivity
    // 以 First() 断言全局监听流，[DependsOn] 使本测试排在该类全部测试
    // 成功完成之后执行，消除并行竞争。
    [Test]
    [DependsOn(typeof(IterativeDomainEventDispatcherTests))]
    [NotInParallel]
    public async Task Dispatch_DerivedEventInstance_HandledMetricNotIncremented()
    {
        using var handledListener = new RecordingMeterListener("paldd.event_handlers.handled");
        using var failedListener = new RecordingMeterListener("paldd.event_handlers.failed");
        var dispatcher = new IterativeDomainEventDispatcher([new CountingBaseEventHandler()]);

        await dispatcher.DispatchAsync([new ProbeDerivedEvent()]);

        // 事实断言：PalMetrics.EventHandlersHandled 零记录；
        // Failed 同步断言零记录——区分"静默跳过"与"失败后被吞"两种分支。
        await Assert.That(handledListener.Measurements).IsEmpty();
        await Assert.That(failedListener.Measurements).IsEmpty();
    }

    // 本测试派发领域事件会向进程级 ActivitySource 广播——既有
    // IterativeDomainEventDispatcherTests.SingleEvent_StartsEventDispatchActivity
    // 以 First() 断言全局监听流，[DependsOn] 使本测试排在该类全部测试
    // 成功完成之后执行，消除并行竞争。
    [Test]
    [DependsOn(typeof(IterativeDomainEventDispatcherTests))]
    public async Task Dispatch_BaseEventInstance_HandlerInvokedAndMetricRecorded()
    {
        // 探针自证（区分两种行为分支的信号）：同一装置下派发 Base 事件——
        // handler 被调用且指标有记录，证明上两个测试的零调用/零记录
        // 是"派生类型未命中"的行为事实，而非装置失效。
        using var handledListener = new RecordingMeterListener("paldd.event_handlers.handled");
        var handler = new CountingBaseEventHandler();
        var dispatcher = new IterativeDomainEventDispatcher([handler]);

        await dispatcher.DispatchAsync([new ProbeBaseEvent()]);

        await Assert.That(handler.HandleCount).IsEqualTo(1);
        await Assert.That(handledListener.Measurements).Contains(1);
    }
}
