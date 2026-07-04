namespace PalDDD.Integration.Tests;

using PalDDD.Dapper.PostgreSql;

public sealed class PostgreSqlAuditorTests
{
    [Test]
    public async Task AppendAuditLog_EscapesTextAndJsonLiterals()
    {
        var sql = PostgreSqlAuditor.AppendAuditLog(
            "audit_table",
            "row'1",
            "UP'DATE",
            oldDataJson: "{\"name\":\"O'Brien\"}",
            newDataJson: "{\"name\":\"D'Angelo\"}",
            changedBy: "user'o");

        await Assert.That(sql).Contains("'audit_table'");
        await Assert.That(sql).Contains("'row''1'");
        await Assert.That(sql).Contains("'UP''DATE'");
        await Assert.That(sql).Contains("'{\"name\":\"O''Brien\"}'::jsonb");
        await Assert.That(sql).Contains("'{\"name\":\"D''Angelo\"}'::jsonb");
        await Assert.That(sql).Contains("'user''o'");
    }

    [Test]
    public async Task AppendAuditLog_RejectsInvalidIdentifierCharacters()
    {
        await Assert.That(() =>
            PostgreSqlAuditor.AppendAuditLog("audit'table", "row1", "INSERT")).Throws<ArgumentException>();
    }

    [Test]
    public async Task AuditHistory_EscapesTextLiterals()
    {
        var sql = PostgreSqlAuditor.AuditHistory("audit_table", "row'1");

        await Assert.That(sql).Contains("table_name = 'audit_table'");
        await Assert.That(sql).Contains("row_id = 'row''1'");
    }

    [Test]
    public async Task CreateAuditTrigger_AcceptsValidIdentifiers()
    {
        var sql = PostgreSqlAuditor.CreateAuditTrigger("order_audit", "tenant_id");

        await Assert.That(sql).Contains("audit_order_audit");
        await Assert.That(sql).Contains("tenant_id");
    }

    [Test]
    public async Task CreateAuditTrigger_RejectsInvalidTableName()
    {
        await Assert.That(() =>
            PostgreSqlAuditor.CreateAuditTrigger("order\"audit", "id")).Throws<ArgumentException>();
    }

    [Test]
    public async Task PurgeOldAuditLogs_RejectsOutOfRangeValues()
    {
        await Assert.That(() => PostgreSqlAuditor.PurgeOldAuditLogs(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => PostgreSqlAuditor.PurgeOldAuditLogs(366)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PurgeOldAuditLogs_AcceptsValidRange()
    {
        var sql = PostgreSqlAuditor.PurgeOldAuditLogs(30);
        await Assert.That(sql).Contains("INTERVAL '30 days'");
    }
}
