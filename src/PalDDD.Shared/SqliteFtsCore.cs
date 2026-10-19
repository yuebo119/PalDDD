using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace PalDDD.Shared;

internal enum FtsIndexKind
{
    ExternalContent,
    Standalone,
}

internal readonly record struct FtsColumnCore(string Name, bool CastToText);

internal sealed record FtsDefinitionCore(
    string SourceTable,
    string IndexName,
    FtsIndexKind Kind,
    string KeyColumn,
    IReadOnlyList<FtsColumnCore> Columns,
    IReadOnlyList<string> TriggerNames,
    string? MappingTableName,
    IReadOnlyList<string> Statements);

/// <summary>
/// SQLite FTS5 索引定义与查询片段共享核心（internal，源码链接进两个方言包）。
/// </summary>
internal static class SqliteFtsCore
{
    internal const int MaxPrefixIndexLength = 64;

    internal static FtsDefinitionCore CreateExternalContent(
        string sourceTable,
        string indexName,
        string rowidColumn,
        IReadOnlyList<string> columns,
        string? tokenizer,
        IReadOnlyList<int>? prefixIndexes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowidColumn);
        ArgumentNullException.ThrowIfNull(columns);

        ValidateColumnNames(columns);
        var triggerNames = CreateTriggerNames(indexName);
        var index = SqliteSqlCore.EscapeIdentifier(indexName);
        var source = SqliteSqlCore.EscapeIdentifier(sourceTable);
        var rowid = SqliteSqlCore.EscapeIdentifier(rowidColumn);
        var escapedColumns = EscapeIdentifiers(columns);
        var columnList = JoinIdentifiers(escapedColumns);
        var options = BuildFtsOptions(tokenizer, prefixIndexes);

        var createIndex = $"CREATE VIRTUAL TABLE IF NOT EXISTS {index} USING fts5({columnList}, content={SqliteSqlCore.EscapeLiteral(sourceTable)}, content_rowid={SqliteSqlCore.EscapeLiteral(rowidColumn)}{options})";
        var newValues = JoinPrefixedIdentifiers(escapedColumns, "NEW.");
        var oldValues = JoinPrefixedIdentifiers(escapedColumns, "OLD.");

        var createInsert = $"CREATE TRIGGER IF NOT EXISTS {SqliteSqlCore.EscapeIdentifier(triggerNames[0])} AFTER INSERT ON {source} BEGIN INSERT INTO {index}(rowid, {columnList}) VALUES (NEW.{rowid}, {newValues}); END";
        var createDelete = $"CREATE TRIGGER IF NOT EXISTS {SqliteSqlCore.EscapeIdentifier(triggerNames[1])} AFTER DELETE ON {source} BEGIN INSERT INTO {index}({index}, rowid, {columnList}) VALUES ('delete', OLD.{rowid}, {oldValues}); END";
        var createUpdate = $"CREATE TRIGGER IF NOT EXISTS {SqliteSqlCore.EscapeIdentifier(triggerNames[2])} AFTER UPDATE ON {source} BEGIN INSERT INTO {index}({index}, rowid, {columnList}) VALUES ('delete', OLD.{rowid}, {oldValues}); INSERT INTO {index}(rowid, {columnList}) VALUES (NEW.{rowid}, {newValues}); END";

        var coreColumns = new FtsColumnCore[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            coreColumns[i] = new FtsColumnCore(columns[i], false);

        return new FtsDefinitionCore(
            sourceTable,
            indexName,
            FtsIndexKind.ExternalContent,
            rowidColumn,
            coreColumns,
            triggerNames,
            null,
            [createIndex, createInsert, createDelete, createUpdate]);
    }

    internal static FtsDefinitionCore CreateStandalone(
        string sourceTable,
        string indexName,
        string sourceKeyColumn,
        IReadOnlyList<FtsColumnCore> columns,
        string? tokenizer,
        IReadOnlyList<int>? prefixIndexes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKeyColumn);
        ArgumentNullException.ThrowIfNull(columns);

