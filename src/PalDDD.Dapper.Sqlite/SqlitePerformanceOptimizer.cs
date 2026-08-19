// ─────────────────────────────────────────────────────────────
// ⚡ SqlitePerformanceOptimizer — WAL 模式 + 性能 PRAGMA（AOT 安全）
// ─────────────────────────────────────────────────────────────
// SQLite 默认配置适合嵌入式单线程场景，不适合并发 OLTP。
// 此工具一键应用生产级优化，提升并发吞吐 5-10x。
//
// PRAGMA 说明：
//   journal_mode=WAL          — 写前日志，支持并发读写（默认 DELETE 不并发）
//   synchronous=NORMAL        — 关键帧同步（非 FULL），写入快 2x
//   cache_size=-20000         — 缓存 20MB（负数=KB），减少磁盘 I/O
//   busy_timeout=5000         — 等待锁超时 5 秒（代替立即 SQLITE_BUSY）
//   foreign_keys — 优化（二十五轮 B3）移至连接串 "Foreign Keys=True"（驱动层每物理连接
//                 首开必发，比每 scope PRAGMA 重跑可靠且省一条；learn.microsoft.com 关键字证实）
//   temp_store=MEMORY         — 临时表存内存
//   mmap_size=268435456       — 256MB 内存映射（零拷贝读取）
//
// AOT 安全性：
//   ✅ 纯 PRAGMA SQL 执行 — 零反射，零 IL 生成。
//
// 使用方式：
//   await SqlitePerformanceOptimizer.OptimizeAsync(connection).ConfigureAwait(false);
// ─────────────────────────────────────────────────────────────

using Microsoft.Data.Sqlite;

namespace PalDDD.Dapper.Sqlite;

/// <summary>SQLite 生产级性能优化器</summary>
public static class SqlitePerformanceOptimizer
{
    /// <summary>
    /// WAL 模式 PRAGMA（P2/P3 修复·十七轮拆出常量）——需单独执行以确认切换成功
    /// （journal_mode 是库级持久属性，返回结果行确认实际模式），消费方据此与其余 PRAGMA 分离执行。
    /// P3 修复（二十一轮）：消费方（ApplyAsync / SqliteServiceCollectionExtensions.ApplyOptimization）
    /// 现以 ExecuteScalar 读取返回值并比对 "wal"，失配抛 InvalidOperationException——确认声明落地。
    /// </summary>
    public const string WalPragma = "PRAGMA journal_mode=WAL";

    /// <summary>
    /// Production 级别除 WAL 外的其余 PRAGMA（P2/P3 修复·十七轮拆出常量）——
    /// 消费方先单执行 <see cref="WalPragma"/> 再批量执行本串，消除魔法切片偏移。
    /// </summary>
    public const string ProductionRestPragma = """
        PRAGMA synchronous=NORMAL;
        PRAGMA cache_size=-20000;
        PRAGMA busy_timeout=5000;
        PRAGMA temp_store=MEMORY;
        PRAGMA mmap_size=268435456;
        PRAGMA journal_size_limit=67108864;
        """;

    /// <summary>
    /// Light 级别除 WAL 外的其余 PRAGMA（P2/P3 修复·十七轮拆出常量，同 <see cref="ProductionRestPragma"/>）。
    /// </summary>
    public const string LightRestPragma = """
        PRAGMA synchronous=NORMAL;
        PRAGMA cache_size=-8000;
        PRAGMA busy_timeout=3000;
        """;

    /// <summary>获取指定优化级别的 PRAGMA SQL（单一来源——SqliteServiceCollectionExtensions 复用）。</summary>
    public static string GetPragma(SqliteOptimizeLevel level) => level switch
    {
        // P2/P3 修复（十七轮）：WAL + 其余 PRAGMA 常量拼接（输出与拆分前逐语句等价）
        SqliteOptimizeLevel.Production => WalPragma + ";\n" + ProductionRestPragma,
        SqliteOptimizeLevel.Light => WalPragma + ";\n" + LightRestPragma,
        SqliteOptimizeLevel.InMemory => """
            PRAGMA journal_mode=MEMORY;
            PRAGMA synchronous=OFF;
            PRAGMA cache_size=-50000;
            PRAGMA temp_store=MEMORY;
            """,
        _ => ""
    };

