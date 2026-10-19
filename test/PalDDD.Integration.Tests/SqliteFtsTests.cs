using Microsoft.Data.Sqlite;
using PalDDD.Dapper.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>SQLite FTS5 API 执行级测试：外部内容、独立索引、触发器同步、校验与查询片段。</summary>
public sealed class SqliteFtsTests
{
    [Test]
    public async Task CreateExternalContentIndex_EmitsContentAndTriggers()
    {
        var definition = SqliteFts.CreateExternalContentIndex(
            "events", "events_fts", "global_position", ["event_name", "payload"]);

        await Assert.That(definition.IsStandalone).IsFalse();
        await Assert.That(definition.MappingTableName).IsNull();
        await Assert.That(definition.TriggerNames.Count).IsEqualTo(3);
        await Assert.That(definition.Statements.Count).IsEqualTo(4);
        await Assert.That(definition.ToSql()).Contains("content='events'");
        await Assert.That(definition.ToSql()).Contains("content_rowid='global_position'");
        await Assert.That(definition.JoinClause()).Contains("rowid = \"s\".\"global_position\"");
    }

    [Test]
    public async Task CreateStandaloneIndex_UsesMappingTableAndCastToText()
    {
        var definition = SqliteFts.CreateStandaloneIndex(
            "outbox_messages", "outbox_messages_fts", "id",
            [new SqliteFtsColumn("type"), new SqliteFtsColumn("payload", CastToText: true)]);

        await Assert.That(definition.IsStandalone).IsTrue();
        await Assert.That(definition.MappingTableName).IsEqualTo("outbox_messages_fts_map");
        await Assert.That(definition.ToSql()).Contains("CAST(NEW.\"payload\" AS TEXT)");
        await Assert.That(definition.ToSql()).Contains("AUTOINCREMENT");
        await Assert.That(definition.JoinClause()).Contains("CAST(\"s\".\"id\" AS TEXT)");
        await Assert.That(definition.JoinClause()).Contains("fts_rowid");
    }

    [Test]
    public async Task ExternalContent_SyncsInsertUpdateDelete()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE events (global_position INTEGER PRIMARY KEY AUTOINCREMENT, event_name TEXT, payload TEXT)");
        var definition = SqliteFts.CreateExternalContentIndex(
            "events", "events_fts", "global_position", ["event_name", "payload"]);

        await SqliteFts.ValidateAsync(connection, definition);
        ExecuteAll(connection, definition.Statements);

        Execute(connection, "INSERT INTO events (event_name, payload) VALUES ('order.created', '{\"n\":1}'), ('user.registered', '{\"n\":2}')");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM events_fts WHERE events_fts MATCH 'order'")).IsEqualTo(1L);

        Execute(connection, "UPDATE events SET event_name = 'order.shipped' WHERE global_position = 1");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM events_fts WHERE events_fts MATCH 'created'")).IsEqualTo(0L);
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM events_fts WHERE events_fts MATCH 'shipped'")).IsEqualTo(1L);

        Execute(connection, "DELETE FROM events WHERE global_position = 1");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM events_fts WHERE events_fts MATCH 'shipped'")).IsEqualTo(0L);

