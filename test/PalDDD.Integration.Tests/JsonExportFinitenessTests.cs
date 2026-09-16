namespace PalDDD.Integration.Tests;

using System.Text;
using System.Text.Json;
using PalDDD.Dapper.PostgreSql;

public sealed class JsonExportFinitenessTests
{
    // 全仓扫描修复回归：PG 的 float4/float8 合法包含 NaN / ±Infinity，而
    // Utf8JsonWriter.WriteNumberValue 对非有限值抛 ArgumentException —— 修复前导出含此类值的
    // 行会中途崩溃（整份导出失败）。修复后按命名字符串写出（拼写与 PG 自身字面量一致）。
    [Test]
    public async Task WriteJsonValue_NonFiniteFloats_WrittenAsNamedStrings()
    {
        // 直接测 internal 入口（InternalsVisibleTo: PalDDD.Integration.Tests，沿 EscapeCsvSpan 先例）
        await Assert.That(Render(double.NaN)).IsEqualTo("\"NaN\"");
        await Assert.That(Render(double.PositiveInfinity)).IsEqualTo("\"Infinity\"");
        await Assert.That(Render(double.NegativeInfinity)).IsEqualTo("\"-Infinity\"");
        await Assert.That(Render(float.NaN)).IsEqualTo("\"NaN\"");
        await Assert.That(Render(float.PositiveInfinity)).IsEqualTo("\"Infinity\"");
        await Assert.That(Render(float.NegativeInfinity)).IsEqualTo("\"-Infinity\"");
    }

    // 负向对照：有限值仍必须是 JSON 数字（防修复把数值分支一并改成字符串）
    [Test]
    public async Task WriteJsonValue_FiniteNumbers_RemainJsonNumbers()
    {
        await Assert.That(Render(1.5d)).IsEqualTo("1.5");
        await Assert.That(Render(2.25f)).IsEqualTo("2.25");
        await Assert.That(Render(42L)).IsEqualTo("42");
    }

    private static string Render(object value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            PostgreSqlReportHelper.WriteJsonValue(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
