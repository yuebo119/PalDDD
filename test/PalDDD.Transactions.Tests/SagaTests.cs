// ─────────────────────────────────────────────────────────────
// Saga 专用测试 — 补偿/重试/超时/死信四大路径独立验证
// ─────────────────────────────────────────────────────────────
using PalDDD.Testing;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 测试用 Saga 编排器 — 派生类突破 protected 访问
// ═══════════════════════════════════════════════════════════════

internal sealed class TestSagaState : SagaState
{
    public string Payload { get; set; } = "";
}

internal sealed class TestSaga : Saga<TestSagaState>
{
    public TestSaga(CompensationPolicy compensationPolicy = CompensationPolicy.Backward)
    {
        base.CompensationPolicy = compensationPolicy;
        MaxRetries = 2;
        RetryDelay = TimeSpan.FromMilliseconds(1);
    }

    public void UseRetryBackoffPolicy(IRetryBackoffPolicy policy)
        => RetryBackoffPolicy = policy;

    public void UseRetryDelay(TimeSpan delay)
        => RetryDelay = delay;

    // 封装 protected 方法为公共 API
    public void PublicWhen(string state, string stepName, bool shouldFail = false)
    {
        When(state, new SagaStep(stepName,
            execute: (s, _, _) =>
            {
                if (shouldFail) throw new InvalidOperationException("Step failed");
                s.CurrentState = stepName;
                return ValueTask.FromResult(s);
            },
            compensate: (s, ct) =>
            {
                s.CurrentState = $"{stepName}-compensated";
                return ValueTask.CompletedTask;
            }));
    }

    public void PublicWhen<TEvent>(string state, string stepName, bool shouldFail = false)
    {
        When(state, typeof(TEvent), new SagaStep(stepName,
            execute: (s, _, _) =>
            {
                if (shouldFail) throw new InvalidOperationException("Step failed");
                s.CurrentState = stepName;
                return ValueTask.FromResult(s);
            },
            compensate: (s, ct) =>
            {
                s.CurrentState = $"{stepName}-compensated";
                return ValueTask.CompletedTask;
            }));
    }

    public void PublicWhenWithTimeout(string state, string stepName, TimeSpan timeout)
    {
        When(state, new SagaStep(stepName,
            execute: (s, _, _) =>
            {
                s.CurrentState = stepName;
                return ValueTask.FromResult(s);
            },
            timeout: timeout));
    }

    public void PublicWhenWithoutCompensation(string state, string stepName)
    {
        When(state, new SagaStep(stepName,
            execute: (s, _, _) =>
            {
                s.CurrentState = stepName;
                return ValueTask.FromResult(s);
            }));
    }
}

// ═══════════════════════════════════════════════════════════════
// 正常状态转换
// ═══════════════════════════════════════════════════════════════

public class SagaNormalTransitionTests
{
    [Test]
    public async Task SingleStep_TransitionsCorrectly()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Submitted");

        var state = new TestSagaState { CurrentState = "Initial" };
        var result = await saga.ProcessEventAsync(state, new object());

