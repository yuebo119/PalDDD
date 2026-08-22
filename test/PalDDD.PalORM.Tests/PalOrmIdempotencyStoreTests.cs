using PalDDD.PalORM.Sqlite;
using PalDDD.Idempotency;

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

        await Assert.That(record!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        var gotten = await store.GetAsync("op-1", "key-1", now.AddSeconds(1), default);
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
        // ITM-285（R45）：前置断言——TryStart 失败（记录未写入）时 GetAsync 同样返回 null，
        // 无前置则测试恒绿无法区分"存在但过期"与"不存在"
        var seeded = await store.TryStartAsync("op-1", "key-1", now, shortPolicy, default);
        await Assert.That(seeded).IsNotNull();

        var later = now + TimeSpan.FromSeconds(2);
        var gotten = await store.GetAsync("op-1", "key-1", later, default);
        await Assert.That(gotten).IsNull();
    }

    [Test]
    public async Task GetAsync_LeaseTimestampsRoundTripWithZeroOffset()
    {
        // ITM-242 守护（跨方言形态）：locked_until/expires_at/updated_at 物化读回偏移必须为 0，
        // 且时刻与写入值（now + policy）一致——任何方言的本地偏移漂移在此现形
        //（探针结论：SQLite 正常写读路径免疫；MySQL DATETIME 路径漂移 -8h，由下方墙钟测试本地复现）。
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;
        var policy = new IdempotencyPolicy { ProcessingTimeout = TimeSpan.FromMinutes(5), Retention = TimeSpan.FromHours(1) };

        await store.TryStartAsync("op-1", "key-1", now, policy, default);
        var gotten = await store.GetAsync("op-1", "key-1", now.AddSeconds(1), default);

        await Assert.That(gotten!.LockedUntil.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.ExpiresAt.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.UpdatedAt.Offset).IsEqualTo(TimeSpan.Zero);
        var maxDrift = new[]
        {
            Math.Abs((gotten.LockedUntil - (now + policy.ProcessingTimeout)).Ticks),
            Math.Abs((gotten.ExpiresAt - (now + policy.Retention)).Ticks),
            Math.Abs((gotten.UpdatedAt - now).Ticks),
        }.Max();
        await Assert.That(maxDrift).IsLessThan(TimeSpan.FromSeconds(5).Ticks);
    }

    [Test]
    public async Task GetAsync_ReadsUtcWallClockTimestamps_WithoutLocalOffsetDrift()
    {
        // ITM-242 本地复现：模拟 MySQL DATETIME 存储形态——DateTime(Kind=Utc) 参数存无偏移墙钟文本，
        // GetDateTime 返回 Kind=Unspecified + UTC 墙钟。修复前隐式转 DateTimeOffset 套本地偏移
        //（UTC+8 机器实测 -8h 漂移）→ 红；修复后 SpecifyKind(Utc) → offset 0 精确读回 → 绿。
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        var lockedUntilUtc = DateTime.UtcNow.AddMinutes(5);
        var expiresAtUtc = DateTime.UtcNow.AddHours(1);
        await session.ExecuteAsync(
            $"INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at) VALUES ({"op-1"}, {"key-1"}, {(int)IdempotencyRecordStatus.Processing}, {lockedUntilUtc}, {expiresAtUtc}, {updatedAtUtc})");

        var gotten = await store.GetAsync("op-1", "key-1", DateTimeOffset.UtcNow, default);

        await Assert.That(gotten!.LockedUntil.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.LockedUntil.UtcDateTime).IsEqualTo(lockedUntilUtc);
        await Assert.That(gotten.ExpiresAt.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.ExpiresAt.UtcDateTime).IsEqualTo(expiresAtUtc);
        await Assert.That(gotten.UpdatedAt.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.UpdatedAt.UtcDateTime).IsEqualTo(updatedAtUtc);
    }

    /// <summary>ITM-277（R43）：空响应体的 Completed 记录（落库空 bytea 而非 NULL）读回
    /// ResponsePayload 应为非 null 空序列——修复前 Length>0 守卫使其读回 null，
    /// 幂等命中方以 null 判"无可复用响应"会重放副作用。</summary>
    [Test]
    public async Task GetAsync_EmptyResponsePayload_CompletedRoundTripsAsNonNullEmpty()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteIdempotencyStore(session);
        var now = DateTimeOffset.UtcNow;
        await session.ExecuteAsync(
            $"INSERT INTO idempotency_records (operation_name, idempotency_key, status, locked_until, expires_at, updated_at, response_payload) VALUES ({"op-empty"}, {"key-empty"}, {(int)IdempotencyRecordStatus.Completed}, {now}, {now.AddHours(1)}, {now}, {Array.Empty<byte>()})", default);

        var gotten = await store.GetAsync("op-empty", "key-empty", now.AddMinutes(1), default);

        await Assert.That(gotten!.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        await Assert.That(gotten.ResponsePayload!.Value.Length).IsEqualTo(0);
    }
}
