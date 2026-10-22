// ═══════════════════════════════════════════════════════════════
// 🧵 断线续跑运行注册表（ADR-031）— 单写者 + 单附着者 + 有界环形缓冲
// ═══════════════════════════════════════════════════════════════
// run 独立于 HTTP 请求存在：连接断开只解除附着（detach），不取消 run。
// 取消仅来自三条显式路径：cancel 端点 / DetachGrace 超时（无附着宽限期）/ run 预算超限。
//
// 与 EventBus 的边界（ADR-031 D8）：单写者、单附着者、无订阅模型、无跨请求投递保证——
// 这是会话缓冲，不是消息总线。EventBus 移除判决不受影响。
//
// 作用域所有权（ADR-031 实施附记）：run 活过请求作用域，因此 handler 必须从 run 自有
// IServiceScope 解析（scoped 依赖如 DbContext 在请求结束会被销毁）。run 在终态或过期
// 清理时确定性释放该 scope——这正是 ADR-030 被否的「dispatcher 持有对象变体」缺失的
// 释放责任，此处由 run 生命周期显式承担。

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using PalDDD.CQRS;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text.Json.Serialization.Metadata;

namespace PalDDD.Hosting.AspNetCore;

/// <summary>断线续跑选项（ADR-031）。</summary>
/// <remarks>
/// 内存上界 ≈ 2 × <see cref="MaxConcurrentRuns"/> × <see cref="MaxBufferedItems"/> × 平均项大小
/// （运行中与保留终态各至多 <see cref="MaxConcurrentRuns"/>；总跟踪数达到 2×上限时
/// 最旧的终态 run 被提前逐出，<see cref="RetentionAfterCompletion"/> 退化为尽力而为）。
/// token 项约百字节量级时，1000 run × 1024 项 ≈ 100MB 量级，部署按实际项大小调参。
/// </remarks>
public sealed class DetachedStreamOptions
{
    /// <summary>每个 run 的环形缓冲容量（按项计数）。溢出丢最旧并记录最旧可用 index。默认 1024。</summary>
    public int MaxBufferedItems { get; set; } = 1024;

    /// <summary>无附着宽限期——最后一个附着断开后超过该时长仍无新附着则取消 run。默认 5 分钟。</summary>
    /// <remarks>这是「不白烧 token」的落点：用户关页永不回来时 run 自行终止。</remarks>
    public TimeSpan DetachGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>单 run 最长存活时长，到时取消。默认 10 分钟。</summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>单 run 最多产出项数，到时取消。默认 100,000。</summary>
    public int MaxItems { get; set; } = 100_000;

    /// <summary>终态 run 的保留时长（供完成后回看）。默认 10 分钟。</summary>
    public TimeSpan RetentionAfterCompletion { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>并发 run 上限，超出时启动端点返回 503。默认 1,000。</summary>
    public int MaxConcurrentRuns { get; set; } = 1_000;

    /// <summary>为 true 时新附着在已有活跃附着存在时返回 409（默认 false = 接管旧连接）。</summary>
    public bool RejectNewAttach { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBufferedItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DetachGrace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxRunDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetentionAfterCompletion, TimeSpan.Zero);
        // F21（2026-10-21）：Task.Delay 上限为 uint.MaxValue-1 毫秒（约 49.7 天）——超过时
        // 宽限计时任务抛 ArgumentOutOfRangeException，原实现落入丢弃 Task 且无人观察。
        var maxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DetachGrace, maxDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRunDuration, maxDelay);
    }
}

/// <summary>run 状态（ADR-031 D3：四态，终态不可迁移）。</summary>
internal enum StreamRunState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>并发 run 超上限（端点映射为 503）。</summary>
public sealed class StreamRunCapacityExceededException : Exception
{
    public StreamRunCapacityExceededException()
        : base("Detached stream run capacity exceeded.")
    {
    }

    public StreamRunCapacityExceededException(string message)
        : base(message)
    {
    }

    public StreamRunCapacityExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StreamRunCapacityExceededException(int limit)
        : base($"Detached stream run capacity exceeded (limit {limit}).")
    {
    }
}

/// <summary>run 的非泛型管理面（注册表持有与生命周期清理用）。</summary>
internal interface IStreamRun : IDisposable
{
    StreamRunState State { get; }

    int AttachedCount { get; }

    DateTimeOffset TerminalAt { get; }

    void Cancel();
}

/// <summary>run 的缓冲项——event/id 在写入时固化（ADR-031：附着回放不重跑选择器）。</summary>
internal readonly record struct BufferedStreamItem<TItem>(long Index, TItem Item, string? EventName, string? Id);

