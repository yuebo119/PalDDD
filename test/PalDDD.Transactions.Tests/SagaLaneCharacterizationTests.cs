// ─────────────────────────────────────────────────────────────
// 三车道表征测试（decision-2026-09-17 Phase 0 · ITM-787）
// FanOut / ChildSaga / Dynamic 三车道在 ProcessEventAsync 入口的行为锁定——
// Option A 骨架抽取（RunRetryLaneAsync）前的前置网：任何静默漂移在此变红。
// 断言策略：类型 + InnerExceptions 计数 + 关键消息子串（不锁整句文案）。
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 共享基建：记录型 Sink（P3-SRC-603 观察者归因断言用）
// ═══════════════════════════════════════════════════════════════

internal sealed class LaneRecordingSink : ISagaEventSink
{
    public List<(string Event, string StepKey)> Events { get; } = [];

    public ValueTask EmitAsync<T>(T sagaEvent, CancellationToken ct) where T : notnull
    {
        var stepKey = sagaEvent switch
        {
            SagaStepStarted s => s.StepKey,
            SagaStepCompleted s => s.StepKey,
            SagaStepFailed s => s.StepKey,
            SagaCompensationStarted s => s.StepKey,
            _ => "",
        };
        Events.Add((sagaEvent.GetType().Name, stepKey));
        return ValueTask.CompletedTask;
    }

    public bool HasStepEvent(string eventName, string stepKey)
        => Events.Any(e => e.Event == eventName && e.StepKey == stepKey);
}

internal sealed class FanOutLaneProbeState : SagaState;

internal sealed class ParentLaneState : SagaState
{
    public string? Mark { get; set; }
}

// ═══════════════════════════════════════════════════════════════
// FanOut 车道（ExecuteFanOutStepAsync）
// ═══════════════════════════════════════════════════════════════

public class FanOutLaneTests
{
    /// <summary>FanOut 车道成功：轨迹记车道注册键，观察者 Started/Completed 同键</summary>
    [Test]
    public async Task Success_RecordsLaneKey_AndObserverSeesLaneKey(CancellationToken ct)
    {
        var saga = new FanOutLaneSaga(failItem: -1, failCompensate: false);
        var state = new FanOutLaneProbeState { CurrentState = "Start" };
        var sink = new LaneRecordingSink();

        using var observer = new SagaExecutionObserver(sink);
        var result = await saga.ProcessEventAsync(state, new object(), ct);

        await Assert.That(result.ExecutedStepKeys.Contains("Start")).IsTrue();
        await Assert.That(sink.HasStepEvent(nameof(SagaStepStarted), "Start")).IsTrue();
        await Assert.That(sink.HasStepEvent(nameof(SagaStepCompleted), "Start")).IsTrue();
        // 车道键唯一——FanOut 无子项级观察事件（item 粒度不在观察面）
        await Assert.That(sink.Events.All(e => e.StepKey == "Start")).IsTrue();
    }

    /// <summary>部分失败驱动车道重试：首 attempt item2 失败 → AggEx → 整体重试全成功返回。
    /// ⚠️ 本测试同时是 <b>整批重放契约的表征锁定</b>（v2 审计 A-3 / M0-2，2026-09-19）：
    /// ExecutedItems.Count == 3 断言「已成功子任务在重试 attempt 被重新执行」——
    /// 重试粒度 = 整批而非子项（FanOutStep XML 契约「重放语义」段，M1-2 声明）。
    /// 若未来实现改为子项粒度（v3.0 计划），本断言将红——届时契约声明与测试同步更新，
    /// 禁止静默漂移。</summary>
    [Test]
    public async Task PartialFailure_RetriesThenSucceeds(CancellationToken ct)
    {
        var saga = new FanOutLaneSaga(failItem: 2, failCompensate: false);
        var state = new FanOutLaneProbeState { CurrentState = "Start" };

        var result = await saga.ProcessEventAsync(state, new object(), ct);

        // 整批重放锁定：items=[1,2]，首 attempt item1 成功 + item2 抛 → 重试 attempt
        // 对 item1 **再次执行**（1,1,2 共 3 次）——这正是「executor 必须幂等」契约的
        // 机制根源（成功子任务的外部副作用会重复）
        await Assert.That(saga.ExecutedItems).Count().IsEqualTo(3);
        await Assert.That(saga.ExecutedItems.Count(i => i == 1)).IsEqualTo(2); // item1 执行 2 次（M0-2 显式断言）
        await Assert.That(saga.ExecutedItems.Count(i => i == 2)).IsEqualTo(1);
        await Assert.That(saga.CompensationLog).Count().IsEqualTo(0);
    }

