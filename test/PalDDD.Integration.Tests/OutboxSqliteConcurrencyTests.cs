using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Transactions;
using System.Globalization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

public sealed class OutboxSqliteConcurrencyTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<SqliteOutboxDbContext> _options = null!;

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(cancellationToken);

        _options = new DbContextOptionsBuilder<SqliteOutboxDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = new SqliteOutboxDbContext(_options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    [After(Test)]
    public async Task CleanupAsync()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task LeasePending_SequentialWorkers_SecondGetsNoMessage()
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        var firstLeased = await LeaseAsync("worker-1");
        var secondLeased = await LeaseAsync("worker-2");

        await Assert.That(firstLeased).Count().IsEqualTo(1);
        await Assert.That(firstLeased[0].Id).IsEqualTo(messageId);
        await Assert.That(secondLeased).IsEmpty();
    }

    [Test]
    public async Task MarkProcessed_TransactionCommits_PersistsAcrossContexts(CancellationToken cancellationToken)
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        await using var processorCtx = new SqliteOutboxDbContext(_options);
        var store = (IPalOutboxStore)processorCtx;
        var leased = await store.LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, cancellationToken);
        var leasedList = leased.ToList();
        await Assert.That(leasedList).Count().IsEqualTo(1);
        var msg = leasedList[0];

        store.MarkProcessed(msg, DateTimeOffset.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await using var readerCtx = new SqliteOutboxDbContext(_options);
        var loaded = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Processed);
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
    }

    [Test]
    public async Task ReleaseForRetry_IncrementsRetryCountAndClearsLease(CancellationToken cancellationToken)
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        await using var ctx = new SqliteOutboxDbContext(_options);
        var store = (IPalOutboxStore)ctx;
        var leased = await store.LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, cancellationToken);
        var leasedList = leased.ToList();
        await Assert.That(leasedList).Count().IsEqualTo(1);
        var msg = leasedList[0];

        store.ReleaseForRetry(msg, "broker timeout", DateTimeOffset.UtcNow.AddSeconds(30));
        await store.SaveChangesAsync(cancellationToken);

        await using var readerCtx = new SqliteOutboxDbContext(_options);
        var loaded = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.RetryCount).IsEqualTo(1);
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
        await Assert.That(loaded.Error).IsEqualTo("broker timeout");
    }

    [Test]
    public async Task MarkProcessed_LeaseTakenOverByReLease_StaleWriteRejected(CancellationToken cancellationToken)
    {
        // 三十四轮 ITM-210 租约 token 回归：worker-1 持租后租约被重租（同 owner 复用场景，
        // locked_until 更晚 = 新 token），旧 worker 的终态写必须影响 0 行——原 owner 守卫的
        // "LockedBy 相同即放行"分支会让旧写覆盖新租约状态
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        await using var ctx1 = new SqliteOutboxDbContext(_options);
        var store1 = (IPalOutboxStore)ctx1;
        var leased = (await store1.LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, cancellationToken)).ToList();
        await Assert.That(leased).Count().IsEqualTo(1);
        var staleMsg = leased[0];

        // 模拟同 owner 重租（token 单调变化：locked_until 更晚）
        await using var ctx2 = new SqliteOutboxDbContext(_options);
        var row = await ctx2.OutboxMessages.SingleAsync(m => m.Id == messageId, cancellationToken);
        row.LockedUntil = row.LockedUntil!.Value.AddMinutes(5);
        await ctx2.SaveChangesAsync(cancellationToken);

        // 旧 worker 终态写——token 失配应影响 0 行
        store1.MarkProcessed(staleMsg, DateTimeOffset.UtcNow);

        await using var readerCtx = new SqliteOutboxDbContext(_options);
        var final = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(final.Status).IsNotEqualTo(OutboxStatus.Processed);
        await Assert.That(final.LockedBy).IsEqualTo("worker-1"); // 新租约未被旧写清除
    }

    [Test]
    public async Task MarkProcessed_LeaseReleased_StaleWriteRejected(CancellationToken cancellationToken)
    {
        // 三十四轮 ITM-210 租约 token 回归：租约被释放（locked_by/until 置 NULL，
        // 如他路径 RequeueDead/ReleaseForRetry），旧 worker 的终态写必须被拒——
        // 原 "LockedBy IS NULL OR ..." 守卫的 NULL 放行分支正是 fencing 缺口
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        await using var ctx1 = new SqliteOutboxDbContext(_options);
        var store1 = (IPalOutboxStore)ctx1;
        var leased = (await store1.LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, cancellationToken)).ToList();
        var staleMsg = leased[0];

        // 模拟租约释放（行回到未租状态）
        await using var ctx2 = new SqliteOutboxDbContext(_options);
        var row = await ctx2.OutboxMessages.SingleAsync(m => m.Id == messageId, cancellationToken);
        row.LockedBy = null;
        row.LockedUntil = null;
        await ctx2.SaveChangesAsync(cancellationToken);

        // 旧 worker 终态写——持租 token 非空但行未租，必须影响 0 行
        store1.MarkProcessed(staleMsg, DateTimeOffset.UtcNow);

        await using var readerCtx = new SqliteOutboxDbContext(_options);
        var final = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(final.Status).IsEqualTo(OutboxStatus.Pending);
    }

    private async ValueTask SeedMessageAsync(PalUlid id, string type)
    {
        await using var ctx = new SqliteOutboxDbContext(_options);
        ctx.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = type,
            Payload = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = OutboxStatus.Pending
        });
        await ctx.SaveChangesAsync(CancellationToken.None);
    }

    private async ValueTask<IReadOnlyList<OutboxMessage>> LeaseAsync(string owner)
    {
        await using var ctx = new SqliteOutboxDbContext(_options);
        var store = (IPalOutboxStore)ctx;
        var leased = await store.LeasePendingMessagesAsync(
            10, owner, TimeSpan.FromMinutes(2), 5, CancellationToken.None);
        await store.SaveChangesAsync(CancellationToken.None);
        return leased;
    }

    private sealed class SqliteOutboxDbContext(DbContextOptions<SqliteOutboxDbContext> options)
        : OutboxDbContext(options)
    {
        private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse(
            "2026-06-23T00:00:00Z", CultureInfo.InvariantCulture);

        public override async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
            int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            var now = GetUtcNow();
            var pending = await OutboxMessages
                .Where(m => m.Status == OutboxStatus.Pending && m.RetryCount < maxRetryCount)
                .Take(batchSize)
                .ToListAsync(ct);

            var candidates = pending
                .Where(m => (m.NextAttemptAt == null || m.NextAttemptAt <= now)
                         && (m.LockedUntil == null || m.LockedUntil <= now))
                .ToList();

            foreach (var msg in candidates)
            {
                msg.LockedBy = owner;
                msg.LockedUntil = now.Add(leaseDuration);
            }
            await SaveChangesAsync(ct);
            return candidates;
        }

        protected override DateTimeOffset GetUtcNow() => FixedNow;
    }
}
