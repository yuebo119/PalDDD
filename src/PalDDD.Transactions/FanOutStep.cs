// ─────────────────────────────────────────────────────────────
// 🪭 FanOutStep — 并行 Fan-out 步骤
// ─────────────────────────────────────────────────────────────
//
// 💡 什么是 Fan-out？
//   ｜ 将一个 Saga 步骤拆分为 N 个子任务并行执行，收集全部结果。
//   ｜ 例如：审批 Saga 中"并行通知所有审批者"。
//   ｜
// 💡 设计决策：
//   ｜ 部分失败不阻断其他子任务（最佳尽力并行）。
//   ｜ 所有异常收集到 FanOutResult.Failed 中由编排器决定后续策略。
//   ｜ SemaphoreSlim 控制并发上限（默认 Environment.ProcessorCount）。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>内部 Fan-out 步骤接口——非泛型调度。</summary>
internal interface IInternalFanOutStep
{
    ValueTask<FanOutResult<object?>> ExecuteFanOutAsync(SagaState state, CancellationToken ct);
}

/// <summary>
/// 并行 Fan-out 步骤——将一批子任务并行分发执行，收集结果。
/// </summary>
/// <typeparam name="TItem">子任务输入项类型</typeparam>
/// <typeparam name="TResult">子任务输出类型</typeparam>
/// <remarks>
/// ⚠️ <b>结果消费契约（P3 声明·十七轮）</b>：<see cref="FanOutResult{TResult}.Completed"/>
/// 集合<b>仅由 executor 副作用消费</b>——编排器（<see cref="Saga{TState}"/> 的 FanOut
/// 分发路径）只检查 <see cref="FanOutResult{TResult}.AllSucceeded"/> 决定成败/补偿，
/// 不会读取 Completed 内容，也不会把它写回 <see cref="SagaState"/>。子任务结果需要
/// 留存时，executor 必须在自身逻辑内写入状态（副作用）；无 outputApplier 之类的
/// 自动回传通道。
/// </remarks>
public sealed class FanOutStep<TItem, TResult> : SagaStep, IInternalFanOutStep
    where TItem : notnull
{
    private readonly Func<SagaState, IReadOnlyList<TItem>> _selector;
    private readonly Func<TItem, CancellationToken, ValueTask<TResult>> _executor;

    /// <inheritdoc/>
    public override StepDispatchKind DispatchKind => StepDispatchKind.FanOut;

    private int _maxConcurrency = Environment.ProcessorCount;

    /// <summary>最大并发数 — 默认等于 CPU 核心数。0 表示使用默认核数；负值抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    /// <remarks>
    /// ITM-166 修复：init 赋值路径前置校验——对象初始化器 <c>new FanOutStep(...) { MaxConcurrency = -1 }</c>
    /// 在构造完成后赋值，原先只由 ExecuteFanOutAsync 运行时兜底（失败延迟到执行期）；
    /// init setter 在赋值点即时抛错，与构造函数参数路径同语义。
    /// </remarks>
    public int MaxConcurrency
    {
        get => _maxConcurrency;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxConcurrency = value > 0 ? value : Environment.ProcessorCount;
        }
    }

    /// <summary>每个子任务的超时时间（可选）</summary>
    public TimeSpan? PerItemTimeout { get; init; }

    /// <summary>
    /// 创建 Fan-out 步骤。
    /// </summary>
    /// <param name="key">步骤 key</param>
    /// <param name="selector">从 Saga 状态提取子任务输入集合</param>
    /// <param name="executor">每个子任务的执行逻辑</param>
    /// <param name="compensate">补偿动作（可选）</param>
    /// <param name="timeout">整体步骤超时（可选）</param>
    /// <param name="maxConcurrency">最大并发数；0（默认）表示使用 CPU 核心数，负数抛 <see cref="ArgumentOutOfRangeException"/></param>
    public FanOutStep(
        string key,
        Func<SagaState, IReadOnlyList<TItem>> selector,
        Func<TItem, CancellationToken, ValueTask<TResult>> executor,
        Func<SagaState, CancellationToken, ValueTask>? compensate = null,
        TimeSpan? timeout = null,
        int maxConcurrency = 0)
        : base(key, execute: null!, compensate, timeout)
    {
        _selector = selector;
        _executor = executor;
        // P3 修复（十七轮）：MaxConcurrency 校验上移构造函数——原 ThrowIfNegativeOrZero
        // 在 ExecuteFanOutAsync 内，非法值延迟到运行时首跳才爆；构造参数路径即时失败。
        // 0 保持"默认核数"语义（与 init 属性默认值一致）。
        // ITM-166 修复：init 初始化器路径同步由 MaxConcurrency.init setter 前置校验
        // （见属性声明），执行时校验仅作纵深防御保留。
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcurrency);
        MaxConcurrency = maxConcurrency > 0 ? maxConcurrency : Environment.ProcessorCount;
    }

    /// <summary>执行 Fan-out：并行分发所有子任务，收集完成项与失败项。</summary>
    internal async ValueTask<FanOutResult<TResult>> ExecuteFanOutAsync(
        SagaState state, CancellationToken ct)
    {
        var items = _selector(state);
        if (items.Count == 0)
            return new([], Array.Empty<(TResult?, Exception)>());

        // P3 修复：0 时全部子任务挂起（P3·十七轮：构造参数路径已前置校验，
        // 此处兜底对象初始化器直接设 MaxConcurrency <= 0 的路径）
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrency);
        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var results = new TResult?[items.Count];
        // P2 定案（可空结果过滤）：以完成标记收集而非非空过滤——TResult 为可空引用类型
        // 且子任务合法返回 null 时，旧实现把成功结果误判丢弃（Completed 少计）。
        var completedFlags = new bool[items.Count];
        List<(TResult?, Exception)> errors = [];
        var tasks = new Task[items.Count];

        for (int i = 0; i < items.Count; i++)
        {
            var idx = i;
            var item = items[i];
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                using var cts = PerItemTimeout.HasValue
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;
                try
                {
                    if (cts is not null)
                        cts.CancelAfter(PerItemTimeout!.Value);
                    var token = cts?.Token ?? ct;
                    results[idx] = await _executor(item, token).ConfigureAwait(false);
                    completedFlags[idx] = true;
                }
                catch (OperationCanceledException) when (cts is not null && cts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // PerItemTimeout 触发的超时（linked CTS 取消，但外部 ct 未取消）：
                    // 转为失败而非静默丢弃——调用方需感知子任务超时（ITM-001）。
                    lock (errors)
                        errors.Add((default, new TimeoutException(
                            $"FanOut 子任务 [{idx}] 超过 PerItemTimeout {PerItemTimeout!.Value.TotalMilliseconds}ms")));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (errors)
                        errors.Add((default, ex));
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var completed = new List<TResult?>(items.Count);
        for (int i = 0; i < results.Length; i++)
        {
            if (completedFlags[i])
                completed.Add(results[i]);
        }

        return new FanOutResult<TResult>(
            completed.ToArray(),
            errors.AsReadOnly());
    }

    /// <summary>非泛型调度入口 — 映射到 object? 结果。</summary>
    async ValueTask<FanOutResult<object?>> IInternalFanOutStep.ExecuteFanOutAsync(
        SagaState state, CancellationToken ct)
    {
        var result = await ExecuteFanOutAsync(state, ct).ConfigureAwait(false);
        return new FanOutResult<object?>(
            result.Completed.Select(r => (object?)r).ToArray(),
            result.Failed.Select(f => ((object?)f.Item, f.Error)).ToArray());
    }
}

/// <summary>Fan-out 执行结果。</summary>
/// <typeparam name="TResult">子任务输出类型</typeparam>
/// <param name="Completed">成功完成的子任务结果</param>
/// <param name="Failed">失败的子任务（含异常信息）</param>
public readonly record struct FanOutResult<TResult>(
    IReadOnlyList<TResult> Completed,
    IReadOnlyList<(TResult? Item, Exception Error)> Failed)
{
    /// <summary>是否全部成功</summary>
    public bool AllSucceeded => Failed.Count == 0;
}
