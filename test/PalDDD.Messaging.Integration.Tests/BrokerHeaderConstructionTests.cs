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
}
