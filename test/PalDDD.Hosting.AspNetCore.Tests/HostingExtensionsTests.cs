using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalDDD.Hosting.AspNetCore.OutboxHealth;
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
    public async Task AddPalHealthChecks_SelfProvidesLoggerFacadeForExceptionMiddleware()
    {
        // R11 F2 回归锁：usage.md 的独立 ASP.NET 片段只调 AddPalHealthChecks +
        // UsePalExceptionHandler；Hosting 入口必须自带 IPalLogger 门面，否则首个请求
        // 构造 ExceptionMiddleware 时抛 InvalidOperationException。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalHealthChecks();
        using var sp = services.BuildServiceProvider();

        var middleware = Microsoft.Extensions.DependencyInjection.ActivatorUtilities
            .CreateInstance<ExceptionMiddleware>(sp, (RequestDelegate)(_ => Task.CompletedTask));

        await Assert.That(middleware.GetType()).IsEqualTo(typeof(ExceptionMiddleware));
    }

    [Test]
    public async Task AddDetachedStreamRuns_SelfProvidesLoggerFacadeForExceptionMiddleware()
    {
        // R11 F2 同族：Hosting 的另一个公开注册入口也必须装门面（消费方常只注册流式能力）。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDetachedStreamRuns();
        using var sp = services.BuildServiceProvider();

        var middleware = Microsoft.Extensions.DependencyInjection.ActivatorUtilities
            .CreateInstance<ExceptionMiddleware>(sp, (RequestDelegate)(_ => Task.CompletedTask));

        await Assert.That(middleware.GetType()).IsEqualTo(typeof(ExceptionMiddleware));
    }

    [Test]
    public async Task AddPalHealthChecks_CalledTwice_DeduplicatesRegistrations()
    {
        // R11 F3 回归锁：重复注册曾让 HealthCheckService 抛「Duplicate health checks」。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalHealthChecks();
        services.AddPalHealthChecks();
        using var sp = services.BuildServiceProvider();

        var names = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HealthCheckServiceOptions>>()
            .CurrentValue.Registrations.Select(r => r.Name).ToArray();
        var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        await Assert.That(names.Length).IsEqualTo(2);
        await Assert.That(names.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
        await Assert.That(report.Entries.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalHealthChecks_WithOptionalOutboxPackage_DeduplicatesOutbox()
    {
        // R11 F3 回归锁：文档称两个入口「二选一」，实际并用曾让 /health 抛异常。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalHealthChecks();
        services.AddPalOutboxHealthCheck();
        using var sp = services.BuildServiceProvider();

        var names = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HealthCheckServiceOptions>>()
            .CurrentValue.Registrations.Select(r => r.Name).ToArray();
        var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        await Assert.That(names.Length).IsEqualTo(2);
        await Assert.That(names.Count(n => n == "outbox")).IsEqualTo(1);
        await Assert.That(report.Entries.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalHealthChecks_DuplicateName_KeepsLastRegistration()
    {
        // R13 F7：注释承诺「保留最后一次」，实现曾保留首个——重名时幸存者的 Tags
        // 决定 tag 过滤端点（Predicate = r.Tags.Contains("pal"/"persistence")）是否包含该项。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddCheck("outbox", () => HealthCheckResult.Healthy(), tags: ["user"]);
        services.AddPalHealthChecks();
        using var sp = services.BuildServiceProvider();

        var registrations = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HealthCheckServiceOptions>>()
            .CurrentValue.Registrations;
        var survivor = registrations.Single(r => r.Name == "outbox");

        await Assert.That(survivor.Tags.Contains("persistence")).IsTrue();
        await Assert.That(survivor.Tags.Contains("user")).IsFalse();
    }

    [Test]
    public async Task AddDetachedStreamRuns_SecondCallWithInvalidConfig_IsIgnored()
    {
        // R13 F9：文档承诺「幂等：重复调用的配置以首次为准」，但第二次的 configure
        // 与 Validate 在 TryAddSingleton 之前无条件执行——被丢弃的配置仍抛异常。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDetachedStreamRuns();

        services.AddDetachedStreamRuns(o => o.MaxItems = 0);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<DetachedStreamOptions>();
        await Assert.That(options.MaxItems).IsGreaterThanOrEqualTo(1);
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
    public async Task MessageBrokerHealthCheck_BrokerReportsUnhealthyConsuming_ReturnsUnhealthy(CancellationToken cancellationToken)
    {
        // R14：实现 IMessageBrokerHealth 的 broker（Kafka）自述消费链路状态——
        // 消费线程不可恢复终止时健康端点必须转 Unhealthy，而不是「已注册」恒绿。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageBroker>(new UnhealthyBroker());
        services.AddPalHealthChecks();

        using var sp = services.BuildServiceProvider();
        var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            r => r.Name == "message_broker",
            cancellationToken);

        await Assert.That(report.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(report.Entries.First().Value.Description).Contains("消费链路已停止");
    }

    private sealed class UnhealthyBroker : IMessageBroker, IMessageBrokerHealth
    {
        public bool IsConsumingHealthy => false;

        public string? LastFaultReason => "consume thread crashed";

        public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask PublishAsync(
            object message,
            PalDDD.Serialization.MessageDescriptor descriptor,
            ByteAether.Ulid.Ulid messageId,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
            => throw new NotSupportedException();
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

    /// <summary>ITM-800（2026-09-19）：WriteAsJsonAsync(contentType:null) 用框架默认
    /// "application/json; charset=utf-8" **覆盖**此前手动设置——原 :61 的手动
    /// ContentType 赋值是死代码（已删）。本测试锁定删除后的可观察行为：最终
    /// ContentType 为框架默认（含 charset），响应体合法 JSON。</summary>
    [Test]
    public async Task WriteHealthResponse_FinalContentTypeIsFrameworkDefault_NotManualSet()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>(), HealthStatus.Healthy, TimeSpan.Zero);

        await HealthCheckExtensions.WriteHealthResponseAsync(context, report, new FixedTimeProvider(DateTimeOffset.UtcNow));

        await Assert.That(context.Response.ContentType)
            .IsEqualTo("application/json; charset=utf-8");
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

    // ── ITM-696（2026-10-27 清零轮）：MapQuery 绑定段输入解析异常归 400 ──
    // 此前 FormatException 等输入解析失败以 500 逃逸（客户端输入问题被报成服务端故障）。

    [Test]
    public async Task MapQuery_BindQueryThrowsFormatException_Returns400Not500()
    {
        var endpoints = new TestEndpointRouteBuilder();
        endpoints.MapQuery<TestQuery, TestResult>(
            "/orders/{id}",
            static _ => throw new FormatException("Unrecognized Guid format."),
            TestJsonContext.Default.TestResult);

        var context = CreateGetContext();
        await GetRequestDelegate(endpoints)(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
        var body = ReadBody(context);
        await Assert.That(body).Contains("Invalid query binding");
        await Assert.That(body).Contains("Unrecognized Guid format");
    }

    [Test]
    public async Task MapQuery_BindQueryThrowsInvalidOperationException_StillEscapes()
    {
        // 白名单外异常仍逃逸（500 由全局中间件兜底）——证明 400 映射按类型收窄，
        // 服务端 bug 不被吞成 400（ITM-696 设计边界）
        var endpoints = new TestEndpointRouteBuilder();
        endpoints.MapQuery<TestQuery, TestResult>(
            "/q", static _ => throw new InvalidOperationException("server bug"),
            TestJsonContext.Default.TestResult);

        var context = CreateGetContext();
        await Assert.That(() => GetRequestDelegate(endpoints)(context)).Throws<InvalidOperationException>();
    }

    // ── ITM-695（2026-10-27 清零轮）：MapQuery 的授权约定链路可用性 ──
    // 端点返回 IEndpointConventionBuilder，调用方可直接链 RequireAuthorization(策略)——
    // 拒绝执行由 ASP.NET Core 授权中间件按挂载的策略执行（401/403），框架层证明到
    // 策略元数据成功挂载即可。

    [Test]
    public async Task MapQuery_RequireAuthorizationChain_MountsPolicyMetadata()
    {
        var endpoints = new TestEndpointRouteBuilder();
        endpoints.MapQuery<TestQuery, TestResult>(
            "/orders/{id}", static _ => new TestQuery(), TestJsonContext.Default.TestResult)
            .RequireAuthorization("admin-policy");

        var endpoint = endpoints.DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().Single();
        // RequireAuthorization(policyName) 挂载 IAuthorizeData 元数据（策略由授权中间件
        // 从该元数据编译执行）——框架层证明约定链可用即锚定到这一层
        var authorizeData = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>();
        await Assert.That(authorizeData).IsNotNull();
        await Assert.That(authorizeData!.Policy).IsEqualTo("admin-policy");
    }

    [Test]
    public async Task MapPalHealthChecks_ConfigureCallback_AppliesConvention()
    {
        // R13 F10：类备注要求生产部署对 /health 链 RequireAuthorization，但方法返回
        // IEndpointRouteBuilder 无法链接约定——新增可选 configure 回调暴露真正的
        // IEndpointConventionBuilder（非破坏：既有调用不受影响）。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalHealthChecks();
        using var sp = services.BuildServiceProvider();
        var endpoints = new TestEndpointRouteBuilder(sp);

        endpoints.MapPalHealthChecksWithConventions().WithMetadata(new HealthMarkerMetadata());

        var endpoint = endpoints.DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().Single();
        await Assert.That(endpoint.Metadata.GetMetadata<HealthMarkerMetadata>()).IsNotNull();
    }

    private sealed class HealthMarkerMetadata;

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

    /// <summary>构造 GET 请求上下文（MapQuery 绑定段测试）</summary>
    private static DefaultHttpContext CreateGetContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>源生成 JsonContext——仓库全局 JsonSerializerIsReflectionEnabledByDefault=false
    ///（Directory.Build.props），反射版 GetTypeInfo 不可用；415 短路不触达反序列化，
    /// 本 context 仅满足 MapCommand 的非空 JsonTypeInfo 参数契约</summary>
    [JsonSerializable(typeof(TestCommand))]
    [JsonSerializable(typeof(TestCommandWithResult))]
    [JsonSerializable(typeof(TestResult))]
    [JsonSerializable(typeof(TestQuery))]
    private sealed partial class TestJsonContext : JsonSerializerContext;

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>IEndpointRouteBuilder 最小实现——收集 DataSources 供提取注册的端点</summary>
    private sealed class TestEndpointRouteBuilder(IServiceProvider? serviceProvider = null) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider ?? new ServiceCollection().BuildServiceProvider();

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class TestCommand : CQRS.ICommand
    {
        public Guid Id { get; init; }
    }

    /// <summary>MapQuery 绑定测试查询（ITM-695/696）</summary>
    private sealed class TestQuery : CQRS.IQuery<TestResult>;

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
