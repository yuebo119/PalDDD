using PalDDD.Core.Diagnostics;
using System.Collections.Frozen;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// Saga 状态机编排器 — 长事务补偿 + 重试 + 超时
// ─────────────────────────────────────────────────────────────
//
// 💡 Saga 是什么？
//   ｜ 一个跨多个步骤的业务流程编排器。每个步骤可独立成功或失败。
//   ｜ 当任一步骤失败且重试耗尽时，Saga 执行补偿：回滚已成功执行的步骤。
//   ｜ 例如："下单→扣库存→扣款"中，如果"扣款"失败，Saga 会补偿"扣库存"（恢复库存）。
//   ｜
// 💡 步骤查找用 Dictionary 而非 List.Find：
//   ｜ List.Find 是 O(n)，当步骤数量增加时查找变慢。
//   ｜ Dictionary 是 O(1)，同时通过 _stepsInOrder 保持注册顺序用于补偿。
//
// 📁 文件拆分（Batch 8）：
//   ｜ 策略组件（补偿/超时）已提取为独立类：
//   ｜   SagaCompensation.cs · SagaTimeoutDetector.cs
//   ｜ 状态/步骤类型已提取：
//   ｜   SagaStatus.cs · SagaState.cs · SagaStep.cs
//   ｜ Saga<TState> 核心编排（When/ProcessEventAsync/HandleEventAsync）保持在单文件中。
//
// 🧩 Agent 增强能力（Batch 9）：
//   ｜ DispatchKind 枚举驱动步骤分发：FanOut / ChildSaga / Interrupt / Dynamic。
//   ｜ SagaExecutionObserver 贯穿全生命周期发射事件。
//   ｜ ISagaManager 提供中断恢复和子 Saga 执行。
// ─────────────────────────────────────────────────────────────

/// <summary>补偿策略</summary>
public enum CompensationPolicy
{
    /// <summary>无补偿 — 失败后不执行任何回滚</summary>
    None,

    /// <summary>逆序补偿 — 失败后按注册顺序逆序执行已执行步骤的补偿动作</summary>
    Backward,

    /// <summary>正序补偿 — 失败后按注册顺序正序执行补偿动作</summary>
    Forward
}

/// <summary>Saga 编排器基类 — 状态机 + 补偿 + 重试 + 超时 + Agent 增强能力</summary>
/// <typeparam name="TState">Saga 状态类型</typeparam>
/// <remarks>
/// 子类在构造函数中通过 <c>When</c> 方法定义状态转换规则。<br/>
/// 使用 FrozenDictionary 实现 O(1) 状态+事件查找（AOT 安全）。<br/>
/// 补偿按实际已执行步骤的逆序执行。<br/>
/// 内置重试策略：<see cref="MaxRetries"/> + <see cref="RetryBackoffPolicy"/> 控制失败重试。<br/>
/// 支持 4 种增强步骤类型：FanOut / ChildSaga / Interrupt / Dynamic。<br/>
/// 推荐使用 <see cref="ProcessEventAsync"/> 替代直接调用 <see cref="HandleEventAsync"/>，
/// 它自动处理重试和补偿。
/// </remarks>
public abstract class Saga<TState> where TState : SagaState, new()
{
    /// <summary>已注册的步骤 — Dictionary 确保 O(1) 按键查找，替代 List.Find 的 O(n)</summary>
    private readonly Dictionary<string, SagaStep> _stepsByKey = [];

    /// <summary>保持原始注册顺序用于补偿（Dictionary 本身保证插入顺序）</summary>
    private readonly List<(string Key, SagaStep Step)> _stepsInOrder = [];

    private FrozenDictionary<string, SagaStep>? _frozen;

    // ═══════════════════════════════════════════════════════════════
    // 策略组件（内部组合，非多态替换）
    // ═══════════════════════════════════════════════════════════════

    private SagaCompensation<TState>? _compensation;
    private SagaTimeoutDetector<TState>? _timeoutDetector;

    private SagaCompensation<TState> Compensation
        => _compensation ??= new(CompensationPolicy, _stepsByKey);

    private SagaTimeoutDetector<TState> TimeoutDetector
        => _timeoutDetector ??= new(_stepsInOrder);

