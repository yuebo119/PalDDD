// ─────────────────────────────────────────────────────────────
// 🧹 RetentionSql — 历史表清理 SQL 与时间编码（无 ADO 类型）
// ─────────────────────────────────────────────────────────────

using System.Globalization;

namespace PalDDD.Transactions;

/// <summary>保留清理的 SQL 方言。</summary>
public enum RetentionDialect
{
    PostgreSql,
    MySql,
    Sqlite,
}

/// <summary>
/// 保留清理的 SQL 与时间参数编码（2026-10-20 性能轮）。本类只含字符串与值转换，
/// ADO 执行由 Dapper/PalORM/EF Core 三个基础设施适配包各自完成，避免领域/应用层
/// 泄漏 <c>DbCommand</c>。
/// </summary>
public static class RetentionSql
{
    /// <summary>Outbox 终态行删除 SQL（按批次限制）。</summary>
    public static string OutboxDelete(RetentionDialect dialect, bool includeDead)
    {
        if (dialect == RetentionDialect.MySql)
            return includeDead ? OutboxDeleteAllMySql : OutboxDeleteProcessedMySql;
        return includeDead ? OutboxDeleteAllSubquery : OutboxDeleteProcessedSubquery;
    }

    /// <summary>Inbox 已处理行删除 SQL。</summary>
    public static string InboxDelete(RetentionDialect dialect)
        => dialect == RetentionDialect.MySql ? InboxDeleteMySql : InboxDeleteSubquery;

    /// <summary>幂等过期记录删除 SQL。</summary>
    public static string IdempotencyDelete(RetentionDialect dialect)
        => dialect switch
        {
            RetentionDialect.MySql => IdempotencyDeleteMySql,
            RetentionDialect.PostgreSql => IdempotencyDeletePostgreSql,
            _ => IdempotencyDeleteSqlite,
        };

    /// <summary>按方言编码时间参数（与各存储栈既有格式一致）。</summary>
    /// <param name="dialect">数据库方言。</param>
    /// <param name="value">截止时间。</param>
    /// <param name="sqliteProviderNative">
    /// SQLite 专用：true 时返回原生 <see cref="DateTimeOffset"/>，由 Microsoft.Data.Sqlite 编码为
    /// 空格分隔格式（EF Core / PalORM 写入格式）；false 时返回 "O" 字符串（Dapper 写入格式）。
    /// PG/MySQL 忽略本参数。
    /// </param>
    public static object ToTimeParam(
        RetentionDialect dialect,
        DateTimeOffset value,
        bool sqliteProviderNative = false)
        => dialect switch
        {
            RetentionDialect.PostgreSql => value,
            RetentionDialect.MySql => value.UtcDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            _ => sqliteProviderNative ? value : value.ToString("O", CultureInfo.InvariantCulture),
        };

    private const string OutboxDeleteProcessedSubquery =
        "DELETE FROM outbox_messages WHERE id IN (SELECT id FROM outbox_messages WHERE status = 1 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n)";

    private const string OutboxDeleteAllSubquery =
        "DELETE FROM outbox_messages WHERE id IN (SELECT id FROM outbox_messages WHERE status IN (1, 2) AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n)";

    private const string InboxDeleteSubquery =
        "DELETE FROM inbox_messages WHERE id IN (SELECT id FROM inbox_messages WHERE status = 2 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n)";

    private const string OutboxDeleteProcessedMySql =
        "DELETE FROM outbox_messages WHERE status = 1 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n";

    private const string OutboxDeleteAllMySql =
        "DELETE FROM outbox_messages WHERE status IN (1, 2) AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n";

    private const string InboxDeleteMySql =
        "DELETE FROM inbox_messages WHERE status = 2 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT @n";

    private const string IdempotencyDeletePostgreSql =
        "DELETE FROM idempotency_records WHERE ctid IN (SELECT ctid FROM idempotency_records WHERE expires_at < @cutoff ORDER BY expires_at LIMIT @n)";

    private const string IdempotencyDeleteSqlite =
        "DELETE FROM idempotency_records WHERE rowid IN (SELECT rowid FROM idempotency_records WHERE expires_at < @cutoff ORDER BY expires_at LIMIT @n)";

    private const string IdempotencyDeleteMySql =
        "DELETE FROM idempotency_records WHERE expires_at < @cutoff ORDER BY expires_at LIMIT @n";
}
