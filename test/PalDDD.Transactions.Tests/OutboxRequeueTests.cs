namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔄 Outbox 死信重投递测试（IPalOutboxStore.RequeueDeadAsync）
// ═══════════════════════════════════════════════════════════════
// 覆盖 ADR-011 语义约束：
//   1. Dead → Pending 闭环成功
//   2. 非 Dead 状态（Processed/Pending）拒绝重投，返回 0
//   3. RetryCount 保留失败历史不重置
//   4. Error 列写入操作审计串
//   5. retriedBy 空白抛 ArgumentException
// ═══════════════════════════════════════════════════════════════

public class OutboxRequeueTests
{
    private static OutboxMessage MakeDeadMessage(int retryCount = 10)
        => new()
        {
            Id = Guid.NewGuid(),
            Type = "test.event",
            Payload = [],
            Status = OutboxStatus.Dead,
            RetryCount = retryCount,
            Error = "original failure"
        };

    [Test]
    public async Task RequeueDeadAsync_DeadMessage_ReturnsOneAndFlipsToPending()
    {
        var store = new InMemoryOutboxStore();
        var msg = MakeDeadMessage();
        store.AddMessage(msg);

        // nextAttempt 设为过去时间，确保 GetPendingMessagesAsync 能取到（条件：next_attempt_at <= now）
        var nextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var rows = await store.RequeueDeadAsync(msg.Id, nextAttempt, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(1);
        var pending = await store.GetPendingMessagesAsync(10, 100, default);
        await Assert.That(pending).Count().IsEqualTo(1);
        var requeued = pending[0];
        await Assert.That(requeued.Id).IsEqualTo(msg.Id);
        await Assert.That(requeued.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(requeued.NextAttemptAt).IsEqualTo(nextAttempt);
        await Assert.That(requeued.LockedBy).IsNull();
        await Assert.That(requeued.LockedUntil).IsNull();
    }

    [Test]
    public async Task RequeueDeadAsync_PendingMessage_ReturnsZero_NoStateChange()
    {
        var store = new InMemoryOutboxStore();
        var msg = MakeDeadMessage();
        msg.Status = OutboxStatus.Pending;
        store.AddMessage(msg);

        var rows = await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
        var pending = await store.GetPendingMessagesAsync(10, 100, default);
        await Assert.That(pending).Count().IsEqualTo(1); // 仍是 Pending，未被重置
    }

    [Test]
    public async Task RequeueDeadAsync_ProcessedMessage_ReturnsZero()
    {
        var store = new InMemoryOutboxStore();
        var msg = MakeDeadMessage();
        msg.Status = OutboxStatus.Processed;
        store.AddMessage(msg);

        var rows = await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
        var pending = await store.GetPendingMessagesAsync(10, 100, default);
        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task RequeueDeadAsync_NonExistentId_ReturnsZero()
    {
        var store = new InMemoryOutboxStore();

        var rows = await store.RequeueDeadAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
    }

    [Test]
    public async Task RequeueDeadAsync_PreservesRetryCount()
    {
        var store = new InMemoryOutboxStore();
        var msg = MakeDeadMessage(retryCount: 7);
        store.AddMessage(msg);

        await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow.AddSeconds(-1), "ops-alice", default);

        var pending = await store.GetPendingMessagesAsync(10, 100, default);
        await Assert.That(pending).Count().IsEqualTo(1);
        var requeued = pending[0];
        await Assert.That(requeued.RetryCount).IsEqualTo(7); // 失败历史保留，不重置
    }

    [Test]
    public async Task RequeueDeadAsync_WritesAuditStringToError()
    {
        var store = new InMemoryOutboxStore();
        var msg = MakeDeadMessage();
        store.AddMessage(msg);

        await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow.AddSeconds(-1), "ops-bob", default);

        var pending = await store.GetPendingMessagesAsync(10, 100, default);
        await Assert.That(pending).Count().IsEqualTo(1);
        var requeued = pending[0];
        await Assert.That(requeued.Error).Contains("requeued by ops-bob");
        await Assert.That(requeued.Error).DoesNotContain("original failure");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task RequeueDeadAsync_BlankRetriedBy_ThrowsArgumentException(string retriedBy)
    {
        var store = new InMemoryOutboxStore();
        var msg = MakeDeadMessage();
        store.AddMessage(msg);

        await Assert.That(async () =>
            await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow, retriedBy, default).AsTask()).Throws<ArgumentException>();
    }

    [Test]
    public async Task RequeueDeadAsync_NullRetriedBy_ThrowsArgumentNullException()
    {
        var store = new InMemoryOutboxStore();
        var msg = MakeDeadMessage();
        store.AddMessage(msg);

        await Assert.That(async () =>
            await store.RequeueDeadAsync(msg.Id, DateTimeOffset.UtcNow, null!, default).AsTask()).Throws<ArgumentNullException>();
    }
}