        ValidateColumns(columns);
        var triggerNames = CreateTriggerNames(indexName);
        var mappingTableName = MappingTableName(indexName);
        var index = SqliteSqlCore.EscapeIdentifier(indexName);
        var source = SqliteSqlCore.EscapeIdentifier(sourceTable);
        var key = SqliteSqlCore.EscapeIdentifier(sourceKeyColumn);
        var map = SqliteSqlCore.EscapeIdentifier(mappingTableName);
        var escapedNames = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            escapedNames[i] = SqliteSqlCore.EscapeIdentifier(columns[i].Name);
        var columnList = JoinIdentifiers(escapedNames);
        var options = BuildFtsOptions(tokenizer, prefixIndexes);

        var createMap = $"CREATE TABLE IF NOT EXISTS {map} (\"source_key\" TEXT UNIQUE NOT NULL, \"fts_rowid\" INTEGER PRIMARY KEY AUTOINCREMENT)";
        var createIndex = $"CREATE VIRTUAL TABLE IF NOT EXISTS {index} USING fts5({columnList}{options})";

        var newKey = $"CAST(NEW.{key} AS TEXT)";
        var oldKey = $"CAST(OLD.{key} AS TEXT)";
        var newValues = JoinNewValues(escapedNames, columns);
        var insertMap = $"INSERT INTO {map}(\"source_key\") VALUES ({newKey})";
        var insertIndex = $"INSERT INTO {index}(rowid, {columnList}) VALUES (last_insert_rowid(), {newValues})";
        var deleteIndex = $"DELETE FROM {index} WHERE rowid = (SELECT \"fts_rowid\" FROM {map} WHERE \"source_key\" = {oldKey})";
        var deleteMap = $"DELETE FROM {map} WHERE \"source_key\" = {oldKey}";

        var createInsert = $"CREATE TRIGGER IF NOT EXISTS {SqliteSqlCore.EscapeIdentifier(triggerNames[0])} AFTER INSERT ON {source} BEGIN {insertMap}; {insertIndex}; END";
        var createDelete = $"CREATE TRIGGER IF NOT EXISTS {SqliteSqlCore.EscapeIdentifier(triggerNames[1])} AFTER DELETE ON {source} BEGIN {deleteIndex}; {deleteMap}; END";
        var createUpdate = $"CREATE TRIGGER IF NOT EXISTS {SqliteSqlCore.EscapeIdentifier(triggerNames[2])} AFTER UPDATE ON {source} BEGIN {deleteIndex}; {deleteMap}; {insertMap}; {insertIndex}; END";

