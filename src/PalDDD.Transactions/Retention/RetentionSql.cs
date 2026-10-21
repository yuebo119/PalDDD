// ─────────────────────────────────────────────────────────────
// 🧹 RetentionSql — 历史表清理 SQL 与时间编码（无 ADO 类型）
// ─────────────────────────────────────────────────────────────

using System.Globalization;

namespace PalDDD.Transactions;

/// <summary>
/// 保留清理的 SQL 方言选择器。
/// <para>
/// 契约说明（2026-10-21 契约盘点）：本枚举保留 public 是**有意的公开选择器**——
/// <c>EfCoreRetentionStore</c> 的公共构造以它为参数，Dapper/PalORM 适配器各自映射内部的
/// <c>DapperDbType</c>/<c>SqlDialect</c>。它与 <c>DapperDbType</c> 同族：表达方言选择，
/// 不携带 provider 类型（DbContext / DbConnection / Npgsql* 等）。方言 SQL 文本已下沉
/// internal（<c>RetentionSql</c> 不再出现在公共面）。
/// </para>
/// </summary>
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
/// <para>
/// 2026-10-21 契约盘点：由 public 下沉为 internal（IVT 授三栈适配器）——方言 SQL
/// 文本属于基础设施细节，不应出现在抽象程序集的承诺面。
/// </para>
/// </summary>
/// <remarks>
/// 本类的表名/列名是 Dapper/PalORM 的 snake_case 物理名。EF Core 默认映射是 PascalCase
/// （表 = DbSet 名、列 = 属性名），由 <c>EfCoreRetentionStore</c> 从 EF 模型解析后自行构造
/// SQL（2026-10-21 评审 F5：复用本类的 SQL 会在 PG/MySQL 上报 relation/column 不存在）。
/// </remarks>
internal static class RetentionSql
{
    /// <summary>Outbox 终态行删除 SQL（按批次限制）。</summary>
    public static string OutboxDelete(RetentionDialect dialect, bool includeDead)
    {
        ValidateDialect(dialect);
        return dialect == RetentionDialect.MySql
            ? (includeDead ? OutboxDeleteAllMySql : OutboxDeleteProcessedMySql)
            : (includeDead ? OutboxDeleteAllSubquery : OutboxDeleteProcessedSubquery);
    }

    /// <summary>Inbox 已处理行删除 SQL。</summary>
    public static string InboxDelete(RetentionDialect dialect)
    {
        ValidateDialect(dialect);
        return dialect == RetentionDialect.MySql ? InboxDeleteMySql : InboxDeleteSubquery;
    }

    /// <summary>幂等过期记录删除 SQL。</summary>
    public static string IdempotencyDelete(RetentionDialect dialect)
    {
        ValidateDialect(dialect);
        return dialect switch
        {
            RetentionDialect.MySql => IdempotencyDeleteMySql,
            RetentionDialect.PostgreSql => IdempotencyDeletePostgreSql,
            RetentionDialect.Sqlite => IdempotencyDeleteSqlite,
            // 穷尽 switch：新增方言必须在此登记（原 `_ =>` 兜底会把新方言静默映射到 SQLite SQL）。
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown retention dialect."),
        };
    }

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
            RetentionDialect.Sqlite => sqliteProviderNative ? value : value.ToString("O", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown retention dialect."),
        };

    private static void ValidateDialect(RetentionDialect dialect)
    {
        if (!Enum.IsDefined(dialect))
            throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown retention dialect.");
    }

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
