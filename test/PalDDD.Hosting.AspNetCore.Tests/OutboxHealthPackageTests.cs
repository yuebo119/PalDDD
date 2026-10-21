using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PalDDD.Hosting.AspNetCore.OutboxHealth;

namespace PalDDD.Hosting.AspNetCore.Tests;

/// <summary>
/// T8（基础库优化清单）：独立 Outbox 健康检查包。注册名为 outbox 的检查，
/// 不与 Hosting 内置入口混用；无 store 时返回 Degraded。
/// </summary>
public sealed class OutboxHealthPackageTests
{
    [Test]
    public async Task AddPalOutboxHealthCheck_RegistersOutboxCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalOutboxHealthCheck();
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        await Assert.That(options.Registrations.Any(r => r.Name == "outbox")).IsTrue();
    }

    [Test]
    public async Task CheckWithoutStore_ReturnsDegraded()
    {
        var check = new PalOutboxHealthCheck();

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }
}
