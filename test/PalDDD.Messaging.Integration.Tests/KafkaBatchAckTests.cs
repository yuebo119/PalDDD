using Confluent.Kafka;
using PalDDD.Core.Logging;
using PalDDD.Messaging.Kafka;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Testing;

namespace PalDDD.Messaging.Integration.Tests;

// ─────────────────────────────────────────────────────────────
// 2026-11-07 审计 P1-A：Kafka 批量订阅 ack 失效的集成锁定
// ─────────────────────────────────────────────────────────────
//
// 机制：批量订阅的 handler 是 MessageBatchPump.EnqueueAsync——它只把确认器写入组批通道
// （入队前经 DeferredAckRegistry.MarkDeferred 打移交标记），不结算。broker 的
// HandleMessageAsync 在 await handler(...) 返回后进入 finally，原判据只看
// `ack is { IsSettled: false }`，于是抢先补 RejectAsync(requeue:false) 占用
// KafkaMessageAck.TryClaim——批次侧的 AcknowledgeAsync / RejectAsync(requeue:true)
// 全被拦截：失败无法 Seek 重投，EnableAutoOffsetStore=false 下消息丢失。
// 修复：finally 判据并入 `!DeferredAckRegistry.IsDeferred(ack)`（与 RabbitMQ 侧四处同构）。
//
// 三个用例：
//  · UncommittedOffset_DoesNotAdvance —— 批次 handler 阻塞未结算时，broker 不得提前结算
//    （观测 group committed offset：修复前 finally 的 RejectSkip 会 StoreOffset + 显式
//    Commit 推进 offset；修复后不动）。
//  · Requeue_Redelivers —— 批次 handler 显式 RejectAsync(requeue:true) 必须真重投
//    （修复前被 finally 抢占后静默无操作，恒不重投）。
//  · DirectSubscribe_Unaffected —— 直接订阅（无 MarkDeferred）的 finally 兜底行为不变，
//    防止判据加 IsDeferred 误伤逐条路径。

/// <summary>Kafka 批量订阅确认契约（审计 P1-A 锁定）。</summary>
public sealed class KafkaBatchAckTests
{
    [ClassDataSource<BrokerFixture>(Shared = SharedType.PerTestSession)]
    public required BrokerFixture Fixture { get; init; }

    [Before(Test)]
    public async Task Setup()
    {
        await Fixture.InitializeAsync();
        if (!Fixture.DockerAvailable)
            Skip.Test("Docker is not available — broker integration tests are skipped.");
        if (!Fixture.KafkaAvailable)
            Skip.Test("Kafka broker 预检失败（GetMetadata 不可达）——环境问题，非代码失败。");
    }

