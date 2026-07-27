using PalDDD.PalORM.Sqlite;
using PalDDD.Idempotency;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Idempotency Store 测试 —— 迁移自 IdempotencyEfCoreTests.cs（6 测试，从 InMemory 改 SQLite）。
/// <para>复合主键表 (operation_name, key) —— 全程手写 SQL。</para>
/// </summary>
public class PalOrmIdempotencyStoreTests
{
    [Test]
    public async Task TryStartAsync_PersistsProcessingRecordAndGetAsyncReturnsIt()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;

        var record = await store.TryStartAsync("op-1", "key-1", now, IdempotencyPolicy.Default, default);

        await Assert.That(record).IsNotNull();
        await Assert.That(record!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        var gotten = await store.GetAsync("op-1", "key-1", now.AddSeconds(1), default);
        await Assert.That(gotten).IsNotNull();
        await Assert.That(gotten!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
    }

    [Test]
    public async Task TryStartAsync_ReturnsNullWhenProcessingLeaseIsStillActive()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartAsync("op-1", "key-1", now, IdempotencyPolicy.Default, default);
        await Assert.That(first).IsNotNull();

        var second = await store.TryStartAsync("op-1", "key-1", now.AddSeconds(1), IdempotencyPolicy.Default, default);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task TryStartAsync_ReusesExpiredProcessingLease()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;

        var shortPolicy = new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromSeconds(5), Retention = TimeSpan.FromHours(1) };
        var first = await store.TryStartAsync("op-1", "key-1", now, shortPolicy, default);
        await Assert.That(first).IsNotNull();

        var later = now + TimeSpan.FromSeconds(6);
        var reclaimed = await store.TryStartAsync("op-1", "key-1", later, IdempotencyPolicy.Default, default);
        await Assert.That(reclaimed).IsNotNull();
    }

    [Test]
    public async Task MarkCompletedAsync_PersistsReplayPayload()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;

        var record = await store.TryStartAsync("op-1", "key-1", now, IdempotencyPolicy.Default, default);
        var payload = System.Text.Encoding.UTF8.GetBytes("""{"result":42}""");
        await store.MarkCompletedAsync(record!, payload, now.AddSeconds(1), default);

        var gotten = await store.GetAsync("op-1", "key-1", now.AddSeconds(2), default);
        await Assert.That(gotten).IsNotNull();
        await Assert.That(gotten!.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        // ResponsePayload 是 ReadOnlyMemory<byte>? —— 验证非空且长度匹配
        await Assert.That(gotten.ResponsePayload.HasValue).IsTrue();
        await Assert.That(gotten.ResponsePayload!.Value.Length).IsEqualTo(payload.Length);
    }

    [Test]
    public async Task MarkFailedAsync_DoesNotOverwriteRecordCompletedByAnotherProcessor()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;

        // processor-1 用短租约启动（5 秒）
        var shortPolicy = new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromSeconds(5), Retention = TimeSpan.FromHours(1) };
        var r1 = await store.TryStartAsync("op-1", "key-1", now, shortPolicy, default);
        await Assert.That(r1).IsNotNull();

        // 6 秒后租约过期 → processor-2 抢占并完成
        var later = now + TimeSpan.FromSeconds(6);
        var r2 = await store.TryStartAsync("op-1", "key-1", later, shortPolicy, default);
        await Assert.That(r2).IsNotNull();
        var completedPayload = System.Text.Encoding.UTF8.GetBytes("[]");
        await store.MarkCompletedAsync(r2!, completedPayload, later.AddSeconds(1), default);

        // processor-1（持有过期 r1）试图 MarkFailed —— 不应覆盖 Completed
        await store.MarkFailedAsync(r1!, "stale failure", later.AddSeconds(2), default);

        var final = await store.GetAsync("op-1", "key-1", later.AddSeconds(3), default);
        await Assert.That(final!.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
    }

    [Test]
    public async Task GetAsync_DoesNotMutateStoreWhenRecordIsExpired()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;

        var shortPolicy = new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromMinutes(5), Retention = TimeSpan.FromSeconds(1) };
        await store.TryStartAsync("op-1", "key-1", now, shortPolicy, default);

        var later = now + TimeSpan.FromSeconds(2);
        var gotten = await store.GetAsync("op-1", "key-1", later, default);
        await Assert.That(gotten).IsNull();
    }
}