/// <summary>单个流式 run：状态机 + 环形缓冲 + 自有 CTS + 完成信号 + 附着管理。</summary>
internal sealed class StreamRun<TItem> : IStreamRun, IDisposable
{
    private readonly Lock _gate = new();
    private readonly BufferedStreamItem<TItem>[] _buffer;
    private readonly DetachedStreamOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceScope? _scope;
    private readonly IPalLogger<StreamRun<TItem>>? _logger;
    private readonly Action? _onTerminal;
    private readonly CancellationTokenSource _cts;
    private readonly string _runId;
    // R11 F20：是否使用自定义 idSelector。数字回退把 Last-Event-ID 当“位置索引”，
    // 只对默认位置型 Id 成立；自定义数字 id 与缓冲位置无对应关系，必须走 gap。
    private readonly bool _hasCustomIdSelector;
    private int _terminalNotified;

    // 附着等待信号（2026-10-19 性能实施轮）：替代 Monitor.Wait 的线程阻塞形态。
    // 容量 1 + Release 前判 CurrentCount，多个 Append 合并为一次唤醒（消费者每次取走
    // 当前缓冲内的全部可见项，重复信号无意义）。等待超时复用 WaitAsync(ms, ct)，
    // 与旧 Monitor.Wait(ms) 的返回值语义一一对应：true=被唤醒，false=心跳超时。
    private readonly SemaphoreSlim _signal = new(0, 1);

    private int _start;               // 环形缓冲最旧项的位置
    private int _count;               // 缓冲中项数
    private long _oldestAvailable;    // 缓冲中最旧项的逻辑 index
    private long _nextIndex;          // 下一个产出的逻辑 index
    private StreamRunState _state = StreamRunState.Running;
    private DateTimeOffset _terminalAt;
    private int _attachCount;
    private int _attachGeneration;
    private CancellationTokenSource? _graceCts;
    private bool _scopeDisposed;
    private bool _disposed;

    public StreamRun(
        string runId,
        DetachedStreamOptions options,
        TimeProvider timeProvider,
        IServiceScope? scope,
        IPalLogger<StreamRun<TItem>>? logger,
        bool hasCustomIdSelector,
        Action? onTerminal = null)
    {
        _runId = runId;
        _options = options;
        _timeProvider = timeProvider;
        _scope = scope;
        _logger = logger;
        _hasCustomIdSelector = hasCustomIdSelector;
        _onTerminal = onTerminal;
        _buffer = new BufferedStreamItem<TItem>[options.MaxBufferedItems];
        _cts = new CancellationTokenSource();
        // 预算边界：最长存活时长到时取消（pump 观测到 OCE 后进入 Cancelled 终态）
        _cts.CancelAfter(options.MaxRunDuration);
    }

    /// <summary>终态/释放时归还注册表名额（至多一次；Dispose 与 Finish 双路径安全）。</summary>
    private void NotifyTerminal()
    {
        if (Interlocked.Exchange(ref _terminalNotified, 1) == 0)
            _onTerminal?.Invoke();
    }

    public StreamRunState State
    {
        get { lock (_gate) return _state; }
    }

    public int AttachedCount
    {
        get { lock (_gate) return _attachCount; }
    }

    public DateTimeOffset TerminalAt
    {
        get { lock (_gate) return _terminalAt; }
    }

    // ── 测试可观测面（internal：Hosting 测试项目经 IVT 可见） ──

    internal long NextIndex
    {
        get { lock (_gate) return _nextIndex; }
    }

    internal int BufferedCount
    {
        get { lock (_gate) return _count; }
    }

    internal long OldestAvailable
    {
        get { lock (_gate) return _oldestAvailable; }
    }

    /// <summary>启动产出泵（单写者）。</summary>
    public void Start<TQuery>(
        IStreamQueryHandler<TQuery, TItem> handler,
        TQuery query,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector)
        where TQuery : IStreamQuery<TItem>
#pragma warning disable S8949 // 不向 Task.Run 传 token（取消由 PumpAsync 内部观察，调度前取消也必须进入终态）
        => _ = Task.Run(() => PumpAsync(handler, query, eventSelector, idSelector));
#pragma warning restore S8949