    /// <summary>
    /// 批次 handler 阻塞且未显式结算时，broker 的 finally 不得抢占结算。
    /// <para>
    /// 观测口径：group committed offset。修复前 finally 补 RejectSkip 会 StoreOffset 并补
    /// 显式 Commit（auto.commit=false 路径，见 <c>ManualCommit_*</c>），committed offset
    /// 前进到消息位点 +1；修复后 finally 跳过，committed offset 保持 -1001（无提交）。
    /// </para>
    /// <para>
    /// 为什么不能用「重启后是否重投」：关停时 MessageBatchPump 的 OCE 分支会对当前批补
    /// RejectAsync(requeue:false)，两条路径都会推进 offset，关停后观测无法区分。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task UncommittedOffset_DoesNotAdvance(CancellationToken cancellationToken)
    {
        var groupId = $"paldd-batch-uncommitted-{Guid.NewGuid():N}";
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-batch-uncommitted");
        var payload = $"batch-uncommitted-{Guid.NewGuid():N}";

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var created = CreateBroker(topic, groupId, logger, enableAutoCommit: false);
        await using var broker = created.Broker;
        await using var sub = await broker.SubscribeBatchAsync<TestMessage>(
            async (batch, ack, ct) =>
            {
                if (batch.Any(static m => m.Message.Name.StartsWith("batch-uncommitted-", StringComparison.Ordinal)))
                {
                    // 批次 handler 收到即阻塞、不结算：此时 EnqueueAsync 已返回，
                    // broker 的 finally 已执行完毕（修复前已 RejectSkip，修复后已跳过）。
                    handlerEntered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return;
                }
                await ack.AcknowledgeAsync(ct);
            },
            maxBatchSize: 1,
            maxDelay: TimeSpan.FromMilliseconds(20),
            cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        // 给 finally 结算与显式 Commit 落地留窗口（消费线程 poll 100ms + Commit RPC）。
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        var committed = await QueryMaxCommittedOffsetAsync(topic, groupId, cancellationToken);
        await Assert.That(committed).IsLessThan(0)
            .Because($"批次未结算时 committed offset 不得前进（实际 {committed}）；"
                + "修复前 finally 的 RejectSkip 会推进 offset，使批次失败无法 Seek 重投");

        release.TrySetResult();
    }

    /// <summary>
    /// 批次 handler 显式 <c>RejectAsync(requeue:true)</c> 必须真重投。
    /// <para>
    /// 修复前 broker finally 已抢先 RejectSkip 占用 TryClaim，批次侧 Reject 静默无操作
    /// （记 Duplicate Warning），消息永不重投，本用例在 90s 内等不到第二次交付。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task Requeue_Redelivers(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-batch-requeue");
        var payload = $"batch-requeue-{Guid.NewGuid():N}";
        var created = CreateBroker(topic, $"paldd-batch-requeue-{Guid.NewGuid():N}", logger);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        await using var broker = created.Broker;
        await using var sub = await broker.SubscribeBatchAsync<TestMessage>(
            async (batch, ack, ct) =>
            {
                if (!batch.Any(static m => m.Message.Name.StartsWith("batch-requeue-", StringComparison.Ordinal)))
                {
                    await ack.AcknowledgeAsync(ct);
                    return;
                }

                if (Interlocked.Increment(ref count) == 1)
                {
                    await ack.RejectAsync(requeue: true, ct);
                    return;
                }

                await ack.AcknowledgeAsync(ct);
                done.TrySetResult();
            },
            maxBatchSize: 1,
            maxDelay: TimeSpan.FromMilliseconds(20),
            cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);

        await Assert.That(Volatile.Read(ref count)).IsGreaterThanOrEqualTo(2);
        // Seek 重投本身记 Warning（broker :652 的设计行为，便于观测重投），故只锁
        // 「无 Duplicate ack Warning」——TryClaim 被抢占时的特征告警。
        await Assert.That(logger.RecentWarningsSummary).DoesNotContain("Duplicate ack")
            .Because($"批量结算被抢占会产生 Duplicate ack Warning：{logger.RecentWarningsSummary}");
    }

