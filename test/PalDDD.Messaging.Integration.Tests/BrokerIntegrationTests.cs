using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.Messaging.Kafka;
using PalDDD.Messaging.RabbitMQ;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using RabbitMQ.Client;
using System.Text.Json.Serialization;
using Testcontainers.Kafka;
using Testcontainers.RabbitMq;

namespace PalDDD.Messaging.Integration.Tests;

public sealed class TestMessage
{
    public string Name { get; set; } = "";

    public TestMessage()
    { }

    [JsonConstructor]
    public TestMessage(string name) => Name = name;
}

[JsonSerializable(typeof(TestMessage))]
public sealed partial class TestJsonContext : JsonSerializerContext;

public sealed class BrokerFixture : IAsyncDisposable
{
    public bool DockerAvailable { get; private set; }
    private KafkaContainer? _kafka;
    private RabbitMqContainer? _rabbitMq;
    private readonly CatalogAndSerializer _catalogAndSerializer = CreateCatalogAndSerializer();
    private bool _initialized;

    public async ValueTask InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.9.0").Build();
            await _kafka.StartAsync();
            _rabbitMq = new RabbitMqBuilder("rabbitmq:4.1.0-alpine").Build();
            await _rabbitMq.StartAsync();
            DockerAvailable = true;
        }
#pragma warning disable CA1031 // Intentionally broad: detect Docker presence, propagate specific failures through test.
        catch
#pragma warning restore CA1031
        {
            DockerAvailable = false;
        }
    }

    public (KafkaBroker, JsonMessageSerializer) CreateKafkaBroker()
        => CreateKafkaBroker(NullPalLogger<KafkaBroker>.Instance);

    public (KafkaBroker, JsonMessageSerializer) CreateKafkaBroker(IPalLogger<KafkaBroker> logger)
    {
        var producerConfig = new Confluent.Kafka.ProducerConfig
        {
            BootstrapServers = _kafka!.GetBootstrapAddress(),
            AllowAutoCreateTopics = true
        };
        var consumerConfig = new Confluent.Kafka.ConsumerConfig
        {
            BootstrapServers = _kafka!.GetBootstrapAddress(),
            GroupId = $"paldd-test-{Guid.NewGuid():N}",
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = true
        };
        var broker = new KafkaBroker(producerConfig, consumerConfig,
            logger,
            _catalogAndSerializer.Serializer,
            _catalogAndSerializer.Catalog);
        return (broker, _catalogAndSerializer.Serializer);
    }

    public async ValueTask<(RabbitMqBroker, JsonMessageSerializer)> CreateRabbitMqBrokerAsync()
        => await CreateRabbitMqBrokerAsync(NullPalLogger<RabbitMqBroker>.Instance);

    public async ValueTask<(RabbitMqBroker, JsonMessageSerializer)> CreateRabbitMqBrokerAsync(IPalLogger<RabbitMqBroker> logger)
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbitMq!.Hostname,
            Port = _rabbitMq!.GetMappedPublicPort(5672),
            UserName = "guest",
            Password = "guest",
            AutomaticRecoveryEnabled = false
        };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        var broker = new RabbitMqBroker(connection, channel,
            logger,
            _catalogAndSerializer.Serializer,
            _catalogAndSerializer.Catalog);
        return (broker, _catalogAndSerializer.Serializer);
    }

    public async ValueTask DisposeAsync()
    {
        if (_kafka is not null) await _kafka.DisposeAsync();
        if (_rabbitMq is not null) await _rabbitMq.DisposeAsync();
    }

    private static CatalogAndSerializer CreateCatalogAndSerializer()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(MessageDescriptor.Create(TestJsonContext.Default.TestMessage, "test-message"));
        var catalog = builder.Build();
        var serializer = new JsonMessageSerializer(catalog);
        return new(catalog, serializer);
    }

    private sealed record CatalogAndSerializer(IMessageCatalog Catalog, JsonMessageSerializer Serializer);
}

internal sealed class CapturingLogger<T> : IPalLogger<T>
{
    public int ErrorCount;
    public int WarningCount;

    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message) => Interlocked.Increment(ref WarningCount);
    public void Error(Exception ex, string message) => Interlocked.Increment(ref ErrorCount);
    public bool IsEnabled(LogLevel level) => true;
}

public sealed class BrokerIntegrationTests
{
    [ClassDataSource<BrokerFixture>(Shared = SharedType.PerTestSession)]
    public required BrokerFixture Fixture { get; init; }

    [Before(Test)]
    public async Task Setup()
    {
        await Fixture.InitializeAsync();
        if (!Fixture.DockerAvailable)
            Skip.Test("Docker is not available — broker integration tests are skipped.");
    }

    [Test]
    public async Task Kafka_PublishAndSubscribe_RoundTripsMessage(CancellationToken cancellationToken)
    {
        var (broker, _) = Fixture.CreateKafkaBroker();
        var received = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            received.TrySetResult(msg);
            return ValueTask.CompletedTask;
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage("kafka-roundtrip-1"), cancellationToken);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(got.Name).IsEqualTo("kafka-roundtrip-1");
    }

    [Test]
    public async Task Kafka_HandlerCancellation_DoesNotLogHandlerFailure(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<KafkaBroker>();
        var kafka = Fixture.CreateKafkaBroker(logger);
        await using var broker = kafka.Item1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var sub = await broker.SubscribeAsync<TestMessage>(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage("kafka-cancel-handler"), cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await sub.DisposeAsync();

        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task Kafka_MultipleMessages_AllReceived(CancellationToken cancellationToken)
    {
        var (broker, _) = Fixture.CreateKafkaBroker();
        var received = new List<TestMessage>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            lock (received) received.Add(msg);
            if (received.Count >= 5) done.TrySetResult();
            return ValueTask.CompletedTask;
        }, cancellationToken);

        for (var i = 0; i < 5; i++)
            await broker.PublishAsync(new TestMessage($"kafka-{i}"), cancellationToken);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(received.Count).IsEqualTo(5);
    }

    [Test]
    public async Task RabbitMq_PublishAndSubscribe_RoundTripsMessage(CancellationToken cancellationToken)
    {
        var (broker, _) = await Fixture.CreateRabbitMqBrokerAsync();
        var received = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            received.TrySetResult(msg);
            return ValueTask.CompletedTask;
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage("rmq-roundtrip-1"), cancellationToken);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(got.Name).IsEqualTo("rmq-roundtrip-1");
    }

    [Test]
    public async Task RabbitMq_HandlerCancellation_DoesNotLogHandlerFailure(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var rabbit = await Fixture.CreateRabbitMqBrokerAsync(logger);
        await using var broker = rabbit.Item1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var sub = await broker.SubscribeAsync<TestMessage>(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage("rmq-cancel-handler"), cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await sub.DisposeAsync();

        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task RabbitMq_MultipleMessages_AllReceived(CancellationToken cancellationToken)
    {
        var (broker, _) = await Fixture.CreateRabbitMqBrokerAsync();
        var received = new List<TestMessage>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            lock (received) received.Add(msg);
            if (received.Count >= 5) done.TrySetResult();
            return ValueTask.CompletedTask;
        }, cancellationToken);

        for (var i = 0; i < 5; i++)
            await broker.PublishAsync(new TestMessage($"rmq-{i}"), cancellationToken);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(received.Count).IsEqualTo(5);
    }
}
