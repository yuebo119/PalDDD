using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using PalDDD.Core;
using PalDDD.Core.Logging;
using PalDDD.CQRS;
using System.Text;

namespace PalDDD.Hosting.AspNetCore.Tests;

public sealed class ExceptionMiddlewareTests
{
    [Test]
    public async Task InvokeAsync_NoException_PassesThroughWithoutModification()
    {
        var middleware = new ExceptionMiddleware(
            next: ctx => { ctx.Response.StatusCode = StatusCodes.Status204NoContent; return Task.CompletedTask; },
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status204NoContent);
    }

    [Test]
    public async Task InvokeAsync_PalValidationException_Returns400WithProblemDetails()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new PalValidationException(
                [new PalValidationError("Email", "Email is required."),
                 new PalValidationError("Age", "Age must be >= 18.")]),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
        var body = ReadBody(context);
        await Assert.That(body).Contains("Validation Failed");
        await Assert.That(body).Contains("Email");
        await Assert.That(body).Contains("Age");
    }

    [Test]
    public async Task InvokeAsync_HandlerNotFoundException_Returns404WithProblemDetails()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new HandlerNotFoundException(typeof(MissingCommand)),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status404NotFound);
        var body = ReadBody(context);
        await Assert.That(body).Contains("Handler Not Found");
        await Assert.That(body).Contains("MissingCommand");
    }

    [Test]
    public async Task InvokeAsync_UnhandledException_Returns500WithGenericProblemDetails()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new InvalidOperationException("internal database error with secret connection string"),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status500InternalServerError);
        var body = ReadBody(context);
        await Assert.That(body).Contains("Internal Server Error");
        // 敏感信息（如包含连接字符串的异常消息）绝对不能泄露到响应中。
        await Assert.That(body).DoesNotContain("secret connection string");
    }

    [Test]
    [MethodDataSource(nameof(ResponseStartedExceptions))]
    public async Task InvokeAsync_ResponseAlreadyStarted_Rethrows(Exception expected)
    {
        var middleware = new ExceptionMiddleware(
            next: async ctx =>
            {
                await ctx.Response.StartAsync();
                throw expected;
            },
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateStartedContext();
        var actual = await Assert.That(() => middleware.InvokeAsync(context)).Throws<Exception>();

        await Assert.That(actual).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task InvokeAsync_NullContext_ThrowsArgumentNullException()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        await Assert.That(() => middleware.InvokeAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task InvokeAsync_OperationCanceledException_PropagatesAndDoesNotMapTo500()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new OperationCanceledException("client disconnected"),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        var ex = await Assert.That(() => middleware.InvokeAsync(context)).Throws<OperationCanceledException>();

        await Assert.That(ex!.Message).IsEqualTo("client disconnected");
        // 取消请求不应写入 500 响应体
        context.Response.Body.Position = 0;
        await Assert.That(context.Response.Body.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InvokeAsync_UsesUpdatedRfc9110Urls()
    {
        var middleware = new ExceptionMiddleware(
            next: _ => throw new PalValidationException(
                [new PalValidationError("Field", "Required")]),
            logger: NullPalLogger<ExceptionMiddleware>.Instance);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        var body = ReadBody(context);
        await Assert.That(body).Contains("rfc9110");
        await Assert.That(body).DoesNotContain("rfc7231");
    }

    [Test]
    public async Task Constructor_NullNext_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
            new ExceptionMiddleware(next: null!, logger: NullPalLogger<ExceptionMiddleware>.Instance)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
            new ExceptionMiddleware(next: _ => Task.CompletedTask, logger: null!)).Throws<ArgumentNullException>();
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreateStartedContext()
    {
        var context = CreateContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        return context;
    }

    public static IEnumerable<Func<Exception>> ResponseStartedExceptions()
    {
        yield return () => new PalValidationException([new PalValidationError("Email", "Email is required.")]);
        yield return () => new HandlerNotFoundException(typeof(MissingCommand));
        yield return () => new InvalidOperationException("response already started");
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        private readonly HeaderDictionary _headers = [];

        public int StatusCode { get; set; }
        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers
        {
            get => _headers;
            set => throw new NotSupportedException();
        }

        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;

        public void OnCompleted(Func<object, Task> callback, object state)
        { }

        public void OnStarting(Func<object, Task> callback, object state)
        { }
    }

    /// <summary>Marker command type for HandlerNotFoundException tests.</summary>
    private sealed class MissingCommand : ICommand;
}
