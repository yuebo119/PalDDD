namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Transactions;
using System.Collections.ObjectModel;
using System.Globalization;

public sealed class SagaStateEfCoreTests
{
    [Test]
    public async Task SaveChangesAsync_PersistsSagaState(CancellationToken cancellationToken)
    {
        await using var db = new TestSagaStateDbContext(CreateOptions());
        var store = (ISagaStateStore<TestSagaState>)db;
        var state = CreateState(
            "PaymentReserved",
            FixedNow,
            stepStartedAt: new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            {
                ["ReservePayment"] = FixedNow.AddSeconds(1)
            },
            executedStepKeys: ["ReservePayment"]);

        db.SagaStates.Add(state);
        await store.SaveChangesAsync(state, cancellationToken);

        db.ChangeTracker.Clear();
        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.CurrentState).IsEqualTo("PaymentReserved");
        await Assert.That(loaded.StepStartedAt["ReservePayment"]).IsEqualTo(FixedNow.AddSeconds(1));
        var keys = loaded.ExecutedStepKeys.ToList();
        await Assert.That(keys).Count().IsEqualTo(1);
        await Assert.That(keys[0]).IsEqualTo("ReservePayment");
    }

    [Test]
    public async Task GetActiveSagasAsync_ReturnsOnlyActiveStatesInCreatedOrder(CancellationToken cancellationToken)
    {
        await using var db = new TestSagaStateDbContext(CreateOptions());
        db.SagaStates.Add(CreateState("SecondActive", FixedNow.AddMinutes(2)));
        db.SagaStates.Add(CreateState("Completed", FixedNow.AddMinutes(1), status: SagaStatus.Completed));
        db.SagaStates.Add(CreateState("FirstActive", FixedNow));
        db.SagaStates.Add(CreateState("Compensated", FixedNow.AddMinutes(3), status: SagaStatus.Compensated));
        db.SagaStates.Add(CreateState("CompensationFailed", FixedNow.AddMinutes(4), status: SagaStatus.CompensationFailed));
        db.SagaStates.Add(CreateState("DeadLettered", FixedNow.AddMinutes(5), status: SagaStatus.DeadLettered));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var active = await db.GetActiveSagasAsync(10, cancellationToken);
        var activeList = active.ToList();

        await Assert.That(activeList).Count().IsEqualTo(2);
        await Assert.That(activeList[0].CurrentState).IsEqualTo("FirstActive");
        await Assert.That(activeList[1].CurrentState).IsEqualTo("SecondActive");
    }

    [Test]
    public async Task GetActiveSagasAsync_RejectsInvalidBatchSize(CancellationToken cancellationToken)
    {
        await using var db = new TestSagaStateDbContext(CreateOptions());

        await Assert.That(async () =>
            await db.GetActiveSagasAsync(0, cancellationToken)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task LeaseActiveSagasAsync_SetsLeaseAndSkipsAlreadyLeasedStates(CancellationToken cancellationToken)
    {
        await using var db = new TestSagaStateDbContext(CreateOptions());
        db.SagaStates.Add(CreateState("Active", FixedNow));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var first = await db.LeaseActiveSagasAsync("owner-1", TimeSpan.FromMinutes(2), 10, cancellationToken);
        var second = await db.LeaseActiveSagasAsync("owner-2", TimeSpan.FromMinutes(2), 10, cancellationToken);

        var firstList = first.ToList();
        await Assert.That(firstList).Count().IsEqualTo(1);
        var leased = firstList[0];
        await Assert.That(leased.LeasedBy).IsEqualTo("owner-1");
        await Assert.That(leased.LeasedUntil).IsNotNull();
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task SaveChangesAsync_ReturnsOne_WhenSaveSucceeds(CancellationToken cancellationToken)
    {
        // ITM-072 回归：EFCore 版 SaveChangesAsync 必须按接口契约返回 1（写入生效），
        // 而非 DbContext 的"实际写入实体数"（无变更保存返回 0 会被调用方误判为乐观锁冲突）。
        await using var db = new TestSagaStateDbContext(CreateOptions());
        var store = (ISagaStateStore<TestSagaState>)db;
        var state = CreateState("Initial", FixedNow);
        db.SagaStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);
        loaded!.CurrentState = "Mutated";

        var affected = await store.SaveChangesAsync(loaded, cancellationToken);

        await Assert.That(affected).IsEqualTo(1);
    }

    [Test]
    public async Task SaveChangesAsync_ReturnsOne_WhenNoPendingChanges(CancellationToken cancellationToken)
    {
        // ITM-072 回归：无变更保存（EF SaveChanges 写 0 实体）时契约语义是"写入生效"——
        // 返回 1 而非 DbContext 的实体计数 0（0 会被 SagaProcessor 误判为乐观锁冲突）。
        // 契约：0 仅表示"目标行不存在或乐观锁冲突"（见 ISagaStateStore 文档），
        // 无变更保存的行存在且无冲突 → 1。
        await using var db = new TestSagaStateDbContext(CreateOptions());
        var store = (ISagaStateStore<TestSagaState>)db;
        var state = CreateState("Initial", FixedNow);
        db.SagaStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var loaded = await store.GetByIdAsync(state.SagaId, cancellationToken);

        var affected = await store.SaveChangesAsync(loaded!, cancellationToken);

        await Assert.That(affected).IsEqualTo(1);
    }

    [Test]
    public async Task SaveChangesAsync_Sqlite_PersistsSagaStateRoundtrip(CancellationToken cancellationToken)
    {
        // ITM-254（F14/PD26）：SQLite 内存库真 schema——StepStartedAt（JSON 列）与
        // ExecutedStepKeys（PrimitiveCollection）经真实关系型列序列化/反序列化 roundtrip
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateSqliteOptions(connection);

        TestSagaState state;
        await using (var writer = new TestSagaStateDbContext(options))
        {
            await writer.Database.EnsureCreatedAsync(cancellationToken);
            state = CreateState(
                "PaymentReserved",
                FixedNow,
                stepStartedAt: new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
                {
                    ["ReservePayment"] = FixedNow.AddSeconds(1)
                },
                executedStepKeys: ["ReservePayment"]);
            writer.SagaStates.Add(state);
            await ((ISagaStateStore<TestSagaState>)writer).SaveChangesAsync(state, cancellationToken);
        }

        // 写读 roundtrip：新 context 读回（Ulid 主键字符串转换 + JSON 列反序列化）
        await using var reader = new TestSagaStateDbContext(options);
        var loaded = await reader.GetByIdAsync(state.SagaId, cancellationToken);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded.CurrentState).IsEqualTo("PaymentReserved");
        await Assert.That(loaded.StepStartedAt["ReservePayment"]).IsEqualTo(FixedNow.AddSeconds(1));
        await Assert.That(loaded.ExecutedStepKeys.ToList()).IsEquivalentTo(["ReservePayment"]);
    }

    [Test]
    public async Task SaveChangesAsync_Sqlite_OptimisticVersionConflictReturnsZero(CancellationToken cancellationToken)
    {
        // ITM-254（F14）：乐观锁关系型验证——Version 并发令牌在 SQLite 下真实翻译为
        // UPDATE ... WHERE Version=@orig；后写者（快照过期）保存影响 0 行，
        // 契约要求返回 0 而非上抛（ISagaStateStore.SaveChangesAsync 语义）
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateSqliteOptions(connection);

        TestSagaState seedState;
        await using (var seed = new TestSagaStateDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(cancellationToken);
            seedState = CreateState("Initial", FixedNow);
            seed.SagaStates.Add(seedState);
            await seed.SaveChangesAsync(cancellationToken);
        }

        // 两 context 各自加载同一 Saga（快照 Version 均为 0）
        await using var ctxStale = new TestSagaStateDbContext(options);
        var staleSnapshot = (await ctxStale.GetByIdAsync(seedState.SagaId, cancellationToken))!;
        await using var ctxFresh = new TestSagaStateDbContext(options);
        var fresh = (await ctxFresh.GetByIdAsync(seedState.SagaId, cancellationToken))!;

        // 先写者提交：Version 0→1，契约返回 1
        fresh.CurrentState = "MutatedByFresh";
        var freshAffected = await ((ISagaStateStore<TestSagaState>)ctxFresh).SaveChangesAsync(fresh, cancellationToken);
        await Assert.That(freshAffected).IsEqualTo(1);

        // 后写者（过期快照 Version=0）：WHERE Version=0 失配 → 契约返回 0
        staleSnapshot.CurrentState = "MutatedByStale";
        var staleAffected = await ((ISagaStateStore<TestSagaState>)ctxStale).SaveChangesAsync(staleSnapshot, cancellationToken);
        await Assert.That(staleAffected).IsEqualTo(0);

        // DB 终值 = 先写者的状态（后写者未覆盖）
        await using var verifier = new TestSagaStateDbContext(options);
        var final = await verifier.GetByIdAsync(seedState.SagaId, cancellationToken);
        await Assert.That(final!.CurrentState).IsEqualTo("MutatedByFresh");
    }

    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse(
        "2026-05-31T00:00:00Z",
        CultureInfo.InvariantCulture);

    private static DbContextOptions<TestSagaStateDbContext> CreateOptions()
        => new DbContextOptionsBuilder<TestSagaStateDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            .Options;

    /// <summary>SQLite 内存库 options——需共享同一打开的 <see cref="SqliteConnection"/>（:memory: 库随连接存活）。</summary>
    private static DbContextOptions<TestSagaStateDbContext> CreateSqliteOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<TestSagaStateDbContext>()
            .UseSqlite(connection)
            .Options;

    private static TestSagaState CreateState(
        string currentState,
        DateTimeOffset createdAt,
        SagaStatus status = SagaStatus.Active,
        Dictionary<string, DateTimeOffset>? stepStartedAt = null,
        Collection<string>? executedStepKeys = null)
        => new()
        {
            CurrentState = currentState,
            CreatedAt = createdAt,
            Status = status,
            StepStartedAt = stepStartedAt ?? [],
            ExecutedStepKeys = executedStepKeys ?? []
        };

    public sealed class TestSagaState : SagaState;

    private sealed class TestSagaStateDbContext(DbContextOptions<TestSagaStateDbContext> options)
        : SagaStateDbContext<TestSagaState>(options);
}
