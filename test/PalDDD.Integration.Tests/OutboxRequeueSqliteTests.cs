namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.Transactions;
using System.Globalization;
using PalUlid = ByteAether.Ulid.Ulid;

// ═══════════════════════════════════════════════════════════════
// 🔄 Outbox 死信重投递跨栈行为测试（IPalOutboxStore.RequeueDeadAsync）
// ═══════════════════════════════════════════════════════════════
// ITM-642：Transactions.Tests 的 OutboxRequeueTests 只测 InMemory 实现——
// ADR-011 的 Dead→Pending 语义在 Dapper/EFCore/PalORM 三栈零行为测试（实现漂移
// 只能靠偶然通过暴露）。本文件补 Dapper（SQLite）与 EFCore（SQLite）两栈，
// 逐条锁定：Dead→Pending 成功、非 Dead 拒绝返回 0、RetryCount 保留、retriedBy 空白抛。
// PalORM 栈由 PalDDD.PalORM.Tests 的多方言用例覆盖（需 Testcontainers，
// 不属本 SQLite 集成桶）。
// ═══════════════════════════════════════════════════════════════

/// <summary>Dapper 栈 RequeueDeadAsync 行为测试——raw ADO.NET 读写，避免触碰
/// Dapper 全局 TypeHandler/MatchNamesWithUnderscores 状态（对齐 DapperStoreTests 的隔离声明）。</summary>
public sealed class DapperOutboxRequeueSqliteTests
{
    private SqliteConnection _conn = null!;

    private const string OutboxDdl =
        "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload BLOB NOT NULL, " +
        "content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, " +
        "status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, " +
        "created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, " +
        "correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)";

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

