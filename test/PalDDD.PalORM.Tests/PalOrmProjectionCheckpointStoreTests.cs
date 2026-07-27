using PalDDD.PalORM.Sqlite;
using PalDDD.Projections;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

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

        await Assert.That(cp).IsNotNull();
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

        await Assert.That(reclaimed).IsNotNull();
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
}