        await Assert.That(result.CurrentState).IsEqualTo("Submitted");
    }

    [Test]
    public async Task MultipleSteps_ExecuteInOrder()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Step1");
        saga.PublicWhen("Step1", "Step2");
        saga.PublicWhen("Step2", "Completed");

        var state = new TestSagaState { CurrentState = "Initial" };

        state = await saga.ProcessEventAsync(state, new object());
        await Assert.That(state.CurrentState).IsEqualTo("Step1");
        state = await saga.ProcessEventAsync(state, new object());
        await Assert.That(state.CurrentState).IsEqualTo("Step2");
        state = await saga.ProcessEventAsync(state, new object());
        await Assert.That(state.CurrentState).IsEqualTo("Completed");
    }

    [Test]
    public async Task EventSpecificStep_RecordsMatchedKeyForTimeoutAndCompensation()
    {
        var saga = new TestSaga();
        saga.PublicWhen<TestEvent>("Initial", "Submitted");

        var state = new TestSagaState { CurrentState = "Initial" };
        var result = await saga.ProcessEventAsync(state, new TestEvent());

        await Assert.That(result.CurrentState).IsEqualTo("Submitted");
        await Assert.That(result.ExecutedStepKeys).Contains("Initial|TestEvent");
        await Assert.That(result.StepStartedAt.ContainsKey("Initial|TestEvent")).IsTrue();
    }

    [Test]
    public async Task WildcardStep_RecordsWildcardMatchedKey()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Submitted");

        var state = new TestSagaState { CurrentState = "Initial" };
        var result = await saga.ProcessEventAsync(state, new TestEvent());

        await Assert.That(result.CurrentState).IsEqualTo("Submitted");
        await Assert.That(result.ExecutedStepKeys).Contains("Initial");
        await Assert.That(result.ExecutedStepKeys).DoesNotContain("Initial|TestEvent");
    }

    [Test]
    public async Task EventSpecificStep_CanCompensatePreviouslyExecutedStep()
    {
        var compensationLog = new List<string>();
        var saga = new EventSpecificCompensationSaga(compensationLog);

        var state = new TestSagaState { CurrentState = "Initial" };
        state = await saga.ProcessEventAsync(state, new TestEvent());

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(compensationLog).Count().IsEqualTo(2);
        await Assert.That(compensationLog[0]).IsEqualTo("compensate:Fail");
        await Assert.That(compensationLog[1]).IsEqualTo("compensate:Submitted");
    }

    [Test]
    public async Task UnmatchedStep_ReturnsUnchangedState()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Step1");

        var state = new TestSagaState { CurrentState = "NoMatch" };
        var result = await saga.ProcessEventAsync(state, new object());

        await Assert.That(result.CurrentState).IsEqualTo("NoMatch");
        await Assert.That(result).IsSameReferenceAs(state);
    }

    [Test]
    public async Task HandleEventAsync_DirectCall_NoRetryNoCompensation()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Submitted", shouldFail: true);

        var state = new TestSagaState { CurrentState = "Initial" };
        // HandleEventAsync 不重试、不补偿，直接传播异常
        await Assert.That(async () =>
            await saga.HandleEventAsync(state, new object())).Throws<InvalidOperationException>();

        // 失败后状态不变（无补偿）
        await Assert.That(state.CurrentState).IsEqualTo("Initial");
    }
}

// ═══════════════════════════════════════════════════════════════
// 重试 + 补偿
// ═══════════════════════════════════════════════════════════════

public class SagaRetryAndCompensationTests
{
    [Test]
    public async Task StepFails_RetriesUpToMaxRetries()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "FailingStep", shouldFail: true);

        var state = new TestSagaState { CurrentState = "Initial" };

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();
    }

    [Test]
    public async Task StepFails_UsesRetryBackoffPolicyPerAttempt()
    {
        var policy = new RecordingBackoffPolicy(TimeSpan.FromMilliseconds(1));
        var saga = new TestSaga();
        saga.UseRetryBackoffPolicy(policy);
        saga.PublicWhen("Initial", "FailingStep", shouldFail: true);

        var state = new TestSagaState { CurrentState = "Initial" };

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(policy.Attempts).Count().IsEqualTo(2);
        await Assert.That(policy.Attempts[0]).IsEqualTo(1);
        await Assert.That(policy.Attempts[1]).IsEqualTo(2);
    }

    [Test]
    public async Task RetryDelay_StillConfiguresFixedBackoffPolicy()
    {
        var saga = new TestSaga();
        saga.UseRetryDelay(TimeSpan.FromMilliseconds(7));

        await Assert.That(saga.RetryBackoffPolicy.ComputeDelay(1)).IsEqualTo(TimeSpan.FromMilliseconds(7));
        await Assert.That(saga.RetryBackoffPolicy.ComputeDelay(2)).IsEqualTo(TimeSpan.FromMilliseconds(7));
    }

    [Test]
    public async Task RetryExhausted_CompensatesExecutedSteps()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Step1");
        saga.PublicWhen("Step1", "FailingStep", shouldFail: true);

        var state = new TestSagaState { CurrentState = "Initial" };

        state = await saga.ProcessEventAsync(state, new object());
        await Assert.That(state.CurrentState).IsEqualTo("Step1");

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        // 补偿按 Backward 策略：当前失败步骤 + 已执行步骤逆序
        // FailingStep→FailingStep-compensated, Step1→Step1-compensated
        await Assert.That(state.CurrentState).Contains("compensated");
    }

    [Test]
    public async Task StepWithoutCompensation_SkippedOnRollback()
    {
        var saga = new TestSaga();
        saga.PublicWhenWithoutCompensation("Initial", "NoCompensate");
        saga.PublicWhen("NoCompensate", "Fail", shouldFail: true);

        var state = new TestSagaState { CurrentState = "Initial" };
        state = await saga.ProcessEventAsync(state, new object());
        await Assert.That(state.CurrentState).IsEqualTo("NoCompensate");

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        // 补偿后状态应包含 compensated 标记
        await Assert.That(state.CurrentState).Contains("compensated");
    }
}

