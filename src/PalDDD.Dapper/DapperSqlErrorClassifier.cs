// ─────────────────────────────────────────────────────────────
// 🔎 DapperSqlErrorClassifier — 唯一约束冲突错误分类（ITM-796 收口）
// ─────────────────────────────────────────────────────────────
// 原五处私有副本（Inbox/EventLog/Saga/Checkpoint/Idempotency）码集与方言覆盖
// 分叉（MySQL 1062/1586 vs 1062/1022；SQLite 码 vs message；SqlServer 仅部分
// 副本含）——收口为单一分类器，码集取并集。鸭子类型形态（type.Name + 反射
// 属性）与四处历史副本一致：不引入对 Npgsql/MySqlConnector 的硬引用
//（DapperIdempotencyStore 的强类型版随收口退役）。
// 📐 DDD 位置：基础设施层 — Dapper 栈内部共用，对齐 PalORM 侧 SqlErrorClassifier。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Dapper;

internal static class DapperSqlErrorClassifier
{
    /// <summary>唯一约束冲突判定（内层异常链逐层下钻）。
    /// 码集：PG 23505 / MySQL 1062+1586+1022 / SQLite 类型限定 message（基码 19 覆盖
    /// 全部约束家族不可用作唯一判据，R54-P2-1 勘正）/ SqlServer 2601+2627（v20 F1
    /// 防御性保留——DapperDbType 无 SqlServer 值现状下不删）。</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定（原四处私有副本的 IL2075 抑制随 ITM-796 收口迁移至此）。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级）。")]
    internal static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            var typeName = type.Name;

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(inner) is string sqlState
                && sqlState == "23505")
                return true;

            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int mysqlNumber
                && mysqlNumber is 1062 or 1586 or 1022)
                return true;

            if (typeName.Equals("SqliteException", StringComparison.Ordinal))
            {
                // R54-P2-1 勘正（2026-09-19 第五十四轮片1，四码探针实证）：SqliteErrorCode
                // 基码对全部 SQLITE_CONSTRAINT 家族恒 19（NOT NULL=19/1299、UNIQUE=19/2067、
                // CHECK=19/275、PK=19/1555）——码判定会把 NOT NULL/CHECK 违规误判为唯一冲突，
                // 正是 ITM-188/192 勘正明令防的「掩盖真实数据错误」。2067 只存在于
                // SqliteExtendedErrorCode 属性（基码属性上永不出现）。故 SQLite 仅保留
                // 类型限定的 message 匹配（与原 EventLog/Saga 副本逐字等价）。
                var message = inner.Message;
                if (!string.IsNullOrEmpty(message)
                    && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
                return true;
        }
        return false;
    }
}
