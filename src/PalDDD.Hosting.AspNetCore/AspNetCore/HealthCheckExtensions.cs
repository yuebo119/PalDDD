// ─────────────────────────────────────────────────────────────
// ❤️ HealthCheckExtensions — ASP.NET Core 健康检查
// ─────────────────────────────────────────────────────────────
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalDDD.Core.Logging;
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
/// <para><b>滥用控制声明（v53 P3，对齐 EndpointExtensions 同款）</b>：本扩展不内置授权/
/// 限流/TLS——/health 输出组件健康状态与依赖拓扑，生产部署请以授权策略（RequireAuthorization）
/// 或内网专用（允许探测端口独立暴露）限定可达面；传输安全由宿主层 HSTS/反代承担。</para>
/// </remarks>
public static class HealthCheckExtensions
{
    /// <summary>注册 PalDDD 框架的健康检查（MessageBroker/Outbox）</summary>
    public static IServiceCollection AddPalHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // R11 F2：Hosting 入口自带日志门面——usage.md 的独立片段只调本方法与
        // UsePalExceptionHandler，而 ExceptionMiddleware 强制依赖 IPalLogger<T>。
        services.EnsurePalLoggerFacade();
        // R11 F3：重名注册在 .NET 11 的 HealthCheckService 是 ArgumentException（不是
        // 文档暗示的「两个同名检查」）。PostConfigure 在全部 AddCheck 之后按名称去重，
        // 保留最后一次注册，使重复 AddPalHealthChecks / 与可选 OutboxHealth 并用幂等。
        services.EnsureUniqueHealthCheckNames();

        services.AddHealthChecks()
            .AddCheck<PalMessageBrokerHealthCheck>("message_broker", tags: ["pal", "messaging"])
            .AddCheck<PalOutboxHealthCheck>("outbox", tags: ["pal", "persistence"]);

        return services;
    }

    /// <summary>映射 PalDDD 健康检查端点（使用 ASP.NET Core 内置健康检查中间件）</summary>
    /// <param name="endpoints">端点路由构建器</param>
    /// <param name="pattern">健康检查路径，默认 /health</param>
    /// <param name="timeProvider">可选时间源，用于生成响应时间戳</param>
    /// <remarks>需要链 <c>RequireAuthorization()</c>/<c>RequireRateLimiting()</c> 时改用
    /// <see cref="MapPalHealthChecksWithConventions"/>（R13 F10：本方法返回
    /// <see cref="IEndpointRouteBuilder"/>，无法链端点约定）。</remarks>
    public static IEndpointRouteBuilder MapPalHealthChecks(
        this IEndpointRouteBuilder endpoints, string pattern = "/health", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        MapPalHealthChecksWithConventions(endpoints, pattern, timeProvider);
        return endpoints;
    }

    /// <summary>
    /// 映射 PalDDD 健康检查端点并返回端点约定构建器（R13 F10）。
    /// </summary>
    /// <remarks>
    /// /health 输出组件健康状态与依赖拓扑，生产部署通常需要授权或限流：
    /// <c>app.MapPalHealthChecksWithConventions().RequireAuthorization();</c><br/>
    /// 单独新增方法而非给 <see cref="MapPalHealthChecks"/> 加可选参数：后者改变 CLR 签名
    /// （既有二进制 MissingMethodException），且与 3 参重载产生调用二义性。
    /// </remarks>
    /// <param name="endpoints">端点路由构建器</param>
    /// <param name="pattern">健康检查路径，默认 /health</param>
    /// <param name="timeProvider">可选时间源，用于生成响应时间戳</param>
    public static IEndpointConventionBuilder MapPalHealthChecksWithConventions(
        this IEndpointRouteBuilder endpoints, string pattern = "/health", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var clock = timeProvider ?? TimeProvider.System;

        return endpoints.MapHealthChecks(pattern, new HealthCheckOptions
        {
            ResponseWriter = (context, report) => WriteHealthResponseAsync(context, report, clock)
        });
    }

    /// <summary>
    /// R11 F3：按名称去重健康检查注册（保留最后一次）。.NET 11 的
    /// <see cref="HealthCheckService"/> 对重名注册直接抛 <see cref="ArgumentException"/>，
    /// 而重复调用公开 Add* 是常见装配形态（多模块各注册一次）。
    /// </summary>
    internal static IServiceCollection EnsureUniqueHealthCheckNames(this IServiceCollection services)
    {
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(PalHealthCheckDeduplicationMarker)))
            return services;

        services.AddSingleton<PalHealthCheckDeduplicationMarker>();
        services.AddOptions<HealthCheckServiceOptions>().PostConfigure(options =>
        {
            // R13 F7：保留**最后一次**注册（后注册者覆盖先注册者，与注释/DI 惯例一致）。
            // 原实现保留首个：两种注册顺序各错一半——用户先注册 "outbox" 时框架检查被
            // 顶替；框架先注册时用户的后注册被丢弃。幸存者 Tags 决定 tag 过滤端点是否包含该项。
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
        return services;
    }

    internal static async Task WriteHealthResponseAsync(HttpContext context, HealthReport report, TimeProvider clock)
    {
        // ITM-800（2026-09-19）：删除手动 ContentType 赋值——WriteAsJsonAsync(contentType:null)
        // 用框架默认 "application/json; charset=utf-8" 覆盖此设置（死代码）；行为由
        // WriteHealthResponse_FinalContentTypeIsFrameworkDefault_NotManualSet 测试锁定
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
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }
}

// ═══════════════════════════════════════════════════════════════
// 内置健康检查实现
// ═══════════════════════════════════════════════════════════════

/// <summary>R11 F3：健康检查去重 PostConfigure 的幂等标记（单一注册）。</summary>
#pragma warning disable S2094 // 标记类型：只作 PostConfigure 幂等键，永不实例化（DI 类型身份即用途）
internal sealed class PalHealthCheckDeduplicationMarker;
#pragma warning restore S2094

/// <summary>检查消息代理是否已注册 — 不探测外部 Broker 网络连通性。</summary>
internal sealed class PalMessageBrokerHealthCheck(Message.IMessageBroker? broker = null) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // NullMessageBroker 是 AddPalDDD 的默认注册（单节点形态）：健康检查的既有契约是
        // 「注册即健康」，报 Degraded 会让默认应用 readiness 变红（滚动更新阻塞）——
        // 语义变更风险高于收益，维持原判定；真实 broker 是否配置由部署侧自行保证。
        // R14：实现 IMessageBrokerHealth 的 broker（Kafka）自述消费链路状态——消费线程
        // 不可恢复终止时转 Unhealthy；未实现者（含 NullMessageBroker）保持原语义。
        var result = broker switch
        {
            null => HealthCheckResult.Degraded("消息代理未注册"),
            Message.IMessageBrokerHealth { IsConsumingHealthy: false } unhealthy =>
                HealthCheckResult.Unhealthy(
                    $"消息代理消费链路已停止：{unhealthy.LastFaultReason ?? "未知原因"}"),
            _ => HealthCheckResult.Healthy("消息代理已注册"),
        };

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
            await _store.GetPendingMessagesAsync(1, Transactions.IPalOutboxStore.DefaultMaxRetryCount, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("发件箱存储可用（DB 查询成功）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("发件箱存储不可用", ex);
        }
    }
}