    /// <summary>种子一行消息：直接写库，status/retry_count/error 可控。仅绑定 string/数值参数——
    /// 不触发 Dapper 的 Ulid/DateTimeOffset TypeHandler 全局注册（RequeueDeadAsync 自身
    /// 也只用 ToSqliteParameter 产 string，无需 TypeHandler）。</summary>
    private async Task SeedAsync(PalUlid id, OutboxStatus status, int retryCount, string? error = null)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO outbox_messages (id, type, payload, created_at, status, retry_count, error) " +
            "VALUES ($id, 'test.event', '[]', $created, $status, $retry, $error)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$status", (int)status);
        cmd.Parameters.AddWithValue("$retry", retryCount);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(long Status, long RetryCount, string? Error, string? NextAttempt)> ReadRowAsync(PalUlid id)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT status, retry_count, error, next_attempt_at FROM outbox_messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        await Assert.That(await reader.ReadAsync(CancellationToken.None)).IsTrue();
        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            await reader.IsDBNullAsync(2, CancellationToken.None) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, CancellationToken.None) ? null : reader.GetString(3));
    }

    private DapperOutboxStore CreateStore() => new(_conn, DapperDbType.Sqlite);

    [Test]
    public async Task RequeueDeadAsync_DeadMessage_FlipsToPendingAndPreservesRetryCount()
    {
        var store = CreateStore();
        var id = PalUlid.New();
        await SeedAsync(id, OutboxStatus.Dead, retryCount: 7, error: "original failure");
        var nextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1);

        var rows = await store.RequeueDeadAsync(id, nextAttempt, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(1);
        var row = await ReadRowAsync(id);
        await Assert.That(row.Status).IsEqualTo((long)OutboxStatus.Pending);
        await Assert.That(row.RetryCount).IsEqualTo(7L); // 失败历史保留，不重置
        await Assert.That(row.Error).Contains("requeued by ops-alice");
        await Assert.That(row.Error).DoesNotContain("original failure");
        // SQLite TEXT 往返：ToTimeParam 产 "O" 格式（tick 级保真），读回解析后须与传入
        // nextAttempt 等值（解析先例：PalOrmIdempotencyStoreTests 的租约时间戳往返断言）
        await Assert.That(row.NextAttempt).IsNotNull();
        await Assert.That(DateTimeOffset.Parse(row.NextAttempt!, CultureInfo.InvariantCulture))
            .IsEqualTo(nextAttempt);
    }

    [Test]
    public async Task RequeueDeadAsync_PendingMessage_ReturnsZero()
    {
        var store = CreateStore();
        var id = PalUlid.New();
        await SeedAsync(id, OutboxStatus.Pending, retryCount: 3);

        var rows = await store.RequeueDeadAsync(id, DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
        var row = await ReadRowAsync(id);
        await Assert.That(row.Status).IsEqualTo((long)OutboxStatus.Pending);
        await Assert.That(row.RetryCount).IsEqualTo(3L);
    }

    [Test]
    public async Task RequeueDeadAsync_ProcessedMessage_ReturnsZero()
    {
        var store = CreateStore();
        var id = PalUlid.New();
        await SeedAsync(id, OutboxStatus.Processed, retryCount: 1);

        var rows = await store.RequeueDeadAsync(id, DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
        await Assert.That((await ReadRowAsync(id)).Status).IsEqualTo((long)OutboxStatus.Processed);
    }

    [Test]
    public async Task RequeueDeadAsync_NonExistentId_ReturnsZero()
    {
        var store = CreateStore();

        var rows = await store.RequeueDeadAsync(PalUlid.New(), DateTimeOffset.UtcNow, "ops-alice", default);

        await Assert.That(rows).IsEqualTo(0);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task RequeueDeadAsync_BlankRetriedBy_ThrowsArgumentException(string retriedBy)
    {
        var store = CreateStore();
        var id = PalUlid.New();
        await SeedAsync(id, OutboxStatus.Dead, retryCount: 5);

        await Assert.That(async () =>
            await store.RequeueDeadAsync(id, DateTimeOffset.UtcNow, retriedBy, default).AsTask())
            .Throws<ArgumentException>();
    }
}

/// <summary>EFCore 栈 RequeueDeadAsync 行为测试——继承生产 <see cref="SqliteOutboxDbContext"/>，
/// RequeueDeadAsync 走真实 ExecuteUpdate SQL 翻译路径（InMemory provider 不支持 ExecuteUpdate）。</summary>
public sealed class EfCoreOutboxRequeueSqliteTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestSqliteOutboxDbContext> _options = null!;

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(cancellationToken);
        _options = new DbContextOptionsBuilder<TestSqliteOutboxDbContext>()
            .UseSqlite(_connection)
            .Options;
        await using var db = new TestSqliteOutboxDbContext(_options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    [After(Test)]
    public async Task CleanupAsync() => await _connection.DisposeAsync();

    private async Task<PalUlid> SeedAsync(OutboxStatus status, int retryCount, string? error = null)
    {
        var id = PalUlid.New();
        await using var db = new TestSqliteOutboxDbContext(_options);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = "test.event",
            Payload = [],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = status,
            RetryCount = retryCount,
            Error = error
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<OutboxMessage> ReadRowAsync(PalUlid id)
    {
        await using var db = new TestSqliteOutboxDbContext(_options);
        return await db.OutboxMessages.SingleAsync(m => m.Id == id);
    }

    [Test]
    public async Task RequeueDeadAsync_DeadMessage_FlipsToPendingAndPreservesRetryCount(CancellationToken cancellationToken)
    {
        var id = await SeedAsync(OutboxStatus.Dead, retryCount: 7, error: "original failure");
        var nextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1);

        await using var db = new TestSqliteOutboxDbContext(_options);
        var rows = await ((IPalOutboxStore)db).RequeueDeadAsync(id, nextAttempt, "ops-alice", cancellationToken);

        await Assert.That(rows).IsEqualTo(1);
        var loaded = await ReadRowAsync(id);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.RetryCount).IsEqualTo(7); // 失败历史保留，不重置
        await Assert.That(loaded.Error).Contains("requeued by ops-alice");
        await Assert.That(loaded.Error).DoesNotContain("original failure");
        // EFCore SQLite TEXT 往返（DateTimeOffset.Equals 按绝对时刻比较，偏移表示差异不误伤）
        await Assert.That(loaded.NextAttemptAt).IsNotNull();
        await Assert.That(loaded.NextAttemptAt.Value).IsEqualTo(nextAttempt);
        await Assert.That(loaded.ProcessedAt).IsNull();
        await Assert.That(loaded.LockedBy).IsNull();
        await Assert.That(loaded.LockedUntil).IsNull();
    }

    [Test]
    public async Task RequeueDeadAsync_PendingMessage_ReturnsZero(CancellationToken cancellationToken)
    {
        var id = await SeedAsync(OutboxStatus.Pending, retryCount: 3);

        await using var db = new TestSqliteOutboxDbContext(_options);
        var rows = await ((IPalOutboxStore)db).RequeueDeadAsync(id, DateTimeOffset.UtcNow, "ops-alice", cancellationToken);

        await Assert.That(rows).IsEqualTo(0);
        var loaded = await ReadRowAsync(id);
        await Assert.That(loaded.Status).IsEqualTo(OutboxStatus.Pending);
        await Assert.That(loaded.RetryCount).IsEqualTo(3);
    }

    [Test]
    public async Task RequeueDeadAsync_ProcessedMessage_ReturnsZero(CancellationToken cancellationToken)
    {
        var id = await SeedAsync(OutboxStatus.Processed, retryCount: 1);

        await using var db = new TestSqliteOutboxDbContext(_options);
        var rows = await ((IPalOutboxStore)db).RequeueDeadAsync(id, DateTimeOffset.UtcNow, "ops-alice", cancellationToken);

        await Assert.That(rows).IsEqualTo(0);
        await Assert.That((await ReadRowAsync(id)).Status).IsEqualTo(OutboxStatus.Processed);
    }

    [Test]
    public async Task RequeueDeadAsync_NonExistentId_ReturnsZero(CancellationToken cancellationToken)
    {
        await using var db = new TestSqliteOutboxDbContext(_options);

        var rows = await ((IPalOutboxStore)db).RequeueDeadAsync(
            PalUlid.New(), DateTimeOffset.UtcNow, "ops-alice", cancellationToken);

        await Assert.That(rows).IsEqualTo(0);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task RequeueDeadAsync_BlankRetriedBy_ThrowsArgumentException(string retriedBy)
    {
        var id = await SeedAsync(OutboxStatus.Dead, retryCount: 5);

        await using var db = new TestSqliteOutboxDbContext(_options);
        await Assert.That(async () =>
            await ((IPalOutboxStore)db).RequeueDeadAsync(id, DateTimeOffset.UtcNow, retriedBy, CancellationToken.None).AsTask())
            .Throws<ArgumentException>();
    }

    /// <summary>测试上下文——直接继承生产 <see cref="SqliteOutboxDbContext"/>，无重写，
    /// RequeueDeadAsync 走生产 ExecuteUpdate 路径（对齐 OutboxSqliteConcurrencyTests 的形态）。</summary>
    private sealed class TestSqliteOutboxDbContext(DbContextOptions<TestSqliteOutboxDbContext> options)
        : SqliteOutboxDbContext(options);
}
