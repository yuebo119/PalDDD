// ─────────────────────────────────────────────────────────────
// 📦 PostgreSqlJsonbExtensions — JSONB 原生操作符（AOT 安全）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯 SQL 字符串拼接 — 零反射，零 IL 生成。
//   ✅ 所有操作符在 PostgreSQL 服务端执行，客户端只发 SQL。
//   ✅ 不涉及运行时 JSON 反序列化（那是 Dapper/STJ 的职责）。
//
// PostgreSQL JSONB 操作符速览：
//   @>    包含检查       payload @> '{"Type":"OrderCreated"}'
//   <@    被包含检查       '{"Type":"OrderCreated"}' <@ payload
//   ?     键存在检查       payload ? 'CorrelationId'
//   ?|    任意键存在       payload ?| array['Type','Schema']
//   ?&    所有键存在       payload ?& array['Type','Schema']
//   ->>   提取文本值       payload ->> 'Type'          → "OrderCreated"
//   ->    提取 JSON 值     payload ->  'Headers'        → {"key":"value"}
//   #>    路径提取 JSON     payload #> '{Headers,key}'   → "value"
//   #>>   路径提取文本     payload #>> '{Headers,key}'  → "value"
//
// 使用方式（Dapper）：
//   var sql = $"SELECT * FROM outbox_messages WHERE {PostgreSqlJsonb.Include("payload", "Type", "OrderCreated")}";
//
// 使用方式（SqlKata）：
//   query.WhereRaw(PostgreSqlJsonb.Include("payload", "Type", "OrderCreated"));
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 纯基础设施工具类，零领域逻辑。
//   - 生成的是纯 SQL 片段，直接嵌入 Dapper/SqlKata 查询。
//   - PostgreSQL 专属——其他数据库不支持此语法。
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>PostgreSQL JSONB 操作符工具 — 生成类型安全的 SQL 片段</summary>
public static class PostgreSqlJsonb
{
    // ── 包含操作符（最常用）──

    /// <summary>
    /// 生成 JSONB 包含条件：payload @> '{Key:"Value"}'::jsonb
    /// 💡 <b>安全注意事项</b>：<c>value</c> 参数直接嵌入 SQL 字符串中（无 Dapper 参数化）。
    /// 如果 <c>value</c> 来自用户输入，必须先做 JSON 转义！
    /// 建议使用：<c>JsonEncodedText.Encode(value)</c> 处理后再传入。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Include(string column, string key, string value)
        => $"{Escape(column)} @> '{{\"{Escape(key)}\":\"{Escape(value)}\"}}'::jsonb";

    /// <summary>生成 JSONB 被包含条件（ &lt;@ ）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string IncludedBy(string column, string key, string value)
        => $"'{{\"{Escape(key)}\":\"{Escape(value)}\"}}'::jsonb <@ {Escape(column)}";

    // ── 键存在操作符 ──

    /// <summary>检查 JSONB 中是否存在指定键：payload ? 'Key'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string HasKey(string column, string key)
        => $"{Escape(column)} ? '{Escape(key)}'";

    /// <summary>检查 JSONB 中是否存在任意指定键：payload ?| array['K1','K2']</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string HasAnyKey(string column, params string[] keys)
    {
        var list = string.Join(",", keys.Select(k => $"'{Escape(k)}'"));
        return $"{Escape(column)} ?| array[{list}]";
    }

    /// <summary>检查 JSONB 中是否存在所有指定键：payload ?& array['K1','K2']</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string HasAllKeys(string column, params string[] keys)
    {
        var list = string.Join(",", keys.Select(k => $"'{Escape(k)}'"));
        return $"{Escape(column)} ?& array[{list}]";
    }

    // ── 提取操作符 ──

    /// <summary>提取 JSONB 字段文本值：payload ->> 'Key'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractText(string column, string key)
        => $"{Escape(column)} ->> '{Escape(key)}'";

    /// <summary>提取 JSONB 字段 JSON 值：payload -> 'Key'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractJson(string column, string key)
        => $"{Escape(column)} -> '{Escape(key)}'";

    /// <summary>路径提取文本：payload #>> '{path,to,key}'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractTextByPath(string column, params string[] path)
    {
        var p = string.Join(",", path.Select(k => $"'{Escape(k)}'"));
        return $"{Escape(column)} #>> '{{{p}}}'";
    }

    /// <summary>路径提取 JSON：payload #> '{path,to,key}'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractJsonByPath(string column, params string[] path)
    {
        var p = string.Join(",", path.Select(k => $"'{Escape(k)}'"));
        return $"{Escape(column)} #> '{{{p}}}'";
    }

    // ── 常用于 Outbox / Saga 查询的快捷方法 ──

    /// <summary>按 Outbox 消息类型过滤（payload @> '{"Type":"xxx"}')</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string OutboxByType(string messageType)
        => Include("payload", "Type", messageType);

    /// <summary>按 Saga 状态键过滤（saga_data @> '{"OrderId":"xxx"}')</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string SagaByKey(string key, string value)
        => Include("saga_data", key, value);

    /// <summary>提取 Outbox 消息 Type 字段（payload ->> 'Type')</summary>
    public static string OutboxTypeColumn => "payload ->> 'Type'";

    /// <summary>生成索引友好的 JSONB GIN 索引 SQL</summary>
    public static string CreateGinIndex(string table, string column, string indexName)
        => $"CREATE INDEX IF NOT EXISTS {Escape(indexName)} ON {Escape(table)} USING GIN ({Escape(column)} jsonb_path_ops)";

    // ── 内部：标识符转义（防止 SQL 注入）──

    private static string Escape(string identifier)
    {
        // PostgreSQL 标识符用双引号转义，字面值单引号转义已在上层处理
        return identifier.Contains('"') || identifier.Contains('\'')
            ? identifier.Replace("\"", "\"\"")
            : identifier;
    }
}
