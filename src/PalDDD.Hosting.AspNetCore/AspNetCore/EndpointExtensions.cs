using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization.Metadata;

namespace PalDDD.Hosting.AspNetCore;

// ─────────────────────────────────────────────────────────────
// Minimal API 端点映射
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Minimal API 命令/查询端点映射扩展。调用方必须传入源生成 JSON metadata 以保持 AOT 安全。
/// <para>
/// ⚠️ <b>滥用控制声明（三十八轮 P2）</b>：本类映射的端点默认不含速率限制与请求体大小约束——
/// 生产部署须在宿主层配置（ASP.NET Core 内置 RateLimiter 中间件 + RequestSizeLimit），
/// 或置于网关限流之后。传输强制（TLS/HSTS）同样由宿主/反向代理层承担。
/// </para>
/// </summary>
public static class EndpointExtensions
{
    /// <summary>映射无返回值命令到 HTTP POST 端点。⚠️ 滥用控制见类 doc 声明。</summary>
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
            TCommand? cmd;
            try
            {
                cmd = await context.Request.ReadFromJsonAsync(
                    commandJsonTypeInfo,
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                // P3 修复：畸形 JSON 是用户输入错误 → 400 而非未捕获 500
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // P2 修复：验证失败异常映射 400（框架意图与实现一致化）
                // ITM-283（R45）：统一走 Factory 带 ProblemDetails body——原裸 400 与
                // 派发段（ITM-168）形态分叉（自定义 JsonConverter 抛此异常时客户端拿不到错误明细）
                await WriteValidationProblemAsync(context, ex).ConfigureAwait(false);
                return;
            }
            if (cmd is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            try
            {
                // ITM-091 修复：SendAsync 纳入本地 try——PalValidationException 由 Dispatcher 派发时
                // 抛出（原 catch 仅覆盖 ReadFromJsonAsync），此前验证失败会逃逸为 500；
                // JsonException catch 语义保持不变（仍只覆盖反序列化）
                await dispatcher.SendAsync(cmd, ct).ConfigureAwait(false);
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // 验证轮返工：与 ExceptionMiddleware 同款 ProblemDetails 响应体（裸 400 无 body
                // 会让客户端拿不到错误明细；日志由派发管线内记录）
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                var response = ValidationProblemResponseFactory.Create(ex);
                await context.Response.WriteAsJsonAsync(
                    response,
                    PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                    contentType: null,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }
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
            TCommand? cmd;
            try
            {
                cmd = await context.Request.ReadFromJsonAsync(
                    commandJsonTypeInfo,
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                // P3 修复：畸形 JSON 是用户输入错误 → 400 而非未捕获 500
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // P2 修复：验证失败异常映射 400（框架意图与实现一致化）
                // ITM-283（R45）：统一走 Factory 带 ProblemDetails body——原裸 400 与
                // 派发段（ITM-168）形态分叉（自定义 JsonConverter 抛此异常时客户端拿不到错误明细）
                await WriteValidationProblemAsync(context, ex).ConfigureAwait(false);
                return;
            }
            if (cmd is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            TResponse result;
            try
            {
                // ITM-091 修复：SendAsync 纳入本地 try——PalValidationException 由 Dispatcher 派发时
                // 抛出（原 catch 仅覆盖 ReadFromJsonAsync），此前验证失败会逃逸为 500；
                // JsonException catch 语义保持不变（仍只覆盖反序列化）
                result = await dispatcher.SendAsync(cmd, ct).ConfigureAwait(false);
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // 验证轮返工：与 ExceptionMiddleware 同款 ProblemDetails 响应体
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                var response = ValidationProblemResponseFactory.Create(ex);
                await context.Response.WriteAsJsonAsync(
                    response,
                    PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                    contentType: null,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            await context.Response.WriteAsJsonAsync(
                result,
                responseJsonTypeInfo,
                contentType: null,
                cancellationToken: ct).ConfigureAwait(false);
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
            TResult result;
            try
            {
                // ITM-168 修复：QueryAsync 纳入本地 try——PalValidationException 由 Dispatcher
                // 派发时抛出，此前验证失败逃逸为 500（MapCommand 两个方法均已映射 400，
                // 查询端点缺失同族映射）。
                result = await dispatcher.QueryAsync(query, ct).ConfigureAwait(false);
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // 严格对齐 MapCommand 两个方法的响应体写法：与 ExceptionMiddleware 同款
                // ProblemDetails 响应体（裸 400 无 body 会让客户端拿不到错误明细）。
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                var response = ValidationProblemResponseFactory.Create(ex);
                await context.Response.WriteAsJsonAsync(
                    response,
                    PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                    contentType: null,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            await context.Response.WriteAsJsonAsync(
                result,
                responseJsonTypeInfo,
                contentType: null,
                cancellationToken: ct).ConfigureAwait(false);
        });
    }

    /// <summary>写入 400 + ValidationProblemResponse 体（ITM-283：反序列化段与派发段统一形态——
    /// 消除裸 400 无 body 与派发段 ITM-168 形态的分叉）。</summary>
    private static async Task WriteValidationProblemAsync(HttpContext context, CQRS.PalValidationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        var response = ValidationProblemResponseFactory.Create(ex);
        await context.Response.WriteAsJsonAsync(
            response,
            PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
            contentType: null).ConfigureAwait(false);
    }
}

internal static class ValidationProblemResponseFactory
{
    /// <summary>按 PalValidationException 构造规范 ValidationProblemResponse（RFC 9110 §15.5.1）。</summary>
    internal static ValidationProblemResponse Create(CQRS.PalValidationException ex)
        => new(
            "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1",
            "Validation Failed",
            StatusCodes.Status400BadRequest,
            ex.Errors.Select(e => new ValidationProblemError(e.PropertyName, e.Message)).ToArray());
}
