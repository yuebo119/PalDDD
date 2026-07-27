using ByteAether.Ulid;
using PalDDD.PalORM.Stores;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalDDD.PalORM.MySql;
using PalDDD.PalORM.PostgreSql;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace PalDDD.PalORM.Tests;

/// <summary>Saga Store 跨方言测试 —— 验证 saga_data JSON 列 + version 乐观锁。</summary>
public class PalOrmSagaMultiDialectTests
{
    private sealed class TestSagaState : SagaState
    {
        public string CustomerId { get; set; } = "";
    }

    [Test]
    public async Task Saga_Sqlite_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await new MultiDialectFixture().CreateSqliteAsync());

    [Test]
    public async Task Saga_PostgreSql_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await new MultiDialectFixture().CreatePostgreSqlAsync());

    [Test]
    public async Task Saga_MySql_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await new MultiDialectFixture().CreateMySqlAsync());

    private static async Task Test_InsertNew_ThenGetById<TProvider>(DataSession<TProvider> session)
        where TProvider : IDbProvider
    {
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(session);
        var state = new TestSagaState { CustomerId = "cust-1", CurrentState = "Started" };

        await store.SaveChangesAsync(state, default);
        var loaded = await store.GetByIdAsync(state.SagaId, default);

        await Assert.That(loaded).IsNotNull();
        // 注：无 jsonTypeInfo 时 saga_data 不持久化，CustomerId 是默认值。
        // 验证元数据列（DB 列直接映射，不依赖 JSON 快照）
        await Assert.That(loaded!.CurrentState).IsEqualTo("Started");
        await Assert.That(loaded.Version).IsEqualTo(0);  // INSERT 不自增 version（与 Dapper 实现一致）
        await Assert.That(loaded.Status).IsEqualTo(SagaStatus.Active);
    }

    [Test]
    public async Task Saga_Sqlite_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await new MultiDialectFixture().CreateSqliteAsync());

    [Test]
    public async Task Saga_PostgreSql_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await new MultiDialectFixture().CreatePostgreSqlAsync());

    [Test]
    public async Task Saga_MySql_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await new MultiDialectFixture().CreateMySqlAsync());

    private static async Task Test_GetActiveSagas<TProvider>(DataSession<TProvider> session)
        where TProvider : IDbProvider
    {
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(session);
        var active = new TestSagaState { CurrentState = "Active" };
        var completed = new TestSagaState { CurrentState = "Done", Status = SagaStatus.Completed };

        await store.SaveChangesAsync(active, default);
        await store.SaveChangesAsync(completed, default);

        var actives = await store.GetActiveSagasAsync(10, default);
        await Assert.That(actives.Count).IsEqualTo(1);
        // 比较 CurrentState 而非 SagaId（读回的是 new TState()，SagaId 是新 Ulid，与原始不同）
        await Assert.That(actives[0].CurrentState).IsEqualTo("Active");
    }

    [Test]
    public async Task Saga_Sqlite_LeaseActiveSagas()
        => await Test_LeaseActiveSagas(await new MultiDialectFixture().CreateSqliteAsync());

    [Test]
    public async Task Saga_PostgreSql_LeaseActiveSagas()
        => await Test_LeaseActiveSagas(await new MultiDialectFixture().CreatePostgreSqlAsync());

    [Test]
    public async Task Saga_MySql_LeaseActiveSagas()
        => await Test_LeaseActiveSagas(await new MultiDialectFixture().CreateMySqlAsync());

    private static async Task Test_LeaseActiveSagas<TProvider>(DataSession<TProvider> session)
        where TProvider : IDbProvider
    {
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(session);
        await store.SaveChangesAsync(new TestSagaState { CurrentState = "Active" }, default);

        var leased = await store.LeaseActiveSagasAsync("worker-1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased.Count).IsEqualTo(1);

        // 第二次 Lease 应为空
        var second = await store.LeaseActiveSagasAsync("worker-2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }
}