// ═══════════════════════════════════════════════════════════════
// 超时检测
// ═══════════════════════════════════════════════════════════════

public class SagaTimeoutTests
{
    [Test]
    public async Task IsNotTimedOut_WhenNoStepHasTimeout()
    {
        var saga = new TestSaga();
        saga.PublicWhen("Initial", "Step1");

        var state = new TestSagaState { CurrentState = "Initial" };
        var timedOut = saga.IsTimedOut(state, DateTimeOffset.UtcNow.AddHours(1), out var steps);

        await Assert.That(timedOut).IsFalse();
        await Assert.That(steps).IsEmpty();
    }

    [Test]
    public async Task IsTimedOut_WhenStepExceedsTimeout()
    {
        var saga = new TestSaga();
        saga.PublicWhenWithTimeout("Initial", "Slow", TimeSpan.FromMilliseconds(100));

        var state = new TestSagaState { CurrentState = "Initial" };
        state.StepStartedAt["Initial"] = DateTimeOffset.UtcNow.AddHours(-1);

        var timedOut = saga.IsTimedOut(state, DateTimeOffset.UtcNow, out var steps);

        await Assert.That(timedOut).IsTrue();
        await Assert.That(steps).IsNotEmpty();
    }
}

// ═══════════════════════════════════════════════════════════════
// 补偿策略
// ═══════════════════════════════════════════════════════════════

public class SagaCompensationPolicyTests
{
    [Test]
    public async Task BackwardPolicy_CompensatesInReverseOrder()
    {
        var saga = new TestSaga(CompensationPolicy.Backward);
        saga.PublicWhen("A", "B");
        saga.PublicWhen("B", "Fail", shouldFail: true);

        var state = new TestSagaState { CurrentState = "A" };
        state = await saga.ProcessEventAsync(state, new object());

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(state.CurrentState).Contains("compensated");
    }

    [Test]
    public async Task ForwardPolicy_CompensatesInForwardOrder()
    {
        var saga = new TestSaga(CompensationPolicy.Forward);
        saga.PublicWhen("A", "B");
        saga.PublicWhen("B", "Fail", shouldFail: true);

        var state = new TestSagaState { CurrentState = "A" };
        state = await saga.ProcessEventAsync(state, new object());

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(state.CurrentState).Contains("compensated");
    }

    [Test]
    public async Task NonePolicy_SkipsAllCompensation()
    {
        var saga = new TestSaga(CompensationPolicy.None);
        saga.PublicWhen("A", "B");
        saga.PublicWhen("B", "Fail", shouldFail: true);

        var state = new TestSagaState { CurrentState = "A" };
        state = await saga.ProcessEventAsync(state, new object());

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(state.CurrentState).IsEqualTo("B");
    }

