using PalDDD.Transactions;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// ADR-039 / 审计 A4：Saga 只读步骤表（GetRegisteredSteps）
// ═══════════════════════════════════════════════════════════════
//
// 为什么需要：Steps 是 protected，框架此前无公开枚举入口；DynamicStep.Route 返回未知
// key 时零信号——拼写错误与滚动发布窗口期未注册新 key 在观测上不可区分。
// 本表给宿主/运维一个可查询的步骤清单，且不暴露 SagaStep 的委托与可变成员。

internal sealed class StepTableState : SagaState;

internal sealed record StepTableStartEvent;

internal sealed class StepTableSaga : Saga<StepTableState>
{
    public StepTableSaga()
    {
        When("Initial", new SagaStep(
            "plain-step",
            execute: (s, _, _) => ValueTask.FromResult(s),
            compensate: (_, _) => ValueTask.CompletedTask));

        // 不同 State：同 (State, EventName) 键会覆盖（既有注册语义），此处刻意区分
        When("Second", new SagaStep(
            "timed-step",
            execute: (s, _, _) => ValueTask.FromResult(s),
            compensate: (_, _) => ValueTask.CompletedTask)
        {
            Timeout = TimeSpan.FromSeconds(30)
        });

        WhenDynamic<StepTableStartEvent>("Initial", new DynamicStep("dyn-step", router: _ => "Initial"));
    }
}

public sealed class SagaStepDescriptorTests
{
    /// <summary>
    /// 步骤表投影：按注册顺序返回 key / 分发类型 / 超时；普通步骤 DispatchKind 为 Normal，
    /// Dynamic 步骤为 Dynamic，Timeout 与注册值一致。
    /// </summary>
    [Test]
    public async Task GetRegisteredSteps_ProjectsKeyDispatchKindAndTimeout()
    {
        var saga = new StepTableSaga();

        var steps = saga.GetRegisteredSteps();

        await Assert.That(steps.Count).IsEqualTo(3);
        // Key 是 SagaKey.Make(State, EventName) 的规范化形式（State 名，含事件名时带后缀），
        // 不是 SagaStep.Name
        await Assert.That(steps[0].Key).Contains("Initial");
        await Assert.That(steps[0].DispatchKind).IsEqualTo(StepDispatchKind.Normal);
        await Assert.That(steps[0].Timeout).IsNull();

        await Assert.That(steps[1].Key).Contains("Second");
        await Assert.That(steps[1].Timeout).IsEqualTo(TimeSpan.FromSeconds(30));

        await Assert.That(steps[2].Key).Contains("Initial");
        await Assert.That(steps[2].DispatchKind).IsEqualTo(StepDispatchKind.Dynamic);
    }

    /// <summary>
    /// 投影是快照（不可变 DTO），不含 SagaStep 引用——调用方拿不到委托与可变成员。
    /// </summary>
    [Test]
    public async Task GetRegisteredSteps_ReturnsDescriptorSnapshot_NotSagaStepReferences()
    {
        var saga = new StepTableSaga();

        var steps = saga.GetRegisteredSteps();

        await Assert.That(steps).IsAssignableTo<IReadOnlyList<SagaStepDescriptor>>();
        // record struct 的值语义：两次调用内容相等
        var again = saga.GetRegisteredSteps();
        await Assert.That(again.Count).IsEqualTo(steps.Count);
        await Assert.That(again[0]).IsEqualTo(steps[0]);
    }
}
