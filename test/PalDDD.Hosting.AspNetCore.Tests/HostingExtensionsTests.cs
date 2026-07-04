using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalDDD.Messaging;
using System.Globalization;
using System.Text.Json;

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

public sealed class EndpointExtensionsTests
{
    [Test]
    public async Task MapCommand_NullEndpoints_Throws()
    {
        await Assert.That(() =>
            ((IEndpointRouteBuilder)null!).MapCommand<TestCommand>("/cmd", null!)).Throws<ArgumentNullException>();
    }

    private sealed class TestCommand : CQRS.ICommand
    {
        public Guid Id { get; init; }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => timestamp;
}
