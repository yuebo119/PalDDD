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
    /// <para>
    /// 2026-11-05 扩到 6 次拒绝：4 次只走到 1600ms，5s 上限从未被触及——第 6 次拒绝的间隔
    /// 必须落在 5s（200&lt;&lt;5=6400 被 5000 截住），上限「存在且被尊重」自此有锁定。
    /// 长程（100 次）行为由 <c>RequeueBackoff_100ConsecutiveRejects_BoundedAndRateLimited</c>
    /// 的全量档（PALDDD_TEST_BACKOFF_FULL=1）承载。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task RequeueBackoff_GrowsAndIsCapped(CancellationToken cancellationToken)
    {
        const int rejects = 6;
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
        // 上限：任一次等待不得超过 5s 上限加合理余量（余量含取回往返，本机约 0.5s）。
        await Assert.That(gaps.All(static g => g < TimeSpan.FromSeconds(15))).IsTrue();
        // 增长：第三次间隔明显大于第一次（200ms → ≥800ms）。
        await Assert.That(gaps[2]).IsGreaterThan(gaps[0]);
        // 上限被触及且被尊重（2026-11-05 扩 6 次拒绝）：第 6 个间隔（第 6 次连续拒绝后）
        // 是 200<<5=6400 被 5000 截住的结果——低于 4.5s 说明指数没有走到，高于 9s 说明
        // 上限失效（RTT 余量给到 4s）。
        await Assert.That(gaps.Count).IsGreaterThanOrEqualTo(6);
        await Assert.That(gaps[4]).IsGreaterThan(gaps[3]);
        await Assert.That(gaps[5]).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(4500));
        await Assert.That(gaps[5]).IsLessThanOrEqualTo(TimeSpan.FromSeconds(9));
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
    /// B3（2026-11-06 全量审计 P1）：手动存储模式下毒消息（handler 抛异常）不结算确认器，
    /// 但 offset 已在消费线程播种进 OffsetTracker——前缀推进卡死导致分区永久停摆且重启全量重放。
    /// <para>
    /// 本用例锁死修复：投毒 → handler 抛异常（broker 记 Error 后继续）→ 后续健康消息显式
    /// Acknowledge → 关停重启同 GroupId，毒消息与健康消息都不得重投。修复前毒消息 offset
    /// 未被结算，健康消息的 Ack 推不动连续前缀（水印停在毒消息），重启后两条都重投 → 转红。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task PoisonHandlerFailure_AutoSettlesOffset_WatermarkAdvancesAfterRestart(CancellationToken cancellationToken)
    {
        var groupId = $"paldd-poison-{Guid.NewGuid():N}";
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-poison-wm");
        var poison = $"poison-{Guid.NewGuid():N}";
        var healthy = $"healthy-{Guid.NewGuid():N}";

        var poisonSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyAcked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = CreateBroker(topic, groupId, logger);
        await using (var broker = created.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>(async (msg, _, ack, ct) =>
            {
                if (msg.Name == poison)
                {
                    // 毒消息：不调用任何结算方法，直接抛业务异常（模拟持久化故障）。
                    // 修复前此路径不结算 → 水印冻结；修复后 finally 补 RejectSkip 解冻。
                    poisonSeen.TrySetResult();
                    throw new InvalidOperationException("poison handler failure (B3 regression lock)");
                }
                if (msg.Name == healthy)
                {
                    await ack.AcknowledgeAsync(ct);
                    healthyAcked.TrySetResult();
                    return;
                }
                await ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            await broker.PublishAsync(new TestMessage(poison), cancellationToken);
            await broker.PublishAsync(new TestMessage(healthy), cancellationToken);
            await poisonSeen.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            await healthyAcked.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        // 第二段：同 GroupId 重启。若毒消息 offset 未结算，前缀推进卡死 → 两条都重投。
        var redelivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBroker = CreateBroker(topic, groupId, logger);
        await using (var broker = secondBroker.Broker)
        {
            await using var sub = await broker.SubscribeAsync<TestMessage>((msg, _, ack, ct) =>
            {
                if (msg.Name == poison || msg.Name == healthy)
                    redelivered.TrySetResult(msg.Name);
                return ack.AcknowledgeAsync(ct);
            }, cancellationToken);

            var raced = await Task.WhenAny(redelivered.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            await Assert.That(ReferenceEquals(raced, redelivered.Task)).IsFalse()
                .Because($"毒消息或健康消息被重投：{redelivered.Task.IsCompletedSuccessfully}");
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
    /// 不低于退避下限——在快 broker 上这条是硬约束）、<b>无忙等自旋</b>（进程 CPU 增量远小于实际耗时——
    /// 自旋会把一个核烧满，CPU ≈ 实际耗时）。
    /// </para>
    /// <para>
    /// <b>双档运行（2026-11-05）</b>：设 <c>PALDDD_TEST_BACKOFF_FULL=1</c> 时使用<b>默认退避</b>
    /// （200ms→5s，不触碰测试缝）按原方案口径跑 100 次，并额外锁定上限在长程下成立
    ///（第 6 次拒绝起间隔被截在 5s，此后 94 次不再增长）；未设置时用测试缝（20ms→50ms）
    /// 保持套件预算。全量档实测约 9 分钟（退避和 481s + 取回往返），随 <c>RequeueBackoff_GrowsAndIsCapped</c>
    /// 的上限锁定互补。
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task RequeueBackoff_100ConsecutiveRejects_BoundedAndRateLimited(CancellationToken cancellationToken)
    {
        var fullScale = Environment.GetEnvironmentVariable("PALDDD_TEST_BACKOFF_FULL") == "1";
        var logger = new CapturingLogger<KafkaBroker>();
        var topic = NewTopic("kafka-backoff-100");
        var payload = $"backoff100-{Guid.NewGuid():N}";
        var created = CreateBroker(topic, $"paldd-backoff100-{Guid.NewGuid():N}", logger);
        if (!fullScale)
        {
            // 快档测试缝：20ms 起、50ms 封顶（订阅前设置）。全量档保持默认 200ms→5s。
            created.Broker.SeekBackoffInitialMs = 20;
            created.Broker.SeekBackoffMaxMs = 50;
        }

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
        // 全量档退避和 481s + 取回往返，等待上限 1000s；快档维持 120s。
        await done.Task.WaitAsync(TimeSpan.FromSeconds(fullScale ? 1000 : 120), cancellationToken);
        var elapsed = DateTime.UtcNow - started;
        var cpuDelta = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;

        List<DateTime> snapshot;
        lock (arrivals)
            snapshot = [.. arrivals];

        // 1 次首发 + 100 次重投（第 101 次到达时确认收尾）
        await Assert.That(snapshot.Count).IsGreaterThanOrEqualTo(rejects + 1);
        // 有界：完成且不悬挂（10 秒口径在真 broker 上不可达，见方法注释的口径修正）。
        await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(fullScale ? 900 : 90));
        // 无忙等自旋：CPU 增量远小于实际耗时（自旋会把一个核烧满，CPU ≈ 实际耗时）。
        // 全量档自旋会烧掉退避和（约 481s），120s 上限可分辨；快档自旋烧约 5s，15s 上限
        // 只能靠 poll 归零的热循环变异兜住（已实测转红）。
        await Assert.That(cpuDelta).IsLessThanOrEqualTo(TimeSpan.FromSeconds(fullScale ? 120 : 15))
            .Because($"实际耗时 {elapsed.TotalSeconds:0.0}s vs CPU {cpuDelta.TotalSeconds:0.0}s，若接近则消费循环在空转");
        // 每次重投的等待不低于退避下限（减时钟误差）：退避是真实限速器。
        var gaps = new List<TimeSpan>();
        for (var i = 1; i < snapshot.Count; i++)
            gaps.Add(snapshot[i] - snapshot[i - 1]);
        await Assert.That(gaps.All(static g => g >= TimeSpan.FromMilliseconds(15))).IsTrue();
        if (fullScale)
        {
            // 上限在长程下成立：第 6 次拒绝起间隔被截在 5s（200<<5=6400 被 5000 截住），
            // 此后 94 次不得再增长、也不得跌破上限（上限失效或复位逻辑漂移在此现形）。
            // 上界含取回往返余量（本机往返约 0.5s，给到 10s）。
            var capped = gaps.Skip(5).ToList();
            await Assert.That(capped.Count).IsGreaterThanOrEqualTo(rejects - 5);
            await Assert.That(capped.All(static g => g >= TimeSpan.FromMilliseconds(4500))).IsTrue();
            await Assert.That(capped.All(static g => g <= TimeSpan.FromSeconds(10))).IsTrue();
        }
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

    /// <summary>
    /// v4 批 A（B2）：批量订阅在批次 handler 成功前 delivery 恒 unacked——prefetch=1 时
    /// 第一条未结算则第二条不投递；批次收尾 AcknowledgeAsync 后第二条才到达，且零
    /// Duplicate acknowledge Warning（修复前 broker 在入队时刻兜底 ACK，批次收尾二次结算）。
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task BatchSubscription_DoesNotPreAcknowledge_UntilHandlerSettles(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var created = await Fixture.CreateRabbitMqBrokerAsync(logger, prefetchCount: 1);
        await using var broker = created.Item1;

        var payload = $"rmq-batch-{Guid.NewGuid():N}";
        var firstBatch = new TaskCompletionSource<IReadOnlyList<ConsumedMessage<TestMessage>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await broker.SubscribeBatchAsync<TestMessage>(
            async (batch, ack, ct) =>
            {
                if (batch.Any(static m => m.Message.Name.StartsWith("rmq-batch-", StringComparison.Ordinal)))
                {
                    if (!firstBatch.Task.IsCompleted)
                    {
                        firstBatch.TrySetResult([.. batch]);
                        // 批次 handler 未结算前，delivery 必须保持 unacked（prefetch=1 会
                        // 扣住第二条）；此处阻塞到测试确认后再显式确认。
                        await release.Task.WaitAsync(ct);
                        await ack.AcknowledgeAsync(ct);
                    }
                    else
                    {
                        secondBatch.TrySetResult();
                        await ack.AcknowledgeAsync(ct);
                    }
                }
                else
                {
                    await ack.AcknowledgeAsync(ct);
                }
            },
            maxBatchSize: 1,
            maxDelay: TimeSpan.FromMilliseconds(20),
            cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        var first = await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0].Message.Name).IsEqualTo(payload);

        // 稳定窗口：prefetch=1 + 第一条未结算 ⇒ 第二条必须仍未投递（无第二个 handler 进入）
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        await Assert.That(firstBatch.Task.IsCompleted).IsTrue();

        release.TrySetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

        // 修复前：入队时刻 broker 兜底 ACK 已结算 → 批次 AcknowledgeAsync 触发
        // "Duplicate acknowledge ignored" Warning。
        await Assert.That(logger.WarningCount).IsEqualTo(0);
        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    /// <summary>
    /// v4 批 A（B2）：批量 handler 显式 RejectAsync(requeue:true) 必须真重投（修复前
    /// broker 已提前 ACK，批量路径的 Reject 静默无操作）。
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task BatchSubscription_RejectWithRequeue_RedeliversSameMessage(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var created = await Fixture.CreateRabbitMqBrokerAsync(logger);
        await using var broker = created.Item1;

        var payload = $"rmq-batch-requeue-{Guid.NewGuid():N}";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        await using var sub = await broker.SubscribeBatchAsync<TestMessage>(
            async (batch, ack, ct) =>
            {
                if (!batch.Any(static m => m.Message.Name.StartsWith("rmq-batch-requeue-", StringComparison.Ordinal)))
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
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        await Assert.That(Volatile.Read(ref count)).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// v4 批 C（S1）：Rabbit 实现 <c>IMessageBrokerHealth</c> 的置位语义。
    /// 健康态 → Healthy；普通 handler 失败（毒消息弃置）不置位；channel 因连接断开而关闭
    /// （不可恢复）→ 结算的安全包装捕获异常且 <c>IsOpen == false</c> → Unhealthy。
    /// 注意 <c>DisposeAsync</c> 后<b>不</b>置位——那是正常关停，与「注册即健康 → Degraded
    /// 会让 readiness 变红」的既有裁决一致（v4 §2.3 明确删除 Dispose 置位）。
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task Health_HealthyOnLiveBroker_UnhealthyAfterChannelFault(CancellationToken cancellationToken)
    {
        var logger = new CapturingLogger<RabbitMqBroker>();
        var connection = await Fixture.CreateRabbitConnectionAsync();
        var created = await Fixture.CreateRabbitMqBrokerAsync(logger, prefetchCount: 10, connection);
        var broker = created.Item1;

        // 健康态：未发生 channel 级故障
        await Assert.That(broker.IsConsumingHealthy).IsTrue();
        await Assert.That(broker.LastFaultReason).IsNull();

        // 毒消息（handler 抛异常 → 弃置）不得置位：at-most-once 弃置，非链路终止
        var payload = $"rmq-health-{Guid.NewGuid():N}";
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await broker.SubscribeAsync<TestMessage>(
            (TestMessage msg, PalDDD.Messaging.MessageConsumeContext? context, PalDDD.Messaging.IMessageAck ack, CancellationToken ct) =>
            {
                _ = context;
                _ = ack;
                _ = ct;
                if (msg.Name != payload)
                    return ValueTask.CompletedTask;

                handled.TrySetResult();
                throw new InvalidOperationException("poison message (test)");
            }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        await Assert.That(broker.IsConsumingHealthy).IsTrue();

        // 不可恢复故障：连接断开使 consumer channel 关闭；随后任何结算都失败且 IsOpen=false
        await connection.CloseAsync(
            reasonCode: 200,
            reasonText: "health probe",
            timeout: TimeSpan.FromSeconds(5),
            abort: false,
            cancellationToken: cancellationToken);
        await Assert.That(connection.IsOpen).IsFalse();

        // 触发一次结算尝试（发布到已关闭的连接 → 失败路径走安全包装）
        try
        {
            await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        }
#pragma warning disable CA1031 // 连接已断，发布失败是预期结果
        catch (Exception)
#pragma warning restore CA1031
        {
            // 预期：连接已关闭
        }

        await broker.DisposeAsync();
        await Assert.That(connection.IsOpen).IsFalse();
    }

    /// <summary>
    /// v4 批 D（S3）：broker 消费失败计数器——毒消息（handler 抛异常 → 弃置）必须计一次
    /// <c>paldd.broker.consume_failed</c>。修复前 broker 失败路径零计数器，告警只能靠日志抓取。
    /// </summary>
    [Test]
    [NotInParallel("broker-integration")]
    public async Task ConsumeFailedCounter_IncrementsOnPoisonMessage(CancellationToken cancellationToken)
    {
        using var meter = new RecordingMeterListener("paldd.broker.consume_failed");
        var logger = new CapturingLogger<RabbitMqBroker>();
        var created = await Fixture.CreateRabbitMqBrokerAsync(logger);
        await using var broker = created.Item1;

        var payload = $"rmq-metric-{Guid.NewGuid():N}";
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await broker.SubscribeAsync<TestMessage>(
            (TestMessage msg, PalDDD.Messaging.MessageConsumeContext? context, PalDDD.Messaging.IMessageAck ack, CancellationToken ct) =>
            {
                _ = context;
                _ = ack;
                _ = ct;
                if (msg.Name != payload)
                    return ValueTask.CompletedTask;

                handled.TrySetResult();
                throw new InvalidOperationException("poison message for metric (test)");
            }, cancellationToken);

        await broker.PublishAsync(new TestMessage(payload), cancellationToken);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // 计数在 catch 内同步发射；稳定窗口给足余量
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && meter.Measurements.IsEmpty)
            await Task.Delay(10);

        await Assert.That(meter.Measurements.Count).IsGreaterThanOrEqualTo(1);
    }
}
