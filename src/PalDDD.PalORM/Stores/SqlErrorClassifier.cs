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
/// 仅判定重复键——其他错误由调用方原样上抛。Provider 异常按类型名鸭子类型判定，
/// <see cref="PropertyCache"/> 缓存反射属性消除逐次 GetProperty 开销。
/// </remarks>
internal static class SqlErrorClassifier
{
    /// <summary>Provider 异常属性反射缓存（Type → 属性或 null）——原五处实现逐次 GetProperty。</summary>
    private static readonly ConcurrentDictionary<(Type Type, string Property), System.Reflection.PropertyInfo?> PropertyCache = new();

    /// <summary>判定异常链中是否含唯一约束冲突（遍历 InnerException 链）。</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃）。")]
    public static bool IsUniqueKeyViolation(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var typeName = ex.GetType().Name;

            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && GetPropertyValue(ex, "Number") is int mysqlNum
                && (mysqlNum == 1062 || mysqlNum == 1586))
                return true;

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && GetPropertyValue(ex, "SqlState") is string pgState
                && pgState == "23505")
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
