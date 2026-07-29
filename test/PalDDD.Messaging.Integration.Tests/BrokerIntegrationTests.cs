using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.Messaging.Kafka;
using PalDDD.Messaging.RabbitMQ;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Testing;
using RabbitMQ.Client;
using System.Text.Json;
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

    // 远程连接字段（环境变量设置时使用，跳过 Testcontainers）
    private string? _remoteKafkaBootstrap;
    private string? _remoteRabbitHost;
    private int _remoteRabbitPort;
    public async ValueTask InitializeAsync()
    {
        // 统一配置：环境变量 > appsettings.test*.json > Testcontainers（默认）
        // CI 环境（无 appsettings.test*.json）→ Testcontainers 自动启动容器
        // 本地开发 → appsettings.test.local.json 配置远程连接

        var kafkaBootstrap = TestEnvironment.KafkaBootstrapServers;
        var rabbitHost = TestEnvironment.RabbitMqHost;

        // 如果配置指向 localhost（默认值/Testcontainers 模式），尝试用 Testcontainers 启动
        if (kafkaBootstrap.Contains("localhost") && rabbitHost.Contains("localhost"))
        {
            if (DockerAvailable || _triedTestcontainers) return;
            _triedTestcontainers = true;
            try
            {
                _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.9.0").Build();
                await _kafka.StartAsync();
                _rabbitMq = new RabbitMqBuilder("rabbitmq:4.1.0-alpine").Build();
                await _rabbitMq.StartAsync();
                _remoteKafkaBootstrap = _kafka.GetBootstrapAddress();
                _remoteRabbitHost = _rabbitMq!.Hostname;
                _remoteRabbitPort = _rabbitMq!.GetMappedPublicPort(5672);
                DockerAvailable = true;
                return;
            }
#pragma warning disable CA1031 // Intentionally broad: detect Docker presence
            catch
#pragma warning restore CA1031
            {
                DockerAvailable = false;
                return;
            }
        }

        // 远程配置（非 localhost）→ 直接用配置的连接串
        _remoteKafkaBootstrap = kafkaBootstrap;
        _remoteRabbitHost = rabbitHost;
        _remoteRabbitPort = TestEnvironment.RabbitMqPort;
        DockerAvailable = true;
    }

    private bool _triedTestcontainers;

    public (KafkaBroker, JsonMessageSerializer) CreateKafkaBroker()
        => CreateKafkaBroker(NullPalLogger<KafkaBroker>.Instance);

    public (KafkaBroker, JsonMessageSerializer) CreateKafkaBroker(IPalLogger<KafkaBroker> logger)
    {
        var bootstrap = _remoteKafkaBootstrap ?? _kafka!.GetBootstrapAddress();
        var producerConfig = new Confluent.Kafka.ProducerConfig
        {
            BootstrapServers = bootstrap,
            AllowAutoCreateTopics = true
        };
        var consumerConfig = new Confluent.Kafka.ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = $"paldd-test-{Guid.NewGuid():N}",
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,  // Earliest 确保 consumer join 后能读到已发消息
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
        var host = _remoteRabbitHost ?? _rabbitMq!.Hostname;
        var port = _remoteRabbitHost is not null ? _remoteRabbitPort : _rabbitMq!.GetMappedPublicPort(5672);
        var factory = new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = TestEnvironment.RabbitMqUsername,
            Password = TestEnvironment.RabbitMqPassword,
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
        var tag = Guid.NewGuid().ToString("N")[..8];
        var received = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            if (msg.Name == $"kafka-rt-{tag}") received.TrySetResult(msg);
            return ValueTask.CompletedTask;
        }, cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        await broker.PublishAsync(new TestMessage($"kafka-rt-{tag}"), cancellationToken);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        await Assert.That(got.Name).IsEqualTo($"kafka-rt-{tag}");
    }

    [Test]
    public async Task Kafka_HandlerCancellation_DoesNotLogHandlerFailure(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<KafkaBroker>();
        var kafka = Fixture.CreateKafkaBroker(logger);
        await using var broker = kafka.Item1;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntered = false;

        var sub = await broker.SubscribeAsync<TestMessage>(async (msg, ct) =>
        {
            // 第一条消息作为 warmup 确认 consumer ready
            if (msg.Name == $"kafka-ready-{tag}")
            {
                ready.TrySetResult();
                return;
            }
            // 第二条消息触发 cancel handler 测试
            if (msg.Name == $"kafka-cancel-{tag}" && !handlerEntered)
            {
                handlerEntered = true;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }, cancellationToken);

        // 先发 warmup 消息确认 consumer ready
        await broker.PublishAsync(new TestMessage($"kafka-ready-{tag}"), cancellationToken);
        // 等待 consumer 确认 ready（证明 join group 完成 + 消费链路通）
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);

        // consumer 已 ready，再发测试消息
        await broker.PublishAsync(new TestMessage($"kafka-cancel-{tag}"), cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        await sub.DisposeAsync();

        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task Kafka_MultipleMessages_AllReceived(CancellationToken cancellationToken)
    {
        var (broker, _) = Fixture.CreateKafkaBroker();
        var received = new List<TestMessage>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = $"kafka-multi-{Guid.NewGuid():N}".Substring(0, 20);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            if (!msg.Name.StartsWith(prefix, StringComparison.Ordinal)) return ValueTask.CompletedTask;
            lock (received) received.Add(msg);
            if (received.Count >= 5) done.TrySetResult();
            return ValueTask.CompletedTask;
        }, cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        for (var i = 0; i < 5; i++)
            await broker.PublishAsync(new TestMessage($"{prefix}-{i}"), cancellationToken);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        await Assert.That(received.Count).IsEqualTo(5);
    }

    [Test]
    public async Task RabbitMq_PublishAndSubscribe_RoundTripsMessage(CancellationToken cancellationToken)
    {
        var (broker, _) = await Fixture.CreateRabbitMqBrokerAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var received = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            if (msg.Name == $"rmq-rt-{tag}") received.TrySetResult(msg);
            return ValueTask.CompletedTask;
        }, cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await broker.PublishAsync(new TestMessage($"rmq-rt-{tag}"), cancellationToken);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        await Assert.That(got.Name).IsEqualTo($"rmq-rt-{tag}");
    }

    [Test]
    public async Task RabbitMq_HandlerCancellation_DoesNotLogHandlerFailure(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var rabbit = await Fixture.CreateRabbitMqBrokerAsync(logger);
        await using var broker = rabbit.Item1;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntered = false;

        var sub = await broker.SubscribeAsync<TestMessage>(async (msg, ct) =>
        {
            // warmup 消息确认 consumer ready
            if (msg.Name == $"rmq-ready-{tag}")
            {
                ready.TrySetResult();
                return;
            }
            // 测试消息触发 cancel handler
            if (msg.Name == $"rmq-cancel-{tag}" && !handlerEntered)
            {
                handlerEntered = true;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }, cancellationToken);

        // 先发 warmup 确认 consumer 链路通畅
        await broker.PublishAsync(new TestMessage($"rmq-ready-{tag}"), cancellationToken);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);

        // consumer 已 ready，发测试消息
        await broker.PublishAsync(new TestMessage($"rmq-cancel-{tag}"), cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        await sub.DisposeAsync();

        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task RabbitMq_MultipleMessages_AllReceived(CancellationToken cancellationToken)
    {
        var (broker, _) = await Fixture.CreateRabbitMqBrokerAsync();
        var received = new List<TestMessage>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = $"rmq-multi-{Guid.NewGuid():N}".Substring(0, 20);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            if (!msg.Name.StartsWith(prefix, StringComparison.Ordinal)) return ValueTask.CompletedTask;
            lock (received) received.Add(msg);
            if (received.Count >= 5) done.TrySetResult();
            return ValueTask.CompletedTask;
        }, cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        for (var i = 0; i < 5; i++)
            await broker.PublishAsync(new TestMessage($"{prefix}-{i}"), cancellationToken);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        await Assert.That(received.Count).IsEqualTo(5);
    }
}
