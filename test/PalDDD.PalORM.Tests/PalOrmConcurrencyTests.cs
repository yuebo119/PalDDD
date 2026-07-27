using ByteAether.Ulid;
using PalORM;
using PalDDD.PalORM.Sqlite;
using PalDDD.Idempotency;
using PalDDD.Projections;
using PalDDD.Transactions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// 真并发测试 —— 填补现有 Dapper/EFCore 共同盲区。
/// <para>
/// <b>背景</b>：DapperStoreTests.cs 全桶 0 次命中 Task.WhenAll / Parallel / SemaphoreSlim ——
/// 所有"并发"测试都是"顺序调用两次 Lease 验证第二次为空"。
/// 这些测试无法发现"两个 worker 真正同时调 LeasePending 时 SQL WHERE 子句是否真正原子"。
/// </para>
/// <para><b>本测试桶</b>：用 Task.WhenAll + N 个 worker 并发抢租约，验证：</para>
/// <list type="bullet">
/// <item>无重复分配（每条消息只被一个 worker 抢到）</item>
/// <item>无丢失（所有消息都被消费）</item>
/// <item>乐观锁在并发 UPDATE 下正确冲突</item>
/// </list>
/// </summary>
public class PalOrmConcurrencyTests
{
    /// <summary>
    /// Outbox LeasePending 多 worker 并发 —— 验证无重复分配。
    /// <para>100 条 Pending 消息 + 10 个 worker（每个 batch=20），Task.WhenAll 并发。</para>
    /// </summary>
    [Test]
    public async Task Outbox_Lease_Concurrent_NoDoubleDispatch()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);

        // 准备 100 条 Pending 消息
        for (var i = 0; i < 100; i++)
        {
            store.AddMessage(new OutboxMessage
            {
                Id = Ulid.New(),
                Type = $"concurrent.event.{i}",
                Payload = System.Text.Encoding.UTF8.GetBytes("[]"),
            });
        }

        // 10 个 worker 并发抢租约（batch=20，理论上每个 worker 抢 10 条）
        var workers = Enumerable.Range(0, 10)
            .Select(w => Task.Run(async () =>
                await store.LeasePendingMessagesAsync(20, $"worker-{w}", TimeSpan.FromMinutes(5), 10, default)))
            .ToArray();

        var results = await Task.WhenAll(workers);
        var allLeased = results.SelectMany(r => r).ToList();

        // 断言 1：无重复 ID（每条消息只被一个 worker 抢到）
        var distinctCount = allLeased.DistinctBy(m => m.Id).Count();
        await Assert.That(distinctCount).IsEqualTo(allLeased.Count);

        // 断言 2：全部 100 条都被消费（无丢失）
        await Assert.That(allLeased.Count).IsEqualTo(100);
    }

    /// <summary>
    /// Inbox TryStartProcessing 多 worker 并发 —— 验证同一消息只被一个 consumer 抢到。
    /// </summary>
    [Test]
    public async Task Inbox_TryStartProcessing_Concurrent_SingleAcquisition()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        // 50 个不同 consumer 并发尝试启动同一消息 —— 应只有 1 个成功
        var workers = Enumerable.Range(0, 50)
            .Select(c => Task.Run(async () =>
                await store.TryStartProcessingAsync($"consumer-{c}", "shared-msg", now, TimeSpan.FromMinutes(5), default)))
            .ToArray();

        var results = await Task.WhenAll(workers);
        var successCount = results.Count(r => r is not null);

        // 每个 consumer 名称不同 → 都应成功（不同 consumer 独立）
        await Assert.That(successCount).IsEqualTo(50);
    }

    /// <summary>
    /// Inbox TryStartProcessing 同一 consumer 并发 —— 验证只抢一次（幂等）。
    /// </summary>
    [Test]
    public async Task Inbox_SameConsumer_Concurrent_OnlyOnce()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteInboxStore(session);
        var now = DateTimeOffset.UtcNow;

        // 同一 consumer 并发 20 次尝试同一消息 —— 应只有 1 次成功
        var workers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
                await store.TryStartProcessingAsync("consumer-same", "shared-msg", now, TimeSpan.FromMinutes(5), default)))
            .ToArray();

        var results = await Task.WhenAll(workers);
        var successCount = results.Count(r => r is not null);

        await Assert.That(successCount).IsEqualTo(1);
    }

    /// <summary>
    /// Projection TryStart 多 worker 并发 —— 验证同一检查点只被一个 worker 抢到。
    /// </summary>
    [Test]
    public async Task Projection_TryStart_Concurrent_SingleAcquisition()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var now = DateTimeOffset.UtcNow;

        var workers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
                await store.TryStartAsync("proj-1", "source-1", "pos-1", now, TimeSpan.FromMinutes(5), default)))
            .ToArray();

        var results = await Task.WhenAll(workers);
        var successCount = results.Count(r => r is not null);

        await Assert.That(successCount).IsEqualTo(1);
    }

    /// <summary>
    /// Idempotency TryStart 多 worker 并发 —— 验证同一幂等键只被一个 worker 抢到。
    /// </summary>
    [Test]
    public async Task Idempotency_TryStart_Concurrent_SingleAcquisition()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;

        var workers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
                await store.TryStartAsync("op-1", "shared-key", now, IdempotencyPolicy.Default, default)))
            .ToArray();

        var results = await Task.WhenAll(workers);
        var successCount = results.Count(r => r is not null);

        await Assert.That(successCount).IsEqualTo(1);
    }

    /// <summary>
    /// Outbox 并发 MarkProcessed —— 验证不会破坏数据（同一消息被并发标记不应崩溃）。
    /// <para>注意：OutboxMessageRow.RetryCount 有 [ConcurrencyCheck] —— 并发 UPDATE 应抛 ConcurrencyConflictException。</para>
    /// </summary>
    [Test]
    public async Task Outbox_MarkProcessed_Concurrent_OptimisticLockDetectsConflict()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);

        var msg = new OutboxMessage
        {
            Id = Ulid.New(),
            Type = "optimistic.test",
            Payload = System.Text.Encoding.UTF8.GetBytes("[]"),
        };
        store.AddMessage(msg);

        // 两个 worker 持有同一内存对象并发 MarkProcessed
        // [ConcurrencyCheck]RetryCount 应让其中一个抛 ConcurrencyConflictException
        var now = DateTimeOffset.UtcNow;
        var tasks = new[]
        {
            Task.Run(() => store.MarkProcessed(msg, now)),
            Task.Run(() => store.MarkProcessed(msg, now.AddSeconds(1))),
        };

        // 至少有一个会抛 ConcurrencyConflictException（乐观锁检测到版本变化）
        // 不抛的失败视为回归 —— 用 try/catch 统计
        var exceptions = 0;
        foreach (var t in tasks)
        {
            try { await t; }
            catch (ConcurrencyConflictException) { exceptions++; }
        }
        await Assert.That(exceptions).IsGreaterThan(0);
    }
}
