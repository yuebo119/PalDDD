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
    /// 生成 JSONB 包含条件：payload @> '{Key:"Value"}'::jsonb<br/>
    /// 内部执行双重转义：JSON 转义（防 JSON 注入）+ 单引号翻倍（防 SQL 字面量提前终止）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Include(string column, string key, string value)
    {
        // ITM-167 修复：补 null/空白守卫——缺守卫时 null 列名/键值进入转义
        // 生成畸形 SQL 片段，失败延迟到服务端执行期。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return $"{Escape(column)} @> '{{\"{EscapeSqlLiteral(EscapeJsonValue(key))}\":\"{EscapeSqlLiteral(EscapeJsonValue(value))}\"}}'::jsonb";
    }

    /// <summary>生成 JSONB 被包含条件（ &lt;@ ），转义策略同 <see cref="Include"/>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string IncludedBy(string column, string key, string value)
    {
        // ITM-167 修复：补 null/空白守卫（同 Include）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return $"'{{\"{EscapeSqlLiteral(EscapeJsonValue(key))}\":\"{EscapeSqlLiteral(EscapeJsonValue(value))}\"}}'::jsonb <@ {Escape(column)}";
    }

    // ── 键存在操作符 ──

    /// <summary>检查 JSONB 中是否存在指定键：payload ? 'Key'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string HasKey(string column, string key)
    {
        // ITM-167 修复：补 null/空白守卫（同 Include）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"{Escape(column)} ? '{EscapeLiteral(key)}'";
    }

    /// <summary>检查 JSONB 中是否存在任意指定键：payload ?| array['K1','K2']</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string HasAnyKey(string column, params string[] keys)
    {
        // ITM-167 修复：补 null/空白守卫（keys 数组及每个元素）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var k in keys)
            ArgumentException.ThrowIfNullOrWhiteSpace(k);

        var list = string.Join(",", keys.Select(k => $"'{EscapeLiteral(k)}'"));
        return $"{Escape(column)} ?| array[{list}]";
    }

    /// <summary>检查 JSONB 中是否存在所有指定键：payload ?& array['K1','K2']</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string HasAllKeys(string column, params string[] keys)
    {
        // ITM-167 修复：补 null/空白守卫（同 HasAnyKey）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var k in keys)
            ArgumentException.ThrowIfNullOrWhiteSpace(k);

        var list = string.Join(",", keys.Select(k => $"'{EscapeLiteral(k)}'"));
        return $"{Escape(column)} ?& array[{list}]";
    }

    // ── 提取操作符 ──

    /// <summary>提取 JSONB 字段文本值：payload ->> 'Key'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractText(string column, string key)
    {
        // ITM-167 修复：补 null/空白守卫（同 Include）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"{Escape(column)} ->> '{EscapeLiteral(key)}'";
    }

    /// <summary>提取 JSONB 字段 JSON 值：payload -> 'Key'</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractJson(string column, string key)
    {
        // ITM-167 修复：补 null/空白守卫（同 ExtractText）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"{Escape(column)} -> '{EscapeLiteral(key)}'";
    }

    /// <summary>路径提取文本：payload #>> '{path,to,key}'</summary>
    /// <param name="path">
    /// 路径段数组。⚠️ 每段不得含逗号（P3·二十一轮 doc 声明）——PG path 数组格式为
    /// <c>'{a,b}'</c>（逗号分隔、元素不带外层引号），元素内逗号会被解释为数组分隔符，
    /// 静默查错嵌套位置；同理不得含花括号 <c>{ }</c>（数组字面量定界符）。
    /// 含逗号/花括号的键请改用原生参数化 SQL。段内单引号已按 SQL 标准翻倍处理（八轮修复）。
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractTextByPath(string column, params string[] path)
    {
        // ITM-167 修复：补 null/空白守卫（column 与 path 数组及每个元素）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(path);
        foreach (var segment in path)
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);

        // P3 修复（八轮评审）：path 元素内单引号改 SQL 标准翻倍（对齐同文件 EscapeLiteral）——
        // 此前 Replace("'","\\") 的反斜杠转义在 standard_conforming_strings=on（PG 默认）下不生效，
        // 含单引号的 path 元素会提前终止字符串字面量。PG path 数组格式为 '{a,b}'（元素不带外层引号）。
        var p = string.Join(",", path.Select(k => k.Replace("'", "''")));
        return $"{Escape(column)} #>> '{{{p}}}'";
    }

    /// <summary>路径提取 JSON：payload #> '{path,to,key}'</summary>
    /// <param name="path">
    /// 路径段数组（同 <see cref="ExtractTextByPath"/> 的 path 约束：每段不得含逗号/花括号——
    /// P3·二十一轮 doc 声明，元素内逗号是 PG path 数组分隔符，静默拆段查错位置）。
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractJsonByPath(string column, params string[] path)
    {
        // ITM-167 修复：补 null/空白守卫（同 ExtractTextByPath）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(path);
        foreach (var segment in path)
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);

        // P3 修复（八轮评审）：path 元素内单引号改 SQL 标准翻倍（对齐同文件 EscapeLiteral）——
        // 此前 Replace("'","\\") 的反斜杠转义在 standard_conforming_strings=on（PG 默认）下不生效，
        // 含单引号的 path 元素会提前终止字符串字面量。PG path 数组格式为 '{a,b}'（元素不带外层引号）。
        var p = string.Join(",", path.Select(k => k.Replace("'", "''")));
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
    {
        // ITM-167 修复：补 null/空白守卫（同 Include——DDL 标识符更需入口校验）。
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return $"CREATE INDEX IF NOT EXISTS {Escape(indexName)} ON {Escape(table)} USING GIN ({Escape(column)} jsonb_path_ops)";
    }

    // ── 内部：标识符转义（防止 SQL 注入）──

    /// <summary>
    /// PostgreSQL 标识符转义 —— 加外层双引号 + 内部双引号翻倍（P3-1 修复）。
    /// 仅适用于已知可信标识符（列名/表名硬编码）。用户输入必须先白名单校验。
    /// ⚠️ 仅用于标识符位置（列名/表名/索引名）；字符串字面量内文用 <see cref="EscapeLiteral"/>，
    /// JSON 键值用 <see cref="EscapeJsonValue"/>（ITM-062：三种语义不得混用）。
    /// </summary>
    private static string Escape(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// 单引号字符串字面量内文转义 —— 单引号翻倍，不添加外层引号（模板已提供）。
    /// 用于 JSONB 键存在/提取操作符的 'Key' 位置（ITM-062）。
    /// </summary>
    private static string EscapeLiteral(string value)
        => value.Replace("'", "''");

    /// <summary>
    /// JSON 值转义 —— 防止 JSON 注入（P0-FIX-5）。
    /// 转义规则：反斜杠 → \\，双引号 → \"，控制字符 → \uXXXX。
    /// 不做此转义时，攻击者可注入额外 JSON 键绕过条件。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string EscapeJsonValue(string value)
        => System.Text.Json.JsonEncodedText.Encode(value).ToString();

    /// <summary>
    /// SQL 单引号字面量内文转义（P1 修复）：JSON 转义后的值嵌入 '...'::jsonb 字面量时，
    /// 值含单引号会提前终止 SQL 字符串——必须再翻倍单引号。key 同理受影响但通常为
    /// 开发者常量，此处只对 value 应用（key 走 <see cref="EscapeJsonValue"/> + 本方法）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string EscapeSqlLiteral(string jsonEscaped)
        => jsonEscaped.Replace("'", "''");
}
