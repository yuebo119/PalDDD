using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧾 R4/P0-2（ADR-027）终态批量标记跨栈 SQL 正确性测试（SQLite 真库，本地可跑）
// ═══════════════════════════════════════════════════════════════
// Dapper 栈与 EF Core 栈的 IBatchMarkOutboxStore.MarkProcessedBatchAsync：
// 全批命中（行状态断言 raw 直查）／fencing 部分拒绝（被重置行不动、其余标记、内存零回写）／
// 未租约批防御抛／混租约批防御抛／全命中内存六字段回写。
// 处理器集成（批序/冲刷时点/失败镜像）由 Transactions.Tests.OutboxBatchMarkTests 承载；
// PG/MySQL 方言的批量 SQL 由 CI dialect-probe 车道按需跟进（本文件锁定语义而非方言文本）。
// ═══════════════════════════════════════════════════════════════

/// <summary>Dapper 栈批量标记测试——租约/回读经生产 store 路径（TypeHandler 全局注册
/// 对齐 DapperStoreTests，[NotInParallel] 同组防全局状态竞态）。</summary>
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class DapperBatchMarkSqliteTests
{
    private SqliteConnection _conn = null!;
    private static bool s_previousUnderscoreSetting;

    private const string OutboxDdl =
        "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload BLOB NOT NULL, " +
        "content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, " +
        "status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, " +
        "created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, " +
        "correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)";

    [Before(Class)]
    public static void ClassInitialize()
    {
        s_previousUnderscoreSetting = global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores;
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        var dtoHandler = new SqliteDateTimeOffsetTypeHandler();
        global::Dapper.SqlMapper.AddTypeHandler(dtoHandler);
        global::Dapper.SqlMapper.AddTypeHandler(typeof(DateTimeOffset), dtoHandler);
        global::Dapper.SqlMapper.AddTypeHandler(typeof(DateTimeOffset?), dtoHandler);
        var ulidHandler = new SqliteUlidTypeHandler();
        global::Dapper.SqlMapper.AddTypeHandler(ulidHandler);
        global::Dapper.SqlMapper.AddTypeHandler(typeof(PalUlid), ulidHandler);
    }

    [After(Class)]
    public static void ClassCleanup()
    {
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = s_previousUnderscoreSetting;
        global::Dapper.SqlMapper.ResetTypeHandlers();
    }

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync(cancellationToken);
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = OutboxDdl;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    [After(Test)]
    public async Task CleanupAsync() => await _conn.DisposeAsync();

    /// <summary>种子 n 行 Pending 消息（raw ADO 写入，created_at 过去时刻保证可租；
    /// payload 以 byte[] 参数写入 BLOB——Dapper.AOT 物化 OutboxMessage.Payload 为 byte[]，
    /// TEXT 形态字符串会触发 InvalidCastException）。</summary>
    private async Task<List<PalUlid>> SeedPendingAsync(int count)
    {
        var ids = new List<PalUlid>(count);
        for (var i = 0; i < count; i++)
        {
            var id = PalUlid.New();
            ids.Add(id);
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO outbox_messages (id, type, payload, created_at) VALUES ($id, 'test.event', $payload, $created)";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$payload", Array.Empty<byte>());
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        return ids;
    }

    private async Task<(int Status, int RetryCount, string? LockedBy)> ReadRowAsync(PalUlid id)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT status, retry_count, locked_by FROM outbox_messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException($"row {id} not found");
        return (reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private DapperOutboxStore CreateStore() => new(_conn, DapperDbType.Sqlite);

    [Test]
    public async Task LeasedBatch_AllMarked_RowStateVerified(CancellationToken cancellationToken)
    {
        var ids = await SeedPendingAsync(5);
        var store = CreateStore();
        var leased = await store.LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, cancellationToken);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, cancellationToken);

        await Assert.That(affected).IsEqualTo(5);
        foreach (var id in ids)
        {
            var (status, _, lockedBy) = await ReadRowAsync(id);
            await Assert.That(status).IsEqualTo(1); // Processed
            await Assert.That(lockedBy).IsNull();   // 租约显式释放
        }
        // 全命中 → 内存六字段回写（ADR-026 ② 契约）
        foreach (var msg in leased)
        {
            await Assert.That(msg.Status).IsEqualTo(OutboxStatus.Processed);
            await Assert.That(msg.LockedBy).IsNull();
        }
    }

    [Test]
    public async Task FencingPartialReject_RequeuedRowUntouched(CancellationToken cancellationToken)
    {
        var ids = await SeedPendingAsync(3);
        var store = CreateStore();
        var leased = await store.LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, cancellationToken);

        // 首条被 ReleaseForRetry 推进（DB：retry_count +1、租约清空；内存：成功回写清租约字段）。
        // 恢复其内存租约快照——模拟真实僵尸 worker（持有重置前冻结的租约快照，DB 行已被推进）
        var staleOwner = leased[0].LockedBy;
        var staleUntil = leased[0].LockedUntil;
        await store.ReleaseForRetryAsync(leased[0], "simulated failure", DateTimeOffset.UtcNow.AddSeconds(1), cancellationToken);
        leased[0].LockedBy = staleOwner;
        leased[0].LockedUntil = staleUntil;

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, cancellationToken);

        await Assert.That(affected).IsEqualTo(2);
        var (status, retry, lockedBy) = await ReadRowAsync(ids[0]);
        await Assert.That(status).IsEqualTo(0); // Pending 保留，未被批量误标
        await Assert.That(retry).IsEqualTo(1);
        await Assert.That(lockedBy).IsNull();
        for (var i = 1; i < 3; i++)
        {
            var (s, _, _) = await ReadRowAsync(ids[i]);
            await Assert.That(s).IsEqualTo(1);
        }
        // 部分拒绝 → 内存零回写（保守契约：无法逐行定位被拒者）：全部入参保持内存快照形态
        // ——成功行（leased[1]/[2]）的 DB 真相已 Processed（上方 raw 断言），内存不回写
        await Assert.That(leased[0].Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(leased[1].Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(leased[2].Status).IsEqualTo(OutboxStatus.Pending);
    }

    [Test]
    public async Task NullLeaseBatch_ThrowsInvalidOperationException()
    {
        var store = CreateStore();
        var msg = new OutboxMessage { Id = PalUlid.New(), Type = "t", Payload = [] }; // LockedBy/LockedUntil = null

        await Assert.That(async () =>
            await store.MarkProcessedBatchAsync([msg], DateTimeOffset.UtcNow, CancellationToken.None).AsTask())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task MixedLeaseBatch_ThrowsInvalidOperationException(CancellationToken cancellationToken)
    {
        await SeedPendingAsync(2);
        var store = CreateStore();
        // 两次租约（时长不同 → until 必异）——混批防御
        var first = await store.LeasePendingMessagesAsync(1, "worker-a", TimeSpan.FromMinutes(1), 10, cancellationToken);
        var second = await store.LeasePendingMessagesAsync(1, "worker-a", TimeSpan.FromMinutes(2), 10, cancellationToken);

        await Assert.That(async () =>
            await store.MarkProcessedBatchAsync([first[0], second[0]], DateTimeOffset.UtcNow, CancellationToken.None).AsTask())
            .Throws<InvalidOperationException>();
    }
}

/// <summary>EF Core 栈批量标记测试——生产 <see cref="SqliteOutboxDbContext"/> 直用（无重写）。</summary>
public sealed class EfCoreBatchMarkSqliteTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestBatchMarkOutboxDbContext> _options = null!;

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(cancellationToken);
        _options = new DbContextOptionsBuilder<TestBatchMarkOutboxDbContext>()
            .UseSqlite(_connection)
            .Options;
        await using var db = new TestBatchMarkOutboxDbContext(_options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    [After(Test)]
    public async Task CleanupAsync() => await _connection.DisposeAsync();

    private async Task<List<PalUlid>> SeedPendingAsync(int count)
    {
        List<PalUlid> ids = new(count);
        for (var i = 0; i < count; i++)
        {
            var id = PalUlid.New();
            ids.Add(id);
            await using var db = new TestBatchMarkOutboxDbContext(_options);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = id,
                Type = "test.event",
                Payload = [],
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            await db.SaveChangesAsync();
        }
        return ids;
    }

    [Test]
    public async Task LeasedBatch_AllMarked(CancellationToken cancellationToken)
    {
        var ids = await SeedPendingAsync(4);
        await using var db = new TestBatchMarkOutboxDbContext(_options);
        var leased = await ((IPalOutboxStore)db).LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, cancellationToken);

        var affected = await db.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, cancellationToken);

        await Assert.That(affected).IsEqualTo(4);
        foreach (var id in ids)
        {
            await using var verify = new TestBatchMarkOutboxDbContext(_options);
            var row = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id, cancellationToken);
            await Assert.That(row.Status).IsEqualTo(OutboxStatus.Processed);
            await Assert.That(row.LockedBy).IsNull();
        }
        foreach (var msg in leased)
        {
            await Assert.That(msg.Status).IsEqualTo(OutboxStatus.Processed); // 全命中回写
        }
    }

    [Test]
    public async Task FencingPartialReject_RequeuedRowUntouched(CancellationToken cancellationToken)
    {
        var ids = await SeedPendingAsync(3);
        await using var db = new TestBatchMarkOutboxDbContext(_options);
        var leased = await ((IPalOutboxStore)db).LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, cancellationToken);

        // 首条被 ReleaseForRetry 推进（内存回写清租约字段）——恢复其内存租约快照，
        // 模拟真实僵尸 worker（持重置前冻结快照，DB 行已被推进）
        var staleOwner = leased[0].LockedBy;
        var staleUntil = leased[0].LockedUntil;
        await ((IPalOutboxStore)db).ReleaseForRetryAsync(leased[0], "simulated failure", DateTimeOffset.UtcNow.AddSeconds(1), cancellationToken);
        leased[0].LockedBy = staleOwner;
        leased[0].LockedUntil = staleUntil;

        var affected = await db.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, cancellationToken);

        await Assert.That(affected).IsEqualTo(2);
        await using (var verify = new TestBatchMarkOutboxDbContext(_options))
        {
            var first = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == ids[0], cancellationToken);
            await Assert.That(first.Status).IsEqualTo(OutboxStatus.Pending);
            await Assert.That(first.RetryCount).IsEqualTo(1);
            for (var i = 1; i < 3; i++)
            {
                var row = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == ids[i], cancellationToken);
                await Assert.That(row.Status).IsEqualTo(OutboxStatus.Processed);
            }
        }
        // 部分拒绝 → 内存零回写（保守契约）：全部入参保持内存快照形态，DB 真相由上方断言承载
        await Assert.That(leased[0].Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(leased[1].Status).IsEqualTo(OutboxStatus.Pending);
    }

    [Test]
    public async Task NullLeaseBatch_ThrowsInvalidOperationException()
    {
        await using var db = new TestBatchMarkOutboxDbContext(_options);
        var msg = new OutboxMessage { Id = PalUlid.New(), Type = "t", Payload = [] };

        await Assert.That(async () =>
            await db.MarkProcessedBatchAsync([msg], DateTimeOffset.UtcNow, CancellationToken.None).AsTask())
            .Throws<InvalidOperationException>();
    }

    /// <summary>测试上下文——直接继承生产 SqliteOutboxDbContext，无重写（对齐
    /// EfCoreOutboxRequeueSqliteTests 形态）。</summary>
    private sealed class TestBatchMarkOutboxDbContext(DbContextOptions<TestBatchMarkOutboxDbContext> options)
        : SqliteOutboxDbContext(options);
}
