// ─────────────────────────────────────────────────────────────
// 🗄️ DapperConfiguration — 按 DapperDbType 创建数据库连接
// ─────────────────────────────────────────────────────────────
//
// 💡 switch 表达式分发：3 个枚举值，JIT 生成跳表，与 FrozenDictionary 同为 O(1)。
//   添加新数据库只需加一个 case 分支。
//
// ✅ AOT 安全：零反射、零 MakeGenericType。
//
// 📐 DDD 位置：基础设施层 — 数据库连接创建属于基础设施关注点。
// ─────────────────────────────────────────────────────────────

using System.Data.Common;

namespace PalDDD.Dapper;

/// <summary>
/// Dapper 数据库配置 — 根据 <see cref="DapperDbType"/> 创建对应的数据库连接。
/// </summary>
/// <remarks>
/// 使用示例：
/// <code>
///   var conn = DapperConfiguration.Create(DapperDbType.PostgreSql, connectionString);
///   var outbox = new DapperOutboxStore(conn, DapperDbType.PostgreSql);
/// </code>
/// </remarks>
public static class DapperConfiguration
{
    /// <summary>
    /// 根据数据库类型创建连接。<br/>
    /// 💡 返回 DbConnection——调用方负责 Open() 和管理生命周期。
    /// </summary>
    /// <param name="type">数据库类型（Sqlite / PostgreSql / MySql）</param>
    /// <param name="connectionString">数据库连接字符串</param>
    /// <returns>数据库连接</returns>
    /// <exception cref="ArgumentOutOfRangeException">未注册的数据库类型</exception>
    public static DbConnection Create(DapperDbType type, string connectionString)
        => type switch
        {
            DapperDbType.Sqlite => new Microsoft.Data.Sqlite.SqliteConnection(connectionString),
            DapperDbType.PostgreSql => new Npgsql.NpgsqlConnection(connectionString),
            DapperDbType.MySql => new MySqlConnector.MySqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
}
