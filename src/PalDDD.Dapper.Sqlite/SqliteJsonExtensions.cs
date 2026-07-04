// ─────────────────────────────────────────────────────────────
// 📦 SqliteJsonExtensions — SQLite JSON 函数工具（AOT 安全）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯 SQL 字符串拼接 — 零反射，零 IL 生成。
//   ✅ SQLite 内置 JSON1 扩展 — 默认启用，零额外配置。
//
// SQLite JSON 函数（内置，SQLite 3.9+）：
//   json_extract(col, '$.key')    — 提取值（等价 PG col->>'key'）
//   json_type(col)                — 获取 JSON 类型
//   json_valid(col)               — 验证 JSON 有效性
//   json_array_length(col)        — 数组长度
//   json_array('a','b')           — 创建 JSON 数组
//   json_object('k','v')          — 创建 JSON 对象
//   json_each(col)                — 展开数组为行（表值函数）
//   json_tree(col)                — 递归展开为树
//
// 与 PostgreSQL JSONB 操作符对应：
//   PG  @>         → json_extract 组合
//   PG  ->> 'key'  → json_extract(col, '$.key')
//   PG  ?           → json_type(col, '$.key') IS NOT NULL
//
// 使用方式（Dapper）：
//   conn.QueryAsync<OutboxMessage>(
//     $"SELECT * FROM outbox WHERE {SqliteJson.Extract("payload", "Type")} = @type",
//     new { type = "OrderCreated" });
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;

namespace PalDDD.Dapper.Sqlite;

/// <summary>SQLite JSON 函数工具 — 生成类型安全的 SQL 片段</summary>
public static class SqliteJson
{
    // ── 提取操作（最常用）──

    /// <summary>提取 JSON 字段值：json_extract(col, '$.key')</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Extract(string column, string key)
        => $"json_extract({Escape(column)}, '$.{Escape(key)}')";

    /// <summary>提取嵌套 JSON 路径：json_extract(col, '$.a.b.c')</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractPath(string column, params string[] path)
        => $"json_extract({Escape(column)}, '$.{string.Join('.', path.Select(Escape))}')";

    // ── 类型检查 ──

    /// <summary>获取 JSON 字段类型：json_type(col, '$.key') → 'text'/'integer'/...</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Type(string column, string key)
        => $"json_type({Escape(column)}, '$.{Escape(key)}')";

    /// <summary>验证 JSON 有效性：json_valid(col) → 1/0</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string IsValid(string column)
        => $"json_valid({Escape(column)})";

    /// <summary>检查键是否存在：json_type(col, '$.key') IS NOT NULL</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string HasKey(string column, string key)
        => $"{Type(column, key)} IS NOT NULL";

    // ── 数组操作 ──

    /// <summary>JSON 数组长度：json_array_length(col)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ArrayLength(string column)
        => $"json_array_length({Escape(column)})";

    /// <summary>创建 JSON 数组：json_array('a','b','c')</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Array(params string[] values)
        => $"json_array({string.Join(',', values.Select(v => $"'{Escape(v)}'"))})";

    /// <summary>创建 JSON 对象：json_object('k1','v1','k2','v2')</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string BuildObject(params string[] keyValuePairs)
    {
        if (keyValuePairs.Length % 2 != 0)
            throw new ArgumentException("keyValuePairs must have even count.");
        var parts = new string[keyValuePairs.Length];
        for (int i = 0; i < keyValuePairs.Length; i += 2)
        {
            parts[i] = $"'{Escape(keyValuePairs[i])}'";
            parts[i + 1] = $"'{Escape(keyValuePairs[i + 1])}'";
        }
        return $"json_object({string.Join(',', parts)})";
    }

    // ── 表值函数（FROM 子句展开数组）──

    /// <summary>展开 JSON 数组为行：json_each(col) → key/value/type/...</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Each(string column)
        => $"json_each({Escape(column)})";

    /// <summary>递归展开 JSON 树：json_tree(col) → key/value/type/path</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Tree(string column)
        => $"json_tree({Escape(column)})";

    // ── 常用于 Outbox/Saga 查询的快捷方法 ──

    /// <summary>按 Outbox 消息类型过滤（payload JSON 中 Type 字段）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string OutboxByType(string messageType)
        => $"{Extract("payload", "Type")} = '{Escape(messageType)}'";

    // ── 内部 ──

    private static string Escape(string s) => s.Replace("'", "''");
}
