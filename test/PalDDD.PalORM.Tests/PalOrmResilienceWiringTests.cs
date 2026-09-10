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
/// <para>v53 P3 代表性声明：仅覆盖 Sqlite——PG/MySql 的 configureResilience 工厂实现与
/// Sqlite 逐字同构（仅类型名差异），且二者 CreateAsync 需真实服务端（Testcontainers），
/// 回调语义由 Sqlite 代表性覆盖 + 编译期签名一致保障。</para>
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

    [Test]
    public async Task ConfigureResilience_CallbackThrows_PropagatesException()
    {
        // v53 P2 诚实改名（ITM-645）：原名 ..._CallbackDisposesOpenedSession 与断言不符——
        // 本测试只锁定"回调异常正确传播"，无法直接观测已打开会话是否被释放（SQLite :memory:
        // 会话泄漏行为级不可断言；不释放则物理连接滞留至 GC）。工厂的 catch-dispose 释放路径
        // 由代码审查保障，测试端只覆盖异常传播这一半。
        var thrown = await Assert.That(() =>
        {
            using var sp = BuildProvider(_ => throw new InvalidOperationException("callback-boom"));
            _ = sp.GetRequiredService<DataSession<SqliteProvider>>();
        }).Throws<InvalidOperationException>();

        var message = thrown?.Message ?? "";
        await Assert.That(message).Contains("callback-boom");
    }
}
