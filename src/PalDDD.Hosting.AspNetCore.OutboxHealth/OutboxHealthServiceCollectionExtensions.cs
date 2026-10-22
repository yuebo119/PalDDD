using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalDDD.Core.Logging;
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
        // R11 F2/F3：可选包也是 Hosting 注册入口——自带日志门面与健康检查去重，
        // 与 AddPalHealthChecks 并用时不再因重名 outbox 让 /health 抛异常。
        // 去重逻辑与 Hosting 的 EnsureUniqueHealthCheckNames 同构（本包刻意不引用
        // Hosting，避免为 12 行逻辑传递整个适配层）。
        services.EnsurePalLoggerFacade();
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(PalOutboxHealthDeduplicationMarker)))
        {
            services.AddSingleton<PalOutboxHealthDeduplicationMarker>();
            services.AddOptions<HealthCheckServiceOptions>().PostConfigure(options =>
            {
                // R13 F7：与 Hosting 的 EnsureUniqueHealthCheckNames 同步改为保留最后一次
                // （原实现保留首个，与注释相反；两种注册顺序各错一半）。
                var snapshot = options.Registrations.ToList();
                var lastIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < snapshot.Count; i++)
                    lastIndexByName[snapshot[i].Name] = i;

                List<HealthCheckRegistration>? duplicates = null;
                for (var i = 0; i < snapshot.Count; i++)
                {
                    if (lastIndexByName[snapshot[i].Name] != i)
                        (duplicates ??= []).Add(snapshot[i]);
                }

                if (duplicates is null)
                    return;
                foreach (var duplicate in duplicates)
                    options.Registrations.Remove(duplicate);
            });
        }
        services.AddHealthChecks()
            .AddCheck<PalOutboxHealthCheck>("outbox", tags: ["pal", "persistence"]);
        return services;
    }
}

/// <summary>R11 F3：本包健康检查去重 PostConfigure 的幂等标记。</summary>
internal sealed class PalOutboxHealthDeduplicationMarker;
