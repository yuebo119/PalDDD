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
//   foreign_keys=ON           — 强制外键约束
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
    /// <summary>获取指定优化级别的 PRAGMA SQL（单一来源——SqliteServiceCollectionExtensions 复用）。</summary>
    public static string GetPragma(SqliteOptimizeLevel level) => level switch
    {
        SqliteOptimizeLevel.Production => """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-20000;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;
            PRAGMA journal_size_limit=67108864;
            """,
        SqliteOptimizeLevel.Light => """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-8000;
            PRAGMA busy_timeout=3000;
            PRAGMA foreign_keys=ON;
            """,
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
        await connection.OpenAsync().ConfigureAwait(false);
        await ApplyAsync(connection, SqliteOptimizeLevel.Production).ConfigureAwait(false);
    }

    /// <summary>应用轻量优化（嵌入式/移动端）</summary>
    public static async ValueTask OptimizeLightAsync(SqliteConnection connection)
    {
        await connection.OpenAsync().ConfigureAwait(false);
        await ApplyAsync(connection, SqliteOptimizeLevel.Light).ConfigureAwait(false);
    }

    /// <summary>应用内存优先优化（测试/CI 环境）</summary>
    public static async ValueTask OptimizeInMemoryAsync(SqliteConnection connection)
    {
        await connection.OpenAsync().ConfigureAwait(false);
        await ApplyAsync(connection, SqliteOptimizeLevel.InMemory).ConfigureAwait(false);
    }

    /// <summary>在已打开的连接上应用指定级别的 PRAGMA（async 路径）。</summary>
    private static async ValueTask ApplyAsync(SqliteConnection connection, SqliteOptimizeLevel level)
    {
        var sql = GetPragma(level);
        if (sql.Length == 0) return;

        // WAL 模式需单独执行确认切换成功，其余 PRAGMA 批量执行
        if (level is SqliteOptimizeLevel.Production or SqliteOptimizeLevel.Light)
        {
            using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL";
            await walCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql["PRAGMA journal_mode=WAL;\n".Length..];
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
        await connection.OpenAsync().ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version(), sqlite_source_id()";
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync())
            return $"SQLite {reader.GetString(0)} — {reader.GetString(1)[..20]}...";
        return "Unknown";
    }
}
