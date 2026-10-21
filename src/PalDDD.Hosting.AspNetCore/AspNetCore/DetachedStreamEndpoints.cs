// ═══════════════════════════════════════════════════════════════
// 🧵 断线续跑端点（ADR-031）— 启动即附着 / 重连附着 / 显式取消
// ═══════════════════════════════════════════════════════════════
// 三个端点：
//   MapDetachedStreamQuery  GET/POST {pattern}                 启动 run 并附着（响应头 X-Run-Id）
//   MapStreamRunAttach      GET       {pattern}/{runId}/stream  重连附着（遵守 Last-Event-ID）
//   MapStreamRunCancel      POST      {pattern}/{runId}/cancel  幂等取消
//
// 与 MapStreamQuery（ADR-030）的分工：后者连接断即取消；本组端点 detach 不取消 run，
// 断线后可在 DetachGrace 内重连续看。不需要续跑的场景用 MapStreamQuery（更简单、零注册表）。
//
// 验证前置与 SSE 头写法对齐 MapStreamQuery（PalValidatorRunner 单一出口、头写出前 400 可回退）。
// 启动形态双份（2026-10-19 补强）：GET 供 EventSource 类客户端，POST 供 prompt/参数体
// 超过 URL 长度的 agent 请求；两者共用 StartAndAttachAsync 单一编排。

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PalDDD.CQRS;
using PalDDD.Core.Logging;
using System.Text.Json.Serialization.Metadata;

namespace PalDDD.Hosting.AspNetCore;

/// <summary>断线续跑端点映射与 DI 扩展（ADR-031）。</summary>
public static class DetachedStreamEndpoints
{
    /// <summary>注册断线续跑基础设施（选项 + 运行注册表）。幂等：重复调用的配置以首次为准。</summary>
    public static IServiceCollection AddDetachedStreamRuns(
        this IServiceCollection services,
        Action<DetachedStreamOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // R11 F2：Hosting 注册入口自带日志门面（与 AddPalHealthChecks 同族）。
        services.EnsurePalLoggerFacade();

        var options = new DetachedStreamOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<StreamRunRegistry>();
        return services;
    }

