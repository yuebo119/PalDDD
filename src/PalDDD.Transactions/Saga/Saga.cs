using PalDDD.Core.Diagnostics;
using System.Collections.Frozen;
using PalUlid = ByteAether.Ulid.Ulid;

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

    /// <summary>保持原始注册顺序用于补偿（v29 P3 勘正：注册顺序由本 List 维护——Dictionary 枚举序无保证）</summary>
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
    /// <remarks>ITM-186 修复（二十九轮）：负值无语义（for 循环零次执行、静默跳过），setter 拒绝。</remarks>
    private int _maxRetries = 3;

    public int MaxRetries
    {
        get => _maxRetries;
        protected set
        {
            // ITM-186 修复：负重试次数会让重试循环零次执行、ProcessEventAsync 静默返回
            // current（步骤未执行也无异常），调用方误以为走完——断言非负。
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxRetries));
            _maxRetries = value;
        }
    }

    /// <summary>重试退避策略 — 默认固定 1 秒，保持历史 RetryDelay 语义</summary>
    public IRetryBackoffPolicy RetryBackoffPolicy { get; protected set; } = new FixedBackoffPolicy(TimeSpan.FromSeconds(1));

    /// <summary>重试间隔 — 兼容旧配置，赋值时同步为固定退避策略</summary>
    public TimeSpan RetryDelay
    {
        get => RetryBackoffPolicy.ComputeDelay(1);
        // v17 声明（v34 P3 勘正）：protected set 不做 null 校验——派生类显式置 null 属
        // 程序化配置错误。v17 原"重试路径 NRE 自曝"已被 v30 ComputeRetryDelaySafely
        // catch(Exception) 吞掉静默降级 1s 推翻：null 策略经降级守卫静默 1s，无异常暴露
        // ——配置错误检测靠调用方启动期校验（ValidateOnStart 模式）与步骤异常复现，非 NRE。
                protected set => RetryBackoffPolicy = new FixedBackoffPolicy(value);
    }

    /// <summary>
    /// v30 P3：重试延迟计算降级守卫（镜像 v29 OutboxBatchProcessor 的 ComputeDelay 降级形态）。
    /// 四处重试路径（Normal/FanOut/ChildSaga/Dynamic）原先裸调
    /// <c>Task.Delay(RetryBackoffPolicy.ComputeDelay(...))</c>——自定义策略的 ComputeDelay
    /// 抛异常时，策略异常会替换原始步骤异常向上传播（Task.Delay 实参求值即抛，步骤根因
    /// failures 与 AggregateException 均不再到达调用方）。降级为默认 1s（对齐 RetryDelay
    /// 默认 FixedBackoffPolicy(1s) 语义），保证重试循环按原始异常路径继续。
    /// </summary>
    /// <remarks>Saga 上下文无 IPalLogger（v29 OutboxBatchProcessor 形态的 Warning 分支不可用）——
    /// 静默降级 + 注释声明可观测性取舍：策略配置错误由后续步骤异常的稳定复现暴露，
    /// 不叠加新的异常源。</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "v30 P3：自定义退避策略的任意计算异常降级为 1s 默认延迟（镜像 v29 OutboxBatchProcessor 同款）——策略故障不得替换/吞掉原始步骤异常。")]
    private TimeSpan ComputeRetryDelaySafely(int attempt)
    {
        try
        {
            return RetryBackoffPolicy.ComputeDelay(attempt);
        }
        catch (Exception)
        {
            return TimeSpan.FromSeconds(1);
        }
    }

    /// <summary>获取所有已注册的步骤（按注册顺序）</summary>
    protected IReadOnlyList<(string Key, SagaStep Step)> Steps => _stepsInOrder;

    // ═══════════════════════════════════════════════════════════════
    // Saga 管理器（中断恢复 + 子 Saga 执行）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Saga 管理器——提供中断恢复和子 Saga 执行能力。<br/>
    /// 可由 DI 注入或手动设置。若为 null，ChildSagaStep 和恢复流程将不可用。<br/>
    /// ⚠️ <b>中断后果（P3 声明·十七轮；三十四轮兜底落地）</b>：<see cref="InterruptStep"/> 执行时若本属性为 null
    /// （或非 <see cref="DefaultSagaManager"/> 类型），中断条目无处注册——该 Saga 状态已被置为
    /// <see cref="SagaStatus.AwaitingHumanDecision"/> 且无人能消费后续人工决策，将<b>滞留</b>在中断态。
    /// 滞留时长由中断步骤的 <see cref="SagaStep.Timeout"/> 决定：<br/>
    /// - <b>配置了 Timeout</b>：超期后 <c>SagaProcessor.CheckTimeoutsAsync</c> 扫描
    ///   （三十四轮起 <c>LeaseActiveSagasAsync</c> 纳入 AwaitingHumanDecision）命中
    ///   <c>IsTimedOut</c>，触发补偿回滚（Status → Compensated）——人工决策超时兜底；<br/>
    /// - <b>未配置 Timeout</b>：显式无限等待——仅人工决策可恢复，超时扫描永不命中。<br/>
    /// 使用 Interrupt 步骤前必须设置本属性。
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
        // P3 修复（八轮）：重复注册同 key 时先移除旧步骤再追加——否则 _stepsInOrder
        // 残留已作废步骤（补偿/超时扫描会执行它），且顺序列表与 _stepsByKey 不一致
        _stepsInOrder.RemoveAll(existing => existing.Key == key);
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
        // P3 修复（八轮）：同上——重复注册同 key 时移除旧步骤，避免补偿/超时执行作废步骤
        _stepsInOrder.RemoveAll(existing => existing.Key == key);
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
            return current; // P3-SRC-101 宽容语义；注：此路径发生在 SafeObserveStarted 前，观察端零观测（v17 声明的不对称，刻意保留以兼容无匹配静默跳过的既有消费形态）

        var (stepKey, step) = match.Value;
        var wasCompleted = current.Status == SagaStatus.Completed;

        // 记录实际命中步骤的开始时间，用于精确超时计算。
        // ⚠️ v17 边界声明：StepStartedAt 只写不清、重入不刷新——状态回流到已执行步骤并滞留
        // 超期时会再次命中 IsTimedOut 触发兜底补偿（"滞留→补偿"宣称语义的自然延伸，
        // 与三十四轮中断态纳入同设计）。重入场景需要刷新时间戳的调用方请自行管理。
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
                    current, stepKey, step, wasCompleted, startedAt, observer, ct).ConfigureAwait(false);

            case StepDispatchKind.ChildSaga:
