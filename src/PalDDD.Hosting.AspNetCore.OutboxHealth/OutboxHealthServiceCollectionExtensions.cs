using Microsoft.Extensions.DependencyInjection;
using PalDDD.Transactions;

namespace PalDDD.Hosting.AspNetCore.OutboxHealth;

/// <summary>可选 Outbox 健康检查的注册入口。</summary>
public static class OutboxHealthServiceCollectionExtensions
{
    /// <summary>
    /// 注册名为 <c>outbox</c> 的健康检查（tags: pal, persistence）。
    /// </summary>
    /// <remarks>
    /// 与 <c>PalDDD.Hosting.AspNetCore</c> 的 <c>AddPalHealthChecks()</c> 二选一：
    /// 后者同时注册 message_broker 与 outbox，并因此引用 Transactions；本入口只注册 outbox。
    /// </remarks>
    public static IServiceCollection AddPalOutboxHealthCheck(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHealthChecks()
            .AddCheck<PalOutboxHealthCheck>("outbox", tags: ["pal", "persistence"]);
        return services;
    }
}
