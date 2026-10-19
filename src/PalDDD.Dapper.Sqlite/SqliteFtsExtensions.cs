using System.Data.Common;
using PalDDD.Shared;

namespace PalDDD.Dapper.Sqlite;

/// <summary>
/// SQLite FTS5 索引定义与查询片段生成。
/// <para>所有标识符、字面量与路径在构建期转义；值优先走参数化方法，字面量方法只服务常量。</para>
/// </summary>
public static class SqliteFts
{
    /// <summary>默认 EventLog FTS5 索引名。</summary>
    public const string EventLogIndex = "events_fts";

    /// <summary>默认 Outbox FTS5 索引名。</summary>
    public const string OutboxIndex = "outbox_messages_fts";

    /// <summary>
    /// 创建 external-content FTS5 索引定义。
    /// <para><b>约束</b>：<paramref name="rowidColumn"/> 必须是源表的
    /// <c>INTEGER PRIMARY KEY</c> 列。TEXT/ULID 主键请改用
    /// <see cref="CreateStandaloneIndex"/>；否则触发器写入会报 datatype mismatch。
    /// 可用 <see cref="ValidateAsync"/> 在 DDL 前校验。</para>
    /// </summary>
    public static SqliteFtsIndexDefinition CreateExternalContentIndex(
        string sourceTable,
        string indexName,
        string rowidColumn,
        IReadOnlyList<string> columns,
        string? tokenizer = null,
        IReadOnlyList<int>? prefixIndexes = null)
    {
        var core = SqliteFtsCore.CreateExternalContent(
            sourceTable, indexName, rowidColumn, columns, tokenizer, prefixIndexes);
        return new SqliteFtsIndexDefinition(core);
    }

    /// <summary>
    /// 创建 standalone FTS5 索引定义（普通 FTS5 表 + 映射表 + 三个同步触发器）。
    /// <para>适用于任意主键类型，包括 TEXT/ULID。键列在触发器中 CAST 成 TEXT 写入映射表。</para>
    /// </summary>
    public static SqliteFtsIndexDefinition CreateStandaloneIndex(
        string sourceTable,
        string indexName,
        string sourceKeyColumn,
        IReadOnlyList<SqliteFtsColumn> columns,
        string? tokenizer = null,
        IReadOnlyList<int>? prefixIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var coreColumns = new FtsColumnCore[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(column.Name);
            coreColumns[i] = new FtsColumnCore(column.Name, column.CastToText);
        }

        var core = SqliteFtsCore.CreateStandalone(
            sourceTable, indexName, sourceKeyColumn, coreColumns, tokenizer, prefixIndexes);
        return new SqliteFtsIndexDefinition(core);
    }

    /// <summary>
    /// EventLog 默认索引：standalone，键 <c>global_position</c>，列 <c>event_name</c>
    /// 与（可选）<c>payload</c>。payload 默认 CAST 成 TEXT，兼容 BLOB 存储。
    /// </summary>
    public static SqliteFtsIndexDefinition CreateEventLogIndex(
        string sourceTable = "events",
        bool includePayload = true,
        string? tokenizer = null,
        IReadOnlyList<int>? prefixIndexes = null)
    {
        var columns = new List<SqliteFtsColumn>(2) { new("event_name") };
        if (includePayload)
            columns.Add(new SqliteFtsColumn("payload", CastToText: true));
        return CreateStandaloneIndex(sourceTable, EventLogIndex, "global_position", columns, tokenizer, prefixIndexes);
    }

    /// <summary>
    /// Outbox 默认索引：standalone，键 <c>id</c>（TEXT/ULID 安全），列 <c>type</c>
    /// 与（可选）<c>payload</c>。payload 默认 CAST 成 TEXT，兼容 BLOB 存储。
    /// </summary>
    public static SqliteFtsIndexDefinition CreateOutboxIndex(
        string sourceTable = "outbox_messages",
        bool includePayload = true,
        string? tokenizer = null,
        IReadOnlyList<int>? prefixIndexes = null)
    {
        var columns = new List<SqliteFtsColumn>(2) { new("type") };
        if (includePayload)
            columns.Add(new SqliteFtsColumn("payload", CastToText: true));
        return CreateStandaloneIndex(sourceTable, OutboxIndex, "id", columns, tokenizer, prefixIndexes);
    }

    /// <summary>
    /// 执行 DDL 前校验源表：所有索引列必须存在；external-content 的键列必须是
    /// <c>INTEGER PRIMARY KEY</c>。连接未打开时会自动打开。
    /// </summary>
    public static Task ValidateAsync(
        DbConnection connection,
        SqliteFtsIndexDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SqliteFtsCore.ValidateAsync(connection, definition.Core, cancellationToken);
    }

    /// <summary>生成 <c>"索引" MATCH '查询'</c>。查询串只做单引号翻倍，FTS5 操作符不拦截，用户输入请改用 <see cref="MatchParameter"/>。</summary>
    public static string MatchLiteral(string index, string query)
        => SqliteFtsCore.MatchLiteral(index, query);

    /// <summary>生成 <c>"索引" MATCH @参数</c>。参数名可写 <c>q</c> 或 <c>@q</c>。</summary>
    public static string MatchParameter(string index, string parameterName)
        => SqliteFtsCore.MatchParameter(index, parameterName);

    /// <summary>生成 <c>"索引" MATCH</c>，由调用方拼接参数或 FTS5 表达式。</summary>
    public static string MatchExpression(string index)
        => SqliteFtsCore.MatchExpression(index);

    /// <summary>生成 <c>ORDER BY bm25("索引")</c>，可选每列权重。</summary>
    public static string OrderByRank(string index, params double[] weights)
        => SqliteFtsCore.OrderByRank(index, weights);

    /// <summary>生成 <c>bm25("索引") AS rank</c>，可选每列权重。</summary>
    public static string Rank(string index, params double[] weights)
        => SqliteFtsCore.Rank(index, weights);

    /// <summary>生成 <c>highlight</c> 片段，列索引从 0 开始。</summary>
    public static string Highlight(string index, int columnIndex, string open = "<b>", string close = "</b>")
        => SqliteFtsCore.Highlight(index, columnIndex, open, close);

    /// <summary>生成 <c>snippet</c> 片段，列索引从 0 开始。</summary>
    public static string Snippet(
        string index,
        int columnIndex,
        string start = "<b>",
        string end = "</b>",
        string ellipsis = "...",
        int tokenCount = 64)
        => SqliteFtsCore.Snippet(index, columnIndex, start, end, ellipsis, tokenCount);

    /// <summary>返回三个同步触发器的完整名称（插入/删除/更新）。</summary>
    public static IReadOnlyList<string> TriggerNames(string indexName)
        => SqliteFtsCore.CreateTriggerNames(indexName);

    /// <summary>删除索引：三个触发器、FTS5 虚表；standalone 还删除映射表。</summary>
    public static IReadOnlyList<string> DropStatements(string indexName, bool standalone = false)
        => SqliteFtsCore.BuildDropStatements(indexName, standalone);

    /// <summary>
    /// 清理 v4 旧触发器名（<c>{清洗名}_ai/ad/au</c>）与旧 FTS5 虚表。
    /// v4 升级到 v5 时先执行本方法，再创建新索引。
    /// </summary>
    public static IReadOnlyList<string> DropLegacyStatements(string indexName)
        => SqliteFtsCore.BuildDropLegacyStatements(indexName);
}