    /// <summary>应用全部生产级 PRAGMA 优化</summary>
    public static async ValueTask OptimizeAsync(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // ITM-220 修复（三十二轮）：State 守卫——对照 MySqlPerformanceOptimizer 同款，
        // 对已打开连接重复 Open 抛 InvalidOperationException（调用方复用共享连接场景）
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync().ConfigureAwait(false);
        await ApplyAsync(connection, SqliteOptimizeLevel.Production).ConfigureAwait(false);
    }

    /// <summary>应用轻量优化（嵌入式/移动端）</summary>
    public static async ValueTask OptimizeLightAsync(SqliteConnection connection)
    {
        // ITM-165 修复：补 connection null 守卫（对齐 OptimizeAsync）——原路径在
        // connection.OpenAsync() 处以 NullReferenceException 暴露，失败点与异常类型
        // 与 Production 路径不一致。
        ArgumentNullException.ThrowIfNull(connection);
        // ITM-220 修复（三十二轮）：State 守卫（见 OptimizeAsync）
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync().ConfigureAwait(false);
        await ApplyAsync(connection, SqliteOptimizeLevel.Light).ConfigureAwait(false);
    }

    /// <summary>应用内存优先优化（测试/CI 环境）</summary>
    public static async ValueTask OptimizeInMemoryAsync(SqliteConnection connection)
    {
        // ITM-165 修复：补 connection null 守卫（对齐 OptimizeAsync/Light）。
        ArgumentNullException.ThrowIfNull(connection);
        // ITM-220 修复（三十二轮）：State 守卫（见 OptimizeAsync）
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync().ConfigureAwait(false);
        await ApplyAsync(connection, SqliteOptimizeLevel.InMemory).ConfigureAwait(false);
    }

    /// <summary>在已打开的连接上应用指定级别的 PRAGMA（async 路径）。</summary>
    private static async ValueTask ApplyAsync(SqliteConnection connection, SqliteOptimizeLevel level)
    {
        var sql = GetPragma(level);
        if (sql.Length == 0) return;

        // WAL 模式需单独执行确认切换成功，其余 PRAGMA 批量执行
        // P2/P3 修复（十七轮）：改消费 WalPragma/RestPragma 常量——消除魔法切片偏移
        // （原 sql["PRAGMA journal_mode=WAL;\n".Length..] 依赖前缀字面量与 GetPragma 输出硬耦合）
        // P3 修复（二十一轮）：WAL 确认声明落地——PRAGMA journal_mode 返回实际生效模式，
        // 改 ExecuteScalarAsync 读返回值比对 "wal"，失配（如文件在只读卷/网络盘不支持 WAL）
        // 抛异常而非静默继续以 DELETE 模式运行（并发读写承诺无声失效）。
        if (level is SqliteOptimizeLevel.Production or SqliteOptimizeLevel.Light)
        {
            using var walCmd = connection.CreateCommand();
            walCmd.CommandText = WalPragma;
            var walResult = await walCmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (!string.Equals(walResult?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"PRAGMA journal_mode=WAL 未生效（返回 {walResult?.ToString() ?? "<null>"}）——"
                    + "目标卷可能不支持 WAL（网络盘/只读卷）；WAL 依赖的并发读写语义不会成立。");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = level == SqliteOptimizeLevel.Production ? ProductionRestPragma : LightRestPragma;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        else
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    /// <summary>获取当前 SQLite 版本和编译选项（诊断用）</summary>
    public static async Task<string> GetDiagnosticsAsync(SqliteConnection connection)
    {
        // ITM-195 修复（三十轮）：补 null 守卫——同文件 Optimize 系列已 ITM-165 对齐，唯此漏。
        ArgumentNullException.ThrowIfNull(connection);
        // ITM-220 修复（三十二轮）：State 守卫（见 OptimizeAsync）
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync().ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version(), sqlite_source_id()";
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            // P3 修复：source_id 短于 20 字符时切片越界
            var sourceId = reader.GetString(1);
            return $"SQLite {reader.GetString(0)} — {(sourceId.Length <= 20 ? sourceId : sourceId[..20])}...";
        }
        return "Unknown";
    }
}
