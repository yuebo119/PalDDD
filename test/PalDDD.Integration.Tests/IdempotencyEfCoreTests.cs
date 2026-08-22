namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Idempotency;
using System.Globalization;

public sealed class IdempotencyEfCoreTests
{
    [Test]
    public async Task TryStartAsync_PersistsProcessingRecordAndGetAsyncReturnsIt(CancellationToken cancellationToken)
    {
        await using var db = new TestIdempotencyDbContext(CreateOptions());
        var store = (IIdempotencyStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);

        var record = await store.TryStartAsync(
            "CreateOrder",
            "cmd-1",
            now,
            new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromMinutes(2), Retention = TimeSpan.FromHours(1) },
            cancellationToken);

        await Assert.That(record).IsNotNull();
        var loaded = await store.GetAsync("CreateOrder", "cmd-1", now, cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
        await Assert.That(loaded.LockedUntil).IsEqualTo(now.AddMinutes(2));
        await Assert.That(loaded.ExpiresAt).IsEqualTo(now.AddHours(1));
    }

    [Test]
    public async Task MarkCompletedAsync_PersistsReplayPayload(CancellationToken cancellationToken)
    {
        await using var db = new TestIdempotencyDbContext(CreateOptions());
        var store = (IIdempotencyStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        var record = await store.TryStartAsync(
            "CreateOrder",
            "cmd-1",
            now,
            IdempotencyPolicy.Default,
            cancellationToken);

        await store.MarkCompletedAsync(
            record!,
            "order-123"u8.ToArray(),
            now.AddSeconds(1),
            cancellationToken);

        db.ChangeTracker.Clear();
        var loaded = await store.GetAsync("CreateOrder", "cmd-1", now.AddSeconds(2), cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        await Assert.That(loaded.ResponsePayload?.ToArray()).IsEquivalentTo("order-123"u8.ToArray());
    }

    [Test]
    public async Task TryStartAsync_ReturnsNullWhenProcessingLeaseIsStillActive(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        await using var first = new TestIdempotencyDbContext(options);
        var firstStore = (IIdempotencyStore)first;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        await firstStore.TryStartAsync("CreateOrder", "cmd-1", now, IdempotencyPolicy.Default, cancellationToken);

        await using var second = new TestIdempotencyDbContext(options);
        var secondStore = (IIdempotencyStore)second;
        var duplicate = await secondStore.TryStartAsync(
            "CreateOrder",
            "cmd-1",
            now.AddSeconds(10),
            IdempotencyPolicy.Default,
            cancellationToken);

        await Assert.That(duplicate).IsNull();
    }

    [Test]
    public async Task TryStartAsync_ReusesExpiredProcessingLease(CancellationToken cancellationToken)
    {
        await using var db = new TestIdempotencyDbContext(CreateOptions());
        var store = (IIdempotencyStore)db;
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        await store.TryStartAsync(
            "CreateOrder",
            "cmd-1",
            now,
            new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromSeconds(5), Retention = TimeSpan.FromMinutes(10) },
            cancellationToken);

        db.ChangeTracker.Clear();
        var reused = await store.TryStartAsync(
            "CreateOrder",
            "cmd-1",
            now.AddSeconds(6),
            new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromSeconds(5), Retention = TimeSpan.FromMinutes(10) },
            cancellationToken);

        await Assert.That(reused).IsNotNull();
        await Assert.That(reused.LockedUntil).IsEqualTo(now.AddSeconds(11));
    }

    [Test]
    public async Task MarkFailedAsync_DoesNotOverwriteRecordCompletedByAnotherProcessor(CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        IdempotencyRecord staleRecord;

        await using (var first = new TestIdempotencyDbContext(options))
        {
            staleRecord = (await ((IIdempotencyStore)first).TryStartAsync(
                "CreateOrder",
                "cmd-1",
                now,
                new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromSeconds(5), Retention = TimeSpan.FromMinutes(10) },
                cancellationToken))!;
        }

