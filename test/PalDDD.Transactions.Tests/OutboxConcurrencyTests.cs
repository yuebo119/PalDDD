using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

/// <summary>
/// InMemoryOutboxStore 并发竞争测试 — 多线程同时 lease 验证消息不重复投递。
/// 补充 OutboxSqliteConcurrencyTests 的 SQLite 场景，覆盖内存存储的线程安全。
/// </summary>
/// <remarks>
/// 覆盖语义（刻意）：单次运行概率性覆盖并发窗口——竞态是否真实交错取决于调度器，
/// 通过即验证该次交错下无重复分配；确定性并发回归由 fencing 行为测试
/// （InMemoryStoreTests 的 StaleReferenceAfterReLease 系列）与 PalOrmConcurrencyTests 承载。
/// </remarks>
public sealed class OutboxConcurrencyTests
{
    [Test]
    public async Task LeasePending_ParallelWorkers_NoDuplicateAssignment()
    {
        var store = new InMemoryOutboxStore();
        // 种入 10 条消息
        for (var i = 0; i < 10; i++)
        {
            store.AddMessage(new OutboxMessage
            {
                Id = PalUlid.New(),
                Type = "test.event.v1",
                Payload = [],
            });
        }

        var allLeased = new System.Collections.Concurrent.ConcurrentBag<OutboxMessage>();
        var workerCount = 4;

        // 4 个 worker 并行抢消息
        await Parallel.ForAsync(0, workerCount, async (workerId, ct) =>
        {
            var leased = await store.LeasePendingMessagesAsync(5, $"worker-{workerId}", TimeSpan.FromMinutes(5), 3, ct);
            await store.SaveChangesAsync(ct);
            foreach (var msg in leased) allLeased.Add(msg);
        });

        // 验证：10 条消息每条只被一个 worker 拿到（无重复）
        var ids = allLeased.Select(m => m.Id).ToList();
        await Assert.That(ids).Count().IsEqualTo(10);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(10);
    }

    [Test]
    public async Task LeasePending_MoreWorkersThanMessages_OnlyOneWorkerGetsMessage()
    {
        var store = new InMemoryOutboxStore();
        store.AddMessage(new OutboxMessage
        {
            Id = PalUlid.New(),
            Type = "test.single.v1",
            Payload = [],
        });

        var allLeased = new System.Collections.Concurrent.ConcurrentBag<OutboxMessage>();

        // 8 个 worker 抢 1 条消息
        await Parallel.ForAsync(0, 8, async (workerId, ct) =>
        {
            var leased = await store.LeasePendingMessagesAsync(1, $"worker-{workerId}", TimeSpan.FromMinutes(5), 3, ct);
            await store.SaveChangesAsync(ct);
            foreach (var msg in leased) allLeased.Add(msg);
        });

        // 只有 1 个 worker 拿到消息
        await Assert.That(allLeased).Count().IsEqualTo(1);
    }
}
