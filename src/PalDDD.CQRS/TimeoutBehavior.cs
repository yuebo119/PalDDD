// ─────────────────────────────────────────────────────────────
// ⏱️ TimeoutBehavior — 请求级超时预算
// ─────────────────────────────────────────────────────────────
// 语义边界（必读）：
//   1. 超时只放弃等待，不中断 handler——管道 next 委托不接受 CancellationToken
//      （PipelineStateMachine 用 Reset 时捕获的原始 ct 调用 handler），超时后
//      handler 仍可能在后台继续执行直至自行完成。
//   2. 区分「超时」与「调用方取消」：调用方取消原样透传 OperationCanceledException，
//      只有超时转 TimeoutException（HTTP 层映射 504）。
//   3. 快路径零分配：next 同步完成时直接返回原 ValueTask，不构造 Timer/Task。
//
namespace PalDDD.CQRS;

/// <summary>超时行为的配置载体——由 <c>AddPalTimeoutBehavior</c> 注册为单例。</summary>
/// <remarks>
/// 单例注册、后注册覆盖先注册（<c>GetRequiredService</c> 取最后一个 descriptor）；
/// 需要按请求类型给不同预算时，实现自定义 <see cref="IPipelineBehavior{TRequest,TResponse}"/>，
/// 不要依赖本类型承载多值配置。
/// </remarks>
internal sealed record PalTimeoutBehaviorOptions(TimeSpan Timeout);

/// <summary>超时管道行为——对超过配置预算的请求抛出 <see cref="TimeoutException"/>。</summary>
/// <remarks>
/// 注册方式：<c>services.AddPalTimeoutBehavior(TimeSpan.FromSeconds(30))</c>（开放泛型，JIT）
/// 或 <c>services.AddPalTimeoutBehavior&lt;TRequest, TResponse&gt;(...)</c>（闭合泛型，Native AOT 安全）。<br/>
/// 默认不注册：超时预算是业务决策，框架不替调用方选默认值。
/// <para>⚠️ <b>只能放弃等待</b>：handler 不响应取消信号时工作仍会跑完，框架不强制中断
/// （进程内无法安全中止任意异步工作）。需要真正中断时应在 handler 内外层调用点自行传递
/// <see cref="CancellationToken"/>。</para>
/// <para>⚠️ <b>与流式读互不影响</b>：<c>MapStreamQuery</c> 不经 Dispatcher、不走本行为，
/// 其超时由 <c>StreamRunRegistry.MaxRunDuration</c> 独立控制。</para>
/// </remarks>
internal sealed class TimeoutBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly TimeSpan _timeout;

    public TimeoutBehavior(PalTimeoutBehaviorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Timeout, "Timeout must be greater than zero.");

        _timeout = options.Timeout;
    }

    public ValueTask<TResponse> HandleAsync(
        TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next)
    {
        var task = next();
        // 快路径：已同步完成（含同步抛出的异常已由 next 直接抛出）——零分配透传
        if (task.IsCompletedSuccessfully)
            return task;

        return WaitWithTimeoutAsync(task, ct);
    }

    // 显式非泛型实现——与内置 Validation/Logging 同款，免 DIM 桥的闭包 + Func 两笔分配
    ValueTask<object?> IPipelineBehavior.HandleAsync(
        IBaseRequest request, CancellationToken ct, Func<ValueTask<object?>> next)
    {
        var task = next();
        if (task.IsCompletedSuccessfully)
            return task;

        return WaitWithTimeoutObjectAsync(task, ct);
    }

    private async ValueTask<TResponse> WaitWithTimeoutAsync(ValueTask<TResponse> task, CancellationToken ct)
    {
        // ValueTask 只能被消费一次——转 Task 后才能用 WaitAsync 获得超时能力。
        // WaitAsync 的取消语义：超时抛 TimeoutException，外部 ct 取消抛 OCE，两者天然分流。
        var running = task.AsTask();
        try
        {
            return await running.WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveFault(running);
            throw CreateTimeoutException();
        }
    }

    private async ValueTask<object?> WaitWithTimeoutObjectAsync(ValueTask<object?> task, CancellationToken ct)
    {
        var running = task.AsTask();
        try
        {
            return await running.WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveFault(running);
            throw CreateTimeoutException();
        }
    }

    private TimeoutException CreateTimeoutException()
        => new($"Request {typeof(TRequest).Name} exceeded the configured timeout of {_timeout}.");

    /// <summary>
    /// 观察被放弃任务的异常——超时路径下 handler 仍可能在后台失败，不观察会让异常
    /// 在 Finalizer 线程以 UnobservedTaskException 形式出现（噪声，且丢失原始堆栈上下文）。
    /// </summary>
    /// <remarks>
    /// v4 批 B（2026-11-07，独立 P3 可观测项）：原实现只 <c>_ = t.Exception</c> 静默吞掉，
    /// 超时后 abandoned handler 的失败在日志与追踪里完全不可见。本项<b>不改变运行时行为</b>
    /// （仍不重抛、不影响 504 时序），只把故障记到当前 Activity 事件，供采集侧观察。
    /// 局限须明说：<c>OnlyOnFaulted</c> 续体只覆盖「失败收尾」，成功跑完的 abandoned 任务
    /// 依然不可见——因此这是可观测补强，<b>不是 B4 根因修复</b>（根因是 scope 生命周期，
    /// 见 <c>docs/decisions/038-timeout-abandoned-scope.md</c> 阶段 2）。
    /// </remarks>
    private static void ObserveFault(Task task)
        => _ = task.ContinueWith(
            static t =>
            {
                // 先取出异常（观察），再记事件——事件记录失败不得让异常重新变成未观察。
                var ex = t.Exception;
                try
                {
                    System.Diagnostics.Activity.Current?.AddEvent(new(
                        "pal.timeout.abandoned-handler-fault",
                        tags: new System.Diagnostics.ActivityTagsCollection
                        {
                            ["error"] = ex?.GetBaseException().Message
                        }));
                }
#pragma warning disable CA1031 // 可观测动作的失败不得让原异常重新变成未观察异常
                catch (Exception)
#pragma warning restore CA1031
                {
                    // Activity 事件记录失败（监听器抛异常等）时保持原语义：异常已被观察。
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
