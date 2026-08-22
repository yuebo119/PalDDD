using System.Text.Json.Serialization;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Saga State Store 测试（SQLite :memory:，本地可跑）—— ITM-242/243 修复传感器。
/// <para>
/// <b>ITM-242 传感器</b>：租约时间往返断言（offset==0 + 窗口）+
/// 模拟 MySQL DATETIME 存储形态（DateTime Kind=Utc 参数 → SQLite 无偏移墙钟文本，
/// GetDateTime 返回 Kind=Unspecified）的读回断言——后者修复前红（隐式 DateTime→DateTimeOffset
/// 转换对 Unspecified 套本地偏移，UTC+8 机器实测 -8h 漂移）、修复后绿，是 MySQL 时间漂移的本地复现形态。
/// 探针结论（2026-08-22，PalORM 5.3 + Microsoft.Data.Sqlite）：DateTimeOffset 参数存带偏移文本，
/// GetDateTime 返回 Kind=Utc（SQLite 正常写读路径免疫漂移，断言为跨方言守护形态）。
/// </para>
/// <para>
/// <b>ITM-243 传感器</b>：IUnitOfWork 事务内 SaveChangesAsync（内部 GetByIdAsync raw command
/// 经 PalOrmAmbientTransaction 挂接）的 Commit/Rollback 语义。SQLite 无 MySqlConnector 的
/// 严格事务校验，此处在本地验证修复不破坏事务语义（守护形态；MySQL 下修复前第一句 raw
/// command 即抛 InvalidOperationException）。
/// </para>
/// </summary>
public class PalOrmSagaStateStoreTests
{
    public sealed class StoreTestSagaState : SagaState
    {
        public string CustomerId { get; set; } = "";
    }

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<StoreTestSagaState> Json =
        StoreTestJsonContext.Default.StoreTestSagaState;

    [Test]
    public async Task LeaseActiveSagasAsync_LeasedUntilRoundTripsWithZeroOffset()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteSagaStateStore<StoreTestSagaState>(session, Json);
        await store.SaveChangesAsync(new StoreTestSagaState { CustomerId = "cust-1", CurrentState = "Active" }, default);

        var before = DateTimeOffset.UtcNow;
        var leaseDuration = TimeSpan.FromMinutes(5);
        var leased = await store.LeaseActiveSagasAsync("worker-1", leaseDuration, 10, default);

        await Assert.That(leased.Count).IsEqualTo(1);
        var leasedUntil = leased[0].LeasedUntil!.Value;
        // ITM-242 守护：物化读回的时间戳偏移必须为 0——任何方言的本地偏移漂移在此现形
        await Assert.That(leasedUntil.Offset).IsEqualTo(TimeSpan.Zero);
        // 租约起点（LeasedUntil - duration）落在发起时刻前后窗口内（往返无累积漂移）
        var leaseStart = leasedUntil - leaseDuration;
        await Assert.That(leaseStart >= before.AddSeconds(-5) && leaseStart <= DateTimeOffset.UtcNow.AddSeconds(5)).IsTrue();
    }

    [Test]
    public async Task GetActiveSagasAsync_ReadsUtcWallClockTimestamp_WithoutLocalOffsetDrift()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteSagaStateStore<StoreTestSagaState>(session, Json);
        // 模拟 MySQL DATETIME 存储形态：DateTime(Kind=Utc) 参数 → SQLite 无偏移墙钟文本，
        // GetDateTime 返回 Kind=Unspecified + UTC 墙钟（探针场景 3，与 MySQL DATETIME 同病理）。
        // 修复前：Unspecified 隐式转 DateTimeOffset 套本地偏移（UTC+8 机器 created_at 漂移 -8h）→ 红；
        // 修复后：SpecifyKind(Utc) → offset 0 精确读回 → 绿。
        var createdAtUtc = DateTime.UtcNow.AddMinutes(-10);
        // saga_id 须为合法 ULID（Materialize 走 PalUlid.Parse）
        var sagaId = ByteAether.Ulid.Ulid.New().ToString();
        await session.ExecuteAsync(
            $"INSERT INTO saga_states (saga_id, current_state, status, created_at, version) VALUES ({sagaId}, {"Active"}, {(int)SagaStatus.Active}, {createdAtUtc}, {0})");

        var actives = await store.GetActiveSagasAsync(10, default);

        await Assert.That(actives.Count).IsEqualTo(1);
        await Assert.That(actives[0].CreatedAt.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(actives[0].CreatedAt.UtcDateTime).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task SaveChangesAsync_InUnitOfWorkTransaction_CommitMakesDataVisible()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteSagaStateStore<StoreTestSagaState>(session, Json);
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        await uow.BeginTransactionAsync();
        var state = new StoreTestSagaState { CustomerId = "tx-commit", CurrentState = "Started" };

        // SaveChangesAsync 内部先 GetByIdAsync（raw command，ITM-243 经 PalOrmAmbientTransaction
        // 挂接环境事务）再 INSERT（ExecuteAsync 自动 enlist）——Commit 前后语义均须正确。
        await store.SaveChangesAsync(state, default);
        await uow.CommitAsync();

        var loaded = await store.GetByIdAsync(state.SagaId, default);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.CustomerId).IsEqualTo("tx-commit");
    }

    [Test]
    public async Task SaveChangesAsync_InUnitOfWorkTransaction_RollbackHidesData()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteSagaStateStore<StoreTestSagaState>(session, Json);
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        await uow.BeginTransactionAsync();
        var state = new StoreTestSagaState { CustomerId = "tx-rollback", CurrentState = "Started" };

        await store.SaveChangesAsync(state, default);
        await uow.RollbackAsync();

        var loaded = await store.GetByIdAsync(state.SagaId, default);
        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task GetByIdAsync_AfterCommit_RawCommandNotBoundToReleasedTransaction()
    {
        // ITM-243 清理侧守护：Commit 的 finally 清空 PalOrmAmbientTransaction 后，
        // 后续 raw command 不挂已释放事务（AsyncLocal 泄漏会导致命令复用失效 DbTransaction）。
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteSagaStateStore<StoreTestSagaState>(session, Json);
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        await uow.BeginTransactionAsync();
        var first = new StoreTestSagaState { CustomerId = "first", CurrentState = "Started" };
        await store.SaveChangesAsync(first, default);
        await uow.CommitAsync();

        // Commit 之后新开事务写入再回滚——不受上一轮 AsyncLocal 残留影响
        await uow.BeginTransactionAsync();
        var second = new StoreTestSagaState { CustomerId = "second", CurrentState = "Started" };
        await store.SaveChangesAsync(second, default);
        await uow.RollbackAsync();

        var firstLoaded = await store.GetByIdAsync(first.SagaId, default);
        await Assert.That(firstLoaded).IsNotNull();
        await Assert.That(await store.GetByIdAsync(second.SagaId, default)).IsNull();
    }
}

[JsonSerializable(typeof(PalOrmSagaStateStoreTests.StoreTestSagaState))]
internal sealed partial class StoreTestJsonContext : JsonSerializerContext;