    /// <summary>
    /// R12 C-11：从 run 自有 scope 解析 handler 并启动泵。供“先附着、后启动”的启动端点使用，
    /// 避免快速同步生产者在附着完成前填满缓冲，使首个响应以 gap 开局。
    /// </summary>
    internal void StartFromServices<TQuery>(
        TQuery query,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector)
        where TQuery : IStreamQuery<TItem>
    {
        var services = _scope?.ServiceProvider
            ?? throw new InvalidOperationException("Detached stream run has no service scope.");
        var handler = services.GetRequiredService<IStreamQueryHandler<TQuery, TItem>>();
        Start(handler, query, eventSelector, idSelector);
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "run 泵必须捕获所有异常转为 Failed 终态（附着方据此收尾），否则异常在后台线程未观测。")]
    private async Task PumpAsync<TQuery>(
        IStreamQueryHandler<TQuery, TItem> handler,
        TQuery query,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector)
        where TQuery : IStreamQuery<TItem>
    {
        var state = StreamRunState.Completed;
        Exception? failure = null;
        var produced = 0;
        // 遥测与普通流式读同口径（2026-10-19）：span 覆盖 run 全枚举期（含 detach 间隙），
        // TTFT 由首个 first-item 事件时间戳与 span 起点之差给出。run 未附着时也计 started：
        // 产出由注册表泵驱动，与客户端是否在看无关。
        var activity = PalActivitySource.StartStreamQuery(typeof(TQuery).Name, typeof(TItem).Name);
        PalMetrics.StreamStarted.Add(1);
        var firstItemRecorded = false;
        try
        {
            await foreach (var item in handler.HandleAsync(query, _cts.Token)
                .WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                if (produced >= _options.MaxItems)
                {
                    // 预算超限：取消（终态 Cancelled，与客户端取消同态——均非框架缺陷）
                    await _cts.CancelAsync().ConfigureAwait(false);
                    throw new OperationCanceledException(_cts.Token);
                }
                if (!firstItemRecorded)
                {
                    // TTFT 标记：只在订阅方存在时记事件（AddEvent 会分配事件对象与链表节点）
                    firstItemRecorded = true;
                    activity?.AddEvent(new ActivityEvent("first-item"));
                }
                // event/id 写入时固化：附着回放不重跑选择器（可能有副作用）
                Append(new BufferedStreamItem<TItem>(
                    produced, item, eventSelector?.Invoke(item), idSelector?.Invoke(item)));
                produced++;
            }
        }
        catch (OperationCanceledException)
        {
            state = StreamRunState.Cancelled;
        }
        catch (Exception ex)
        {
            state = StreamRunState.Failed;
            failure = ex;
        }
        finally
        {
            try
            {
                await FinishAsync(state, failure, produced).ConfigureAwait(false);
                // 终态计数与普通流式读共用同一组计数器（零 tag 纪律）；取消独立于失败。
                // 项吞吐按「已产出」计：缓冲溢出丢弃的项也计入产出，与 StreamItems 语义一致。
                switch (state)
                {
                    case StreamRunState.Completed:
                        PalMetrics.StreamCompleted.Add(1);
                        break;
                    case StreamRunState.Failed:
                        PalMetrics.StreamFailed.Add(1);
                        if (failure is not null)
                            activity?.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
                        break;
                    case StreamRunState.Cancelled:
                        PalMetrics.StreamCancelled.Add(1);
                        break;
                }
                PalMetrics.StreamItems.Add(produced);
            }
            finally
            {
                // 即使 run 收尾（含 scope Dispose）抛异常也不泄漏 Activity
                activity?.Dispose();
            }
        }
    }

    private void Append(in BufferedStreamItem<TItem> item)
    {
        lock (_gate)
        {
            if (_count == _buffer.Length)
            {
                // 溢出丢最旧（ADR-031 D2）；_oldestAvailable 前移供 gap 判定
                _start = (_start + 1) % _buffer.Length;
                _count--;
                _oldestAvailable++;
            }
            _buffer[(_start + _count) % _buffer.Length] = item;
            _count++;
            _nextIndex = item.Index + 1;
            SignalWaitersLocked();
        }
    }

    /// <summary>唤醒附着等待者；调用方必须持有 <see cref="_gate"/>。</summary>
    /// <remarks>
    /// 容量 1 的信号量在已有一枚未消费信号时不再 Release（否则抛 SemaphoreFullException）。
    /// 消费者每轮取出当前全部可见项，因此合并唤醒不丢项；允许最多出现一次空唤醒
    /// （信号在无等待者时被留下），下一轮会立即重新进入等待。
    /// </remarks>
    private void SignalWaitersLocked()
    {
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    private async ValueTask FinishAsync(StreamRunState state, Exception? failure, int produced)
    {
        CancellationTokenSource? grace;
        lock (_gate)
        {
            _state = state;
            _terminalAt = _timeProvider.GetUtcNow();
            // R12 PF-2：替换/终态时取走并释放宽限 CTS——原实现只 Cancel 不 Dispose，
            // 每次断线重连都会让被替换的 linked CTS 以父 token 注册形式滞留到 run 释放。
            grace = _graceCts;
            _graceCts = null;
            SignalWaitersLocked();
        }
        NotifyTerminal();
        if (grace is not null)
        {
            await grace.CancelAsync().ConfigureAwait(false); // 终态：宽限计时不再有意义
            grace.Dispose();
        }
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.Debug(failure is null
                ? $"Stream run {_runId}: {state} after {produced} item(s)"
                : $"Stream run {_runId}: {state} after {produced} item(s): {failure.GetType().Name}");
        }
        // 2026-10-19 性能实施轮：scope 释放推迟到「终态且无附着」。
        // 原实现终态立即 Dispose，附着方可能正持有一批已拷贝出锁的项在锁外序列化，
        // 其中若引用 scoped 资源会在 scope 释放后失效；同步 Dispose 还会阻塞泵线程。
        var scope = TryTakeScopeForDisposalLocked();
        if (scope is not null)
            await DisposeScopeAsync(scope).ConfigureAwait(false);
    }

    /// <summary>终态且无附着时取走 scope 并标记已释放；调用方负责在锁外释放。</summary>
    private IServiceScope? TryTakeScopeForDisposalLocked()
    {
        lock (_gate)
        {
            if (_scopeDisposed || _scope is null || _state == StreamRunState.Running || _attachCount > 0)
                return null;
            _scopeDisposed = true;
            return _scope;
        }
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "scope 释放失败不得影响 run 终态与附着收尾；异步释放仅记录告警。")]
    private async ValueTask DisposeScopeAsync(IServiceScope scope)
    {
        try
        {
            if (scope is IAsyncDisposable asyncScope)
                await asyncScope.DisposeAsync().ConfigureAwait(false);
            else
                scope.Dispose();
        }
        catch (Exception ex)
        {
            if (_logger is not null && _logger.IsEnabled(LogLevel.Warning))
                _logger.Warning($"Stream run {_runId}: scope disposal failed: {ex.GetType().Name}");
        }
    }

    /// <summary>取消 run（幂等；终态后为 no-op）。</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
            // 唤醒可能在等待中的附着者：取消后无新项 Append，若不唤醒
            // 附着者要等 waitMilliseconds 超时才能看到终态（KeepAlive 间隔可达 30s，
            // 表现为「取消后连接僵死」——DetachGrace 测试实测挂起的根因之一）
            lock (_gate)
                SignalWaitersLocked();
        }
        catch (ObjectDisposedException)
        {
            // 已清理的 run：取消语义已由终态满足
        }
    }

    /// <summary>尝试开始一次附着；选项 <c>RejectNewAttach</c> 为 true 时已有附着则拒绝。</summary>
    public bool TryBeginAttach(
        string? lastEventId, out long startIndex, out bool gap, out int generation)
    {
        CancellationTokenSource? replacedGrace;
        lock (_gate)
        {
            if (_options.RejectNewAttach && _attachCount > 0)
            {
                startIndex = 0;
                gap = false;
                generation = 0;
                return false;
            }
            // 接管语义（ADR-031 D6）：递增代际，旧附着在下次唤醒时看到代际不匹配即收尾
            generation = ++_attachGeneration;
            _attachCount++;
            // R12 PF-2：取走宽限 CTS 并在锁外取消 + 释放（Cancel 同步执行注册回调，
            // 不在注册表锁内跑用户continuation），避免每次重连泄漏一个 linked CTS。
            replacedGrace = _graceCts;
            _graceCts = null;
            (startIndex, gap) = ResolveStartLocked(lastEventId);
            // 立即唤醒旧附着的等待者：让它马上看到代际不匹配并发 detached 收尾
            //（不唤醒则旧连接要等下一次 Wait 超时——keepAlive 间隔最长 30s 量级，
            // 接管帧迟迟不到，客户端感知为连接僵死）
            SignalWaitersLocked();
        }
        if (replacedGrace is not null)
        {
            replacedGrace.Cancel();
            replacedGrace.Dispose();
        }
        // 附着计数在锁外递增：MeterListener 回调属用户代码，不在注册表锁内执行
        //（RejectNewAttach 拒绝路径已在锁内提前返回，不计）
        PalMetrics.StreamAttached.Add(1);
        return true;
    }

    /// <summary>
    /// 解析回放起点：优先按 Last-Event-ID 在缓冲中找 Id 匹配项（通用，不假设 id 为数字）；
    /// 找不到时按数字解析；仍不可知则从最旧可用位置起并标记 gap（有丢弃时）。
    /// </summary>
    private (long StartIndex, bool Gap) ResolveStartLocked(string? lastEventId)
    {
        if (lastEventId is null)
            return (_oldestAvailable, _oldestAvailable > 0);

        // 从新到旧扫描缓冲找 Id 匹配（缓冲 ≤ MaxBufferedItems，O(n) 可接受）
        for (var i = _count - 1; i >= 0; i--)
        {
            var item = _buffer[(_start + i) % _buffer.Length];
            if (string.Equals(item.Id, lastEventId, StringComparison.Ordinal))
                return (item.Index + 1, false);
        }

        // R11 F20：数字解析只在默认位置型 Id（未提供 idSelector）时成立。自定义数字
        // id（数据库序列/时间戳）与缓冲位置无对应关系，按位置解释会静默跳过大段数据
        // 且不报 gap；此时直接落到下方“从最旧可用位置起”的分支。
        if (!_hasCustomIdSelector
            && long.TryParse(lastEventId, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            var next = parsed + 1;
            if (next < _oldestAvailable)
                return (_oldestAvailable, true);  // 位置已滚出缓冲 → gap
            if (next <= _oldestAvailable + _count)
                return (next, false);
        }

        // 无从定位（自定义 id 且已滚出缓冲）：从最旧起，有丢弃则报 gap
        return (_oldestAvailable, _oldestAvailable > 0);
    }

    /// <summary>释放附着；最后一个附着离开且 run 仍运行则启动宽限计时（ADR-031 D3）。</summary>
    public async ValueTask ReleaseAttachmentAsync()
    {
        CancellationTokenSource? grace = null;
        lock (_gate)
        {
            if (_attachCount > 0)
                _attachCount--;
            if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
                _logger.Debug($"Stream run {_runId}: release attachment (remaining {_attachCount}, state {_state})");
            if (_attachCount == 0 && _state == StreamRunState.Running)
                grace = _graceCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        }
        if (grace is not null)
        {
#pragma warning disable S8949 // 宽限期由 grace.Token 观察，调度前取消也必须执行清理
            _ = Task.Run(async () =>
            {
#pragma warning restore S8949
                try
                {
                    await Task.Delay(_options.DetachGrace, grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // 新附着到达（grace 被取消）或 run 已终态
                }
#pragma warning disable CA1031 // 观察包装：任意异常都必须记录后继续取消 run，不得形成未观察任务
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // F21：观察任务内的任何异常（DetachGrace 上界已校验；此处兜底 provider/
                    // 运行时异常），避免未观察任务 + 宽限计时静默失效。
                    if (_logger is not null)
                        _logger.Error(ex, $"Stream run {_runId}: detach grace timer failed; cancelling run.");
                }
                Cancel(); // 宽限期满仍无附着：终止 run，不白烧 token
            });
        }

        // 终态后最后一个附着离开才释放 scope（见 FinishAsync 注释）
        var scope = TryTakeScopeForDisposalLocked();
        if (scope is not null)
            await DisposeScopeAsync(scope).ConfigureAwait(false);
    }

    /// <summary>
    /// 向附着连接推流：先回放缓冲（可按需先发 gap 帧），再转 live，终态发收尾帧。
    /// 被接管时向旧连接发 <c>event: detached</c> 后返回。
    /// </summary>
    public async Task StreamToAsync(
        HttpContext context,
        JsonTypeInfo<TItem> jsonTypeInfo,
        int generation,
        long startIndex,
        bool gap,
        TimeSpan? keepAliveInterval)
    {
        var ct = context.RequestAborted;
        var bodyWriter = context.Response.BodyWriter;
        // 无心跳配置时无限等待 Append/Finish/Cancel 信号：原 500ms 超时让每个空闲附着
        // 每秒醒两次（1000 个 run 约 2000 次唤醒/秒），且每次带超时 WaitAsync 都建计时器。
        var waitMilliseconds = keepAliveInterval is { } ka
            ? (int)Math.Clamp(ka.TotalMilliseconds, 1, int.MaxValue)
            : -1;
        // 单次附着内复用批次缓冲：live 场景每项唤醒不再新建 List；回放大批次时复用同一数组。
        var batch = new List<BufferedStreamItem<TItem>>(Math.Min(_buffer.Length, 256));

        try
        {
            if (gap)
            {
                WriteGapFrame(bodyWriter, startIndex);
                await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                gap = false; // 初始 gap 已发；后续仅由「附着期间再丢弃」重新置位
            }

            while (true)
            {
                batch.Clear();
                var superseded = false;
                var terminal = false;
                var heartbeat = false;
                var wait = false;

                lock (_gate)
                {
                    if (generation != _attachGeneration)
                    {
                        superseded = true;
                    }
                    else if (startIndex < _oldestAvailable)
                    {
                        // 附着期间又被丢弃（慢客户端）：补一个 gap 再续
                        gap = true;
                        startIndex = _oldestAvailable;
                    }
                    else
                    {
                        var end = _oldestAvailable + _count;
                        if (startIndex < end)
                        {
                            var offset = (int)(startIndex - _oldestAvailable);
                            var take = (int)Math.Min(end - startIndex, _count);
                            if (batch.Capacity < take)
                                batch.Capacity = take;
                            for (var i = 0; i < take; i++)
                                batch.Add(_buffer[(_start + offset + i) % _buffer.Length]);
                            startIndex += take;
                        }
                        else if (_state != StreamRunState.Running)
                        {
                            terminal = true;
                        }
                        else
                        {
                            // 无新项：锁外异步等待 Append/Finish/Cancel 的信号。
                            // 旧实现 Monitor.Wait 在此阻塞线程池线程，空闲附着数一多即线程饥饿。
                            wait = true;
                        }
                    }
                }

                if (wait)
                {
                    // true=被 Append/Finish/Cancel/接管唤醒；false=等待期满（有心跳则写心跳）。
                    // 唤醒即 continue；只有等待期满才落下去走心跳分支——旧实现赋值后无条件
                    // continue，心跳帧从未写出（S1854 死赋值暴露）。
                    bool signaled;
                    if (waitMilliseconds < 0)
                    {
                        await _signal.WaitAsync(ct).ConfigureAwait(false);
                        signaled = true; // 只在被唤醒时返回；取消由外层处理
                    }
                    else
                    {
                        signaled = await _signal.WaitAsync(waitMilliseconds, ct).ConfigureAwait(false);
                    }
                    if (signaled)
                        continue;
                    ct.ThrowIfCancellationRequested();
                    heartbeat = keepAliveInterval is not null;
                }

                if (gap && batch.Count == 0 && !superseded && !terminal)
                {
                    // 上一轮检测到丢弃：本轮开头补 gap 帧
                    WriteGapFrame(bodyWriter, startIndex);
                    await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                    gap = false;
                    continue;
                }

                ct.ThrowIfCancellationRequested();

                if (superseded)
                {
                    // 接管：告知旧连接后收尾（不返 409——ADR-031 D6）
                    StreamFrameWriter.WriteRawFrame(bodyWriter, "event: detached\ndata: {}\n\n");
                    await bodyWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (heartbeat)
                {
                    StreamFrameWriter.WriteHeartbeat(bodyWriter);
                    await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (batch.Count > 0)
                {
                    var written = 0;
                    foreach (var item in batch)
                    {
                        StreamFrameWriter.WriteDataFrame(
                            bodyWriter,
                            StreamFrameSerializer.Serialize(item.Item, jsonTypeInfo),
                            item.EventName,
                            item.Id);
                        // 回放大批次分段 flush：live 的 take=1 仍由批末 flush；重连回放
                        // 不必把 1024 帧全部堆进管道后才让客户端看到首帧。
                        if (batch.Count > 1
                            && (++written % 64 == 0 || bodyWriter.UnflushedBytes >= 16 * 1024))
                        {
                            await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                        }
                    }
                    await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (terminal)
                {
                    WriteTerminalFrame(bodyWriter);
                    await bodyWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
        }
        finally
        {
            await ReleaseAttachmentAsync().ConfigureAwait(false);
        }
    }

    private void WriteTerminalFrame(PipeWriter writer)
    {
        var state = State;
        switch (state)
        {
            case StreamRunState.Completed:
                StreamFrameWriter.WriteRawFrame(writer, "event: done\ndata: {}\n\n");
                break;
            case StreamRunState.Cancelled:
                StreamFrameWriter.WriteRawFrame(writer, "event: cancelled\ndata: {}\n\n");
                break;
            default:
                // 失败：帧内文案通用化（异常详情只在 run 内部日志/应用层审计）
                StreamFrameWriter.WriteErrorFrame(writer);
                break;
        }
    }

    private static void WriteGapFrame(PipeWriter writer, long oldestIndex)
    {
        StreamFrameWriter.WriteRawFrame(
            writer, $"event: gap\ndata: {{\"oldestIndex\":{oldestIndex}}}\n\n");
        // 帧写入管道缓冲后计数（写入抛异常则不计；实际网络可达性不在此口径内）
        PalMetrics.StreamGap.Add(1);
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "run 释放路径必须对任意用户 Dispose 异常免疫，否则 Timer/停机线程被未处理异常终止；异常仅记录。")]
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：终态语义已满足
        }
        lock (_gate)
            SignalWaitersLocked();
        // R12 PF-2：与 Finish/TryBeginAttach 同一所有权协议——锁内取走并置 null，
        // 谁取到谁负责 Dispose，避免并发路径重复释放或使用已释放的 CTS。
        CancellationTokenSource? grace;
        lock (_gate)
        {
            grace = _graceCts;
            _graceCts = null;
        }
        grace?.Dispose();
        _cts.Dispose();
        if (!_scopeDisposed)
        {
            _scopeDisposed = true;
            try
            {
                _scope?.Dispose();
            }
            catch (Exception ex)
            {
                if (_logger is not null && _logger.IsEnabled(LogLevel.Warning))
                    _logger.Warning($"Stream run {_runId}: scope disposal failed during Dispose: {ex.GetType().Name}");
            }
        }
        // 仅在没有附着时释放信号量：Dispose 正常路径由 Sweep 在 attachCount==0 后调用；
        // 宿主停机与附着并发时留给 GC，避免等待方观察到 ObjectDisposedException。
        if (AttachedCount == 0)
            _signal.Dispose();
        NotifyTerminal();
    }
}

/// <summary>
/// 运行注册表（ADR-031 D1/D7）：进程内并发字典 + 过期清理 + 并发上限。
/// 进程重启丢进行中 run（已知约束）；完成态审计回放走 EventLog。
/// </summary>
internal sealed class StreamRunRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, IStreamRun> _runs = new(StringComparer.Ordinal);
    private readonly DetachedStreamOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPalLogger<StreamRunRegistry>? _registryLogger;
    private readonly Timer _sweeper;
    private int _runningCount; // 运行中 run 的原子计数——容量判定 O(1)，不再每次枚举全部 run

    public StreamRunRegistry(
        DetachedStreamOptions options,
        IServiceScopeFactory scopeFactory,
        TimeProvider? timeProvider = null,
        IPalLogger<StreamRunRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        options.Validate();
        _options = options;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registryLogger = logger;
        // 清理器：终态满保留期且无附着的 run 移除并释放 scope/CTS
        _sweeper = new Timer(static state => ((StreamRunRegistry)state!).Sweep(), this,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>创建并启动 run（单写者泵）；并发超限抛 <see cref="StreamRunCapacityExceededException"/>。</summary>
    /// <remarks>
    /// handler 默认从 run 自有 scope 解析——run 活过请求作用域，不能接收请求作用域实例。
    /// <paramref name="handlerOverride"/> 仅测试用（内部测试需注入探针实例）。
    /// </remarks>
    public string Register<TQuery, TItem>(
        TQuery query,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector,
        IStreamQueryHandler<TQuery, TItem>? handlerOverride = null)
        where TQuery : IStreamQuery<TItem>
        => RegisterCore(query, eventSelector, idSelector, handlerOverride, startImmediately: true, out _);

    /// <summary>
    /// R12 C-11：只创建并登记 run，不启动产出泵。调用方完成附着后再调用
    /// <see cref="StreamRun{TItem}.StartFromServices{TQuery}"/>，保证首个响应不会
    /// 因“泵先跑、附着后到”而丢掉前缀。
    /// </summary>
    internal string RegisterDeferred<TQuery, TItem>(
        TQuery query,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector,
        out StreamRun<TItem>? run)
        where TQuery : IStreamQuery<TItem>
        => RegisterCore(query, eventSelector, idSelector, handlerOverride: null, startImmediately: false, out run);

    private string RegisterCore<TQuery, TItem>(
        TQuery query,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector,
        IStreamQueryHandler<TQuery, TItem>? handlerOverride,
        bool startImmediately,
        out StreamRun<TItem>? createdRun)
        where TQuery : IStreamQuery<TItem>
    {
        createdRun = null;
        // R11 F19：名额判定改为 CAS 循环——原 Volatile.Read 检查后再 Increment 是
        // check-then-act，N 个并发 Register 可同时通过检查、超出 MaxConcurrentRuns
        // 最多 N-1（峰值并发时内存保护失效）。
        while (true)
        {
            var running = Volatile.Read(ref _runningCount);
            if (running >= _options.MaxConcurrentRuns)
                throw new StreamRunCapacityExceededException(_options.MaxConcurrentRuns);
            if (Interlocked.CompareExchange(ref _runningCount, running + 1, running) == running)
                break;
        }

        // R12 PF-1：终态 run 保留期不受并发上限约束，完成速率高时内存随“速率 × 保留期”
        // 增长。每次注册前把总跟踪数压到 2×MaxConcurrentRuns（运行中 + 保留终态），
        // 超出的最旧终态 run 提前逐出（其缓冲释放，回看退化为 404）。
        TrimRetainedTerminalRuns();

        var runId = Guid.NewGuid().ToString("N");
        // 先占名额再建 run：run 可能在 Start 后立即完成并回调 OnRunTerminal，后置递增会短暂
        // 负计数。构造/解析失败路径在 catch 中归还名额。
        IServiceScope? scope = null;
        StreamRun<TItem>? run = null;
        try
        {
            scope = _scopeFactory.CreateScope();
            var logger = scope.ServiceProvider.GetService<IPalLogger<StreamRun<TItem>>>();
            run = new StreamRun<TItem>(runId, _options, _timeProvider, scope, logger, idSelector is not null, OnRunTerminal);
            _runs[runId] = run;
            createdRun = run;

            if (startImmediately)
            {
                var handler = handlerOverride
                    ?? scope.ServiceProvider.GetRequiredService<IStreamQueryHandler<TQuery, TItem>>();
                run.Start(handler, query, eventSelector, idSelector);
            }
        }
        catch
        {
            // 解析失败：回收 run（Dispose 释放 scope 并经终态回调归还名额）
            if (run is not null)
            {
                _runs.TryRemove(runId, out _);
                run.Dispose();
            }
            else
            {
                scope?.Dispose();
                Interlocked.Decrement(ref _runningCount);
            }
            throw;
        }

        if (_registryLogger is not null && _registryLogger.IsEnabled(LogLevel.Debug))
            _registryLogger.Debug($"Detached stream run {runId}: started ({_runs.Count} tracked)");
        return runId;
    }

    /// <summary>run 进入终态或释放时归还运行名额（幂等由 StreamRun 的终态通知门保证）。</summary>
    private void OnRunTerminal() => Interlocked.Decrement(ref _runningCount);

    /// <summary>
    /// R12 PF-1：总跟踪数超过 2×<see cref="DetachedStreamOptions.MaxConcurrentRuns"/> 时，
    /// 逐出最旧的终态且无附着的 run。保留期内的回看是尽力而为——容量优先于保留时长。
    /// </summary>
    [SuppressMessage("Design", "CA1031",
        Justification = "容量清理不得因单个 run 的用户 Dispose 异常中断注册路径；逐 run 记录后继续。")]
    private void TrimRetainedTerminalRuns()
    {
        var hardLimit = (long)_options.MaxConcurrentRuns * 2;
        while (_runs.Count >= hardLimit)
        {
            string? oldestId = null;
            var oldestAt = DateTimeOffset.MaxValue;
            foreach (var (runId, run) in _runs)
            {
                if (run.State == StreamRunState.Running || run.AttachedCount > 0)
                    continue;
                var terminalAt = run.TerminalAt;
                if (terminalAt < oldestAt)
                {
                    oldestAt = terminalAt;
                    oldestId = runId;
                }
            }

            if (oldestId is null)
                return; // 全部在运行/附着中，无可逐出对象

            if (!_runs.TryRemove(oldestId, out var removed))
                continue;
            try
            {
                removed.Dispose();
                if (_registryLogger is not null && _registryLogger.IsEnabled(LogLevel.Debug))
                    _registryLogger.Debug($"Detached stream run {oldestId}: evicted to enforce capacity bound");
            }
            catch (Exception ex)
            {
                if (_registryLogger is not null)
                    _registryLogger.Warning($"Detached stream run {oldestId}: capacity eviction failed: {ex.GetType().Name}");
            }
        }
    }

    public bool TryGet<TItem>(string runId, [NotNullWhen(true)] out StreamRun<TItem>? run)
    {
        if (_runs.TryGetValue(runId, out var any) && any is StreamRun<TItem> typed)
        {
            run = typed;
            return true;
        }
        run = null;
        return false;
    }

    /// <summary>测试可观测面：当前跟踪的 run 数（internal，IVT 测试项目可见）。</summary>
    internal int TrackedCount => _runs.Count;

    /// <summary>测试可观测面：跟踪的唯一 run（多 run 时返回 null；internal，IVT 可见）。</summary>
    internal StreamRun<TItem>? PeekSingle<TItem>()
    {
        StreamRun<TItem>? found = null;
        var matches = 0;
        foreach (var run in _runs.Values)
        {
            if (run is not StreamRun<TItem> typed)
                continue;
            matches++;
            if (matches == 1)
                found = typed;
        }
        return matches == 1 ? found : null;
    }

    /// <summary>取消 run（幂等）；未知 run 返回 false。</summary>
    public bool Cancel(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run))
            return false;
        run.Cancel(); // 终态 run 的 Cancel 为 no-op（幂等语义由 204 答复承载）
        return true;
    }

    /// <summary>
    /// R12 C-11：移除并释放一个尚未启动/启动失败的 run（启动端点 409 或 handler 解析失败）。
    /// 返回 false 表示已被其他路径移除。
    /// </summary>
    [SuppressMessage("Design", "CA1031",
        Justification = "启动失败清理不得因用户 scope 的 Dispose 异常掩盖原始错误；记录后继续。")]
    internal bool TryRemoveAndDispose(string runId)
    {
        if (!_runs.TryRemove(runId, out var run))
            return false;
        try
        {
            run.Dispose();
        }
        catch (Exception ex)
        {
            _registryLogger?.Warning($"Detached stream run {runId}: dispose after failed start failed: {ex.GetType().Name}");
        }
        return true;
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "Timer 回调不得因单个 run 的 Dispose 失败而终止进程；逐 run 记录告警并继续清理。")]
    private void Sweep()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (runId, run) in _runs)
        {
            if (run.State == StreamRunState.Running || run.AttachedCount > 0)
                continue;
            if (now - run.TerminalAt < _options.RetentionAfterCompletion)
                continue;
            if (_runs.TryRemove(runId, out var removed))
            {
                // Timer 回调抛异常会作为未处理异常终止进程；单个 run 的 Dispose 失败
                // （用户服务的 Dispose 可抛）必须隔离并继续清理其余 run。
                try
                {
                    removed.Dispose();
                    if (_registryLogger is not null && _registryLogger.IsEnabled(LogLevel.Debug))
                        _registryLogger.Debug($"Detached stream run {runId}: evicted after retention");
                }
                catch (Exception ex)
                {
                    if (_registryLogger is not null)
                        _registryLogger.Warning($"Detached stream run {runId}: eviction failed: {ex.GetType().Name}");
                }
            }
        }

        // R12 PF-1：无新注册时也由清理器兜住总量（完成突发最多滞留一个扫描周期）。
        TrimRetainedTerminalRuns();
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "注册表停机释放必须对任意 run 的 Cancel/Dispose 异常免疫，逐 run 记录后继续。")]
    public void Dispose()
    {
        _sweeper.Dispose();
        // 先取消再释放：CancellationTokenSource.Dispose 不会触发取消，泵可能已在释放的
        // scope 上继续运行；显式 Cancel 让 handler 的 finally 先执行。
        foreach (var run in _runs.Values)
        {
            try
            {
                run.Cancel();
            }
            catch (Exception ex)
            {
                if (_registryLogger is not null && _registryLogger.IsEnabled(LogLevel.Warning))
                    _registryLogger.Warning($"Detached stream run cancel during dispose failed: {ex.GetType().Name}");
            }
        }
        foreach (var run in _runs.Values)
        {
            try
            {
                run.Dispose();
            }
            catch (Exception ex)
            {
                if (_registryLogger is not null && _registryLogger.IsEnabled(LogLevel.Warning))
                    _registryLogger.Warning($"Detached stream run dispose failed: {ex.GetType().Name}");
            }
        }
        _runs.Clear();
    }
}
