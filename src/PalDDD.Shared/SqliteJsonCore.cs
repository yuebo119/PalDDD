namespace PalDDD.Shared;

/// <summary>
/// SQLite JSON1 函数与路径片段共享核心（internal，源码链接进两个方言包）。
/// <para>热路径先计算精确长度再单次写出，避免中间字符串与 StringBuilder 缓冲。</para>
/// </summary>
internal static class SqliteJsonCore
{
    internal static string PathLiteral(IReadOnlyList<string>? segments, string paramName)
        => SqliteSqlCore.BuildPathLiteral(segments, paramName);

    internal static string Extract(string column, IReadOnlyList<string>? path)
        => BuildCall("json_extract", column, path, includeEmptyPath: true);

    internal static string ExtractText(string column, IReadOnlyList<string>? path)
        => BuildOperator("->>", column, path);

    internal static string ExtractJson(string column, IReadOnlyList<string>? path)
        => BuildOperator("->", column, path);

    internal static string Type(string column, IReadOnlyList<string>? path)
        => BuildCall("json_type", column, path, includeEmptyPath: false);

    internal static string IsValid(string column)
        => BuildCall("json_valid", column, null, includeEmptyPath: false);

    internal static string HasKey(string column, IReadOnlyList<string>? path)
        => Type(column, path) + " IS NOT NULL";

    internal static string ArrayLength(string column, IReadOnlyList<string>? path)
        => BuildCall("json_array_length", column, path, includeEmptyPath: false);

