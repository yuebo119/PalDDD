using PalDDD.Messaging;
using PalDDD.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging.Tests;

/// <summary>
/// 批次 A（2026-11-04）消费确认契约的单元锁定。<br/>
/// 覆盖点：无操作确认器不抛（旧重载零变化的前提）、不覆写新重载的 broker 仍为 at-most-once、
/// 批次级确认器的聚合/单次结算/逆序拒绝语义、批量泵的兜底（成功确认 / 异常弃置）。
/// </summary>
public sealed class MessageAckTests
{
    // ── NoOpMessageAck ──

    [Test]
    public async Task NoOpMessageAck_Acknowledge_CompletesSuccessfullyWithoutAsyncState()
    {
        var pending = NoOpMessageAck.Instance.AcknowledgeAsync();

        // 同步成功完成 = 无异步状态机、无捕获上下文：旧重载路径不因确认器多一次调度。
        await Assert.That(pending.IsCompletedSuccessfully).IsTrue();
        await pending;
    }

    [Test]
    public async Task NoOpMessageAck_Reject_CompletesSuccessfullyForBothModes()
    {
        var requeue = NoOpMessageAck.Instance.RejectAsync(requeue: true);
        var discard = NoOpMessageAck.Instance.RejectAsync(requeue: false);

        await Assert.That(requeue.IsCompletedSuccessfully).IsTrue();
        await Assert.That(discard.IsCompletedSuccessfully).IsTrue();
        await requeue;
        await discard;
    }

    /// <summary>
    /// 已取消的 token 下也必须「成功完成」：旧重载路径（二参/三参）经 DIM 转发到确认重载时
    /// 拿到 NoOpMessageAck，若它抛 OCE，handler 成功返回后反而凭空多出一条取消异常。
    /// </summary>
    [Test]
    public async Task NoOpMessageAck_WithCanceledToken_StillCompletesSuccessfully()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var acknowledge = NoOpMessageAck.Instance.AcknowledgeAsync(cts.Token);
        var requeue = NoOpMessageAck.Instance.RejectAsync(requeue: true, cts.Token);
        var discard = NoOpMessageAck.Instance.RejectAsync(requeue: false, cts.Token);

