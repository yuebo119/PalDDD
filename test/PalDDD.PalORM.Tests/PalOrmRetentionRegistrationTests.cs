using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>保留清理注册契约（2026-10-21 审计 P0）：PalORM 入口默认只提供 IRetentionStore，
/// 不启动后台删除；清理必须显式 AddPalRetention。</summary>
public sealed class PalOrmRetentionRegistrationTests
{
    [Test]
    public async Task AddPalOrmSqlite_DoesNotStartRetentionByDefault()
    {
        var services = new ServiceCollection();
        services.AddPalOrmSqlite("Data Source=:memory:");

        var retentionHosted = services.Count(static d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(RetentionProcessorService));

        await Assert.That(retentionHosted).IsEqualTo(0);
    }

    [Test]
    public async Task AddPalRetention_AfterAddPalOrmSqlite_EnablesRetentionExplicitly()
    {
        var services = new ServiceCollection();
        services.AddPalOrmSqlite("Data Source=:memory:");
        services.AddPalRetention();

        var retentionHosted = services.Count(static d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(RetentionProcessorService));

        await Assert.That(retentionHosted).IsEqualTo(1);
    }
}