        await using (var second = new TestIdempotencyDbContext(options))
        {
            var freshRecord = (await ((IIdempotencyStore)second).TryStartAsync(
                "CreateOrder",
                "cmd-1",
                now.AddSeconds(6),
                new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromSeconds(5), Retention = TimeSpan.FromMinutes(10) },
                cancellationToken))!;
            await ((IIdempotencyStore)second).MarkCompletedAsync(
                freshRecord,
                "order-123"u8.ToArray(),
                now.AddSeconds(7),
                cancellationToken);
        }

        await using (var stale = new TestIdempotencyDbContext(options))
        {
            await ((IIdempotencyStore)stale).MarkFailedAsync(
                staleRecord,
                "stale handler failed",
                now.AddSeconds(8),
                cancellationToken);
        }

        await using var reader = new TestIdempotencyDbContext(options);
        var loaded = await ((IIdempotencyStore)reader).GetAsync(
            "CreateOrder",
            "cmd-1",
            now.AddSeconds(9),
            cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        await Assert.That(loaded.ResponsePayload?.ToArray()).IsEquivalentTo("order-123"u8.ToArray());
        await Assert.That(loaded.Error).IsNull();
    }

    [Test]
    public async Task GetAsync_DoesNotMutateStoreWhenRecordIsExpired(CancellationToken cancellationToken)
    {
        // 读 API 不应隐含写操作 —— 过期记录返回 null，但不应触发 Remove+SaveChanges。
        // 删除过期记录是 GC 任务的职责，不在读路径中执行。
        var options = CreateOptions();
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);

        await using (var writer = new TestIdempotencyDbContext(options))
        {
            await ((IIdempotencyStore)writer).TryStartAsync(
                "CreateOrder",
                "cmd-1",
                now,
                new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromMinutes(2), Retention = TimeSpan.FromSeconds(5) },
                cancellationToken);
        }

        await using var reader = new TestIdempotencyDbContext(options);
        var afterRetention = now.AddSeconds(10);
        var loaded = await ((IIdempotencyStore)reader).GetAsync(
            "CreateOrder",
            "cmd-1",
            afterRetention,
            cancellationToken);

        await Assert.That(loaded).IsNull();

        // 验证记录依然存在 —— 读路径未执行删除写操作。
        await using var inspector = new TestIdempotencyDbContext(options);
        var stillThere = await inspector.IdempotencyRecords.SingleOrDefaultAsync(
            r => r.OperationName == "CreateOrder" && r.Key == "cmd-1",
            cancellationToken);
        await Assert.That(stillThere).IsNotNull();
    }

    [Test]
    public async Task TryStartAsync_Sqlite_DuplicateReturnsNullAndExpiredLeaseIsReused(CancellationToken cancellationToken)
    {
        // ITM-254（F14/PD26）：SQLite 内存库真 schema——复合主键 (OperationName,Key) 唯一真实在场，
        // 重复 TryStart 幂等判定 + 过期租约回收（UpdatedAt 并发令牌 SaveChanges）在关系型下验证
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateSqliteOptions(connection);
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);
        var policy = new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromSeconds(5), Retention = TimeSpan.FromMinutes(10) };

        await using (var first = new TestIdempotencyDbContext(options))
        {
            await first.Database.EnsureCreatedAsync(cancellationToken);
            var record = await ((IIdempotencyStore)first).TryStartAsync(
                "CreateOrder", "cmd-1", now, policy, cancellationToken);
            await Assert.That(record).IsNotNull();
        }

        // 写读 roundtrip：新 context 读回 Processing 记录（关系型持久化验证）
        await using var second = new TestIdempotencyDbContext(options);
        var loaded = await ((IIdempotencyStore)second).GetAsync("CreateOrder", "cmd-1", now.AddSeconds(1), cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
        await Assert.That(loaded.LockedUntil).IsEqualTo(now.AddSeconds(5));

        // 重复 TryStart（now+2，租约活跃窗口内 LockedUntil=now+5 > now+2）——返回 null
        var duplicate = await ((IIdempotencyStore)second).TryStartAsync(
            "CreateOrder", "cmd-1", now.AddSeconds(2), policy, cancellationToken);
        await Assert.That(duplicate).IsNull();

        // 过期回收（now+6 已过 LockedUntil=now+5）——租约被复用，LockedUntil 前移到 now+6+5s
        var reused = await ((IIdempotencyStore)second).TryStartAsync(
            "CreateOrder", "cmd-1", now.AddSeconds(6), policy, cancellationToken);
        await Assert.That(reused).IsNotNull();
        await Assert.That(reused.LockedUntil).IsEqualTo(now.AddSeconds(11));
    }

    private static DbContextOptions<TestIdempotencyDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestIdempotencyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    /// <summary>SQLite 内存库 options——需共享同一打开的 <see cref="SqliteConnection"/>（:memory: 库随连接存活）。</summary>
    private static DbContextOptions<TestIdempotencyDbContext> CreateSqliteOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<TestIdempotencyDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestIdempotencyDbContext(DbContextOptions<TestIdempotencyDbContext> options)
        : IdempotencyDbContext(options);
}