    /// <summary>
    /// 直接订阅（未走批量、无 <c>MarkDeferred</c> 标记）的 finally 兜底行为不变。
    /// <para>
    /// handler 成功但未显式结算时必须补 RejectSkip——否则 offset 冻结、重启全量重放。
    /// 本用例锁死判据加 <c>!IsDeferred</c> 后逐条路径不被误跳过（修复前后均绿，回归保护）。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task DirectSubscribe_Unaffected(CancellationToken cancellationToken)
    {
        var groupId = $"paldd-direct-unaffected-{Guid.NewGuid():N}";
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-direct-unaffected");
        var payload = $"direct-unaffected-{Guid.NewGuid():N}";

        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = CreateBroker(topic, groupId, logger, enableAutoCommit: false);
        await using (var broker = created.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
            {
                if (msg.Name == payload)
                {
                    // 成功但不显式结算 → finally 补 RejectSkip 推进 offset。
                    handled.TrySetResult();
                    return;
                }
                await ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            await broker.PublishAsync(new TestMessage(payload), cancellationToken);
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

            // finally 的 RejectSkip 是异步的（await 消费线程排空 + 显式 Commit），
            // 必须等它落地再关停——否则 Dispose 中断 StoreOffset/Commit，offset 未推进，
            // 重启重投（与修复无关的假红）。
            var committedAfterHandle = await WaitForCommittedOffsetAsync(
                topic, groupId, TimeSpan.FromSeconds(10), cancellationToken);
            await Assert.That(committedAfterHandle).IsGreaterThanOrEqualTo(0)
                .Because("直接订阅未结算时 finally 必须补 RejectSkip 推进 offset");
        }

        // 同 GroupId 重启：finally 兜底生效则 offset 已前进，消息不重投。
        var redelivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBroker = CreateBroker(topic, groupId, logger, enableAutoCommit: false);
        await using (var broker = secondBroker.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>((msg, _, ack, ct) =>
            {
                if (msg.Name == payload)
                    redelivered.TrySetResult(msg.Name);
                return ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            var raced = await Task.WhenAny(redelivered.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            await Assert.That(ReferenceEquals(raced, redelivered.Task)).IsFalse()
                .Because("直接订阅的 finally 兜底被误跳过——未结算消息的 offset 未推进，重启后重投");
        }
    }

    private static string NewTopic(string prefix)
        // Kafka topic 名上限 249 字符且只允许 [a-zA-Z0-9._-]；Guid N 形足够唯一。
        => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>
    /// 查询 group 在 topic 所有分区上的最大 committed offset；无提交时返回 -1001
    /// （Confluent 的 <c>Offset.Unset</c> 语义）。
    /// </summary>
    /// <remarks>
    /// 用 <c>Assign</c>（手动分配，不 JoinGroup）查询 broker 端 group offset，避免独立
    /// consumer 加入 group 触发 rebalance 干扰 broker 消费。
    /// </remarks>
    private async Task<long> QueryMaxCommittedOffsetAsync(string topic, string groupId, CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig
            {
                BootstrapServers = Fixture.KafkaBootstrap,
                BrokerAddressFamily = BrokerAddressFamily.V4,
            }).Build();
        var metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        var topicPartitions = metadata.Topics
            .Single(t => t.Topic == topic)
            .Partitions
            .Select(p => new TopicPartition(topic, p.PartitionId))
            .ToList();

        using var consumer = new ConsumerBuilder<Ignore, Ignore>(
            new ConsumerConfig
            {
                BootstrapServers = Fixture.KafkaBootstrap,
                GroupId = groupId,
                BrokerAddressFamily = BrokerAddressFamily.V4,
            }).Build();
        consumer.Assign(topicPartitions);

        var committed = consumer.Committed(topicPartitions, TimeSpan.FromSeconds(10));
        return committed.Count == 0 ? -1001 : committed.Max(static c => c.Offset);
    }

    /// <summary>轮询等待 group committed offset 落盘（<paramref name="timeout"/> 内未推进则返回最后观测值）。</summary>
    private async Task<long> WaitForCommittedOffsetAsync(
        string topic,
        string groupId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var committed = await QueryMaxCommittedOffsetAsync(topic, groupId, cancellationToken);
            if (committed >= 0)
                return committed;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        return await QueryMaxCommittedOffsetAsync(topic, groupId, cancellationToken);
    }

    private (KafkaBroker Broker, JsonMessageSerializer Serializer) CreateBroker(
        string topic,
        string groupId,
        IPalLogger<KafkaBroker> logger,
        bool? enableAutoCommit = null)
    {
        // 独立 catalog：把 TestMessage 映射到本轮独占 topic，避免历史消息污染 offset 断言。
        var builder = new MessageCatalogBuilder();
        builder.Add(MessageDescriptor.Create(TestJsonContext.Default.TestMessage, topic));
        var catalog = builder.Build();
        var serializer = new JsonMessageSerializer(catalog);

        var broker = new KafkaBroker(
            new ProducerConfig
            {
                BootstrapServers = Fixture.KafkaBootstrap,
                AllowAutoCreateTopics = true,
                // 每轮用全新 topic（隐式建 topic）——5s 的快速失败超时在 auto-create 的
                // metadata 传播窗口内会误报 "Local: Message timed out"，故放宽到 60s。
                MessageTimeoutMs = 60_000,
                // 容器通告 localhost 会让 rdkafka 先试 IPv6 ::1（本机不可达），
                // 回落窗口内叠加超时判定 → 随机假失败。显式限定 IPv4。
                BrokerAddressFamily = BrokerAddressFamily.V4
            },
            new ConsumerConfig
            {
                BootstrapServers = Fixture.KafkaBootstrap,
                GroupId = groupId,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                AllowAutoCreateTopics = true,
                BrokerAddressFamily = BrokerAddressFamily.V4,
                // 修复的核心前提：确认器只有在关闭自动存储时才真正存储 offset。
                EnableAutoOffsetStore = false,
                // 观测 committed offset 需要显式 Commit——自动提交会掩盖 StoreOffset 的
                // 有无（auto.commit=true 时 librdkafka 周期提交内部 offset store）。
                EnableAutoCommit = enableAutoCommit
            },
            logger,
            serializer,
            catalog);
        return (broker, serializer);
    }
}
