// ─────────────────────────────────────────────────────────────
// 🗑️ PostgreSqlSoftDelete — PostgreSQL 软删除（deleted_at 模式）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯 SQL 字符串拼接 — 零反射。
//   ✅ deleted_at 列 + 部分索引，查询自动排除已删除行。
//
// 模式说明：
//   DELETE → UPDATE xxx SET deleted_at = NOW()
//   SELECT → WHERE deleted_at IS NULL（自动注入）
//   RESTORE → UPDATE xxx SET deleted_at = NULL
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 基础设施层 SQL 工具类，零领域逻辑。
//   - 可配合 Dapper/SqlKata/NpgsqlCommand 使用。
//   - 建议配合 PostgreSQL 部分索引：
//     CREATE INDEX idx_active ON outbox_messages(created_at) WHERE deleted_at IS NULL;
//
// 使用方式（Dapper）：
//   conn.Execute(PostgreSqlSoftDelete.Delete("outbox_messages", "id=@id"), new { id });
//   var sql = "SELECT * FROM outbox_messages WHERE " + PostgreSqlSoftDelete.ActiveFilter();
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>PostgreSQL 软删除工具 — deleted_at 模式</summary>
public static class PostgreSqlSoftDelete
{
    /// <summary>默认软删除列名</summary>
    public const string DefaultColumn = "deleted_at";

    /// <summary>生成软删除 UPDATE 语句（替代物理 DELETE）</summary>
    /// <param name="table">表名</param>
    /// <param name="whereClause">
    /// WHERE 条件（不带 WHERE 关键字）。此参数是 SQL 片段，调用方必须只传入受信任模板，
    /// 用户输入必须通过 Dapper/Npgsql 参数绑定，不得拼接进此字符串。
    /// </param>
    /// <param name="column">软删除列名（默认 "deleted_at"）</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Delete(string table, string whereClause, string column = DefaultColumn)
        => $"UPDATE {Escape(table)} SET {Escape(column)} = NOW() WHERE {whereClause} AND {Escape(column)} IS NULL";

    /// <summary>生成恢复语句（取消软删除）</summary>
    /// <param name="table">表名</param>
    /// <param name="whereClause">
    /// WHERE 条件（不带 WHERE 关键字）。此参数是 SQL 片段，调用方必须只传入受信任模板，
    /// 用户输入必须通过 Dapper/Npgsql 参数绑定，不得拼接进此字符串。
    /// </param>
    /// <param name="column">软删除列名（默认 "deleted_at"）</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Restore(string table, string whereClause, string column = DefaultColumn)
        => $"UPDATE {Escape(table)} SET {Escape(column)} = NULL WHERE {whereClause}";

    /// <summary>生成硬删除历史数据（清理超过 N 天的已删除行）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Purge(string table, int olderThanDays, string column = DefaultColumn)
        => $"DELETE FROM {Escape(table)} WHERE {Escape(column)} IS NOT NULL AND {Escape(column)} < NOW() - INTERVAL '{olderThanDays} days'";

    /// <summary>生成活跃行过滤器（WHERE deleted_at IS NULL）</summary>
    /// <param name="alias">可选的表别名（如 "m"）</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ActiveFilter(string? alias = null, string column = DefaultColumn)
    {
        var col = alias is null ? Escape(column) : $"{Escape(alias)}.{Escape(column)}";
        return $"{col} IS NULL";
    }

    /// <summary>生成部分索引创建脚本（仅索引活跃行）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string CreatePartialIndex(string table, string indexName, string indexedColumns, string column = DefaultColumn)
        => $"CREATE INDEX IF NOT EXISTS {Escape(indexName)} ON {Escape(table)} ({indexedColumns}) WHERE {Escape(column)} IS NULL";

    /// <summary>为建表添加软删除列（ALTER TABLE ADD COLUMN IF NOT EXISTS）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string AddColumn(string table, string column = DefaultColumn)
        => $"ALTER TABLE {Escape(table)} ADD COLUMN IF NOT EXISTS {Escape(column)} TIMESTAMPTZ";

    private static string Escape(string s) => s.Contains('"') ? s.Replace("\"", "\"\"") : s;
}
