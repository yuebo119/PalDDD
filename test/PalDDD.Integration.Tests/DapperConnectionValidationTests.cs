using Microsoft.Data.Sqlite;
using PalDDD.Dapper;

namespace PalDDD.Integration.Tests;

/// <summary>
/// 契约盘点 2026-10-21：Dapper store 的配置错误必须在构造期给出可行动消息，
/// 而不是等到首次查询抛 provider 语法错误 / 缺表错误。
/// </summary>
public sealed class DapperConnectionValidationTests
{
    [Test]
    public async Task Store_WithoutConnectionString_ThrowsActionableMessage()
    {
        using var connection = new SqliteConnection("");

        ArgumentException? captured = null;
        try
        {
            _ = new DapperOutboxStore(connection, DapperDbType.Sqlite);
        }
        catch (ArgumentException ex)
        {
            captured = ex;
        }

        await Assert.That(captured).IsTypeOf<ArgumentException>();
        await Assert.That(captured!.Message.Contains("未配置连接串", StringComparison.Ordinal)).IsTrue();
        await Assert.That(captured.Message.Contains("Data Source", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Store_DialectMismatch_ThrowsActionableMessage()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");

        ArgumentException? captured = null;
        try
        {
            _ = new DapperOutboxStore(connection, DapperDbType.PostgreSql);
        }
        catch (ArgumentException ex)
        {
            captured = ex;
        }

        await Assert.That(captured).IsTypeOf<ArgumentException>();
        await Assert.That(captured!.Message.Contains("不匹配", StringComparison.Ordinal)).IsTrue();
        await Assert.That(captured.Message.Contains("PostgreSql", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Store_MatchingDialect_ConstructsSuccessfully()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");

        var store = new DapperOutboxStore(connection, DapperDbType.Sqlite);

        await Assert.That(store).IsTypeOf<DapperOutboxStore>();
    }
}
