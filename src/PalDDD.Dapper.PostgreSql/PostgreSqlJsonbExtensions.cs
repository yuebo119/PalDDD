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

        // v33 P3：零长度 keys 客户端 fail-fast——空数组会生成 `payload ?| array[]` 畸形片段，
        // PG 服务端报语法错误（失败延迟到执行期）；对齐 ITM-167 守卫形态（入口统一抛出）
        if (keys.Length == 0)
            throw new ArgumentException("keys 不能为空——零长度数组生成 `?| array[]` 畸形 SQL 片段，PostgreSQL 服务端报错。", nameof(keys));

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

        // v33 P3：零长度 keys 客户端 fail-fast（同 HasAnyKey——`?& array[]` 同型畸形片段）。
        if (keys.Length == 0)
            throw new ArgumentException("keys 不能为空——零长度数组生成 `?& array[]` 畸形 SQL 片段，PostgreSQL 服务端报错。", nameof(keys));

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
    /// 路径段数组。⚠️ 每段不得含逗号或花括号——PG path 数组格式为
    /// <c>'{a,b}'</c>（逗号分隔、元素不带外层引号），元素内逗号会被解释为数组分隔符，
    /// 静默查错嵌套位置；同理不得含花括号 <c>{ }</c>（数组字面量定界符）。
    /// 含逗号/花括号的键请改用原生参数化 SQL。段内单引号已按 SQL 标准翻倍处理（八轮修复）。
    /// 三十七轮 P2-2：违禁字符改为构建期 fail-fast（对齐 SqliteJson.EscapeJsonPathSegment 口径）。
    /// v34 P3 声明：反斜杠段未经转义处理——standard_conforming_strings（PG 默认）下
    /// <c>\</c> 被 PG 数组解析器吃掉，含反斜杠的键路径会静默错位（键名含反斜杠属不支持场景）。
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractTextByPath(string column, params string[] path)
    {
        // ITM-167 修复：补 null/空白守卫（column 与 path 数组及每个元素）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(path);
        foreach (var segment in path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            // 三十七轮 P2-2：PG path 数组语法违禁字符构建期 fail-fast
            if (segment.Contains(',') || segment.Contains('{') || segment.Contains('}'))
                throw new ArgumentException(
                    $"JSON 路径段含违禁字符（逗号或花括号）：\"{segment}\"。PG path 数组语法限制，请改用原生参数化 SQL。", nameof(path));
        }

        // P3 修复（八轮评审）：path 元素内单引号改 SQL 标准翻倍（对齐同文件 EscapeLiteral）——
        // 此前 Replace("'","\\") 的反斜杠转义在 standard_conforming_strings=on（PG 默认）下不生效，
        // 含单引号的 path 元素会提前终止字符串字面量。PG path 数组格式为 '{a,b}'（元素不带外层引号）。
        var p = string.Join(",", path.Select(k => k.Replace("'", "''")));
        return $"{Escape(column)} #>> '{{{p}}}'";
    }

    /// <summary>路径提取 JSON：payload #> '{path,to,key}'</summary>
    /// <param name="path">
    /// 路径段数组（同 <see cref="ExtractTextByPath"/> 的 path 约束：每段不得含逗号/花括号——
    /// 元素内逗号是 PG path 数组分隔符，静默拆段查错位置；花括号是数组字面量定界符）。
    /// ITM-248（F6，对齐姊妹三十七轮 P2-2）：违禁字符由 doc 声明升级为构建期 fail-fast。
    /// v34 P3 声明（对齐姊妹）：反斜杠段未经转义处理——standard_conforming_strings（PG 默认）
    /// 下 <c>\</c> 被 PG 数组解析器吃掉，含反斜杠的键路径会静默错位（键名含反斜杠属不支持场景）。
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractJsonByPath(string column, params string[] path)
    {
        // ITM-167 修复：补 null/空白守卫（同 ExtractTextByPath）。
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(path);
        foreach (var segment in path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            // ITM-248 修复（F6，PD24 对称）：对齐姊妹 ExtractTextByPath（三十七轮 P2-2）——
            // PG path 数组语法违禁字符构建期 fail-fast，不再仅 doc 声明靠调用方自觉
            if (segment.Contains(',') || segment.Contains('{') || segment.Contains('}'))
                throw new ArgumentException(
                    $"JSON 路径段含违禁字符（逗号或花括号）：\"{segment}\"。PG path 数组语法限制，请改用原生参数化 SQL。", nameof(path));
        }

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
    /// <para>
    /// v65 P3（反斜杠声明）：本方法<b>不处理反斜杠</b>。在 PG 默认
    /// <c>standard_conforming_strings=on</c>（PG 9.1+ 默认）下，普通字符串字面量中
    /// <c>\</c> 是字面反斜杠，键名含反斜杠可正常工作；但若服务端显式配置为 <c>off</c>，
    /// <c>\</c> 会被当作转义引导符吃掉，含反斜杠的键静默错查（键名含反斜杠属不支持场景）。
    /// <b>不做反斜杠翻倍</b>：<c>\\</c> 在 <c>on</c> 下被解释为两个字面反斜杠——翻倍会在
    /// 默认配置下破坏当前合法的含反斜杠键，两种服务端配置下不存在统一正确的转义形态，
    /// 故保持现状并声明。需要绝对安全时请改用参数化查询（<c>payload ? @key</c>）。
    /// </para>
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
    /// 值含单引号会提前终止 SQL 字符串——必须再翻倍单引号。key 与 value 两侧均应用本方法
    ///（Include/IncludedBy 中 key/value 都走 <see cref="EscapeJsonValue"/> + 本方法双重转义，
    /// key 虽通常为开发者常量亦不豁免——P3-SRC-404 勘正旧 doc"只对 value 应用"与代码的矛盾）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string EscapeSqlLiteral(string jsonEscaped)
        => jsonEscaped.Replace("'", "''");
}
