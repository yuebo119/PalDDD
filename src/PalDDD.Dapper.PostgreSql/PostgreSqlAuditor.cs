// ─────────────────────────────────────────────────────────────
// 📝 PostgreSqlAuditor — PostgreSQL 审计日志（触发器模式 + 行级审计）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯 SQL 生成 + PostgreSQL 触发器 — 零反射，零 IL 生成。
//   ✅ 审计逻辑在数据库层执行，应用程序零开销。
//
// 审计模式：
//   1. 触发器模式（推荐）— 数据库自动记录，零应用代码
//   2. 应用层模式         — 手动调用 AppendAuditLog() 记录
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 触发器模式：审计完全在 DB 层，领域代码无感知。
//   - 应用层模式：通过工具方法生成审计 INSERT 语句，适配器层调用。
//   - 不修改任何领域实体。
//
// 使用方式：
//   // 创建审计表 + 触发器
//   conn.Execute(PostgreSqlAuditor.CreateAuditTrigger("outbox_messages"));
//
//   // 查询审计日志
//   var audit = conn.QueryAsync<PostgreSqlAuditEntry>(
//       "SELECT * FROM audit_log WHERE table_name='outbox_messages'");
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>PostgreSQL 审计日志工具</summary>
public static class PostgreSqlAuditor
{
    // ── 审计表 DDL ──

    /// <summary>创建标准审计日志表</summary>
    public const string CreateAuditTable = """
        CREATE TABLE IF NOT EXISTS audit_log (
            id          BIGSERIAL PRIMARY KEY,
            table_name  TEXT        NOT NULL,
            row_id      TEXT        NOT NULL,
            operation   TEXT        NOT NULL,  -- INSERT | UPDATE | DELETE
            old_data    JSONB,
            new_data    JSONB,
            changed_by  TEXT,                  -- 操作者标识
            changed_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE INDEX IF NOT EXISTS idx_audit_table_row
            ON audit_log(table_name, row_id, changed_at DESC);

        CREATE INDEX IF NOT EXISTS idx_audit_changed_at
            ON audit_log(changed_at);
        """;

    /// <summary>为标准表创建审计触发器（INSERT/UPDATE/DELETE 全记录）</summary>
    public static string CreateAuditTrigger(string tableName, string pkColumn = "id")
    {
        // P3 修复（二十一轮）：DDL 标识符位置（ON 表名 / NEW.列名 / 函数名 / 触发器名）
        // 改消费 QuoteIdentifier 返回的双引号包裹标识符——此前 bare/EscapeLiteral 混用：
        // 表名走单引号翻倍语义（标识符位置错配，含大写/关键字时 fold 或语法错误），
        // 函数名/触发器名裸拼接（含大写时 CREATE 与 DROP 的 fold 语义不一致）。
        var quotedTable = QuoteIdentifier(tableName, nameof(tableName));
        var quotedPk = QuoteIdentifier(pkColumn, nameof(pkColumn));
        var funcName = QuoteIdentifier($"audit_{tableName}", nameof(tableName));
        var triggerName = QuoteIdentifier($"trg_audit_{tableName}", nameof(tableName));

        return $"""
        CREATE OR REPLACE FUNCTION {funcName}() RETURNS TRIGGER AS $$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                INSERT INTO audit_log(table_name, row_id, operation, new_data)
                VALUES ('{EscapeLiteral(tableName)}', NEW.{quotedPk}::TEXT, 'INSERT', row_to_json(NEW)::jsonb);
                RETURN NEW;
            ELSIF TG_OP = 'UPDATE' THEN
                INSERT INTO audit_log(table_name, row_id, operation, old_data, new_data)
                VALUES ('{EscapeLiteral(tableName)}', NEW.{quotedPk}::TEXT, 'UPDATE',
                        row_to_json(OLD)::jsonb, row_to_json(NEW)::jsonb);
                RETURN NEW;
            ELSIF TG_OP = 'DELETE' THEN
                INSERT INTO audit_log(table_name, row_id, operation, old_data)
                VALUES ('{EscapeLiteral(tableName)}', OLD.{quotedPk}::TEXT, 'DELETE', row_to_json(OLD)::jsonb);
                RETURN OLD;
            END IF;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS {triggerName} ON {quotedTable};
        CREATE TRIGGER {triggerName}
            AFTER INSERT OR UPDATE OR DELETE ON {quotedTable}
            FOR EACH ROW EXECUTE FUNCTION {funcName}();
        """;
    }

    // ── 应用层审计（手动记录）──

