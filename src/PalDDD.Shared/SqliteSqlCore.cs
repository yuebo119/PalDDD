using System.Globalization;
using System.Text;

namespace PalDDD.Shared;

/// <summary>
/// SQLite SQL 片段共享核心（internal，源码链接进 Dapper/PalORM 两个方言包）。
/// <para>只依赖 BCL；标识符、字面量、参数名、JSON 路径的转义规则在此单一出处。</para>
/// </summary>
internal static class SqliteSqlCore
{
    /// <summary>双引号包裹标识符，内部双引号翻倍。</summary>
    internal static string EscapeIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>单引号包裹 SQL 字面量，内部单引号翻倍。</summary>
    internal static string EscapeLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// 规范化 SQL 参数名：接受 <c>q</c> 或 <c>@q</c>，返回 <c>@q</c>。
    /// 仅允许 ASCII 字母、数字、下划线，且首字符不得为数字。
    /// </summary>
    internal static string NormalizeParameterName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var bare = name[0] == '@' ? name[1..] : name;
        if (bare.Length == 0)
            throw new ArgumentException("参数名不得只有 '@'。", nameof(name));

        for (var i = 0; i < bare.Length; i++)
        {
            var c = bare[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"参数名含非法字符 '{c}'；仅允许 ASCII 字母、数字、下划线。", nameof(name));
            if (i == 0 && char.IsAsciiDigit(c))
                throw new ArgumentException("参数名不得以数字开头。", nameof(name));
        }

        return "@" + bare;
    }

    /// <summary>触发器名清洗片段：只保留 ASCII 字母、数字、下划线；空结果回退 ft。</summary>
    internal static string SanitizeNameFragment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var buffer = new char[value.Length];
        var count = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
                buffer[count++] = c;
        }

        return count == 0 ? "fts" : new string(buffer, 0, count);
    }

    /// <summary>FNV-1a 64 位稳定哈希，16 位小写十六进制。</summary>
    internal static string StableHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hash = 14695981039346656037UL;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>校验 JSON 路径段：非空、无 NUL；空集合合法（表示根路径）。</summary>
    internal static void ValidatePathSegments(IReadOnlyList<string>? segments, string paramName)
    {
        ArgumentNullException.ThrowIfNull(segments, paramName);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (string.IsNullOrWhiteSpace(segment))
                throw new ArgumentException("JSON 路径段不得为空。", paramName);
            if (segment.Contains('\0'))
                throw new ArgumentException("JSON 路径段不得含 NUL 字符。", paramName);
        }
    }

    /// <summary>
    /// 构造 SQLite JSON 路径字面量（含外层单引号）。
    /// 对象键始终用双引号包裹；键内反斜杠与双引号按 SQLite 3.45+ 路径规则转义。
    /// </summary>
    internal static string BuildPathLiteral(IReadOnlyList<string>? segments, string paramName)
    {
        ValidatePathSegments(segments, paramName);
        var builder = new StringBuilder(3 + segments!.Count * 8);
        builder.Append("'$");
        foreach (var segment in segments)
        {
            builder.Append(".\"");
            foreach (var c in segment)
            {
                if (c == '\\')
                    builder.Append("\\\\");
                else if (c == '"')
                    builder.Append("\\\"");
                else
                    builder.Append(c);
            }

            builder.Append('"');
        }

        builder.Append('\'');
        return builder.ToString();
    }

    /// <summary>以分号换行拼接多条语句，并补末尾分号。</summary>
    internal static string JoinStatements(IReadOnlyList<string> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < statements.Count; i++)
        {
            if (i > 0)
                builder.Append(";\n");
            builder.Append(statements[i]);
        }

        builder.Append(';');
        return builder.ToString();
    }

    /// <summary>校验并引用表别名。</summary>
    internal static string QuoteAlias(string alias, string paramName)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Contains('\0'))
            throw new ArgumentException("别名不得为空或含 NUL 字符。", paramName);
        return EscapeIdentifier(alias);
    }
}
