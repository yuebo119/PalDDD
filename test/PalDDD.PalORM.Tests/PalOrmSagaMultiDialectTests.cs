using ByteAether.Ulid;
using PalDDD.PalORM.Stores;
using System.Text.Json.Serialization;
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
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmSagaMultiDialectTests
{
    public sealed class TestSagaState : SagaState
    {
        public string CustomerId { get; set; } = "";
    }

    [Test]
    public async Task Saga_Sqlite_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Saga_PostgreSql_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Saga_MySql_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_InsertNew_ThenGetById<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(ts.Session);
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
        => await Test_GetActiveSagas(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Saga_PostgreSql_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Saga_MySql_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_GetActiveSagas<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(ts.Session);
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
        => await Test_LeaseActiveSagas(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Saga_PostgreSql_LeaseActiveSagas()
        => await Test_LeaseActiveSagas(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Saga_MySql_LeaseActiveSagas()
        => await Test_LeaseActiveSagas(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_LeaseActiveSagas<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(ts.Session);
        await store.SaveChangesAsync(new TestSagaState { CurrentState = "Active" }, default);

        var leased = await store.LeaseActiveSagasAsync("worker-1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased.Count).IsEqualTo(1);

        // 第二次 Lease 应为空
        var second = await store.LeaseActiveSagasAsync("worker-2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }

    // ── ITM-014: jsonTypeInfo 非 null 路径 — Saga 业务字段 JSON 往返 ──

    [Test]
    public async Task Saga_Sqlite_WithJsonTypeInfo_PreservesBusinessFields()
        => await Test_WithJsonTypeInfo_PreservesBusinessFields(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Saga_PostgreSql_WithJsonTypeInfo_PreservesBusinessFields()
    {
        // ITM-127 回归：必须走 PG 方言派生类（RequiresJsonbCast=true）——
        // 泛型基类在 PG 上无 CAST 会抛 42804，本测试锁死方言固化类的 jsonb 快照往返
        var ts = await MultiDialectFixture.CreatePostgreSqlAsync();
        var store = new PostgreSqlSagaStateStore<TestSagaState>(
            ts.Session, SagaTestJsonContext.Default.TestSagaState);
        var state = new TestSagaState { CustomerId = "cust-json-pg", CurrentState = "Started" };

        await store.SaveChangesAsync(state, default);
        var loaded = await store.GetByIdAsync(state.SagaId, default);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomerId).IsEqualTo("cust-json-pg");
        await Assert.That(loaded.SagaId).IsEqualTo(state.SagaId);
        await Assert.That(loaded.CurrentState).IsEqualTo("Started");
    }

    private static async Task Test_WithJsonTypeInfo_PreservesBusinessFields<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        // 注入 jsonTypeInfo，使 saga_data JSON 列持久化完整状态快照（ITM-014 覆盖）
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(ts.Session, SagaTestJsonContext.Default.TestSagaState);
        var state = new TestSagaState { CustomerId = "cust-json-42", CurrentState = "Started" };

        await store.SaveChangesAsync(state, default);
        var loaded = await store.GetByIdAsync(state.SagaId, default);

        await Assert.That(loaded).IsNotNull();
        // jsonTypeInfo 非 null 路径：CustomerId 从 saga_data JSON 反序列化恢复
        await Assert.That(loaded!.CustomerId).IsEqualTo("cust-json-42");
        await Assert.That(loaded.SagaId).IsEqualTo(state.SagaId);
        await Assert.That(loaded.CurrentState).IsEqualTo("Started");
    }

    // ── ITM-015: 乐观锁冲突路径 — version 不匹配 UPDATE 0 行 ──

    [Test]
    public async Task Saga_Sqlite_SaveChanges_WithStaleVersion_ReturnsZero()
        => await Test_SaveChanges_WithStaleVersion_ReturnsZero(await MultiDialectFixture.CreateSqliteAsync());

    private static async Task Test_SaveChanges_WithStaleVersion_ReturnsZero<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var store = new PalOrmSagaStateStore<TProvider, TestSagaState>(ts.Session);
        var state = new TestSagaState { CustomerId = "cust-1", CurrentState = "Started" };

        // 首次 INSERT（version=0），state.Version 内存快照仍为 0
        await store.SaveChangesAsync(state, default);

        // P2 修复后（expectedVersion = state.Version 内存快照）：真实冲突可测——
        // 模拟另一实例保存了同一 Saga（DB version 0→1），本实例内存快照仍是 0
        state.CurrentState = "ConflictAttempt";
        await ts.Session.ExecuteAsync(
            $"UPDATE saga_states SET version = version + 1 WHERE saga_id = {state.SagaId.ToString()}");

        // 用过期快照（version=0，DB 已是 1）保存 → UPDATE WHERE version=0 → 0 行
        var rowsAffected = await store.SaveChangesAsync(state, default);
        await Assert.That(rowsAffected).IsEqualTo(0); // 冲突被正确检测（旧实现重读 DB 版本恒过，测不出冲突）
        await Assert.That(state.Version).IsEqualTo(0); // 版本不前进（保存未生效）
    }
}

[JsonSerializable(typeof(PalOrmSagaMultiDialectTests.TestSagaState))]
internal sealed partial class SagaTestJsonContext : JsonSerializerContext;