    /// <summary>重试耗尽：AggregateException 含 FanOut 部分失败聚合；车道补偿被调用</summary>
    [Test]
    public async Task Exhausted_ThrowsAggregate_AndCompensates(CancellationToken ct)
    {
        var saga = new FanOutLaneSaga(failItem: 0, failCompensate: false);
        var state = new FanOutLaneProbeState { CurrentState = "Start" };

        var ex = await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object(), ct)).Throws<AggregateException>();

        // 车道异常文案用注册键（stepKey），非 FanOutStep 构造名
        await Assert.That(ex!.Message.Contains("FanOut step 'Start'", StringComparison.Ordinal)).IsTrue();
        // 3 attempts × 内层部分失败聚合（每项含 item 异常）
        await Assert.That(ex.InnerExceptions.Count).IsEqualTo(3);
        await Assert.That(saga.CompensationLog).Count().IsEqualTo(1);
        await Assert.That(saga.CompensationLog[0]).IsEqualTo("fan-compensated");
    }

    /// <summary>补偿自身再失败：步骤异常与补偿异常同入聚合（"and compensation also failed"）</summary>
    [Test]
    public async Task Exhausted_CompensationAlsoFails_BothInAggregate(CancellationToken ct)
    {
        var saga = new FanOutLaneSaga(failItem: 0, failCompensate: true);
        var state = new FanOutLaneProbeState { CurrentState = "Start" };

        var ex = await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object(), ct)).Throws<AggregateException>();

        await Assert.That(ex!.Message.Contains("and compensation also failed", StringComparison.Ordinal)).IsTrue();
        // 3 个步骤 AggEx + 1 个补偿异常
        await Assert.That(ex.InnerExceptions.Count).IsEqualTo(4);
        // 补偿异常经 SagaCompensation.RunAsync 聚合（非裸抛）——ToString 递归展开断根因
        await Assert.That(ex.InnerExceptions.Any(e =>
            e.ToString().Contains("fan compensate boom", StringComparison.Ordinal))).IsTrue();
    }
}

/// <summary>FanOut 车道测试 saga。failItem 语义：-1 全成功；0 所有 item 恒失败；n（n&gt;0）item n 仅首次尝试失败（含失败的尝试计数，非成功计数）。</summary>
internal sealed class FanOutLaneSaga : Saga<FanOutLaneProbeState>
{
    private readonly bool _failCompensate;
    private readonly int _failItem;

    public List<int> ExecutedItems { get; } = [];
    public Dictionary<int, int> ItemAttempts { get; } = [];
    public List<string> CompensationLog { get; } = [];

    public FanOutLaneSaga(int failItem, bool failCompensate)
    {
        _failItem = failItem;
        _failCompensate = failCompensate;
        MaxRetries = 2;
        RetryDelay = TimeSpan.FromMilliseconds(1);

        When("Start", new FanOutStep<int, bool>(
            "fan",
            selector: _ => [1, 2],
            executor: (item, _) =>
            {
                // 尝试计数含失败（否则失败的 item 恒 seen==0，重试永不通过）
                var attempts = ItemAttempts.TryGetValue(item, out var n) ? n : 0;
                ItemAttempts[item] = attempts + 1;
                if ((_failItem == 0 || item == _failItem) && (_failItem == 0 || attempts == 0))
                    throw new InvalidOperationException($"fan item {item} boom");
                ExecutedItems.Add(item);
                return ValueTask.FromResult(true);
            },
            compensate: (_, _) =>
            {
                CompensationLog.Add("fan-compensated");
                if (_failCompensate) throw new InvalidOperationException("fan compensate boom");
                return ValueTask.CompletedTask;
            }));
    }
}

