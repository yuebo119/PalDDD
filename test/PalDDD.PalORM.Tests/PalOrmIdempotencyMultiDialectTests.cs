using PalDDD.PalORM.Stores;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalDDD.PalORM.MySql;
using PalDDD.PalORM.PostgreSql;
using PalDDD.PalORM.Sqlite;
using PalDDD.Idempotency;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace PalDDD.PalORM.Tests;

/// <summary>Idempotency Store 跨方言测试 —— 验证复合主键表 + ResponsePayload 回放。</summary>
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmIdempotencyMultiDialectTests
{
    [Test]
    public async Task Idempotency_Sqlite_TryStart_ThenMarkCompleted()
        => await Test_TryStart_ThenMarkCompleted(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Idempotency_PostgreSql_TryStart_ThenMarkCompleted()
        => await Test_TryStart_ThenMarkCompleted(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Idempotency_MySql_TryStart_ThenMarkCompleted()
        => await Test_TryStart_ThenMarkCompleted(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_TryStart_ThenMarkCompleted<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var store = new PalOrmIdempotencyStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;
        var record = await store.TryStartAsync("op", "key", now, IdempotencyPolicy.Default, default);
        await Assert.That(record).IsNotNull();

        var payload = System.Text.Encoding.UTF8.GetBytes("""{"r":42}""");
        await store.MarkCompletedAsync(record!, payload, now.AddSeconds(1), default);

        var gotten = await store.GetAsync("op", "key", now.AddSeconds(2), default);
        await Assert.That(gotten).IsNotNull();
        await Assert.That(gotten!.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        await Assert.That(gotten.ResponsePayload.HasValue).IsTrue();
    }

    [Test]
    public async Task Idempotency_Sqlite_Duplicate_ReturnsCompleted()
        => await Test_Duplicate_ReturnsCompleted(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Idempotency_PostgreSql_Duplicate_ReturnsCompleted()
        => await Test_Duplicate_ReturnsCompleted(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Idempotency_MySql_Duplicate_ReturnsCompleted()
        => await Test_Duplicate_ReturnsCompleted(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_Duplicate_ReturnsCompleted<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var store = new PalOrmIdempotencyStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;
        var r1 = await store.TryStartAsync("op", "key", now, IdempotencyPolicy.Default, default);
        await store.MarkCompletedAsync(r1!, System.Text.Encoding.UTF8.GetBytes("ok"), now.AddSeconds(1), default);

        // ITM-078 契约对齐：二次 TryStart 返回 null（Completed 记录不可再启动）——
        // 幂等回放走 GetAsync（与 EFCore/InMemory 实现一致；处理器路径先 GetAsync 短路，
        // 从未依赖 TryStartAsync 返回 Completed 记录）。
        var r2 = await store.TryStartAsync("op", "key", now.AddSeconds(2), IdempotencyPolicy.Default, default);
        await Assert.That(r2).IsNull();

        // 回放路径：GetAsync 返回 Completed 记录 + 缓存响应（幂等回放语义不变）
        var replayed = await store.GetAsync("op", "key", now.AddSeconds(2), default);
        await Assert.That(replayed).IsNotNull();
        await Assert.That(replayed!.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        await Assert.That(replayed.ResponsePayload.HasValue).IsTrue();
    }

    [Test]
    public async Task Idempotency_Sqlite_LeaseActive_ReturnsNull()
        => await Test_LeaseActive_ReturnsNull(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Idempotency_PostgreSql_LeaseActive_ReturnsNull()
        => await Test_LeaseActive_ReturnsNull(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Idempotency_MySql_LeaseActive_ReturnsNull()
        => await Test_LeaseActive_ReturnsNull(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_LeaseActive_ReturnsNull<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var store = new PalOrmIdempotencyStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;
        await store.TryStartAsync("op", "key", now, IdempotencyPolicy.Default, default);

        // 租约未过期 → 二次 TryStart 返回 null
        var r2 = await store.TryStartAsync("op", "key", now.AddSeconds(1), IdempotencyPolicy.Default, default);
        await Assert.That(r2).IsNull();
    }
}