        ExecuteAll(connection, definition.DropStatements());
        Execute(connection, "INSERT INTO events (event_name, payload) VALUES ('after.drop', '{}')");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM events")).IsEqualTo(2L);
    }

    [Test]
    public async Task Standalone_SyncsTextKeyAndBlobPayload()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT, payload BLOB)");
        var definition = SqliteFts.CreateOutboxIndex();

        await SqliteFts.ValidateAsync(connection, definition);
        ExecuteAll(connection, definition.Statements);

        InsertOutbox(connection, "01ABC", "Order.Created", """{"n":1}""");
        InsertOutbox(connection, "02XYZ", "User.Registered", """{"n":2}""");

        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts WHERE outbox_messages_fts MATCH 'Order'")).IsEqualTo(1L);
        await Assert.That(Count(connection, """
            SELECT COUNT(*) FROM outbox_messages s
            JOIN outbox_messages_fts_map m ON m.source_key = CAST(s.id AS TEXT)
            JOIN outbox_messages_fts f ON f.rowid = m.fts_rowid
            WHERE outbox_messages_fts MATCH 'Registered'
            """)).IsEqualTo(1L);

        Execute(connection, "UPDATE outbox_messages SET type = 'Order.Shipped' WHERE id = '01ABC'");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts WHERE outbox_messages_fts MATCH 'Created'")).IsEqualTo(0L);
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts WHERE outbox_messages_fts MATCH 'Shipped'")).IsEqualTo(1L);

        Execute(connection, "DELETE FROM outbox_messages WHERE id = '01ABC'");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts WHERE outbox_messages_fts MATCH 'Shipped'")).IsEqualTo(0L);
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts_map")).IsEqualTo(1L);

        ExecuteAll(connection, definition.DropStatements());
        Execute(connection, "INSERT INTO outbox_messages (id, type, payload) VALUES ('03POST', 'After.Drop', x'7B7D')");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages")).IsEqualTo(2L);
    }

    [Test]
    public async Task Standalone_RebuildRepopulatesIndex()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT, payload BLOB)");
        var definition = SqliteFts.CreateOutboxIndex();

        ExecuteAll(connection, definition.Statements);
        InsertOutbox(connection, "01ABC", "Order.Created", """{"n":1}""");
        Execute(connection, "DELETE FROM outbox_messages_fts");
        Execute(connection, "DELETE FROM outbox_messages_fts_map");
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts WHERE outbox_messages_fts MATCH 'Order'")).IsEqualTo(0L);

        ExecuteAll(connection, definition.RebuildStatements());
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts WHERE outbox_messages_fts MATCH 'Order'")).IsEqualTo(1L);
        await Assert.That(Count(connection, "SELECT COUNT(*) FROM outbox_messages_fts_map")).IsEqualTo(1L);
    }

    [Test]
    public async Task ValidateAsync_RejectsTextRowidForExternalContent()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE bad (id TEXT PRIMARY KEY, event_name TEXT)");
        var definition = SqliteFts.CreateExternalContentIndex(
            "bad", "bad_fts", "id", ["event_name"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await SqliteFts.ValidateAsync(connection, definition));
        await Assert.That(exception!.Message).Contains("INTEGER PRIMARY KEY");
    }

    [Test]
    public async Task ValidateAsync_RejectsMissingColumn()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE events (global_position INTEGER PRIMARY KEY, event_name TEXT)");
        var definition = SqliteFts.CreateExternalContentIndex(
            "events", "events_fts", "global_position", ["event_name", "payload"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await SqliteFts.ValidateAsync(connection, definition));
        await Assert.That(exception!.Message).Contains("payload");
    }

    [Test]
    public async Task TriggerNames_AvoidSanitizeCollision()
    {
        var first = SqliteFts.TriggerNames("a-b");
        var second = SqliteFts.TriggerNames("a.b");

        await Assert.That(first[0]).IsNotEqualTo(second[0]);
        await Assert.That(first[0]).Contains("_ai");
    }

    [Test]
    public async Task MatchParameter_ExecutesOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE events (global_position INTEGER PRIMARY KEY AUTOINCREMENT, event_name TEXT, payload TEXT)");
        var definition = SqliteFts.CreateEventLogIndex("events");
        ExecuteAll(connection, definition.Statements);
        Execute(connection, "INSERT INTO events (event_name, payload) VALUES ('order.created', '{\"n\":1}')");

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM events_fts WHERE {SqliteFts.MatchParameter("events_fts", "q")}";
        command.Parameters.AddWithValue("@q", "order");
        await Assert.That(Convert.ToInt64(command.ExecuteScalar())).IsEqualTo(1L);
    }

    [Test]
    public async Task RankHighlightAndSnippet_ExecuteOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE events (global_position INTEGER PRIMARY KEY AUTOINCREMENT, event_name TEXT, payload TEXT)");
        var definition = SqliteFts.CreateEventLogIndex("events");
        ExecuteAll(connection, definition.Statements);
        Execute(connection, "INSERT INTO events (event_name, payload) VALUES ('order.created', '{\"n\":1}'), ('order.shipped', '{\"n\":2}')");

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteFts.Rank("events_fts")},
                   {SqliteFts.Highlight("events_fts", 0)},
                   {SqliteFts.Snippet("events_fts", 0)}
            FROM events_fts
            WHERE events_fts MATCH 'order'
            {SqliteFts.OrderByRank("events_fts", 1.0, 1.0)}
            """;
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(1)).Contains("<b>order</b>");
        await Assert.That(reader.GetString(2)).Contains("order");
    }

    [Test]
    public async Task Guards_RejectInvalidArguments()
    {
        await Assert.That(() => SqliteFts.CreateExternalContentIndex("events", "events_fts", "global_position", []))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteFts.CreateExternalContentIndex("events", "events_fts", "global_position", ["event_name", "event_name"]))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteFts.MatchParameter("events_fts", "1bad"))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteFts.Highlight("events_fts", -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SqliteFts.Snippet("events_fts", 0, tokenCount: 0))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DropLegacyStatements_UsesOldTriggerNames()
    {
        var statements = SqliteFts.DropLegacyStatements("events_fts");
        await Assert.That(statements.Count).IsEqualTo(4);
        await Assert.That(statements[0]).Contains("events_fts_ai");
        await Assert.That(statements[3]).Contains("DROP TABLE IF EXISTS \"events_fts\"");
    }

    private static SqliteConnection OpenMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteAll(SqliteConnection connection, IReadOnlyList<string> statements)
    {
        foreach (var statement in statements)
            Execute(connection, statement);
    }

    private static void InsertOutbox(SqliteConnection connection, string id, string type, string payload)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO outbox_messages (id, type, payload) VALUES ($id, $type, $payload)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$payload", System.Text.Encoding.UTF8.GetBytes(payload));
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
