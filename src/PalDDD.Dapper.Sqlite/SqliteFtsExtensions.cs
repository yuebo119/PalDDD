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
//   conn.Execute(SqliteFts.CreateOutboxIndex("outbox_messages"));
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
        => $"""
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

    /// <summary>删除 FTS5 索引</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Drop(string indexName)
        => $"DROP TABLE IF EXISTS {Escape(indexName)}";

    // ── 辅助 ──

    // P2 修复（转义语义拆分）：标识符用双引号包裹（本方法）；单引号字面量内文用
    // EscapeLiteral（单引号翻倍）。此前 content='...' 在单引号字面量内用双引号转义——上下文错配。
    private static string Escape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static string EscapeLiteral(string s) => s.Replace("'", "''");

    /// <summary>P3 修复：触发器名只允许字母数字下划线（SQLite 标识符约束）——非标识符字符剔除。</summary>
    private static string SanitizeTriggerName(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length > 0 ? new string(chars) : "fts";
    }

    private static string EscapeFts(string s) => s.Replace("'", "''");
}
