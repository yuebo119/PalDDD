namespace PalDDD.Integration.Tests;

using Microsoft.EntityFrameworkCore;
using PalDDD.Transactions;
using System.Globalization;

public sealed class InboxEfCoreTests
{
    [Test]
    public async Task TryStartProcessingAsync_PersistsProcessingRecord(CancellationToken cancellationToken)
    {
        await using var db = new TestInboxDbContext(CreateOptions());
        var store = (IInboxStore)db;
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);

        var record = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(record).IsNotNull();
        await Assert.That(record.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(record.Attempts).IsEqualTo(1);

        db.ChangeTracker.Clear();
        var loaded = await db.InboxMessages.SingleAsync(
            x => x.ConsumerName == "orders" && x.MessageId == "message-1",
            cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(loaded.ProcessingStartedAt).IsEqualTo(now);
    }

    [Test]
    public async Task TryStartProcessingAsync_ReturnsNullWhenProcessingLeaseIsStillActive(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        await using (var first = new TestInboxDbContext(options))
        {
            var firstStore = (IInboxStore)first;
            await firstStore.TryStartProcessingAsync(
                "orders",
                "message-1",
                now,
                TimeSpan.FromMinutes(5),
                cancellationToken);
        }

        await using var second = new TestInboxDbContext(options);
        var secondStore = (IInboxStore)second;
        var duplicate = await secondStore.TryStartProcessingAsync(
            "orders",
            "message-1",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(duplicate).IsNull();
    }

    [Test]
    public async Task TryStartProcessingAsync_ReusesFailedMessage(CancellationToken cancellationToken)
    {
        await using var db = new TestInboxDbContext(CreateOptions());
        var store = (IInboxStore)db;
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        var record = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await store.MarkFailedAsync(record!, "handler failed", cancellationToken);
        db.ChangeTracker.Clear();

        var retry = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(retry).IsNotNull();
        await Assert.That(retry.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(retry.Attempts).IsEqualTo(2);
        await Assert.That(retry.LastError).IsNull();
    }

    [Test]
    public async Task MarkProcessedAsync_PreventsDuplicateProcessing(CancellationToken cancellationToken)
    {
        await using var db = new TestInboxDbContext(CreateOptions());
        var store = (IInboxStore)db;
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        var record = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now,
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await store.MarkProcessedAsync(record!, now.AddSeconds(1), cancellationToken);
        db.ChangeTracker.Clear();

        var duplicate = await store.TryStartProcessingAsync(
            "orders",
            "message-1",
            now.AddMinutes(10),
            TimeSpan.FromMinutes(5),
            cancellationToken);

        await Assert.That(duplicate).IsNull();
        var loaded = await db.InboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(InboxStatus.Processed);
        await Assert.That(loaded.ProcessedAt).IsEqualTo(now.AddSeconds(1));
    }

    [Test]
    public async Task MarkFailedAsync_DoesNotOverwriteRecordCompletedByAnotherProcessor(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        var now = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);
        InboxMessage staleRecord;

        await using (var first = new TestInboxDbContext(options))
        {
            staleRecord = (await ((IInboxStore)first).TryStartProcessingAsync(
                "orders",
                "message-1",
                now,
                TimeSpan.FromSeconds(5),
                cancellationToken))!;
        }

        await using (var second = new TestInboxDbContext(options))
        {
            var freshRecord = (await ((IInboxStore)second).TryStartProcessingAsync(
                "orders",
                "message-1",
                now.AddSeconds(6),
                TimeSpan.FromSeconds(5),
                cancellationToken))!;
            await ((IInboxStore)second).MarkProcessedAsync(
                freshRecord,
                now.AddSeconds(7),
                cancellationToken);
        }

        await using (var stale = new TestInboxDbContext(options))
        {
            await ((IInboxStore)stale).MarkFailedAsync(
                staleRecord,
                "stale handler failed",
                cancellationToken);
        }

        await using var reader = new TestInboxDbContext(options);
        var loaded = await reader.InboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(InboxStatus.Processed);
        await Assert.That(loaded.LastError).IsNull();
    }

    [Test]
    public async Task TryStartProcessingAsync_PreemptsZombieRecordAfterTimeout(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        var startedAt = DateTimeOffset.Parse("2026-05-31T00:00:00Z", CultureInfo.InvariantCulture);

        await using (var first = new TestInboxDbContext(options))
        {
            var firstStore = (IInboxStore)first;
            var record = await firstStore.TryStartProcessingAsync(
                "orders",
                "message-1",
                startedAt,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            await Assert.That(record).IsNotNull();
        }

        await using var second = new TestInboxDbContext(options);
        var secondStore = (IInboxStore)second;
        var preempted = await secondStore.TryStartProcessingAsync(
            "orders",
            "message-1",
            startedAt.AddSeconds(31),
            TimeSpan.FromSeconds(30),
            cancellationToken);

        await Assert.That(preempted).IsNotNull();
        await Assert.That(preempted.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(preempted.Attempts).IsEqualTo(2);
        await Assert.That(preempted.ProcessingStartedAt).IsEqualTo(startedAt.AddSeconds(31));
    }

    private static DbContextOptions<TestInboxDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestInboxDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    private sealed class TestInboxDbContext(DbContextOptions<TestInboxDbContext> options)
        : InboxDbContext(options);
}
