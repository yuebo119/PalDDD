using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>
/// Npgsql 数据源的统一注册形态（2026-10-21 评审 F13）。
/// </summary>
/// <remarks>
/// MS.DI 只释放<b>容器自己创建</b>的实例：<c>AddSingleton&lt;T&gt;(instance)</c> 走
/// ImplementationInstance，宿主关闭时 <c>Dispose</c> 调用次数为 0（ITM-637 实测
/// <c>Disposed=False</c>），Npgsql 连接池随进程退出才回收。全部入口改用工厂重载，
/// 且抽象 <c>DbDataSource</c> 委托到同一具体注册，避免同一实例被两个描述符重复释放。
/// </remarks>
internal static class PostgreSqlDataSourceRegistration
{
    internal static void Register(IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        // 工厂重载：容器接管该实例的释放。
        services.AddSingleton<NpgsqlDataSource>(_ => dataSource);
        // 抽象注册指向同一实例（不额外持有所有权）。
        services.AddSingleton<System.Data.Common.DbDataSource>(sp => sp.GetRequiredService<NpgsqlDataSource>());
    }
}
