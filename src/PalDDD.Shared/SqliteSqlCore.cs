using System.Globalization;

namespace PalDDD.Shared;

/// <summary>
/// SQLite SQL 片段共享核心（internal，源码链接进 Dapper/PalORM 两个方言包）。
/// <para>只依赖 BCL；标识符、字面量、参数名、JSON 路径的转义规则在此单一出处。</para>
/// <para>热路径方法先计算精确长度，再用 <see cref="string.Create{TState}(int, TState, System.Buffers.SpanAction{char, TState})"/>
/// 一次写出，避免中间字符串与 <see cref="System.Text.StringBuilder"/> 缓冲。</para>
/// </summary>
internal static class SqliteSqlCore
{
    /// <summary>转义后标识符长度（含两侧双引号）。</summary>
    internal static int EscapedIdentifierLength(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var length = value.Length + 2;
        foreach (var c in value)
        {
            if (c == '"')
                length++;
        }

        return length;
    }

    /// <summary>把双引号包裹的标识符写入目标 span，返回写入长度。</summary>
    internal static int WriteEscapedIdentifier(Span<char> destination, string value)
    {
        var position = 0;
        destination[position++] = '"';
        foreach (var c in value)
        {
            if (c == '"')
                destination[position++] = '"';
            destination[position++] = c;
        }

        destination[position++] = '"';
        return position;
    }

    /// <summary>双引号包裹标识符，内部双引号翻倍。</summary>
    internal static string EscapeIdentifier(string value)
    {
        var length = EscapedIdentifierLength(value);
        return string.Create(length, value, static (span, state) => WriteEscapedIdentifier(span, state));
    }

    /// <summary>转义后 SQL 字面量长度（含两侧单引号）。</summary>
    internal static int EscapedLiteralLength(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var length = value.Length + 2;
        foreach (var c in value)
        {
            if (c == '\'')
                length++;
        }

        return length;
    }

    /// <summary>把单引号包裹的 SQL 字面量写入目标 span，返回写入长度。</summary>
    internal static int WriteEscapedLiteral(Span<char> destination, string value)
    {
        var position = 0;
        destination[position++] = '\'';
        foreach (var c in value)
        {
            if (c == '\'')
                destination[position++] = '\'';
            destination[position++] = c;
        }

        destination[position++] = '\'';
        return position;
    }

    /// <summary>单引号包裹 SQL 字面量，内部单引号翻倍。</summary>
    internal static string EscapeLiteral(string value)
    {
        var length = EscapedLiteralLength(value);
        return string.Create(length, value, static (span, state) => WriteEscapedLiteral(span, state));
    }

    /// <summary>校验参数名：可带一个前导 <c>@</c>，其余仅 ASCII 字母、数字、下划线，首字符不得为数字。</summary>
    internal static void ValidateParameterName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var start = name[0] == '@' ? 1 : 0;
        if (start == name.Length)
            throw new ArgumentException("参数名不得只有 '@'。", nameof(name));

        for (var i = start; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"参数名含非法字符 '{c}'；仅允许 ASCII 字母、数字、下划线。", nameof(name));
            if (i == start && char.IsAsciiDigit(c))
                throw new ArgumentException("参数名不得以数字开头。", nameof(name));
        }
    }

    /// <summary>规范化参数名后的长度：已带 <c>@</c> 时原样，否则加一个字符。</summary>
    internal static int NormalizedParameterNameLength(string name)
    {
        ValidateParameterName(name);
        return name[0] == '@' ? name.Length : name.Length + 1;
    }

    /// <summary>把规范化参数名写入目标 span，返回写入长度。</summary>
    internal static int WriteNormalizedParameterName(Span<char> destination, string name)
    {
        var position = 0;
        if (name[0] != '@')
            destination[position++] = '@';
        name.AsSpan().CopyTo(destination[position..]);
        return position + name.Length;
    }

    /// <summary>
    /// 规范化 SQL 参数名：接受 <c>q</c> 或 <c>@q</c>，返回 <c>@q</c>。
    /// 已带 <c>@</c> 时返回原实例，零分配。
    /// </summary>
    internal static string NormalizeParameterName(string name)
    {
        ValidateParameterName(name);
        return name[0] == '@' ? name : "@" + name;
    }

    /// <summary>触发器名清洗片段：只保留 ASCII 字母、数字、下划线；空结果回退 fts。</summary>
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

    /// <summary>JSON 路径字面量长度（含外层单引号）。</summary>
    internal static int PathLiteralLength(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var length = 3;
        foreach (var segment in segments)
        {
            length += 3;
            foreach (var c in segment)
            {
                length += c is '\\' or '"' ? 2 : 1;
            }
        }

        return length;
    }

    /// <summary>把 JSON 路径字面量写入目标 span，返回写入长度。</summary>
    internal static int WritePathLiteral(Span<char> destination, IReadOnlyList<string> segments)
    {
        var position = 0;
        destination[position++] = '\'';
        destination[position++] = '$';
        foreach (var segment in segments)
        {
            destination[position++] = '.';
            destination[position++] = '"';
            foreach (var c in segment)
            {
                if (c is '\\' or '"')
                    destination[position++] = '\\';
                destination[position++] = c;
            }

            destination[position++] = '"';
        }

        destination[position++] = '\'';
        return position;
    }

    /// <summary>
    /// 构造 SQLite JSON 路径字面量（含外层单引号）。
    /// 对象键始终用双引号包裹；键内反斜杠与双引号按 SQLite 3.45+ 路径规则转义。
    /// </summary>
    internal static string BuildPathLiteral(IReadOnlyList<string>? segments, string paramName)
    {
        ValidatePathSegments(segments, paramName);
        var length = PathLiteralLength(segments!);
        return string.Create(length, segments!, static (span, state) => WritePathLiteral(span, state));
    }

    /// <summary>以分号换行拼接多条语句，并补末尾分号。</summary>
    internal static string JoinStatements(IReadOnlyList<string> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count == 0)
            return string.Empty;

        var length = 1;
        for (var i = 0; i < statements.Count; i++)
        {
            length += statements[i].Length;
            if (i > 0)
                length += 2;
        }

        return string.Create(length, statements, static (span, state) =>
        {
            var position = 0;
            for (var i = 0; i < state.Count; i++)
            {
                if (i > 0)
                {
                    span[position++] = ';';
                    span[position++] = '\n';
                }

                state[i].AsSpan().CopyTo(span[position..]);
                position += state[i].Length;
            }

            span[position] = ';';
        });
    }

    /// <summary>校验并引用表别名。</summary>
    internal static string QuoteAlias(string alias, string paramName)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Contains('\0'))
            throw new ArgumentException("别名不得为空或含 NUL 字符。", paramName);
        return EscapeIdentifier(alias);
    }
}
