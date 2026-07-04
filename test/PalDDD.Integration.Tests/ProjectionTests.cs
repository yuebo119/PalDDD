namespace PalDDD.Integration.Tests;

using PalDDD.Projections;
using PalDDD.Testing;
using System.Diagnostics;

using TUnit.Core;

[NotInParallel]
public sealed class ProjectionTests
{
    [Test]
    public async Task ProcessAsync_SkipsAlreadyCompletedCheckpoint(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var context = new ProjectionContext("orders", "42", DateTimeOffset.UnixEpoch);

        var first = await processor.ProcessAsync(new OrderPlaced(Guid.NewGuid()), context, cancellationToken);
        var second = await processor.ProcessAsync(new OrderPlaced(Guid.NewGuid()), context, cancellationToken);
        var checkpoint = await store.GetAsync("order-summary", "orders", "42", cancellationToken);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(handler.Count).IsEqualTo(1);
        await Assert.That(checkpoint).IsNotNull();
        await Assert.That(checkpoint.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);
    }

    [Test]
    public async Task RebuildAsync_ResetsCheckpointsAndReplaysEventsInOrder(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "2", DateTimeOffset.UnixEpoch.AddSeconds(2), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        await Assert.That(await processor.ProcessAsync(
            new OrderPlaced(Guid.NewGuid()),
            new ProjectionContext("orders", "1", DateTimeOffset.UnixEpoch),
            cancellationToken)).IsTrue();

        var replayed = await rebuilder.RebuildAsync(cancellationToken);

        await Assert.That(replayed).IsEqualTo(2);
        await Assert.That(handler.Count).IsEqualTo(3);
        await Assert.That(await store.GetAsync("order-summary", "orders", "1", cancellationToken)).IsNotNull();
        await Assert.That(await store.GetAsync("order-summary", "orders", "2", cancellationToken)).IsNotNull();
    }

