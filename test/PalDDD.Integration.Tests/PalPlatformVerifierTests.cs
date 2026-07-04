namespace PalDDD.Integration.Tests;

using PalDDD.Serialization;
using PalDDD.Serialization.Evolution;
using System.Text.Json.Serialization;

public sealed partial class PalPlatformVerifierTests
{
    [Test]
    public async Task ValidateMessageEvolutionPaths_WithCompletePaths_DoesNotThrow()
    {
        var orderV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var orderV2Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var invoiceV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.InvoiceIssuedV1,
            "invoice-issued",
            schemaVersion: 1);
        var invoiceV2Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.InvoiceIssuedV2,
            "invoice-issued",
            schemaVersion: 2);
        var pipeline = new MessageEvolutionBuilder()
            .Add<OrderSubmittedV1, OrderSubmittedV2>(
                orderV1Descriptor,
                orderV2Descriptor,
                old => new OrderSubmittedV2(old.OrderId, 0m))
            .Add<InvoiceIssuedV1, InvoiceIssuedV2>(
                invoiceV1Descriptor,
                invoiceV2Descriptor,
                old => new InvoiceIssuedV2(old.InvoiceId, "USD"))
            .Build();
        var requirements = new[]
        {
            new MessageEvolutionPathRequirement(orderV1Descriptor, orderV2Descriptor),
            new MessageEvolutionPathRequirement(invoiceV1Descriptor, invoiceV2Descriptor),
        };

        new PalPlatformVerifier().ValidateMessageEvolutionPaths(pipeline, requirements);
    }

    [Test]
    public async Task ValidateMessageEvolutionPaths_WhenMultiplePathsFail_ThrowsAggregatedException()
    {
        var orderV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var orderV2Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var invoiceV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.InvoiceIssuedV1,
            "invoice-issued",
            schemaVersion: 1);
        var invoiceV2Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.InvoiceIssuedV2,
            "invoice-issued",
            schemaVersion: 2);
        var pipeline = new MessageEvolutionBuilder().Build();
        var requirements = new[]
        {
            new MessageEvolutionPathRequirement(orderV1Descriptor, orderV2Descriptor),
            new MessageEvolutionPathRequirement(invoiceV1Descriptor, invoiceV2Descriptor),
        };

        var exception = await Assert.That(() =>
            new PalPlatformVerifier().ValidateMessageEvolutionPaths(pipeline, requirements)).Throws<PalPlatformVerificationException>();

        await Assert.That(exception!.Errors.Count).IsEqualTo(2);
        await Assert.That(exception!.Errors[0].Requirement.SourceDescriptor.Name).IsEqualTo("order-submitted");
        await Assert.That(exception!.Errors[1].Requirement.SourceDescriptor.Name).IsEqualTo("invoice-issued");
        foreach (var error in exception!.Errors)
        {
            var evolutionException = error.Exception as MessageEvolutionException;
            await Assert.That(evolutionException).IsNotNull();
        }
    }

    [Test]
    public async Task CreateEvolutionManifest_FromCatalog_RequiresAdjacentVersionsForEveryVersionedMessage()
    {
        var orderV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var orderV2Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var orderV3Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV3,
            "order-submitted",
            schemaVersion: 3);
        var invoiceV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.InvoiceIssuedV1,
            "invoice-issued",
            schemaVersion: 1);
        var catalog = new MessageCatalogBuilder()
            .Add(orderV1Descriptor)
            .Add(orderV2Descriptor)
            .Add(orderV3Descriptor)
            .Add(invoiceV1Descriptor)
            .Build();

        var manifest = MessageContractManifest.Create(catalog);

        var requirements = manifest.EvolutionRequirements.ToList();
        await Assert.That(requirements).Count().IsEqualTo(2);
        await Assert.That(requirements[0].SourceDescriptor).IsSameReferenceAs(orderV1Descriptor);
        await Assert.That(requirements[0].TargetDescriptor).IsSameReferenceAs(orderV2Descriptor);
        await Assert.That(requirements[1].SourceDescriptor).IsSameReferenceAs(orderV2Descriptor);
        await Assert.That(requirements[1].TargetDescriptor).IsSameReferenceAs(orderV3Descriptor);
    }

    [Test]
    public async Task ValidateMessageContractManifest_WhenCatalogEvolutionIsIncomplete_ThrowsAggregatedException()
    {
        var orderV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var orderV2Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var invoiceV1Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.InvoiceIssuedV1,
            "invoice-issued",
            schemaVersion: 1);
        var invoiceV2Descriptor = MessageDescriptor.Create(
            PalPlatformVerifierJsonContext.Default.InvoiceIssuedV2,
            "invoice-issued",
            schemaVersion: 2);
        var catalog = new MessageCatalogBuilder()
            .Add(orderV1Descriptor)
            .Add(orderV2Descriptor)
            .Add(invoiceV1Descriptor)
            .Add(invoiceV2Descriptor)
            .Build();
        var manifest = MessageContractManifest.Create(catalog);
        var pipeline = new MessageEvolutionBuilder()
            .Add<OrderSubmittedV1, OrderSubmittedV2>(
                orderV1Descriptor,
                orderV2Descriptor,
                old => new OrderSubmittedV2(old.OrderId, 0m))
            .Build();

        var exception = await Assert.That(() =>
            new PalPlatformVerifier().ValidateMessageContractManifest(pipeline, manifest)).Throws<PalPlatformVerificationException>();

        var errors = exception!.Errors.ToList();
        await Assert.That(errors).Count().IsEqualTo(1);
        var error = errors[0];
        await Assert.That(error.Requirement.SourceDescriptor.Name).IsEqualTo("invoice-issued");
        var evolutionException = error.Exception as MessageEvolutionException;
        await Assert.That(evolutionException).IsNotNull();
    }

    private sealed record OrderSubmittedV1(Guid OrderId);

    private sealed record OrderSubmittedV2(Guid OrderId, decimal Amount);

    private sealed record OrderSubmittedV3(Guid OrderId, decimal Amount, string Status);

    private sealed record InvoiceIssuedV1(Guid InvoiceId);

    private sealed record InvoiceIssuedV2(Guid InvoiceId, string Currency);

    [JsonSerializable(typeof(OrderSubmittedV1))]
    [JsonSerializable(typeof(OrderSubmittedV2))]
    [JsonSerializable(typeof(OrderSubmittedV3))]
    [JsonSerializable(typeof(InvoiceIssuedV1))]
    [JsonSerializable(typeof(InvoiceIssuedV2))]
    private sealed partial class PalPlatformVerifierJsonContext : JsonSerializerContext;
}
