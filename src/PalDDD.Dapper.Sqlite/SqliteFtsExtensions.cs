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

    /// <summary>创建 Outbox 消息 FTS5 索引（索引 type + payload）</summary>
    public static string CreateOutboxIndex(string sourceTable, string indexName = OutboxIndex)
        => $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS {Escape(indexName)} USING fts5(
            type,
            payload,
            content='{Escape(sourceTable)}',
            content_rowid='id'
        );

        -- 触发器：源表 INSERT/UPDATE/DELETE 自动同步 FTS5 索引
        CREATE TRIGGER IF NOT EXISTS trg_{Escape(indexName)}_ai AFTER INSERT ON {Escape(sourceTable)} BEGIN
            INSERT INTO {Escape(indexName)}(rowid, type, payload) VALUES (NEW.id, NEW.type, NEW.payload);
        END;

        CREATE TRIGGER IF NOT EXISTS trg_{Escape(indexName)}_ad AFTER DELETE ON {Escape(sourceTable)} BEGIN
            INSERT INTO {Escape(indexName)}({Escape(indexName)}, rowid, type, payload) VALUES('delete', OLD.id, OLD.type, OLD.payload);
        END;

        CREATE TRIGGER IF NOT EXISTS trg_{Escape(indexName)}_au AFTER UPDATE ON {Escape(sourceTable)} BEGIN
            INSERT INTO {Escape(indexName)}({Escape(indexName)}, rowid, type, payload) VALUES('delete', OLD.id, OLD.type, OLD.payload);
            INSERT INTO {Escape(indexName)}(rowid, type, payload) VALUES (NEW.id, NEW.type, NEW.payload);
        END;
        """;

    /// <summary>创建事件日志 FTS5 索引（索引 event_name + payload）</summary>
    public static string CreateEventLogIndex(string sourceTable, string indexName = EventLogIndex)
        => $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS {Escape(indexName)} USING fts5(
            event_name,
            payload,
            content='{Escape(sourceTable)}',
            content_rowid='global_position'
        );

        CREATE TRIGGER IF NOT EXISTS trg_{Escape(indexName)}_ai AFTER INSERT ON {Escape(sourceTable)} BEGIN
            INSERT INTO {Escape(indexName)}(rowid, event_name, payload)
            VALUES (NEW.global_position, NEW.event_name, NEW.payload);
        END;
        """;

    // ── 查询 ──

    /// <summary>全文搜索 MATCH 子句：fts MATCH 'keywords'</summary>
    /// <param name="index">FTS5 索引名</param>
    /// <param name="query">FTS5 查询语法（支持 AND/OR/NOT、短语、前缀*）</param>
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

    private static string Escape(string s) => s.Replace("\"", "\"\"");

    private static string EscapeFts(string s) => s.Replace("'", "''");
}
