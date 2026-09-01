namespace PalDDD.Integration.Tests;

using PalDDD.Dapper.PostgreSql;

public sealed class PostgreSqlSoftDeleteTests
{
    [Test]
    public async Task Delete_KeepsWhereClauseAsTrustedParameterizedSqlFragment()
    {
        var sql = PostgreSqlSoftDelete.Delete("outbox_messages", "id=@id");

        // v43 P3：whereClause 改为括号包裹后与软删除过滤条件 AND 连接
        await Assert.That(sql).IsEqualTo(
            "UPDATE \"outbox_messages\" SET \"deleted_at\" = NOW() WHERE (id=@id) AND \"deleted_at\" IS NULL");
    }

    // v43 P3：whereClause 含顶层 OR 时的优先级语义修复——原 "WHERE cond1 OR cond2 AND deleted_at IS NULL"
    // 按 AND 优先于 OR 解析，OR 左支命中的已删行再次被 UPDATE、deleted_at 被覆盖；括号包裹后
    // 软删除过滤恒为合取项，已删行不可能再命中
    [Test]
    public async Task Delete_TopLevelOrInWhereClause_SoftDeleteFilterStaysConjunctive()
    {
        var sql = PostgreSqlSoftDelete.Delete("outbox_messages", "id=@id OR tenant=@t");

        await Assert.That(sql).IsEqualTo(
            "UPDATE \"outbox_messages\" SET \"deleted_at\" = NOW() WHERE (id=@id OR tenant=@t) AND \"deleted_at\" IS NULL");
    }

    // TST-206：标识符注入防护测试。注（偏离说明）：PostgreSqlSoftDelete 与 PostgreSqlAuditor
    // 策略不同——Auditor 对非法标识符抛 ArgumentException（拒绝式），SoftDelete 的 Escape 是
    // 标识符转义式（双引号包裹 + 内嵌双引号翻倍），不抛异常。故本测试不做 Throws 断言，
    // 而以字符串级断言验证恶意标识符被完整转义中和（无法逃逸出双引号上下文执行注入）。
    [Test]
    public async Task Delete_MaliciousIdentifier_DoubleQuoteEscaped_NotInjectable()
    {
        var malicious = "outbox\"; DROP TABLE outbox_messages;--";

        var sql = PostgreSqlSoftDelete.Delete(malicious, "id=@id");

        // 恶意标识符整体被双引号包裹且内嵌双引号翻倍 —— 生成的 UPDATE 目标是
        // 一个名为 outbox"; DROP TABLE outbox_messages;-- 的（不存在的）表，
        // 注入载荷无法逃逸标识符上下文
        await Assert.That(sql).Contains("\"outbox\"\"; DROP TABLE outbox_messages;--\"");
        await Assert.That(sql.StartsWith("UPDATE \"outbox\"\";", StringComparison.Ordinal)).IsTrue();
    }
}
