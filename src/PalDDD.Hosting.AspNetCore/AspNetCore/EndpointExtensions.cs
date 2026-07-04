using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization.Metadata;

namespace PalDDD.Hosting.AspNetCore;

// ─────────────────────────────────────────────────────────────
// Minimal API 端点映射
// ─────────────────────────────────────────────────────────────

/// <summary>Minimal API 命令/查询端点映射扩展。调用方必须传入源生成 JSON metadata 以保持 AOT 安全。</summary>
public static class EndpointExtensions
{
    /// <summary>映射无返回值命令到 HTTP POST 端点</summary>
    public static IEndpointConventionBuilder MapCommand<TCommand>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        JsonTypeInfo<TCommand> commandJsonTypeInfo)
        where TCommand : CQRS.ICommand
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(commandJsonTypeInfo);

        return endpoints.MapPost(pattern, async context =>
        {
            var cmd = await context.Request.ReadFromJsonAsync(
                commandJsonTypeInfo,
                context.RequestAborted);
            if (cmd is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            await dispatcher.SendAsync(cmd, ct);
            context.Response.StatusCode = StatusCodes.Status200OK;
        });
    }

    /// <summary>映射有返回值命令到 HTTP POST 端点</summary>
    public static IEndpointConventionBuilder MapCommand<TCommand, TResponse>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        JsonTypeInfo<TCommand> commandJsonTypeInfo,
        JsonTypeInfo<TResponse> responseJsonTypeInfo)
        where TCommand : CQRS.ICommand<TResponse>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(commandJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(responseJsonTypeInfo);

        return endpoints.MapPost(pattern, async context =>
        {
            var cmd = await context.Request.ReadFromJsonAsync(
                commandJsonTypeInfo,
                context.RequestAborted);
            if (cmd is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            var result = await dispatcher.SendAsync(cmd, ct);
            await context.Response.WriteAsJsonAsync(
                result,
                responseJsonTypeInfo,
                contentType: null,
                cancellationToken: ct);
        });
    }

    /// <summary>映射查询到 HTTP GET 端点。查询绑定由调用方显式提供，避免运行时模型绑定反射。</summary>
    public static IEndpointConventionBuilder MapQuery<TQuery, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TQuery> bindQuery,
        JsonTypeInfo<TResult> responseJsonTypeInfo)
        where TQuery : CQRS.IQuery<TResult>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(bindQuery);
        ArgumentNullException.ThrowIfNull(responseJsonTypeInfo);

        return endpoints.MapGet(pattern, async context =>
        {
            var query = bindQuery(context);
            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            var result = await dispatcher.QueryAsync(query, ct);
            await context.Response.WriteAsJsonAsync(
                result,
                responseJsonTypeInfo,
                contentType: null,
                cancellationToken: ct);
        });
    }
}