    // ═══════════════════════════════════════════════════════════════
    // 策略配置
    // ═══════════════════════════════════════════════════════════════

    /// <summary>时间提供者 — 派生编排器可在测试中替换</summary>
    protected virtual TimeProvider Clock => TimeProvider.System;

    /// <summary>补偿策略 — 默认逆序补偿</summary>
    public CompensationPolicy CompensationPolicy { get; protected set; } = CompensationPolicy.Backward;

    /// <summary>最大重试次数 — 0 表示不重试</summary>
    public int MaxRetries { get; protected set; } = 3;

    /// <summary>重试退避策略 — 默认固定 1 秒，保持历史 RetryDelay 语义</summary>
    public IRetryBackoffPolicy RetryBackoffPolicy { get; protected set; } = new FixedBackoffPolicy(TimeSpan.FromSeconds(1));

    /// <summary>重试间隔 — 兼容旧配置，赋值时同步为固定退避策略</summary>
    public TimeSpan RetryDelay
    {
        get => RetryBackoffPolicy.ComputeDelay(1);
        protected set => RetryBackoffPolicy = new FixedBackoffPolicy(value);
    }

    /// <summary>获取所有已注册的步骤（按注册顺序）</summary>
    protected IReadOnlyList<(string Key, SagaStep Step)> Steps => _stepsInOrder;

    // ═══════════════════════════════════════════════════════════════
    // Saga 管理器（中断恢复 + 子 Saga 执行）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Saga 管理器——提供中断恢复和子 Saga 执行能力。<br/>
    /// 可由 DI 注入或手动设置。若为 null，ChildSagaStep 和恢复流程将不可用。
    /// </summary>
    public ISagaManager? SagaManager { get; set; }

    /// <summary>
    /// 解析子 Saga 编排器。派生类可重写以提供自定义解析逻辑（如从 DI 容器）。
    /// 默认返回 null。
    /// </summary>
    /// <typeparam name="TChildState">子 Saga 状态类型</typeparam>
    protected virtual Saga<TChildState>? ResolveChildSaga<TChildState>()
        where TChildState : SagaState, new()
        => null;

    // ═══════════════════════════════════════════════════════════════
    // 步骤注册
    // ═══════════════════════════════════════════════════════════════

    /// <summary>定义状态转换——通过事件类型精确匹配。</summary>
    /// <remarks>
    /// 必须在启动期单线程调用（通常从派生编排器的构造函数中调用）。<br/>
    /// 首次 <see cref="ProcessEventAsync"/> 调用后不得再添加步骤——<br/>
    /// <see cref="_stepsByKey"/> 和 <see cref="_frozen"/> 的读写无同步保护，<br/>
    /// 并发调用可能导致状态不一致或 FrozenDictionary 构建时的数据损坏。
    /// </remarks>
    internal void When(string state, Type eventType, SagaStep step)
    {
        var key = MakeKey(state, eventType);
        _stepsByKey[key] = step;             // O(1) 查找
        _stepsInOrder.Add((key, step));       // 保持补偿顺序
        _frozen = null;
    }

    /// <summary>定义状态转换——泛型版本，消除 typeof(TEvent) 样板</summary>
    protected void When<TEvent>(string state, SagaStep step)
        => When(state, typeof(TEvent), step);

    /// <summary>定义状态转换（简化版 — 不指定事件类型，任何事件触发）</summary>
    protected internal void When(string state, SagaStep step)
    {
        var key = MakeKey(state, null);
        _stepsByKey[key] = step;             // O(1) 查找
        _stepsInOrder.Add((key, step));       // 保持补偿顺序
        _frozen = null;
    }

    /// <summary>注册动态路由步骤——运行时根据状态决定下一步 key。</summary>
    protected void WhenDynamic(string state, DynamicStep step)
        => When(state, step);

    /// <summary>注册动态路由步骤（事件精确匹配版本）。</summary>
    protected void WhenDynamic<TEvent>(string state, DynamicStep step)
        => When<TEvent>(state, step);

