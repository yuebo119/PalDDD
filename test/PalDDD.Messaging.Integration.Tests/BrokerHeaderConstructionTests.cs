using System.Text;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ B-5（EPSL 地毯审计 2026-10-11）：默认发布路径的头构造按需化
// 旧码无条件 new Headers()/new Dictionary()——Empty 上下文（PublishAsync 恒传）下
// 空对象随每条消息分配。锁定：空入参 → null；非空入参 → 键值齐全（行为不变）。
// 红证明为结构性红：旧码 CreateHeaders 为 private，本测试不可达（改 internal 后可达）。
// 本测试不需要 broker 连接（纯头构造/解码逻辑），随本工程运行不随 broker 门跳过。
// 消费侧对位（FromHeaders(null) 零分配快路径）由 PalDDD.Messaging.Tests 锁定
//（架构边界：领域测试项目不引用 Infra 实现工程，broker 单测归属本工程）。
// ═══════════════════════════════════════════════════════════════

public sealed class BrokerHeaderConstructionTests
{
    [Test]
    public async Task Kafka_CreateHeaders_EmptyContext_ReturnsNull()
    {
        var headers = PalDDD.Messaging.Kafka.KafkaBroker.CreateHeaders(MessagePublishContext.Empty);

        await Assert.That(headers).IsNull();
    }

    [Test]
    public async Task Kafka_CreateHeaders_WithTraceParent_ProducesSingleUtf8Header()
    {
        var context = new MessagePublishContext(null, null, "00-abc-def-01", null);

        var headers = PalDDD.Messaging.Kafka.KafkaBroker.CreateHeaders(context);

        await Assert.That(headers).IsNotNull();
        await Assert.That(headers!.Count).IsEqualTo(1);
        await Assert.That(headers[0].Key).IsEqualTo(MessageConsumeContext.HeaderNames.TraceParent);
        // 值为 UTF-8 字节——消费端 FromKafkaHeaders/FromHeaders 的解码形态
        await Assert.That(Encoding.UTF8.GetString(headers[0].GetValueBytes())).IsEqualTo("00-abc-def-01");
    }

    [Test]
    public async Task Rabbit_CreateHeaders_EmptyContext_ReturnsNull()
    {
        var headers = PalDDD.Messaging.RabbitMQ.RabbitMqBroker.CreateHeaders(MessagePublishContext.Empty);

        await Assert.That(headers).IsNull();
    }

    [Test]
    public async Task Rabbit_CreateHeaders_WithCausationId_ProducesSingleUtf8Header()
    {
        var causationId = PalUlid.New();
        var context = new MessagePublishContext(null, causationId, null, null);

        var headers = PalDDD.Messaging.RabbitMQ.RabbitMqBroker.CreateHeaders(context);

        await Assert.That(headers).IsNotNull();
        await Assert.That(headers!.Count).IsEqualTo(1);
        await Assert.That(headers.ContainsKey(MessageConsumeContext.HeaderNames.CausationId)).IsTrue();
        var value = headers[MessageConsumeContext.HeaderNames.CausationId];
        await Assert.That(value).IsTypeOf<byte[]>();
        await Assert.That(Encoding.UTF8.GetString((byte[])value!)).IsEqualTo(causationId.ToString());
    }

    /// <summary>
    /// ADR-039 / 审计 A3：自定义业务头必须写入两栈；框架保留键名冲突时框架头优先
    /// （业务头静默跳过，不破坏追踪链）。
    /// </summary>
    [Test]
    public async Task ExtraHeaders_WrittenByBothBrokers_FrameworkKeysTakePrecedence()
    {
        var extra = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["x-tenant"] = "tenant-42",
            ["x-version"] = "v3",
            // 保留键名：必须被跳过（框架头优先）
            [MessageConsumeContext.HeaderNames.TraceParent] = "00-attacker",
            ["x-blank"] = null
        };
        var context = new MessagePublishContext(null, null, "00-abc-def-01", null)
        {
            ExtraHeaders = extra
        };

        var kafka = PalDDD.Messaging.Kafka.KafkaBroker.CreateHeaders(context);
        await Assert.That(kafka).IsNotEmpty();
        var kafkaMap = kafka!.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()));
        await Assert.That(kafkaMap["x-tenant"]).IsEqualTo("tenant-42");
        await Assert.That(kafkaMap["x-version"]).IsEqualTo("v3");
        // 框架头未被业务值覆盖
        await Assert.That(kafkaMap[MessageConsumeContext.HeaderNames.TraceParent]).IsEqualTo("00-abc-def-01");
        // null 值的业务头不写入
        await Assert.That(kafkaMap.ContainsKey("x-blank")).IsFalse();

        var rabbit = PalDDD.Messaging.RabbitMQ.RabbitMqBroker.CreateHeaders(context);
        await Assert.That(rabbit).IsNotEmpty();
        await Assert.That(Encoding.UTF8.GetString((byte[])rabbit!["x-tenant"]!)).IsEqualTo("tenant-42");
        await Assert.That(Encoding.UTF8.GetString((byte[])rabbit["x-version"]!)).IsEqualTo("v3");
        await Assert.That(rabbit.ContainsKey(MessageConsumeContext.HeaderNames.TraceParent)).IsTrue();
        await Assert.That(rabbit.ContainsKey("x-blank")).IsFalse();
    }

    /// <summary>ADR-039：ExtraHeaders 为 null 时默认路径零分配（不构造字典）。</summary>
    [Test]
    public async Task ExtraHeaders_Null_KeepsEmptyContextZeroAllocationPath()
    {
        await Assert.That(PalDDD.Messaging.Kafka.KafkaBroker.CreateHeaders(MessagePublishContext.Empty)).IsNull();
        await Assert.That(PalDDD.Messaging.RabbitMQ.RabbitMqBroker.CreateHeaders(MessagePublishContext.Empty)).IsNull();
    }
}
