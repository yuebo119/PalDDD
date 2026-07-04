namespace PalDDD.Integration.Tests;

using PalDDD.Serialization;
using PalDDD.Serialization.Evolution;
using PalDDD.Serialization.Json;
using System.Text.Json.Serialization;

public sealed partial class MessageEvolutionTests
{
    [Test]
    public async Task Upgrade_DeserializesOldVersionAndConvertsToCurrentDescriptor()
    {
        var oldDescriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var currentDescriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var pipeline = new MessageEvolutionBuilder()
            .Add<OrderSubmittedV1, OrderSubmittedV2>(
                oldDescriptor,
                currentDescriptor,
                old => new OrderSubmittedV2(old.OrderId, 0m))
            .Build();

        var payload = serializer.Serialize(new OrderSubmittedV1(Guid.Parse("10a092b0-5b98-4ed6-a123-8d1be49d6c6a")), oldDescriptor);

        var upgraded = pipeline.Upgrade(payload.Span, oldDescriptor, currentDescriptor, serializer) as OrderSubmittedV2;
        await Assert.That(upgraded).IsNotNull();

        await Assert.That(upgraded.OrderId).IsEqualTo(Guid.Parse("10a092b0-5b98-4ed6-a123-8d1be49d6c6a"));
        await Assert.That(upgraded.Amount).IsEqualTo(0m);
    }

    [Test]
    public async Task Upgrade_WhenStepIsMissing_ThrowsStructuredException()
    {
        var oldDescriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var currentDescriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var pipeline = new MessageEvolutionBuilder().Build();
        var payload = serializer.Serialize(new OrderSubmittedV1(Guid.Parse("10a092b0-5b98-4ed6-a123-8d1be49d6c6a")), oldDescriptor);

        var exception = await Assert.That(() =>
            pipeline.Upgrade(payload.Span, oldDescriptor, currentDescriptor, serializer)).Throws<MessageEvolutionException>();

        await Assert.That(exception!.Message).Contains("order-submitted");
        await Assert.That(exception!.Message.ToUpperInvariant()).Contains("MISSING");
    }

    [Test]
    public async Task Upgrade_WhenStepSkipsRequestedTarget_ThrowsStructuredException()
    {
        var v1Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var v2Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var v3Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV3,
            "order-submitted",
            schemaVersion: 3);
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var pipeline = new MessageEvolutionBuilder()
            .Add<OrderSubmittedV1, OrderSubmittedV3>(
                v1Descriptor,
                v3Descriptor,
                old => new OrderSubmittedV3(old.OrderId, 0m, "new"))
            .Build();
        var payload = serializer.Serialize(new OrderSubmittedV1(Guid.Parse("10a092b0-5b98-4ed6-a123-8d1be49d6c6a")), v1Descriptor);

        var exception = await Assert.That(() =>
            pipeline.Upgrade(payload.Span, v1Descriptor, v2Descriptor, serializer)).Throws<MessageEvolutionException>();

        await Assert.That(exception!.Message).Contains("order-submitted");
        await Assert.That(exception!.Message.ToUpperInvariant()).Contains("OVERSHOT");
    }

    [Test]
    public async Task ValidatePath_WhenStepIsMissing_ThrowsStructuredException()
    {
        var oldDescriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var currentDescriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var pipeline = new MessageEvolutionBuilder().Build();

        var exception = await Assert.That(() =>
            pipeline.ValidatePath(oldDescriptor, currentDescriptor)).Throws<MessageEvolutionException>();

        await Assert.That(exception!.Message).Contains("order-submitted");
        await Assert.That(exception!.Message.ToUpperInvariant()).Contains("MISSING");
    }

    [Test]
    public async Task ValidatePath_WhenStepSkipsRequestedTarget_ThrowsStructuredException()
    {
        var v1Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var v2Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var v3Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV3,
            "order-submitted",
            schemaVersion: 3);
        var pipeline = new MessageEvolutionBuilder()
            .Add<OrderSubmittedV1, OrderSubmittedV3>(
                v1Descriptor,
                v3Descriptor,
                old => new OrderSubmittedV3(old.OrderId, 0m, "new"))
            .Build();

        var exception = await Assert.That(() =>
            pipeline.ValidatePath(v1Descriptor, v2Descriptor)).Throws<MessageEvolutionException>();

        await Assert.That(exception!.Message).Contains("order-submitted");
        await Assert.That(exception!.Message.ToUpperInvariant()).Contains("OVERSHOT");
    }

    [Test]
    public async Task ValidatePath_WithCompletePath_DoesNotThrow()
    {
        var v1Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV1,
            "order-submitted",
            schemaVersion: 1);
        var v2Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV2,
            "order-submitted",
            schemaVersion: 2);
        var v3Descriptor = MessageDescriptor.Create(
            EvolutionTestJsonContext.Default.OrderSubmittedV3,
            "order-submitted",
            schemaVersion: 3);
        var pipeline = new MessageEvolutionBuilder()
            .Add<OrderSubmittedV1, OrderSubmittedV2>(
                v1Descriptor,
                v2Descriptor,
                old => new OrderSubmittedV2(old.OrderId, 0m))
            .Add<OrderSubmittedV2, OrderSubmittedV3>(
                v2Descriptor,
                v3Descriptor,
                old => new OrderSubmittedV3(old.OrderId, old.Amount, "new"))
            .Build();

        pipeline.ValidatePath(v1Descriptor, v3Descriptor);
    }

    private sealed record OrderSubmittedV1(Guid OrderId);

    private sealed record OrderSubmittedV2(Guid OrderId, decimal Amount);

    private sealed record OrderSubmittedV3(Guid OrderId, decimal Amount, string Status);

    [JsonSerializable(typeof(OrderSubmittedV1))]
    [JsonSerializable(typeof(OrderSubmittedV2))]
    [JsonSerializable(typeof(OrderSubmittedV3))]
    private sealed partial class EvolutionTestJsonContext : JsonSerializerContext;
}
