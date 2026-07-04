// ─────────────────────────────────────────────────────────────
// 🗄️ DapperDbType — Dapper 数据库类型枚举（基础设施抽象）
// ─────────────────────────────────────────────────────────────
// 💡 放在 PalDDD.Dapper 中，作为 Dapper 基础设施枚举，
//    被 Transaction/EventLog/Projection/Repository 各 Store 共享。

namespace PalDDD.Dapper;

/// <summary>
/// Dapper 数据库类型枚举。<br/>
/// 用于 DapperConfiguration 的策略分发——每种数据库对应一个工厂函数。<br/>
/// 也用于各 Dapper Store 类在运行时选择对应数据库的 SQL 方言。
/// </summary>
public enum DapperDbType
{
    /// <summary>SQLite（内存/文件数据库，零外部依赖）</summary>
    Sqlite,

    /// <summary>PostgreSQL（生产级关系数据库，推荐）</summary>
    PostgreSql,

    /// <summary>MySQL / MariaDB（兼容 MySQL 协议）</summary>
    MySql
}