    // ═══════════════════════════════════════════════════════════════
    // 事件处理（带重试 + 补偿 + Agent 增强能力分发）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>处理事件并自动重试 — 所有重试耗尽后补偿全部已执行步骤</summary>
    /// <remarks>
    /// 这是对外推荐的主入口。与 <see cref="HandleEventAsync"/> 不同，<br/>
    /// 此方法自动处理失败重试和补偿编排。<br/>
    /// 支持 4 种增强步骤类型：FanOut / ChildSaga / Interrupt / Dynamic。<br/>
    /// 补偿范围：所有已成功执行的步骤（按注册顺序或逆序，由 <see cref="CompensationPolicy"/> 控制），<br/>
    /// 而非仅当前失败的步骤。<br/>
    /// 重试策略见 <see cref="MaxRetries"/> 和 <see cref="RetryBackoffPolicy"/>。
    /// </remarks>
    /// <param name="current">当前 Saga 状态</param>
    /// <param name="event">触发事件</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>新的 Saga 状态</returns>
    public async ValueTask<TState> ProcessEventAsync(TState current, object @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(@event);

        var eventType = @event.GetType();
        var requestedStepKey = MakeKey(current.CurrentState, eventType);

        // 查找实际命中的注册步骤：精确匹配使用 state|event，通配匹配使用 state。
        var match = FindStep(current, requestedStepKey);
        if (match is null)
            return current;

        var (stepKey, step) = match.Value;
        var wasCompleted = current.Status == SagaStatus.Completed;

        // 记录实际命中步骤的开始时间，用于精确超时计算。
        if (!current.StepStartedAt.TryGetValue(stepKey, out var startedAt))
        {
            startedAt = Clock.GetUtcNow();
            current.StepStartedAt[stepKey] = startedAt;
        }

        var observer = SagaExecutionObserver.Current;

        // ── 分发：根据 DispatchKind 选择执行路径 ──
        switch (step.DispatchKind)
        {
            case StepDispatchKind.FanOut:
                return await ExecuteFanOutStepAsync(
                    current, stepKey, step, wasCompleted, startedAt, observer, ct);

            case StepDispatchKind.ChildSaga:
#pragma warning disable IL2026, IL3050 // ChildSaga dispatch uses reflection; safe in non-AOT scenarios
                return await ExecuteChildSagaStepAsync(
                    current, stepKey, step, @event, wasCompleted, startedAt, observer, ct);
#pragma warning restore IL2026, IL3050

            case StepDispatchKind.Interrupt:
                return ExecuteInterruptStep(
                    current, stepKey, (InterruptStep)step, wasCompleted, startedAt, observer);

            case StepDispatchKind.Dynamic:
                return await ExecuteDynamicStepAsync(
                    current, stepKey, (DynamicStep)step, @event, wasCompleted, startedAt, observer, ct);

            default:
                // Normal — 标准重试循环
                return await ExecuteNormalStepAsync(
                    current, stepKey, step, @event, wasCompleted, startedAt, observer, ct);
        }
    }

    private static void RecordExecutedStep(TState current, TState result, string stepKey, DateTimeOffset startedAt)
    {
        if (!result.StepStartedAt.ContainsKey(stepKey))
            result.StepStartedAt[stepKey] = startedAt;

        foreach (var key in current.ExecutedStepKeys)
            if (!result.ExecutedStepKeys.Contains(key))
                result.ExecutedStepKeys.Add(key);

        if (!result.ExecutedStepKeys.Contains(stepKey))
            result.ExecutedStepKeys.Add(stepKey);
    }

    // ═══════════════════════════════════════════════════════════════
    // Normal 步骤执行（标准重试 + 补偿）
    // ═══════════════════════════════════════════════════════════════