        await Assert.That(acknowledge.IsCompletedSuccessfully).IsTrue();
        await Assert.That(requeue.IsCompletedSuccessfully).IsTrue();
        await Assert.That(discard.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task NoOpMessageAck_Instance_IsSharedSingleton()
    {
        var first = NoOpMessageAck.Instance;
        var second = NoOpMessageAck.Instance;

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    // ── 默认接口实现（不覆写新重载的 broker 仍是 at-most-once）──

    [Test]
    public async Task AckOverload_DefaultImplementation_ForwardsNoOpAckToHandler(CancellationToken cancellationToken)
    {
        // LegacyOnlyBroker 只实现二参重载——四参（含确认器）重载走 DIM。
        var broker = new LegacyOnlyBroker<string>();
        IMessageBroker iface = broker;
        IMessageAck? observed = null;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await iface.SubscribeAsync<string>(
            (message, context, ack, token) =>
            {
                _ = message;
                _ = context;
                _ = token;
                observed = ack;
                received.TrySetResult();
                return ValueTask.CompletedTask;
            },
            cancellationToken);

        await broker.PublishIncomingAsync("payload", cancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        await Assert.That(ReferenceEquals(observed, NoOpMessageAck.Instance)).IsTrue();
    }

    // ── MessageBatchAck（internal，IVT）──

    [Test]
    public async Task BatchAck_Acknowledge_DelegatesToEveryDelivery()
    {
        var log = new List<string>();
        var batchAck = new MessageBatchAck([new RecordingAck(0, log), new RecordingAck(1, log), new RecordingAck(2, log)]);

        await batchAck.AcknowledgeAsync();

        await Assert.That(string.Join(",", log)).IsEqualTo("ack-0,ack-1,ack-2");
        await Assert.That(batchAck.IsSettled).IsTrue();
    }

    /// <summary>
    /// 逆序委派是刻意锁定的实现取舍：Kafka 的 <c>Seek</c> 最后一次生效，按批内顺序逐条
    /// reject 会让重投从最后一条开始；逆序后最后一次 seek 落回批内最早一条，整批重放。
    /// </summary>
    [Test]
    public async Task BatchAck_Reject_DelegatesInReverseOrder()
    {
        var log = new List<string>();
        var batchAck = new MessageBatchAck([new RecordingAck(0, log), new RecordingAck(1, log), new RecordingAck(2, log)]);

        await batchAck.RejectAsync(requeue: true);

        await Assert.That(string.Join(",", log)).IsEqualTo("reject-2-True,reject-1-True,reject-0-True");
    }

    [Test]
    public async Task BatchAck_SecondSettlement_IsIgnored()
    {
        var log = new List<string>();
        var batchAck = new MessageBatchAck([new RecordingAck(0, log)]);

        await batchAck.AcknowledgeAsync();
        await batchAck.RejectAsync(requeue: false);

        await Assert.That(string.Join(",", log)).IsEqualTo("ack-0");
    }

    [Test]
    public async Task BatchAck_Empty_IsNoOpAndStaysUnsettled()
    {
        await MessageBatchAck.Empty.AcknowledgeAsync();
        await MessageBatchAck.Empty.RejectAsync(requeue: true);

        await Assert.That(MessageBatchAck.Empty.IsSettled).IsFalse();
    }

    // ── 批量泵：确认器装配与兜底 ──

    [Test]
    public async Task SubscribeBatchAsync_WithAckHandler_DefaultSettlementAcknowledgesEveryDelivery(CancellationToken cancellationToken)
    {
        var broker = new AckProbeBroker<string>();
        IMessageBroker iface = broker;
        var log = new List<string>();
        var handedAck = new TaskCompletionSource<IMessageAck>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await iface.SubscribeBatchAsync<string>(
            (batch, ack, token) =>
            {
                _ = batch;
                _ = token;
                handedAck.TrySetResult(ack);
                return ValueTask.CompletedTask; // 不显式结算 → 兜底整批确认
            },
            maxBatchSize: 3,
            maxDelay: TimeSpan.FromMilliseconds(10),
            cancellationToken);

        for (var i = 0; i < 3; i++)
            await broker.PushAsync($"m{i}", new RecordingAck(i, log), cancellationToken);

        var ack = await handedAck.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await WaitUntilAsync(() => log.Count == 3, "兜底确认未在时限内落地");

        await Assert.That(ReferenceEquals(ack, NoOpMessageAck.Instance)).IsFalse();
        await Assert.That(string.Join(",", log)).IsEqualTo("ack-0,ack-1,ack-2");
    }

    [Test]
    public async Task SubscribeBatchAsync_HandlerRejectsWithRequeue_RejectsEveryDeliveryInReverseOrder(CancellationToken cancellationToken)
    {
        var broker = new AckProbeBroker<string>();
        IMessageBroker iface = broker;
        var log = new List<string>();
        var handlerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await iface.SubscribeBatchAsync<string>(
            async (batch, ack, token) =>
            {
                _ = batch;
                await ack.RejectAsync(requeue: true, token);
                handlerDone.TrySetResult();
            },
            maxBatchSize: 3,
            maxDelay: TimeSpan.FromMilliseconds(10),
            cancellationToken);

        for (var i = 0; i < 3; i++)
            await broker.PushAsync($"m{i}", new RecordingAck(i, log), cancellationToken);

        await handlerDone.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await WaitUntilAsync(() => log.Count == 3, "显式拒绝未在时限内落地");

        await Assert.That(string.Join(",", log)).IsEqualTo("reject-2-True,reject-1-True,reject-0-True");
    }

    [Test]
    public async Task SubscribeBatchAsync_HandlerThrows_DiscardsWholeBatchWithoutRequeue(CancellationToken cancellationToken)
    {
        var broker = new AckProbeBroker<string>();
        IMessageBroker iface = broker;
        var log = new List<string>();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 经 MessageBrokerBase 覆写才拿到 OnBatchHandlerFailed 的错误回调；此处走 IMessageBroker
        // DIM（静态无操作回调），因此只断言「整批弃置」这一可观测结果。
        await using var subscription = await iface.SubscribeBatchAsync<string>(
            (batch, ack, token) =>
            {
                _ = batch;
                _ = ack;
                _ = token;
                handlerEntered.TrySetResult();
                throw new InvalidOperationException("batch handler failure (test)");
            },
            maxBatchSize: 2,
            maxDelay: TimeSpan.FromMilliseconds(10),
            cancellationToken);

        for (var i = 0; i < 2; i++)
            await broker.PushAsync($"m{i}", new RecordingAck(i, log), cancellationToken);

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await WaitUntilAsync(() => log.Count == 2, "异常后的弃置结算未在时限内落地");

        await Assert.That(string.Join(",", log)).IsEqualTo("reject-1-False,reject-0-False");
    }

    [Test]
    public async Task SubscribeBatchAsync_LegacyOverload_SettlesNothing(CancellationToken cancellationToken)
    {
        // 旧重载路径不收集确认器：兜底动作落在空批次确认器上，因此 broker 侧零结算调用。
        var broker = new AckProbeBroker<string>();
        IMessageBroker iface = broker;
        var log = new List<string>();
        var handlerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await iface.SubscribeBatchAsync<string>(
            (batch, token) =>
            {
                _ = batch;
                _ = token;
                handlerDone.TrySetResult();
                return ValueTask.CompletedTask;
            },
            maxBatchSize: 2,
            maxDelay: TimeSpan.FromMilliseconds(10),
            cancellationToken);

        for (var i = 0; i < 2; i++)
            await broker.PushAsync($"m{i}", new RecordingAck(i, log), cancellationToken);

        await handlerDone.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        // 稳定窗口：确认该批的兜底已执行完（无结算动作可观测，只能等泵一轮）。
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

        await Assert.That(log.Count).IsEqualTo(0);
    }

    // ── v4 批 A（B2）：结算移交标记 + 泵关停兜底 ──

    /// <summary>
    /// 标记时序锁定：broker 的 handler（泵的 EnqueueAsync）返回时标记必须已置位。
    /// 若标记挪到 WriteAsync 之后或 reader 侧，broker 的兜底 ACK 判定会漏标 → pre-ACK。
    /// </summary>
    [Test]
    public async Task SubscribeBatchAsync_AckPath_MarksDeferredBeforeBrokerHandlerReturns(CancellationToken cancellationToken)
    {
        var broker = new AckProbeBroker<string>();
        IMessageBroker iface = broker;
        var log = new List<string>();
        var first = new RecordingAck(0, log);
        var second = new RecordingAck(1, log);

        await using var subscription = await iface.SubscribeBatchAsync<string>(
            (batch, ack, token) =>
            {
                _ = batch;
                _ = token;
                _ = ack;
                return ValueTask.CompletedTask;
            },
            maxBatchSize: 2,
            maxDelay: TimeSpan.FromMilliseconds(10),
            cancellationToken);

        await broker.PushAsync("m0", first, cancellationToken);

        // PushAsync 内部 await 了泵的四参 handler；返回即表示 EnqueueAsync 已完成，
        // 此时标记必须已置位（v4 §2.1 顺序硬约束）
        await Assert.That(DeferredAckRegistry.IsDeferred(first)).IsTrue();
        // 未进入泵的确认器不受影响（负向对照）
        await Assert.That(DeferredAckRegistry.IsDeferred(second)).IsFalse();
    }

    /// <summary>
    /// 泵关停兜底：handler 因取消抛 OCE 时，当前批必须补 Reject(requeue:false)，
    /// 否则已入通道的 delivery 恒 unacked（占用 prefetch 名额）。
    /// </summary>
    [Test]
    public async Task SubscribeBatchAsync_ShutdownDuringHandler_RejectsCurrentBatch(CancellationToken cancellationToken)
    {
        var broker = new AckProbeBroker<string>();
        IMessageBroker iface = broker;
        var log = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var subscription = await iface.SubscribeBatchAsync<string>(
            async (batch, ack, token) =>
            {
                _ = batch;
                _ = ack;
                entered.TrySetResult();
                // 等到订阅关停：pump 的 ct 取消 → OCE 从 handler 逃逸 → 关停兜底分支
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            maxBatchSize: 2,
            maxDelay: TimeSpan.FromMilliseconds(10),
            cts.Token);

        await broker.PushAsync("m0", new RecordingAck(0, log), cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        await cts.CancelAsync();
        await subscription.DisposeAsync();

        await Assert.That(string.Join(",", log)).IsEqualTo("reject-0-False");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException(message);
    }

    /// <summary>记录结算动作的确认器（id 用于锁定批内顺序）。</summary>
    private sealed class RecordingAck(int id, List<string> log) : IMessageAck
    {
        public ValueTask AcknowledgeAsync(CancellationToken ct = default)
        {
            lock (log)
                log.Add($"ack-{id}");
            return ValueTask.CompletedTask;
        }

        public ValueTask RejectAsync(bool requeue, CancellationToken ct = default)
        {
            lock (log)
                log.Add($"reject-{id}-{requeue}");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>只实现二参重载的 broker——用于证明四参重载的 DIM 兜底传 NoOpMessageAck。</summary>
    private sealed class LegacyOnlyBroker<TMessage> : IMessageBroker
    {
        private Func<TMessage, CancellationToken, ValueTask>? _handler;

        public ValueTask<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
        {
            if (typeof(T) != typeof(TMessage))
                throw new InvalidOperationException($"LegacyOnlyBroker<{typeof(TMessage).Name}> cannot subscribe {typeof(T).Name}.");
            _handler = (Func<TMessage, CancellationToken, ValueTask>)(object)handler;
            return new ValueTask<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public async ValueTask PublishIncomingAsync(TMessage message, CancellationToken ct)
        {
            var handler = _handler ?? throw new InvalidOperationException("No subscription registered.");
            await handler(message, ct).ConfigureAwait(false);
        }

        public ValueTask PublishAsync<T>(T message, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask PublishAsync(
            object message,
            MessageDescriptor descriptor,
            PalUlid messageId,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>同时支持两条订阅通路的探针 broker：四参（确认）与二参（旧重载）。</summary>
    private sealed class AckProbeBroker<TMessage> : IMessageBroker
    {
        private Func<TMessage, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask>? _ackHandler;
        private Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask>? _contextHandler;

        public ValueTask<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
        {
            if (typeof(T) != typeof(TMessage))
                throw new InvalidOperationException($"AckProbeBroker<{typeof(TMessage).Name}> cannot subscribe {typeof(T).Name}.");
            _ackHandler = (Func<TMessage, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask>)(object)handler;
            return new ValueTask<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, MessageConsumeContext?, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
        {
            if (typeof(T) != typeof(TMessage))
                throw new InvalidOperationException($"AckProbeBroker<{typeof(TMessage).Name}> cannot subscribe {typeof(T).Name}.");
            _contextHandler = (Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask>)(object)handler;
            return new ValueTask<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
            => SubscribeAsync<T>((message, _, token) => handler(message, token), ct);

        public async ValueTask PushAsync(TMessage message, IMessageAck ack, CancellationToken ct)
        {
            var context = MessageConsumeContext.FromHeaders(null, null, $"id-{Guid.NewGuid():N}");
            if (_ackHandler is { } ackHandler)
            {
                await ackHandler(message, context, ack, ct).ConfigureAwait(false);
                return;
            }

            if (_contextHandler is { } contextHandler)
            {
                // 旧重载路径：broker 交付的确认器被丢弃（这正是被测行为）。
                _ = ack;
                await contextHandler(message, context, ct).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("No subscription registered.");
        }

        public ValueTask PublishAsync<T>(T message, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask PublishAsync(
            object message,
            MessageDescriptor descriptor,
            PalUlid messageId,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
