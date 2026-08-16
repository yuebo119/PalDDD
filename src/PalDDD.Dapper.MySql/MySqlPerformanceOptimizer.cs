// ─────────────────────────────────────────────────────────────
// ⚡ MySqlPerformanceOptimizer — MySQL/InnoDB 生产级性能参数优化
// ─────────────────────────────────────────────────────────────
// AOT 安全性：✅ 完全 AOT 安全
//   ✅ 纯 SQL 执行（SET SESSION 语句）— 零反射，零对象映射。
//   ✅ MySqlConnection + MySqlCommand — ADO.NET 原生 API，AOT 兼容。
//
// 💡 为什么要优化 InnoDB 参数？
//   ｜ MySQL/InnoDB 的默认配置倾向于数据安全而非性能。
//   ｜ 在生产环境中，根据业务特性调整以下参数可显著提升吞吐量：
//   ｜
//   ｜ innodb_lock_wait_timeout = 10（默认 50s → 10s）
//   ｜   → 减少行锁等待时间，避免长时间阻塞。适合 OLTP 短小事务。
//   ｜
//   ｜ sql_mode = 'STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION'
//   ｜   → 严格模式：插入无效数据时回滚而非静默截断。
//   ｜   → NO_ENGINE_SUBSTITUTION：存储引擎不可用时抛错误，而非静默替换为 MyISAM。
//   ｜
//   ｜ transaction_isolation = 'READ-COMMITTED'
//   ｜   → 从 REPEATABLE-READ 降至 READ-COMMITTED。
//   ｜   → 减少间隙锁（Gap Lock），降低死锁概率，提升并发写入吞吐量。
//
// 💡 注意：优化是会话级的（SET SESSION），仅影响当前连接。
//   ｜ 对于持久化设置，建议在 MySQL 配置文件（my.cnf）中全局配置。
// ─────────────────────────────────────────────────────────────

using MySqlConnector;

namespace PalDDD.Dapper.MySql;

/// <summary>
/// MySQL/InnoDB 生产级性能优化器。<br/>
/// 通过 SET SESSION 命令调整当前连接的 InnoDB 参数，优化 OLTP 场景下的并发性能。
/// </summary>
public static class MySqlPerformanceOptimizer
{
    /// <summary>
    /// 应用生产级 InnoDB 优化参数到当前连接。<br/>
    /// 调整三个关键参数：行锁超时、SQL 严格模式、事务隔离级别。
    /// </summary>
    /// <param name="connection">MySQL 连接（方法内部会打开连接）</param>
    /// <remarks>
    /// ITM-089 修复（声明）：连接生命周期归调用方——本方法接收调用方传入的连接，仅在未打开时
    /// 幂等 Open，<b>不会 Close/Dispose 连接</b>。SET SESSION 是会话级设置，优化后连接保持打开，
    /// 调用方可继续使用；连接最终由调用方负责关闭/释放（本库调用点均自行 Dispose）。
    /// </remarks>
    public static void Optimize(MySqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open) connection.Open(); // P3 修复：幂等开连接

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SET SESSION innodb_lock_wait_timeout = 10;
            SET SESSION sql_mode = 'STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION';
            SET SESSION transaction_isolation = 'READ-COMMITTED';
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 设置当前连接的字符集为 utf8mb4（完整 Unicode 支持，含 Emoji）。<br/>
    /// ⚠️ MySQL 的 "utf8" 别名实际只支持 3 字节 UTF-8，始终使用 "utf8mb4"。
    /// </summary>
    /// <param name="connection">MySQL 连接（方法内部会打开连接）</param>
    /// <remarks>
    /// ITM-089 修复（声明）：连接生命周期归调用方——本方法仅在未打开时幂等 Open，
    /// <b>不会 Close/Dispose 连接</b>；调用方负责用后关闭/释放。
    /// </remarks>
    public static void SetUtf8mb4(MySqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open) connection.Open(); // P3 修复：幂等开连接
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SET NAMES utf8mb4 COLLATE utf8mb4_unicode_ci";
        cmd.ExecuteNonQuery();
    }
}
