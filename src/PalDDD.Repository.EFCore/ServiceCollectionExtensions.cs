using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PalDDD.Core.Repository;

namespace PalDDD.Repository.EFCore;

// ─────────────────────────────────────────────────────────────
// 仓储服务注册
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core 工作单元与领域事件拦截器的注册入口。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 EF Core 工作单元与 Outbox 领域事件拦截器。</summary>
    /// <remarks>
    /// <c>DispatchingDomainEventInterceptor</c> 已在 v0.2.0 移除——其 AT-MOST-ONCE 语义导致
    /// broker 不可达时事件永久丢失。此方法只注册 Outbox 路径，是唯一推荐的事件持久化方式。
    /// </remarks>
    public static IServiceCollection AddPalOutboxUnitOfWork<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IUnitOfWork, UnitOfWork<TContext>>();
        services.TryAddScoped<OutboxDomainEventInterceptor>();
        return services;
    }
}
