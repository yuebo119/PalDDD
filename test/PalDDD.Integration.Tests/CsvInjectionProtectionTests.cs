namespace PalDDD.Integration.Tests;

using PalDDD.Dapper.PostgreSql;

public sealed class CsvInjectionProtectionTests
{
    // ITM-201 回归（三十一轮）：CSV 公式注入防护（OWASP CSV Injection）——
    // 以 = + - @ Tab 开头的单元格在 Excel 打开时会被当作公式执行；
    // 修复后前置单引号强制文本解释。

    [Test]
    public async Task EscapeCsv_FormulaPrefix_IsNeutralized()
    {
        // 直接测 internal 入口（InternalsVisibleTo: PalDDD.Integration.Tests）
        foreach (var prefix in new[] { "=", "+", "-", "@", "\t" })
        {
            var raw = $"{prefix}cmd|' /C calc'!A0";
            var escaped = PostgreSqlReportHelper.EscapeCsvSpan(raw.AsSpan());

            await Assert.That(escaped[0]).IsEqualTo('\'');
            await Assert.That(escaped[1]).IsEqualTo(prefix[0]);
        }
    }

    [Test]
    public async Task EscapeCsv_NonFormula_Unchanged()
    {
        // 非公式前缀的普通文本不受影响
        await Assert.That(PostgreSqlReportHelper.EscapeCsvSpan("hello world".AsSpan())).IsEqualTo("hello world");
        await Assert.That(PostgreSqlReportHelper.EscapeCsvSpan("123".AsSpan())).IsEqualTo("123");
        // 含逗号/引号仍按原有规则引号包裹（回归保护）
        await Assert.That(PostgreSqlReportHelper.EscapeCsvSpan("a,b".AsSpan())).IsEqualTo("\"a,b\"");
    }

    [Test]
    public async Task EscapeCsv_FormulaWithComma_BothNeutralizedAndQuoted()
    {
        // ITM-215 修复（三十二轮）：公式前缀 + 逗号共存——单引号必须在 CSV 双引号内部
        // 原实现 '"..."' 中单引号在双引号外，CSV 解析器按逗号拆成两列（第二列仍以 = 开头）
        var escaped = PostgreSqlReportHelper.EscapeCsvSpan("=1+1,x".AsSpan());
        // 修复后："'=1+1,x"——双引号开头，单引号在双引号内作为字段值前缀
        await Assert.That(escaped[0]).IsEqualTo('"');
        await Assert.That(escaped).IsEqualTo("\"'=1+1,x\"");
        // 标准 CSV 解析验证：按逗号拆分后必须是单列（值以 ' 开头，公式被中和）
        var inner = escaped.Trim('"');
        await Assert.That(inner[0]).IsEqualTo('\'');
        await Assert.That(inner).Contains(",");
    }
}
