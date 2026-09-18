using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

public sealed class OutboxSqliteConcurrencyTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestSqliteOutboxDbContext> _options = null!;

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(cancellationToken);

        _options = new DbContextOptionsBuilder<TestSqliteOutboxDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = new TestSqliteOutboxDbContext(_options);
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

        await using var firstCtx = new TestSqliteOutboxDbContext(_options);
        var firstLeased = await ((IPalOutboxStore)firstCtx).LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);
        await using var secondCtx = new TestSqliteOutboxDbContext(_options);
        var secondLeased = await ((IPalOutboxStore)secondCtx).LeasePendingMessagesAsync(
            10, "worker-2", TimeSpan.FromMinutes(2), 5, CancellationToken.None);

        await Assert.That(firstLeased).Count().IsEqualTo(1);
        await Assert.That(firstLeased[0].Id).IsEqualTo(messageId);
        await Assert.That(secondLeased).IsEmpty();
    }

    // TST-402 改名：原名 MarkProcessed_TransactionCommits_PersistsAcrossContexts 名不副实
    // （本测试不涉及显式事务提交语义，聚焦 MarkProcessed 跨 DbContext 持久化）
    [Test]
    public async Task MarkProcessed_PersistsAcrossContexts(CancellationToken cancellationToken)
    {
        // 历史（ITM-261 已修复，原"见 Skip 说明"勘正）——
        // 租约改直接种子建立；本测试聚焦 MarkProcessed 持久化语义（其 fencing 用 == 相等比较，可翻译）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var processorCtx = new TestSqliteOutboxDbContext(_options);
        var store = (IPalOutboxStore)processorCtx;
        var msg = await processorCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        store.MarkProcessed(msg, DateTimeOffset.UtcNow);
        await store.SaveChangesAsync(cancellationToken);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
        var loaded = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Processed);
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
    }

    [Test]
    public async Task ReleaseForRetry_IncrementsRetryCountAndClearsLease(CancellationToken cancellationToken)
    {
        // 租约直接种子建立（ITM-261 已修复；保留此形态聚焦终态写语义——见类尾注释）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var store = (IPalOutboxStore)ctx;
        var msg = await ctx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        store.ReleaseForRetry(msg, "broker timeout", DateTimeOffset.UtcNow.AddSeconds(30));
        await store.SaveChangesAsync(cancellationToken);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
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
        // 租约直接种子建立（ITM-261 已修复；保留此形态聚焦终态写语义——见类尾注释）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var ctx1 = new TestSqliteOutboxDbContext(_options);
        var store1 = (IPalOutboxStore)ctx1;
        var staleMsg = await ctx1.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        // 模拟同 owner 重租（token 单调变化：locked_until 更晚）
        await using var ctx2 = new TestSqliteOutboxDbContext(_options);
        var row = await ctx2.OutboxMessages.SingleAsync(m => m.Id == messageId, cancellationToken);
        row.LockedUntil = row.LockedUntil!.Value.AddMinutes(5);
        await ctx2.SaveChangesAsync(cancellationToken);

        // 旧 worker 终态写——token 失配应影响 0 行
        store1.MarkProcessed(staleMsg, DateTimeOffset.UtcNow);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
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
        // 租约直接种子建立（ITM-261 已修复；保留此形态聚焦终态写语义——见类尾注释）
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-1", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(2));

        await using var ctx1 = new TestSqliteOutboxDbContext(_options);
        var store1 = (IPalOutboxStore)ctx1;
        var staleMsg = await ctx1.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);

        // 模拟租约释放（行回到未租状态）
        await using var ctx2 = new TestSqliteOutboxDbContext(_options);
        var row = await ctx2.OutboxMessages.SingleAsync(m => m.Id == messageId, cancellationToken);
        row.LockedBy = null;
        row.LockedUntil = null;
        await ctx2.SaveChangesAsync(cancellationToken);

        // 旧 worker 终态写——持租 token 非空但行未租，必须影响 0 行
        store1.MarkProcessed(staleMsg, DateTimeOffset.UtcNow);

        await using var readerCtx = new TestSqliteOutboxDbContext(_options);
        var final = await readerCtx.OutboxMessages.SingleAsync(
            m => m.Id == messageId, cancellationToken);
        await Assert.That(final.Status).IsEqualTo(OutboxStatus.Pending);
    }

    // ── 2026-09-19 批量化（decision-2026-09-17 shape 2）回归三联 ──

    /// <summary>批量分割：4 条消息两 worker 各租 2——交集为空、合并覆盖全部
    /// （单语句资格谓词子查询重估的互斥语义，对齐 Dapper/PalORM 同款测试）。</summary>
    [Test]
    public async Task LeasePending_BatchSplitAcrossWorkers_NoIntersection()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => PalUlid.New()).ToList();
        foreach (var id in ids)
            await SeedMessageAsync(id, "orders.created.v1");

        await using var firstCtx = new TestSqliteOutboxDbContext(_options);
        var firstLeased = await ((IPalOutboxStore)firstCtx).LeasePendingMessagesAsync(
            2, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);
        await using var secondCtx = new TestSqliteOutboxDbContext(_options);
        var secondLeased = await ((IPalOutboxStore)secondCtx).LeasePendingMessagesAsync(
            2, "worker-2", TimeSpan.FromMinutes(2), 5, CancellationToken.None);

        await Assert.That(firstLeased).Count().IsEqualTo(2);
        await Assert.That(secondLeased).Count().IsEqualTo(2);
        var firstIds = firstLeased.Select(m => m.Id).ToHashSet();
        await Assert.That(secondLeased.All(m => !firstIds.Contains(m.Id))).IsTrue();
        var union = firstLeased.Select(m => m.Id).Concat(secondLeased.Select(m => m.Id)).ToHashSet();
        await Assert.That(union.SetEquals(ids)).IsTrue();
    }

    /// <summary>过期租约回收：LockedUntil 已过期的行对新一轮租约可见（谓词
    /// <c>LockedUntil IS NULL OR LockedUntil &lt;= now</c> 的回收路径）。</summary>
    [Test]
    public async Task LeasePending_ExpiredLease_RecoversMessage()
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1",
            lockedBy: "worker-dead", lockedUntil: DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var leased = await ((IPalOutboxStore)ctx).LeasePendingMessagesAsync(
            10, "worker-new", TimeSpan.FromMinutes(2), 5, CancellationToken.None);

        await Assert.That(leased).Count().IsEqualTo(1);
        await Assert.That(leased[0].Id).IsEqualTo(messageId);
        await Assert.That(leased[0].LockedBy).IsEqualTo("worker-new"); // 守卫回读带出租约标识
    }

    /// <summary>UTC 前提锁定：lease 写入的 LockedUntil 存储文本偏移恒 +00:00——
    /// 时间列文本序比较等于时间序的前提（类头 remarks 的 UTC 前提，机器本地时区
    /// 为 +08:00 仍须成立，因时间源恒 GetUtcNow）。</summary>
    [Test]
    public async Task LeasePending_LockedUntilStoredAsUtc_TextOrderSound()
    {
        var messageId = PalUlid.New();
        await SeedMessageAsync(messageId, "orders.created.v1");

        await using var ctx = new TestSqliteOutboxDbContext(_options);
        var leased = await ((IPalOutboxStore)ctx).LeasePendingMessagesAsync(
            10, "worker-1", TimeSpan.FromMinutes(2), 5, CancellationToken.None);
        await Assert.That(leased).Count().IsEqualTo(1);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT LockedUntil FROM OutboxMessages WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        var storedText = (string)cmd.ExecuteScalar()!;
        await Assert.That(storedText.EndsWith("+00:00", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>种子一条 Pending 消息；可选预置租约字段（终态写测试聚焦语义的直建租约形态——见类尾 ITM-261 勘正注释）。</summary>
    private async ValueTask SeedMessageAsync(
        PalUlid id,
        string type,
        string? lockedBy = null,
        DateTimeOffset? lockedUntil = null)
    {
        await using var ctx = new TestSqliteOutboxDbContext(_options);
        ctx.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = type,
            Payload = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = OutboxStatus.Pending,
            LockedBy = lockedBy,
            LockedUntil = lockedUntil
        });
        await ctx.SaveChangesAsync(CancellationToken.None);
    }

    // ITM-252（F10/F11 修复）：此处曾本地重写 LeasePendingMessagesAsync（时间过滤移到
    // ToListAsync 之后的内存过滤）——5 个测试（含 3 个 ITM-210 fencing 回归）的 Lease 端
    // 测的是重写版，生产 EF Lease 零覆盖。重写已删除：本类仅继承生产抽象类
    // SqliteOutboxDbContext，租约/终态写全部走生产实现路径，时钟用生产默认 TimeProvider.System。
    //
    // ITM-261（R40 主线程修复，历史勘正——原"缺陷固化 Skip 等 src 修复"描述已过时）：
    // 删除重写后曾暴露生产 GetPending/Lease 的 "DateTimeOffset <= now" 在 EF Core 11 preview7
    // SQLite provider 下不可翻译（== 可译、<= 与 ORDER BY 均抛）。src 侧已改为分页物化 + 内存
    // 时间过滤 + OrderBy(Id)（ULID 字典序=创建序）——Lease 互斥测试已恢复直调生产路径（无 Skip）；
    // MarkProcessed/ReleaseForRetry/fencing 回归保留直接种子租约字段形态（终态写仍走生产路径）。
    private sealed class TestSqliteOutboxDbContext(DbContextOptions<TestSqliteOutboxDbContext> options)
        : SqliteOutboxDbContext(options);
}
