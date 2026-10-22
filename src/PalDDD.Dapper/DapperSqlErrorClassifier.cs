// ─────────────────────────────────────────────────────────────
// 🔎 DapperSqlErrorClassifier — 唯一约束冲突错误分类（ITM-796 收口）
// ─────────────────────────────────────────────────────────────
// 原五处私有副本（Inbox/EventLog/Saga/Checkpoint/Idempotency）码集与方言覆盖
// 分叉（MySQL 1062/1586 vs 1062/1022；SQLite 码 vs message；SqlServer 仅部分
// 副本含）——收口为单一分类器，码集取并集。
// 2026-10-21 AOT 勘正：PG/MySQL/SQLite 改强类型判定。Npgsql/MySqlConnector/
// Microsoft.Data.Sqlite 已是本包的实现私有依赖（PrivateAssets=all，不进 nuspec），
// 强类型不扩大消费方依赖面；原鸭子类型反射在 NativeAOT 裁剪后属性元数据不可见，
// 唯一冲突被误判为非冲突（Inbox 幂等重复插入逃逸为 provider 异常）。SqlServer
// 分支保持鸭子类型（本包不引用 SqlClient）。
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
        Justification = "仅 SqlServer 防御分支保留鸭子类型（本包不引用 SqlClient，DapperDbType 亦无 SqlServer 值）。PG/MySQL/SQLite 已改强类型判定，不再依赖裁剪后可能消失的属性元数据。")]
    internal static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            // R13 F12（P1）：三驱动是 PrivateAssets=all 的实现私有依赖，消费方只引用自己的
            // provider。直接写 `inner is MySqlConnector.MySqlException` 会在缺失程序集时于
            // **异常过滤器内**抛 FileNotFoundException——C# 过滤器吞掉该异常并判定 false，
            // 原 provider 异常原样逃逸（PG-only 消费方的唯一冲突分类整体失效）。先按类型
            // 全名短路，只有名字命中才触发类型加载/转换（此时程序集必然已加载）。
            var typeName = inner.GetType().FullName;

            if (string.Equals(typeName, "Npgsql.PostgresException", StringComparison.Ordinal)
                && inner is Npgsql.PostgresException pg && pg.SqlState == "23505")
                return true;

            if (string.Equals(typeName, "MySqlConnector.MySqlException", StringComparison.Ordinal)
                && inner is MySqlConnector.MySqlException mySql
                && mySql.Number is 1062 or 1586 or 1022)
                return true;

            if (string.Equals(typeName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal)
                && inner is Microsoft.Data.Sqlite.SqliteException)
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

            // SqlServer 分支保持鸭子类型：本包不引用 SqlClient，且当前 DapperDbType 无
            // SqlServer 值，仅作防御性保留（v20 F1）。
            var type = inner.GetType();
            if (type.Name.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
                return true;
        }
        return false;
    }
}