    internal static string ArrayLiteral(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return "json_array()";

        var length = 12;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i] ?? throw new ArgumentNullException(nameof(values), "JSON 数组值不得为 null。");
            length += SqliteSqlCore.EscapedLiteralLength(value);
            if (i > 0)
                length += 2;
        }

        return string.Create(length, values, static (span, state) =>
        {
            "json_array(".AsSpan().CopyTo(span);
            var position = 11;
            for (var i = 0; i < state.Count; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                position += SqliteSqlCore.WriteEscapedLiteral(span[position..], state[i]);
            }

            span[position] = ')';
        });
    }

    internal static string ArrayParameters(IReadOnlyList<string> parameterNames)
    {
        ArgumentNullException.ThrowIfNull(parameterNames);
        if (parameterNames.Count == 0)
            return "json_array()";

        var length = 12;
        for (var i = 0; i < parameterNames.Count; i++)
        {
            length += SqliteSqlCore.NormalizedParameterNameLength(parameterNames[i]);
            if (i > 0)
                length += 2;
        }

        return string.Create(length, parameterNames, static (span, state) =>
        {
            "json_array(".AsSpan().CopyTo(span);
            var position = 11;
            for (var i = 0; i < state.Count; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                position += SqliteSqlCore.WriteNormalizedParameterName(span[position..], state[i]);
            }

            span[position] = ')';
        });
    }

    internal static string ObjectLiteral(IReadOnlyList<(string Key, string Value)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
            return "json_object()";

        var length = 13;
        for (var i = 0; i < pairs.Count; i++)
        {
            var (key, value) = pairs[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            length += SqliteSqlCore.EscapedLiteralLength(key) + 2 + SqliteSqlCore.EscapedLiteralLength(value);
            if (i > 0)
                length += 2;
        }

        return string.Create(length, pairs, static (span, state) =>
        {
            "json_object(".AsSpan().CopyTo(span);
            var position = 12;
            for (var i = 0; i < state.Count; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                var (key, value) = state[i];
                position += SqliteSqlCore.WriteEscapedLiteral(span[position..], key);
                span[position++] = ',';
                span[position++] = ' ';
                position += SqliteSqlCore.WriteEscapedLiteral(span[position..], value);
            }

            span[position] = ')';
        });
    }

    internal static string ObjectParameters(IReadOnlyList<(string Key, string ParameterName)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
            return "json_object()";

        var length = 13;
        for (var i = 0; i < pairs.Count; i++)
        {
            var (key, parameterName) = pairs[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            length += SqliteSqlCore.EscapedLiteralLength(key) + 2 + SqliteSqlCore.NormalizedParameterNameLength(parameterName);
            if (i > 0)
                length += 2;
        }

        return string.Create(length, pairs, static (span, state) =>
        {
            "json_object(".AsSpan().CopyTo(span);
            var position = 12;
            for (var i = 0; i < state.Count; i++)
            {
                if (i > 0)
                {
                    span[position++] = ',';
                    span[position++] = ' ';
                }

                var (key, parameterName) = state[i];
                position += SqliteSqlCore.WriteEscapedLiteral(span[position..], key);
                span[position++] = ',';
                span[position++] = ' ';
                position += SqliteSqlCore.WriteNormalizedParameterName(span[position..], parameterName);
            }

            span[position] = ')';
        });
    }

    internal static string SetParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => BuildCallWithParameter("json_set", column, path, parameterName);

    internal static string InsertParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => BuildCallWithParameter("json_insert", column, path, parameterName);

    internal static string ReplaceParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => BuildCallWithParameter("json_replace", column, path, parameterName);

    internal static string PatchParameter(string column, string parameterName)
        => BuildBinaryCall("json_patch", column, parameterName);

    internal static string Remove(string column, IReadOnlyList<string>? path)
        => BuildCall("json_remove", column, path, includeEmptyPath: true);

    internal static string QuoteLiteral(string value)
    {
        var literalLength = SqliteSqlCore.EscapedLiteralLength(value);
        var length = 12 + literalLength;
        return string.Create(length, value, static (span, state) =>
        {
            "json_quote(".AsSpan().CopyTo(span);
            SqliteSqlCore.WriteEscapedLiteral(span[11..], state);
            span[^1] = ')';
        });
    }

    internal static string GroupArray(string column)
        => BuildCall("json_group_array", column, null, includeEmptyPath: false);

    internal static string GroupObject(string keyColumn, string valueColumn)
        => BuildTwoIdentifierCall("json_group_object", keyColumn, valueColumn);

    internal static string Each(string column, IReadOnlyList<string>? path)
        => BuildCall("json_each", column, path, includeEmptyPath: false);

    internal static string Tree(string column, IReadOnlyList<string>? path)
        => BuildCall("json_tree", column, path, includeEmptyPath: false);

    internal static string EqualsLiteral(string column, string value, IReadOnlyList<string>? path)
        => BuildEquals(column, path, value, null);

    internal static string EqualsParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => BuildEquals(column, path, null, parameterName);

    /// <summary>
    /// Outbox 按消息类型过滤：<c>"type" = '类型'</c>。
    /// R13 F6：框架 outbox 表的类型在独立 <c>type</c> 列，payload 是消息体（不含 Type
    /// 字段）——原谓词 json_extract(payload,'$.Type') 对框架表恒 0 行，按类型统计/重投
    /// 会静默得出"无积压"的错误结论。payload 内嵌 Type 的自定义表请直接用
    /// <see cref="EqualsLiteral"/>。
    /// </summary>
    internal static string OutboxByType(string messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        const string Column = "type";
        var length = SqliteSqlCore.EscapedIdentifierLength(Column)
                     + 3
                     + SqliteSqlCore.EscapedLiteralLength(messageType);
        return string.Create(length, messageType, static (span, state) =>
        {
            var position = SqliteSqlCore.WriteEscapedIdentifier(span, Column);
            span[position++] = ' ';
            span[position++] = '=';
            span[position++] = ' ';
            SqliteSqlCore.WriteEscapedLiteral(span[position..], state);
        });
    }

    private static bool HasPath(IReadOnlyList<string>? path, bool includeEmptyPath)
        => path is not null && (includeEmptyPath || path.Count > 0);

    private static int CallLength(string function, string column, IReadOnlyList<string>? path, bool includeEmptyPath)
    {
        var length = function.Length + 1 + SqliteSqlCore.EscapedIdentifierLength(column) + 1;
        if (HasPath(path, includeEmptyPath))
            length += 2 + SqliteSqlCore.PathLiteralLength(path!);
        return length;
    }

    private static int WriteCall(
        Span<char> destination,
        string function,
        string column,
        IReadOnlyList<string>? path,
        bool includeEmptyPath)
    {
        var position = 0;
        function.AsSpan().CopyTo(destination);
        position += function.Length;
        destination[position++] = '(';
        position += SqliteSqlCore.WriteEscapedIdentifier(destination[position..], column);
        if (HasPath(path, includeEmptyPath))
        {
            destination[position++] = ',';
            destination[position++] = ' ';
            position += SqliteSqlCore.WritePathLiteral(destination[position..], path!);
        }

        destination[position++] = ')';
        return position;
    }

    private static string BuildCall(string function, string column, IReadOnlyList<string>? path, bool includeEmptyPath)
    {
        if (includeEmptyPath)
            ArgumentNullException.ThrowIfNull(path, nameof(path));
        if (path is not null)
            SqliteSqlCore.ValidatePathSegments(path, nameof(path));

        var length = CallLength(function, column, path, includeEmptyPath);
        return string.Create(length, (function, column, path, includeEmptyPath), static (span, state) =>
        {
            WriteCall(span, state.function, state.column, state.path, state.includeEmptyPath);
        });
    }

    private static string BuildOperator(string op, string column, IReadOnlyList<string>? path)
    {
        ArgumentNullException.ThrowIfNull(path, nameof(path));
        SqliteSqlCore.ValidatePathSegments(path, nameof(path));

        var identifierLength = SqliteSqlCore.EscapedIdentifierLength(column);
        var pathLength = SqliteSqlCore.PathLiteralLength(path);
        var length = identifierLength + 1 + op.Length + 1 + pathLength;
        return string.Create(length, (op, column, path), static (span, state) =>
        {
            var position = SqliteSqlCore.WriteEscapedIdentifier(span, state.column);
            span[position++] = ' ';
            state.op.AsSpan().CopyTo(span[position..]);
            position += state.op.Length;
            span[position++] = ' ';
            SqliteSqlCore.WritePathLiteral(span[position..], state.path);
        });
    }

    private static string BuildCallWithParameter(
        string function,
        string column,
        IReadOnlyList<string>? path,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, nameof(path));
        SqliteSqlCore.ValidatePathSegments(path, nameof(path));

        var identifierLength = SqliteSqlCore.EscapedIdentifierLength(column);
        var pathLength = SqliteSqlCore.PathLiteralLength(path);
        var parameterLength = SqliteSqlCore.NormalizedParameterNameLength(parameterName);
        var length = function.Length + 1 + identifierLength + 2 + pathLength + 2 + parameterLength + 1;
        return string.Create(length, (function, column, path, parameterName), static (span, state) =>
        {
            var position = 0;
            state.function.AsSpan().CopyTo(span);
            position += state.function.Length;
            span[position++] = '(';
            position += SqliteSqlCore.WriteEscapedIdentifier(span[position..], state.column);
            span[position++] = ',';
            span[position++] = ' ';
            position += SqliteSqlCore.WritePathLiteral(span[position..], state.path);
            span[position++] = ',';
            span[position++] = ' ';
            position += SqliteSqlCore.WriteNormalizedParameterName(span[position..], state.parameterName);
            span[position] = ')';
        });
    }

    private static string BuildBinaryCall(string function, string column, string parameterName)
    {
        var identifierLength = SqliteSqlCore.EscapedIdentifierLength(column);
        var parameterLength = SqliteSqlCore.NormalizedParameterNameLength(parameterName);
        var length = function.Length + 1 + identifierLength + 2 + parameterLength + 1;
        return string.Create(length, (function, column, parameterName), static (span, state) =>
        {
            var position = 0;
            state.function.AsSpan().CopyTo(span);
            position += state.function.Length;
            span[position++] = '(';
            position += SqliteSqlCore.WriteEscapedIdentifier(span[position..], state.column);
            span[position++] = ',';
            span[position++] = ' ';
            position += SqliteSqlCore.WriteNormalizedParameterName(span[position..], state.parameterName);
            span[position] = ')';
        });
    }

    private static string BuildTwoIdentifierCall(string function, string first, string second)
    {
        var firstLength = SqliteSqlCore.EscapedIdentifierLength(first);
        var secondLength = SqliteSqlCore.EscapedIdentifierLength(second);
        var length = function.Length + 1 + firstLength + 2 + secondLength + 1;
        return string.Create(length, (function, first, second), static (span, state) =>
        {
            var position = 0;
            state.function.AsSpan().CopyTo(span);
            position += state.function.Length;
            span[position++] = '(';
            position += SqliteSqlCore.WriteEscapedIdentifier(span[position..], state.first);
            span[position++] = ',';
            span[position++] = ' ';
            position += SqliteSqlCore.WriteEscapedIdentifier(span[position..], state.second);
            span[position] = ')';
        });
    }

    private static string BuildEquals(
        string column,
        IReadOnlyList<string>? path,
        string? literal,
        string? parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, nameof(path));
        SqliteSqlCore.ValidatePathSegments(path, nameof(path));

        var callLength = CallLength("json_extract", column, path, includeEmptyPath: true);
        var rightLength = literal is not null
            ? SqliteSqlCore.EscapedLiteralLength(literal)
            : SqliteSqlCore.NormalizedParameterNameLength(parameterName!);
        var length = callLength + 3 + rightLength;
        return string.Create(length, (column, path, literal, parameterName), static (span, state) =>
        {
            var position = WriteCall(span, "json_extract", state.column, state.path, includeEmptyPath: true);
            span[position++] = ' ';
            span[position++] = '=';
            span[position++] = ' ';
            if (state.literal is not null)
                SqliteSqlCore.WriteEscapedLiteral(span[position..], state.literal);
            else
                SqliteSqlCore.WriteNormalizedParameterName(span[position..], state.parameterName!);
        });
    }
}
