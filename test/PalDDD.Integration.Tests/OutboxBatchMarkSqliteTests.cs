using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;
using PalDDD.PalORM.Sqlite;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧾 R4/P0-2（ADR-027）终态批量标记跨栈 SQL 正确性测试（SQLite 真库，本地可跑）
// ═══════════════════════════════════════════════════════════════
// Dapper 栈、EF Core 栈与 PalORM 栈的 IBatchMarkOutboxStore.MarkProcessedBatchAsync：
// 全批命中（行状态断言 raw 直查）／fencing 部分拒绝（被重置行不动、其余标记、内存零回写）／
// 未租约批防御抛／混租约批防御抛／全命中内存六字段回写；PalORM 腿另锁定 RetryCount
// 分组（两组两语句）与 400 行分块边界。
// 处理器集成（批序/冲刷时点/失败镜像）由 Transactions.Tests.OutboxBatchMarkTests 承载；
// PG/MySQL 方言的批量 SQL 由 PalDDD.PalORM.Tests.PalOrmOutboxMultiDialectTests 的方言
// 测试锁定（本地 skip，CI dialect-probe 车道执行；until 编码三方言探针实证见 ADR-027）。
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

/// <summary>PalORM 栈批量标记测试——生产 SqliteOutboxStore 直用（ADR-027 延后段 2026-10-16
/// 清算：until 编码三方言探针实证后实施，见 PalOrmOutboxStore.MarkProcessedBatchAsync remarks）。
/// DDL 经 raw 连接执行——PalORM 的 ExecuteAsync(FormattableString) 会把插值字符串参数化为
/// @pN，整句 DDL 退化（MultiDialectFixture.ApplySchemaAsync 同款教训）。</summary>
public sealed class PalOrmBatchMarkSqliteTests
{
    private const string OutboxDdl =
        "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload BLOB NOT NULL, " +
        "content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, " +
        "status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, " +
        "created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, " +
        "correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)";

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(DbOptions.Development("Data Source=:memory:"));
        var raw = session.GetRawConnection();
        if (raw.State != System.Data.ConnectionState.Open) await raw.OpenAsync();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = OutboxDdl;
        await cmd.ExecuteNonQueryAsync();
        return session;
    }

    private static async Task SeedAsync(SqliteOutboxStore store, string type, int count, int retryCount = 0)
    {
        var messages = Enumerable.Range(0, count)
            .Select(i => new OutboxMessage { Id = PalUlid.New(), Type = $"{type}.{i}", Payload = [], RetryCount = retryCount })
            .ToList();
        await store.AddMessagesAsync(messages);
    }

    /// <summary>raw 行计数断言（避免 ScalarAsync 对 NULL 列的映射不确定性，用聚合口径）。
    /// predicate 以字面量拼进 format 文本、id 走 {0} 参数孔——PD18 教训：字符串变量作插值孔
    /// 会被 PalORM 整体参数化为 @pN（谓词退化恒假）。</summary>
    private static async Task<long> CountAsync(DataSession<SqliteProvider> session, string id, string predicate) =>
        await session.ScalarAsync<long>(
            FormattableStringFactory.Create("SELECT COUNT(*) FROM outbox_messages WHERE id = {0} AND " + predicate, id));

    [Test]
    public async Task LeasedBatch_AllMarked_RowStateVerified()
    {
        await using var session = await CreateSessionAsync();
        var store = new SqliteOutboxStore(session);
        await SeedAsync(store, "batchmark.all", 5);

        var leased = await store.LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, default);
        await Assert.That(leased).Count().IsEqualTo(5);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);

        await Assert.That(affected).IsEqualTo(5);
        foreach (var m in leased)
        {
            var id = m.Id.ToString();
            await Assert.That(await CountAsync(session, id, $"status = {(int)OutboxStatus.Processed}")).IsEqualTo(1);
            // 租约显式释放 + processed_at 落库（六字段 SET 的 raw 核对）
            await Assert.That(await CountAsync(session, id, "locked_by IS NULL AND locked_until IS NULL AND processed_at IS NOT NULL AND error IS NULL AND next_attempt_at IS NULL")).IsEqualTo(1);
        }
        // 全命中 → 内存六字段回写（ADR-026 ② 契约）
        foreach (var m in leased)
        {
            await Assert.That(m.Status).IsEqualTo(OutboxStatus.Processed);
            await Assert.That(m.ProcessedAt).IsNotNull();
            await Assert.That(m.Error).IsNull();
            await Assert.That(m.NextAttemptAt).IsNull();
            await Assert.That(m.LockedBy).IsNull();
            await Assert.That(m.LockedUntil).IsNull();
        }
        // 终态行不再可租（二次租约为空）
        var second = await store.LeasePendingMessagesAsync(10, "worker-b", TimeSpan.FromMinutes(1), 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task FencingPartialReject_RequeuedRowUntouched()
    {
        await using var session = await CreateSessionAsync();
        var store = new SqliteOutboxStore(session);
        await SeedAsync(store, "batchmark.fence", 3);

        var leased = await store.LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, default);
        await Assert.That(leased).Count().IsEqualTo(3);

        // 首条被 ReleaseForRetry 推进（DB：retry_count+1、租约清空；内存成功回写清租约字段）——
        // 恢复内存租约快照，模拟真实僵尸 worker（持重置前冻结快照，DB 行已被推进）
        var staleOwner = leased[0].LockedBy;
        var staleUntil = leased[0].LockedUntil;
        await store.ReleaseForRetryAsync(leased[0], "simulated failure", DateTimeOffset.UtcNow.AddSeconds(1), default);
        leased[0].LockedBy = staleOwner;
        leased[0].LockedUntil = staleUntil;

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);

        // 首条双守卫拒（retry_count 快照 + 租约 token），其余两条标记
        await Assert.That(affected).IsEqualTo(2);
        var first = leased[0].Id.ToString();
        await Assert.That(await CountAsync(session, first, $"status = {(int)OutboxStatus.Pending} AND retry_count = 1")).IsEqualTo(1);
        // 陈旧写未触碰租约列（重投路径已清空，保持空）
        await Assert.That(await CountAsync(session, first, "locked_by IS NULL AND locked_until IS NULL")).IsEqualTo(1);
        for (int i = 1; i < 3; i++)
            await Assert.That(await CountAsync(session, leased[i].Id.ToString(), $"status = {(int)OutboxStatus.Processed}")).IsEqualTo(1);
        // 部分拒绝 → 内存零回写（保守契约：无法逐行定位被拒者）
        foreach (var m in leased)
            await Assert.That(m.Status).IsEqualTo(OutboxStatus.Pending);
    }

    [Test]
    public async Task RetryCountGroups_TwoGroupsBothMarked()
    {
        await using var session = await CreateSessionAsync();
        var store = new SqliteOutboxStore(session);
        // 两组不同 RetryCount（0 与 2）——分组单语句两发，各命中本组
        await SeedAsync(store, "batchmark.g0", 2, retryCount: 0);
        await SeedAsync(store, "batchmark.g2", 2, retryCount: 2);

        var leased = await store.LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, default);
        await Assert.That(leased).Count().IsEqualTo(4);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);

        await Assert.That(affected).IsEqualTo(4);
        await Assert.That(await session.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM outbox_messages WHERE status = {(int)OutboxStatus.Processed}")).IsEqualTo(4);
    }

    [Test]
    public async Task ChunkBoundary_Above400Rows_TwoChunksAllMarked()
    {
        await using var session = await CreateSessionAsync();
        var store = new SqliteOutboxStore(session);
        // 450 行 > 组内 400 行分块上限 → 两发（400 + 50），SQLite 999 参数上限防御路径
        await SeedAsync(store, "batchmark.chunk", 450);

        var leased = await store.LeasePendingMessagesAsync(500, "worker-a", TimeSpan.FromMinutes(1), 10, default);
        await Assert.That(leased).Count().IsEqualTo(450);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);

        await Assert.That(affected).IsEqualTo(450);
        await Assert.That(await session.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM outbox_messages WHERE status = {(int)OutboxStatus.Processed}")).IsEqualTo(450);
    }

    [Test]
    public async Task NullLeaseBatch_ThrowsInvalidOperationException()
    {
        await using var session = await CreateSessionAsync();
        var store = new SqliteOutboxStore(session);
        var msg = new OutboxMessage { Id = PalUlid.New(), Type = "t", Payload = [] }; // LockedBy/LockedUntil = null

        await Assert.That(async () =>
            await store.MarkProcessedBatchAsync([msg], DateTimeOffset.UtcNow, CancellationToken.None).AsTask())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task MixedLeaseBatch_ThrowsInvalidOperationException()
    {
        await using var session = await CreateSessionAsync();
        var store = new SqliteOutboxStore(session);
        await SeedAsync(store, "batchmark.mixed", 2);
        // 两次租约（时长不同 → until 必异）——混批防御
        var first = await store.LeasePendingMessagesAsync(1, "worker-a", TimeSpan.FromMinutes(1), 10, default);
        var second = await store.LeasePendingMessagesAsync(1, "worker-a", TimeSpan.FromMinutes(2), 10, default);

        await Assert.That(async () =>
            await store.MarkProcessedBatchAsync([first[0], second[0]], DateTimeOffset.UtcNow, CancellationToken.None).AsTask())
            .Throws<InvalidOperationException>();
    }
}
