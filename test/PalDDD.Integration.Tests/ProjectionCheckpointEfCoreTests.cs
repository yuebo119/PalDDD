namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
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

    [Test]
    public async Task TryStartAndMarkCompleted_Sqlite_RoundtripWithCompositePrimaryKey(CancellationToken cancellationToken)
    {
        // ITM-254（F14/PD26）：SQLite 内存库真 schema——复合主键 (ProjectionName,SourceName,Position)
        // 唯一真实在场，TryStart → MarkCompleted → 重复 TryStart 幂等链在关系型下验证
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateSqliteOptions(connection);
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);

        await using (var writer = new TestProjectionCheckpointDbContext(options))
        {
            await writer.Database.EnsureCreatedAsync(cancellationToken);
            var store = (IProjectionCheckpointStore)writer;
            var checkpoint = await store.TryStartAsync(
                "order-summary", "orders", "42", now, TimeSpan.FromMinutes(5), cancellationToken);
            await Assert.That(checkpoint).IsNotNull();
            await store.MarkCompletedAsync(checkpoint!, now.AddSeconds(1), cancellationToken);
        }

        // 写读 roundtrip：新 context 读回 Completed 记录
        await using var reader = new TestProjectionCheckpointDbContext(options);
        var loaded = await ((IProjectionCheckpointStore)reader).GetAsync("order-summary", "orders", "42", cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);
        await Assert.That(loaded.UpdatedAt).IsEqualTo(now.AddSeconds(1));

        // 已完成的位置永不重新处理——重复 TryStart 返回 null（真索引/真主键在场）
        var duplicate = await ((IProjectionCheckpointStore)reader).TryStartAsync(
            "order-summary", "orders", "42", now.AddSeconds(2), TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(duplicate).IsNull();
    }

    [Test]
    public async Task AddDuplicateCheckpoint_Sqlite_ViolatesCompositePrimaryKey(CancellationToken cancellationToken)
    {
        // ITM-254（F14）：真实复合主键存在性守护——同 (ProjectionName,SourceName,Position)
        // 直插第二行必须触发主键冲突（TryStart 的幂等语义依赖此键真实在场）
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateSqliteOptions(connection);
        var now = DateTimeOffset.Parse("2026-05-30T00:00:00Z", CultureInfo.InvariantCulture);

        await using var db = new TestProjectionCheckpointDbContext(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        db.ProjectionCheckpoints.Add(new ProjectionCheckpoint(
            "order-summary", "orders", "42", ProjectionCheckpointStatus.Processing, now));
        await db.SaveChangesAsync(cancellationToken);
        // 先提交第一条再清跟踪——同 context Add 同键两实例会被 EF 身份映射直接拒绝
        // （InvalidOperationException），清空后第二条走 INSERT → 真实主键冲突
        db.ChangeTracker.Clear();
        db.ProjectionCheckpoints.Add(new ProjectionCheckpoint(
            "order-summary", "orders", "42", ProjectionCheckpointStatus.Completed, now.AddSeconds(1)));

        await Assert.That(async () =>
            await db.SaveChangesAsync(cancellationToken)).Throws<DbUpdateException>();
    }

    private static DbContextOptions<TestProjectionCheckpointDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestProjectionCheckpointDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    /// <summary>SQLite 内存库 options——需共享同一打开的 <see cref="SqliteConnection"/>（:memory: 库随连接存活）。</summary>
    private static DbContextOptions<TestProjectionCheckpointDbContext> CreateSqliteOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<TestProjectionCheckpointDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestProjectionCheckpointDbContext(DbContextOptions<TestProjectionCheckpointDbContext> options)
        : ProjectionCheckpointDbContext(options);
}
