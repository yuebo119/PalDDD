using PalDDD.PalORM.Stores;
using System.Text.Json.Serialization;
using PalORM;
using PalDDD.PalORM.MySql;
using PalDDD.PalORM.PostgreSql;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>Saga Store 跨方言测试 —— 验证 saga_data JSON 列 + version 乐观锁。</summary>
/// <remarks>
/// unified v2.0（2026-08-20）：全部 store 构造注入 jsonTypeInfo——产品侧 SaveChangesAsync 已是
/// fail-fast 守卫（无 jsonTypeInfo 抛 InvalidOperationException，防 saga_data 静默丢失，ITM-005 谱系），
/// 无 json 的宽容时代测试属过期契约；PG 方言必须走派生类（jsonb CAST，ITM-127 回归）。
/// </remarks>
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmSagaMultiDialectTests
{
    public sealed class TestSagaState : SagaState
    {
        public string CustomerId { get; set; } = "";
    }

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestSagaState> Json =
        SagaTestJsonContext.Default.TestSagaState;

    // ── 方言 store 工厂（PG 走派生类避 42804；SQLite/MySQL 泛型基类即可）──

    private static async Task<ISagaStateStore<TestSagaState>> CreateSqliteStoreAsync()
    {
        var ts = await MultiDialectFixture.CreateSqliteAsync();
        return new SqliteSagaStateStore<TestSagaState>(ts.Session, Json);
    }

    private static async Task<ISagaStateStore<TestSagaState>> CreatePostgreSqlStoreAsync()
    {
        var ts = await MultiDialectFixture.CreatePostgreSqlAsync();
        return new PostgreSqlSagaStateStore<TestSagaState>(ts.Session, Json);
    }

    private static async Task<ISagaStateStore<TestSagaState>> CreateMySqlStoreAsync()
    {
        var ts = await MultiDialectFixture.CreateMySqlAsync();
        return new MySqlSagaStateStore<TestSagaState>(ts.Session, Json);
    }

    [Test]
    public async Task Saga_Sqlite_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await CreateSqliteStoreAsync());

    [Test]
    public async Task Saga_PostgreSql_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await CreatePostgreSqlStoreAsync());

    [Test]
    public async Task Saga_MySql_InsertNew_ThenGetById()
        => await Test_InsertNew_ThenGetById(await CreateMySqlStoreAsync());

    private static async Task Test_InsertNew_ThenGetById(ISagaStateStore<TestSagaState> store)
    {
        var state = new TestSagaState { CustomerId = "cust-1", CurrentState = "Started" };

        await store.SaveChangesAsync(state, default);
        var loaded = await store.GetByIdAsync(state.SagaId, default);

        await Assert.That(loaded).IsNotNull();
        // jsonTypeInfo 注入路径：CustomerId 从 saga_data JSON 反序列化恢复（fail-fast 契约下必持久化）
        await Assert.That(loaded!.CustomerId).IsEqualTo("cust-1");
        await Assert.That(loaded.CurrentState).IsEqualTo("Started");
        await Assert.That(loaded.Version).IsEqualTo(0);  // INSERT 不自增 version（与 Dapper 实现一致）
        await Assert.That(loaded.Status).IsEqualTo(SagaStatus.Active);
    }

    [Test]
    public async Task Saga_Sqlite_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await CreateSqliteStoreAsync());

    [Test]
    public async Task Saga_PostgreSql_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await CreatePostgreSqlStoreAsync());

    [Test]
    public async Task Saga_MySql_GetActiveSagas_ReturnsOnlyActive()
        => await Test_GetActiveSagas(await CreateMySqlStoreAsync());

    private static async Task Test_GetActiveSagas(ISagaStateStore<TestSagaState> store)
    {
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
        => await Test_LeaseActiveSagas(await CreateSqliteStoreAsync());

    [Test]
    public async Task Saga_PostgreSql_LeaseActiveSagas()
        => await Test_LeaseActiveSagas(await CreatePostgreSqlStoreAsync());

    [Test]
    public async Task Saga_MySql_LeaseActiveSagas()
        => await Test_LeaseActiveSagas(await CreateMySqlStoreAsync());

    private static async Task Test_LeaseActiveSagas(ISagaStateStore<TestSagaState> store)
    {
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
        => await Test_WithJsonTypeInfo_PreservesBusinessFields(await CreateSqliteStoreAsync());

    [Test]
    public async Task Saga_PostgreSql_WithJsonTypeInfo_PreservesBusinessFields()
        => await Test_WithJsonTypeInfo_PreservesBusinessFields(await CreatePostgreSqlStoreAsync());

    private static async Task Test_WithJsonTypeInfo_PreservesBusinessFields(ISagaStateStore<TestSagaState> store)
    {
        // 注入 jsonTypeInfo，使 saga_data JSON 列持久化完整状态快照（ITM-014 覆盖）
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
    {
        var ts = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteSagaStateStore<TestSagaState>(ts.Session, Json);
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