    private async ValueTask<TState> ExecuteNormalStepAsync(
        TState current, string stepKey, SagaStep step, object @event,
        bool wasCompleted, DateTimeOffset startedAt,
        SagaExecutionObserver? observer, CancellationToken ct)
    {
        List<Exception> failures = [];

        // Emit step started
        if (observer is not null)
            await observer.OnStepStarted(current.SagaId, stepKey, ct).ConfigureAwait(false);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = (TState)await step.ExecuteAsync(current, @event, ct).ConfigureAwait(false);
                sw.Stop();

                if (!wasCompleted && result.Status == SagaStatus.Completed)
                    PalMetrics.SagaCompleted.Add(1);

                RecordExecutedStep(current, result, stepKey, startedAt);

                if (observer is not null)
                    await observer.OnStepCompleted(current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                // 失败但还有重试次数 — 等待后继续
                // 使用 Clock（TimeProvider）控制延迟，测试中可注入 FakeTimeProvider 实现确定性重试时序
                failures.Add(ex);
                await Task.Delay(RetryBackoffPolicy.ComputeDelay(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 所有重试耗尽 — 补偿所有已成功执行的步骤（含当前步骤如果有补偿）
                failures.Add(ex);

                if (observer is not null)
                    await observer.OnStepFailed(current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                await CompensateExecutedStepsAsync(current, stepKey, step, ct).ConfigureAwait(false);
                throw new AggregateException(
                    $"Saga step '{stepKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}). See inner exceptions for each attempt.",
                    failures);
            }
        }
        return current;
    }

    // ═══════════════════════════════════════════════════════════════
    // FanOut 步骤执行
    // ═══════════════════════════════════════════════════════════════

    private async ValueTask<TState> ExecuteFanOutStepAsync(
        TState current, string stepKey, SagaStep step,
        bool wasCompleted, DateTimeOffset startedAt,
        SagaExecutionObserver? observer, CancellationToken ct)
    {
        if (step is not IInternalFanOutStep fanOutStep)
            throw new InvalidOperationException(
                $"Step '{stepKey}' has DispatchKind.FanOut but does not implement IInternalFanOutStep.");

        List<Exception> failures = [];

        if (observer is not null)
            await observer.OnStepStarted(current.SagaId, stepKey, ct).ConfigureAwait(false);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await fanOutStep.ExecuteFanOutAsync(current, ct).ConfigureAwait(false);
                sw.Stop();

                if (!result.AllSucceeded)
                {
                    // 部分失败——合并异常
                    var aggEx = new AggregateException(
                        $"FanOut step '{stepKey}' had {result.Failed.Count} failures out of {result.Failed.Count + result.Completed.Count} items.",
                        result.Failed.Select(f => f.Error));
                    throw aggEx;
                }

                if (!wasCompleted && current.Status != SagaStatus.Completed)
                    PalMetrics.SagaCompleted.Add(1);

                RecordExecutedStep(current, current, stepKey, startedAt);

                if (observer is not null)
                    await observer.OnStepCompleted(current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);

                return current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                failures.Add(ex);
                await Task.Delay(RetryBackoffPolicy.ComputeDelay(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);

                if (observer is not null)
                    await observer.OnStepFailed(current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                await CompensateExecutedStepsAsync(current, stepKey, step, ct).ConfigureAwait(false);
                throw new AggregateException(
                    $"FanOut step '{stepKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}). See inner exceptions.",
                    failures);
            }
        }
        return current;
    }

    // ═══════════════════════════════════════════════════════════════
    // ChildSaga 步骤执行
    // ═══════════════════════════════════════════════════════════════

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("ChildSaga dispatch relies on reflection to resolve sagas and create state; add partial class wiring for AOT.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("ChildSaga dispatch uses MakeGenericMethod/MakeGenericType; not compatible with native AOT.")]
    private async ValueTask<TState> ExecuteChildSagaStepAsync(
        TState current, string stepKey, SagaStep step, object @event,
        bool wasCompleted, DateTimeOffset startedAt,
        SagaExecutionObserver? observer, CancellationToken ct)
    {
        if (step is not IInternalChildSagaStep childStep)
            throw new InvalidOperationException(
                $"Step '{stepKey}' has DispatchKind.ChildSaga but does not implement IInternalChildSagaStep.");

        var childStateType = childStep.ChildStateType;

        // Resolve the child saga orchestrator
        var resolved = ResolveChildSagaByType(childStateType);
        if (resolved is null)
            throw new InvalidOperationException(
                $"Child saga orchestrator for state type '{childStateType.Name}' could not be resolved. "
                + "Override ResolveChildSaga<T>() or inject an ISagaManager.");

        var manager = SagaManager ?? new DefaultSagaManager();

        List<Exception> failures = [];

        if (observer is not null)
            await observer.OnStepStarted(current.SagaId, stepKey, ct).ConfigureAwait(false);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Create child state and apply input
                var childState = CreateChildState(childStateType);
                var input = childStep.ExtractInput(current);

                // Execute child saga via non-generic dispatch (avoids dynamic keyword)
                var childEvent = new ChildSagaInputEvent(input);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var finalChildState = await manager.ExecuteChildSagaNonGenericAsync(
                    resolved, childState, childEvent, ct).ConfigureAwait(false);
                sw.Stop();

                // Apply child output back to parent
                childStep.ApplyOutput(current, finalChildState);

                if (!wasCompleted && current.Status != SagaStatus.Completed)
                    PalMetrics.SagaCompleted.Add(1);

                RecordExecutedStep(current, current, stepKey, startedAt);

                if (observer is not null)
                    await observer.OnStepCompleted(current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);

                return current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                failures.Add(ex);
                await Task.Delay(RetryBackoffPolicy.ComputeDelay(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);

                if (observer is not null)
                    await observer.OnStepFailed(current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                await CompensateExecutedStepsAsync(current, stepKey, step, ct).ConfigureAwait(false);
                throw new AggregateException(
                    $"ChildSaga step '{stepKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}). See inner exceptions.",
                    failures);
            }
        }
        return current;
    }

    /// <summary>通过反射解析子 Saga 编排器（绕过泛型约束——仅用于 AOT 非目标场景）。</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Child saga resolution relies on reflection; add partial class wiring for AOT.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Uses MakeGenericMethod for generic method dispatch; not compatible with native AOT.")]
    private object? ResolveChildSagaByType(Type childStateType)
    {
        // Try the generic ResolveChildSaga first via reflection
        var method = typeof(Saga<TState>).GetMethod(
            nameof(ResolveChildSaga),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (method is not null)
        {
            var generic = method.MakeGenericMethod(childStateType);
            return generic.Invoke(this, null);
        }

        return null;
    }

    /// <summary>通过反射创建子 Saga 状态实例（仅用于 AOT 非目标场景）。</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Child state instantiation relies on Activator.CreateInstance; provide a factory for AOT.")]
    private static SagaState CreateChildState(Type childStateType)
    {
        var instance = Activator.CreateInstance(childStateType)
            ?? throw new InvalidOperationException(
                $"Could not create instance of {childStateType.Name}.");
        return (SagaState)instance;
    }

    /// <summary>子 Saga 输入事件——包装输入数据传递给子 Saga。</summary>
    internal sealed class ChildSagaInputEvent
    {
        public object? Input { get; }
        public ChildSagaInputEvent(object? input) => Input = input;
    }

    // ═══════════════════════════════════════════════════════════════
    // Interrupt 步骤执行
    // ═══════════════════════════════════════════════════════════════

    private TState ExecuteInterruptStep(
        TState current, string stepKey, InterruptStep step,
        bool wasCompleted, DateTimeOffset startedAt,
        SagaExecutionObserver? observer)
    {
        // 挂起 Saga：设置 AwaitingHumanDecision 状态
        var oldStatus = current.Status;
        current.Status = SagaStatus.AwaitingHumanDecision;
        current.InterruptReason = step.InterruptReason;

        RecordExecutedStep(current, current, stepKey, startedAt);

        // 注册到 DefaultSagaManager（如果可用）
        if (SagaManager is DefaultSagaManager defaultManager)
            defaultManager.RegisterInterrupted(current.SagaId, step.InterruptReason, step.DecisionType);

        // Emit status change via observer (fire-and-forget in sync context)
        if (observer is not null)
        {
#pragma warning disable CA2012 // ValueTask 不应被忽略——Interrupt 步骤为同步执行，观察者事件为尽力通知，丢失不影响正确性
            _ = observer.OnStatusChanged(current.SagaId, oldStatus, SagaStatus.AwaitingHumanDecision, CancellationToken.None);
#pragma warning restore CA2012
        }

        return current;
    }

    // ═══════════════════════════════════════════════════════════════
    // Dynamic 步骤执行
    // ═══════════════════════════════════════════════════════════════

    private async ValueTask<TState> ExecuteDynamicStepAsync(
        TState current, string stepKey, DynamicStep step, object @event,
        bool wasCompleted, DateTimeOffset startedAt,
        SagaExecutionObserver? observer, CancellationToken ct)
    {
        // 路由到目标步骤 key
        var targetKey = step.Route(current);

        // 在已注册步骤中查找
        var dict = GetFrozen();
        if (!dict.TryGetValue(targetKey, out var routedStep))
        {
            // 目标未找到——无操作
            return current;
        }

        // 记录动态步骤本身
        RecordExecutedStep(current, current, stepKey, startedAt);

        if (observer is not null)
            await observer.OnStepStarted(current.SagaId, stepKey, ct).ConfigureAwait(false);

        // 递归分发到路由步骤（可能也是特殊步骤）
        // 使用 HandleEventAsync 的查找逻辑，但走的是当前状态+事件类型的匹配
        var (matchedKey, matchedStep) = (targetKey, routedStep);

        List<Exception> failures = [];
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = (TState)await matchedStep.ExecuteAsync(current, @event, ct).ConfigureAwait(false);
                sw.Stop();

                if (!wasCompleted && result.Status == SagaStatus.Completed)
                    PalMetrics.SagaCompleted.Add(1);

                RecordExecutedStep(current, result, matchedKey, startedAt);

                if (observer is not null)
                {
                    await observer.OnStepCompleted(current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                failures.Add(ex);
                await Task.Delay(RetryBackoffPolicy.ComputeDelay(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);

                if (observer is not null)
                    await observer.OnStepFailed(current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                await CompensateExecutedStepsAsync(current, matchedKey, matchedStep, ct).ConfigureAwait(false);
                throw new AggregateException(
                    $"Dynamic step '{stepKey}' routed to '{matchedKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}). See inner exceptions.",
                    failures);
            }
        }
        return current;
    }

    // ═══════════════════════════════════════════════════════════════
    // HandleEventAsync（无重试无补偿，直接执行）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>处理事件 — 查找匹配的状态转换并执行（无重试，无补偿）</summary>
    public async ValueTask<TState> HandleEventAsync(TState current, object @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(@event);

        var stepKey = MakeKey(current.CurrentState, @event.GetType());
        var match = FindStep(current, stepKey);
        return match is null ? current : (TState)await match.Value.Step.ExecuteAsync(current, @event, ct).ConfigureAwait(false);
    }

    /// <summary>查找匹配步骤（不执行），返回实际命中的注册键。</summary>
    private (string Key, SagaStep Step)? FindStep(TState current, string stepKey)
    {
        var dict = GetFrozen();

        // 先精确匹配（state + eventType），再按状态通配匹配。
        if (dict.TryGetValue(stepKey, out var step))
            return (stepKey, step);

        var wildcardKey = MakeKey(current.CurrentState, null);
        return dict.TryGetValue(wildcardKey, out step)
            ? (wildcardKey, step)
            : null;
    }

    // ═══════════════════════════════════════════════════════════════
    // 补偿
    // ═══════════════════════════════════════════════════════════════

    private async ValueTask CompensateExecutedStepsAsync(
        TState state, string failedStepKey, SagaStep failedStep, CancellationToken ct)
    {
        var observer = SagaExecutionObserver.Current;
        if (observer is not null)
            await observer.OnCompensationStarted(state.SagaId, failedStepKey, ct).ConfigureAwait(false);

        await Compensation.CompensateExecutedStepsAsync(state, failedStepKey, failedStep, ct).ConfigureAwait(false);
    }

    /// <inheritdoc cref="SagaCompensation{TState}.CompensateAllAsync"/>
    public async ValueTask CompensateAsync(TState state, CancellationToken ct = default)
        => await Compensation.CompensateAllAsync(state, ct).ConfigureAwait(false);

    // ═══════════════════════════════════════════════════════════════
    // 超时检测
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc cref="SagaTimeoutDetector{TState}.IsTimedOut"/>
    public bool IsTimedOut(TState state, DateTimeOffset now, out IReadOnlyList<SagaStep> timedOutSteps)
        => TimeoutDetector.IsTimedOut(state, now, out timedOutSteps);

    // ═══════════════════════════════════════════════════════════════
    // 内部辅助
    // ═══════════════════════════════════════════════════════════════

    private static string MakeKey(string state, Type? eventType)
        => SagaKey.Make(state, eventType);

    private FrozenDictionary<string, SagaStep> GetFrozen()
        => _frozen ??= _stepsByKey.ToFrozenDictionary(StringComparer.Ordinal);
}
