namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
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
        // P2 修复（八轮）：ReleaseForRetry 改用 ExecuteUpdate 直写 SQL——
        // InMemory provider 不支持 ExecuteUpdate，本测试迁至 SQLite 内存库
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var db = new TestOutboxDbContext(CreateSqliteOptions(connection), FixedNow);
        await db.Database.EnsureCreatedAsync(cancellationToken);
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
        // 模拟 OutboxBatchProcessor.PersistSingleAsync：ReleaseForRetry 已直写 SQL，
        // 无 tracked 修改时此处为 no-op（不再抛 RetryCount 并发令牌假冲突）
        await store.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var loaded = await db.OutboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.ProcessedAt).IsNull();
        await Assert.That(loaded.Error).IsEqualTo("broker failed");
        await Assert.That(loaded.NextAttemptAt).IsEqualTo(nextAttemptAt);
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
        await Assert.That(loaded.RetryCount).IsEqualTo(1);
    }

    [Test]
    public async Task ReleaseForRetry_WhenLeaseStolenByAnotherOwner_DoesNotOverwrite(CancellationToken cancellationToken)
    {
        // P2 修复（八轮）守卫验证：租约被抢占（他实例已 re-lease）时，
        // 原持有者的释放尝试影响 0 行——不覆盖新持有者的租约
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var db = new TestOutboxDbContext(CreateSqliteOptions(connection), FixedNow);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        var store = (IPalOutboxStore)db;
        var message = CreateMessage(
            "orders.submitted",
            FixedNow,
            lockedBy: "worker-1",
            lockedUntil: FixedNow.AddMinutes(2));
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        // 模拟租约被抢占：DB 行的 LockedBy 已被另一持有者改写
        db.ChangeTracker.Clear();
        var rival = await db.OutboxMessages.SingleAsync(cancellationToken);
        rival.LockedBy = "worker-2";
        rival.LockedUntil = FixedNow.AddMinutes(5);
        await db.SaveChangesAsync(cancellationToken);

        // 过期持有者 worker-1（入参 message 的内存快照）的释放尝试：守卫拒绝
        store.ReleaseForRetry(message, "broker failed", FixedNow.AddMinutes(3));
        db.ChangeTracker.Clear();

        var loaded = await db.OutboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.LockedBy).IsEqualTo("worker-2");
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.RetryCount).IsEqualTo(0);
        await Assert.That(loaded.Error).IsNull();
    }

    [Test]
    public async Task LeasePendingMessagesAsync_GrantsAndPersistsLease_ProductionSqlite(CancellationToken cancellationToken)
    {
        // ITM-252（F10/F11）：TestOutboxDbContext 曾带零调用的 Lease 死 override（潜伏缺陷已删），
        // 本文件此前对生产 EF Lease 零覆盖——此测试直接继承生产 SqliteOutboxDbContext，
        // SQLite 内存库跑真实 SQL 翻译。ITM-261 修复后时间过滤物化后内存进行
        //（EF Core 11 preview7 SQLite 对 DateTimeOffset 有序比较不可翻译），状态/重试过滤与
        // 分页仍在 SQL 内——租约写入/持久化断言覆盖完整生产路径。
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var db = new SqliteLeaseOutboxDbContext(CreateSqliteLeaseOptions(connection));
        await db.Database.EnsureCreatedAsync(cancellationToken);
        var message = CreateMessage("orders.submitted", DateTimeOffset.UtcNow.AddMinutes(-5));
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var leased = await db.LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, cancellationToken);

        var leasedList = leased.ToList();
        await Assert.That(leasedList).Count().IsEqualTo(1);
        await Assert.That(leasedList[0].LockedBy).IsEqualTo("worker-1");
        await Assert.That(leasedList[0].Status).IsEqualTo(OutboxStatus.Pending);
        var expectedLockedUntil = leasedList[0].LockedUntil;
        await Assert.That(expectedLockedUntil).IsNotNull();

        // Lease 内部已 SaveChanges——租约用新 context 读回验证持久化（PD26 写读 roundtrip）
        await using var reader = new SqliteLeaseOutboxDbContext(CreateSqliteLeaseOptions(connection));
        var loaded = await reader.OutboxMessages.SingleAsync(cancellationToken);
        await Assert.That(loaded.LockedBy).IsEqualTo("worker-1");
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.LockedUntil).IsEqualTo(expectedLockedUntil);
    }

    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse(
        "2026-05-31T00:00:00Z",
        CultureInfo.InvariantCulture);

    private static DbContextOptions<TestOutboxDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestOutboxDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    /// <summary>SQLite 内存库 options——需共享同一打开的 <see cref="SqliteConnection"/>（:memory: 库随连接存活）。</summary>
    private static DbContextOptions<TestOutboxDbContext> CreateSqliteOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<TestOutboxDbContext>()
            .UseSqlite(connection)
            .Options;

    /// <summary>生产 Lease 专用的 SQLite options（<see cref="SqliteLeaseOutboxDbContext"/>）。</summary>
    private static DbContextOptions<SqliteLeaseOutboxDbContext> CreateSqliteLeaseOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<SqliteLeaseOutboxDbContext>()
            .UseSqlite(connection)
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
        // ITM-252：原死 override（零调用、潜伏缺陷）已删除。InMemory 变体不提供 Lease——
        // Lease 行为由 SqliteLeaseOutboxDbContext（生产 SqliteOutboxDbContext 继承）测试。
        // 显式 throw 防止未来测试误用 InMemory 上下文测 Lease 而不自知。
        public override ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
            int batchSize,
            string owner,
            TimeSpan leaseDuration,
            int maxRetryCount,
            CancellationToken ct)
            => throw new NotSupportedException(
                "TestOutboxDbContext（InMemory）不提供 Lease 实现——Lease 测试请用 SqliteLeaseOutboxDbContext（生产路径）。");

        protected override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// 生产 Lease 测试上下文——直接继承生产 <see cref="SqliteOutboxDbContext"/>，
    /// 无任何重写（ITM-252：原 TestOutboxDbContext 的 Lease 死 override 已删除）。
    /// </summary>
    private sealed class SqliteLeaseOutboxDbContext(DbContextOptions<SqliteLeaseOutboxDbContext> options)
        : SqliteOutboxDbContext(options);
}
