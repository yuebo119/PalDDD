namespace PalDDD.Shared;

/// <summary>
/// SQLite JSON1 函数与路径片段共享核心（internal，源码链接进两个方言包）。
/// </summary>
internal static class SqliteJsonCore
{
    internal static string PathLiteral(IReadOnlyList<string>? segments, string paramName)
        => SqliteSqlCore.BuildPathLiteral(segments, paramName);

    internal static string Extract(string column, IReadOnlyList<string>? path)
        => $"json_extract({SqliteSqlCore.EscapeIdentifier(column)}, {PathLiteral(path, nameof(path))})";

    internal static string ExtractText(string column, IReadOnlyList<string>? path)
        => $"{SqliteSqlCore.EscapeIdentifier(column)} ->> {PathLiteral(path, nameof(path))}";

    internal static string ExtractJson(string column, IReadOnlyList<string>? path)
        => $"{SqliteSqlCore.EscapeIdentifier(column)} -> {PathLiteral(path, nameof(path))}";

    internal static string Type(string column, IReadOnlyList<string>? path)
    {
        var escaped = SqliteSqlCore.EscapeIdentifier(column);
        return path is null || path.Count == 0
            ? $"json_type({escaped})"
            : $"json_type({escaped}, {PathLiteral(path, nameof(path))})";
    }

    internal static string IsValid(string column)
        => $"json_valid({SqliteSqlCore.EscapeIdentifier(column)})";

    internal static string HasKey(string column, IReadOnlyList<string>? path)
        => Type(column, path) + " IS NOT NULL";

    internal static string ArrayLength(string column, IReadOnlyList<string>? path)
    {
        var escaped = SqliteSqlCore.EscapeIdentifier(column);
        return path is null || path.Count == 0
            ? $"json_array_length({escaped})"
            : $"json_array_length({escaped}, {PathLiteral(path, nameof(path))})";
    }

    internal static string ArrayLiteral(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return "json_array()";

        var builder = new System.Text.StringBuilder(16 + values.Count * 8);
        builder.Append("json_array(");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(SqliteSqlCore.EscapeLiteral(values[i] ?? throw new ArgumentNullException(nameof(values), "JSON 数组值不得为 null。")));
        }

        builder.Append(')');
        return builder.ToString();
    }

    internal static string ArrayParameters(IReadOnlyList<string> parameterNames)
    {
        ArgumentNullException.ThrowIfNull(parameterNames);
        if (parameterNames.Count == 0)
            return "json_array()";

        var builder = new System.Text.StringBuilder(16 + parameterNames.Count * 8);
        builder.Append("json_array(");
        for (var i = 0; i < parameterNames.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(SqliteSqlCore.NormalizeParameterName(parameterNames[i]));
        }

        builder.Append(')');
        return builder.ToString();
    }

    internal static string ObjectLiteral(IReadOnlyList<(string Key, string Value)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
            return "json_object()";

        var builder = new System.Text.StringBuilder(16 + pairs.Count * 16);
        builder.Append("json_object(");
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            var (key, value) = pairs[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            builder.Append(SqliteSqlCore.EscapeLiteral(key)).Append(", ").Append(SqliteSqlCore.EscapeLiteral(value));
        }

        builder.Append(')');
        return builder.ToString();
    }

    internal static string ObjectParameters(IReadOnlyList<(string Key, string ParameterName)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
            return "json_object()";

        var builder = new System.Text.StringBuilder(16 + pairs.Count * 16);
        builder.Append("json_object(");
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            var (key, parameterName) = pairs[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            builder.Append(SqliteSqlCore.EscapeLiteral(key)).Append(", ").Append(SqliteSqlCore.NormalizeParameterName(parameterName));
        }

        builder.Append(')');
        return builder.ToString();
    }

    internal static string SetParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => $"json_set({SqliteSqlCore.EscapeIdentifier(column)}, {PathLiteral(path, nameof(path))}, {SqliteSqlCore.NormalizeParameterName(parameterName)})";

    internal static string InsertParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => $"json_insert({SqliteSqlCore.EscapeIdentifier(column)}, {PathLiteral(path, nameof(path))}, {SqliteSqlCore.NormalizeParameterName(parameterName)})";

    internal static string ReplaceParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => $"json_replace({SqliteSqlCore.EscapeIdentifier(column)}, {PathLiteral(path, nameof(path))}, {SqliteSqlCore.NormalizeParameterName(parameterName)})";

    internal static string PatchParameter(string column, string parameterName)
        => $"json_patch({SqliteSqlCore.EscapeIdentifier(column)}, {SqliteSqlCore.NormalizeParameterName(parameterName)})";

    internal static string Remove(string column, IReadOnlyList<string>? path)
        => $"json_remove({SqliteSqlCore.EscapeIdentifier(column)}, {PathLiteral(path, nameof(path))})";

    internal static string QuoteLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"json_quote({SqliteSqlCore.EscapeLiteral(value)})";
    }

    internal static string GroupArray(string column)
        => $"json_group_array({SqliteSqlCore.EscapeIdentifier(column)})";

    internal static string GroupObject(string keyColumn, string valueColumn)
        => $"json_group_object({SqliteSqlCore.EscapeIdentifier(keyColumn)}, {SqliteSqlCore.EscapeIdentifier(valueColumn)})";

    internal static string Each(string column, IReadOnlyList<string>? path)
    {
        var escaped = SqliteSqlCore.EscapeIdentifier(column);
        return path is null || path.Count == 0
            ? $"json_each({escaped})"
            : $"json_each({escaped}, {PathLiteral(path, nameof(path))})";
    }

    internal static string Tree(string column, IReadOnlyList<string>? path)
    {
        var escaped = SqliteSqlCore.EscapeIdentifier(column);
        return path is null || path.Count == 0
            ? $"json_tree({escaped})"
            : $"json_tree({escaped}, {PathLiteral(path, nameof(path))})";
    }

    internal static string EqualsLiteral(string column, string value, IReadOnlyList<string>? path)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Extract(column, path) + " = " + SqliteSqlCore.EscapeLiteral(value);
    }

    internal static string EqualsParameter(string column, string parameterName, IReadOnlyList<string>? path)
        => Extract(column, path) + " = " + SqliteSqlCore.NormalizeParameterName(parameterName);

    internal static string OutboxByType(string messageType)
        => EqualsLiteral("payload", messageType, ["Type"]);
}
