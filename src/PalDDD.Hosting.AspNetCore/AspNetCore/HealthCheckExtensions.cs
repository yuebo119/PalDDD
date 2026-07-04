// ─────────────────────────────────────────────────────────────
// ❤️ HealthCheckExtensions — ASP.NET Core 健康检查
// ─────────────────────────────────────────────────────────────
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Hosting.AspNetCore;

// ─────────────────────────────────────────────────────────────
// 健康检查端点
// ─────────────────────────────────────────────────────────────

/// <summary>PalDDD 健康检查端点 — 基于 ASP.NET Core 内置 <see cref="HealthCheckService"/> 架构</summary>
/// <remarks>
/// 使用方式：<br/>
/// 1. 注册：<c>services.AddPalHealthChecks()</c><br/>
/// 2. 映射：<c>app.MapPalHealthChecks()</c>
/// </remarks>
public static class HealthCheckExtensions
{
    /// <summary>注册 PalDDD 框架的健康检查（MessageBroker/Outbox）</summary>
    public static IServiceCollection AddPalHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHealthChecks()
            .AddCheck<PalMessageBrokerHealthCheck>("message_broker", tags: ["pal", "messaging"])
            .AddCheck<PalOutboxHealthCheck>("outbox", tags: ["pal", "persistence"]);

        return services;
    }

    /// <summary>映射 PalDDD 健康检查端点（使用 ASP.NET Core 内置健康检查中间件）</summary>
    /// <param name="endpoints">端点路由构建器</param>
    /// <param name="pattern">健康检查路径，默认 /health</param>
    /// <param name="timeProvider">可选时间源，用于生成响应时间戳</param>
    public static IEndpointRouteBuilder MapPalHealthChecks(
        this IEndpointRouteBuilder endpoints, string pattern = "/health", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var clock = timeProvider ?? TimeProvider.System;

        endpoints.MapHealthChecks(pattern, new HealthCheckOptions
        {
            ResponseWriter = (context, report) => WriteHealthResponseAsync(context, report, clock)
        });

        return endpoints;
    }

    internal static async Task WriteHealthResponseAsync(HttpContext context, HealthReport report, TimeProvider clock)
    {
        context.Response.ContentType = "application/json";
        var response = new PalHealthResponse(
            report.Status.ToString(),
            clock.GetUtcNow(),
            report.TotalDuration.TotalMilliseconds,
            report.Entries
                .Select(e => new PalHealthComponent(
                    e.Key,
                    e.Value.Status.ToString(),
                    e.Value.Description,
                    e.Value.Duration.TotalMilliseconds,
                    e.Value.Tags.ToArray()))
                .ToArray());

        await context.Response.WriteAsJsonAsync(
            response,
            PalAspNetCoreJsonContext.Default.PalHealthResponse,
            contentType: null,
            cancellationToken: context.RequestAborted);
    }
}

// ═══════════════════════════════════════════════════════════════
// 内置健康检查实现
// ═══════════════════════════════════════════════════════════════

/// <summary>检查消息代理是否已注册 — 不探测外部 Broker 网络连通性。</summary>
internal sealed class PalMessageBrokerHealthCheck(Message.IMessageBroker? broker = null) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = broker is null
            ? HealthCheckResult.Degraded("消息代理未注册")
            : HealthCheckResult.Healthy("消息代理已注册");

        return Task.FromResult(result);
    }
}

/// <summary>检查发件箱存储可用性 — 通过尝试 DB 查询探测</summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "健康检查需将依赖探针失败转换为 unhealthy 结果，需捕获 Exception 基类。")]
internal sealed class PalOutboxHealthCheck : IHealthCheck
{
    private readonly Transactions.IPalOutboxStore? _store;

    public PalOutboxHealthCheck(Transactions.IPalOutboxStore? store = null) => _store = store;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_store is null)
            return HealthCheckResult.Degraded("发件箱存储未注册");

        try
        {
            // 尝试获取待处理消息来验证数据库连接
            await _store.GetPendingMessagesAsync(1, Transactions.IPalOutboxStore.DefaultMaxRetryCount, cancellationToken);
            return HealthCheckResult.Healthy("发件箱存储可用（DB 查询成功）");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("发件箱存储不可用", ex);
        }
    }
}
