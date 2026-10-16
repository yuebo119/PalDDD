using System.Text;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Messaging.Kafka;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ EPSL 地毯审计 2026-10-16 A 批 #1：Kafka 发布路径单次全载荷拷贝
// 旧码发布路径两次全载荷拷贝——A13（S1 轮）新增的 TLS 直写后 ToArray（KafkaBroker.cs:133）
// 与遗留的 value.ToArray()（:149）相抵，A13 对该路径净效果为零，且 :142-148 注释
// 「本处 ToArray 现为唯一一次托管拷贝」与同方法两个 ToArray 的事实不符。
// 修复 = Value 直传 byte[]（一行）+ 注释勘误。
// 红证明：本文件经内部构造 seam（IProducer 注入）可达发布路径；分配断言在旧码
// （:149 双拷贝形态）下超预算判红，新码绿。行为断言锁定 Key/Value/Headers 契约。
// 归属：broker 单测归集成侧工程（架构分层边界测试约束，B-5 先例同款说明）。
// ═══════════════════════════════════════════════════════════════

[JsonSerializable(typeof(KafkaPublishProbeMessage))]
internal sealed partial class KafkaPublishProbeJsonContext : JsonSerializerContext;

internal sealed record KafkaPublishProbeMessage(string Payload);

public sealed class KafkaPublishAllocationTests
{
    private static (KafkaBroker Broker, FakeProducer Producer, MessageDescriptor Descriptor) CreateBroker()
    {
        var builder = new MessageCatalogBuilder();
        var descriptor = builder.Add(
            KafkaPublishProbeJsonContext.Default.KafkaPublishProbeMessage,
            name: "kafka.publish.probe.v1");
        var catalog = builder.Build();
        var producer = new FakeProducer();
        var broker = new KafkaBroker(
            producer,
            new ConsumerConfig(),
            NullPalLogger<KafkaBroker>.Instance,
            new JsonMessageSerializer(catalog),
            catalog);
        return (broker, producer, descriptor);
    }

    [Test]
    public async Task PublishAsync_PublishesMessage_WithKeyValueAndNullHeaders()
    {
        var (broker, producer, descriptor) = CreateBroker();
        var message = new KafkaPublishProbeMessage("hello-kafka");
        var messageId = PalUlid.New();

        await broker.PublishAsync(message, descriptor, messageId, MessagePublishContext.Empty);

        await Assert.That(producer.ProduceCount).IsEqualTo(1);
        // Key = 消息 ID 字符串（可追踪性契约）；Value = JSON 载荷字节。
        // 行为断言直取属性（Captured 为 null 时 NRE 即红，不用 IsNotNull 弱断言——
        // AssertionStrengthGateTests 棘轮口径）
        await Assert.That(producer.Captured!.Key).IsEqualTo(messageId.ToString());
        await Assert.That(Encoding.UTF8.GetString(producer.Captured!.Value)).Contains("hello-kafka");
        // 空追踪上下文 → 无头（B-5 按需构造契约，非空字典）
        await Assert.That(producer.Captured!.Headers).IsNull();
    }

    [Test]
    public async Task PublishAsync_SinglePayloadCopy_NoRedundantToArray()
    {
        var (broker, producer, descriptor) = CreateBroker();
        // 1KB 载荷：让全载荷拷贝在分配计数里显著（旧码双拷 ≈ 2.1KB/迭代）
        var message = new KafkaPublishProbeMessage(new string('x', 1024));
        var messageId = PalUlid.New();

        // 预热：JIT 成型 + TLS 缓冲成型（首次入口分配 256B 起步槽）+ catalog 路径稳定
        for (var i = 0; i < 100; i++)
            await broker.PublishAsync(message, descriptor, messageId, MessagePublishContext.Empty);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 1_000;
        for (var i = 0; i < iterations; i++)
            await broker.PublishAsync(message, descriptor, messageId, MessagePublishContext.Empty);

        var alloc = GC.GetAllocatedBytesForCurrentThread() - baseline;
        await Assert.That(producer.ProduceCount).IsGreaterThan(iterations);

        // 预算构成（新码实测标定，2026-10-16）：JSON 输出一次 ToArray ~1.06KB + key 字符串
        // ~64B + Message 对象 ~64B + Task ~80B + Debug 构建 async 状态机类 ~150B ≈ 1.42KB。
        // 旧码（:149 value.ToArray 第二次全载荷拷贝）≈ 2.48KB/迭代，在本预算下红——
        // 锁定的回归敏感点即发布路径冗余全载荷拷贝的重引入。
        await Assert.That(alloc).IsLessThanOrEqualTo(1500L * iterations);
    }

    // Confluent IProducer<string, byte[]> 假件——仅发布路径单测需要的 ProduceAsync
    // 有真实行为（捕获消息 + 完成的任务）；其余成员为接口完备性占位（发布路径不触达）。
    private sealed class FakeProducer : IProducer<string, byte[]>
    {
        public Message<string, byte[]>? Captured { get; private set; }

        public int ProduceCount { get; private set; }

        public Task<DeliveryResult<string, byte[]>> ProduceAsync(
            string topic, Message<string, byte[]> message, CancellationToken cancellationToken)
        {
            Captured = message;
            ProduceCount++;
            // 发布路径消费方丢弃结果（await 后不取 DeliveryReport），返回已完成任务
            return Task.FromResult<DeliveryResult<string, byte[]>>(null!);
        }

        public Task<DeliveryResult<string, byte[]>> ProduceAsync(
            TopicPartition topicPartition, Message<string, byte[]> message, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void Produce(string topic, Message<string, byte[]> message, Action<DeliveryReport<string, byte[]>> deliveryHandler)
            => throw new NotSupportedException();

        public void Produce(TopicPartition topicPartition, Message<string, byte[]> message, Action<DeliveryReport<string, byte[]>> deliveryHandler)
            => throw new NotSupportedException();

        public int Poll(TimeSpan timeout) => throw new NotSupportedException();

        public int Flush(TimeSpan timeout) => 0;

        public void Flush(CancellationToken cancellationToken)
        {
        }

        public void InitTransactions(TimeSpan timeout) => throw new NotSupportedException();

        public void BeginTransaction() => throw new NotSupportedException();

        public void CommitTransaction(TimeSpan timeout) => throw new NotSupportedException();

        public void CommitTransaction() => throw new NotSupportedException();

        public void AbortTransaction(TimeSpan timeout) => throw new NotSupportedException();

        public void AbortTransaction() => throw new NotSupportedException();

        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
            => throw new NotSupportedException();

        public Handle Handle => null!;

        public string Name => "fake-producer";

        public int AddBrokers(string brokers) => throw new NotSupportedException();

        public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
