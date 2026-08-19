// ─────────────────────────────────────────────────────────────
// 🛡️ ExceptionMiddleware — 全局异常处理（400/404/500 + SourceGen JSON）
// ─────────────────────────────────────────────────────────────
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Hosting.AspNetCore;

// ─────────────────────────────────────────────────────────────
// 异常处理中间件
// ─────────────────────────────────────────────────────────────

/// <summary>PalDDD 异常处理中间件 — 将框架异常映射为 ProblemDetails HTTP 响应</summary>
/// <remarks>
/// 注册方式（建议放在中间件管道最前面）：<br/>
/// <c>app.UsePalExceptionHandler();</c>
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "顶层 ASP.NET Core 异常中间件需将未预期异常统一映射为 500 响应，故需捕获 Exception 基类。")]
public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IPalLogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, IPalLogger<ExceptionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (CQRS.PalValidationException ex)
        {
            if (context.Response.HasStarted) throw;

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            var response = new ValidationProblemResponse(
                "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1",
                "Validation Failed",
                StatusCodes.Status400BadRequest,
                ex.Errors.Select(e => new ValidationProblemError(e.PropertyName, e.Message)).ToArray());
            await context.Response.WriteAsJsonAsync(
                response,
                PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                contentType: null,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (CQRS.HandlerNotFoundException ex)
        {
            if (context.Response.HasStarted) throw;

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            var response = new HandlerNotFoundProblemResponse(
                "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.5",
                "Handler Not Found",
                StatusCodes.Status404NotFound,
                ex.Message);
            await context.Response.WriteAsJsonAsync(
                response,
                PalAspNetCoreJsonContext.Default.HandlerNotFoundProblemResponse,
                contentType: null,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 请求取消不映射为 500，正常传播以触发 ASP.NET Core 的标准取消处理。
            throw;
        }
        catch (Exception ex)
        {
            // ITM-197 修复（三十轮）：响应已开始（SSE/流式）后抛出的未处理异常也须落日志——
            // 原实现 HasStarted 直接 rethrow 不记录，该路径在日志中不可见（与常规 500 分歧）。
            _logger.Error(ex, "Unhandled exception");
            if (context.Response.HasStarted) throw;

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            var response = new InternalServerErrorProblemResponse(
                "https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1",
                "Internal Server Error",
                StatusCodes.Status500InternalServerError);
            await context.Response.WriteAsJsonAsync(
                response,
                PalAspNetCoreJsonContext.Default.InternalServerErrorProblemResponse,
                contentType: null,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
    }
}

/// <summary>异常处理中间件扩展</summary>
public static class ExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UsePalExceptionHandler(this IApplicationBuilder builder)
        => builder.UseMiddleware<ExceptionMiddleware>();
}
