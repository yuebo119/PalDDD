// ─────────────────────────────────────────────────────────────
// 🗄️ DapperSqlDialect — 数据库方言 SQL 片段（内部 record struct）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Dapper;

internal readonly record struct DapperSqlDialect(string InboxInsert, bool SupportsOutboxReturning)
{
    public static DapperSqlDialect For(DapperDbType dbType)
    {
        // 2026-10-21 评审 F24：未知枚举值 fail-fast——原默认臂静默落 SQLite SQL，
        // 非 SQLite 连接会执行错误方言（Create 已抛，三处口径不一致）。
        if (!Enum.IsDefined(dbType))
            throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "Unknown Dapper database type.");
        return dbType switch
        {
            DapperDbType.PostgreSql => new(SqlTemplates.InboxInsertPG, SupportsOutboxReturning: true),
            DapperDbType.MySql => new(SqlTemplates.InboxInsertMySql, SupportsOutboxReturning: false),
            DapperDbType.Sqlite => new(SqlTemplates.InboxInsertSqlite, SupportsOutboxReturning: false),
            _ => throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "Unknown Dapper database type."),
        };
    }
}
