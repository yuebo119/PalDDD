using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

/// <summary>
/// 批次 C（2026-11-04）读侧 API 的单元矩阵——InMemory 栈（Outbox + Inbox）。<br/>
/// 覆盖：状态隔离 / maxCount 边界 / 主键排序 / 空表 / 未定义状态值可见失败。
/// 持久化三栈（EF / Dapper / PalORM）的同语义矩阵在各自测试工程内。
/// </summary>
public sealed class OutboxInboxStatusQueryTests
{
    // ── Outbox ──

    [Test]
    public async Task Outbox_GetByStatusAsync_EachStatusReturnsOnlyItsOwn()
    {
        var store = new InMemoryOutboxStore();
        var pending = Seed(store, OutboxStatus.Pending);
        var processed = Seed(store, OutboxStatus.Processed);
        var dead = Seed(store, OutboxStatus.Dead);

        await Assert.That(await IdsAsync(store, OutboxStatus.Pending)).IsEquivalentTo([pending.Id]);
        await Assert.That(await IdsAsync(store, OutboxStatus.Processed)).IsEquivalentTo([processed.Id]);
        await Assert.That(await IdsAsync(store, OutboxStatus.Dead)).IsEquivalentTo([dead.Id]);
    }

    [Test]
    public async Task Outbox_GetByStatusAsync_AppliesMaxCountAndOrdersById()
    {
        var store = new InMemoryOutboxStore();
        for (var i = 0; i < 4; i++)
            Seed(store, OutboxStatus.Pending);

        var all = await store.GetByStatusAsync(OutboxStatus.Pending, 100, CancellationToken.None);
        await Assert.That(all.Count).IsEqualTo(4);
        var ids = all.Select(static m => m.Id).ToList();
        await Assert.That(ids.SequenceEqual(ids.OrderBy(static id => id))).IsTrue();

        var capped = await store.GetByStatusAsync(OutboxStatus.Pending, 2, CancellationToken.None);
        await Assert.That(capped.Count).IsEqualTo(2);
        // maxCount 取的是 id 最小的前 N 条（排序后再截断）
        await Assert.That(capped[0].Id).IsEqualTo(ids[0]);
        await Assert.That(capped[1].Id).IsEqualTo(ids[1]);
    }

