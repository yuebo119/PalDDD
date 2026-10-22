namespace PalDDD.Integration.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.Serialization.Evolution;
using PalDDD.Serialization.Json;
using System.Text.Json.Serialization;

public sealed partial class PalPlatformVerificationHostedServiceTests
{
    [Test]
    public async Task StartAsync_WhenMessageContractManifestIsValid_CompletesStartup(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddPalJsonSerialization(builder =>
        {
            builder.Add(
                HostedVerificationJsonContext.Default.OrderSubmittedV1,
                "order-submitted",
                schemaVersion: 1);
            builder.Add(
                HostedVerificationJsonContext.Default.OrderSubmittedV2,
                "order-submitted",
                schemaVersion: 2);
        });
        services.AddPalMessageContractVerification(builder =>
            builder.Add<OrderSubmittedV1, OrderSubmittedV2>(
                HostedVerificationJsonContext.Default.OrderSubmittedV1,
                HostedVerificationJsonContext.Default.OrderSubmittedV2,
                old => new OrderSubmittedV2(old.OrderId, 0m),
                name: "order-submitted"));
        await using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        await Assert.That(hostedServices).Count().IsEqualTo(1);
        var hostedService = hostedServices[0];

        await Assert.That(async () =>
            await hostedService.StartAsync(cancellationToken)).ThrowsNothing();
    }

    [Test]
    public async Task StartAsync_WhenCatalogNotRegistered_ThrowsActionableMessage()
    {
        // R13 F24：catalog 缺失时原实现在解析 hosted service 阶段抛 DI 通用
        // "Unable to resolve service for type IMessageCatalog"，不点名注册入口；
        // 修复后 hosted service 可构造，StartAsync 抛可行动异常。
        var services = new ServiceCollection();
        services.AddPalMessageContractVerification();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().Single();
        var exception = await Assert.That(async () => await hosted.StartAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("IMessageCatalog");
        await Assert.That(exception!.Message).Contains("AddPalJsonSerialization");
    }

    [Test]
    public async Task StartAsync_WhenMessageContractManifestIsInvalid_FailsStartupWithAggregatedErrors(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddPalJsonSerialization(builder =>
        {
            builder.Add(
                HostedVerificationJsonContext.Default.OrderSubmittedV1,
                "order-submitted",
                schemaVersion: 1);
            builder.Add(
                HostedVerificationJsonContext.Default.OrderSubmittedV2,
                "order-submitted",
                schemaVersion: 2);
        });
        services.AddPalMessageContractVerification();
        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        await Assert.That(hostedServices).Count().IsEqualTo(1);
        var hostedService = hostedServices[0];

        var exception = await Assert.That(() =>
            hostedService.StartAsync(cancellationToken)).Throws<PalPlatformVerificationException>();

        var errors = exception!.Errors.ToList();
        await Assert.That(errors).Count().IsEqualTo(1);
        var error = errors[0];
        await Assert.That(error.Requirement.SourceDescriptor.Name).IsEqualTo("order-submitted");
        var evolutionException = error.Exception as MessageEvolutionException;
        await Assert.That(evolutionException).IsNotNull();
    }

    private sealed record OrderSubmittedV1(Guid OrderId);

    private sealed record OrderSubmittedV2(Guid OrderId, decimal Amount);

    [JsonSerializable(typeof(OrderSubmittedV1))]
    [JsonSerializable(typeof(OrderSubmittedV2))]
    private sealed partial class HostedVerificationJsonContext : JsonSerializerContext;
}