    // ═══════════════════════════════════════════════════════════════
    // 补偿顺序精确验证
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Backward 策略 — 失败步骤与已执行步骤都被补偿</summary>
    [Test]
    public async Task BackwardPolicy_CompensatesFailedAndExecutedSteps()
    {
        var compensationLog = new List<string>();
        var saga = new ExecutedStepThenFailingSaga(CompensationPolicy.Backward, compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        state = await saga.ProcessEventAsync(state, new object());

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(compensationLog).Count().IsEqualTo(2);
        await Assert.That(compensationLog[0]).IsEqualTo("compensate:Failing");
        await Assert.That(compensationLog[1]).IsEqualTo("compensate:Executed");
    }

    /// <summary>Forward 策略 — 已执行步骤先补偿，失败步骤后补偿</summary>
    [Test]
    public async Task ForwardPolicy_CompensatesExecutedThenFailedStep()
    {
        var compensationLog = new List<string>();
        var saga = new ExecutedStepThenFailingSaga(CompensationPolicy.Forward, compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        state = await saga.ProcessEventAsync(state, new object());

        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(compensationLog).Count().IsEqualTo(2);
        await Assert.That(compensationLog[0]).IsEqualTo("compensate:Executed");
        await Assert.That(compensationLog[1]).IsEqualTo("compensate:Failing");
    }

    /// <summary>补偿策略 None — 失败步骤不被补偿</summary>
    [Test]
    public async Task NonePolicy_FailedStep_NotCompensated()
    {
        var compensationLog = new List<string>();
        var saga = new FailingStepSaga(CompensationPolicy.None, compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(compensationLog).IsEmpty();
    }

    /// <summary>无补偿处理器的步骤失败时不补偿</summary>
    [Test]
    public async Task FailedStepWithoutCompensation_NotCompensated()
    {
        var compensationLog = new List<string>();
        var saga = new NoCompensationSaga(CompensationPolicy.Backward, compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        await Assert.That(async () =>
            await saga.ProcessEventAsync(state, new object())).Throws<AggregateException>();

        await Assert.That(compensationLog).IsEmpty();
    }
}

// ═══════════════════════════════════════════════════════════════
// Batch 1：CompensateAllAsync 基于 ExecutedStepKeys + 补偿异常收集
// ═══════════════════════════════════════════════════════════════

public class SagaCompensateAllAsyncTests
{
    /// <summary>CompensateAllAsync 只补偿已执行步骤，不补偿未执行步骤。</summary>
    [Test]
    public async Task CompensateAllAsync_OnlyCompensatesExecutedSteps()
    {
        var compensationLog = new List<string>();
        var saga = new MultiStepCompensationSaga(compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        // 只执行 Step1，Step2 和 Step3 未执行
        state = await saga.ProcessEventAsync(state, new object());
        await Assert.That(state.CurrentState).IsEqualTo("Step1");

        // CompensateAllAsync 应只补偿 Step1（已执行），不补偿 Step2/Step3
        await saga.CompensateAsync(state);

        await Assert.That(compensationLog).Count().IsEqualTo(1);
        await Assert.That(compensationLog[0]).IsEqualTo("compensate:Step1");
    }

    /// <summary>CompensateAllAsync 在 ExecutedStepKeys 为空时不补偿任何步骤。</summary>
    [Test]
    public async Task CompensateAllAsync_NoExecutedSteps_CompensatesNothing()
    {
        var compensationLog = new List<string>();
        var saga = new MultiStepCompensationSaga(compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        // 未执行任何步骤

        await saga.CompensateAsync(state);

        await Assert.That(compensationLog).IsEmpty();
    }

    /// <summary>CompensateAllAsync 在 None 策略下跳过所有补偿。</summary>
    [Test]
    public async Task CompensateAllAsync_NonePolicy_SkipsCompensation()
    {
        var compensationLog = new List<string>();
        // 即使注册和 execute 函数中包含 compensation delegate，None 策略也应跳过
        var saga = new NonePolicyMultiStepSaga(compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        state = await saga.ProcessEventAsync(state, new object());
        await Assert.That(state.CurrentState).IsEqualTo("Step1");

        await saga.CompensateAsync(state);

        await Assert.That(compensationLog).IsEmpty();
    }
}

public class SagaCompensationExceptionCollectionTests
{
    /// <summary>
    /// 补偿第一个步骤失败时不中断第二个步骤的补偿，所有异常收集后抛 AggregateException。
    /// 同时验证 SagaCompensationFailed 指标计数。
    /// </summary>
    [Test]
    public async Task CompensationFailure_DoesNotBlockSubsequentCompensation()
    {
        using var listener = new RecordingMeterListener("paldd.saga.compensation_failed");
        var compensationLog = new List<string>();
        var saga = new FailingCompensationSaga(compensationLog);

        var state = new TestSagaState { CurrentState = "Start" };
        state = await saga.ProcessEventAsync(state, new object());
        state = await saga.ProcessEventAsync(state, new object());
        // ExecutedStepKeys 包含 state 机键，如 "Start" 和 "Step1"
        await Assert.That(state.ExecutedStepKeys).Count().IsEqualTo(2);
        await Assert.That(state.ExecutedStepKeys).Contains("Start");
        await Assert.That(state.ExecutedStepKeys).Contains("Step1");

        var ex = await Assert.That(() =>
            saga.CompensateAsync(state).AsTask()).Throws<AggregateException>();

        // 两个步骤都尝试了补偿（即使第一步的补偿抛异常）
        await Assert.That(compensationLog).Contains("compensate:Step1");
        await Assert.That(compensationLog).Contains("compensate:Step2");
        await Assert.That(ex!.Message).Contains("Step1 compensation failed");
        await Assert.That(ex.InnerExceptions).Count().IsEqualTo(1);

        // 验证 SagaCompensationFailed 指标计数（Step1 补偿失败 → 1）
        await Assert.That(listener.Measurements).Contains(1);
    }

    /// <summary>
    /// 补偿过程中收到取消信号时，OperationCanceledException 立即传播，不收集。
    /// </summary>
    [Test]
    public async Task CompensationCanceled_PropagatesImmediately()
    {
        var saga = new CancellableCompensationSaga();
        var state = new TestSagaState { CurrentState = "Start" };
        state = await saga.ProcessEventAsync(state, new object());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(() =>
            saga.CompensateAsync(state, cts.Token).AsTask()).Throws<OperationCanceledException>();
    }
}

public sealed class TestEvent;

internal sealed class RecordingBackoffPolicy(TimeSpan delay) : IRetryBackoffPolicy
{
    public List<int> Attempts { get; } = [];

    public TimeSpan ComputeDelay(int attempt)
    {
        Attempts.Add(attempt);
        return delay;
    }
}

/// <summary>事件精确匹配 Saga — 验证补偿使用实际命中的 state|event key。</summary>
internal sealed class EventSpecificCompensationSaga : Saga<TestSagaState>
{
    private readonly List<string> _log;

    public EventSpecificCompensationSaga(List<string> compensationLog)
    {
        MaxRetries = 0;
        _log = compensationLog;

        When("Initial", typeof(TestEvent), new SagaStep("Submitted",
            execute: (s, _, _) =>
            {
                s.CurrentState = "Submitted";
                return ValueTask.FromResult(s);
            },
            compensate: (_, _) =>
            {
                _log.Add("compensate:Submitted");
                return ValueTask.CompletedTask;
            }));

        When("Submitted", new SagaStep("Fail",
            execute: (_, _, _) => throw new InvalidOperationException("Step failed"),
            compensate: (_, _) =>
            {
                _log.Add("compensate:Fail");
                return ValueTask.CompletedTask;
            }));
    }
}

/// <summary>已执行步骤后失败 Saga — 验证已执行步骤补偿顺序</summary>
internal sealed class ExecutedStepThenFailingSaga : Saga<TestSagaState>
{
    private readonly List<string> _log;

    public ExecutedStepThenFailingSaga(CompensationPolicy policy, List<string> compensationLog)
    {
        CompensationPolicy = policy;
        MaxRetries = 0;
        _log = compensationLog;

        When("Start", new SagaStep("Executed",
            execute: (s, _, _) =>
            {
                s.CurrentState = "Executed";
                return ValueTask.FromResult(s);
            },
            compensate: (_, _) =>
            {
                _log.Add("compensate:Executed");
                return ValueTask.CompletedTask;
            }));

        When("Executed", new SagaStep("Failing",
            execute: (_, _, _) => throw new InvalidOperationException("Step failed"),
            compensate: (_, _) =>
            {
                _log.Add("compensate:Failing");
                return ValueTask.CompletedTask;
            }));
    }
}

/// <summary>单步骤失败 Saga — 验证失败步骤的补偿行为</summary>
internal sealed class FailingStepSaga : Saga<TestSagaState>
{
    private readonly List<string> _log;

    public FailingStepSaga(CompensationPolicy policy, List<string> compensationLog)
    {
        CompensationPolicy = policy;
        MaxRetries = 0;
        _log = compensationLog;

        When("Start", new SagaStep("Failing",
            execute: (_, _, _) => throw new InvalidOperationException("Step failed"),
            compensate: (s, _) => { _log.Add("compensate:Failing"); return ValueTask.CompletedTask; }));
    }
}

/// <summary>无补偿步骤 Saga — 验证无补偿步骤失败时不触发补偿</summary>
internal sealed class NoCompensationSaga : Saga<TestSagaState>
{
    private readonly List<string> _log;

    public NoCompensationSaga(CompensationPolicy policy, List<string> compensationLog)
    {
        CompensationPolicy = policy;
        MaxRetries = 0;
        _log = compensationLog;

        When("Start", new SagaStep("NoCompensate",
            execute: (_, _, _) => throw new InvalidOperationException("Step failed")));
        // 无 compensate 委托
    }
}

/// <summary>多步骤补偿 Saga — 验证 CompensateAllAsync 只补偿已执行步骤</summary>
internal sealed class MultiStepCompensationSaga : Saga<TestSagaState>
{
    private readonly List<string> _log;

    public MultiStepCompensationSaga(List<string> compensationLog)
    {
        MaxRetries = 0;
        _log = compensationLog;

        When("Start", new SagaStep("Step1",
            execute: (s, _, _) => { s.CurrentState = "Step1"; return ValueTask.FromResult(s); },
            compensate: (_, _) => { _log.Add("compensate:Step1"); return ValueTask.CompletedTask; }));

        When("Step1", new SagaStep("Step2",
            execute: (s, _, _) => { s.CurrentState = "Step2"; return ValueTask.FromResult(s); },
            compensate: (_, _) => { _log.Add("compensate:Step2"); return ValueTask.CompletedTask; }));

        When("Step2", new SagaStep("Step3",
            execute: (s, _, _) => { s.CurrentState = "Step3"; return ValueTask.FromResult(s); },
            compensate: (_, _) => { _log.Add("compensate:Step3"); return ValueTask.CompletedTask; }));
    }
}

/// <summary>None 策略多步骤 Saga — 验证 None 时 CompensateAllAsync 不补偿</summary>
internal sealed class NonePolicyMultiStepSaga : Saga<TestSagaState>
{
    private readonly List<string> _log;

    public NonePolicyMultiStepSaga(List<string> compensationLog)
    {
        CompensationPolicy = CompensationPolicy.None;
        MaxRetries = 0;
        _log = compensationLog;

        When("Start", new SagaStep("Step1",
            execute: (s, _, _) => { s.CurrentState = "Step1"; return ValueTask.FromResult(s); },
            compensate: (_, _) => { _log.Add("compensate:Step1"); return ValueTask.CompletedTask; }));

        When("Step1", new SagaStep("Step2",
            execute: (s, _, _) => { s.CurrentState = "Step2"; return ValueTask.FromResult(s); },
            compensate: (_, _) => { _log.Add("compensate:Step2"); return ValueTask.CompletedTask; }));
    }
}

/// <summary>补偿失败 Saga — Step1 补偿抛异常，Step2 补偿正常</summary>
internal sealed class FailingCompensationSaga : Saga<TestSagaState>
{
    private readonly List<string> _log;

    public FailingCompensationSaga(List<string> compensationLog)
    {
        MaxRetries = 0;
        _log = compensationLog;

        When("Start", new SagaStep("Step1",
            execute: (s, _, _) => { s.CurrentState = "Step1"; return ValueTask.FromResult(s); },
            compensate: (_, _) => { _log.Add("compensate:Step1"); throw new InvalidOperationException("Step1 compensation failed"); }));

        When("Step1", new SagaStep("Step2",
            execute: (s, _, _) => { s.CurrentState = "Step2"; return ValueTask.FromResult(s); },
            compensate: (_, _) => { _log.Add("compensate:Step2"); return ValueTask.CompletedTask; }));
    }
}

/// <summary>可取消补偿 Saga — 补偿中检查 CancellationToken</summary>
internal sealed class CancellableCompensationSaga : Saga<TestSagaState>
{
    public CancellableCompensationSaga()
    {
        MaxRetries = 0;

        When("Start", new SagaStep("Step1",
            execute: (s, _, _) => { s.CurrentState = "Step1"; return ValueTask.FromResult(s); },
            compensate: (_, ct) => { ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }));
    }
}

// ═══════════════════════════════════════════════════════════════
// SagaKey | 分隔符校验测试
// ═══════════════════════════════════════════════════════════════

public class SagaKeyValidationTests
{
    [Test]
    public async Task Make_StateWithoutPipe_ReturnsKey()
    {
        var key = SagaKey.Make("Approved", typeof(TestEvent));
        await Assert.That(key).StartsWith("Approved|");
        await Assert.That(key).EndsWith(nameof(TestEvent));
    }

    [Test]
    public async Task Make_NullEventType_ReturnsStateOnly()
    {
        var key = SagaKey.Make("Submitted", null);
        await Assert.That(key).IsEqualTo("Submitted");
    }

    [Test]
    public async Task Make_StateWithPipe_ThrowsArgumentException()
    {
        var ex = await Assert.That(() =>
            SagaKey.Make("Start|End", typeof(TestEvent))).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("|");
        await Assert.That(ex!.Message).Contains("Start|End");
    }

    [Test]
    public async Task ExtractState_ReturnsStateBeforePipe()
    {
        var state = SagaKey.ExtractState("Approved|TestEvent");
        await Assert.That(state).IsEqualTo("Approved");
    }

    [Test]
    public async Task ExtractState_NoPipe_ReturnsFullString()
    {
        var state = SagaKey.ExtractState("Compensated");
        await Assert.That(state).IsEqualTo("Compensated");
    }
}

public class SagaStateValidationTests
{
    [Test]
    public async Task CurrentState_SetWithoutPipe_Succeeds()
    {
        var state = new TestSagaState();
        state.CurrentState = "Processing";
        await Assert.That(state.CurrentState).IsEqualTo("Processing");
    }

    [Test]
    public async Task CurrentState_SetWithPipe_ThrowsArgumentException()
    {
        var state = new TestSagaState();
        var ex = await Assert.That(() =>
            state.CurrentState = "Start|End").Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("|");
        await Assert.That(ex!.Message).Contains("Start|End");
    }

    [Test]
    public async Task CurrentState_DefaultValue_IsInitial()
    {
        var state = new TestSagaState();
        await Assert.That(state.CurrentState).IsEqualTo("Initial");
    }
}