#pragma warning disable IL2026, IL3050 // ChildSaga dispatch uses reflection; safe in non-AOT scenarios
                return await ExecuteChildSagaStepAsync(
                    current, stepKey, step, @event, wasCompleted, startedAt, observer, ct).ConfigureAwait(false);
#pragma warning restore IL2026, IL3050

            case StepDispatchKind.Interrupt:
                // v29 P3：wasCompleted 死参数移除——ExecuteInterruptStep 挂起语义不产出 Completed
                // 终态（置 AwaitingHumanDecision），其余分发路径用于 Completed 指标判重的该参数
                // 在本路径从未被读取；私有方法单调用点，签名同步收敛
                return ExecuteInterruptStep(
                    current, stepKey, (InterruptStep)step, startedAt, observer);

            case StepDispatchKind.Dynamic:
                return await ExecuteDynamicStepAsync(
                    current, stepKey, (DynamicStep)step, @event, wasCompleted, startedAt, observer, ct).ConfigureAwait(false);

            default:
                // Normal — 标准重试循环
                return await ExecuteNormalStepAsync(
                    current, stepKey, step, @event, wasCompleted, startedAt, observer, ct).ConfigureAwait(false);
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

    // ─────────────────────────────────────────────────────────────
    // ITM-212：Observer best-effort 安全调用——Sink 异常不影响业务结果
    // ─────────────────────────────────────────────────────────────

    // ── SafeObserve 族边界声明（v17 补文，四方法共用）──
    // catch 过滤 `when (obsEx is not OCE)`：Sink 抛 OCE 时会传播（与普通异常不同）。
    // 理由：OCE 视为"调用方取消意图的镜像"应透传；已知副作用是 Failed 观察点在 catch 块内
    // 调用时 Sink 的 OCE 会替换原步骤异常——ITM-212 家族共有语义，触发需 Sink 自身抛出
    // 未关联外部取消的 OCE，窗口窄。派生 saga 的 RetryBackoffPolicy（protected set）同理
    // 无守卫——置 null 属程序化配置错误，框架不防（v34 P3 勘正：null 策略经
    // ComputeRetryDelaySafely 降级守卫静默 1s，无异常暴露——配置错误检测靠调用方
    // 启动期校验（ValidateOnStart 模式）与步骤异常复现，非 NRE）。
    private static async ValueTask SafeObserveCompletedAsync(
        SagaExecutionObserver? observer, PalUlid sagaId, string stepKey, TimeSpan elapsed, CancellationToken ct)
    {
        if (observer is null) return;
        try
        {
            await observer.OnStepCompleted(sagaId, stepKey, elapsed, ct).ConfigureAwait(false);
        }
        catch (Exception obsEx) when (obsEx is not OperationCanceledException)
        {
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "saga.observer.step-completed-failed",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = obsEx.Message, ["step"] = stepKey }));
        }
    }

    // 三十八轮 P3 修复（ITM-212 防护补全）：OnStepStarted/OnStepFailed 原为直调——Sink 抛异常
    // 会使步骤未执行即失败 / 替换遮蔽原始步骤异常，与 ISagaEventSink "尽力语义"不符。
    // 对齐 SafeObserveCompletedAsync 同款隔离。

    private static async ValueTask SafeObserveStartedAsync(
        SagaExecutionObserver? observer, PalUlid sagaId, string stepKey, CancellationToken ct)
    {
        if (observer is null) return;
        try
        {
            await observer.OnStepStarted(sagaId, stepKey, ct).ConfigureAwait(false);
        }
        catch (Exception obsEx) when (obsEx is not OperationCanceledException)
        {
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "saga.observer.step-started-failed",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = obsEx.Message, ["step"] = stepKey }));
        }
    }

    /// <summary>必须在原步骤异常的 catch 块内调用——观察者异常被吞，原始异常继续向上传播。</summary>
    private static async ValueTask SafeObserveFailedAsync(
        SagaExecutionObserver? observer, PalUlid sagaId, string stepKey, Exception stepError, CancellationToken ct)
    {
        if (observer is null) return;
        try
        {
            await observer.OnStepFailed(sagaId, stepKey, stepError, ct).ConfigureAwait(false);
        }
        catch (Exception obsEx) when (obsEx is not OperationCanceledException)
        {
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "saga.observer.step-failed-notify-error",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = obsEx.Message, ["step"] = stepKey }));
        }
    }

    private async ValueTask<TState> ExecuteNormalStepAsync(
        TState current, string stepKey, SagaStep step, object @event,
        bool wasCompleted, DateTimeOffset startedAt,
        SagaExecutionObserver? observer, CancellationToken ct)
    {
        List<Exception> failures = [];

        // Emit step started
        await SafeObserveStartedAsync(observer, current.SagaId, stepKey, ct).ConfigureAwait(false);

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

                // ITM-212：Observer best-effort——Sink 异常不重放业务步骤
                await SafeObserveCompletedAsync(observer, current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);

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
                await Task.Delay(ComputeRetryDelaySafely(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 所有重试耗尽 — 补偿所有已成功执行的步骤（含当前步骤如果有补偿）
                failures.Add(ex);

                // 三十八轮 P3：观察者异常被吞，原始步骤异常 ex 继续传播
                await SafeObserveFailedAsync(observer, current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                // P3 修复（十七轮）：补偿自身抛出会替换原步骤失败异常向上传播，步骤根因
                // （failures）丢失——catch 后把补偿异常并入 failures 抛出，外层
                // AggregateException 同时携带步骤失败与补偿失败
                try
                {
                    await CompensateExecutedStepsAsync(current, stepKey, step, ct).ConfigureAwait(false);
                }
                catch (Exception compensationEx) when (compensationEx is not OperationCanceledException)
                {
                    failures.Add(compensationEx);
                    throw new AggregateException(
                        $"Saga step '{stepKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}) and compensation also failed. See inner exceptions.",
                        failures);
                }
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

        await SafeObserveStartedAsync(observer, current.SagaId, stepKey, ct).ConfigureAwait(false);

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

                // 指标修正（三轮评审反弹终结）：FanOut/ChildSaga 步骤不写 Status——
                // saga 完成由用户回调（如 ApplyOutput 设 Completed）或后续步骤决定。
                // 与 Normal 路径同判 current.Status == Completed：用户回调设置了完成态
                // 则计数；FanOutStep 从不写 Status 时本指标不触发是正确行为。
                if (!wasCompleted && current.Status == SagaStatus.Completed)
                    PalMetrics.SagaCompleted.Add(1);

                RecordExecutedStep(current, current, stepKey, startedAt);

                await SafeObserveCompletedAsync(observer, current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);

                return current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                failures.Add(ex);
                await Task.Delay(ComputeRetryDelaySafely(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);

                // 三十八轮 P3：观察者异常被吞，原始步骤异常 ex 继续传播
                await SafeObserveFailedAsync(observer, current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                // P3 修复（十七轮）：补偿失败嵌套（见 ExecuteNormalStepAsync 同名修复注释）——
                // 补偿异常并入 failures 抛出，不吞 FanOut 步骤根因
                try
                {
                    await CompensateExecutedStepsAsync(current, stepKey, step, ct).ConfigureAwait(false);
                }
                catch (Exception compensationEx) when (compensationEx is not OperationCanceledException)
                {
                    failures.Add(compensationEx);
                    throw new AggregateException(
                        $"FanOut step '{stepKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}) and compensation also failed. See inner exceptions.",
                        failures);
                }
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
                + "Override ResolveChildSaga<T>() to return the child saga orchestrator.");

        // P3-SRC-401 声明：临时 manager 使"使用 Interrupt 前须设 SagaManager"的前提不可检测——
        // 子 saga 含 InterruptStep 且父未设 manager 时条目落入临时实例（执行完丢弃），滞留中断态
        // 无警告；含 Interrupt 的子 saga 必须显式设 SagaManager（见 InterruptStep 的 HITL 设计注释）。
        var manager = SagaManager ?? new DefaultSagaManager();

        List<Exception> failures = [];

        await SafeObserveStartedAsync(observer, current.SagaId, stepKey, ct).ConfigureAwait(false);

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

                // 指标修正（三轮评审反弹终结）：FanOut/ChildSaga 步骤不写 Status——
                // saga 完成由用户回调（如 ApplyOutput 设 Completed）或后续步骤决定。
                // 与 Normal 路径同判 current.Status == Completed：用户回调设置了完成态
                // 则计数；FanOutStep 从不写 Status 时本指标不触发是正确行为。
                if (!wasCompleted && current.Status == SagaStatus.Completed)
                    PalMetrics.SagaCompleted.Add(1);

                RecordExecutedStep(current, current, stepKey, startedAt);

                await SafeObserveCompletedAsync(observer, current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);

                return current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                failures.Add(ex);
                await Task.Delay(ComputeRetryDelaySafely(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);

                // 三十八轮 P3：观察者异常被吞，原始步骤异常 ex 继续传播
                await SafeObserveFailedAsync(observer, current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                // P3 修复（十七轮）：补偿失败嵌套（见 ExecuteNormalStepAsync 同名修复注释）——
                // 补偿异常并入 failures 抛出，不吞 ChildSaga 步骤根因
                try
                {
                    await CompensateExecutedStepsAsync(current, stepKey, step, ct).ConfigureAwait(false);
                }
                catch (Exception compensationEx) when (compensationEx is not OperationCanceledException)
                {
                    failures.Add(compensationEx);
                    throw new AggregateException(
                        $"ChildSaga step '{stepKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}) and compensation also failed. See inner exceptions.",
                        failures);
                }
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
        DateTimeOffset startedAt,
        SagaExecutionObserver? observer)
    {
        // v28 P3 修复：注册前移到状态变更之前——原顺序先改状态（AWD + RecordExecutedStep）
        // 后注册，RegisterInterrupted 抛 IOE（v27 失效集拒绝：Saga 已被超时补偿失效）时
        // 状态污染无回滚（内存实体滞留 AWD 但中断条目未建立）。前移后注册成功才改状态，
        // 失效拒绝路径状态零污染。时序评估：ResumeDispatch 闭包捕获的是 current 实例引用、
        // 延迟到人工决策（ResumeAsync）时才调用，届时状态已改；RecordExecutedStep 为纯
        // 内存写（注册后执行不抛）；OnStatusChanged 仍在注册后发射——对外语义不变。
        // 注册到 DefaultSagaManager（如果可用）
        // P3 声明（十七轮）：SagaManager 为 null 或非 DefaultSagaManager 时本块整体跳过——
        // 下方把状态置为 AwaitingHumanDecision，但中断条目无处注册，人工决策无人消费，
        // saga 永久滞留中断态（详见 SagaManager 属性 XML doc 的后果声明）
        if (SagaManager is DefaultSagaManager defaultManager)
        {
            // P2 修复（八轮）：注册时捕获恢复派发闭包——ResumeAsync 到达决策时以决策为
            // 事件重新进入 ProcessEventAsync 管线。此前仅暂存决策且无人消费（恢复链路断裂）
            // P3 修复（二十一轮）：不再传 step.DecisionType——RegisterInterrupted 已删除该
            // 参数（下游字段只写不读的死状态，详见其注释）
            // v34 P3：不再传 step.InterruptReason——reason 参数同理只写不读（中断原因仍由
            // SagaState.InterruptReason 承载，条目无需副本），见 RegisterInterrupted 注释
            defaultManager.RegisterInterrupted(
                current.SagaId,
                async (decision, dispatchCt) =>
                    await ProcessEventAsync(current, decision, dispatchCt).ConfigureAwait(false));
        }

        // 挂起 Saga：设置 AwaitingHumanDecision 状态
        var oldStatus = current.Status;
        current.Status = SagaStatus.AwaitingHumanDecision;
        current.InterruptReason = step.InterruptReason;

        RecordExecutedStep(current, current, stepKey, startedAt);

        // Emit status change via observer（能力实证轮 v12 补隔离——原 fire-and-forget 只抑制了
        // CA2012 警告，Sink 同步抛异常仍会逃逸，违背 :280 "ITM-212 观察者异常不影响业务结果"——
        // 对齐 SafeObserve 族第五个观察点；此上下文无法 await（Interrupt 同步），catch 后记
        // Activity 事件，与 fire-and-forget 的尽力语义一致）
        if (observer is not null)
        {
#pragma warning disable CA2012 // ValueTask 不应被忽略——Interrupt 步骤为同步执行
            try
            {
                var vt = observer.OnStatusChanged(current.SagaId, oldStatus, SagaStatus.AwaitingHumanDecision, CancellationToken.None);
                // v13 异步半面补全（CAP-2 完整闭环）：同步 try-catch 只捕半面——真异步 Sink 故障
                // 落入被丢弃的 ValueTask 无任何观测。Preserve 后挂 OnlyOnFaulted 延续记 Activity
                // （尽力观测，不阻塞 Interrupt 同步路径）。
                if (!vt.IsCompletedSuccessfully)
                    _ = vt.Preserve().AsTask().ContinueWith(
                        t => RecordObserverFault(t.Exception!.GetBaseException()),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default); // v14 修正：漏传 OnlyOnFaulted——原版每次正常完成也执行延续，t.Exception 为 null 时延续内 NRE 被静默吞掉
            }
            catch (Exception obsEx) when (obsEx is not OperationCanceledException)
            {
                RecordObserverFault(obsEx);
            }
#pragma warning restore CA2012
        }

        return current;

        // v13：观察者故障的统一记录点（同步 catch 与异步 OnlyOnFaulted 延续共用）
        void RecordObserverFault(Exception obsEx)
        {
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "saga.observer.status-changed-failed",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = obsEx.Message }));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Dynamic 步骤执行
    // ═══════════════════════════════════════════════════════════════

    private async ValueTask<TState> ExecuteDynamicStepAsync(
        TState current, string stepKey, DynamicStep step, object @event,
        bool wasCompleted, DateTimeOffset startedAt,
        SagaExecutionObserver? observer, CancellationToken ct)
    {
        // P3-SRC-603（R45）声明：观察者 OnStepStarted/Completed/Failed 上报的是 DynamicStep
        // 注册键（stepKey 参数）而非路由目标键（matchedKey）——实际执行体与计时对象是 matchedKey。
        // 耗时/失败在观察端归因到 Dynamic 入口名下属刻意设计（跟踪 Dynamic 分发总量）。
        // 路由到目标步骤 key
        var targetKey = step.Route(current);

        // 在已注册步骤中查找
        var dict = GetFrozen();
        if (!dict.TryGetValue(targetKey, out var routedStep))
        {
            // P3-SRC-101 设计意图声明：未注册 key 静默返回 current 为刻意宽容——对比下方
            // ITM-069 对"路由到特殊步骤"的显式拒绝：路由表可演进（滚动发布窗口期新事件
            // 先到、旧版本未注册新步骤 key），未知 key 容忍使 saga 停留当前状态等待后续
            // 事件，而非炸掉整条管线；特殊步骤分发则是结构性错误（execute 为 null!，必
            // NRE），故显式拒绝。两种处置的差异是刻意的
            return current;
        }

        // 记录动态步骤本身
        RecordExecutedStep(current, current, stepKey, startedAt);

        await SafeObserveStartedAsync(observer, current.SagaId, stepKey, ct).ConfigureAwait(false);

        // 递归分发到路由步骤（可能也是特殊步骤）
        // 使用 HandleEventAsync 的查找逻辑，但走的是当前状态+事件类型的匹配
        var (matchedKey, matchedStep) = (targetKey, routedStep);

        // ITM-069：特殊步骤（FanOut/Interrupt/Dynamic/ChildSaga）的 execute 为 null!，
        // 直接路由分发会 NRE 且错误信息不指向真实原因。显式拒绝并给出可定位的错误。
        if (matchedStep.DispatchKind != StepDispatchKind.Normal)
            throw new InvalidOperationException(
                $"DynamicStep 路由目标 '{matchedKey}' 是 {matchedStep.DispatchKind} 类型步骤，不支持事件路由分发；路由目标必须是普通步骤。");

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

                // ITM-212：Observer best-effort——Sink 异常不重放业务步骤
                // P3-SRC-214：删除冗余 observer null 包裹——SafeObserveCompletedAsync 内部
                // 已判 null，对齐 Normal/FanOut/ChildSaga 三路径的直调形态
                await SafeObserveCompletedAsync(observer, current.SagaId, stepKey, sw.Elapsed, ct).ConfigureAwait(false);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                failures.Add(ex);
                await Task.Delay(ComputeRetryDelaySafely(attempt + 1), Clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);

                // 三十八轮 P3：观察者异常被吞，原始步骤异常 ex 继续传播
                await SafeObserveFailedAsync(observer, current.SagaId, stepKey, ex, ct).ConfigureAwait(false);

                // P3 修复（十七轮）：补偿失败嵌套（见 ExecuteNormalStepAsync 同名修复注释）——
                // 补偿异常并入 failures 抛出，不吞 Dynamic 路由步骤根因
                try
                {
                    await CompensateExecutedStepsAsync(current, matchedKey, matchedStep, ct).ConfigureAwait(false);
                }
                catch (Exception compensationEx) when (compensationEx is not OperationCanceledException)
                {
                    failures.Add(compensationEx);
                    throw new AggregateException(
                        $"Dynamic step '{stepKey}' routed to '{matchedKey}' failed after {attempt + 1} attempts (MaxRetries={MaxRetries}) and compensation also failed. See inner exceptions.",
                        failures);
                }
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
    /// <remarks>
    /// v25 P3 行为族 B4（契约声明）：经本入口执行的步骤不记录
    /// <see cref="SagaState.ExecutedStepKeys"/>/<see cref="SagaState.StepStartedAt"/>——
    /// 后续 <see cref="ProcessEventAsync"/> 失败触发的补偿范围不含此步骤；
    /// 需要参与补偿轨迹的步骤应走 <see cref="ProcessEventAsync"/>。
    /// </remarks>
    public async ValueTask<TState> HandleEventAsync(TState current, object @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(@event);

        var stepKey = MakeKey(current.CurrentState, @event.GetType());
        var match = FindStep(current, stepKey);
        if (match is null) return current;

        // 修复覆盖残留（ITM-069 只守卫了 ProcessEventAsync 路由路径）：本入口同样
        // 不支持特殊步骤（FanOut/ChildSaga/Interrupt/Dynamic 的 execute 为 null! 契约）
        if (match.Value.Step.DispatchKind != StepDispatchKind.Normal)
            throw new InvalidOperationException(
                $"HandleEventAsync 命中步骤 '{match.Value.Key}' 是 {match.Value.Step.DispatchKind} 类型，仅支持在编排器 When(...) 注册的普通步骤上直接事件处理；特殊步骤须走 ProcessEventAsync。");

        return (TState)await match.Value.Step.ExecuteAsync(current, @event, ct).ConfigureAwait(false);
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
        // v9 P2-1 修复：OnCompensationStarted 原为直 await——Sink 抛异常时下方真实补偿被跳过，
        // 且异常被外层 catch (compensationEx) 误并入"补偿也失败"聚合（补偿实际未执行却报失败）。
        // 对齐 SafeObserve 族（ITM-212 姊妹补全）：观察者异常隔离为 Activity 事件，补偿照常执行。
        await SafeObserveCompensationStartedAsync(observer, state.SagaId, failedStepKey, ct).ConfigureAwait(false);

        await Compensation.CompensateExecutedStepsAsync(state, failedStepKey, failedStep, ct).ConfigureAwait(false);
    }

    /// <summary>补偿开始观察的隔离版（v9 P2-1）——Sink 异常不得阻断真实补偿（ISagaEventSink 尽力语义）。</summary>
    private static async ValueTask SafeObserveCompensationStartedAsync(
        SagaExecutionObserver? observer, PalUlid sagaId, string stepKey, CancellationToken ct)
    {
        if (observer is null) return;
        try
        {
            await observer.OnCompensationStarted(sagaId, stepKey, ct).ConfigureAwait(false);
        }
        catch (Exception obsEx) when (obsEx is not OperationCanceledException)
        {
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "saga.observer.compensation-started-failed",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = obsEx.Message, ["step"] = stepKey }));
        }
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
