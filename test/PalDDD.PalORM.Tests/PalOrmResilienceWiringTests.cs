using Microsoft.Extensions.DependencyInjection;
using PalORM;
using PalORM.Sqlite;
using PalDDD.PalORM.Sqlite;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// PalORM 5.4 弹性层 DI 接线测试 —— configureResilience 回调三态验证。
/// <para>
/// 覆盖面依据 PalORM 5.4 XML doc：WithRetry/WithCircuitBreaker 作用于连接建立 +
/// 只读查询内置管线，写入路径与事务内查询维持直连（非幂等写不重试）。
/// 本测试验证接线与行为正常性（瞬时故障注入需 Provider 判定配合，属 PalORM 库内测试职责）。
/// </para>
/// </summary>
public class PalOrmResilienceWiringTests
{
    private static ServiceProvider BuildProvider(Action<DataSession<SqliteProvider>>? configureResilience)
    {
        var services = new ServiceCollection();
        services.AddPalOrmSqlite("Data Source=:memory:", configureResilience: configureResilience);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task ConfigureResilience_Null_DefaultsPassThrough_SessionUsable()
    {
        await using var sp = BuildProvider(null);
        var session = sp.GetRequiredService<DataSession<SqliteProvider>>();

        var one = await session.ScalarAsync<long>($"SELECT 1", default);
        await Assert.That(one).IsEqualTo(1L);
    }

    [Test]
    public async Task ConfigureResilience_WithRetryAndCircuitBreaker_ReadsStillWork()
    {
        var applied = false;
        await using var sp = BuildProvider(s =>
        {
            s.WithRetry(3, attempt => TimeSpan.FromMilliseconds(10 * attempt));
            s.WithCircuitBreaker(5, TimeSpan.FromSeconds(30));
            applied = true;
        });
        var session = sp.GetRequiredService<DataSession<SqliteProvider>>();

        await Assert.That(applied).IsTrue();

        var one = await session.ScalarAsync<long>($"SELECT 1", default);
        await Assert.That(one).IsEqualTo(1L);
    }

    [Test]
    public async Task ConfigureResilience_WritesRemainDirect_SessionUsable()
    {
        await using var sp = BuildProvider(s =>
        {
            s.WithRetry(3, attempt => TimeSpan.FromMilliseconds(10 * attempt));
            s.WithCircuitBreaker(5, TimeSpan.FromSeconds(30));
        });
        var session = sp.GetRequiredService<DataSession<SqliteProvider>>();

        // 写路径直连语义：配置弹性后 ExecuteAsync 建表写入照常
        var affected = await session.ExecuteAsync(
            $"CREATE TABLE resilience_probe (id INTEGER PRIMARY KEY, note TEXT)", default);
        await Assert.That(affected).IsGreaterThanOrEqualTo(0);

        var inserted = await session.ExecuteAsync(
            $"INSERT INTO resilience_probe (note) VALUES ('direct-write')", default);
        await Assert.That(inserted).IsEqualTo(1);

        var count = await session.ScalarAsync<long>($"SELECT COUNT(*) FROM resilience_probe", default);
        await Assert.That(count).IsEqualTo(1L);
    }
}
