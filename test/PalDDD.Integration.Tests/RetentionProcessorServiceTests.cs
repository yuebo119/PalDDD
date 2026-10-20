using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.Dapper;
using PalDDD.Transactions;
using System.Data.Common;

namespace PalDDD.Integration.Tests;

/// <summary>保留清理后台服务的 DI 生命周期（2026-10-20 性能轮）。</summary>
public sealed class RetentionProcessorServiceTests
{
    [Test]
    public async Task AddPalDapperRetention_RegistersHostedServiceAndProcessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbConnection>(new SqliteConnection("Data Source=:memory:"));
        services.AddPalDapperRetention(DapperDbType.Sqlite, static options =>
        {
            options.PollInterval = TimeSpan.FromMinutes(1);
            options.MaxBatchesPerTick = 1;
        });

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().OfType<RetentionProcessorService>().ToList();
        await Assert.That(hosted.Count).IsEqualTo(1);
        await Assert.That(provider.GetService<RetentionProcessor>()).IsTypeOf<RetentionProcessor>();
    }
}
