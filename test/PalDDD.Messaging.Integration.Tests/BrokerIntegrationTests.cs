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

    /// <summary>
    /// 三十轮重设计：Kafka broker 预检结果——AdminClient GetMetadata 探测失败时为 false，
    /// Kafka 测试显式 Skip（环境不可达 ≠ 代码失败，CI/本地排查不再产生 120s 假失败）。
    /// 注意：预检通过不保证消费链路健康（断连型故障 metadata 可正常而 join 失败）——
    /// 那类"半坏"由 CapturingLogger 诊断输出兜底。
    /// </summary>
    public bool KafkaAvailable { get; private set; } = true;
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

        // 三十轮重设计：远程 Kafka 预检——AdminClient 元数据探测（8s 超时）。
        // 服务器断连型故障（TCP 通但建连 1ms 被 RST）时 GetMetadata 也会失败，
        // 此时标记不可用让 Kafka 测试显式 Skip 而非 120s 假失败。
        try
        {
            using var admin = new Confluent.Kafka.AdminClientBuilder(
                new Confluent.Kafka.AdminClientConfig { BootstrapServers = _remoteKafkaBootstrap }).Build();
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(8));
            KafkaAvailable = metadata.Brokers.Count > 0;
        }
#pragma warning disable CA1031 // Intentionally broad: 环境探测任意失败均视为不可用
        catch
