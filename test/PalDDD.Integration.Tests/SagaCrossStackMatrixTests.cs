using System.Data.Common;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

/// <summary>
/// Saga 四栈契约矩阵（2026-10-21 契约盘点 R5）：
/// InMemory / Dapper SQLite / PalORM SQLite / EF SQLite 跑同一组断言。
/// 锁定条款：缺失返回 null、新状态可保存、按 ID 读回一致、活跃态可租约、
/// 租约后终态保存生效。EF 栈按文档要求经 ChangeTracker 写入（新实体先 Add）。
/// </summary>
public sealed partial class SagaCrossStackMatrixTests
{
    private static readonly string[] SagaDdl =
    [
        "CREATE TABLE saga_states (saga_id TEXT PRIMARY KEY, current_state TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, completed_at TEXT, error TEXT, error_at TEXT, version INTEGER NOT NULL DEFAULT 0, saga_data TEXT, next_check_at TEXT, leased_by TEXT, leased_until TEXT)",
    ];

    [Test]
    public async Task Saga_FourStacks_SameStateAndLeaseContract(CancellationToken ct)
    {
        await AssertContractAsync(new InMemoryHarness(new InMemorySagaStateStore<MatrixSagaState>()), ct);

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            var store = new DapperSagaStateStore<MatrixSagaState>(
                conn,
                jsonTypeInfo: MatrixSagaJsonContext.Default.MatrixSagaState,
                dbType: DapperDbType.Sqlite);
            await AssertContractAsync(new StoreHarness(store), ct);
        }

        await using (var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct))
        {
            var conn = session.GetRawConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            var store = new SqliteSagaStateStore<MatrixSagaState>(
                session, MatrixSagaJsonContext.Default.MatrixSagaState);
            await AssertContractAsync(new StoreHarness(store), ct);
        }

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            var options = new DbContextOptionsBuilder<ProbeSagaContext>()
                .UseSqlite(conn)
                .Options;
            await using var context = new ProbeSagaContext(options);
            await context.Database.EnsureCreatedAsync(ct);
            await AssertContractAsync(new EfHarness(context), ct);
        }
    }

    private static async Task AssertContractAsync(ISagaHarness harness, CancellationToken ct)
    {
        var missing = await harness.GetAsync(PalUlid.New(), ct);
        await Assert.That(missing).IsNull();

        var state = new MatrixSagaState { CurrentState = "Started" };
        var created = await harness.CreateAsync(state, ct);
        await Assert.That(created).IsEqualTo(1);

        var loaded = await harness.GetAsync(state.SagaId, ct);
        await Assert.That(loaded).IsTypeOf<MatrixSagaState>();
        await Assert.That(loaded!.CurrentState).IsEqualTo("Started");
        await Assert.That(loaded.Status).IsEqualTo(SagaStatus.Active);

        var leased = await harness.LeaseAsync("matrix-owner", ct);
        await Assert.That(leased.Count).IsEqualTo(1);
        await Assert.That(leased[0].SagaId).IsEqualTo(state.SagaId);
        await Assert.That(leased[0].LeasedBy).IsEqualTo("matrix-owner");

        var leasedState = leased[0];
        leasedState.Status = SagaStatus.Completed;
        await Assert.That(await harness.SaveAsync(leasedState, ct)).IsEqualTo(1);

        var completed = await harness.GetAsync(state.SagaId, ct);
        await Assert.That(completed!.Status).IsEqualTo(SagaStatus.Completed);
    }

    private static async Task ExecuteAsync(DbConnection connection, CancellationToken ct)
    {
        foreach (var sql in SagaDdl)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private interface ISagaHarness
    {
        ValueTask<int> CreateAsync(MatrixSagaState state, CancellationToken ct);

        ValueTask<int> SaveAsync(MatrixSagaState state, CancellationToken ct);

        ValueTask<MatrixSagaState?> GetAsync(PalUlid id, CancellationToken ct);

        ValueTask<IReadOnlyList<MatrixSagaState>> LeaseAsync(string owner, CancellationToken ct);
    }

    private sealed class StoreHarness(ISagaStateStore<MatrixSagaState> store) : ISagaHarness
    {
        public ValueTask<int> CreateAsync(MatrixSagaState state, CancellationToken ct)
            => store.SaveChangesAsync(state, ct);

        public ValueTask<int> SaveAsync(MatrixSagaState state, CancellationToken ct)
            => store.SaveChangesAsync(state, ct);

        public ValueTask<MatrixSagaState?> GetAsync(PalUlid id, CancellationToken ct)
            => store.GetByIdAsync(id, ct);

        public ValueTask<IReadOnlyList<MatrixSagaState>> LeaseAsync(string owner, CancellationToken ct)
            => store.LeaseActiveSagasAsync(owner, TimeSpan.FromMinutes(1), 10, ct);
    }

    private sealed class InMemoryHarness(InMemorySagaStateStore<MatrixSagaState> store) : ISagaHarness
    {
        public ValueTask<int> CreateAsync(MatrixSagaState state, CancellationToken ct)
        {
            store.Add(state);
            return store.SaveChangesAsync(state, ct);
        }

        public ValueTask<int> SaveAsync(MatrixSagaState state, CancellationToken ct)
            => store.SaveChangesAsync(state, ct);

        public ValueTask<MatrixSagaState?> GetAsync(PalUlid id, CancellationToken ct)
            => store.GetByIdAsync(id, ct);

        public ValueTask<IReadOnlyList<MatrixSagaState>> LeaseAsync(string owner, CancellationToken ct)
            => store.LeaseActiveSagasAsync(owner, TimeSpan.FromMinutes(1), 10, ct);
    }

    private sealed class EfHarness(ProbeSagaContext context) : ISagaHarness
    {
        private ISagaStateStore<MatrixSagaState> Store => context;

        public ValueTask<int> CreateAsync(MatrixSagaState state, CancellationToken ct)
        {
            // EF 栈按接口文档经 ChangeTracker 写入：新实体先 Add。
            if (context.Entry(state).State == EntityState.Detached)
                context.SagaStates.Add(state);
            return Store.SaveChangesAsync(state, ct);
        }

        public ValueTask<int> SaveAsync(MatrixSagaState state, CancellationToken ct)
        {
            if (context.Entry(state).State == EntityState.Detached)
                context.SagaStates.Attach(state);
            return Store.SaveChangesAsync(state, ct);
        }

        public ValueTask<MatrixSagaState?> GetAsync(PalUlid id, CancellationToken ct)
            => Store.GetByIdAsync(id, ct);

        public ValueTask<IReadOnlyList<MatrixSagaState>> LeaseAsync(string owner, CancellationToken ct)
            => Store.LeaseActiveSagasAsync(owner, TimeSpan.FromMinutes(1), 10, ct);
    }

    public sealed class MatrixSagaState : SagaState
    {
        public string Payload { get; set; } = string.Empty;
    }

    [JsonSerializable(typeof(MatrixSagaState))]
    private sealed partial class MatrixSagaJsonContext : JsonSerializerContext;

    private sealed class ProbeSagaContext(DbContextOptions options) : SagaStateDbContext<MatrixSagaState>(options);
}
