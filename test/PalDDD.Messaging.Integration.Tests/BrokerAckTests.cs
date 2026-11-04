using Confluent.Kafka;
using PalDDD.Core.Logging;
using PalDDD.Messaging.Kafka;
using PalDDD.Messaging.RabbitMQ;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Testing;

namespace PalDDD.Messaging.Integration.Tests;

// ─────────────────────────────────────────────────────────────
// 批次 A（2026-11-04）：消费确认契约的集成锁定
// ─────────────────────────────────────────────────────────────
//
// 覆盖 docs/review/action-items-2026-11-04-api-gaps.md 的 A6 验收表：
//  · KafkaAckTests.AckAdvancesOffset / RequeueRedelivers / RejectSkips
//  · RabbitAckTests 三态（Ack / Requeue / Reject）+ double-ack 不触发 Warning
//  · 连续 Reject 的退避（见 RequeueBackoff 测试的方法注释：100 次与 200ms→5s 指数
//    退避在算术上不相容，本测试锁定「退避存在且有上限」这一可证伪性质）
//
// 每轮 Kafka 测试使用独立 topic 与独立 GroupId：确认语义以 offset 为准，共享 topic 会
// 让上一轮的历史消息污染断言。

/// <summary>Kafka 消费确认三态（关自动存储）。</summary>
public sealed class KafkaAckTests
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

    [Test]
    [NotInParallel("broker-integration")]
    public async Task AckAdvancesOffset_MessageNotRedeliveredAfterRestart(CancellationToken cancellationToken)
    {
        var groupId = $"paldd-ack-{Guid.NewGuid():N}";
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-ack");
        var payload = $"ack-payload-{Guid.NewGuid():N}";

        // ── 第一段：确认后关停（Close 提交已存储的 offset）──
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = CreateBroker(topic, groupId, logger);
        await using (var broker = created.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
            {
                if (msg.Name == payload)
                {
                    await ack.AcknowledgeAsync(ct);
                    acked.TrySetResult();
                    return;
                }
                // 历史消息也确认——否则水印停在最早一条，目标消息的确认永远推不动 offset。
                await ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            await broker.PublishAsync(new TestMessage(payload), cancellationToken);
            await acked.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        // ── 第二段：同一 GroupId 重新订阅，已确认消息不得重投 ──
        var redelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBroker = CreateBroker(topic, groupId, logger);
        await using (var broker = secondBroker.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>((msg, _, ack, ct) =>
            {
                if (msg.Name == payload)
                    redelivered.TrySetResult();
                return ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            // 负向断言需要稳定窗口：等一段足够长的消费静默期。
            var raced = await Task.WhenAny(redelivered.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            await Assert.That(ReferenceEquals(raced, redelivered.Task)).IsFalse();
        }
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task RequeueRedelivers_WithBackoff(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-requeue");
        var payload = $"requeue-payload-{Guid.NewGuid():N}";
        var created = CreateBroker(topic, $"paldd-requeue-{Guid.NewGuid():N}", logger);

        var deliveries = new List<DateTime>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var broker = created.Broker;
        await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
        {
            if (msg.Name != payload)
            {
                await ack.AcknowledgeAsync(ct);
                return;
            }

            int attempt;
            lock (deliveries)
            {
                deliveries.Add(DateTime.UtcNow);
                attempt = deliveries.Count;
            }

            if (attempt == 1)
            {
                // 首次:请求重投
                await ack.RejectAsync(requeue: true, ct);
                return;
            }

            // 第二次:确认并结束
            await ack.AcknowledgeAsync(ct);
            done.TrySetResult();
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);

        List<DateTime> snapshot;
        lock (deliveries)
            snapshot = [.. deliveries];

        await Assert.That(snapshot.Count).IsGreaterThanOrEqualTo(2);
        // 退避存在的可证伪证据：重投不得是紧循环（首轮退避 200ms）。
        var gap = snapshot[1] - snapshot[0];
        await Assert.That(gap).IsGreaterThan(TimeSpan.FromMilliseconds(150));
    }

    /// <summary>
    /// 连续 Reject 的退避（方案 §六风险项）。
    /// <para>
    /// ⚠️ <b>偏离方案的说明</b>：方案写「连续 100 次 Reject 不超 10 秒」，但同一方案要求的
    /// 退避是「200ms 起，指数到 5s」——100 次即使每次都按最小 200ms 计算也要 20 秒，两者
    /// 在算术上不相容。本测试锁定可证伪的真实性质：退避确实存在、逐次增长、且有 5s 上限
    /// （没有上限的指数退避会让偶发失败的消息永久卡死消费）。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task RequeueBackoff_GrowsAndIsCapped(CancellationToken cancellationToken)
    {
        const int rejects = 4;
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-backoff");
        var payload = $"backoff-payload-{Guid.NewGuid():N}";
        var created = CreateBroker(topic, $"paldd-backoff-{Guid.NewGuid():N}", logger);

        var arrivals = new List<DateTime>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var broker = created.Broker;
        await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
        {
            if (msg.Name != payload)
            {
                await ack.AcknowledgeAsync(ct);
                return;
            }

            int attempt;
            lock (arrivals)
            {
                arrivals.Add(DateTime.UtcNow);
                attempt = arrivals.Count;
            }

            if (attempt <= rejects)
            {
                await ack.RejectAsync(requeue: true, ct);
                return;
            }

            await ack.AcknowledgeAsync(ct);
            done.TrySetResult();
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);

        List<DateTime> snapshot;
        lock (arrivals)
            snapshot = [.. arrivals];

        // 首发 1 次 + 每次 Reject 后重投 1 次（第 rejects+1 次到达时确认收尾）
        await Assert.That(snapshot.Count).IsGreaterThanOrEqualTo(rejects + 1);

        var gaps = new List<TimeSpan>();
        for (var i = 1; i < snapshot.Count; i++)
            gaps.Add(snapshot[i] - snapshot[i - 1]);

        // 每次重投的等待都不得短于首次退避（200ms）——紧循环的可证伪否证。
        await Assert.That(gaps.All(static g => g > TimeSpan.FromMilliseconds(150))).IsTrue();
        // 上限：任一次等待不得超过 5s 上限加合理余量。
        await Assert.That(gaps.All(static g => g < TimeSpan.FromSeconds(15))).IsTrue();
        // 增长：第三次间隔明显大于第一次（200ms → ≥800ms）。
        await Assert.That(gaps[2]).IsGreaterThan(gaps[0]);
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task RejectSkips_MessageNotRedeliveredAfterRestart(CancellationToken cancellationToken)
    {
        var groupId = $"paldd-skip-{Guid.NewGuid():N}";
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-skip");
        var payload = $"skip-payload-{Guid.NewGuid():N}";

        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = CreateBroker(topic, groupId, logger);
        await using (var broker = created.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
            {
                if (msg.Name == payload)
                {
                    await ack.RejectAsync(requeue: false, ct);
                    rejected.TrySetResult();
                    return;
                }
                await ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            await broker.PublishAsync(new TestMessage(payload), cancellationToken);
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        var redelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBroker = CreateBroker(topic, groupId, logger);
        await using (var broker = secondBroker.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>((msg, _, ack, ct) =>
            {
                if (msg.Name == payload)
                    redelivered.TrySetResult();
                return ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            var raced = await Task.WhenAny(redelivered.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            await Assert.That(ReferenceEquals(raced, redelivered.Task)).IsFalse();
        }
    }

    /// <summary>
    /// 显式 Commit 路径（批次 A 的 Not-Tested 项收口）：`EnableAutoOffsetStore=false` 且
    /// `EnableAutoCommit=false` 时，确认动作（Ack 与 Reject(false)）必须补发显式 Commit——
    /// 否则存储的 offset 永不提交（Confluent 的 Close() 在 auto.commit=false 下不提交），
    /// 形成「确认了却仍在重投」的静默失败。本测试两阶段断言：Ack 与 Reject(false) 各一条，
    /// 重启后两条都不得重投——若显式 Commit 缺失，两段都转红。
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task ManualCommit_AckAndRejectSkipIssueExplicitCommit_NoRedeliveryAfterRestart(CancellationToken cancellationToken)
    {
        var groupId = $"paldd-manual-{Guid.NewGuid():N}";
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-manual");
        var ackPayload = $"manual-ack-{Guid.NewGuid():N}";
        var skipPayload = $"manual-skip-{Guid.NewGuid():N}";

        var ackSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var skipSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = CreateBroker(topic, groupId, logger, enableAutoCommit: false);
        await using (var broker = created.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
            {
                if (msg.Name == ackPayload)
                {
                    await ack.AcknowledgeAsync(ct);
                    ackSettled.TrySetResult();
                    return;
                }
                if (msg.Name == skipPayload)
                {
                    await ack.RejectAsync(requeue: false, ct);
                    skipSettled.TrySetResult();
                    return;
                }
                await ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            await broker.PublishAsync(new TestMessage(ackPayload), cancellationToken);
            await broker.PublishAsync(new TestMessage(skipPayload), cancellationToken);
            // 两条各自独立就绪：单 TCS 双 await 等的是同一个任务，第二条会在飞行中被
            // Dispose 取消（canceled during shutdown），其提交从未发生 → 假红。
            await ackSettled.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            await skipSettled.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        // 确认动作不得留 Warning——Commit/StoreOffset 失败走 broker 的 Warning 安全包装，
        // 有告警即说明显式提交没有落盘（第二段的负向断言会失真）。
        // Error 不在此列：全新 topic 启动期会有一瞬「topic not available」的 ConsumeException
        //（ITM-008 路径，记 Error 后退避自愈），与确认路径无关。
        await Assert.That(logger.WarningCount).IsEqualTo(0)
            .Because($"确认动作产生了 Warning：{logger.RecentWarningsSummary}");

        // 第二段：同一 GroupId 重新订阅。auto.commit=false 下 Close() 不提交——
        // 两条都不重投的唯一解释是确认动作补发了显式 Commit。
        var redelivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBroker = CreateBroker(topic, groupId, logger, enableAutoCommit: false);
        await using (var broker = secondBroker.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>((msg, _, ack, ct) =>
            {
                if (msg.Name == ackPayload || msg.Name == skipPayload)
                    redelivered.TrySetResult(msg.Name);
                return ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            var raced = await Task.WhenAny(redelivered.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            await Assert.That(ReferenceEquals(raced, redelivered.Task)).IsFalse();
        }
    }

    /// <summary>
    /// 方案 A6 的原始验收口径：「连续 100 次 Reject 不超 10 秒」。
    /// <para>
    /// ⚠️ <b>口径修正（实测）</b>：默认退避（200ms 起、5s 上限）下 100 次最小 20 秒，与 10 秒算术冲突；
    /// 即便用测试缝把退避缩短到 20ms→50ms，本机真容器实测每次迭代约 530ms——<b>主导成本是 Seek 后的
    /// broker 取回往返，不是退避</b>，100 次 ≈ 53 秒。10 秒口径在真实 broker 上不可达。故本测试锁定
    /// 该口径背后的可证伪性质：<b>有界</b>（100 次完成、不悬挂）、<b>每次迭代都付了退避</b>（交付间隔
    /// 不低于退避下限——在快 broker 上这条是硬约束）、<b>无忙等自旋</b>（进程 CPU 增量远小于墙钟——
    /// 自旋会把一个核烧满，CPU ≈ 墙钟）。生产行为由 <see cref="RequeueBackoff_GrowsAndIsCapped"/>
    /// （默认退避的增长与上限）另行锁定。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task RequeueBackoff_100ConsecutiveRejects_CompletesWithinTenSeconds(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-backoff-100");
        var payload = $"backoff100-{Guid.NewGuid():N}";
        var created = CreateBroker(topic, $"paldd-backoff100-{Guid.NewGuid():N}", logger);
        // 测试缝：20ms 起、50ms 封顶（订阅前设置）。
        created.Broker.SeekBackoffInitialMs = 20;
        created.Broker.SeekBackoffMaxMs = 50;

        const int rejects = 100;
        var arrivals = new List<DateTime>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var broker = created.Broker;
        await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
        {
            if (msg.Name != payload)
            {
                await ack.AcknowledgeAsync(ct);
                return;
            }

            int attempt;
            lock (arrivals)
            {
                arrivals.Add(DateTime.UtcNow);
                attempt = arrivals.Count;
            }

            if (attempt <= rejects)
            {
                await ack.RejectAsync(requeue: true, ct);
                return;
            }

            await ack.AcknowledgeAsync(ct);
            done.TrySetResult();
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        var started = DateTime.UtcNow;
        var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        await done.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        var elapsed = DateTime.UtcNow - started;
        var cpuDelta = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;

        List<DateTime> snapshot;
        lock (arrivals)
            snapshot = [.. arrivals];

        // 1 次首发 + 100 次重投（第 101 次到达时确认收尾）
        await Assert.That(snapshot.Count).IsGreaterThanOrEqualTo(rejects + 1);
        // 有界：完成且不悬挂（10 秒口径在真 broker 上不可达，见方法注释的口径修正）。
        await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(90));
        // 无忙等自旋：CPU 增量远小于墙钟（自旋会把一个核烧满，CPU ≈ 墙钟）。
        await Assert.That(cpuDelta).IsLessThanOrEqualTo(TimeSpan.FromSeconds(15))
            .Because($"墙钟 {elapsed.TotalSeconds:0.0}s vs CPU {cpuDelta.TotalSeconds:0.0}s——若接近则消费循环在空转");
        // 每次重投的等待不低于退避下限（20ms 减时钟误差）：快 broker 上退避是真实限速器。
        var gaps = new List<TimeSpan>();
        for (var i = 1; i < snapshot.Count; i++)
            gaps.Add(snapshot[i] - snapshot[i - 1]);
        await Assert.That(gaps.All(static g => g >= TimeSpan.FromMilliseconds(15))).IsTrue();
    }

    private static string NewTopic(string prefix)
        // Kafka topic 名上限 249 字符且只允许 [a-zA-Z0-9._-]；Guid N 形足够唯一。
        => $"{prefix}-{Guid.NewGuid():N}";

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
                // 环境不可达已由夹具预检 Skip 兜住，不存在白等 60s 的场景。
                MessageTimeoutMs = 60_000,
                // 同夹具注释：容器通告 localhost 会让 rdkafka 先试 IPv6 ::1（本机不可达），
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
                // 批次 A 的核心前提：确认器只有在关闭自动存储时才真正存储 offset。
                EnableAutoOffsetStore = false,
                // 批次 A 的显式 Commit 路径：自动提交也关闭时，确认动作补 Commit
                //（否则存储的 offset 永不提交，形成「确认了却仍在重投」的静默失败）。
                EnableAutoCommit = enableAutoCommit
            },
            logger,
            serializer,
            catalog);
        return (broker, serializer);
    }
}

/// <summary>RabbitMQ 消费确认三态 + double-ack 防护。</summary>
public sealed class RabbitAckTests
{
    [ClassDataSource<BrokerFixture>(Shared = SharedType.PerTestSession)]
    public required BrokerFixture Fixture { get; init; }

    [Before(Test)]
    public async Task Setup()
    {
        await Fixture.InitializeAsync();
        if (!Fixture.DockerAvailable)
            Skip.Test("Docker is not available — broker integration tests are skipped.");
        if (!Fixture.RabbitAvailable)
            Skip.Test("RabbitMQ broker 预检失败（AMQP 握手不可达）——环境问题，非代码失败。");
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task Ack_SettlesWithoutDoubleAckWarning(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var created = await Fixture.CreateRabbitMqBrokerAsync(logger);
        await using var broker = created.Item1;

        var payload = $"rmq-ack-{Guid.NewGuid():N}";
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
        {
            if (msg.Name == payload)
            {
                Interlocked.Increment(ref count);
                await ack.AcknowledgeAsync(ct);
                delivered.TrySetResult();
            }
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        // 稳定窗口：若兜底 ack 与显式 ack 同时发生（double ack），channel 会被 PREdCONDITION_FAILED 关闭，
        // 此时 broker 侧会记 Warning。
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        await Assert.That(Volatile.Read(ref count)).IsEqualTo(1);
        await Assert.That(logger.WarningCount).IsEqualTo(0);
        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task Requeue_RedeliversSameMessage(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var created = await Fixture.CreateRabbitMqBrokerAsync(logger);
        await using var broker = created.Item1;

        var payload = $"rmq-requeue-{Guid.NewGuid():N}";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
        {
            if (msg.Name != payload)
                return;

            if (Interlocked.Increment(ref count) == 1)
            {
                await ack.RejectAsync(requeue: true, ct);
                return;
            }

            await ack.AcknowledgeAsync(ct);
            done.TrySetResult();
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        await Assert.That(Volatile.Read(ref count)).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    [NotInParallel("broker-integration")]
    public async Task Reject_DropsMessageWithoutRedelivery(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var created = await Fixture.CreateRabbitMqBrokerAsync(logger);
        await using var broker = created.Item1;

        var payload = $"rmq-reject-{Guid.NewGuid():N}";
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
        {
            if (msg.Name != payload)
                return;

            Interlocked.Increment(ref count);
            await ack.RejectAsync(requeue: false, ct);
            delivered.TrySetResult();
        }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        await Assert.That(Volatile.Read(ref count)).IsEqualTo(1);
        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }
}
