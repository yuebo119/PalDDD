using PalDDD.PalORM.Stores;
using PalORM;
using PalDDD.Projections;

namespace PalDDD.PalORM.Tests;

/// <summary>Projection Store 跨方言测试 —— 验证复合主键表 + revision 乐观锁。</summary>
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmProjectionMultiDialectTests
{
    [Test]
    public async Task Projection_Sqlite_TryStart_CreatesCheckpoint()
        => await Test_TryStart_CreatesCheckpoint(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Projection_PostgreSql_TryStart_CreatesCheckpoint()
        => await Test_TryStart_CreatesCheckpoint(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Projection_MySql_TryStart_CreatesCheckpoint()
        => await Test_TryStart_CreatesCheckpoint(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_TryStart_CreatesCheckpoint<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using (ts)
        {
            var store = new PalOrmProjectionCheckpointStore<TProvider>(ts.Session);
            var now = DateTimeOffset.UtcNow;
            var cp = await store.TryStartAsync("proj", "src", "pos", now, TimeSpan.FromMinutes(5), default);
            await Assert.That(cp!.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
            await Assert.That(cp.Revision).IsEqualTo(1L);
            // ITM-245：时间戳往返守护网——lease_until 物化读回与写入值（now+5min）绝对差值须在窗口内
            await MultiDialectFixture.AssertTimestampRoundTrip(
                cp.LeaseUntil, now + TimeSpan.FromMinutes(5), nameof(cp.LeaseUntil));
        }
    }

    [Test]
    public async Task Projection_Sqlite_MarkCompleted_PreventsReprocessing()
        => await Test_MarkCompleted_PreventsReprocessing(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Projection_PostgreSql_MarkCompleted_PreventsReprocessing()
        => await Test_MarkCompleted_PreventsReprocessing(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Projection_MySql_MarkCompleted_PreventsReprocessing()
        => await Test_MarkCompleted_PreventsReprocessing(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_MarkCompleted_PreventsReprocessing<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using (ts)
        {
            var store = new PalOrmProjectionCheckpointStore<TProvider>(ts.Session);
            var now = DateTimeOffset.UtcNow;
            var cp = await store.TryStartAsync("proj", "src", "pos", now, TimeSpan.FromMinutes(5), default);
            await store.MarkCompletedAsync(cp!, now.AddSeconds(1), default);

            var again = await store.TryStartAsync("proj", "src", "pos", now.AddSeconds(2), TimeSpan.FromMinutes(5), default);
            await Assert.That(again).IsNull();
        }
    }

    [Test]
    public async Task Projection_Sqlite_Reset_RemovesSource()
        => await Test_Reset_RemovesSource(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Projection_PostgreSql_Reset_RemovesSource()
        => await Test_Reset_RemovesSource(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Projection_MySql_Reset_RemovesSource()
        => await Test_Reset_RemovesSource(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_Reset_RemovesSource<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using (ts)
        {
            var store = new PalOrmProjectionCheckpointStore<TProvider>(ts.Session);
            var now = DateTimeOffset.UtcNow;
            await store.TryStartAsync("proj", "src-1", "pos-1", now, TimeSpan.FromMinutes(5), default);
            await store.TryStartAsync("proj", "src-2", "pos-1", now, TimeSpan.FromMinutes(5), default);

            await store.ResetAsync("proj", "src-1", default);
            await Assert.That(await store.GetAsync("proj", "src-1", "pos-1", default)).IsNull();
            await Assert.That(await store.GetAsync("proj", "src-2", "pos-1", default)).IsNotNull();
        }
    }

    // ── ITM-245：过期 lease 抢占 —— Processing 且租约过期的检查点可被重新 TryStart（revision 递增）。
    // SQLite 逻辑先例见 PalOrmProjectionCheckpointStoreTests.ReclaimsExpiredLease；此处补全跨方言矩阵。──

    [Test]
    public async Task Projection_Sqlite_TryStart_ReclaimsExpiredLease()
        => await Test_TryStart_ReclaimsExpiredLease(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Projection_PostgreSql_TryStart_ReclaimsExpiredLease()
        => await Test_TryStart_ReclaimsExpiredLease(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Projection_MySql_TryStart_ReclaimsExpiredLease()
        => await Test_TryStart_ReclaimsExpiredLease(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_TryStart_ReclaimsExpiredLease<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using (ts)
        {
            var store = new PalOrmProjectionCheckpointStore<TProvider>(ts.Session);
            var now = DateTimeOffset.UtcNow;

            // 1 秒租约 → 以 now+2s 视角已过期 → 第二个 worker 可抢占
            var first = await store.TryStartAsync("proj", "src", "pos", now, TimeSpan.FromSeconds(1), default);
            await Assert.That(first).IsNotNull();

            var later = now + TimeSpan.FromSeconds(2);
            var reclaimed = await store.TryStartAsync("proj", "src", "pos", later, TimeSpan.FromMinutes(5), default);

            await Assert.That(reclaimed!.Revision).IsEqualTo(2L);
        }
    }
}