    [Test]
    public async Task RebuildAsync_EmitsProjectionRebuildActivity(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "2", DateTimeOffset.UnixEpoch.AddSeconds(2), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        var replayed = await rebuilder.RebuildAsync(cancellationToken);

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Projection Rebuild").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a =>
            string.Equals(a.GetTagItem("pal.projection.name") as string, "order-summary", StringComparison.Ordinal));
        await Assert.That(replayed).IsEqualTo(2);
        await Assert.That(activity.GetTagItem("pal.projection.name")).IsEqualTo("order-summary");
        await Assert.That(activity.GetTagItem("pal.projection.source")).IsEqualTo("orders");
        await Assert.That(activity.GetTagItem("pal.projection.replayed")).IsEqualTo(2);
    }

    [Test]
    public async Task RebuildAsync_RecordsProjectionReplayedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.projection.replayed");
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "2", DateTimeOffset.UnixEpoch.AddSeconds(2), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        await rebuilder.RebuildAsync(cancellationToken);

        await Assert.That(listener.Measurements).Contains(2);
    }

    [Test]
    public async Task RebuildAsync_PassesReplayAuditToProjectionContext(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CapturingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var audit = new ReplayAuditMetadata(
            "user-123",
            "rebuild order projection",
            Guid.Parse("ae9ce6d5-1263-43a0-a7f2-263f15d57e64"),
            Guid.Parse("9ea92652-d96f-4700-8d99-a92a6beb87d7"),
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            "tenant=ordering");
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>(
                "orders",
                "1",
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                new OrderPlaced(Guid.Parse("9c5bd607-9cfc-4f44-ae90-c7198ebe43be")),
                audit)
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        await rebuilder.RebuildAsync(cancellationToken);

        await Assert.That(handler.Context.Audit).IsEqualTo(audit);
    }

    [Test]
    public async Task RebuildAsync_WhenProjectionFails_MarksActivityAsError(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new FailingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        var exception = await Assert.That(
            async () => await rebuilder.RebuildAsync(cancellationToken)).Throws<InvalidOperationException>();

        var matches = listener.StoppedActivities.Where(a => a.OperationName == "Projection Rebuild").ToList();
        await Assert.That(matches).Count().IsGreaterThanOrEqualTo(1);
        var activity = matches.First(a => a.Status == ActivityStatusCode.Error);
        await Assert.That(exception!.Message).IsEqualTo("projection failed");
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.StatusDescription).Contains("projection failed");
    }

    [Test]
    public async Task RebuildAsync_WhenProjectionFails_RecordsProjectionFailedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.projection.failed");
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new FailingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        await Assert.That(
            async () => await rebuilder.RebuildAsync(cancellationToken)).Throws<InvalidOperationException>();

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ProcessAsync_WhenProjectionFails_MarksCheckpointFailed(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new FailingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var context = new ProjectionContext("orders", "failed", DateTimeOffset.UnixEpoch);

        await Assert.That(
            async () => await processor.ProcessAsync(new OrderPlaced(Guid.NewGuid()), context, cancellationToken)).Throws<InvalidOperationException>();

        var checkpoint = await store.GetAsync("order-summary", "orders", "failed", cancellationToken);
        await Assert.That(checkpoint).IsNotNull();
        await Assert.That(checkpoint.Status).IsEqualTo(ProjectionCheckpointStatus.Failed);
        await Assert.That(checkpoint.Error).IsEqualTo("projection failed");
    }

    [Test]
    public async Task ProcessAsync_WhenProjectionCanceled_DoesNotMarkCheckpointFailed(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CanceledProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var context = new ProjectionContext("orders", "canceled", DateTimeOffset.UnixEpoch);

        await Assert.That(
            async () => await processor.ProcessAsync(new OrderPlaced(Guid.NewGuid()), context, cancellationToken)).Throws<OperationCanceledException>();

        var checkpoint = await store.GetAsync("order-summary", "orders", "canceled", cancellationToken);
        await Assert.That(checkpoint).IsNotNull();
        await Assert.That(checkpoint.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
        await Assert.That(checkpoint.Error).IsNull();
    }

    [Test]
    public async Task RebuildAsync_AfterMidStreamFailure_CanBeRetriedFromScratch(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new FailOnceProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "2", DateTimeOffset.UnixEpoch.AddSeconds(2), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "3", DateTimeOffset.UnixEpoch.AddSeconds(3), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        await Assert.That(
            async () => await rebuilder.RebuildAsync(cancellationToken)).Throws<InvalidOperationException>();

        var replayed = await rebuilder.RebuildAsync(cancellationToken);

        await Assert.That(replayed).IsEqualTo(3);
        await Assert.That(await store.GetAsync("order-summary", "orders", "1", cancellationToken)).IsNotNull();
        await Assert.That(await store.GetAsync("order-summary", "orders", "2", cancellationToken)).IsNotNull();
        await Assert.That(await store.GetAsync("order-summary", "orders", "3", cancellationToken)).IsNotNull();
    }

    [Test]
    public async Task ReplayAsync_SkipsCompletedCheckpointsAndOnlyProcessesMissing(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new CountingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "2", DateTimeOffset.UnixEpoch.AddSeconds(2), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "3", DateTimeOffset.UnixEpoch.AddSeconds(3), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        await Assert.That(await processor.ProcessAsync(
            new OrderPlaced(Guid.NewGuid()),
            new ProjectionContext("orders", "1", DateTimeOffset.UnixEpoch),
            cancellationToken)).IsTrue();
        await Assert.That(handler.Count).IsEqualTo(1);

        var replayed = await rebuilder.ReplayAsync(cancellationToken);

        await Assert.That(replayed).IsEqualTo(2);
        await Assert.That(handler.Count).IsEqualTo(3);
        var cp1 = await store.GetAsync("order-summary", "orders", "1", cancellationToken);
        await Assert.That(cp1).IsNotNull();
        await Assert.That(cp1.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);
    }

    [Test]
    public async Task ReplayAsync_AfterMidStreamFailure_OldCheckpointsIntact(CancellationToken cancellationToken)
    {
        var store = new InMemoryProjectionCheckpointStore();
        var handler = new FailOnceProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, store);
        var source = new InMemoryReplaySource<OrderPlaced>(
        [
            new ReplayEvent<OrderPlaced>("orders", "1", DateTimeOffset.UnixEpoch.AddSeconds(1), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "2", DateTimeOffset.UnixEpoch.AddSeconds(2), new OrderPlaced(Guid.NewGuid())),
            new ReplayEvent<OrderPlaced>("orders", "3", DateTimeOffset.UnixEpoch.AddSeconds(3), new OrderPlaced(Guid.NewGuid()))
        ]);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "orders",
            source,
            store,
            processor);

        await processor.ProcessAsync(
            new OrderPlaced(Guid.NewGuid()),
            new ProjectionContext("orders", "1", DateTimeOffset.UnixEpoch),
            cancellationToken);

        await Assert.That(
            async () => await rebuilder.ReplayAsync(cancellationToken)).Throws<InvalidOperationException>();

        var cp1 = await store.GetAsync("order-summary", "orders", "1", cancellationToken);
        await Assert.That(cp1).IsNotNull();
        await Assert.That(cp1.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);

        var replayed = await rebuilder.ReplayAsync(cancellationToken);
        await Assert.That(replayed).IsEqualTo(2);
    }

    private sealed record OrderPlaced(Guid OrderId);

    private sealed class CountingProjectionHandler : IProjectionHandler<OrderPlaced>
    {
        public int Count { get; private set; }

        public string ProjectionName => "order-summary";

        public ValueTask ProjectAsync(OrderPlaced message, ProjectionContext context, CancellationToken ct = default)
        {
            Count++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingProjectionHandler : IProjectionHandler<OrderPlaced>
    {
        public string ProjectionName => "order-summary";

        public ProjectionContext Context { get; private set; }

        public ValueTask ProjectAsync(OrderPlaced message, ProjectionContext context, CancellationToken ct = default)
        {
            Context = context;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingProjectionHandler : IProjectionHandler<OrderPlaced>
    {
        public string ProjectionName => "order-summary";

        public ValueTask ProjectAsync(OrderPlaced message, ProjectionContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("projection failed");
    }

    private sealed class CanceledProjectionHandler : IProjectionHandler<OrderPlaced>
    {
        public string ProjectionName => "order-summary";

        public ValueTask ProjectAsync(OrderPlaced message, ProjectionContext context, CancellationToken ct = default)
            => throw new OperationCanceledException("projection canceled");
    }

    private sealed class FailOnceProjectionHandler : IProjectionHandler<OrderPlaced>
    {
        private int _attemptCount;
        private bool _hasFailed;

        public string ProjectionName => "order-summary";

        public ValueTask ProjectAsync(OrderPlaced message, ProjectionContext context, CancellationToken ct = default)
        {
            _attemptCount++;
            if (!_hasFailed && _attemptCount == 2)
            {
                _hasFailed = true;
                throw new InvalidOperationException("transient projection failure");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryReplaySource<TMessage>(IReadOnlyList<ReplayEvent<TMessage>> events)
        : IEventReplaySource<TMessage>
    {
        public async IAsyncEnumerable<ReplayEvent<TMessage>> ReadAsync(
            string sourceName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var @event in events)
            {
                ct.ThrowIfCancellationRequested();

                if (@event.SourceName == sourceName)
                    yield return @event;

                await Task.Yield();
            }
        }
    }
}
