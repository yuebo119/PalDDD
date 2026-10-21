using PalDDD.Dapper;
using PalDDD.Transactions;

namespace PalDDD.Integration.Tests;

/// <summary>
/// T3（基础库优化清单）：方言枚举穷尽性。新增 DapperDbType/RetentionDialect 成员
/// 而未登记映射时，本组测试变红；比注释清单可靠。
/// </summary>
public sealed class DialectEnumExhaustivenessTests
{
    [Test]
    public async Task DapperDbType_AllValues_HaveConfigurationAndRetentionMapping()
    {
        foreach (var dbType in Enum.GetValues<DapperDbType>())
        {
            var connectionString = dbType switch
            {
                DapperDbType.Sqlite => "Data Source=:memory:",
                DapperDbType.PostgreSql => "Host=localhost;Database=probe;Username=probe;Password=probe",
                DapperDbType.MySql => "Server=localhost;Database=probe;User=probe;Password=probe",
                _ => throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "Unhandled DapperDbType."),
            };

            await using var connection = DapperConfiguration.Create(dbType, connectionString);
            _ = new DapperRetentionStore(connection, dbType);
        }
    }

    [Test]
    public async Task RetentionDialect_AllValues_HaveAllSqlShapes()
    {
        foreach (var dialect in Enum.GetValues<RetentionDialect>())
        {
            await Assert.That(RetentionSql.OutboxDelete(dialect, includeDead: false)).IsNotEmpty();
            await Assert.That(RetentionSql.OutboxDelete(dialect, includeDead: true)).IsNotEmpty();
            await Assert.That(RetentionSql.InboxDelete(dialect)).IsNotEmpty();
            await Assert.That(RetentionSql.IdempotencyDelete(dialect)).IsNotEmpty();
            // 时间编码不抛异常即视为该方言已登记（返回对象由后续存储层消费）。
            _ = RetentionSql.ToTimeParam(dialect, DateTimeOffset.UnixEpoch);
        }
    }
}
