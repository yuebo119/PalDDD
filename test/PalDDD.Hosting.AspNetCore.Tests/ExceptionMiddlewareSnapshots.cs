using Microsoft.AspNetCore.Http;
using PalDDD.Core;
using PalDDD.Core.Logging;
using PalDDD.CQRS;
using System.Text;

namespace PalDDD.Hosting.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════
// 📸 快照测试 — 用 Verify 捕获 HTTP 响应完整结构
// ═══════════════════════════════════════════════════════════════
// 替代 Assert.Contains 的脆弱字符串断言。
// 首次运行生成 .received.txt，经人工审阅后成为 .verified.txt 基线。
// 响应结构变更时 diff 醒目，避免 ProblemDetails 契约漂移。
// ═══════════════════════════════════════════════════════════════

public sealed class ExceptionMiddlewareSnapshots
{
    [Test]
    public async Task ValidationException_ProducesCanonicalProblemDetails()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new PalValidationException(
                [new PalValidationError("Email", "Email is required."),
                 new PalValidationError("Age", "Age must be >= 18.")]),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        await Verifier.Verify(new
        {
            context.Response.StatusCode,
            context.Response.ContentType,
            Body = ReadBody(context)
        });
    }

    [Test]
    public async Task HandlerNotFoundException_Produces404ProblemDetails()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new HandlerNotFoundException(typeof(SnapshotMissingCommand)),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        await Verifier.Verify(new
        {
            context.Response.StatusCode,
            Body = ReadBody(context)
        });
    }

    [Test]
    public async Task UnhandledException_Produces500GenericProblemDetails()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new InvalidOperationException("internal error with secret token"),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        await Verifier.Verify(new
        {
            context.Response.StatusCode,
            Body = ReadBody(context)
        });
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>快照测试专用标记命令</summary>
    private sealed class SnapshotMissingCommand : ICommand;
}
