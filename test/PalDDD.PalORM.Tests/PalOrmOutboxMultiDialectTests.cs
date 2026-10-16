using ByteAether.Ulid;
using PalDDD.PalORM.MySql;
using PalDDD.PalORM.PostgreSql;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Outbox Store 跨方言集成测试 —— 验证 PG/MySQL/SQLite 三方言下的行为一致性。
/// <para>
/// <b>核心验证点</b>：
/// <list type="bullet">
/// <item>PG/SQLite 的 <c>UPDATE...RETURNING</c> 单语句原子租约</item>
/// <item>MySQL 的 <c>UPDATE + SELECT</c> 两步租约（无 RETURNING）</item>
/// <item>三方言的 ULID 主键、二进制 Payload（BLOB/BYTEA/LONGBLOB，070b42f 原生化）、DateTimeOffset 时间戳往返</item>
/// <item>三方言的事务 Commit/Rollback</item>
/// </list>
/// </para>
/// <para><b>前置条件</b>：Docker 运行（PG/MySQL 经 Testcontainers 启动）。</para>
/// </summary>
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmOutboxMultiDialectTests
{
    // ─── SQLite ────────────────────────────────────────────────

    [Test]
    public async Task Outbox_Sqlite_AddMessage_ThenGetPending()
    {
        await using var session = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteOutboxStore(session.Session);

        var msg = CreateMessage("sqlite.event");
        await store.AddMessageAsync(msg);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Type).IsEqualTo("sqlite.event");
    }

    [Test]
    public async Task Outbox_Sqlite_LeasePending_AtomicAcquisition()
    {
        await using var session = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteOutboxStore(session.Session);
        var before = DateTimeOffset.UtcNow;
        await store.AddMessageAsync(CreateMessage("sqlite.lease"));

        var leaseDuration = TimeSpan.FromMinutes(5);
        var leased = await store.LeasePendingMessagesAsync(10, "w1", leaseDuration, 10, default);
        await Assert.That(leased).Count().IsEqualTo(1);
        // ITM-245：时间戳往返守护网——created_at 物化读回 + lease_until（减租约时长=租约起点）
        // 与写入时刻绝对差值须在窗口内（时区漂移回归必红）
        await MultiDialectFixture.AssertTimestampRoundTrip(leased[0].CreatedAt, before, "CreatedAt");
        await MultiDialectFixture.AssertTimestampRoundTrip(
            leased[0].LockedUntil!.Value - leaseDuration, before, "LockedUntil");

        // 第二次 Lease 应为空
        var second = await store.LeasePendingMessagesAsync(10, "w2", leaseDuration, 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_Sqlite_MarkProcessed()
    {
        await using var session = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteOutboxStore(session.Session);
        var msg = CreateMessage("sqlite.processed");
        await store.AddMessageAsync(msg);
        await store.MarkProcessedAsync(msg, DateTimeOffset.UtcNow);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
    }

    // ─── PostgreSQL ─────────────────────────────────────────────

    [Test]
    public async Task Outbox_PostgreSql_AddMessage_ThenGetPending()
    {
        await using var session = await MultiDialectFixture.CreatePostgreSqlAsync();
        var store = new PostgreSqlOutboxStore(session.Session);

        var msg = CreateMessage("pg.event");
        await store.AddMessageAsync(msg);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Type).IsEqualTo("pg.event");
    }

    [Test]
    public async Task Outbox_PostgreSql_LeasePending_AtomicAcquisition_Returning()
    {
        // PG 走 RETURNING 单语句路径 —— 验证 SupportsReturningClause=true 分支
        await using var session = await MultiDialectFixture.CreatePostgreSqlAsync();
        var store = new PostgreSqlOutboxStore(session.Session);
        var before = DateTimeOffset.UtcNow;
        await store.AddMessageAsync(CreateMessage("pg.lease"));

        var leaseDuration = TimeSpan.FromMinutes(5);
        var leased = await store.LeasePendingMessagesAsync(10, "w1", leaseDuration, 10, default);
        await Assert.That(leased).Count().IsEqualTo(1);
        // ITM-245：时间戳往返守护网（TIMESTAMPTZ 路径）
        await MultiDialectFixture.AssertTimestampRoundTrip(leased[0].CreatedAt, before, "CreatedAt");
        await MultiDialectFixture.AssertTimestampRoundTrip(
            leased[0].LockedUntil!.Value - leaseDuration, before, "LockedUntil");

        var second = await store.LeasePendingMessagesAsync(10, "w2", leaseDuration, 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_PostgreSql_MarkProcessed()
    {
        await using var session = await MultiDialectFixture.CreatePostgreSqlAsync();
        var store = new PostgreSqlOutboxStore(session.Session);
        var msg = CreateMessage("pg.processed");
        await store.AddMessageAsync(msg);
        await store.MarkProcessedAsync(msg, DateTimeOffset.UtcNow);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
    }

    // ─── MySQL ─────────────────────────────────────────────────

    [Test]
    public async Task Outbox_MySql_AddMessage_ThenGetPending()
    {
        await using var session = await MultiDialectFixture.CreateMySqlAsync();
        var store = new MySqlOutboxStore(session.Session);

        var msg = CreateMessage("mysql.event");
        await store.AddMessageAsync(msg);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Type).IsEqualTo("mysql.event");
    }

    [Test]
    public async Task Outbox_MySql_LeasePending_TwoStepAcquisition()
    {
        // MySQL 走两步 UPDATE + SELECT 路径（无 RETURNING）—— 验证 SupportsReturningClause=false 分支
        await using var session = await MultiDialectFixture.CreateMySqlAsync();
        var store = new MySqlOutboxStore(session.Session);
        var before = DateTimeOffset.UtcNow;
        await store.AddMessageAsync(CreateMessage("mysql.lease"));

        var leaseDuration = TimeSpan.FromMinutes(5);
        var leased = await store.LeasePendingMessagesAsync(10, "w1", leaseDuration, 10, default);
        await Assert.That(leased).Count().IsEqualTo(1);
        // ITM-245：时间戳往返守护网（DATETIME(6) 无时区路径——漂移高风险区）
        await MultiDialectFixture.AssertTimestampRoundTrip(leased[0].CreatedAt, before, "CreatedAt");
        await MultiDialectFixture.AssertTimestampRoundTrip(
            leased[0].LockedUntil!.Value - leaseDuration, before, "LockedUntil");

        var second = await store.LeasePendingMessagesAsync(10, "w2", leaseDuration, 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_MySql_MarkProcessed()
    {
        await using var session = await MultiDialectFixture.CreateMySqlAsync();
        var store = new MySqlOutboxStore(session.Session);
        var msg = CreateMessage("mysql.processed");
        await store.AddMessageAsync(msg);
        await store.MarkProcessedAsync(msg, DateTimeOffset.UtcNow);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
    }

    private static OutboxMessage CreateMessage(string type) => new()
    {
        Id = Ulid.New(),
        Type = type,
        Payload = System.Text.Encoding.UTF8.GetBytes("""{"v":1}"""),
        ContentType = "application/json",
        SchemaVersion = 1,
    };

    // ─── IBatchMarkOutboxStore 批量标记（ADR-027 延后段 2026-10-16 清算）───
    // until 编码三方言探针实证（SQLite 本地 + PG/MySQL wslc 外部实例：正腿命中全批、
    // until±1s/换 owner/retry+1/已终态四负腿全拒）后实施。本组为 CI dialect-probe
    // 车道锁：正腿（回读 until 批量命中）+ 负腿（until 前移后旧快照全拒=谓词选择性）。
    // SQLite 本地执行；PG/MySQL 经 Testcontainers（本地无 Docker 时按设计跳过）。

    [Test]
    public async Task Outbox_Sqlite_BatchMark_LeasedBatch_AllMarked()
    {
        await using var session = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteOutboxStore(session.Session);
        for (int i = 0; i < 3; i++) await store.AddMessageAsync(CreateMessage($"sqlite.batchmark.{i}"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(3);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);
        await Assert.That(affected).IsEqualTo(3);

        foreach (var m in leased) await Assert.That(m.Status).IsEqualTo(OutboxStatus.Processed);
        var second = await store.LeasePendingMessagesAsync(10, "w2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty(); // 终态行不可再租
    }

    [Test]
    public async Task Outbox_Sqlite_BatchMark_StaleToken_AllRejected()
    {
        await using var session = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteOutboxStore(session.Session);
        for (int i = 0; i < 3; i++) await store.AddMessageAsync(CreateMessage($"sqlite.batchmark.stale.{i}"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(3);

        // 同 owner 重租（仅 until 前移 = 新 token；DB 推进，内存持旧快照）——v74 P1 until 腿的批量版
        var newUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        foreach (var m in leased)
            await session.Session.ExecuteAsync(
                $"UPDATE outbox_messages SET locked_until = {newUntil} WHERE id = {m.Id.ToString()}", default);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);
        await Assert.That(affected).IsEqualTo(0); // 旧 until 快照全拒（谓词有选择性，非恒真）

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty(); // 行仍被新 token 租约持有
        foreach (var m in leased) await Assert.That(m.Status).IsEqualTo(OutboxStatus.Pending); // 零回写
    }

    [Test]
    public async Task Outbox_PostgreSql_BatchMark_LeasedBatch_AllMarked()
    {
        await using var session = await MultiDialectFixture.CreatePostgreSqlAsync();
        var store = new PostgreSqlOutboxStore(session.Session);
        for (int i = 0; i < 3; i++) await store.AddMessageAsync(CreateMessage($"pg.batchmark.{i}"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(3);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);
        await Assert.That(affected).IsEqualTo(3);

        foreach (var m in leased) await Assert.That(m.Status).IsEqualTo(OutboxStatus.Processed);
        var second = await store.LeasePendingMessagesAsync(10, "w2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_PostgreSql_BatchMark_StaleToken_AllRejected()
    {
        await using var session = await MultiDialectFixture.CreatePostgreSqlAsync();
        var store = new PostgreSqlOutboxStore(session.Session);
        for (int i = 0; i < 3; i++) await store.AddMessageAsync(CreateMessage($"pg.batchmark.stale.{i}"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(3);

        var newUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        foreach (var m in leased)
            await session.Session.ExecuteAsync(
                $"UPDATE outbox_messages SET locked_until = {newUntil} WHERE id = {m.Id.ToString()}", default);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);
        await Assert.That(affected).IsEqualTo(0);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
        foreach (var m in leased) await Assert.That(m.Status).IsEqualTo(OutboxStatus.Pending);
    }

    [Test]
    public async Task Outbox_MySql_BatchMark_LeasedBatch_AllMarked()
    {
        await using var session = await MultiDialectFixture.CreateMySqlAsync();
        var store = new MySqlOutboxStore(session.Session);
        for (int i = 0; i < 3; i++) await store.AddMessageAsync(CreateMessage($"mysql.batchmark.{i}"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(3);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);
        await Assert.That(affected).IsEqualTo(3);

        foreach (var m in leased) await Assert.That(m.Status).IsEqualTo(OutboxStatus.Processed);
        var second = await store.LeasePendingMessagesAsync(10, "w2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_MySql_BatchMark_StaleToken_AllRejected()
    {
        await using var session = await MultiDialectFixture.CreateMySqlAsync();
        var store = new MySqlOutboxStore(session.Session);
        for (int i = 0; i < 3; i++) await store.AddMessageAsync(CreateMessage($"mysql.batchmark.stale.{i}"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(3);

        var newUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        foreach (var m in leased)
            await session.Session.ExecuteAsync(
                $"UPDATE outbox_messages SET locked_until = {newUntil} WHERE id = {m.Id.ToString()}", default);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, default);
        await Assert.That(affected).IsEqualTo(0);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).IsEmpty();
        foreach (var m in leased) await Assert.That(m.Status).IsEqualTo(OutboxStatus.Pending);
    }
}
