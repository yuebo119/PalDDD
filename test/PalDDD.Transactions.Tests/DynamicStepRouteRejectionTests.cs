namespace PalDDD.Transactions.Tests;

// ══════════════════════════════════════════════════════════════
// ITM-775 回归：Dynamic 步骤路由到**特殊步骤**（ITM-069 拒绝路径）时，
// 被拒绝的 dispatch 不得进入 ExecutedStepKeys。
// 契约依据：SagaCompensation.cs:29-30、:85「只补偿实际已执行的步骤，避免补偿
// 未执行步骤（两个入口均先经 ExecutedStepKeys 过滤）」——修复前 RecordExecutedStep
// 位于 dispatch-kind 校验**之前**，被拒绝的键已进轨迹，补偿会补偿未执行的步骤。
//
// ⚠️ 本文件的前置断言是**异常消息**而非仅异常类型：首版用例只断言
// `Throws<InvalidOperationException>`，而该类型可由多条路径抛出（含"事件无匹配步骤"），
// 结果测试在修复前后均通过（非区分性）——正是 AssertionStrength 守卫针对的弱断言形态。
// ══════════════════════════════════════════════════════════════

public sealed class SpecialTargetProbeState : SagaState
{
}

public sealed record DynamicRouteSpecialTargetEvent;

public sealed class DynamicStepRouteRejectionTests
{
    /// <summary>Dynamic 步骤路由到 FanOut（特殊步骤）→ 触发 ITM-069 显式拒绝</summary>
    private sealed class SpecialTargetSaga : Saga<SpecialTargetProbeState>
    {
        public SpecialTargetSaga()
        {
            WhenDynamic<DynamicRouteSpecialTargetEvent>(
                "Start",
                new DynamicStep(
                    "dyn",
                    router: _ => "Start",
                    compensate: (_, _) => ValueTask.CompletedTask));
            When("Start", new FanOutStep<int, object?>(
                "fan",
                static _ => [],
                static (_, _) => ValueTask.FromResult<object?>(null)));
        }
    }

    [Test]
    public async Task DynamicRouteToSpecialStep_RejectedDispatchDoesNotPolluteExecutedKeys(CancellationToken ct)
    {
        var saga = new SpecialTargetSaga();
        var state = new SpecialTargetProbeState { CurrentState = "Start" };

        // 前置断言：必须命中 ITM-069 的**路由目标拒绝**路径（而非其他 InvalidOperationException）
        var ex = await Assert.That(async () =>
                await saga.ProcessEventAsync(state, new DynamicRouteSpecialTargetEvent(), ct))
            .Throws<InvalidOperationException>();
        await Assert.That(ex!.Message.Contains("不支持事件路由分发", StringComparison.Ordinal)).IsTrue();

        // 契约：被拒绝的 dispatch 未执行任何步骤，故不得进入执行轨迹
        //（修复前此断言为假——键已被 RecordExecutedStep 写入，补偿将补偿未执行步骤）
        await Assert.That(state.ExecutedStepKeys.Contains("Start|DynamicRouteSpecialTargetEvent")).IsFalse();
    }
}
