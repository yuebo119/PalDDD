using Microsoft.Extensions.DependencyInjection;
using PalORM;
using PalORM.Sqlite;
using PalDDD.PalORM.Sqlite;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// PalORM 6.6.0 <see cref="IDataSessionFactory{TProvider}"/> 会话池 DI 接线测试。
/// <para>
/// 覆盖：显式启用后工厂注册、Scoped 会话跨作用域复用、作用域释放归还（IdleCount）、
/// 弹性回调按租期重放、默认路径不注册工厂（防默认行为漂移）。
/// </para>
/// <para>仅覆盖 Sqlite：PG/MySql 的工厂实现与 Sqlite 逐字同构（仅类型名差异），
/// 且二者 CreateAsync 需真实服务端（Testcontainers），池化语义由 Sqlite 代表性覆盖
/// + 编译期签名一致保障。</para>
/// </summary>
public class PalOrmSessionPoolWiringTests
{
    [Test]
    public async Task UseSessionPool_RentsAndReturnsSession_ReusesAcrossScopes()
    {
        var applyCount = 0;
        var services = new ServiceCollection();
        services.AddPalOrmSqlite(
            "Data Source=:memory:",
            configureResilience: _ => applyCount++,
            useSessionPool: true);
        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IDataSessionFactory<SqliteProvider>>();
        await Assert.That(factory.IdleCount).IsEqualTo(0);

        DataSession<SqliteProvider> first;
        await using (var scope = sp.CreateAsyncScope())
        {
            first = scope.ServiceProvider.GetRequiredService<DataSession<SqliteProvider>>();
            await first.ExecuteAsync(
                $"CREATE TABLE pool_probe (id INTEGER PRIMARY KEY, note TEXT)", default);
        }

        // 作用域释放即归还；:memory: 库随池化连接存活，下一租期读到同库
        await Assert.That(factory.IdleCount).IsEqualTo(1);
        await Assert.That(applyCount).IsEqualTo(1);

        await using (var scope = sp.CreateAsyncScope())
        {
            var second = scope.ServiceProvider.GetRequiredService<DataSession<SqliteProvider>>();
            await Assert.That(ReferenceEquals(first, second)).IsTrue();
            var count = await second.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pool_probe'",
                ct: default);
            await Assert.That(count).IsEqualTo(1L);
        }

        await Assert.That(factory.IdleCount).IsEqualTo(1);
        await Assert.That(applyCount).IsEqualTo(2);
    }

    [Test]
    public async Task Default_DoesNotRegisterSessionFactory()
    {
        var services = new ServiceCollection();
        services.AddPalOrmSqlite("Data Source=:memory:");
        await using var sp = services.BuildServiceProvider();

        // 默认 false：不注册工厂，保持每作用域 CreateAsync 的既有生命周期
        await Assert.That(sp.GetService<IDataSessionFactory<SqliteProvider>>()).IsNull();

        await using var scope = sp.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DataSession<SqliteProvider>>();
        await Assert.That(await session.ScalarAsync<long>($"SELECT 1", ct: default)).IsEqualTo(1L);
    }
}