    [Test]
    public async Task Outbox_GetByStatusAsync_EmptyTableAndMissReturnEmpty()
    {
        var store = new InMemoryOutboxStore();

        await Assert.That(await store.GetByStatusAsync(OutboxStatus.Pending, 10, CancellationToken.None)).IsEmpty();
        await Assert.That(await store.GetByStatusAsync(OutboxStatus.Dead, 10, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task Outbox_GetByStatusAsync_NonPositiveMaxCount_Throws()
    {
        var store = new InMemoryOutboxStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetByStatusAsync(OutboxStatus.Pending, 0, CancellationToken.None).AsTask());
    }

    [Test]
    public async Task Outbox_GetByStatusAsync_UndefinedStatus_ThrowsActively()
    {
        var store = new InMemoryOutboxStore();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetByStatusAsync((OutboxStatus)99, 10, CancellationToken.None).AsTask());
        await Assert.That(ex!.Message.Contains("OutboxStatus", StringComparison.Ordinal)).IsTrue();
    }

    // ── Inbox ──

    [Test]
    public async Task Inbox_GetByStatusAsync_EachStatusReturnsOnlyItsOwn()
    {
        var store = new InMemoryInboxStore();
        var processing = SeedInbox(store, InboxStatus.Processing);
        var processed = SeedInbox(store, InboxStatus.Processed);
        var failed = SeedInbox(store, InboxStatus.Failed);

        await Assert.That(await InboxIdsAsync(store, InboxStatus.Processing)).IsEquivalentTo([processing]);
        await Assert.That(await InboxIdsAsync(store, InboxStatus.Processed)).IsEquivalentTo([processed]);
        await Assert.That(await InboxIdsAsync(store, InboxStatus.Failed)).IsEquivalentTo([failed]);

        // Pending 行在 InMemory 栈不可构造：TryStartProcessingAsync 创建即 Processing
        // （认领与状态迁移同帧原子）。返回空是实况，不是缺陷。
        await Assert.That(await InboxIdsAsync(store, InboxStatus.Pending)).IsEmpty();
    }

    [Test]
    public async Task Inbox_GetByStatusAsync_AppliesMaxCountAndOrdersById()
    {
        var store = new InMemoryInboxStore();
        for (var i = 0; i < 4; i++)
            SeedInbox(store, InboxStatus.Failed);

        var all = await store.GetByStatusAsync(InboxStatus.Failed, 100, CancellationToken.None);
        await Assert.That(all.Count).IsEqualTo(4);
        var ids = all.Select(static m => m.Id).ToList();
        await Assert.That(ids.SequenceEqual(ids.OrderBy(static id => id))).IsTrue();

        var capped = await store.GetByStatusAsync(InboxStatus.Failed, 2, CancellationToken.None);
        await Assert.That(capped.Count).IsEqualTo(2);
        await Assert.That(capped[0].Id).IsEqualTo(ids[0]);
        await Assert.That(capped[1].Id).IsEqualTo(ids[1]);
    }

    [Test]
    public async Task Inbox_GetByStatusAsync_EmptyTableAndMissReturnEmpty()
    {
        var store = new InMemoryInboxStore();

        await Assert.That(await store.GetByStatusAsync(InboxStatus.Pending, 10, CancellationToken.None)).IsEmpty();
        await Assert.That(await store.GetByStatusAsync(InboxStatus.Failed, 10, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task Inbox_GetByStatusAsync_UndefinedStatus_ThrowsActively()
    {
        var store = new InMemoryInboxStore();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetByStatusAsync((InboxStatus)99, 10, CancellationToken.None).AsTask());
        await Assert.That(ex!.Message.Contains("InboxStatus", StringComparison.Ordinal)).IsTrue();
    }

    // ── 工厂 ──

    private static OutboxMessage Seed(InMemoryOutboxStore store, OutboxStatus status)
    {
        var message = new OutboxMessage
        {
            Id = PalUlid.New(),
            Type = "test.event.v1",
            Payload = [1, 2, 3],
            ContentType = "application/json",
            SchemaVersion = 1,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        store.AddMessageAsync(message, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return message;
    }

    private static long SeedInbox(InMemoryInboxStore store, InboxStatus status)
    {
        var messageId = $"message-{PalUlid.New()}";
        var now = DateTimeOffset.UtcNow;
        var claimed = store.TryStartProcessingAsync("test-consumer", messageId, now,
                TimeSpan.FromMinutes(5), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("内存栈认领应返回新建行。");

        switch (status)
        {
            case InboxStatus.Processing:
                return claimed.Id;
            case InboxStatus.Processed:
                store.MarkProcessedAsync(claimed, now.AddSeconds(1), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                return claimed.Id;
            case InboxStatus.Failed:
                store.MarkFailedAsync(claimed, "seeded failure", CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                return claimed.Id;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(status), status,
                    "InMemory 栈的 Pending 行不可构造（TryStartProcessingAsync 创建即 Processing）。");
        }
    }

    private static async Task<List<PalUlid>> IdsAsync(InMemoryOutboxStore store, OutboxStatus status)
    {
        var rows = await store.GetByStatusAsync(status, 100, CancellationToken.None);
        return rows.Select(static m => m.Id).ToList();
    }

    private static async Task<List<long>> InboxIdsAsync(InMemoryInboxStore store, InboxStatus status)
    {
        var rows = await store.GetByStatusAsync(status, 100, CancellationToken.None);
        return rows.Select(static m => m.Id).ToList();
    }
}
