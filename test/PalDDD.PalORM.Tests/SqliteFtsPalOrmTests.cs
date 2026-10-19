using System.Runtime.CompilerServices;
using System.Text;
using ByteAether.Ulid;
using PalDDD.EventLog;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.PalORM.Tests;

/// <summary>PalORM SQLite FTS5/JSON1 API 执行级测试：DDL 生命周期与原生 SQL 配方。</summary>
public sealed class SqliteFtsPalOrmTests
{
    [Test]
    public async Task EnsureEventLogIndex_SearchAfterAppend()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var definition = SqliteFts.CreateEventLogIndex();
        await session.EnsureIndexAsync(definition);

        var log = new SqliteEventLog(session);
        await log.AppendAsync(
            "stream-1",
            ExpectedStreamVersion.NoStream,
            [MultiDialectTestData.CreateEventData("order.created")],
            default);

        var format = "SELECT COUNT(*) FROM events s "
            + definition.JoinClause()
            + " WHERE "
            + SqliteFts.MatchExpression(definition.IndexName)
            + " {0}";
        var sql = FormattableStringFactory.Create(format, "order");

        var count = await session.ScalarAsync<long>(sql);
        await Assert.That(count).IsEqualTo(1L);
    }

    [Test]
    public async Task EnsureOutboxIndex_SearchesBlobPayload()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var definition = SqliteFts.CreateOutboxIndex();
        await session.EnsureIndexAsync(definition);

        var store = new SqliteOutboxStore(session);
        await store.AddMessageAsync(CreateOutboxMessage(
            "order.created",
            """{"Type":"Order.Created","note":"urgent delivery"}"""));

        var format = "SELECT COUNT(*) FROM outbox_messages s "
            + definition.JoinClause()
            + " WHERE "
            + SqliteFts.MatchExpression(definition.IndexName)
            + " {0}";
        var sql = FormattableStringFactory.Create(format, "urgent");

        var count = await session.ScalarAsync<long>(sql);
        await Assert.That(count).IsEqualTo(1L);
    }

    [Test]
    public async Task IndexLifecycle_ExistsRebuildOptimizeDrop()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var definition = SqliteFts.CreateEventLogIndex();

        await Assert.That(await session.IndexExistsAsync(definition.IndexName)).IsFalse();
        await session.EnsureIndexAsync(definition);
        await Assert.That(await session.IndexExistsAsync(definition.IndexName)).IsTrue();

        await session.RebuildIndexAsync(definition);
        await session.OptimizeIndexAsync(definition);
        await session.DropIndexAsync(definition);
        await Assert.That(await session.IndexExistsAsync(definition.IndexName)).IsFalse();
        await Assert.That(await session.IndexExistsAsync(definition.MappingTableName!)).IsFalse();
    }

    [Test]
    public async Task EnsureIndex_RejectsMissingSourceTable()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var definition = SqliteFts.CreateEventLogIndex("missing_events");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.EnsureIndexAsync(definition));
        await Assert.That(exception!.Message).Contains("missing_events");
    }

    [Test]
    public async Task OutboxJsonQuery_WithFormattableStringRecipe()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        await store.AddMessageAsync(CreateOutboxMessage(
            "Order.Created",
            """{"Type":"Order.Created","n":7}"""));

        var predicate = SqliteJson.Extract("payload", "Type") + " = {0}";
        var sql = FormattableStringFactory.Create(
            "SELECT COUNT(*) FROM outbox_messages WHERE " + predicate,
            "Order.Created");

        var count = await session.ScalarAsync<long>(sql);
        await Assert.That(count).IsEqualTo(1L);
    }

    [Test]
    public async Task OutboxJsonQuery_WithRawCommandAndParameter()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var store = new SqliteOutboxStore(session);
        await store.AddMessageAsync(CreateOutboxMessage(
            "Order.Created",
            """{"Type":"Order.Created","n":7}"""));

        using var command = session.GetRawConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox_messages WHERE " + SqliteJson.EqualsParameter("payload", "type", "Type");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@type";
        parameter.Value = "Order.Created";
        command.Parameters.Add(parameter);

        var count = Convert.ToInt64(command.ExecuteScalar());
        await Assert.That(count).IsEqualTo(1L);
    }

    private static OutboxMessage CreateOutboxMessage(string type, string payload) => new()
    {
        Id = Ulid.New(),
        Type = type,
        Payload = Encoding.UTF8.GetBytes(payload),
        ContentType = "application/json",
        SchemaVersion = 1,
    };
}
