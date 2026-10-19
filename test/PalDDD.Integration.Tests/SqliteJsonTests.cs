using Microsoft.Data.Sqlite;
using PalDDD.Dapper.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>SQLite JSON1 API 执行级测试：引号路径、BLOB 输入、参数化写入与聚合。</summary>
public sealed class SqliteJsonTests
{
    [Test]
    public async Task Path_QuotesSpecialKeys()
    {
        var path = SqliteJson.Path("a.b", "c[0]", "d e", "f\"g", "h\\i");
        await Assert.That(path).IsEqualTo("'$.\"a.b\".\"c[0]\".\"d e\".\"f\\\"g\".\"h\\\\i\"'");
    }

    [Test]
    public async Task Extract_SpecialKeys_ExecutesOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE t (payload TEXT)");
        Execute(connection, """INSERT INTO t (payload) VALUES ('{"a.b":1,"c[0]":2,"d e":3,"f\"g":4,"h\\i":5}')""");

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteJson.Extract("payload", "a.b")},
                   {SqliteJson.Extract("payload", "c[0]")},
                   {SqliteJson.Extract("payload", "d e")},
                   {SqliteJson.Extract("payload", "f\"g")},
                   {SqliteJson.Extract("payload", "h\\i")}
            FROM t
            """;
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt64(0)).IsEqualTo(1L);
        await Assert.That(reader.GetInt64(1)).IsEqualTo(2L);
        await Assert.That(reader.GetInt64(2)).IsEqualTo(3L);
        await Assert.That(reader.GetInt64(3)).IsEqualTo(4L);
        await Assert.That(reader.GetInt64(4)).IsEqualTo(5L);
    }

    [Test]
    public async Task ExtractTextAndExtractJson_ExecuteOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE t (payload TEXT)");
        Execute(connection, """INSERT INTO t (payload) VALUES ('{"a":{"b":7}}')""");

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteJson.ExtractText("payload", "a", "b")},
                   {SqliteJson.ExtractJson("payload", "a")},
                   {SqliteJson.Type("payload", "a")},
                   {SqliteJson.IsValid("payload")},
                   {SqliteJson.HasKey("payload", "a")}
            FROM t
            """;
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt64(0)).IsEqualTo(7L);
        await Assert.That(reader.GetString(1)).IsEqualTo("""{"b":7}""");
        await Assert.That(reader.GetString(2)).IsEqualTo("object");
        await Assert.That(reader.GetInt64(3)).IsEqualTo(1L);
        await Assert.That(reader.GetInt64(4)).IsEqualTo(1L);
    }

    [Test]
    public async Task ArrayLengthEachAndTree_ExecuteOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE t (payload TEXT)");
        Execute(connection, """INSERT INTO t (payload) VALUES ('{"arr":[1,2,3]}')""");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {SqliteJson.ArrayLength("payload", "arr")},
                       (SELECT COUNT(*) FROM {SqliteJson.Each("payload", "arr")}),
                       (SELECT COUNT(*) FROM {SqliteJson.Tree("payload", "arr")})
                FROM t
                """;
            using var reader = command.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(reader.GetInt64(0)).IsEqualTo(3L);
            await Assert.That(reader.GetInt64(1)).IsEqualTo(3L);
            await Assert.That(reader.GetInt64(2)).IsGreaterThanOrEqualTo(3L);
        }
    }

    [Test]
    public async Task EmptyPath_UsesRootDocument()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE t (payload TEXT)");
        Execute(connection, """INSERT INTO t (payload) VALUES ('{"a":1}')""");

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SqliteJson.Extract("payload")}, {SqliteJson.Type("payload")}, {SqliteJson.ArrayLength("payload")} FROM t";
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("""{"a":1}""");
        await Assert.That(reader.GetString(1)).IsEqualTo("object");
        await Assert.That(reader.GetInt64(2)).IsEqualTo(0L);
    }

    [Test]
    public async Task BlobJsonInput_ExecutesOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE t (payload BLOB)");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (payload) VALUES ($payload)";
            insert.Parameters.AddWithValue("$payload", System.Text.Encoding.UTF8.GetBytes("""{"Type":"Order.Created","n":7}"""));
            insert.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SqliteJson.Extract("payload", "Type")}, {SqliteJson.Extract("payload", "n")} FROM t";
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("Order.Created");
        await Assert.That(reader.GetInt64(1)).IsEqualTo(7L);
    }

    [Test]
    public async Task ArrayAndObjectLiteral_EscapeValues()
    {
        await Assert.That(SqliteJson.ArrayLiteral("a'b", "c")).IsEqualTo("json_array('a''b', 'c')");
        await Assert.That(SqliteJson.ObjectLiteral(("k1", "v1"), ("k2", "v'2"))).IsEqualTo("json_object('k1', 'v1', 'k2', 'v''2')");
        await Assert.That(SqliteJson.ArrayLiteral()).IsEqualTo("json_array()");
        await Assert.That(SqliteJson.ObjectLiteral()).IsEqualTo("json_object()");
    }

    [Test]
    public async Task ParameterArraysAndObjects_BindAndExecute()
    {
        using var connection = OpenMemory();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SqliteJson.ArrayParameters("a", "@b")}, {SqliteJson.ObjectParameters(("k", "v"))}";
        command.Parameters.AddWithValue("@a", 1);
        command.Parameters.AddWithValue("@b", 2);
        command.Parameters.AddWithValue("@v", "x");
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("[1,2]");
        await Assert.That(reader.GetString(1)).IsEqualTo("""{"k":"x"}""");
    }

    [Test]
    public async Task SetInsertReplacePatchRemove_ExecuteOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE t (payload TEXT)");
        Execute(connection, """INSERT INTO t (payload) VALUES ('{"a":1}')""");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SqliteJson.SetParameter("payload", "v", "a")} FROM t";
            command.Parameters.AddWithValue("@v", "x");
            await Assert.That(Convert.ToString(command.ExecuteScalar())).Contains("\"a\":\"x\"");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SqliteJson.InsertParameter("payload", "v", "b")} FROM t";
            command.Parameters.AddWithValue("@v", "y");
            await Assert.That(Convert.ToString(command.ExecuteScalar())).Contains("\"b\":\"y\"");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SqliteJson.ReplaceParameter("payload", "v", "b")} FROM t";
            command.Parameters.AddWithValue("@v", "z");
            await Assert.That(Convert.ToString(command.ExecuteScalar())).Contains("\"a\":1");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SqliteJson.PatchParameter("payload", "p")} FROM t";
            command.Parameters.AddWithValue("@p", """{"c":3}""");
            await Assert.That(Convert.ToString(command.ExecuteScalar())).Contains("\"c\":3");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SqliteJson.Remove("payload", "a")} FROM t";
            await Assert.That(Convert.ToString(command.ExecuteScalar())).DoesNotContain("\"a\"");
        }
    }

    [Test]
    public async Task QuoteLiteralAndAggregates_ExecuteOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE t (k TEXT, v TEXT)");
        Execute(connection, "INSERT INTO t (k, v) VALUES ('a', 'x'), ('b', 'y')");

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteJson.QuoteLiteral("a\"b")},
                   (SELECT {SqliteJson.GroupArray("k")} FROM t),
                   (SELECT {SqliteJson.GroupObject("k", "v")} FROM t)
            """;
        using var reader = command.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("\"a\\\"b\"");
        await Assert.That(reader.GetString(1)).IsEqualTo("""["a","b"]""");
        await Assert.That(reader.GetString(2)).IsEqualTo("""{"a":"x","b":"y"}""");
    }

    [Test]
    public async Task EqualsParameterAndOutboxByType_ExecuteOnRealSqlite()
    {
        using var connection = OpenMemory();
        Execute(connection, "CREATE TABLE outbox_messages (payload TEXT)");
        Execute(connection, """INSERT INTO outbox_messages (payload) VALUES ('{"Type":"Order.Created","n":7}'), ('{"Type":"Other"}')""");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*) FROM outbox_messages WHERE {SqliteJson.EqualsParameter("payload", "type", "Type")}";
            command.Parameters.AddWithValue("@type", "Order.Created");
            await Assert.That(Convert.ToInt64(command.ExecuteScalar())).IsEqualTo(1L);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*) FROM outbox_messages WHERE {SqliteJson.OutboxByType("Order.Created")}";
            await Assert.That(Convert.ToInt64(command.ExecuteScalar())).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task Guards_RejectInvalidArguments()
    {
        await Assert.That(() => SqliteJson.Path(""))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.Path((string[])null!))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.Extract(null!, "a"))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.ObjectLiteral(("", "v")))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.ArrayLiteral((string[])null!))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.EqualsParameter("payload", "1bad", "Type"))
            .Throws<ArgumentException>();
        await Assert.That(() => SqliteJson.SetParameter("payload", "1bad", "Type"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task EmptyPathSegment_ReportsPathParameterName()
    {
        await Assert.That(() => SqliteJson.Extract("payload", ""))
            .Throws<ArgumentException>()
            .WithParameterName("path");
    }

    [Test]
    public async Task IdentifierWithQuotes_IsEscaped()
    {
        await Assert.That(SqliteJson.Extract("a\"b", "k")).IsEqualTo("json_extract(\"a\"\"b\", '$.\"k\"')");
        await Assert.That(SqliteJson.Extract("a'b", "k")).IsEqualTo("json_extract(\"a'b\", '$.\"k\"')");
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
}
