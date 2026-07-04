namespace PalDDD.Integration.Tests;

using Microsoft.EntityFrameworkCore;
using PalDDD.Projections;
using System.Globalization;

public sealed class ProjectionCheckpointEfCoreTests
{
    [Test]
    public async Task TryStartAsync_PersistsProcessingCheckpointAndGetAsyncReturnsIt(CancellationToken cancellationToken)
    {
        await using var db = new TestProjectionCheckpointDbContext(CreateOptions());
        var store = (IProjectionCheckpointStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);

        var checkpoint = await store.TryStartAsync(
            "order-summary",
            "orders",
            "42",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(checkpoint).IsNotNull();
        var loaded = await store.GetAsync("order-summary", "orders", "42", cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
        await Assert.That(loaded.UpdatedAt).IsEqualTo(now);
    }

    [Test]
    public async Task MarkCompletedAsync_PersistsCompletedCheckpointAndPreventsDuplicateStart(CancellationToken cancellationToken)
    {
        await using var db = new TestProjectionCheckpointDbContext(CreateOptions());
        var store = (IProjectionCheckpointStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        var checkpoint = await store.TryStartAsync(
            "order-summary",
            "orders",
            "42",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await store.MarkCompletedAsync(checkpoint!, now.AddSeconds(1), cancellationToken);
        db.ChangeTracker.Clear();

        var duplicate = await store.TryStartAsync(
            "order-summary",
            "orders",
            "42",
            now.AddSeconds(2),
            TimeSpan.FromMinutes(5),
            cancellationToken);
        var loaded = await store.GetAsync("order-summary", "orders", "42", cancellationToken);

        await Assert.That(duplicate).IsNull();
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);
        await Assert.That(loaded.UpdatedAt).IsEqualTo(now.AddSeconds(1));
    }

    [Test]
    public async Task TryStartAsync_ReusesFailedCheckpoint(CancellationToken cancellationToken)
    {
        await using var db = new TestProjectionCheckpointDbContext(CreateOptions());
        var store = (IProjectionCheckpointStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        var checkpoint = await store.TryStartAsync(
            "order-summary",
            "orders",
            "42",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        await store.MarkFailedAsync(checkpoint!, "projection failed", now.AddSeconds(1), cancellationToken);

        db.ChangeTracker.Clear();
        var retry = await store.TryStartAsync(
            "order-summary",
            "orders",
            "42",
            now.AddSeconds(2),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(retry).IsNotNull();
        await Assert.That(retry.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
        await Assert.That(retry.UpdatedAt).IsEqualTo(now.AddSeconds(2));
        await Assert.That(retry.Error).IsNull();
    }

    [Test]
    public async Task TryStartAsync_PreemptsZombieProcessingRecord(CancellationToken cancellationToken)
    {
        await using var db = new TestProjectionCheckpointDbContext(CreateOptions());
        var store = (IProjectionCheckpointStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);

        var first = await store.TryStartAsync(
            "order-summary", "orders", "42", now,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(first).IsNotNull();

        db.ChangeTracker.Clear();
        var stillAlive = await store.TryStartAsync(
            "order-summary", "orders", "42", now.AddMinutes(1),
            TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(stillAlive).IsNull();

        db.ChangeTracker.Clear();
        var preempted = await store.TryStartAsync(
            "order-summary", "orders", "42", now.AddMinutes(6),
            TimeSpan.FromMinutes(5),
            cancellationToken);
        await Assert.That(preempted).IsNotNull();
        await Assert.That(preempted.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
    }

    [Test]
    public async Task ResetAsync_RemovesOnlyMatchingProjectionSource(CancellationToken cancellationToken)
    {
        await using var db = new TestProjectionCheckpointDbContext(CreateOptions());
        var store = (IProjectionCheckpointStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        await store.TryStartAsync("order-summary", "orders", "42", now, TimeSpan.FromMinutes(5), cancellationToken);
        await store.TryStartAsync("order-summary", "payments", "9", now, TimeSpan.FromMinutes(5), cancellationToken);
        await store.TryStartAsync("customer-summary", "orders", "7", now, TimeSpan.FromMinutes(5), cancellationToken);

        await store.ResetAsync("order-summary", "orders", cancellationToken);

        await Assert.That(await store.GetAsync("order-summary", "orders", "42", cancellationToken)).IsNull();
        await Assert.That(await store.GetAsync("order-summary", "payments", "9", cancellationToken)).IsNotNull();
        await Assert.That(await store.GetAsync("customer-summary", "orders", "7", cancellationToken)).IsNotNull();
    }

    [Test]
    public async Task ResetAsync_HandlesLargeCheckpointCountWithoutChangeTrackerLeak(CancellationToken cancellationToken)
    {
        await using var db = new TestProjectionCheckpointDbContext(CreateOptions());
        var store = (IProjectionCheckpointStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);

        for (var i = 0; i < 500; i++)
        {
            await store.TryStartAsync(
                "order-summary",
                "orders",
                i.ToString(CultureInfo.InvariantCulture),
                now,
                TimeSpan.FromMinutes(5),
                cancellationToken);
        }
        db.ChangeTracker.Clear();

        await store.ResetAsync("order-summary", "orders", cancellationToken);

        await Assert.That(db.ChangeTracker.Entries<ProjectionCheckpoint>()).IsEmpty();

        var remaining = await db.ProjectionCheckpoints
            .Where(c => c.ProjectionName == "order-summary" && c.SourceName == "orders")
            .CountAsync(cancellationToken);
        await Assert.That(remaining).IsEqualTo(0);
    }

    private static DbContextOptions<TestProjectionCheckpointDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestProjectionCheckpointDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    private sealed class TestProjectionCheckpointDbContext(DbContextOptions<TestProjectionCheckpointDbContext> options)
        : ProjectionCheckpointDbContext(options);
}
