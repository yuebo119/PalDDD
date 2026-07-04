namespace PalDDD.Integration.Tests;

using Microsoft.EntityFrameworkCore;
using PalDDD.Transactions;
using System.Globalization;

public sealed class OutboxEfCoreTests
{
    [Test]
    public async Task AddMessageAndSaveChangesAsync_PersistsPendingRecord(CancellationToken cancellationToken)
    {
        await using var db = new TestOutboxDbContext(CreateOptions(), FixedNow);
        var store = (IPalOutboxStore)db;
        var message = CreateMessage("orders.submitted", FixedNow);

        store.AddMessage(message);
        await store.SaveChangesAsync(cancellationToken);

        db.ChangeTracker.Clear();
        var loaded = await db.OutboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Id).IsEqualTo(message.Id);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.Type).IsEqualTo("orders.submitted");
        await Assert.That(loaded.Payload).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task GetPendingMessagesAsync_ReturnsOnlyEligibleUnlockedMessages(CancellationToken cancellationToken)
    {
        await using var db = new TestOutboxDbContext(CreateOptions(), FixedNow);
        db.OutboxMessages.Add(CreateMessage("eligible", FixedNow.AddMinutes(-4)));
        db.OutboxMessages.Add(CreateMessage("future-retry", FixedNow.AddMinutes(-3), nextAttemptAt: FixedNow.AddMinutes(1)));
        db.OutboxMessages.Add(CreateMessage("active-lease", FixedNow.AddMinutes(-2), lockedUntil: FixedNow.AddMinutes(1)));
        db.OutboxMessages.Add(CreateMessage("processed", FixedNow.AddMinutes(-1), status: OutboxStatus.Processed));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var pending = await db.GetPendingMessagesAsync(10, new OutboxOptions().MaxRetryCount, cancellationToken);

        var pendingList = pending.ToList();
        await Assert.That(pendingList).Count().IsEqualTo(1);
        var message = pendingList[0];
        await Assert.That(message.Type).IsEqualTo("eligible");
    }

    [Test]
    public async Task MarkProcessed_ClearsLeaseAndRetryState(CancellationToken cancellationToken)
    {
        await using var db = new TestOutboxDbContext(CreateOptions(), FixedNow);
        var store = (IPalOutboxStore)db;
        var message = CreateMessage(
            "orders.submitted",
            FixedNow,
            nextAttemptAt: FixedNow.AddMinutes(5),
            lockedBy: "worker-1",
            lockedUntil: FixedNow.AddMinutes(2),
            error: "previous failure");
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        store.MarkProcessed(message, FixedNow.AddSeconds(1));
        await store.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var loaded = await db.OutboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Processed);
        await Assert.That(loaded.ProcessedAt).IsEqualTo(FixedNow.AddSeconds(1));
        await Assert.That(loaded.NextAttemptAt).IsNull();
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
        await Assert.That(loaded.Error).IsNull();
    }

    [Test]
    public async Task ReleaseForRetry_ClearsLeaseAndSchedulesNextAttempt(CancellationToken cancellationToken)
    {
        await using var db = new TestOutboxDbContext(CreateOptions(), FixedNow);
        var store = (IPalOutboxStore)db;
        var message = CreateMessage(
            "orders.submitted",
            FixedNow,
            lockedBy: "worker-1",
            lockedUntil: FixedNow.AddMinutes(2));
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        var nextAttemptAt = FixedNow.AddMinutes(3);
        store.ReleaseForRetry(message, "broker failed", nextAttemptAt);
        await store.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var loaded = await db.OutboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.ProcessedAt).IsNull();
        await Assert.That(loaded.Error).IsEqualTo("broker failed");
        await Assert.That(loaded.NextAttemptAt).IsEqualTo(nextAttemptAt);
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
    }

    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse(
        "2026-05-31T00:00:00Z",
        CultureInfo.InvariantCulture);

    private static DbContextOptions<TestOutboxDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestOutboxDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    private static OutboxMessage CreateMessage(
        string type,
        DateTimeOffset createdAt,
        OutboxStatus status = OutboxStatus.Pending,
        DateTimeOffset? nextAttemptAt = null,
        string? lockedBy = null,
        DateTimeOffset? lockedUntil = null,
        string? error = null)
        => new()
        {
            Type = type,
            Payload = [1, 2, 3],
            CreatedAt = createdAt,
            Status = status,
            NextAttemptAt = nextAttemptAt,
            LockedBy = lockedBy,
            LockedUntil = lockedUntil,
            Error = error
        };

    private sealed class TestOutboxDbContext(
        DbContextOptions<TestOutboxDbContext> options,
        DateTimeOffset utcNow) : OutboxDbContext(options)
    {
        public override async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
            int batchSize,
            string owner,
            TimeSpan leaseDuration,
            int maxRetryCount,
            CancellationToken ct)
        {
            var messages = await GetPendingMessagesAsync(batchSize, maxRetryCount, ct);
            foreach (var message in messages)
            {
                message.LockedBy = owner;
                message.LockedUntil = utcNow.Add(leaseDuration);
            }

            await SaveChangesAsync(ct);
            return messages;
        }

        protected override DateTimeOffset GetUtcNow() => utcNow;
    }
}