// ═══════════════════════════════════════════════════════════════
// ChildSaga 车道（ExecuteChildSagaStepAsync · ITM-787 从零补齐）
// ═══════════════════════════════════════════════════════════════

internal sealed class ChildProbeState : SagaState
{
    public bool Done { get; set; }
}

/// <summary>子 saga：以 SagaState.InitialStateName（新建 childState 的默认状态）精确注册
/// ChildSagaInputEvent（v35 公开化契约——父车道以 Saga&lt;TParent&gt;.ChildSagaInputEvent 触发，
/// Name 同名故键匹配；childState 由 CreateChildState 创建，CurrentState 默认 "Initial"）</summary>
internal sealed class ChildLaneSaga : Saga<ChildProbeState>
{
    private readonly int _failFirstN;
    private int _attempts;

    public List<ChildProbeState> SeenChildStates { get; } = [];

    public ChildLaneSaga(int failFirstN)
    {
        _failFirstN = failFirstN;
        // MaxRetries=0：子 saga 不做内部重试——重试归父车道驱动，才能干净观察
        // "父每 attempt 重建 child state"（子内部重试在同实例上打转，掩盖重建语义）
        MaxRetries = 0;
        RetryDelay = TimeSpan.FromMilliseconds(1);

        When<ChildSagaInputEvent>(SagaState.InitialStateName, new SagaStep("childWork",
            execute: (s, _, _) =>
            {
                var child = (ChildProbeState)s;
                SeenChildStates.Add(child);
                if (++_attempts <= _failFirstN)
                    throw new InvalidOperationException("child boom");
                child.Done = true;
                return ValueTask.FromResult(s);
            }));
    }
}

internal sealed class ParentOfChildSaga : Saga<ParentLaneState>
{
    private readonly ChildLaneSaga _child;

    private ParentOfChildSaga(ChildLaneSaga child, List<string> compensationLog, bool failCompensate)
    {
        _child = child;
        MaxRetries = 2;
        RetryDelay = TimeSpan.FromMilliseconds(1);

        When("Start", new ChildSagaStep<ChildProbeState, string, bool>(
            "spawnChild",
            inputSelector: _ => "child-input",
            outputSelector: childState => childState.Done,
            outputApplier: (parent, done) => ((ParentLaneState)parent).Mark = done ? "done" : "no",
            compensate: (_, _) =>
            {
                compensationLog.Add("spawnChild-compensated");
                if (failCompensate) throw new InvalidOperationException("parent compensate boom");
                return ValueTask.CompletedTask;
            }));
    }

    public static ParentOfChildSaga Create(ChildLaneSaga child, List<string> compensationLog, bool failCompensate = false)
        => new(child, compensationLog, failCompensate);

    protected override Saga<TChild>? ResolveChildSaga<TChild>()
        => _child as Saga<TChild>;
}

public class ChildSagaLaneTests
{
    /// <summary>成功：子输出经 outputApplier 写回父状态；轨迹记车道键</summary>
    [Test]
    public async Task Success_AppliesChildOutputToParent(CancellationToken ct)
    {
        var child = new ChildLaneSaga(failFirstN: 0);
        var saga = ParentOfChildSaga.Create(child, []);
        var state = new ParentLaneState { CurrentState = "Start" };

        var result = await saga.ProcessEventAsync(state, new object(), ct);

        await Assert.That(result.Mark).IsEqualTo("done");
        await Assert.That(result.ExecutedStepKeys.Contains("Start")).IsTrue();
    }

    /// <summary>每 attempt 重建 child state：两次执行收到不同实例，首实例未被污染（decision §1.4-6）</summary>
    [Test]
    public async Task EachAttempt_RebuildsChildState_PreviousNotReused(CancellationToken ct)
    {
        var child = new ChildLaneSaga(failFirstN: 1);
        var saga = ParentOfChildSaga.Create(child, []);
        var state = new ParentLaneState { CurrentState = "Start" };

        var result = await saga.ProcessEventAsync(state, new object(), ct);

        await Assert.That(child.SeenChildStates).Count().IsEqualTo(2);
        await Assert.That(ReferenceEquals(child.SeenChildStates[0], child.SeenChildStates[1])).IsFalse();
        await Assert.That(child.SeenChildStates[0].Done).IsFalse(); // 旧实例状态不被复用
        await Assert.That(result.Mark).IsEqualTo("done");           // attempt 1 成功的输出仍写回
    }

