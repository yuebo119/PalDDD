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

    private static ReadOnlyMemory<byte> Serialize(string value)
        => Encoding.UTF8.GetBytes(value);

    private static string Deserialize(ReadOnlyMemory<byte> payload)
        => Encoding.UTF8.GetString(payload.Span);

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
