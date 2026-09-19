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
    /// 码集并集：PG 23505 / MySQL 1062+1586+1022 / SQLite 19+2067 码（message
    /// 限定兜底——裸消息匹配会把文案恰含该词组的非唯一异常误判，ITM-188/192 族
    /// 勘正保留类型限定）/ SqlServer 2601+2627（v20 F1 防御性保留——DapperDbType
    /// 无 SqlServer 值现状下不删，避免恢复该方言时的静默漏判）。</summary>
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
                if (type.GetProperty("SqliteErrorCode")?.GetValue(inner) is int sqliteCode
                    && sqliteCode is 19 or 2067)
                    return true;
                // message 兜底（v25 P3 守卫族：null/空消息不进 Contains）——码不可得
                //（版本差异/包装异常）时的保守补充，类型已限定
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
