using ByteAether.Ulid;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Outbox Store 基础测试 —— 验证 Fixture + PalORM 替换后的核心 CRUD 行为。
/// 迁移自 DapperStoreTests.cs 的 Outbox 部分（15 测试，先迁移 5 个核心场景验证框架）。
/// </summary>
public class PalOrmOutboxStoreTests
{
    [Test]
    public async Task Outbox_AddMessage_ThenGetPending()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("test.event.v1");

        store.AddMessage(msg);

        var pending = await store.GetPendingMessagesAsync(batchSize: 10, maxRetryCount: 5, ct: default);
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Type).IsEqualTo("test.event.v1");
        await Assert.That(pending[0].Status).IsEqualTo(OutboxStatus.Pending);
    }

    [Test]
    public async Task Outbox_NonPositiveMaxRetryCount_ThrowsArgumentOutOfRange()
    {
        // ITM-659 守卫族收口（镜像 DapperStoreTests）：maxRetryCount 非正守卫——非正值使
        // retry_count < maxRetryCount 恒假（retry_count >= 0），原实现三方言 SQL 下静默
        // 空返回无诊断（直调路径防御性 fail-fast；守卫在 SQL 执行前抛出）。
        // GetPending 与 Lease 两方法同守卫（Lease 三处 SQL 分支共用同一谓词）。
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);

        await Assert.That(async () => await store.GetPendingMessagesAsync(10, -1, default))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await store.LeasePendingMessagesAsync(10, "worker-1", TimeSpan.FromMinutes(5), 0, default))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Outbox_MarkProcessed()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("test.event.v2");
        store.AddMessage(msg);

        store.MarkProcessed(msg, DateTimeOffset.UtcNow);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task Outbox_MarkDead()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("test.dead");
        store.AddMessage(msg);

        store.MarkDead(msg, "network timeout", DateTimeOffset.UtcNow);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task Outbox_LeasePendingMessages_AtomicAcquisition()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        store.AddMessage(CreateOutboxMessage("lease.test"));

        var leased = await store.LeasePendingMessagesAsync(10, "worker-1", TimeSpan.FromMinutes(5), 10, default);

        await Assert.That(leased).Count().IsEqualTo(1);
        await Assert.That(leased[0].LockedBy).IsEqualTo("worker-1");
    }

    [Test]
    public async Task Outbox_LeasePendingMessages_SkipLocked()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        store.AddMessage(CreateOutboxMessage("concurrent.test"));

        // 第一次 Lease 成功
        var first = await store.LeasePendingMessagesAsync(10, "worker-1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(first).Count().IsEqualTo(1);

        // 第二次 Lease 应为空（已被 worker-1 锁定）
        var second = await store.LeasePendingMessagesAsync(10, "worker-2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_ReleaseForRetry()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("retry.test");
        store.AddMessage(msg);

        // ReleaseForRetry 设置 next_attempt_at 为未来 → GetPending 应为空
        var future = DateTimeOffset.UtcNow.AddMinutes(5);
        store.ReleaseForRetry(msg, "transient error", future);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
        await Assert.That(msg.RetryCount).IsEqualTo(1);
    }

    [Test]
    public async Task Outbox_DeadLetterFilter_Rh10()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("dlq.test");
        store.AddMessage(msg);

        // 循环 10 次重试到上限 —— 第 11 次 GetPending 应过滤掉
        for (var i = 0; i < 10; i++)
        {
            store.ReleaseForRetry(msg, $"attempt {i + 1}", DateTimeOffset.UtcNow.AddSeconds(-1));  // 过去时间，立即可见
        }

        await Assert.That(msg.RetryCount).IsEqualTo(10);
        var pending = await store.GetPendingMessagesAsync(10, maxRetryCount: 10, ct: default);
        await Assert.That(pending).IsEmpty();  // retry_count=10 不再返回（WHERE retry_count<10）
    }

    [Test]
    public async Task Outbox_AddMessagesAsync_BulkInsert()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);

        var messages = Enumerable.Range(0, 5)
            .Select(i => CreateOutboxMessage($"bulk.event.{i}"))
            .ToList();

        var count = await store.AddMessagesAsync(messages);
        await Assert.That(count).IsEqualTo(5);

        var pending = await store.GetPendingMessagesAsync(100, 10, default);
        await Assert.That(pending).Count().IsEqualTo(5);
    }

    [Test]
    public async Task Outbox_AddMessagesAsync_EmptyList()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);

        var count = await store.AddMessagesAsync([]);
        await Assert.That(count).IsEqualTo(0);
    }

    // ── ITM-642：RequeueDeadAsync（ADR-011 Dead→Pending）行为——PalORM 栈此前零覆盖 ──

    [Test]
    public async Task Outbox_RequeueDeadAsync_DeadMessage_FlipsToPendingAndPreservesRetryCount()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("requeue.dead");
        msg.RetryCount = 7; // 失败历史（MarkDead 不重置 retry_count）
        store.AddMessage(msg);
        store.MarkDead(msg, "original failure", DateTimeOffset.UtcNow);

        var nextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var rows = await store.RequeueDeadAsync(msg.Id, nextAttempt, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(1);
        var pending = await store.GetPendingMessagesAsync(10, 10, default); // maxRetryCount > 既有 RetryCount(7)
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(pending[0].RetryCount).IsEqualTo(7); // 失败历史保留，不重置
        await Assert.That(pending[0].Error).Contains("requeued by ops-alice");
        await Assert.That(pending[0].Error).DoesNotContain("original failure");
    }

    [Test]
    public async Task Outbox_RequeueDeadAsync_PendingMessage_ReturnsZero()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("requeue.pending");
        store.AddMessage(msg);

        var rows = await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).Count().IsEqualTo(1); // 仍 Pending，未被重置
    }

    [Test]
    public async Task Outbox_RequeueDeadAsync_ProcessedMessage_ReturnsZero()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("requeue.processed");
        store.AddMessage(msg);
        store.MarkProcessed(msg, DateTimeOffset.UtcNow);

        var rows = await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Outbox_RequeueDeadAsync_BlankRetriedBy_ThrowsArgumentException(string retriedBy)
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        var msg = CreateOutboxMessage("requeue.blank");
        store.AddMessage(msg);
        store.MarkDead(msg, "original failure", DateTimeOffset.UtcNow);

        await Assert.That(async () =>
            await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow, retriedBy, default).AsTask())
            .Throws<ArgumentException>();
    }

    private static OutboxMessage CreateOutboxMessage(string type) => new()
    {
        Id = Ulid.New(),
        Type = type,
        Payload = System.Text.Encoding.UTF8.GetBytes("""{"v":1}"""),
        ContentType = "application/json",
        SchemaVersion = 1,
    };
}
