using Microsoft.Extensions.Options;
using PalDDD.Core.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
// 全限定会踩 C# 名字解析陷阱：本文件内的类型继承 TimeProvider，裸 `System` 会解析到
// 继承来的静态属性 TimeProvider.System（而非命名空间）——故用 using 引入短名。
using System.Runtime.ExceptionServices;

namespace PalDDD.Testing;

// ─────────────────────────────────────────────────────────────
// 共享测试基础设施 — 跨项目复用
// ─────────────────────────────────────────────────────────────

/// <summary>Records OpenTelemetry Activity events for test assertions.</summary>
/// <remarks>
/// 📐 <b>跨测试项目隔离设计</b>：TUnit 类级并行 + MTP 多项目并行会同时运行不同测试项目，全局 <c>ActivitySource</c>
/// 的 listener 会收到所有项目的 activity。本类在构造时记录时间戳，<c>ActivityStopped</c>
/// 回调中过滤 <c>StartTimeUtc</c> 早于构造时间的残留 activity，确保只收集本 listener
/// 创建后产生的 activity——无需依赖 <c>[Collection]</c> 序列化即可跨项目隔离。
/// </remarks>
public sealed class RecordingActivityListener : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;

    public RecordingActivityListener()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == PalActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // 过滤残留 activity：只入队 StartTimeUtc >= 构造时间的 activity，
            // 消除跨测试项目并行运行时前序测试未及时停止的 activity 污染。
            ActivityStopped = activity =>
            {
                if (activity.StartTimeUtc >= _createdAt.UtcDateTime)
                    StoppedActivities.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public ConcurrentQueue<Activity> StoppedActivities { get; } = [];

    public void Dispose()
    {
        while (StoppedActivities.TryDequeue(out _)) { }
        _listener.Dispose();
    }
}

/// <summary>
/// 记录 OpenTelemetry Meter 测量值以供测试断言。<br/>
/// 支持 <see cref="long"/>（Counter/UpDownCounter）和 <see cref="double"/>（Histogram）两种测量类型。
/// </summary>
public sealed class RecordingMeterListener : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly string _instrumentName;

    public RecordingMeterListener(string instrumentName)
    {
        _instrumentName = instrumentName;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PalActivitySource.Name
                && string.Equals(instrument.Name, _instrumentName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        // long 回调 — 覆盖 Counter<long> / UpDownCounter<long>
        _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Measurements.Enqueue(measurement));
        // double 回调 — 覆盖 Histogram<double>（CommandDuration / BehaviorDuration）
        _listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => DoubleMeasurements.Enqueue(measurement));
        _listener.Start();
    }

    /// <summary>long 类型测量值（Counter / UpDownCounter）</summary>
    public ConcurrentQueue<long> Measurements { get; } = [];

    /// <summary>double 类型测量值（Histogram — duration 等）</summary>
    public ConcurrentQueue<double> DoubleMeasurements { get; } = [];

    public void Dispose()
        => _listener.Dispose();
}

/// <summary>
/// 固定值 <see cref="IOptionsMonitor{TOptions}"/>，用于测试注入。<br/>
/// <see cref="OnChange"/> 返回 no-op <see cref="IDisposable"/>，避免消费者 NRE。
/// </summary>
public sealed class FixedOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
{
    public TOptions CurrentValue => value;

    public TOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => NullDisposable.Instance;
}

