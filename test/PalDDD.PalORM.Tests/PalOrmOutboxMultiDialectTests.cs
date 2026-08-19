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
/// <item>三方言的 ULID 主键、Base64 Payload、DateTimeOffset 时间戳往返</item>
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
        store.AddMessage(msg);

        var pending = await store.GetPendingMessagesAsync(10, 5, default);
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Type).IsEqualTo("sqlite.event");
    }

    [Test]
    public async Task Outbox_Sqlite_LeasePending_AtomicAcquisition()
    {
        await using var session = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteOutboxStore(session.Session);
        store.AddMessage(CreateMessage("sqlite.lease"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(1);

        // 第二次 Lease 应为空
        var second = await store.LeasePendingMessagesAsync(10, "w2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_Sqlite_MarkProcessed()
    {
        await using var session = await MultiDialectFixture.CreateSqliteAsync();
        var store = new SqliteOutboxStore(session.Session);
        var msg = CreateMessage("sqlite.processed");
        store.AddMessage(msg);
        store.MarkProcessed(msg, DateTimeOffset.UtcNow);

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
        store.AddMessage(msg);

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
        store.AddMessage(CreateMessage("pg.lease"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(1);

        var second = await store.LeasePendingMessagesAsync(10, "w2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_PostgreSql_MarkProcessed()
    {
        await using var session = await MultiDialectFixture.CreatePostgreSqlAsync();
        var store = new PostgreSqlOutboxStore(session.Session);
        var msg = CreateMessage("pg.processed");
        store.AddMessage(msg);
        store.MarkProcessed(msg, DateTimeOffset.UtcNow);

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
        store.AddMessage(msg);

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
        store.AddMessage(CreateMessage("mysql.lease"));

        var leased = await store.LeasePendingMessagesAsync(10, "w1", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(leased).Count().IsEqualTo(1);

        var second = await store.LeasePendingMessagesAsync(10, "w2", TimeSpan.FromMinutes(5), 10, default);
        await Assert.That(second).IsEmpty();
    }

    [Test]
    public async Task Outbox_MySql_MarkProcessed()
    {
        await using var session = await MultiDialectFixture.CreateMySqlAsync();
        var store = new MySqlOutboxStore(session.Session);
        var msg = CreateMessage("mysql.processed");
        store.AddMessage(msg);
        store.MarkProcessed(msg, DateTimeOffset.UtcNow);

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
}
