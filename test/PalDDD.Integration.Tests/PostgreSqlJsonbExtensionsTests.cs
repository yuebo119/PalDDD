namespace PalDDD.Integration.Tests;

using PalDDD.Dapper.PostgreSql;

// ITM-248 传感器（F6）：ExtractJsonByPath 的违禁字符 fail-fast——
// 姊妹 ExtractTextByPath（三十七轮 P2-2）已构建期拒绝逗号/花括号（PG path 数组
// 语法：'{a,b}' 逗号分隔、花括号定界，段内出现会被解释为数组结构、静默查错嵌套位置），
// 本方法 doc 声称同约束但修复前只校验空白。字符串级断言，与 PostgreSqlAuditorTests 同深度。
public sealed class PostgreSqlJsonbExtensionsTests
{
    [Test]
    [Arguments("a,b")]
    [Arguments("a{b")]
    [Arguments("a}b")]
    public async Task ExtractTextByPath_RejectsForbiddenPathSegments(string segment)
    {
        await Assert.That(() => PostgreSqlJsonb.ExtractTextByPath("payload", segment))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("a,b")]
    [Arguments("a{b")]
    [Arguments("a}b")]
    public async Task ExtractJsonByPath_RejectsForbiddenPathSegments(string segment)
    {
        await Assert.That(() => PostgreSqlJsonb.ExtractJsonByPath("payload", segment))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ExtractTextByPath_GeneratesSqlForValidPath()
    {
        var sql = PostgreSqlJsonb.ExtractTextByPath("payload", "Headers", "key");
        await Assert.That(sql).IsEqualTo("\"payload\" #>> '{Headers,key}'");
    }

    [Test]
    public async Task ExtractJsonByPath_GeneratesSqlForValidPath()
    {
        var sql = PostgreSqlJsonb.ExtractJsonByPath("payload", "Headers", "key");
        await Assert.That(sql).IsEqualTo("\"payload\" #> '{Headers,key}'");
    }

    [Test]
    public async Task ExtractJsonByPath_EscapesSingleQuotesInSegment()
    {
        var sql = PostgreSqlJsonb.ExtractJsonByPath("payload", "a'b");
        await Assert.That(sql).IsEqualTo("\"payload\" #> '{a''b}'");
    }
}
