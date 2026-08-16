// 🧪 InMemoryEventLog — 内存事件日志（测试/原型）
// ─────────────────────────────────────────────────────────────

using PalDDD.Core.Diagnostics;
using System.Runtime.CompilerServices;

namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 内存事件日志 — 单进程测试用
// ─────────────────────────────────────────────────────────────

/// <summary>内存事件日志 — 用于测试和单进程原型。</summary>
public sealed class InMemoryEventLog : IEventLog
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<RecordedEvent>> _streams = new(StringComparer.Ordinal);
    private readonly List<RecordedEvent> _global = [];
    private readonly TimeProvider _timeProvider;

    /// <summary>创建内存事件日志。</summary>
    public InMemoryEventLog(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ValueTask<AppendEventsResult> AppendAsync(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();

        if (events.Count == 0)
            throw new ArgumentException("At least one event is required.", nameof(events));

        foreach (var @event in events)
            ArgumentNullException.ThrowIfNull(@event);

        using var activity = PalActivitySource.StartEventLogAppend(streamName, events.Count);

        lock (_lock)
        {
            var stream = GetOrCreateStream(streamName);
            var actualVersion = stream.Count - 1L;
            EnsureExpectedVersion(streamName, expectedVersion, actualVersion);

            var firstStreamVersion = stream.Count;
            // 📐 P3 定案（语义声明）：InMemory 的 GlobalPosition 从 0 连续分配，EFCore 版走
            // Hi/Lo 预分配（起始值非 0 且块内连续）——两版 position 语义不对齐是刻意的：
            // InMemory 是测试替身，position 断言只应在同一实现内比较（跨实现比较无意义）。
            var firstGlobalPosition = _global.Count;
            var now = _timeProvider.GetUtcNow();

            for (var i = 0; i < events.Count; i++)
            {
                var recorded = new RecordedEvent(
                    streamName,
                    firstStreamVersion + i,
                    firstGlobalPosition + i,
                    now,
                    events[i]);
                stream.Add(recorded);
                _global.Add(recorded);
            }

            var result = new AppendEventsResult(
                streamName,
                firstStreamVersion,
                stream.Count - 1L,
                firstGlobalPosition,
                _global.Count - 1L);
            activity?.SetTag("pal.eventlog.first_stream_version", result.FirstStreamVersion);
            activity?.SetTag("pal.eventlog.last_stream_version", result.LastStreamVersion);
            activity?.SetTag("pal.eventlog.first_global_position", result.FirstGlobalPosition);
            activity?.SetTag("pal.eventlog.last_global_position", result.LastGlobalPosition);
            PalMetrics.EventLogAppended.Add(events.Count);

            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName,
        long fromVersion = 0,
        int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        using var activity = PalActivitySource.StartEventLogReadStream(streamName, fromVersion);

        List<RecordedEvent> snapshot;
        lock (_lock)
        {
            snapshot = _streams.TryGetValue(streamName, out var stream)
                ? stream.Where(e => e.StreamVersion >= fromVersion).Take(maxCount).ToList()
                : [];
        }

        var read = 0;
        // P3 修复（二十一轮）：metrics 尾部语句移入 finally——迭代器被消费方提前 Dispose
        //（await foreach 中 break/抛异常）时循环后语句不执行，已产出事件的计数丢失；
        // finally 在迭代器任何退出路径（正常走完/早退 Dispose/异常）都会执行，
        // 且先于上方 using activity 的 Dispose（SetTag 先于 Activity 结束生效）。
        // 注意：C# 迭代器中 try/finally 含 yield 合法（try/catch 含 yield 不合法）。
        try
        {
            foreach (var @event in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // ITM-120 修复：yield 前计数——原 yield 后计数使消费方 break 早退时
                // 最后一个已产出事件不计入 read 指标；前置后早退路径计数完整
                checked { read++; }
                yield return @event;
                await Task.Yield();
            }
        }
        finally
        {
            activity?.SetTag("pal.eventlog.read_count", read);
            PalMetrics.EventLogRead.Add(read);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0,
        int maxCount = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fromPosition, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        using var activity = PalActivitySource.StartEventLogReadAll(fromPosition);

        List<RecordedEvent> snapshot;
        lock (_lock)
        {
            snapshot = _global.Where(e => e.GlobalPosition >= fromPosition).Take(maxCount).ToList();
        }

        var read = 0;
        // P3 修复（二十一轮）：metrics 尾部语句移入 finally——迭代器被消费方提前 Dispose
        //（await foreach 中 break/抛异常）时循环后语句不执行，已产出事件的计数丢失；
        // finally 在迭代器任何退出路径（正常走完/早退 Dispose/异常）都会执行，
        // 且先于上方 using activity 的 Dispose（SetTag 先于 Activity 结束生效）。
        // 注意：C# 迭代器中 try/finally 含 yield 合法（try/catch 含 yield 不合法）。
        try
        {
            foreach (var @event in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // ITM-120 修复：yield 前计数——原 yield 后计数使消费方 break 早退时
                // 最后一个已产出事件不计入 read 指标；前置后早退路径计数完整
                checked { read++; }
                yield return @event;
                await Task.Yield();
            }
        }
        finally
        {
            activity?.SetTag("pal.eventlog.read_count", read);
            PalMetrics.EventLogRead.Add(read);
        }
    }

    private List<RecordedEvent> GetOrCreateStream(string streamName)
    {
        if (_streams.TryGetValue(streamName, out var stream))
            return stream;

        stream = [];
        _streams.Add(streamName, stream);
        return stream;
    }

    private static void EnsureExpectedVersion(
        string streamName,
        ExpectedStreamVersion expectedVersion,
        long actualVersion)
    {
        if (!expectedVersion.Matches(actualVersion))
            throw new EventStreamConcurrencyException(streamName, expectedVersion, actualVersion);
    }
}