        return new FtsDefinitionCore(
            sourceTable,
            indexName,
            FtsIndexKind.Standalone,
            sourceKeyColumn,
            columns,
            triggerNames,
            mappingTableName,
            [createMap, createIndex, createInsert, createDelete, createUpdate]);
    }

    internal static IReadOnlyList<string> CreateTriggerNames(string indexName)
    {
        var sanitized = SqliteSqlCore.SanitizeNameFragment(indexName);
        var hash = SqliteSqlCore.StableHash(indexName);
        return
        [
            $"fts_{sanitized}_{hash}_ai",
            $"fts_{sanitized}_{hash}_ad",
            $"fts_{sanitized}_{hash}_au",
        ];
    }

    internal static string MappingTableName(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return indexName + "_map";
    }

    internal static IReadOnlyList<string> BuildDropStatements(string indexName, bool standalone)
    {
        var triggers = CreateTriggerNames(indexName);
        var statements = new List<string>(5)
        {
            $"DROP TRIGGER IF EXISTS {SqliteSqlCore.EscapeIdentifier(triggers[0])}",
            $"DROP TRIGGER IF EXISTS {SqliteSqlCore.EscapeIdentifier(triggers[1])}",
            $"DROP TRIGGER IF EXISTS {SqliteSqlCore.EscapeIdentifier(triggers[2])}",
            $"DROP TABLE IF EXISTS {SqliteSqlCore.EscapeIdentifier(indexName)}",
        };

        if (standalone)
            statements.Add($"DROP TABLE IF EXISTS {SqliteSqlCore.EscapeIdentifier(MappingTableName(indexName))}");

        return statements;
    }

    internal static IReadOnlyList<string> BuildDropStatements(FtsDefinitionCore definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return BuildDropStatements(definition.IndexName, definition.Kind == FtsIndexKind.Standalone);
    }

    internal static IReadOnlyList<string> BuildDropLegacyStatements(string indexName)
    {
        var sanitized = SqliteSqlCore.SanitizeNameFragment(indexName);
        return
        [
            $"DROP TRIGGER IF EXISTS {SqliteSqlCore.EscapeIdentifier(sanitized + "_ai")}",
            $"DROP TRIGGER IF EXISTS {SqliteSqlCore.EscapeIdentifier(sanitized + "_ad")}",
            $"DROP TRIGGER IF EXISTS {SqliteSqlCore.EscapeIdentifier(sanitized + "_au")}",
            $"DROP TABLE IF EXISTS {SqliteSqlCore.EscapeIdentifier(indexName)}",
        ];
    }

    internal static IReadOnlyList<string> BuildRebuildStatements(FtsDefinitionCore definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var index = SqliteSqlCore.EscapeIdentifier(definition.IndexName);

        if (definition.Kind == FtsIndexKind.ExternalContent)
            return [$"INSERT INTO {index}({index}) VALUES('rebuild')"];

        var source = SqliteSqlCore.EscapeIdentifier(definition.SourceTable);
        var map = SqliteSqlCore.EscapeIdentifier(definition.MappingTableName!);
        var key = SqliteSqlCore.EscapeIdentifier(definition.KeyColumn);
        const string sourceAlias = "\"pal_src\"";
        const string mapAlias = "\"pal_map\"";
        var escapedNames = new string[definition.Columns.Count];
        for (var i = 0; i < definition.Columns.Count; i++)
            escapedNames[i] = SqliteSqlCore.EscapeIdentifier(definition.Columns[i].Name);
        var columnList = JoinIdentifiers(escapedNames);
        var selectValues = JoinSelectValues(escapedNames, definition.Columns, sourceAlias);

        return
        [
            $"DELETE FROM {index}",
            $"DELETE FROM {map}",
            $"INSERT INTO {map}(\"source_key\") SELECT CAST({sourceAlias}.{key} AS TEXT) FROM {source} AS {sourceAlias}",
            $"INSERT INTO {index}(rowid, {columnList}) SELECT {mapAlias}.\"fts_rowid\", {selectValues} FROM {source} AS {sourceAlias} JOIN {map} AS {mapAlias} ON {mapAlias}.\"source_key\" = CAST({sourceAlias}.{key} AS TEXT)",
        ];
    }

    internal static IReadOnlyList<string> BuildOptimizeStatements(FtsDefinitionCore definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var index = SqliteSqlCore.EscapeIdentifier(definition.IndexName);
        return [$"INSERT INTO {index}({index}) VALUES('optimize')"];
    }

    internal static string BuildJoinClause(FtsDefinitionCore definition, string sourceAlias, string mapAlias)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var source = SqliteSqlCore.QuoteAlias(sourceAlias, nameof(sourceAlias));
        var map = SqliteSqlCore.QuoteAlias(mapAlias, nameof(mapAlias));
        if (string.Equals(sourceAlias, mapAlias, StringComparison.Ordinal))
            throw new ArgumentException("sourceAlias 与 mapAlias 不得相同。", nameof(mapAlias));

        var index = SqliteSqlCore.EscapeIdentifier(definition.IndexName);
        var key = SqliteSqlCore.EscapeIdentifier(definition.KeyColumn);

        if (definition.Kind == FtsIndexKind.ExternalContent)
            return $"JOIN {index} ON {index}.rowid = {source}.{key}";

        var mapping = SqliteSqlCore.EscapeIdentifier(definition.MappingTableName!);
        return $"JOIN {mapping} AS {map} ON {map}.\"source_key\" = CAST({source}.{key} AS TEXT) JOIN {index} ON {index}.rowid = {map}.\"fts_rowid\"";
    }

    internal static string MatchExpression(string index)
    {
        var identifierLength = SqliteSqlCore.EscapedIdentifierLength(index);
        var length = identifierLength + 6;
        return string.Create(length, index, static (span, state) =>
        {
            var position = SqliteSqlCore.WriteEscapedIdentifier(span, state);
            " MATCH".AsSpan().CopyTo(span[position..]);
        });
    }

    internal static string MatchLiteral(string index, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var identifierLength = SqliteSqlCore.EscapedIdentifierLength(index);
        var literalLength = SqliteSqlCore.EscapedLiteralLength(query);
        var length = identifierLength + 7 + literalLength;
        return string.Create(length, (index, query), static (span, state) =>
        {
            var position = SqliteSqlCore.WriteEscapedIdentifier(span, state.index);
            " MATCH ".AsSpan().CopyTo(span[position..]);
            position += 7;
            SqliteSqlCore.WriteEscapedLiteral(span[position..], state.query);
        });
    }

    internal static string MatchParameter(string index, string parameterName)
    {
        var identifierLength = SqliteSqlCore.EscapedIdentifierLength(index);
        var parameterLength = SqliteSqlCore.NormalizedParameterNameLength(parameterName);
        var length = identifierLength + 7 + parameterLength;
        return string.Create(length, (index, parameterName), static (span, state) =>
        {
            var position = SqliteSqlCore.WriteEscapedIdentifier(span, state.index);
            " MATCH ".AsSpan().CopyTo(span[position..]);
            position += 7;
            SqliteSqlCore.WriteNormalizedParameterName(span[position..], state.parameterName);
        });
    }

    internal static string OrderByRank(string index, IReadOnlyList<double>? weights)
        => "ORDER BY " + Bm25(index, weights);

    internal static string Rank(string index, IReadOnlyList<double>? weights)
        => Bm25(index, weights) + " AS rank";

    internal static string Highlight(string index, int columnIndex, string open, string close)
    {
        if (columnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(columnIndex), columnIndex, "FTS5 列索引必须 >= 0。");
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);
        return $"highlight({SqliteSqlCore.EscapeIdentifier(index)}, {columnIndex}, {SqliteSqlCore.EscapeLiteral(open)}, {SqliteSqlCore.EscapeLiteral(close)})";
    }

    internal static string Snippet(string index, int columnIndex, string start, string end, string ellipsis, int tokenCount)
    {
        if (columnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(columnIndex), columnIndex, "FTS5 列索引必须 >= 0。");
        if (tokenCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokenCount), tokenCount, "snippet token 数必须 >= 1。");
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(ellipsis);
        return $"snippet({SqliteSqlCore.EscapeIdentifier(index)}, {columnIndex}, {SqliteSqlCore.EscapeLiteral(start)}, {SqliteSqlCore.EscapeLiteral(end)}, {SqliteSqlCore.EscapeLiteral(ellipsis)}, {tokenCount})";
    }

    internal static async Task ValidateAsync(DbConnection connection, FtsDefinitionCore definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tableColumns = await ReadTableInfoAsync(connection, definition.SourceTable, cancellationToken).ConfigureAwait(false);
        if (tableColumns.Count == 0)
            throw new InvalidOperationException($"源表 {definition.SourceTable} 不存在或没有列。");

        if (!tableColumns.TryGetValue(definition.KeyColumn, out var keyInfo))
            throw new InvalidOperationException($"源表 {definition.SourceTable} 缺少键列 {definition.KeyColumn}。");

        foreach (var column in definition.Columns)
        {
            if (!tableColumns.ContainsKey(column.Name))
                throw new InvalidOperationException($"源表 {definition.SourceTable} 缺少列 {column.Name}。");
        }

        if (definition.Kind == FtsIndexKind.ExternalContent
            && (keyInfo.Pk != 1 || !string.Equals(keyInfo.Type, "INTEGER", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"external-content FTS5 要求键列 {definition.SourceTable}.{definition.KeyColumn} 是 INTEGER PRIMARY KEY；实际 type={keyInfo.Type}, pk={keyInfo.Pk}。请改用 CreateStandaloneIndex。");
        }
    }

    private static string Bm25(string index, IReadOnlyList<double>? weights)
    {
        var escaped = SqliteSqlCore.EscapeIdentifier(index);
        if (weights is null || weights.Count == 0)
            return $"bm25({escaped})";

        var builder = new StringBuilder(16 + weights.Count * 8);
        builder.Append("bm25(").Append(escaped);
        foreach (var weight in weights)
        {
            if (!double.IsFinite(weight))
                throw new ArgumentOutOfRangeException(nameof(weights), weight, "BM25 权重必须是有限数值。");
            builder.Append(", ").Append(weight.ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static void ValidateColumnNames(IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
            throw new ArgumentException("至少需要一个 FTS5 索引列。", nameof(columns));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column);
            if (string.Equals(column, "rowid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(column, "rank", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"列名 {column} 与 FTS5 保留列冲突。", nameof(columns));
            if (!seen.Add(column))
                throw new ArgumentException($"列名 {column} 重复。", nameof(columns));
        }
    }

    private static void ValidateColumns(IReadOnlyList<FtsColumnCore> columns)
    {
        if (columns.Count == 0)
            throw new ArgumentException("至少需要一个 FTS5 索引列。", nameof(columns));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column.Name);
            if (string.Equals(column.Name, "rowid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Name, "rank", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"列名 {column.Name} 与 FTS5 保留列冲突。", nameof(columns));
            if (!seen.Add(column.Name))
                throw new ArgumentException($"列名 {column.Name} 重复。", nameof(columns));
        }
    }

    private static string BuildFtsOptions(string? tokenizer, IReadOnlyList<int>? prefixIndexes)
    {
        var builder = new StringBuilder();
        if (tokenizer is not null)
        {
            ValidateTokenizer(tokenizer);
            builder.Append(", tokenize = ").Append(SqliteSqlCore.EscapeLiteral(tokenizer));
        }

        if (prefixIndexes is { Count: > 0 })
        {
            var parts = new string[prefixIndexes.Count];
            for (var i = 0; i < prefixIndexes.Count; i++)
            {
                var value = prefixIndexes[i];
                if (value is <= 0 or > MaxPrefixIndexLength)
                    throw new ArgumentOutOfRangeException(nameof(prefixIndexes), value, $"FTS5 前缀索引长度必须在 1..{MaxPrefixIndexLength}。");
                parts[i] = value.ToString(CultureInfo.InvariantCulture);
            }

            builder.Append(", prefix = ").Append(SqliteSqlCore.EscapeLiteral(string.Join(' ', parts)));
        }

        return builder.ToString();
    }

    private static void ValidateTokenizer(string tokenizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizer);
        foreach (var c in tokenizer)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ' '))
                throw new ArgumentException($"tokenizer 含非法字符 '{c}'；仅允许 ASCII 字母、数字、空格、下划线、连字符、点。", nameof(tokenizer));
        }
    }

    private static string[] EscapeIdentifiers(IReadOnlyList<string> values)
    {
        var escaped = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
            escaped[i] = SqliteSqlCore.EscapeIdentifier(values[i]);
        return escaped;
    }

    private static string JoinIdentifiers(string[] escaped)
    {
        if (escaped.Length == 0)
            return string.Empty;

        var length = 0;
        for (var i = 0; i < escaped.Length; i++)
            length += escaped[i].Length + (i > 0 ? 2 : 0);

        return string.Create(length, escaped, static (span, state) =>
        {
            var position = 0;
            for (var i = 0; i < state.Length; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                state[i].AsSpan().CopyTo(span[position..]);
                position += state[i].Length;
            }
        });
    }

    private static string JoinPrefixedIdentifiers(string[] escaped, string prefix)
    {
        if (escaped.Length == 0)
            return string.Empty;

        var length = 0;
        for (var i = 0; i < escaped.Length; i++)
            length += prefix.Length + escaped[i].Length + (i > 0 ? 2 : 0);

        return string.Create(length, (prefix, escaped), static (span, state) =>
        {
            var position = 0;
            for (var i = 0; i < state.escaped.Length; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                state.prefix.AsSpan().CopyTo(span[position..]);
                position += state.prefix.Length;
                state.escaped[i].AsSpan().CopyTo(span[position..]);
                position += state.escaped[i].Length;
            }
        });
    }

    private static string JoinNewValues(string[] escapedNames, IReadOnlyList<FtsColumnCore> columns)
    {
        var length = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            length += escapedNames[i].Length + (columns[i].CastToText ? 18 : 4);
            if (i > 0)
                length += 2;
        }

        return string.Create(length, (escapedNames, columns), static (span, state) =>
        {
            var position = 0;
            for (var i = 0; i < state.columns.Count; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                if (state.columns[i].CastToText)
                {
                    "CAST(NEW.".AsSpan().CopyTo(span[position..]);
                    position += 9;
                    state.escapedNames[i].AsSpan().CopyTo(span[position..]);
                    position += state.escapedNames[i].Length;
                    " AS TEXT)".AsSpan().CopyTo(span[position..]);
                    position += 9;
                }
                else
                {
                    "NEW.".AsSpan().CopyTo(span[position..]);
                    position += 4;
                    state.escapedNames[i].AsSpan().CopyTo(span[position..]);
                    position += state.escapedNames[i].Length;
                }
            }
        });
    }

    private static string JoinSelectValues(
        string[] escapedNames,
        IReadOnlyList<FtsColumnCore> columns,
        string sourceAlias)
    {
        var length = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            length += sourceAlias.Length + 1 + escapedNames[i].Length;
            if (columns[i].CastToText)
                length += 14;
            if (i > 0)
                length += 2;
        }

        return string.Create(length, (sourceAlias, escapedNames, columns), static (span, state) =>
        {
            var position = 0;
            for (var i = 0; i < state.columns.Count; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                if (state.columns[i].CastToText)
                {
                    "CAST(".AsSpan().CopyTo(span[position..]);
                    position += 5;
                }

                state.sourceAlias.AsSpan().CopyTo(span[position..]);
                position += state.sourceAlias.Length;
                span[position++] = '.';
                state.escapedNames[i].AsSpan().CopyTo(span[position..]);
                position += state.escapedNames[i].Length;

                if (state.columns[i].CastToText)
                {
                    " AS TEXT)".AsSpan().CopyTo(span[position..]);
                    position += 9;
                }
            }
        });
    }

    private static async Task<Dictionary<string, ColumnInfo>> ReadTableInfoAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
#pragma warning disable S2077 // 表名经 EscapeIdentifier 双引号转义；PRAGMA 不支持参数绑定
        command.CommandText = "PRAGMA table_info(" + SqliteSqlCore.EscapeIdentifier(table) + ")";
#pragma warning restore S2077
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var pk = reader.GetInt32(5);
            columns[name] = new ColumnInfo(type, pk);
        }

        return columns;
    }

    private readonly record struct ColumnInfo(string Type, int Pk);
}