    /// <summary>重试耗尽：子步骤异常以嵌套 AggEx 浮现，父车道补偿被调用</summary>
    [Test]
    public async Task Exhausted_ThrowsNestedAggregate_AndCompensates(CancellationToken ct)
    {
        var compensationLog = new List<string>();
        var child = new ChildLaneSaga(failFirstN: 99);
        var saga = ParentOfChildSaga.Create(child, compensationLog);
        var state = new ParentLaneState { CurrentState = "Start" };

        var ex = await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object(), ct)).Throws<AggregateException>();

        await Assert.That(ex!.Message.Contains("ChildSaga step 'Start'", StringComparison.Ordinal)).IsTrue();
        // 内层是子 saga Normal 车道的 AggEx（其 stepKey 为子的注册键 Initial|ChildSagaInputEvent）
        await Assert.That(ex.InnerExceptions.All(e => e is AggregateException)).IsTrue();
        await Assert.That(ex.InnerExceptions.Any(e =>
            e.Message.Contains("Initial|ChildSagaInputEvent", StringComparison.Ordinal))).IsTrue();
        await Assert.That(compensationLog).Count().IsEqualTo(1);
        await Assert.That(compensationLog[0]).IsEqualTo("spawnChild-compensated");
    }

    /// <summary>补偿自身再失败：子异常与补偿异常同入聚合</summary>
    [Test]
    public async Task Exhausted_CompensationAlsoFails_BothInAggregate(CancellationToken ct)
    {
        var child = new ChildLaneSaga(failFirstN: 99);
        var saga = ParentOfChildSaga.Create(child, [], failCompensate: true);
        var state = new ParentLaneState { CurrentState = "Start" };

        var ex = await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object(), ct)).Throws<AggregateException>();

        await Assert.That(ex!.Message.Contains("and compensation also failed", StringComparison.Ordinal)).IsTrue();
        // 补偿异常经 SagaCompensation.RunAsync 聚合（非裸抛）——ToString 递归展开断根因
        await Assert.That(ex.InnerExceptions.Any(e =>
            e.ToString().Contains("parent compensate boom", StringComparison.Ordinal))).IsTrue();
    }
}

// ═══════════════════════════════════════════════════════════════
// Dynamic 车道（ExecuteDynamicStepAsync）
// ═══════════════════════════════════════════════════════════════

internal sealed class DynLaneState : SagaState;

public class DynamicLaneTests
{
    /// <summary>成功：轨迹记双键（dynamic 入口键 + matchedKey）；步骤观察事件只归 dynamic 键（P3-SRC-603）</summary>
    [Test]
    public async Task Success_RecordsBothKeys_ObserverSeesOnlyDynamicKey(CancellationToken ct)
    {
        var saga = new DynLaneSaga();
        var state = new DynLaneState { CurrentState = "Start" };
        var sink = new LaneRecordingSink();

        using var observer = new SagaExecutionObserver(sink);
        var result = await saga.ProcessEventAsync(state, new object(), ct);

        await Assert.That(result.ExecutedStepKeys.Contains("Start")).IsTrue();   // dynamic 入口键
        await Assert.That(result.ExecutedStepKeys.Contains("Target")).IsTrue();  // matchedKey
        await Assert.That(sink.HasStepEvent(nameof(SagaStepStarted), "Start")).IsTrue();
        await Assert.That(sink.HasStepEvent(nameof(SagaStepCompleted), "Start")).IsTrue();
        await Assert.That(sink.Events.All(e => e.StepKey == "Start")).IsTrue();  // 观察端零 "Target" 归因
    }

