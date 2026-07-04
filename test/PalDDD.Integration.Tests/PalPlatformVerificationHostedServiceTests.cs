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

        await hostedService.StartAsync(cancellationToken);
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
