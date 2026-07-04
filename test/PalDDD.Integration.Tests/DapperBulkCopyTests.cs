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
}
