namespace PalDDD.Integration.Tests;

using PalDDD.Dapper.PostgreSql;

public sealed class PostgreSqlSoftDeleteTests
{
    [Test]
    public async Task Delete_KeepsWhereClauseAsTrustedParameterizedSqlFragment()
    {
        var sql = PostgreSqlSoftDelete.Delete("outbox_messages", "id=@id");

        await Assert.That(sql).IsEqualTo(
            "UPDATE outbox_messages SET deleted_at = NOW() WHERE id=@id AND deleted_at IS NULL");
    }
}
