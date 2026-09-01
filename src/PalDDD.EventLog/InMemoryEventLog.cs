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
            // v39 P3：版本校验前置——原顺序 GetOrCreateStream 先于 EnsureExpectedVersion，
            // 流不存在时先创建空流条目再做校验，校验失败（如期望 NoStream 而探测到空流、
            // 或期望特定版本与空流失配）抛异常后空流条目残留 _streams（脏状态：后续对
            // 同一流名的 NoStream 语义检查失配）。改为只读探测实际版本 → 校验 → 通过才
            // 创建；流不存在时 actualVersion=-1 与空流 Count-1 等价，Matches 行为不变
            var actualVersion = (_streams.TryGetValue(streamName, out var existing) ? existing.Count : 0) - 1L;
            EnsureExpectedVersion(streamName, expectedVersion, actualVersion);

            var stream = GetOrCreateStream(streamName);
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
            // v25 P3 指标族（D5）：first/last_stream_version 与 first/last_global_position
            // 四个 SetTag 移除——版本/位置值每事件唯一递增，高基数命中 ITM-229 清理标准；
            // 位置信息已由 AppendEventsResult 返回值承载（调用方按需记录）
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
                // 每事件让出调度器（P3-SRC-106 动机声明）：async 迭代器循环体内无其他 await 时
                // MoveNextAsync 同步级联完成——消费方逐事件同步处理（重计算/同步阻塞）时整个
                // 枚举垄断线程池线程，饿死同池排队的工作项（计时器/其他 task）。Task.Yield
                // 强制回到调度器给其他工作项执行机会。历史行为保留：移除需基准证据
                await Task.Yield();
            }
        }
        finally
        {
            // v25 P3 指标族（D5）：pal.eventlog.read_count SetTag 移除——读取数量随
            // maxCount/流长度无界，高基数命中 ITM-229 清理标准；计数保留在
            // EventLogRead 指标（零 tag Counter，不受 tag 基数影响）
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
                // 每事件让出调度器（P3-SRC-106 动机声明）：async 迭代器循环体内无其他 await 时
                // MoveNextAsync 同步级联完成——消费方逐事件同步处理（重计算/同步阻塞）时整个
                // 枚举垄断线程池线程，饿死同池排队的工作项（计时器/其他 task）。Task.Yield
                // 强制回到调度器给其他工作项执行机会。历史行为保留：移除需基准证据
                await Task.Yield();
            }
        }
        finally
        {
            // v25 P3 指标族（D5）：pal.eventlog.read_count SetTag 移除——同 ReadStreamAsync
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