#pragma warning restore CA1031
        {
            KafkaAvailable = false;
        }
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
            AllowAutoCreateTopics = true,
            // 二十八轮修复：broker 不可达时 ProduceAsync 默认 5 分钟才抛
            // ProduceException（MessageTimeoutMs 默认 300000）——3 条测试各白等
            // 5 分钟，套件无谓耗时 15 分钟。5s 快速失败让环境故障立即可见。
            MessageTimeoutMs = 5000
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

    // 三十轮重设计：诊断黑盒修复——原实现只计数不记文本，Kafka 消费循环失败时
    // 测试只能报裸 TimeoutException，无法区分"broker 断连/join 卡死/业务异常"。
    // 环形保留最近 5 条错误全文，失败时随断言输出。
    private readonly Queue<string> _recentErrors = new();
    private readonly Lock _lock = new();

    public string RecentErrorsSummary
    {
        get
        {
            lock (_lock) return _recentErrors.Count == 0 ? "（无）" : string.Join(" | ", _recentErrors);
        }
    }

    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message) => Interlocked.Increment(ref WarningCount);
    public void Error(Exception ex, string message)
    {
        Interlocked.Increment(ref ErrorCount);
        lock (_lock)
        {
            _recentErrors.Enqueue($"{ex.GetType().Name}: {ex.Message}");
            while (_recentErrors.Count > 5) _recentErrors.Dequeue();
        }
    }
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

    // ── 三十轮重设计：Kafka 等待基建 ──
    // 原设计四个缺陷（诊断实战暴露）：① NullLogger 吞掉消费循环全部错误，120s 裸超时
    // 无从排查；② broker 完全不可达时报"测试失败"而非"环境不可用"；③ warmup 单发，
    // consumer join 慢于发送时消息白发；④ TimeoutException 无上下文。
    // 新设计：① CapturingLogger 记最近错误；② 预检 Skip（Fixture.KafkaAvailable）；
    // ③ WaitForSignalAsync 周期重发 warmup（幂等触发器：join 后任一条即就绪）；
    // ④ 超时异常携带消费侧错误摘要。

    /// <summary>Kafka broker 预检守卫——预检失败（broker 完全不可达）时显式 Skip。</summary>
    private void SkipIfKafkaUnavailable()
    {
        if (!Fixture.KafkaAvailable)
            Skip.Test("Kafka broker 预检失败（GetMetadata 不可达）——环境问题，非代码失败。检查服务器 Kafka 服务/网络后重试。");
    }

    /// <summary>
    /// 等待信号（TCS Task）就绪，未就绪时每 15s 重发 warmup（幂等：consumer join 完成
    /// 后收到的任意一条 warmup 都会触发信号），总时限内未就绪抛带诊断的 TimeoutException。
    /// </summary>
    private static async Task WaitForSignalAsync(
        Task signal,
        Func<CancellationToken, ValueTask> resendWarmup,
        CapturingLogger<KafkaBroker> logger,
        string signalName,
        TimeSpan total,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(total);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var slice = TimeSpan.FromSeconds(15);
            if (slice > remaining) slice = remaining;

            var delay = Task.Delay(slice, ct);
            var completed = await Task.WhenAny(signal, delay).ConfigureAwait(false);
            if (completed == signal)
                return; // 信号已就绪
            // 15s 未就绪 → 重发 warmup（join 前的消息白发不浪费——Earliest 会追，且任一条即触发）
            await resendWarmup(ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Kafka 等待 {signalName} 超时（{total.TotalSeconds:0}s）。消费侧最近错误：{logger.RecentErrorsSummary}。" +
            "若为空则 consumer 可能从未完成 join（服务器断连型故障的特征：TCP 通但建连后被 RST）。");
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task Kafka_PublishAndSubscribe_RoundTripsMessage(CancellationToken cancellationToken)
    {
        SkipIfKafkaUnavailable();
        var logger = new CapturingLogger<KafkaBroker>();
        var created = Fixture.CreateKafkaBroker(logger);
        await using var broker = created.Item1;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var received = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            consumerReady.TrySetResult(); // handler 首次回调 = consumer join group + 消费链路通
            if (msg.Name == $"kafka-rt-{tag}") received.TrySetResult(msg);
            return ValueTask.CompletedTask;
        }, cancellationToken);

        // warmup + 周期重发（三十轮重设计：join 延迟/断连重试下单发会白发）
        await broker.PublishAsync(new TestMessage($"kafka-ready-{tag}"), cancellationToken);
        await WaitForSignalAsync(consumerReady.Task,
            ct => broker.PublishAsync(new TestMessage($"kafka-ready-{tag}"), ct),
            logger, "consumer join（warmup 回调）", TimeSpan.FromSeconds(120), cancellationToken);

        // consumer ready 后再发测试消息
        await broker.PublishAsync(new TestMessage($"kafka-rt-{tag}"), cancellationToken);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(got.Name).IsEqualTo($"kafka-rt-{tag}");
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task Kafka_HandlerCancellation_DoesNotLogHandlerFailure(CancellationToken cancellationToken)
    {
        SkipIfKafkaUnavailable();
        var logger = new CapturingLogger<KafkaBroker>();
        var kafka = Fixture.CreateKafkaBroker(logger);
        await using var broker = kafka.Item1;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntered = false;

        var sub = await broker.SubscribeAsync<TestMessage>(async (msg, ct) =>
        {
            // 第一条消息：consumer 已 join group，确认消费链路通
            if (msg.Name == $"kafka-ready-{tag}")
            {
                ready.TrySetResult();
                return;
            }
            // 第二条消息：触发 cancel handler 测试
            if (msg.Name == $"kafka-cancel-{tag}" && !handlerEntered)
            {
                handlerEntered = true;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }, cancellationToken);

        // warmup + 周期重发（三十轮重设计）
        await broker.PublishAsync(new TestMessage($"kafka-ready-{tag}"), cancellationToken);
        await WaitForSignalAsync(ready.Task,
            ct => broker.PublishAsync(new TestMessage($"kafka-ready-{tag}"), ct),
            logger, "consumer join（warmup 回调）", TimeSpan.FromSeconds(120), cancellationToken);

        // consumer ready 后发 cancel 测试消息
        await broker.PublishAsync(new TestMessage($"kafka-cancel-{tag}"), cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await sub.DisposeAsync();

        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task Kafka_MultipleMessages_AllReceived(CancellationToken cancellationToken)
    {
        SkipIfKafkaUnavailable();
        var logger = new CapturingLogger<KafkaBroker>();
        var created = Fixture.CreateKafkaBroker(logger);
        await using var broker = created.Item1;
        var received = new List<TestMessage>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = $"kafka-multi-{Guid.NewGuid():N}".Substring(0, 20);
        var consumerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeAsync<TestMessage>((msg, ct) =>
        {
            consumerReady.TrySetResult(); // handler 首次回调 = consumer join group + 消费链路通
            if (!msg.Name.StartsWith(prefix, StringComparison.Ordinal)) return ValueTask.CompletedTask;
            lock (received) received.Add(msg);
            if (received.Count >= 5) done.TrySetResult();
            return ValueTask.CompletedTask;
        }, cancellationToken);

        // warmup + 周期重发（三十轮重设计）
        await broker.PublishAsync(new TestMessage($"kafka-ready-{prefix}"), cancellationToken);
        await WaitForSignalAsync(consumerReady.Task,
            ct => broker.PublishAsync(new TestMessage($"kafka-ready-{prefix}"), ct),
            logger, "consumer join（warmup 回调）", TimeSpan.FromSeconds(120), cancellationToken);

        for (var i = 0; i < 5; i++)
            await broker.PublishAsync(new TestMessage($"{prefix}-{i}"), cancellationToken);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(received.Count).IsEqualTo(5);
    }

    [Test]
    public async Task RabbitMq_PublishAndSubscribe_RoundTripsMessage(CancellationToken cancellationToken)
    {
        var created = await Fixture.CreateRabbitMqBrokerAsync();
        await using var broker = created.Item1;
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
        var created = await Fixture.CreateRabbitMqBrokerAsync();
        await using var broker = created.Item1;
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
