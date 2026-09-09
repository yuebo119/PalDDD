using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalDDD.Messaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalDDD.Hosting.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════
// 🏥 Hosting 扩展测试 — HealthCheck/Endpoint 注册验证
// ═══════════════════════════════════════════════════════════════
// 验证 DI 注册与端点映射的正确性，不测试 HTTP 运行时（那是集成测试职责）。
// ═══════════════════════════════════════════════════════════════

public sealed class HealthCheckExtensionsTests
{
    [Test]
    public async Task AddPalHealthChecks_RegistersPalHealthChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPalHealthChecks();

        using var sp = services.BuildServiceProvider();
        var healthService = sp.GetService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        await Assert.That(healthService).IsNotNull();
    }

    [Test]
    public async Task AddPalHealthChecks_RegistersBrokerAndOutboxChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPalHealthChecks();

        // 验证注册了两个健康检查（message_broker + outbox）
        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>();
        var registrations = options.CurrentValue.Registrations;

        var names = registrations.Select(r => r.Name).ToHashSet();
        await Assert.That(names).Contains("message_broker");
        await Assert.That(names).Contains("outbox");
    }

    [Test]
    public async Task MessageBrokerHealthCheck_WithBroker_ReturnsHealthyRegistrationCheck(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageBroker, NullMessageBroker>();
        services.AddPalHealthChecks();

        using var sp = services.BuildServiceProvider();
        var healthService = sp.GetRequiredService<HealthCheckService>();

        var report = await healthService.CheckHealthAsync(
            r => r.Name == "message_broker",
            cancellationToken);

        await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(report.Entries).Count().IsEqualTo(1);
        var entry = report.Entries.First();
        await Assert.That(entry.Value.Description).IsEqualTo("消息代理已注册");
    }

    [Test]
    public async Task MessageBrokerHealthCheck_WithoutBroker_ReturnsDegradedRegistrationCheck(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalHealthChecks();

        using var sp = services.BuildServiceProvider();
        var healthService = sp.GetRequiredService<HealthCheckService>();

        var report = await healthService.CheckHealthAsync(
            r => r.Name == "message_broker",
            cancellationToken);

        await Assert.That(report.Status).IsEqualTo(HealthStatus.Degraded);
        await Assert.That(report.Entries).Count().IsEqualTo(1);
        var entry = report.Entries.First();
        await Assert.That(entry.Value.Description).IsEqualTo("消息代理未注册");
    }

    [Test]
    public async Task MapPalHealthChecks_UsesProvidedTimeProviderForResponseTimestamp(CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Parse("2026-06-27T12:00:00Z", CultureInfo.InvariantCulture);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>(), HealthStatus.Healthy, TimeSpan.Zero);

        await HealthCheckExtensions.WriteHealthResponseAsync(context, report, new FixedTimeProvider(timestamp));

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: cancellationToken);
        await Assert.That(document.RootElement.GetProperty("timestamp").GetDateTimeOffset()).IsEqualTo(timestamp);
    }

    [Test]
    public async Task AddPalHealthChecks_NullServices_Throws()
    {
        await Assert.That(() =>
            ((IServiceCollection)null!).AddPalHealthChecks()).Throws<ArgumentNullException>();
    }
}

// 容器类 partial：嵌套 TestJsonContext（源生成 JsonSerializerContext）要求包含类型链全部 partial（SYSLIB1032）
public sealed partial class EndpointExtensionsTests
{
    /// <summary>v9 E6：CreateInvalidBody（畸形 JSON/空 body 400 的 ProblemDetails 工厂）契约——
    /// v8 引入时零测试覆盖（人工复核替代），本测试锁定 title/errors/状态码形态（IVT 可见 internal）。</summary>
    [Test]
    public async Task CreateInvalidBody_ProducesProblemDetailsShape()
    {
        var response = ValidationProblemResponseFactory.CreateInvalidBody("probe detail");

        await Assert.That(response.Status).IsEqualTo(StatusCodes.Status400BadRequest);
        await Assert.That(response.Title).IsEqualTo("Invalid Request Body");
        await Assert.That(response.Type).IsEqualTo("https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1");
        await Assert.That(response.Errors).Count().IsEqualTo(1);
    }

    [Test]
    public async Task MapCommand_NullEndpoints_Throws()
    {
        await Assert.That(() =>
            ((IEndpointRouteBuilder)null!).MapCommand<TestCommand>("/cmd", null!)).Throws<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════
    // v41 P3：415 短路路径测试（v40 引入，此前零测试覆盖）
    // ═══════════════════════════════════════════════════════════════
    // 最小 endpoint 测试：经 MapCommand 注册路由后直取 RouteEndpoint.RequestDelegate 调用
    //（镜像 ExceptionMiddlewareTests 的 DefaultHttpContext 形态，无 TestServer 依赖）；
    // 415 短路发生在 ReadFromJsonAsync 之前，无需注册 Dispatcher，反射版 JsonTypeInfo
    // 也不会被实际用于反序列化

    [Test]
    public async Task MapCommand_NonJsonContentType_Returns415WithProblemDetails()
    {
        var endpoints = new TestEndpointRouteBuilder();
        endpoints.MapCommand<TestCommand>("/cmd", TestJsonContext.Default.TestCommand);

        var context = CreatePostContext("text/plain");
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status415UnsupportedMediaType);
        var body = ReadBody(context);
        await Assert.That(body).Contains("Unsupported Media Type");
        await Assert.That(body).Contains("contentType");
    }

    [Test]
    public async Task MapCommandWithResponse_NonJsonContentType_Returns415WithProblemDetails()
    {
        var endpoints = new TestEndpointRouteBuilder();
        endpoints.MapCommand<TestCommandWithResult, TestResult>(
            "/cmd", TestJsonContext.Default.TestCommandWithResult, TestJsonContext.Default.TestResult);

        var context = CreatePostContext("application/xml");
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status415UnsupportedMediaType);
        var body = ReadBody(context);
        await Assert.That(body).Contains("Unsupported Media Type");
    }

    /// <summary>提取路由构建结果中唯一端点的请求委托（MapPost 注册的 lambda）；
    /// 编译期标注可空（未构建路由表），运行时由 MapPost 保证非空</summary>
    private static RequestDelegate GetRequestDelegate(IEndpointRouteBuilder endpoints)
        => endpoints.DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().Single().RequestDelegate!;

    /// <summary>构造带指定 Content-Type 的 POST 请求上下文（415 短路判定只依赖 Content-Type）</summary>
    private static DefaultHttpContext CreatePostContext(string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>源生成 JsonContext——仓库全局 JsonSerializerIsReflectionEnabledByDefault=false
    ///（Directory.Build.props），反射版 GetTypeInfo 不可用；415 短路不触达反序列化，
    /// 本 context 仅满足 MapCommand 的非空 JsonTypeInfo 参数契约</summary>
    [JsonSerializable(typeof(TestCommand))]
    [JsonSerializable(typeof(TestCommandWithResult))]
    [JsonSerializable(typeof(TestResult))]
    private sealed partial class TestJsonContext : JsonSerializerContext;

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>IEndpointRouteBuilder 最小实现——收集 DataSources 供提取注册的端点</summary>
    private sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class TestCommand : CQRS.ICommand
    {
        public Guid Id { get; init; }
    }

    private sealed class TestCommandWithResult : CQRS.ICommand<TestResult>
    {
        public Guid Id { get; init; }
    }

    private sealed record TestResult(Guid Id);
}

internal sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => timestamp;
}
