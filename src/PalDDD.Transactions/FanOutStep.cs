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
public sealed class FanOutStep<TItem, TResult> : SagaStep, IInternalFanOutStep
    where TItem : notnull
{
    private readonly Func<SagaState, IReadOnlyList<TItem>> _selector;
    private readonly Func<TItem, CancellationToken, ValueTask<TResult>> _executor;

    /// <inheritdoc/>
    public override StepDispatchKind DispatchKind => StepDispatchKind.FanOut;

    /// <summary>最大并发数 — 默认等于 CPU 核心数</summary>
    public int MaxConcurrency { get; init; } = Environment.ProcessorCount;

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
    public FanOutStep(
        string key,
        Func<SagaState, IReadOnlyList<TItem>> selector,
        Func<TItem, CancellationToken, ValueTask<TResult>> executor,
        Func<SagaState, CancellationToken, ValueTask>? compensate = null,
        TimeSpan? timeout = null)
        : base(key, execute: null!, compensate, timeout)
    {
        _selector = selector;
        _executor = executor;
    }

    /// <summary>执行 Fan-out：并行分发所有子任务，收集完成项与失败项。</summary>
    internal async ValueTask<FanOutResult<TResult>> ExecuteFanOutAsync(
        SagaState state, CancellationToken ct)
    {
        var items = _selector(state);
        if (items.Count == 0)
            return new([], Array.Empty<(TResult?, Exception)>());

        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var results = new TResult?[items.Count];
        List<(TResult?, Exception)> errors = [];
        var tasks = new Task[items.Count];

        for (int i = 0; i < items.Count; i++)
        {
            var idx = i;
            var item = items[i];
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var cts = PerItemTimeout.HasValue
                        ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                        : null;
                    if (cts is not null)
                        cts.CancelAfter(PerItemTimeout!.Value);
                    var token = cts?.Token ?? ct;
                    results[idx] = await _executor(item, token).ConfigureAwait(false);
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

        return new FanOutResult<TResult>(
            results.Where(r => r is not null).Select(r => r!).ToArray(),
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
