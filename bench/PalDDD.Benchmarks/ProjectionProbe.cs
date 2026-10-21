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

        foreach (var batchSize in new[] { 1, 100 })
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
        if (perEvent.TryStart != count || perEvent.Completed != count)
            throw new InvalidOperationException($"M2 batch=1 checkpoint calls unexpected: {perEvent}");
        // 2026-10-21 评审 F7：组首锚点在处理前取得（每组 +1 TryStart、+1 MarkCompleted），
        // 100k 事件 / 100 条一组 = 1000 组 → 上限从 1000 放宽到 2500（仍比逐事件 200k 低 80×）。
        if (batched.TryStart > 1_250 || batched.Completed > 1_250)
            throw new InvalidOperationException($"M2 batch=100 checkpoint calls exceed 2.5k: {batched}");

        Console.WriteLine(
            $"M2 PASS: events={count}, batch=1 calls={perEvent.TryStart + perEvent.Completed} ({perEvent.ElapsedMs}ms), " +
            $"batch=100 calls={batched.TryStart + batched.Completed} ({batched.ElapsedMs}ms), " +
            $"reduction={(perEvent.TryStart + perEvent.Completed) / (double)(batched.TryStart + batched.Completed):F0}x");
        return 0;
    }

    private sealed class CountingStore(IProjectionCheckpointStore inner) : IProjectionCheckpointStore
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
