using Microsoft.Extensions.Options;
using PalDDD.Core.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PalDDD.Testing;

// ─────────────────────────────────────────────────────────────
// 共享测试基础设施 — 跨项目复用
// ─────────────────────────────────────────────────────────────

/// <summary>Records OpenTelemetry Activity events for test assertions.</summary>
/// <remarks>
/// 📐 <b>跨测试项目隔离设计</b>：xunit 并行运行不同测试项目，全局 <c>ActivitySource</c>
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
    public void AdvanceNowAndTriggerTimers(TimeSpan delta)
    {
        var threshold = _now.Add(delta);

        // 收集所有在阈值之前到期的计时器（避免回调中修改集合）
        var expired = new List<FakeTimer>();
        foreach (var timer in _timers)
        {
            if (timer.DueTime <= threshold)
                expired.Add(timer);
        }

        // 移除到期计时器
        foreach (var t in expired)
            _timers.Remove(t);

        // 批量触发回调（在移除后进行，避免回调中注册新计时器的重进入问题）
        foreach (var t in expired)
        {
            if (!t.IsCancelled)
            {
                t.Callback(t.State);
            }
        }

        // 无论是否有计时器到期，时间必须推进到阈值
        _now = threshold;
        _timestamp = threshold.Ticks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(callback, state, _now + dueTime);
        _timers.Add(timer);
        return timer;
    }

    private sealed class FakeTimer : ITimer
    {
        private int _cancelled;

        public TimerCallback Callback { get; }
        public object? State { get; }
        public DateTimeOffset DueTime { get; }
        public bool IsCancelled => Volatile.Read(ref _cancelled) == 1;

        public FakeTimer(TimerCallback callback, object? state, DateTimeOffset dueTime)
        {
            Callback = callback;
            State = state;
            DueTime = dueTime;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // 简化实现：不支持周期计时器（Saga/Outbox 场景不需要）
            return false;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _cancelled, 1);
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
