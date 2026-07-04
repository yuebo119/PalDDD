// ─────────────────────────────────────────────────────────────
// 🗄️ DapperSqlDialect — 数据库方言 SQL 片段（内部 record struct）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Dapper;

internal readonly record struct DapperSqlDialect(string InboxInsert, bool SupportsOutboxReturning)
{
    public static DapperSqlDialect For(DapperDbType dbType)
        => dbType switch
        {
            DapperDbType.PostgreSql => new(SqlTemplates.InboxInsertPG, SupportsOutboxReturning: true),
            DapperDbType.MySql => new(SqlTemplates.InboxInsertMySql, SupportsOutboxReturning: false),
            _ => new(SqlTemplates.InboxInsertSqlite, SupportsOutboxReturning: false)
        };
}
