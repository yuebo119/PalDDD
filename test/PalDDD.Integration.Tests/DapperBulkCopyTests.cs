namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using PalDDD.Dapper;

public sealed class DapperBulkCopyTests
{
    [Test]
    [Arguments("outbox;DROP TABLE outbox_messages")]
    [Arguments("1outbox")]
    [Arguments("public.")]
    [Arguments("public..outbox_messages")]
    public async Task BulkInsertAsync_RejectsUnsafeTableName(string tableName)
    {
        var exception = await Assert.That(() =>
            DapperBulkCopy.BulkInsertAsync(
                new SqliteConnection("Data Source=:memory:"),
                DapperDbType.Sqlite,
                tableName,
                ["id"],
                Array.Empty<object>(),
                static item => [item]).AsTask()).Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("tableName");
    }

    // v66 PG 事务类型对称 fail-fast 回归网（守卫在 (NpgsqlConnection) 强转之前——
    // 传 SqliteTransaction 即触发，零 PG 连接依赖）
    [Test]
    public async Task BulkInsertAsync_PG_WrongTransactionType_ThrowsArgumentException()
    {
        var exception = await Assert.That(() =>
            DapperBulkCopy.BulkInsertAsync(
                new SqliteConnection("Data Source=:memory:"),
                DapperDbType.PostgreSql,
                "outbox_messages",
                ["id"],
                [new object()],
                static item => [item],
                transaction: OpenSqliteWithTransaction()).AsTask())
            .Throws<ArgumentException>();

        await Assert.That(exception!.Message).Contains("NpgsqlTransaction");
    }

    [Test]
    [Arguments("id;DROP TABLE outbox_messages")]
    [Arguments("1id")]
    [Arguments("schema.id")]
    [Arguments("")]
    public async Task BulkInsertAsync_RejectsUnsafeColumnName(string columnName)
    {
        var exception = await Assert.That(() =>
            DapperBulkCopy.BulkInsertAsync(
                new SqliteConnection("Data Source=:memory:"),
                DapperDbType.Sqlite,
                "outbox_messages",
                [columnName],
                Array.Empty<object>(),
                static item => [item]).AsTask()).Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("columns");
    }

    // ITM-251 传感器（F9）：带 null 首行值的批量插入——守护值提取/NULL 归一路径
    // 不被首行 null 短路（MySQL 推断路径的同型场景），并断言 extractor 调用次数契约：
    // 每行恰一次 + 首行入口长度探针额外一次（契约修复后三方言一致；修复前 MySQL 路径
    // 推断循环 + 填充循环对每行各调一次）。SQLite 路径可直接运行验证。
    [Test]
    public async Task BulkInsertAsync_WithNullValuesInFirstRow_InsertsAllRowsCorrectly()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE bulk_null_test (id INTEGER PRIMARY KEY, name TEXT, amount INT)";
            await create.ExecuteNonQueryAsync();
        }

        (int Id, string? Name, int? Amount)[] items =
        [
            (1, null, null),   // 首行含 null 列
            (2, "alpha", 10),
            (3, null, 30),
        ];

        var extractorCalls = 0;
        var inserted = await DapperBulkCopy.BulkInsertAsync(
            conn,
            DapperDbType.Sqlite,
            "bulk_null_test",
            ["id", "name", "amount"],
            items,
            item =>
            {
                extractorCalls++;
                return [item.Id, item.Name, item.Amount];
            });

        await Assert.That(inserted).IsEqualTo(3);
        await Assert.That(extractorCalls).IsEqualTo(items.Length + 1); // 每行一次 + 首行探针一次

        await using var verify = conn.CreateCommand();
        verify.CommandText = "SELECT id, name, amount FROM bulk_null_test ORDER BY id";
        await using var reader = await verify.ExecuteReaderAsync();
        var rows = new List<(long Id, string? Name, long? Amount)>();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0].Name).IsNull();
        await Assert.That(rows[0].Amount).IsNull();
        await Assert.That(rows[1].Name).IsEqualTo("alpha");
        await Assert.That(rows[1].Amount).IsEqualTo(10L);
        await Assert.That(rows[2].Name).IsNull();
        await Assert.That(rows[2].Amount).IsEqualTo(30L);
    }

    private static SqliteTransaction OpenSqliteWithTransaction()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn.BeginTransaction();
    }
}