    /// <summary>
    /// 启动并附着一个断线可续的流式查询（GET）。响应头 <c>X-Run-Id</c> 供后续重连使用。
    /// </summary>
    /// <remarks>
    /// 验证失败 400（SSE 头写出前，状态码可回退）；并发 run 超上限 503。
    /// handler 从 run 自有 scope 解析（run 活过请求作用域）。
    /// </remarks>
    public static IEndpointConventionBuilder MapDetachedStreamQuery<TQuery, TItem>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TQuery> bindQuery,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        Func<TItem, string>? eventSelector = null,
        Func<TItem, string>? idSelector = null,
        TimeSpan? keepAliveInterval = null)
        where TQuery : IStreamQuery<TItem>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(bindQuery);
        ArgumentNullException.ThrowIfNull(itemJsonTypeInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveInterval ?? TimeSpan.MaxValue, TimeSpan.Zero);

        return endpoints.MapGet(pattern, async context =>
        {
            TQuery query;
            try
            {
                query = bindQuery(context);
            }
            catch (PalValidationException ex)
            {
                await WriteValidationProblemAsync(context, ex).ConfigureAwait(false);
                return;
            }
            await StartAndAttachAsync(context, query, itemJsonTypeInfo, eventSelector, idSelector, keepAliveInterval)
                .ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 启动并附着一个断线可续的流式查询（POST，JSON body）。响应头 <c>X-Run-Id</c> 供后续重连使用。
    /// </summary>
    /// <remarks>
    /// 形态对齐 <c>MapStreamQuery</c> POST 重载：prompt 类请求体超过 URL 长度时用它，反序列化段
    /// 复用 <c>ReadJsonBodyOrWriteErrorAsync</c>（415/畸形 JSON/空 body 错误形态不变）。
    /// 浏览器原生 EventSource 不发 POST，客户端须用 fetch + ReadableStream 手工解析。
    /// 验证失败 400（SSE 头写出前）；并发 run 超上限 503。
    /// </remarks>
    public static IEndpointConventionBuilder MapDetachedStreamQuery<TQuery, TItem>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        JsonTypeInfo<TQuery> queryJsonTypeInfo,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        Func<TItem, string>? eventSelector = null,
        Func<TItem, string>? idSelector = null,
        TimeSpan? keepAliveInterval = null)
        where TQuery : IStreamQuery<TItem>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(queryJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(itemJsonTypeInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveInterval ?? TimeSpan.MaxValue, TimeSpan.Zero);

        return endpoints.MapPost(pattern, async context =>
        {
            var (ok, query) = await EndpointExtensions
                .ReadJsonBodyOrWriteErrorAsync(context, queryJsonTypeInfo)
                .ConfigureAwait(false);
            if (!ok)
                return;
            await StartAndAttachAsync(context, query, itemJsonTypeInfo, eventSelector, idSelector, keepAliveInterval)
                .ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 启动与附着的单一编排（GET/POST 共用）：空值检查 → 验证前置 → 注册 run → 首次附着 →
    /// SSE 头 → 推流。错误路径均在 SSE 头写出前返回，状态码可回退。
    /// </summary>
    private static async Task StartAndAttachAsync<TQuery, TItem>(
        HttpContext context,
        TQuery? query,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector,
        TimeSpan? keepAliveInterval)
        where TQuery : IStreamQuery<TItem>
    {
        if (query is null)
        {
            // F24（2026-10-21）：与 MapQuery/MapStreamQuery 的 400 契约收敛——
            // 原走 WriteProblemAsync（about:blank / Invalid Request / property=run），
            // 客户端按 type/property 分支时出现两套形态。
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                ValidationProblemResponseFactory.CreateInvalidBody(
                    "Query binding returned null. Ensure all required query parameters are provided."),
                PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 验证前置（与 MapStreamQuery 同一核心：PalValidatorRunner）
        var validators = context.RequestServices.GetServices<Core.IPalValidator<TQuery>>();
        var errors = PalValidatorRunner.Validate(
            validators as Core.IPalValidator<TQuery>[] ?? validators.ToArray(), query);
        if (errors is { Count: > 0 })
        {
            await WriteValidationProblemAsync(
                context, new PalValidationException([.. errors])).ConfigureAwait(false);
            return;
        }

        var registry = context.RequestServices.GetRequiredService<StreamRunRegistry>();
        string runId;
        try
        {
            runId = registry.Register(query, eventSelector, idSelector);
        }
        catch (StreamRunCapacityExceededException)
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Detached stream run capacity exceeded; retry later.").ConfigureAwait(false);
            return;
        }

        if (!registry.TryGet<TItem>(runId, out var run))
        {
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError,
                "Detached stream run registration failed.").ConfigureAwait(false);
            return;
        }

        if (!run.TryBeginAttach(context.GetLastEventId(), out var startIndex, out var gap, out var generation))
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                "Another client is already attached to this stream run.").ConfigureAwait(false);
            return;
        }

        context.Response.Headers["X-Run-Id"] = runId;
        WriteSseHeaders(context);
        await run.StreamToAsync(context, itemJsonTypeInfo, generation, startIndex, gap, keepAliveInterval)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 附着到已有 run（GET <c>{pattern}/{runId}/stream</c>），遵守 <c>Last-Event-ID</c>：
    /// 先回放缓冲中该位置之后的项，再转 live。
    /// </summary>
    /// <remarks>
    /// 答复：未知或已过期 run 404；位置早于缓冲起点时首帧 <c>event: gap</c>；
    /// 终态 run 在保留期内回放剩余缓冲后以 <c>event: done|cancelled|error</c> 收尾。
    /// 选项 <c>RejectNewAttach</c> 为 true 时已有附着返回 409；默认接管旧连接（旧连接收 <c>event: detached</c>）。
    /// </remarks>
    public static IEndpointConventionBuilder MapStreamRunAttach<TItem>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        TimeSpan? keepAliveInterval = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(itemJsonTypeInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveInterval ?? TimeSpan.MaxValue, TimeSpan.Zero);

        return endpoints.MapGet($"{pattern}/{{runId}}/stream", async context =>
        {
            var registry = context.RequestServices.GetRequiredService<StreamRunRegistry>();
            var runId = context.Request.RouteValues["runId"] as string;
            if (string.IsNullOrEmpty(runId) || !registry.TryGet<TItem>(runId, out var run))
            {
                await WriteProblemAsync(context, StatusCodes.Status404NotFound,
                    "Stream run not found or expired.").ConfigureAwait(false);
                return;
            }

            if (!run.TryBeginAttach(context.GetLastEventId(), out var startIndex, out var gap, out var generation))
            {
                await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                    "Another client is already attached to this stream run.").ConfigureAwait(false);
                return;
            }

            WriteSseHeaders(context);
            await run.StreamToAsync(context, itemJsonTypeInfo, generation, startIndex, gap, keepAliveInterval)
                .ConfigureAwait(false);
        });
    }

    /// <summary>显式取消 run（POST <c>{pattern}/{runId}/cancel</c>，幂等：终态返回 204）。</summary>
    public static IEndpointConventionBuilder MapStreamRunCancel(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost($"{pattern}/{{runId}}/cancel", async context =>
        {
            var registry = context.RequestServices.GetRequiredService<StreamRunRegistry>();
            var runId = context.Request.RouteValues["runId"] as string;
            if (string.IsNullOrEmpty(runId) || !registry.Cancel(runId))
            {
                await WriteProblemAsync(context, StatusCodes.Status404NotFound,
                    "Stream run not found or expired.").ConfigureAwait(false);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
    }

    private static void WriteSseHeaders(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { NoCache = true };
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteValidationProblemAsync(HttpContext context, PalValidationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(
            ValidationProblemResponseFactory.Create(ex),
            PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
            contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        // body 的 status/title 与 HTTP 状态码一致（不复用 400 专用工厂，防 503/404 体报 400）
        await context.Response.WriteAsJsonAsync(
            new ValidationProblemResponse(
                "about:blank",
                statusCode switch
                {
                    StatusCodes.Status404NotFound => "Stream Run Not Found",
                    StatusCodes.Status409Conflict => "Stream Run Conflict",
                    StatusCodes.Status503ServiceUnavailable => "Stream Run Capacity Exceeded",
                    StatusCodes.Status400BadRequest => "Invalid Request",
                    _ => "Stream Run Error"
                },
                statusCode,
                [new ValidationProblemError("run", message)]),
            PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
            contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }
}
