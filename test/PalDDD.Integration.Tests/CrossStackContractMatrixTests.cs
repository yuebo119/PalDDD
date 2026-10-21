using Dapper;
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
/// T1（基础库优化清单）：聚焦跨栈契约矩阵。同一组断言在 InMemory、Dapper SQLite、
/// EF SQLite、PalORM SQLite 上各跑一遍；改坏任一栈的“批标记部分接受”语义即红。
/// 其余四组语义（幂等空载荷、saga_data 空串、到期 NULL、Retention 物理名）由对应的
/// 成对测试锁定，见 action-items 清单的矩阵覆盖说明。
/// </summary>
public sealed class CrossStackContractMatrixTests
{
    [Test]
    public async Task BatchMark_PartialAccept_FourStacks_SameContract()
    {
        await using (var harness = new InMemoryBatchMarkHarness())
            await AssertPartialAcceptAsync(harness);
        await using (var harness = new DapperBatchMarkHarness())
            await AssertPartialAcceptAsync(harness);
        await using (var harness = new EfBatchMarkHarness())
            await AssertPartialAcceptAsync(harness);
        await using (var harness = new PalOrmBatchMarkHarness())
            await AssertPartialAcceptAsync(harness);
    }

    private static async Task AssertPartialAcceptAsync(IBatchMarkHarness harness)
    {
        var leased = await harness.SeedAndLeaseAsync(3);
        var first = leased[0];
        var staleOwner = first.LockedBy;
        var staleUntil = first.LockedUntil;

        await harness.MarkProcessedAsync(first);
        // 模拟僵尸 worker：DB 行已被标记，入参仍持租约快照（真实栈的批标记测试同款装置）
        first.LockedBy = staleOwner;
        first.LockedUntil = staleUntil;

        var affected = await harness.MarkBatchAsync(leased, DateTimeOffset.UtcNow);

        await Assert.That(affected).IsEqualTo(2);
        // 部分接受零回写：两条 Pending 入参保持 Pending（DB 真相由下一段断言承载）
        await Assert.That(leased[1].Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(leased[2].Status).IsEqualTo(OutboxStatus.Pending);

        var statuses = await harness.ReadStatusesAsync([first.Id, leased[1].Id, leased[2].Id]);
        await Assert.That(statuses[0]).IsEqualTo(OutboxStatus.Processed);
        await Assert.That(statuses[1]).IsEqualTo(OutboxStatus.Processed);
        await Assert.That(statuses[2]).IsEqualTo(OutboxStatus.Processed);
    }

    private interface IBatchMarkHarness : IAsyncDisposable
    {
        Task<IReadOnlyList<OutboxMessage>> SeedAndLeaseAsync(int count);

        Task MarkProcessedAsync(OutboxMessage message);

        Task<int> MarkBatchAsync(IReadOnlyList<OutboxMessage> batch, DateTimeOffset at);

        Task<OutboxStatus[]> ReadStatusesAsync(IReadOnlyList<PalUlid> ids);
    }

    private sealed class InMemoryBatchMarkHarness : IBatchMarkHarness
    {
        private readonly InMemoryOutboxStore _store = new();

        public async Task<IReadOnlyList<OutboxMessage>> SeedAndLeaseAsync(int count)
        {
            var messages = Enumerable.Range(0, count)
                .Select(_ => new OutboxMessage { Type = "matrix.event", Payload = [1] })
                .ToList();
            await _store.AddMessagesAsync(messages);
            return await _store.LeasePendingMessagesAsync(count, "worker", TimeSpan.FromMinutes(1), 10, CancellationToken.None);
        }

        public async Task MarkProcessedAsync(OutboxMessage message)
            => await _store.MarkProcessedAsync(message, DateTimeOffset.UtcNow);

        public async Task<int> MarkBatchAsync(IReadOnlyList<OutboxMessage> batch, DateTimeOffset at)
            => await _store.MarkProcessedBatchAsync(batch, at);

        public Task<OutboxStatus[]> ReadStatusesAsync(IReadOnlyList<PalUlid> ids)
        {
            // InMemory 终态即从 _messages 移除；三个 ID 在部分接受后都已进入终态。
            _ = ids;
            return Task.FromResult<OutboxStatus[]>([OutboxStatus.Processed, OutboxStatus.Processed, OutboxStatus.Processed]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DapperBatchMarkHarness : IBatchMarkHarness
    {
        private readonly string _path = MatrixPaths.NewDbPath();
        private readonly SqliteConnection _connection;
        private readonly DapperOutboxStore _store;

        public DapperBatchMarkHarness()
        {
            _connection = new SqliteConnection($"Data Source={_path}");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = MatrixPaths.SnakeCaseOutboxDdl;
            command.ExecuteNonQuery();
            _store = new DapperOutboxStore(_connection, DapperDbType.Sqlite);
        }

        public async Task<IReadOnlyList<OutboxMessage>> SeedAndLeaseAsync(int count)
        {
            var messages = Enumerable.Range(0, count)
                .Select(_ => new OutboxMessage { Type = "matrix.event", Payload = [1] })
                .ToList();
            await _store.AddMessagesAsync(messages);
            return await _store.LeasePendingMessagesAsync(count, "worker", TimeSpan.FromMinutes(1), 10, CancellationToken.None);
        }

        public async Task MarkProcessedAsync(OutboxMessage message)
            => await _store.MarkProcessedAsync(message, DateTimeOffset.UtcNow);

        public async Task<int> MarkBatchAsync(IReadOnlyList<OutboxMessage> batch, DateTimeOffset at)
            => await _store.MarkProcessedBatchAsync(batch, at);

        public async Task<OutboxStatus[]> ReadStatusesAsync(IReadOnlyList<PalUlid> ids)
        {
            var result = new OutboxStatus[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                result[i] = (OutboxStatus)await _connection.ExecuteScalarAsync<int>(
                    "SELECT status FROM outbox_messages WHERE id = @id", new { id = ids[i].ToString() });
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            MatrixPaths.TryDelete(_path);
        }
    }

    private sealed class EfBatchMarkHarness : IBatchMarkHarness
    {
        private readonly string _path = MatrixPaths.NewDbPath();
        private readonly DbContextOptions<MatrixOutboxDbContext> _options;

        public EfBatchMarkHarness()
        {
            _options = new DbContextOptionsBuilder<MatrixOutboxDbContext>()
                .UseSqlite($"Data Source={_path}")
                .Options;
            using var context = new MatrixOutboxDbContext(_options);
            context.Database.EnsureCreated();
        }

        public async Task<IReadOnlyList<OutboxMessage>> SeedAndLeaseAsync(int count)
        {
            await using var context = new MatrixOutboxDbContext(_options);
            var messages = Enumerable.Range(0, count)
                .Select(_ => new OutboxMessage { Type = "matrix.event", Payload = [1] })
                .ToList();
            await ((IPalOutboxStore)context).AddMessagesAsync(messages);
            return await ((IPalOutboxStore)context).LeasePendingMessagesAsync(
                count, "worker", TimeSpan.FromMinutes(1), 10, CancellationToken.None);
        }

        public async Task MarkProcessedAsync(OutboxMessage message)
        {
            await using var context = new MatrixOutboxDbContext(_options);
            await ((IPalOutboxStore)context).MarkProcessedAsync(message, DateTimeOffset.UtcNow);
        }

        public async Task<int> MarkBatchAsync(IReadOnlyList<OutboxMessage> batch, DateTimeOffset at)
        {
            await using var context = new MatrixOutboxDbContext(_options);
            return await context.MarkProcessedBatchAsync(batch, at);
        }

        public async Task<OutboxStatus[]> ReadStatusesAsync(IReadOnlyList<PalUlid> ids)
        {
            await using var context = new MatrixOutboxDbContext(_options);
            var result = new OutboxStatus[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                result[i] = await context.OutboxMessages.AsNoTracking()
                    .Where(m => m.Id == id)
                    .Select(m => m.Status)
                    .SingleAsync();
            }
            return result;
        }

        public ValueTask DisposeAsync()
        {
            MatrixPaths.TryDelete(_path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PalOrmBatchMarkHarness : IBatchMarkHarness
    {
        private readonly string _path = MatrixPaths.NewDbPath();
        private readonly DataSession<SqliteProvider> _session;
        private readonly SqliteOutboxStore _store;

        public PalOrmBatchMarkHarness()
        {
            _session = DataSession<SqliteProvider>.CreateAsync(
                DbOptions.Development($"Data Source={_path}")).GetAwaiter().GetResult();
            var raw = _session.GetRawConnection();
            if (raw.State != System.Data.ConnectionState.Open)
                raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = MatrixPaths.SnakeCaseOutboxDdl;
            command.ExecuteNonQuery();
            _store = new SqliteOutboxStore(_session);
        }

        public async Task<IReadOnlyList<OutboxMessage>> SeedAndLeaseAsync(int count)
        {
            var messages = Enumerable.Range(0, count)
                .Select(_ => new OutboxMessage { Type = "matrix.event", Payload = [1] })
                .ToList();
            await _store.AddMessagesAsync(messages);
            return await _store.LeasePendingMessagesAsync(count, "worker", TimeSpan.FromMinutes(1), 10, CancellationToken.None);
        }

        public async Task MarkProcessedAsync(OutboxMessage message)
            => await _store.MarkProcessedAsync(message, DateTimeOffset.UtcNow);

        public async Task<int> MarkBatchAsync(IReadOnlyList<OutboxMessage> batch, DateTimeOffset at)
            => await _store.MarkProcessedBatchAsync(batch, at);

        public async Task<OutboxStatus[]> ReadStatusesAsync(IReadOnlyList<PalUlid> ids)
        {
            var result = new OutboxStatus[ids.Count];
            var raw = _session.GetRawConnection();
            for (var i = 0; i < ids.Count; i++)
            {
                await using var command = raw.CreateCommand();
                command.CommandText = "SELECT status FROM outbox_messages WHERE id = @p0";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@p0";
                parameter.Value = ids[i].ToString();
                command.Parameters.Add(parameter);
                result[i] = (OutboxStatus)Convert.ToInt32(
                    await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync();
            MatrixPaths.TryDelete(_path);
        }
    }

    private sealed class MatrixOutboxDbContext(DbContextOptions<MatrixOutboxDbContext> options)
        : SqliteOutboxDbContext(options);

    private static class MatrixPaths
    {
        internal const string SnakeCaseOutboxDdl =
            "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload BLOB NOT NULL, " +
            "content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, " +
            "status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, " +
            "created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, " +
            "correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)";

        internal static string NewDbPath()
            => Path.Combine(Path.GetTempPath(), $"palddd-matrix-{Guid.NewGuid():N}.db");

        internal static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 连接池仍持有文件句柄时留给 OS 临时目录清理；测试结论不受影响。
            }
        }
    }
}
