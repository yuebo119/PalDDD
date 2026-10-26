using Confluent.Kafka;

namespace PalDDD.Messaging.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🛡️ 2026-10-21 幂等默认：仅在调用方未触碰幂等相关四项时打开。
// 显式设置任一项（含显式关闭）都必须保持原值——框架不改写调用方的投递语义取舍。
// 纯配置断言，不需要 broker 连接。
// ═══════════════════════════════════════════════════════════════

public sealed class KafkaProducerConfigTests
{
    [Test]
    public async Task ApplyIdempotenceDefault_AllUnset_EnablesIdempotence()
    {
        var config = new ProducerConfig();

        PalDDD.Messaging.Kafka.KafkaBroker.ApplyIdempotenceDefault(config);

        await Assert.That(config.EnableIdempotence).IsTrue();
    }

    [Test]
    public async Task ApplyIdempotenceDefault_ExplicitlyDisabled_StaysDisabled()
    {
        var config = new ProducerConfig { EnableIdempotence = false };

        PalDDD.Messaging.Kafka.KafkaBroker.ApplyIdempotenceDefault(config);

        await Assert.That(config.EnableIdempotence).IsFalse();
    }

    [Test]
    public async Task ApplyIdempotenceDefault_AcksCustomized_DoesNotEnable()
    {
        var config = new ProducerConfig { Acks = Acks.Leader };

        PalDDD.Messaging.Kafka.KafkaBroker.ApplyIdempotenceDefault(config);

        await Assert.That(config.EnableIdempotence).IsNull();
        await Assert.That(config.Acks).IsEqualTo(Acks.Leader);
    }

    [Test]
    public async Task ApplyIdempotenceDefault_RetriesOrInFlightCustomized_DoesNotEnable()
    {
        var retries = new ProducerConfig { MessageSendMaxRetries = 3 };
        var inFlight = new ProducerConfig { MaxInFlight = 1 };

        PalDDD.Messaging.Kafka.KafkaBroker.ApplyIdempotenceDefault(retries);
        PalDDD.Messaging.Kafka.KafkaBroker.ApplyIdempotenceDefault(inFlight);

        await Assert.That(retries.EnableIdempotence).IsNull();
        await Assert.That(retries.MessageSendMaxRetries).IsEqualTo(3);
        await Assert.That(inFlight.EnableIdempotence).IsNull();
        await Assert.That(inFlight.MaxInFlight).IsEqualTo(1);
    }
}
