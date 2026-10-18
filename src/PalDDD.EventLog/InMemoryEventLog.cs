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
        {
            if (@event is null)
                throw new ArgumentNullException(nameof(events), "The events collection contains a null element.");
        }

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
            if (!_streams.TryGetValue(streamName, out var stream))
            {
                snapshot = [];
            }
            else
            {
                // A14（S1 轮，2026-10-15）：二分定位起始下标 + GetRange 只拷所需段——原
                // Where+Take+ToList 对全表线性过滤再整表拷 List（10 万事件流每次读都
                // O(n) 过滤 + O(取数) 拷贝）。流按 StreamVersion 单调追加，首个 >=
                // fromVersion 的下标即起始（语义与 Where 逐字等价）。
                var start = LowerBound(stream, fromVersion, static e => e.StreamVersion);
                var count = Math.Min(maxCount, stream.Count - start);
                snapshot = count > 0 ? stream.GetRange(start, count) : [];
            }
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
                // A14（S1 轮）：让出降频为每 64 事件一次——原每事件 Task.Yield 使大规模
                // 回放（10 万事件 = 10 万次调度器让出）付纯调度税；P3-SRC-106 的防饿死
                // 动机（消费方逐事件同步处理时枚举垄断线程池线程，饿死同池工作项）由
                // 周期让出保留（64 事件窗口远小于饿死阈值，动机不破）。
                if ((read & 63) == 0)
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
            // A14（S1 轮，2026-10-15）：二分定位起始下标 + GetRange 只拷所需段——原
            // Where+Take+ToList 对全量线性过滤再整表拷 List。_global 按 GlobalPosition
            // 单调追加，首个 >= fromPosition 的下标即起始（语义与 Where 逐字等价）。
            var start = LowerBound(_global, fromPosition, static e => e.GlobalPosition);
            var count = Math.Min(maxCount, _global.Count - start);
            snapshot = count > 0 ? _global.GetRange(start, count) : [];
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
                // A14（S1 轮）：让出降频为每 64 事件一次（同 ReadStreamAsync——防饿死
                // 动机由周期让出保留，纯调度税按 64× 收敛）
                if ((read & 63) == 0)
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

    /// <summary>
    /// A14（S1 轮，2026-10-15）：追加有序列表上按 key 定位起始下标（下界二分）——读路径
    /// 替代 Where+Take+ToList 的全表线性过滤。_streams/_global 分别按 StreamVersion/
    /// GlobalPosition 单调追加（append-only 契约），首个 selector >= key 的下界即起始；
    /// 全小于 key 时返回 Count（调用方按空段处理）。语义与 Where(e => key(e) >= key)
    /// 在单调无重复键上逐字等价。
    /// </summary>
    private static int LowerBound(List<RecordedEvent> list, long key, Func<RecordedEvent, long> selector)
    {
        var lo = 0;
        var hi = list.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (selector(list[mid]) < key)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
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
