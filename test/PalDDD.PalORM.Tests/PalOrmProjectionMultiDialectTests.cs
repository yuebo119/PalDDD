using PalDDD.PalORM.Stores;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalDDD.PalORM.MySql;
using PalDDD.PalORM.PostgreSql;
using PalDDD.PalORM.Sqlite;
using PalDDD.Projections;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace PalDDD.PalORM.Tests;

/// <summary>Projection Store 跨方言测试 —— 验证复合主键表 + revision 乐观锁。</summary>
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
        var store = new PalOrmProjectionCheckpointStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;
        var cp = await store.TryStartAsync("proj", "src", "pos", now, TimeSpan.FromMinutes(5), default);
        await Assert.That(cp).IsNotNull();
        await Assert.That(cp!.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);
        await Assert.That(cp.Revision).IsEqualTo(1L);
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
        var store = new PalOrmProjectionCheckpointStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;
        var cp = await store.TryStartAsync("proj", "src", "pos", now, TimeSpan.FromMinutes(5), default);
        await store.MarkCompletedAsync(cp!, now.AddSeconds(1), default);

        var again = await store.TryStartAsync("proj", "src", "pos", now.AddSeconds(2), TimeSpan.FromMinutes(5), default);
        await Assert.That(again).IsNull();
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
        var store = new PalOrmProjectionCheckpointStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;
        await store.TryStartAsync("proj", "src-1", "pos-1", now, TimeSpan.FromMinutes(5), default);
        await store.TryStartAsync("proj", "src-2", "pos-1", now, TimeSpan.FromMinutes(5), default);

        await store.ResetAsync("proj", "src-1", default);
        await Assert.That(await store.GetAsync("proj", "src-1", "pos-1", default)).IsNull();
        await Assert.That(await store.GetAsync("proj", "src-2", "pos-1", default)).IsNotNull();
    }
}
