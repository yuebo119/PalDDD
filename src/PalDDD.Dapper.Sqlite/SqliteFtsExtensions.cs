// ─────────────────────────────────────────────────────────────
// 🔍 SqliteFtsExtensions — FTS5 全文搜索（AOT 安全，零额外依赖）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ FTS5 已内置在 SQLitePCLRaw.bundle_e_sqlite3 — 零额外包。
//   ✅ 纯 SQL 字符串拼接 — 零反射，零 IL 生成。
//
// FTS5 原理：
//   创建虚拟表（内容表 + 索引），INSERT 时自动分词建索引。
//   查询时使用 MATCH 子句，支持布尔/短语/前缀搜索。
//
// 使用场景：
//   1. Outbox 消息内容搜索 — FTS5 索引 payload 中的关键字段
//   2. Saga 状态搜索 — 按 Saga 数据字段全文检索
//   3. EventLog 事件搜索 — 按事件名/流名模糊搜索
//
// 使用方式：
//   // 创建 FTS5 索引
//   conn.Execute(SqliteFts.CreateEventLogIndex(sourceTable: "event_log"));  // 公共入口（Outbox 表 id 为 TEXT/Ulid，见下方 P1 警告——FTS 仅适用 INTEGER 主键表）
//
//   // 全文搜索
//   conn.QueryAsync<OutboxMessage>(
//     $"SELECT om.* FROM outbox_messages om JOIN {SqliteFts.OutboxIndex} fts ON om.id=fts.rowid " +
//     $"WHERE {SqliteFts.Match("fts", "order AND created")}");
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;

namespace PalDDD.Dapper.Sqlite;

/// <summary>SQLite FTS5 全文搜索工具</summary>
public static class SqliteFts
{
    /// <summary>默认 Outbox FTS5 索引名</summary>
    public const string OutboxIndex = "outbox_messages_fts";

    /// <summary>默认 EventLog FTS5 索引名</summary>
    public const string EventLogIndex = "events_fts";

    // ── 建表 ──

    /// <summary>
    /// 创建 Outbox 消息 FTS5 索引（索引 type + payload）。<br/>
    /// ⚠️ <b>P1 修复（四轮评审探针实证）</b>：FTS5 external content 表要求 rowid 为 INTEGER，
    /// 但 outbox_messages 的 id 是 TEXT/Ulid——此方法生成的 DDL 执行时触发器插入 rowid 即
    /// datatype mismatch。<b>仅适用于含 INTEGER 主键的表</b>（如 events 表的 global_position）。
    /// 对 TEXT 主键表请勿使用；如需全文索引 TEXT 主键表，应改用独立的 FTS 表+显式关联。
    /// </summary>
    /// <param name="sourceTable">源表名（必须含 INTEGER 主键列 id）</param>
    /// <param name="indexName">FTS5 索引名</param>
    public static string CreateOutboxIndex(string sourceTable, string indexName = OutboxIndex)
        => CreateFtsIndex(sourceTable, indexName, "type", "payload", "id");

    /// <summary>创建事件日志 FTS5 索引（索引 event_name + payload，rowid=global_position INTEGER ✅）</summary>
    public static string CreateEventLogIndex(string sourceTable, string indexName = EventLogIndex)
        => CreateFtsIndex(sourceTable, indexName, "event_name", "payload", "global_position");

    /// <summary>
    /// 通用 FTS5 external content 索引构建（P1 修复：触发器名不再嵌套引号标识符——
    /// 此前 trg_{"name"}_ai 形式在名字中部含双引号导致 SQLite 语法错误，探针实证）。
    /// </summary>
    private static string CreateFtsIndex(string sourceTable, string indexName, string col1, string col2, string rowidColumn)
    {
        // v44 P3：入口守卫族（对齐 SqliteJsonExtensions ITM-167 同族——null 列名/表名
        // 直入 Escape 产生 NRE，失败点远离入口）。Escape 内部对 null 行为未定义，前置校验
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(col1);
        ArgumentException.ThrowIfNullOrWhiteSpace(col2);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowidColumn);

        return $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS {Escape(indexName)} USING fts5(
            {col1},
            {col2},
            content='{EscapeLiteral(sourceTable)}',
            content_rowid='{rowidColumn}'
        );

        CREATE TRIGGER IF NOT EXISTS {SanitizeTriggerName(indexName)}_ai AFTER INSERT ON {Escape(sourceTable)} BEGIN
            INSERT INTO {Escape(indexName)}(rowid, {col1}, {col2}) VALUES (NEW.{rowidColumn}, NEW.{col1}, NEW.{col2});
        END;

        CREATE TRIGGER IF NOT EXISTS {SanitizeTriggerName(indexName)}_ad AFTER DELETE ON {Escape(sourceTable)} BEGIN
            INSERT INTO {Escape(indexName)}({Escape(indexName)}, rowid, {col1}, {col2}) VALUES('delete', OLD.{rowidColumn}, OLD.{col1}, OLD.{col2});
        END;

