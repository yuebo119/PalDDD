namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// R14 F3 锁定：子 Saga 停在 AwaitingHumanDecision 时，父 ChildSagaStep
// 不得记为成功，也不得继续后续步骤。
//
// 背景：ChildSagaStep 的契约只描述「完成 / 失败」两态。子 Saga 含
// InterruptStep 时 ProcessEventAsync 返回 AwaitingHumanDecision 且不抛异常；
// 原实现直接 ApplyOutput + RecordExecutedStep，父流程静默越过未获人工
// 批准的审批子流程。修复后该组合以明确异常失败（父步骤不推进）。
// ═══════════════════════════════════════════════════════════════
public sealed class ChildSagaHumanDecisionTests
{
    [Test]
    public async Task ChildSagaAwaitingHumanDecision_ParentStepFails_AndAfterStepDoesNotRun()
    {
        var manager = new DefaultSagaManager();
        var child = new InterruptChildSaga { SagaManager = manager };
        var log = new List<string>();
        var parent = new ParentOfInterruptSaga(child, log) { SagaManager = manager };
        var state = new ParentState { CurrentState = "Start" };

        Exception? captured = null;
        try
        {
            await parent.ProcessEventAsync(state, new object(), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            captured = ex;
        }

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.ToString().Contains("AwaitingHumanDecision", StringComparison.Ordinal)).IsTrue();
        await Assert.That(log.Contains("after-child")).IsFalse();
        await Assert.That(state.CurrentState).IsEqualTo("Start");
    }
}

internal sealed class ParentState : SagaState
{
    public string Mark { get; set; } = "";
}

internal sealed class ChildState : SagaState
{
    public bool Approved { get; set; }
}

internal sealed class ChildApproval
{
}

internal sealed class InterruptChildSaga : Saga<ChildState>
{
    public InterruptChildSaga()
    {
        When<ChildSagaInputEvent>(SagaState.InitialStateName,
            new InterruptStep("child-approval", "needs-approval", typeof(ChildApproval)));
    }
}

internal sealed class ParentOfInterruptSaga : Saga<ParentState>
{
    private readonly InterruptChildSaga _child;
    private readonly List<string> _log;

    public ParentOfInterruptSaga(InterruptChildSaga child, List<string> log)
    {
        _child = child;
        _log = log;
        MaxRetries = 0;
        RetryDelay = TimeSpan.FromMilliseconds(1);

        When("Start", new ChildSagaStep<ChildState, string, bool>(
            "spawnChild",
            inputSelector: _ => "child-input",
            outputSelector: childState =>
            {
                _log.Add($"child-status={childState.Status}");
                return childState.Approved;
            },
            outputApplier: (parent, approved) =>
            {
                ((ParentState)parent).Mark = approved ? "approved" : "not-approved";
                parent.CurrentState = "After";
            }));

        When("After", new SagaStep("afterChild",
            execute: (s, _, _) =>
            {
                _log.Add("after-child");
                ((ParentState)s).Mark += "+after";
                return ValueTask.FromResult(s);
            }));
    }

    protected override Saga<TChild>? ResolveChildSaga<TChild>()
        => _child as Saga<TChild>;
}
