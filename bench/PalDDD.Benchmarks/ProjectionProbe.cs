using PalDDD.Projections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PalDDD.Benchmarks;

/// <summary>
/// M2 量具（2026-10-19）：100k 事件内存回放，比较 CheckpointBatchSize 1 与 100 的
/// 检查点调用次数。回答“100k 回放命令数是否降到 2k 以内”。
/// </summary>
internal static class ProjectionProbe
{
    public static async Task<int> RunM2Async(CancellationToken ct = default)
    {
        const int count = 100_000;
        var results = new List<(int Batch, int TryStart, int Completed, long ElapsedMs)>();

        // 2026-10-22 性能轮：默认批宽改为 1000，量具同步加一档（1 / 100 / 1000）。
        foreach (var batchSize in new[] { 1, 100, 1000 })
        {
            var store = new CountingStore(new InMemoryProjectionCheckpointStore());
            var processor = new ProjectionProcessor<SerializationBenchmarks.BenchOrderPlaced>(
                new NoOpHandler(), store)
            {
                CheckpointBatchSize = batchSize,
            };
            var rebuilder = new ProjectionRebuilder<SerializationBenchmarks.BenchOrderPlaced>(
                "m2-probe", "m2-source", new GeneratorSource(count), store, processor);

            var sw = Stopwatch.StartNew();
            var processed = await rebuilder.ReplayAsync(ct);
            sw.Stop();

            if (processed != count)
                throw new InvalidOperationException($"M2 batch={batchSize}: processed {processed} != {count}");
            results.Add((batchSize, store.TryStartCalls, store.CompletedCalls, sw.ElapsedMilliseconds));
        }

        var perEvent = results[0];
        var batched = results[1];
        var wide = results[2];
        if (perEvent.TryStart != count || perEvent.Completed != count)
            throw new InvalidOperationException($"M2 batch=1 checkpoint calls unexpected: {perEvent}");
        // 2026-10-22 口径修正：F7（2026-10-21）把组首锚点前移到 handler 执行之前，每组变成
        // 2 次 TryStart + 2 次 MarkCompleted；100k/100 = 1000 组 → 每类 2000 次。原阈值
        // 「每类 ≤1250」是 F7 前的口径，本量具因此自 F7 起一直失败（TOOL 车道无人跑），
        // 现按结构真值收紧到 2100（余量 100，供未来组边界形态微调）。
        if (batched.TryStart > 2_100 || batched.Completed > 2_100)
            throw new InvalidOperationException($"M2 batch=100 checkpoint calls exceed 2.1k: {batched}");
        // 2026-10-22：默认批宽 1000 → 100 组 → 每类至多 200 次（组首+组末各一次）。
        if (wide.TryStart > 250 || wide.Completed > 250)
            throw new InvalidOperationException($"M2 batch=1000 checkpoint calls exceed 250: {wide}");

        Console.WriteLine(
            $"M2 PASS: events={count}, batch=1 calls={perEvent.TryStart + perEvent.Completed} ({perEvent.ElapsedMs}ms), " +
            $"batch=100 calls={batched.TryStart + batched.Completed} ({batched.ElapsedMs}ms), " +
            $"batch=1000 calls={wide.TryStart + wide.Completed} ({wide.ElapsedMs}ms), " +
            $"reduction=(1 vs 100)={(perEvent.TryStart + perEvent.Completed) / (double)(batched.TryStart + batched.Completed):F0}x, " +
            $"(100 vs 1000)={(batched.TryStart + batched.Completed) / (double)(wide.TryStart + wide.Completed):F0}x");
        return 0;
    }

    /// <summary>
    /// 计数装饰器。2026-10-22：改为实现 <see cref="IResumableProjectionCheckpointStore"/>——
    /// R14 的「批模式要求续读能力」fail-fast 落地后，只实现基础接口的装饰器会让 batch&gt;1
    /// 的整条量具车道抛 InvalidOperationException（探针此前无人跑，回归静默存在于 779bd02..）。
    /// </summary>
    private sealed class CountingStore(IProjectionCheckpointStore inner)
        : IResumableProjectionCheckpointStore
    {
        public int TryStartCalls;
        public int CompletedCalls;

        public ValueTask<ProjectionCheckpoint?> GetAsync(
            string projectionName, string sourceName, string position, CancellationToken ct = default)
            => inner.GetAsync(projectionName, sourceName, position, ct);

        public ValueTask<ProjectionCheckpoint?> TryStartAsync(
            string projectionName, string sourceName, string position, DateTimeOffset startedAt,
            TimeSpan processingTimeout, CancellationToken ct = default)
        {
            TryStartCalls++;
            return inner.TryStartAsync(projectionName, sourceName, position, startedAt, processingTimeout, ct);
        }

        public ValueTask MarkCompletedAsync(
            ProjectionCheckpoint checkpoint, DateTimeOffset completedAt, CancellationToken ct = default)
        {
            CompletedCalls++;
            return inner.MarkCompletedAsync(checkpoint, completedAt, ct);
        }

        public ValueTask MarkFailedAsync(
            ProjectionCheckpoint checkpoint, string failureReason, DateTimeOffset failedAt,
            CancellationToken ct = default)
            => inner.MarkFailedAsync(checkpoint, failureReason, failedAt, ct);

        public ValueTask ResetAsync(string projectionName, string sourceName, CancellationToken ct = default)
            => inner.ResetAsync(projectionName, sourceName, ct);

        public ValueTask<string?> GetResumePositionAsync(
            string projectionName, string sourceName, CancellationToken ct = default)
            => ((IResumableProjectionCheckpointStore)inner).GetResumePositionAsync(projectionName, sourceName, ct);
    }

    private sealed class NoOpHandler : IProjectionHandler<SerializationBenchmarks.BenchOrderPlaced>
    {
        public string ProjectionName => "m2-probe";

        public ValueTask ProjectAsync(
            SerializationBenchmarks.BenchOrderPlaced message, ProjectionContext context,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class GeneratorSource(int count) : IEventReplaySource<SerializationBenchmarks.BenchOrderPlaced>
    {
        public async IAsyncEnumerable<ReplayEvent<SerializationBenchmarks.BenchOrderPlaced>> ReadAsync(
            string sourceName, [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ReplayEvent<SerializationBenchmarks.BenchOrderPlaced>(
                    sourceName,
                    i.ToString(CultureInfo.InvariantCulture),
                    DateTimeOffset.UnixEpoch,
                    new SerializationBenchmarks.BenchOrderPlaced(Guid.Empty, "probe", 1));
                if ((i & 1023) == 0)
                    await Task.Yield();
            }
        }
    }
}
