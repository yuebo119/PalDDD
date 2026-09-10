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
/// <para>
/// <b>取消语义（v65 P3）</b>：外部 <c>ct</c> 取消 → OCE 传播（中止整条 FanOut）；
/// PerItemTimeout 触发 → 该项记为 <see cref="TimeoutException"/> 失败；
/// executor 自身抛出的非外部 OCE（内部超时/子 CTS 取消）→ 该项记为失败，不中止整体。
/// </para>
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
    public TimeSpan? PerItemTimeout
    {
        // v62 P3：负值 fail-fast（对齐 MaxConcurrency init 守卫——CancelAfter(负值) 立即取消全部子任务）
        get => _perItemTimeout;
        init
        {
            if (value is { } t && t < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), "PerItemTimeout must be non-negative.");
            _perItemTimeout = value;
        }
    }

    private readonly TimeSpan? _perItemTimeout;

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
        // 能力实证轮 v12（ITM-166 漏网姊妹）：null 延迟到执行期 NRE 被当业务失败空转
        // MaxRetries + 补偿——构造期 fail-fast 对齐 DynamicStep/ChildSagaStep
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(executor);
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
        // v40 P3（ITM-166 姊妹）：selector 返回 null 执行期 fail-fast——构造期无法校验
        // 委托行为（委托未执行），原实现 null 流到下方 items.Count 处 NRE，在编排器内
        // 被当作步骤失败空转 MaxRetries + 补偿；此处抛 ArgumentException 定位到配置
        // 错误源头（无子任务应返回空集合而非 null）。（无 paramName 重载——CA2208
        // 要求 paramName 匹配本方法形参，selector 非本方法参数）
        var items = _selector(state)
            ?? throw new ArgumentException(
                $"FanOut 步骤 '{Name}' 的 selector 对当前 SagaState 返回 null——无子任务请返回空集合。");
        if (items.Count == 0)
            return new([], Array.Empty<(TResult?, Exception)>());

        // P3 修复：0 时全部子任务挂起。v43 P3 注释勘正：原声称兜底"对象初始化器直接设
        // MaxConcurrency <= 0"路径已不可达（MaxConcurrency 为 init-only——init setter 赋值点
        // 即时校验并归一化，十七轮构造参数路径同样前置校验）。本行按纵深防御保留（构造函数
        // 路径与 init setter 均已保证 >0；此校验防未来新增写入点）。
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
                // v35 P3（EA1）勘正：Release 与 Wait 的配对依赖本行位于下方 try 块<b>之外</b>这一
                // 结构事实——WaitAsync(ct) 抛 OCE（外部取消）时异常直接传播，不进入 try/finally，
                // 不会执行 Release（未 acquire 即未 Release，无幽灵计数）。若未来把本行挪入
                // try 块，finally 的无条件 Release 将在 Wait 失败路径上产生幽灵 +1（信号量计数
                // 超 MaxConcurrency，后续 Release 抛 SemaphoreFullException）——届时必须引入
                // acquired 标志配对。当前结构配对正确，仅以注释固化前提，不做行为改动。
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
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // v65 P3：executor 自身抛出的 OCE（内部超时/自建子 CTS 取消/第三方库取消信号），
                    // 既非外部 ct 取消、也非上方 PerItemTimeout 分支——原实现使其逃逸 Task，
                    // Task.WhenAll 后在编排器被当作整体取消（单个子任务自取消中止整条 FanOut）。
                    // 按"该项失败"收集（对齐文件头"部分失败不阻断其他子任务"的设计契约）；
                    // 外部 ct 真取消时本分支过滤为假，OCE 照常传播（保留取消语义）。
                    lock (errors)
                        errors.Add((default, ex));
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
/// <param name="Failed">失败的子任务（含异常信息）。⚠️ v34 P3 勘正：Item 恒为 default——
/// 失败子任务未产生结果，定位靠 <paramref name="Failed"/> 元组的 Error 异常消息；携带
/// 失败输入项属 v3.0 接口扩展</param>
public readonly record struct FanOutResult<TResult>(
    IReadOnlyList<TResult> Completed,
    IReadOnlyList<(TResult? Item, Exception Error)> Failed)
{
    /// <summary>是否全部成功</summary>
    public bool AllSucceeded => Failed.Count == 0;
}