    /// <summary>生成手动审计 INSERT 语句</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string AppendAuditLog(string tableName, string rowId, string operation,
        string? oldDataJson = null, string? newDataJson = null, string? changedBy = null)
    {
        // P3 修复（二十一轮）：表名在本语句处于单引号字面量位置（VALUES 值）——仅消费校验，丢弃包裹返回值
        _ = QuoteIdentifier(tableName, nameof(tableName));

        return $"""
        INSERT INTO audit_log (table_name, row_id, operation, old_data, new_data, changed_by)
        VALUES ('{EscapeLiteral(tableName)}', '{EscapeLiteral(rowId)}', '{EscapeLiteral(operation)}',
            {NullOrJson(oldDataJson)}, {NullOrJson(newDataJson)}, {NullOrText(changedBy)})
        """;
    }

    /// <summary>查询某行完整审计历史</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string AuditHistory(string tableName, string rowId)
    {
        // P3 修复（二十一轮）：表名在本语句处于单引号字面量位置（WHERE 比较值）——仅消费校验，丢弃包裹返回值
        _ = QuoteIdentifier(tableName, nameof(tableName));

        return $"SELECT * FROM audit_log WHERE table_name = '{EscapeLiteral(tableName)}' AND row_id = '{EscapeLiteral(rowId)}' ORDER BY changed_at DESC";
    }

    /// <summary>清理超过 N 天的审计日志（范围 1–365 天）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string PurgeOldAuditLogs(int olderThanDays)
    {
        if (olderThanDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(olderThanDays), olderThanDays,
                "清理天数必须在 1 到 365 之间。");

        return $"DELETE FROM audit_log WHERE changed_at < NOW() - INTERVAL '{olderThanDays} days'";
    }

    // ── 辅助：SQL 安全 ──

    /// <summary>
    /// 校验并引用 SQL 标识符（表名/列名）。<br/>
    /// 只允许字母、数字、下划线，必须以下划线或字母开头。<br/>
    /// 验证通过后用双引号包裹并返回。<br/>
    /// P3 修复（二十一轮）：此前返回 void——doc 声称"验证通过后用双引号包裹"但从未包裹，
    /// 调用点在标识符位置只能退回 EscapeLiteral（字面量语义）。现返回真包裹的标识符，
    /// DDL 标识符位置消费返回值；字面量位置的调用点（AppendAuditLog/AuditHistory 的表名）
    /// 仅消费校验、显式丢弃返回值。
    /// </summary>
    private static string QuoteIdentifier(string identifier, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier, paramName);

        if (!IsIdentifierStart(identifier[0]))
            throw new ArgumentException("SQL 标识符必须以字母或下划线开头。", paramName);

        for (int i = 1; i < identifier.Length; i++)
            if (!IsIdentifierPart(identifier[i]))
                throw new ArgumentException("SQL 标识符只能包含字母、数字或下划线。", paramName);

        // 字符集校验已排除双引号，直接包裹即可（无翻倍必要）
        return $"\"{identifier}\"";
    }

    /// <summary>
    /// 转义 SQL 字符串字面量 — 将单引号替换为两个单引号。<br/>
    /// 用于 SQL 语句中的字符串值（非标识符）。
    /// </summary>
    private static string EscapeLiteral(string s)
        => s.Contains('\'') ? s.Replace("'", "''") : s;

    private static string NullOrJson(string? v) => v is null ? "NULL" : $"'{EscapeLiteral(v)}'::jsonb";

    private static string NullOrText(string? v) => v is null ? "NULL" : $"'{EscapeLiteral(v)}'";

    private static bool IsIdentifierStart(char c)
        => c is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char c)
        => IsIdentifierStart(c) || c is >= '0' and <= '9';
}

/// <summary>审计日志实体（查询结果映射用）</summary>
public sealed class PostgreSqlAuditEntry
{
    public long Id { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string RowId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? OldData { get; set; }
    public string? NewData { get; set; }
    public string? ChangedBy { get; set; }

    /// <summary>
    /// ITM-115 修复（声明）：ChangedAt 保持 DateTime——审计表列是 TIMESTAMPTZ（见
    /// <see cref="PostgreSqlAuditor.CreateAuditTable"/>），Npgsql 默认映射为 DateTime
    /// （Kind=Local/Utc）。DTO 是公开查询结果类型，改 DateTimeOffset 属公共 API 变更
    /// （PublicApiSnapshot 漂移 + 二进制兼容破坏），故仅声明：DateTime 不携带原始时区
    /// 偏移，跨时区消费方应按 DateTime.Kind 显式转换（如 Kind==Utc 时
    /// <c>new DateTimeOffset(dt, TimeSpan.Zero)</c>），或改用查询侧
    /// <c>changed_at AT TIME ZONE 'UTC'</c> 归一化。
    /// </summary>
    public DateTime ChangedAt { get; set; }
}