        CREATE TRIGGER IF NOT EXISTS {SanitizeTriggerName(indexName)}_au AFTER UPDATE ON {Escape(sourceTable)} BEGIN
            INSERT INTO {Escape(indexName)}({Escape(indexName)}, rowid, {col1}, {col2}) VALUES('delete', OLD.{rowidColumn}, OLD.{col1}, OLD.{col2});
            INSERT INTO {Escape(indexName)}(rowid, {col1}, {col2}) VALUES (NEW.{rowidColumn}, NEW.{col1}, NEW.{col2});
        END;
        """;
    }

    // ── 查询 ──

    /// <summary>全文搜索 MATCH 子句：fts MATCH 'keywords'</summary>
    /// <param name="index">FTS5 索引名</param>
    /// <param name="query">FTS5 查询语法（支持 AND/OR/NOT、短语、前缀*）。⚠️ 仅做单引号翻倍转义，不拦截 FTS5 查询操作符（AND/OR/NOT/NEAR）；若 query 来自用户输入，调用方须自行校验或白名单限制。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Match(string index, string query)
        => $"{Escape(index)} MATCH '{EscapeFts(query)}'";

    /// <summary>获取搜索结果排序子句：ORDER BY rank</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string OrderByRank(string index = OutboxIndex)
        => $"ORDER BY bm25({Escape(index)})";

    /// <summary>高亮搜索结果片段（返回带标记的匹配文本）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Highlight(string index, int columnIndex, string open = "<b>", string close = "</b>")
        => $"highlight({Escape(index)}, {columnIndex}, '{EscapeFts(open)}', '{EscapeFts(close)}')";

    /// <summary>获取 BM25 相关性分数</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Rank(string index = OutboxIndex)
        => $"bm25({Escape(index)}) AS rank";

    // ── 管理 ──

    /// <summary>重建 FTS5 索引（全量刷新）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Rebuild(string indexName)
        => $"INSERT INTO {Escape(indexName)}({Escape(indexName)}) VALUES('rebuild')";

    /// <summary>优化 FTS5 索引（合并碎片）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Optimize(string indexName)
        => $"INSERT INTO {Escape(indexName)}({Escape(indexName)}) VALUES('optimize')";

    /// <summary>删除 FTS5 索引（连同源表上的三个同步触发器）。</summary>
    /// <remarks>
    /// v45 P2 修复：Drop 只 DROP 虚表不清理触发器——SQLite 触发器宿主是 ON 子句的
    /// 源表，虚表删除不级联；此后对源表任何 DML 会触发器体内 <c>INSERT INTO 已删除的
    /// fts 表</c> 报 "no such table"，源表写路径全断。现产出三条 DROP TRIGGER + 虚表
    /// DROP 的一条批处理（与 <see cref="CreateFtsIndex"/> 的三触发器对称）。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Drop(string indexName)
    {
        var sanitized = SanitizeTriggerName(indexName);
        return $"""
            DROP TRIGGER IF EXISTS {Escape(sanitized + "_ai")};
            DROP TRIGGER IF EXISTS {Escape(sanitized + "_ad")};
            DROP TRIGGER IF EXISTS {Escape(sanitized + "_au")};
            DROP TABLE IF EXISTS {Escape(indexName)};
            """;
    }

    // ── 辅助 ──

    // P2 修复（转义语义拆分）：标识符用双引号包裹（本方法）；单引号字面量内文用
    // EscapeLiteral（单引号翻倍）。此前 content='...' 在单引号字面量内用双引号转义——上下文错配。
    private static string Escape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static string EscapeLiteral(string s) => s.Replace("'", "''");

    /// <summary>P3 修复：触发器名只允许字母数字下划线（SQLite 标识符约束）——非标识符字符剔除。
    /// 三十八轮 P3 修复：下划线保留——原实现剔除后 "outbox-messages" 与 "outbox_messages"
    /// 清洗同名，第二张表的 CREATE TRIGGER IF NOT EXISTS 静默跳过致其 FTS 索引停更。
    /// ⚠️ v37 P3 残余声明：清洗后仍可能碰撞——仅保留字母数字下划线，"a-b" 与 "a.b" 清洗
    /// 同名（仅差被剔字符），后者的触发器静默不创建、其 FTS 索引停更。清洗后碰撞检测不可行
    /// （框架无触发器注册表可查），调用方须保证传入的多个 indexName 清洗后互不相同；
    /// 默认两常量（OutboxIndex 及配套 Content 表名）无碰撞。</summary>
    private static string SanitizeTriggerName(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        return chars.Length > 0 ? new string(chars) : "fts";
    }

    private static string EscapeFts(string s) => s.Replace("'", "''");
}
