using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// SQL 唯一约束冲突分类器（评审 P2-5 提取）——收敛此前散落在
/// PalOrmInboxStore/PalOrmIdempotencyStore/PalOrmProjectionCheckpointStore
/// （IsDuplicateKeyError）与 PalOrmSagaStateStore/PalOrmEventLog
/// （IsUniqueConstraintViolation）中的五处同型实现（三十轮系列修复的姊妹同步根源）。
/// </summary>
/// <remarks>
/// 覆盖四 Provider：MySQL 1062/1586、PostgreSQL 23505、SQLite UNIQUE、SqlServer 2601/2627。
/// 仅判定重复键——其他错误由调用方原样上抛。Provider 异常按类型名鸭子类型判定。
/// R12 H3（NativeAOT 实测）：MySQL/PG 改用 AOT 安全的 <c>DbException</c> 虚属性——
/// MySQL 先看 ErrorCode，AOT 下 ErrorCode 退化为 E_FAIL 时回退 SqlState=23000 + 标准
/// "Duplicate entry" 文案；PG 看 SqlState=23505。仅 SqlServer 保留
/// <see cref="PropertyCache"/> 反射兜底（无 AOT 安全的公共替身）。
/// <para>
/// ⚠️ <b>"五处"的射程（2026-09-22 T-23 加注）</b>：上文"五处同型实现"指本类收敛的
/// **PalORM 栈内部** 5 处（列于上文各 Store）。跨项目另有同型实现——4 个 EFCore Context
/// （EventLog/Idempotency/Projections/Transactions）+ Dapper SqlErrorClassifier——**未合并，
/// 且经核验无法合并**：四条 EFCore 链互不共享 PalDDD 引用，唯一共同祖先是领域内核
/// <c>PalDDD.Core</c>，而本判定基于 <c>DbUpdateException</c> 需 EF Core（基础设施依赖），
/// 按「领域层不依赖基础设施」红线不能落位。合并的唯一出路是新增共享 EFCore 项目（改变发布
/// 包集合）或引入姊妹依赖（改变分层方向），**重复约 40 行纯判定逻辑是严格分层的既定成本**。
/// 详见 <c>docs/review/optimization-plan-2026-09-22.md</c> §T-23。
/// </para>
/// </remarks>
internal static class SqlErrorClassifier
{
    /// <summary>Provider 异常属性反射缓存（Type → 属性或 null）——原五处实现逐次 GetProperty。</summary>
    private static readonly ConcurrentDictionary<(Type Type, string Property), System.Reflection.PropertyInfo?> PropertyCache = new();

    /// <summary>判定异常链中是否含唯一约束冲突（遍历 InnerException 链）。</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "仅 SQL Server 分支使用反射（SqlException.Number 无 AOT 安全的公共替身）；MySQL/PG 已改用 DbException 虚属性，裁剪后反射返回 null 时原始异常上抛（安全降级）。")]
    public static bool IsUniqueKeyViolation(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var typeName = ex.GetType().Name;

            // R12 H3：NativeAOT 实测反射 GetProperty 在裁剪后返回 null，且 MySqlException.ErrorCode
            // 在 AOT 下退化为 E_FAIL（-2147467259），仅 SqlState 稳定。分类策略：
            // ① JIT/ErrorCode 快路径（1062/1586）；② AOT 兜底：SqlState=23000 且消息含
            // MySQL 标准 "Duplicate entry"（23000 也覆盖 FK/NOT NULL，故必须叠加文案）。
            if (typeName.Equals("MySqlException", StringComparison.Ordinal))
            {
                if (ex is System.Data.Common.DbException { ErrorCode: 1062 or 1586 })
                    return true;
                if (ex is System.Data.Common.DbException { SqlState: "23000" }
                    && ex.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && ex is System.Data.Common.DbException { SqlState: "23505" })
                return true;

            // SqliteException 类型限定（二十一轮 P2 镜像 InboxDbContext 修复，PD17）——
            // 裸消息匹配会误判非唯一约束异常
            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(ex.Message)
                && ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
                return true;

            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && GetPropertyValue(ex, "Number") is int sqlNum
                && (sqlNum == 2601 || sqlNum == 2627))
                return true;
        }
        return false;
    }

    /// <summary>带缓存的反射属性读取——属性不存在/裁剪后返回 null（判定失败即安全降级）。</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2080:This",
        Justification = "Provider 异常鸭子类型判定。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃）。")]
    private static object? GetPropertyValue(Exception ex, string propertyName)
    {
        var property = PropertyCache.GetOrAdd((ex.GetType(), propertyName),
            static key => key.Type.GetProperty(key.Property));
        return property?.GetValue(ex);
    }
}