    /// <summary>耗尽：异常归因到路由对（stepKey→matchedKey）；补偿收目标步骤 + dynamic 补偿；
    /// 步骤观察归 dynamic 键、补偿观察（OnCompensationStarted）归 matchedKey——P3-SRC-603 完整形态</summary>
    [Test]
    public async Task Exhausted_CompensatesTarget_ObserverSeesDynamicKey(CancellationToken ct)
    {
        var saga = new DynLaneSaga(targetFails: true);
        var state = new DynLaneState { CurrentState = "Start" };
        var sink = new LaneRecordingSink();

        using var observer = new SagaExecutionObserver(sink);
        var ex = await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object(), ct)).Throws<AggregateException>();

        await Assert.That(ex!.Message.Contains("routed to 'Target'", StringComparison.Ordinal)).IsTrue();
        // 补偿面：失败步骤（matchedStep=Target）优先回滚，随后轨迹里的 dynamic 键补偿
        await Assert.That(saga.CompensationLog).Count().IsEqualTo(2);
        await Assert.That(saga.CompensationLog[0]).IsEqualTo("target-compensated");
        await Assert.That(saga.CompensationLog[1]).IsEqualTo("dyn-compensated");
        await Assert.That(sink.HasStepEvent(nameof(SagaStepFailed), "Start")).IsTrue();
        await Assert.That(sink.Events.Where(e => e.Event != nameof(SagaCompensationStarted))
            .All(e => e.StepKey == "Start")).IsTrue();
        // OnCompensationStarted 用 matchedKey（CompensateExecutedStepsAsync 的 failedStepKey）——
        // 与步骤观察的 stepKey 归因不同，本条锁定该差异（P3-SRC-603 只声明步骤事件，此为补偿事件面）
        await Assert.That(sink.HasStepEvent(nameof(SagaCompensationStarted), "Target")).IsTrue();
    }

    /// <summary>Route 抛出：fail-fast（v40/v41 勘正语义）——不进重试/补偿，观察端零事件</summary>
    [Test]
    public async Task RouteThrows_FailsFast_NoCompensation_NoObserverEvents(CancellationToken ct)
    {
        var saga = new DynLaneSaga(routeThrows: true);
        var state = new DynLaneState { CurrentState = "Start" };
        var sink = new LaneRecordingSink();

        using var observer = new SagaExecutionObserver(sink);
        var ex = await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object(), ct)).Throws<AggregateException>();

        await Assert.That(ex!.Message.Contains("路由函数执行失败", StringComparison.Ordinal)).IsTrue();
        await Assert.That(saga.CompensationLog).Count().IsEqualTo(0);
        await Assert.That(sink.Events).Count().IsEqualTo(0);
    }

    /// <summary>未知路由键：P3-SRC-101 宽容语义——静默返回原状态（同引用），无异常无补偿无轨迹</summary>
    [Test]
    public async Task UnknownRouteKey_ReturnsCurrentSilently(CancellationToken ct)
    {
        var saga = new DynLaneSaga(routeTo: "Missing");
        var state = new DynLaneState { CurrentState = "Start" };

        var result = await saga.ProcessEventAsync(state, new object(), ct);

        await Assert.That(result).IsSameReferenceAs(state);
        await Assert.That(saga.CompensationLog).Count().IsEqualTo(0);
        await Assert.That(state.ExecutedStepKeys.Contains("Start")).IsFalse(); // 未进轨迹
    }
}

internal sealed class DynLaneSaga : Saga<DynLaneState>
{
    private readonly string _routeTo;
    private readonly bool _routeThrows;
    private readonly bool _targetFails;

    public List<string> CompensationLog { get; } = [];

    public DynLaneSaga(string routeTo = "Target", bool routeThrows = false, bool targetFails = false)
    {
        _routeTo = routeTo;
        _routeThrows = routeThrows;
        _targetFails = targetFails;
        MaxRetries = 2;
        RetryDelay = TimeSpan.FromMilliseconds(1);

        WhenDynamic("Start", new DynamicStep(
            "dyn",
            router: _ =>
            {
                if (_routeThrows) throw new InvalidOperationException("router boom");
                return _routeTo;
            },
            compensate: (_, _) =>
            {
                CompensationLog.Add("dyn-compensated");
                return ValueTask.CompletedTask;
            }));
        When("Target", new SagaStep(
            "target",
            execute: (s, _, _) =>
            {
                if (_targetFails) throw new InvalidOperationException("target boom");
                s.CurrentState = "Target";
                return ValueTask.FromResult(s);
            },
            compensate: (_, _) =>
            {
                CompensationLog.Add("target-compensated");
                return ValueTask.CompletedTask;
            }));
    }
}
