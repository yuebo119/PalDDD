using PalDDD.Shared;

namespace PalDDD.Dapper.Sqlite;

/// <summary>
/// SQLite JSON1 函数与路径片段生成。
/// <para>对象键始终使用引号路径（<c>$."a.b"</c>），支持点号、方括号、空格；
/// 值优先使用 <c>*Parameter</c> 方法，<c>*Literal</c> 方法只服务常量。</para>
/// </summary>
public static class SqliteJson
{
    /// <summary>
    /// 生成 SQLite JSON 路径字面量，例如 <c>'$."a.b"."c[0]"'</c>。
    /// 路径段不得为空；键内反斜杠与双引号按 SQLite 路径规则转义。
    /// </summary>
    public static string Path(params string[] segments)
        => SqliteJsonCore.PathLiteral(segments, nameof(segments));

    /// <summary>生成 <c>json_extract(列, 路径)</c>。空路径表示整个文档。</summary>
    public static string Extract(string column, params string[] path)
        => SqliteJsonCore.Extract(column, path);

    /// <summary>生成 <c>列 ->> 路径</c>，返回 SQL 文本值。</summary>
    public static string ExtractText(string column, params string[] path)
        => SqliteJsonCore.ExtractText(column, path);

    /// <summary>生成 <c>列 -> 路径</c>，返回 JSON 文本。</summary>
    public static string ExtractJson(string column, params string[] path)
        => SqliteJsonCore.ExtractJson(column, path);

    /// <summary>生成 <c>json_type</c>。空路径返回整个文档的类型。</summary>
    public static string Type(string column, params string[] path)
        => SqliteJsonCore.Type(column, path);

    /// <summary>生成 <c>json_valid(列)</c>。</summary>
    public static string IsValid(string column)
        => SqliteJsonCore.IsValid(column);

    /// <summary>生成键存在判断：<c>json_type(列, 路径) IS NOT NULL</c>。</summary>
    public static string HasKey(string column, params string[] path)
        => SqliteJsonCore.HasKey(column, path);

    /// <summary>生成 <c>json_array_length</c>。空路径返回整个数组的长度。</summary>
    public static string ArrayLength(string column, params string[] path)
        => SqliteJsonCore.ArrayLength(column, path);

    /// <summary>生成字符串字面量数组：<c>json_array('a','b')</c>。空参数生成 <c>json_array()</c>。</summary>
    public static string ArrayLiteral(params string[] values)
        => SqliteJsonCore.ArrayLiteral(values);

    /// <summary>生成参数数组：<c>json_array(@a,@b)</c>。参数名可写 <c>a</c> 或 <c>@a</c>。</summary>
    public static string ArrayParameters(params string[] parameterNames)
        => SqliteJsonCore.ArrayParameters(parameterNames);

    /// <summary>生成字符串字面量对象：<c>json_object('k','v')</c>。</summary>
    public static string ObjectLiteral(params (string Key, string Value)[] pairs)
        => SqliteJsonCore.ObjectLiteral(pairs);

    /// <summary>生成参数值对象：<c>json_object('k',@v)</c>。</summary>
    public static string ObjectParameters(params (string Key, string ParameterName)[] pairs)
        => SqliteJsonCore.ObjectParameters(pairs);

    /// <summary>生成 <c>json_set(列, 路径, @参数)</c>。</summary>
    public static string SetParameter(string column, string parameterName, params string[] path)
        => SqliteJsonCore.SetParameter(column, parameterName, path);

    /// <summary>生成 <c>json_insert(列, 路径, @参数)</c>。</summary>
    public static string InsertParameter(string column, string parameterName, params string[] path)
        => SqliteJsonCore.InsertParameter(column, parameterName, path);

    /// <summary>生成 <c>json_replace(列, 路径, @参数)</c>。</summary>
    public static string ReplaceParameter(string column, string parameterName, params string[] path)
        => SqliteJsonCore.ReplaceParameter(column, parameterName, path);

    /// <summary>生成 <c>json_patch(列, @参数)</c>。</summary>
    public static string PatchParameter(string column, string parameterName)
        => SqliteJsonCore.PatchParameter(column, parameterName);

    /// <summary>生成 <c>json_remove(列, 路径)</c>。</summary>
    public static string Remove(string column, params string[] path)
        => SqliteJsonCore.Remove(column, path);

    /// <summary>生成 <c>json_quote('值')</c>，把普通字符串编码成 JSON 字符串字面量。</summary>
    public static string QuoteLiteral(string value)
        => SqliteJsonCore.QuoteLiteral(value);

    /// <summary>生成 <c>json_group_array("列")</c> 聚合表达式。</summary>
    public static string GroupArray(string column)
        => SqliteJsonCore.GroupArray(column);

    /// <summary>生成 <c>json_group_object("键列","值列")</c> 聚合表达式。</summary>
    public static string GroupObject(string keyColumn, string valueColumn)
        => SqliteJsonCore.GroupObject(keyColumn, valueColumn);

    /// <summary>生成 <c>json_each</c> 表值函数调用。空路径使用单参数形态。</summary>
    public static string Each(string column, params string[] path)
        => SqliteJsonCore.Each(column, path);

    /// <summary>生成 <c>json_tree</c> 表值函数调用。空路径使用单参数形态。</summary>
    public static string Tree(string column, params string[] path)
        => SqliteJsonCore.Tree(column, path);

    /// <summary>生成字面量相等谓词：<c>json_extract(列, 路径) = '值'</c>。</summary>
    public static string EqualsLiteral(string column, string value, params string[] path)
        => SqliteJsonCore.EqualsLiteral(column, value, path);

    /// <summary>生成参数相等谓词：<c>json_extract(列, 路径) = @参数</c>。</summary>
    public static string EqualsParameter(string column, string parameterName, params string[] path)
        => SqliteJsonCore.EqualsParameter(column, parameterName, path);

    /// <summary>Outbox 按消息类型过滤的便捷谓词：<c>json_extract("payload", '$."Type"') = '类型'</c>。值内联，仅服务常量。</summary>
    public static string OutboxByType(string messageType)
        => SqliteJsonCore.OutboxByType(messageType);
}
