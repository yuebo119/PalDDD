using PalDDD.PalORM.Sqlite;
using PalDDD.Projections;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Projection Checkpoint Store 测试 —— 迁移自 DapperStoreTests.cs 的 Projection 部分（5 测试全迁移）。
/// <para>复合主键表 (projection_name, source_name, position) —— 全程手写 SQL。</para>
/// </summary>
public class PalOrmProjectionCheckpointStoreTests
{
    [Test]
    public async Task ProjectionCheckpoint_TryStartAsync_CreatesProcessingCheckpoint()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var now = DateTimeOffset.UtcNow;

        var cp = await store.TryStartAsync("proj-1", "source-1", "pos-1", now, TimeSpan.FromMinutes(5), default);

        await Assert.That(cp!.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
        await Assert.That(cp.LeaseUntil).IsEqualTo(now + TimeSpan.FromMinutes(5));
        await Assert.That(cp.Revision).IsEqualTo(1L);
    }

    [Test]
    public async Task ProjectionCheckpoint_TryStartAsync_SkipsActiveLease()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartAsync("proj-1", "source-1", "pos-1", now, TimeSpan.FromMinutes(5), default);
        await Assert.That(first).IsNotNull();

        // 仍在 Processing 且租约未过期 → null
        var second = await store.TryStartAsync("proj-1", "source-1", "pos-1", now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task ProjectionCheckpoint_TryStartAsync_ReclaimsExpiredLease()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartAsync("proj-1", "source-1", "pos-1", now, TimeSpan.FromSeconds(1), default);
        await Assert.That(first).IsNotNull();

        var later = now + TimeSpan.FromSeconds(2);
        var reclaimed = await store.TryStartAsync("proj-1", "source-1", "pos-1", later, TimeSpan.FromMinutes(5), default);

        await Assert.That(reclaimed!.Revision).IsEqualTo(2L);
    }

    [Test]
    public async Task ProjectionCheckpoint_MarkCompleted_PreventsReprocessing()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var now = DateTimeOffset.UtcNow;

        var cp = await store.TryStartAsync("proj-1", "source-1", "pos-1", now, TimeSpan.FromMinutes(5), default);
        await store.MarkCompletedAsync(cp!, now.AddSeconds(1), default);

        // 完成后不再处理
        var again = await store.TryStartAsync("proj-1", "source-1", "pos-1", now.AddSeconds(2), TimeSpan.FromMinutes(5), default);
        await Assert.That(again).IsNull();

        var getAgain = await store.GetAsync("proj-1", "source-1", "pos-1", default);
        await Assert.That(getAgain!.Status).IsEqualTo(ProjectionCheckpointStatus.Completed);
    }

    [Test]
    public async Task ProjectionCheckpoint_Reset_RemovesProjectionSourceRows()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var now = DateTimeOffset.UtcNow;

        await store.TryStartAsync("proj-1", "source-1", "pos-1", now, TimeSpan.FromMinutes(5), default);
        await store.TryStartAsync("proj-1", "source-1", "pos-2", now, TimeSpan.FromMinutes(5), default);
        await store.TryStartAsync("proj-1", "source-2", "pos-1", now, TimeSpan.FromMinutes(5), default);

        await store.ResetAsync("proj-1", "source-1", default);

        // source-1 的全部 position 删除
        await Assert.That(await store.GetAsync("proj-1", "source-1", "pos-1", default)).IsNull();
        await Assert.That(await store.GetAsync("proj-1", "source-1", "pos-2", default)).IsNull();
        // source-2 保留
        await Assert.That(await store.GetAsync("proj-1", "source-2", "pos-1", default)).IsNotNull();
    }

    [Test]
    public async Task ProjectionCheckpoint_GetAsync_LeaseTimestampsRoundTripWithZeroOffset()
    {
        // ITM-242 守护（跨方言形态）：updated_at/lease_until 物化读回偏移必须为 0，
        // 且时刻与写入值（now + timeout）一致——任何方言的本地偏移漂移在此现形
        //（探针结论：SQLite 正常写读路径免疫；MySQL DATETIME 路径漂移 -8h，由下方墙钟测试本地复现）。
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromMinutes(5);

        await store.TryStartAsync("proj-1", "source-1", "pos-1", now, timeout, default);
        var gotten = await store.GetAsync("proj-1", "source-1", "pos-1", default);

        await Assert.That(gotten!.UpdatedAt.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.LeaseUntil.Offset).IsEqualTo(TimeSpan.Zero);
        var maxDrift = Math.Max(
            Math.Abs((gotten.UpdatedAt - now).Ticks),
            Math.Abs((gotten.LeaseUntil - (now + timeout)).Ticks));
        await Assert.That(maxDrift).IsLessThan(TimeSpan.FromSeconds(5).Ticks);
    }

    [Test]
    public async Task ProjectionCheckpoint_GetAsync_ReadsUtcWallClockTimestamps_WithoutLocalOffsetDrift()
    {
        // ITM-242 本地复现：模拟 MySQL DATETIME 存储形态——DateTime(Kind=Utc) 参数存无偏移墙钟文本，
        // GetDateTime 返回 Kind=Unspecified + UTC 墙钟。修复前隐式转 DateTimeOffset 套本地偏移
        //（UTC+8 机器实测 -8h 漂移）→ 红；修复后 SpecifyKind(Utc) → offset 0 精确读回 → 绿。
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteProjectionCheckpointStore(session);
        var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        var leaseUntilUtc = DateTime.UtcNow.AddMinutes(5);
        await session.ExecuteAsync(
            $"INSERT INTO projection_checkpoints (projection_name, source_name, position, status, updated_at, lease_until, revision) VALUES ({"proj-1"}, {"source-1"}, {"pos-1"}, {(int)ProjectionCheckpointStatus.Processing}, {updatedAtUtc}, {leaseUntilUtc}, {1})");

        var gotten = await store.GetAsync("proj-1", "source-1", "pos-1", default);

        await Assert.That(gotten!.UpdatedAt.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.UpdatedAt.UtcDateTime).IsEqualTo(updatedAtUtc);
        await Assert.That(gotten.LeaseUntil.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(gotten.LeaseUntil.UtcDateTime).IsEqualTo(leaseUntilUtc);
    }
}