/// <summary>
/// 可控时间的 <see cref="TimeProvider"/> — 用于测试中注入确定性时间。<br/>
/// 支持 <see cref="Advance(TimeSpan)"/> 推进时间、
/// <see cref="Set(DateTimeOffset)"/> 设置精确时间、
/// 以及 <see cref="AdvanceNowAndTriggerTimers(TimeSpan)"/> 快进并触发所有到期计时器。
/// <para>
/// 💡 <b>使用场景：</b>与 <c>Task.Delay(TimeSpan, TimeProvider, ct)</c> 配合，
/// 实现 Saga 超时、Outbox 轮询等场景的确定性测试，消除真实等待。
/// </para>
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    private long _timestamp;
    private readonly List<FakeTimer> _timers = [];
    // 保护 _timers 的并发访问（SUT 后台线程 CreateTimer × 测试线程快进扫描）——见
    // AdvanceNowAndTriggerTimers 的锁内快照+移除、CreateTimer 的锁内 Add、FakeTimer.Dispose。
    // 回调一律在**锁外**触发（回调内创建计时器是声明允许的形态，持锁会重进入死锁）。
    private readonly object _timersLock = new();

    public FakeTimeProvider(DateTimeOffset initial)
    {
        _now = initial;
        _timestamp = initial.Ticks;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>推进时间（不触发计时器回调）</summary>
    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
        _timestamp += delta.Ticks;
    }

    /// <summary>设置精确时间（不触发计时器回调）</summary>
    public void Set(DateTimeOffset now)
    {
        var delta = now - _now;
        _now = now;
        _timestamp += delta.Ticks;
    }

    /// <summary>
    /// 快进时间并触发所有在此期间的到期计时器。
    /// <para>
    /// 使用此方法替代测试中的 <c>Task.Delay</c> 真实等待：
    /// <br/>1. 通过 <c>Clock</c> 属性注入此 FakeTimeProvider
    /// <br/>2. 调用 <c>AdvanceNowAndTriggerTimers(timeout)</c> 替代 <c>await Task.Delay(timeout)</c>
    /// <br/>3. 所有到期的计时器回调会被同步触发
    /// </para>
    /// <para>
    /// ⚠️ <b>回调重进入约束：</b>计时器回调中注册的新计时器在当前快进批次中不会触发。
    /// 计时器回调不应修改计时器集合（本方法已在触发前收集到期计时器到独立列表）。
    /// </para>
    /// </summary>
    /// <param name="delta">快进的时间量</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "必须隔离**任意**回调异常以跑完整个到期批次并推进时钟——否则一处断言失败会让其余已出列的计时器永不再触发、假时钟冻结；异常在批次末尾按原调用栈重抛，非吞弃。")]
    public void AdvanceNowAndTriggerTimers(TimeSpan delta)
    {
        var threshold = _now.Add(delta);

        // 收集**并移除**到期计时器：快照与移除在同一把锁内完成（全仓扫描修复：原实现
        // 无同步，SUT 的后台线程 CreateTimer 与本扫描并发修改 List → InvalidOperationException
        // / 集合损坏）。回调在**锁外**触发——回调内创建计时器是既有声明允许的形态，
        // 持锁调用会重进入死锁。
        List<FakeTimer> expired;
        lock (_timersLock)
        {
            expired = [.. _timers.Where(t => t.DueTime <= threshold)];
            foreach (var t in expired)
                _timers.Remove(t);
        }

        // P2 修复（十轮·盲区评审）：按 DueTime 升序触发——对齐真实 Timer 的到期序语义，
        // 此前按注册序触发，后注册但先到期的计时器会晚于后到期者执行
        expired.Sort(static (a, b) => a.DueTime.CompareTo(b.DueTime));

        // 批量触发回调（在移除后进行，避免回调中注册新计时器的重进入问题）。
        // 回调异常隔离（全仓扫描修复）：原实现中任一回调抛出即中断批次——其余**已从
        // _timers 移除**的到期计时器永不触发，且末尾的时间推进被跳过（假时钟冻结），
        // 把一处断言失败放大成"计时器再不上场 + 时钟不走"的难查状态。
        // 现：全部到期计时器都触发、时钟必定推进，异常在批次末尾按原始调用栈重抛
        //（多个则 AggregateException）。
        List<Exception>? failures = null;
        foreach (var t in expired)
        {
            if (t.IsCancelled)
                continue;
            try
            {
                t.Callback(t.State);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        // 无论是否有计时器到期、回调是否抛出，时间必须推进到阈值
        _now = threshold;
        _timestamp = threshold.Ticks;

        if (failures is { Count: 1 })
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        else if (failures is { Count: > 1 })
            throw new AggregateException("FakeTimeProvider: 多个计时器回调抛出（批次已全部执行）。", failures);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // P3 声明：period 有意忽略——FakeTimer 仅支持一次性到期语义（Saga/Outbox 测试场景不需要周期计时器）。
        // 全仓扫描修复（假时钟语义）：dueTime < 0（含 Timeout.InfiniteTimeSpan）在真实 Timer 下
        // 表示**永不触发**，而原实现 `_now + dueTime` 得到一个**已过期**的时刻 → 下一次
        // AdvanceNowAndTriggerTimers（即使 delta 为零）立即触发它。租约/取消路径的
        // `Task.Delay(Timeout.Infinite, timeProvider, ct)` 正是该形态。
        var timer = new FakeTimer(
            this, callback, state,
            dueTime >= TimeSpan.Zero ? _now + dueTime : DateTimeOffset.MaxValue);
        if (dueTime >= TimeSpan.Zero)
        {
            lock (_timersLock)
            {
                _timers.Add(timer);
            }
        }
        return timer;
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private int _cancelled;

        public TimerCallback Callback { get; }
        public object? State { get; }
        public DateTimeOffset DueTime { get; }
        public bool IsCancelled => Volatile.Read(ref _cancelled) == 1;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueTime)
        {
            _owner = owner;
            Callback = callback;
            State = state;
            DueTime = dueTime;
        }

        /// <summary>
        /// 不支持重设计时器——FakeTimer 仅支持一次性到期语义，重设（含周期计时器）显式抛出，
        /// 调用方依赖 Change 语义时立即失败而非静默得到 false。
        /// </summary>
        /// <param name="dueTime">忽略。</param>
        /// <param name="period">忽略。</param>
        /// <exception cref="NotSupportedException">总是抛出。</exception>
        public bool Change(TimeSpan dueTime, TimeSpan period)
            => throw new NotSupportedException("FakeTimer 不支持 Change（重设/周期计时器）——仅支持一次性到期语义。");

        public void Dispose()
        {
            Interlocked.Exchange(ref _cancelled, 1);
            // 全仓扫描修复（泄漏）：原实现只置取消位，计时器仍留在 _timers 直到其 DueTime
            // 被某次快进扫过——远期到期（或永不触发）的已释放计时器永不回收，长跑测试 worker
            // 持续累积。此处同步移除（若已被快照移除过则为 no-op；永不触发者从未入列）。
            lock (_owner._timersLock)
            {
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

/// <summary>no-op <see cref="IDisposable"/>，用于返回非 null 的可释放占位。</summary>
internal sealed class NullDisposable : IDisposable
{
    public static NullDisposable Instance { get; } = new();

    private NullDisposable()
    { }

    public void Dispose()
    { }
}
