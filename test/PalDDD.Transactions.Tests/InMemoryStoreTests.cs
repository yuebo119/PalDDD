namespace PalDDD.Transactions.Tests;

public sealed class InMemoryStoreTests
{
    [Test]
    public async Task InMemoryInboxStore_FirstAttempt_ReturnsRecordWithProcessingStatus(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var record = await store.TryStartProcessingAsync(
            "consumer", "msg-001", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(record).IsNotNull();
        await Assert.That(record!.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(record.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task InMemoryInboxStore_DuplicateProcessing_ReturnsNull(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var now = DateTimeOffset.UtcNow;
        await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        var dup = await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(dup).IsNull();
    }

    [Test]
    public async Task InMemoryInboxStore_AfterProcessed_DedupReturnsNull(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var now = DateTimeOffset.UtcNow;
        var record = await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await store.MarkProcessedAsync(record!, now.AddSeconds(1), cancellationToken);
        var dup = await store.TryStartProcessingAsync("consumer", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(dup).IsNull();
    }

    [Test]
    public async Task InMemoryInboxStore_DifferentConsumers_Independent(CancellationToken cancellationToken)
    {
        var store = new InMemoryInboxStore();
        var now = DateTimeOffset.UtcNow;
        var a = await store.TryStartProcessingAsync("consumer-a", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        var b = await store.TryStartProcessingAsync("consumer-b", "msg-001", now, TimeSpan.FromMinutes(5), cancellationToken);
        await Assert.That(a).IsNotNull();
        await Assert.That(b).IsNotNull();
    }

    [Test]
    public async Task InMemoryOutboxStore_LeaseAndProcess(CancellationToken cancellationToken)
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1, 2, 3], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), new OutboxOptions().MaxRetryCount, cancellationToken);
        await Assert.That(leased).Count().IsEqualTo(1);
        store.MarkProcessed(msg, DateTimeOffset.UtcNow);
        await Assert.That(msg.Status).IsEqualTo(OutboxStatus.Processed);
    }

    [Test]
    public async Task InMemoryOutboxStore_ReleaseForRetry_IncrementsRetryCount()
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.ReleaseForRetry(msg, "test failure", DateTimeOffset.UtcNow.AddSeconds(30));
        await Assert.That(msg.RetryCount).IsEqualTo(1);
        await Assert.That(msg.Status).IsEqualTo(OutboxStatus.Pending);
    }

    [Test]
    public async Task InMemoryOutboxStore_GetPending_UsesConfiguredMaxRetryCount(CancellationToken cancellationToken)
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);
        store.ReleaseForRetry(msg, "test failure", DateTimeOffset.UtcNow.AddSeconds(-1));

        var pending = await store.GetPendingMessagesAsync(10, 1, cancellationToken);

        await Assert.That(pending).IsEmpty();
    }

    [Test]
    public async Task InMemoryOutboxStore_MarkDead_SetsDeadStatus()
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.MarkDead(msg, "max retries", DateTimeOffset.UtcNow);
        await Assert.That(msg.Status).IsEqualTo(OutboxStatus.Dead);
        await Assert.That(msg.Error).IsEqualTo("max retries");
    }

    [Test]
    public async Task InMemoryOutboxStore_WithInjectedTimeProvider_LeaseExpiryIsDeterministic(CancellationToken cancellationToken)
    {
        // 注入 FakeTimeProvider 验证租约过期时序可控——与 OutboxBatchProcessor 的时间抽象对齐
        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new InMemoryOutboxStore(fakeTime);
        var msg = new OutboxMessage { Type = "test", Payload = [1], ContentType = "application/json", SchemaVersion = 1 };
        store.AddMessage(msg);

        // t=0：租约 2 分钟，锁定到 t=2min
        var leased = await store.LeasePendingMessagesAsync(10, "owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        await Assert.That(leased).Count().IsEqualTo(1);

        // t=1min：租约未过期，无法重新获取
        fakeTime.AdvanceTo(DateTimeOffset.Parse("2026-06-25T00:01:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var beforeExpiry = await store.GetPendingMessagesAsync(10, 10, cancellationToken);
        await Assert.That(beforeExpiry).IsEmpty();

        // t=3min：租约已过期，可重新获取
        fakeTime.AdvanceTo(DateTimeOffset.Parse("2026-06-25T00:03:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var afterExpiry = await store.GetPendingMessagesAsync(10, 10, cancellationToken);
        await Assert.That(afterExpiry).Count().IsEqualTo(1);
    }

    /// <summary>轻量 fake TimeProvider — 测试中控制时间推进，验证租约过期时序</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void AdvanceTo(DateTimeOffset now) => _now = now;
    }

    [Test]
    public async Task InMemorySagaStateStore_GetActiveSagas_ReturnsOnlyActiveStates(CancellationToken cancellationToken)
    {
        var store = new InMemorySagaStateStore<SampleSaga>();
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Started", Status = SagaStatus.Active });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Done", Status = SagaStatus.Completed });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Compensated", Status = SagaStatus.Compensated });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "CompensationFailed", Status = SagaStatus.CompensationFailed });
        store.Add(new SampleSaga { SagaId = Guid.NewGuid(), CurrentState = "Dead", Status = SagaStatus.DeadLettered });

        var active = await store.GetActiveSagasAsync(10, cancellationToken);
        await Assert.That(active).Count().IsEqualTo(1); // 只返回 Active，终态和人工介入态均过滤
    }

    [Test]
    public async Task InMemorySagaStateStore_GetById_ReturnsCorrectState(CancellationToken cancellationToken)
    {
        var store = new InMemorySagaStateStore<SampleSaga>();
        var id = Guid.NewGuid();
        store.Add(new SampleSaga { SagaId = id, CurrentState = "Started" });

        var found = await store.GetByIdAsync(id, cancellationToken);
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.CurrentState).IsEqualTo("Started");
    }

    [Test]
    public async Task InMemorySagaStateStore_LeaseActiveSagas_OnlyOneOwnerGetsActiveState(CancellationToken cancellationToken)
    {
        var store = new InMemorySagaStateStore<SampleSaga>();
        var id = Guid.NewGuid();
        store.Add(new SampleSaga { SagaId = id, CurrentState = "Started", Status = SagaStatus.Active });

        var first = await store.LeaseActiveSagasAsync("owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var second = await store.LeaseActiveSagasAsync("owner-2", TimeSpan.FromMinutes(2), 10, cancellationToken);

        await Assert.That(first).Count().IsEqualTo(1);
        var leased = first[0];
        await Assert.That(leased.SagaId).IsEqualTo(id);
        await Assert.That(leased.LeasedBy).IsEqualTo("owner-1");
        await Assert.That(leased.LeasedUntil).IsNotNull();
        await Assert.That(second).IsEmpty();
    }

    public sealed class SampleSaga : SagaState;
}
