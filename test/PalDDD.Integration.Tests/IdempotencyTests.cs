namespace PalDDD.Integration.Tests;

using PalDDD.Idempotency;
using PalDDD.Testing;
using System.Diagnostics;
using System.Text;

public sealed class IdempotencyTests
{
    [Test]
    public async Task ExecuteAsync_EmitsIdempotencyActivityWhenExecuted(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Idempotency Execute").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a =>
            string.Equals(a.GetTagItem("pal.idempotency.operation") as string, "CreateOrder", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("pal.idempotency.result") as string, "executed", StringComparison.Ordinal));
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        await Assert.That(activity.GetTagItem("pal.idempotency.key")).IsEqualTo("cmd-1");
        await Assert.That(activity.GetTagItem("pal.idempotency.result")).IsEqualTo("executed");
    }

    [Test]
    public async Task ExecuteAsync_EmitsIdempotencyActivityWhenCached(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());
        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-456"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var matches = listener.StoppedActivities.Where(a =>
            a.OperationName == "Idempotency Execute" &&
            string.Equals(a.GetTagItem("pal.idempotency.result") as string, "cached", StringComparison.Ordinal)).ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches[0];
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Cached);
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        await Assert.That(activity.GetTagItem("pal.idempotency.key")).IsEqualTo("cmd-1");
    }

    [Test]
    public async Task ExecuteAsync_ReturnsCachedResultForCompletedCommand(CancellationToken cancellationToken)
    {
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        var first = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-123");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var second = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-456");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(first.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        await Assert.That(second.Status).IsEqualTo(IdempotencyExecutionStatus.Cached);
        await Assert.That(second.Result).IsEqualTo("order-123");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_EmitsIdempotencyActivityWhenSkipped(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new SkippingIdempotencyStore());

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Idempotency Execute").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a =>
            string.Equals(a.GetTagItem("pal.idempotency.operation") as string, "CreateOrder", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("pal.idempotency.result") as string, "skipped", StringComparison.Ordinal));
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Skipped);
        await Assert.That(activity.GetTagItem("pal.idempotency.operation")).IsEqualTo("CreateOrder");
        await Assert.That(activity.GetTagItem("pal.idempotency.key")).IsEqualTo("cmd-1");
        await Assert.That(activity.GetTagItem("pal.idempotency.result")).IsEqualTo("skipped");
    }

    [Test]
    public async Task ExecuteAsync_RecordsExecutedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.executed");
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ExecuteAsync_RecordsCachedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.cached");
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());
        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-456"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ExecuteAsync_RecordsSkippedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.skipped");
        var processor = new IdempotencyProcessor(new SkippingIdempotencyStore());

        await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ => ValueTask.FromResult("order-123"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ExecuteAsync_RecordsFailedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.idempotency.failed");
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ => throw new InvalidOperationException("handler failed"),
                Serialize,
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ExecuteAsync_WhenHandlerFails_MarksActivityAsError(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

        var exception = await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ => throw new InvalidOperationException("handler failed"),
                Serialize,
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Idempotency Execute").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a => a.Status == ActivityStatusCode.Error);
        await Assert.That(exception!.Message).IsEqualTo("handler failed");
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.StatusDescription).Contains("handler failed");
        await Assert.That(activity.GetTagItem("pal.idempotency.result")).IsEqualTo("failed");
    }

    [Test]
    public async Task ExecuteAsync_WhenHandlerCancels_PreservesProcessingLease(CancellationToken cancellationToken)
    {
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ =>
                {
                    calls++;
                    throw new OperationCanceledException("handler canceled");
                },
                Serialize,
                Deserialize,
                policy: new IdempotencyPolicy
                {
                    ProcessingTimeout = TimeSpan.FromMinutes(5),
                    Retention = TimeSpan.FromHours(1)
                },
                cancellationToken: cancellationToken)).Throws<OperationCanceledException>();

        var retry = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-123");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(retry.Status).IsEqualTo(IdempotencyExecutionStatus.Skipped);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_RetriesFailedExecution(CancellationToken cancellationToken)
    {
        var store = new InMemoryIdempotencyStore();
        var processor = new IdempotencyProcessor(store);
        var calls = 0;

        await Assert.That(
            async () => await processor.ExecuteAsync<string>(
                "CreateOrder",
                "cmd-1",
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("handler failed");
                },
                Serialize,
                Deserialize,
                cancellationToken: cancellationToken)).Throws<InvalidOperationException>();

        var retry = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-1",
            _ =>
            {
                calls++;
                return ValueTask.FromResult("order-123");
            },
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        await Assert.That(retry.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        await Assert.That(retry.Result).IsEqualTo("order-123");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task InMemoryStore_TryStartAsync_PreemptsZombieAndOldHolderMarkIgnored()
    {
        // P3 回归（二十一轮）：InMemory 幂等存储的僵尸/失败抢占路径现返回新实例（引用隔离对齐
        // InMemoryProjectionCheckpointStore 十七轮语义）——被抢占旧实例的 MarkCompletedAsync
        // 必须被 ReferenceEquals 守卫静默忽略（镜像 ProjectionTests 同名形态）
        var store = new InMemoryIdempotencyStore();
        var now = DateTimeOffset.Parse("2026-08-16T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var policy = new IdempotencyPolicy
        {
            ProcessingTimeout = TimeSpan.FromMinutes(5),
            Retention = TimeSpan.FromHours(1)
        };

        // 行为断言（弱断言棘轮约束：新测试禁 IsNotNull 守卫式）——状态即非空证明
        var first = await store.TryStartAsync("CreateOrder", "cmd-1", now, policy);
        await Assert.That(first!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        // 租约未过期——同键不可重入
        var stillAlive = await store.TryStartAsync("CreateOrder", "cmd-1", now.AddMinutes(1), policy);
        await Assert.That(stillAlive).IsNull();

        // 租约过期——僵尸被抢占，返回新实例（非旧引用复用）
        var preempted = await store.TryStartAsync("CreateOrder", "cmd-1", now.AddMinutes(6), policy);
        await Assert.That(preempted!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
        await Assert.That(preempted).IsNotSameReferenceAs(first);

        // 被抢占旧实例的 Mark 必须被守卫忽略——新持有者仍为 Processing
        await store.MarkCompletedAsync(first!, new byte[] { 1, 2, 3 }, now.AddMinutes(7));
        var current = await store.GetAsync("CreateOrder", "cmd-1", now.AddMinutes(7));
        await Assert.That(current!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
    }

    private static ReadOnlyMemory<byte> Serialize(string value)
        => Encoding.UTF8.GetBytes(value);

    private static string Deserialize(ReadOnlyMemory<byte> payload)
        => Encoding.UTF8.GetString(payload.Span);

    [Test]
    public async Task ExecuteAsync_MarkCompletedFails_ReturnsExecutedWithoutMarkingFailed(CancellationToken cancellationToken)
    {
        // ITM-191 回归（三十轮）：handler 成功但 MarkCompleted 失败（DB 故障）——
        // 不得降级为 Failed（Failed 可重入 → handler 重放 → 副作用二次执行）。
        // 对齐 InboxProcessor ITM-180（镜像修复）。修复前 MarkCompleted 异常落入
        // 通用 catch → MarkFailedAsync 把已成功记录标 Failed。
        ThrowingOnCompleteStore.MarkFailedCalls = 0;
        ThrowingOnCompleteStore.LastRecord = null;
        using var listener = new RecordingActivityListener();
        var processor = new IdempotencyProcessor(new ThrowingOnCompleteStore());

        var execution = await processor.ExecuteAsync(
            "CreateOrder",
            "cmd-fail",
            _ => ValueTask.FromResult("order-ok"),
            Serialize,
            Deserialize,
            cancellationToken: cancellationToken);

        // 副作用已发生：按 Executed 返回——修复前落入 catch 被降级 Failed
        await Assert.That(execution.Status).IsEqualTo(IdempotencyExecutionStatus.Executed);
        // 关键：MarkFailedAsync 必须零调用（不得把已成功记录标 Failed）
        await Assert.That(ThrowingOnCompleteStore.MarkFailedCalls).IsEqualTo(0);
        // 记录仍维持 TryStart 的 Processing 状态（未被降级）——直接断言状态（空引用访问
        // 即失败，无需 IsNotNull 守卫式弱断言，满足断言棘轮约束）
        await Assert.That(ThrowingOnCompleteStore.LastRecord!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        // 定位活动 event 标记 pending-confirmation 语义（可观测性）——Any 行为断言
        await Assert.That(listener.StoppedActivities.Any(a =>
            a.Events.Any(e => e.Name == "idempotency.completed-pending-confirmation"))).IsTrue();
    }

    /// <summary>MarkCompleted 抛 DB 故障、记录 TryStart 对象与 MarkFailed 调用数的存储 —— ITM-191 测试装置。</summary>
    private sealed class ThrowingOnCompleteStore : IIdempotencyStore
    {
        public static IdempotencyRecord? LastRecord;
        public static int MarkFailedCalls;

        public ValueTask<IdempotencyRecord?> GetAsync(string operationName, string key, DateTimeOffset now, CancellationToken ct = default)
            => ValueTask.FromResult<IdempotencyRecord?>(null);

        public ValueTask<IdempotencyRecord?> TryStartAsync(string operationName, string key, DateTimeOffset now,
            IdempotencyPolicy policy, CancellationToken ct = default)
        {
            var nowUtc = now.ToUniversalTime();
            LastRecord = new IdempotencyRecord(operationName, key, IdempotencyRecordStatus.Processing,
                nowUtc.AddMinutes(5), nowUtc.AddMinutes(30), nowUtc);
            return ValueTask.FromResult<IdempotencyRecord?>(LastRecord);
        }

        public ValueTask MarkCompletedAsync(IdempotencyRecord record, ReadOnlyMemory<byte> responsePayload,
            DateTimeOffset completedAt, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated DB failure on complete");

        public ValueTask MarkFailedAsync(IdempotencyRecord record, string failureReason,
            DateTimeOffset failedAt, CancellationToken ct = default)
        {
            MarkFailedCalls++;
            record.MarkFailed(failureReason, failedAt);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SkippingIdempotencyStore : IIdempotencyStore
    {
        public ValueTask<IdempotencyRecord?> GetAsync(
            string operationName,
            string key,
            DateTimeOffset now,
            CancellationToken ct = default)
            => ValueTask.FromResult<IdempotencyRecord?>(null);

        public ValueTask<IdempotencyRecord?> TryStartAsync(
            string operationName,
            string key,
            DateTimeOffset now,
            IdempotencyPolicy policy,
            CancellationToken ct = default)
            => ValueTask.FromResult<IdempotencyRecord?>(null);

        public ValueTask MarkCompletedAsync(
            IdempotencyRecord record,
            ReadOnlyMemory<byte> responsePayload,
            DateTimeOffset completedAt,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Skipped executions must not complete a record.");

        public ValueTask MarkFailedAsync(
            IdempotencyRecord record,
            string failureReason,
            DateTimeOffset failedAt,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Skipped executions must not fail a record.");
    }
}
