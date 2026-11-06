// ─────────────────────────────────────────────────────────────
// 📦 MessageBatchPump — 批量订阅的组批泵（数量界 + 时间界）
// ─────────────────────────────────────────────────────────────
//
// 💡 2026-11-04（批次 A）：新增「批次级确认」通路。逐条订阅时把 broker 交付的
//   ｜ IMessageAck 随批携带，flush 时聚合成一个批次级确认器交给 handler；
//   ｜ handler 未显式结算时按 at-most-once 默认收尾（成功确认、异常弃置）。
//   ｜ 无 ack 的旧重载走同一泵但收集到的确认器为空，兜底为纯无操作——行为零变化。
// ─────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.Threading.Channels;

namespace PalDDD.Messaging;

/// <summary>泵内单条项——消息 + 消费上下文 + 该次交付的确认器（旧重载路径为 <c>null</c>）。</summary>
internal readonly record struct PumpItem<TMessage>(
    TMessage Message,
    MessageConsumeContext? Context,
    IMessageAck? Ack);

/// <summary>
/// 批量订阅的组批泵 — 复用逐条 <see cref="IMessageBroker.SubscribeAsync{TMessage}(Func{TMessage, MessageConsumeContext?, CancellationToken, ValueTask}, CancellationToken)"/>，
/// 按数量或时间界组批后交给批量 handler。批内消息按到达顺序；批列表在 flush 后清空复用。
/// </summary>
internal static class MessageBatchPump
{
    /// <summary>无 ack 的批量订阅（旧重载）——不收集确认器，兜底动作全部为无操作。</summary>
    public static ValueTask<IAsyncDisposable> StartAsync<TMessage>(
        IMessageBroker broker,
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler,
        int maxBatchSize,
        TimeSpan? maxDelay,
        Action<Exception> onHandlerError,
        CancellationToken ct)
        => StartCoreAsync<TMessage>(
            broker,
            (batch, _, token) => handler(batch, token),
            propagateAcks: false,
            maxBatchSize,
            maxDelay,
            onHandlerError,
            ct);

    /// <summary>含批次级确认器的批量订阅（批次 A 重载）。</summary>
    public static ValueTask<IAsyncDisposable> StartAsync<TMessage>(
        IMessageBroker broker,
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, IMessageAck, CancellationToken, ValueTask> handler,
        int maxBatchSize,
        TimeSpan? maxDelay,
        Action<Exception> onHandlerError,
        CancellationToken ct)
        => StartCoreAsync(broker, handler, propagateAcks: true, maxBatchSize, maxDelay, onHandlerError, ct);
    private static async ValueTask<IAsyncDisposable> StartCoreAsync<TMessage>(
        IMessageBroker broker,
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, IMessageAck, CancellationToken, ValueTask> handler,
        bool propagateAcks,
        int maxBatchSize,
        TimeSpan? maxDelay,
        Action<Exception> onHandlerError,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(onHandlerError);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
        // R14：通道容量按 maxBatchSize*4 计算——超 int 上限会在 BoundedChannelOptions 抛
        // 无指向性 AOOR；与 Outbox BatchSize 同口径给单批上界。
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBatchSize, 10_000, nameof(maxBatchSize));
        var delay = maxDelay ?? TimeSpan.FromMilliseconds(50);
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "maxDelay must be greater than zero.");
        // R14：Task.Delay 上限约 49.7 天（uint.MaxValue-1 毫秒）。超限的 AOOR 原本发生在
        // PumpAsync 的 try 之外：订阅已建立、泵直接 fault，broker 继续投递到满通道后
        // 消费静默停摆（无日志、无健康信号）。
        if (delay > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(maxDelay),
                "maxDelay must not exceed the Task.Delay limit (uint.MaxValue-1 milliseconds, about 49.7 days).");

        var channel = Channel.CreateBounded<PumpItem<TMessage>>(
            new BoundedChannelOptions(Math.Max(maxBatchSize * 4, 16))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        // 逐条订阅：ack 通路走四参重载把 broker 的真实确认器带进通道；旧通路仍走三参重载，
        // 传 null 表示「本次交付无需结算」——两条通路的收件逻辑因此只差 Ack 字段。
        ValueTask EnqueueAsync(TMessage message, MessageConsumeContext? context, IMessageAck? ack, CancellationToken token)
            => channel.Writer.WriteAsync(new PumpItem<TMessage>(message, context, ack), token);

        var inner = propagateAcks
            ? await broker.SubscribeAsync<TMessage>(
                (message, context, ack, token) => EnqueueAsync(message, context, ack, token),
                ct).ConfigureAwait(false)
            : await broker.SubscribeAsync<TMessage>(
                (message, context, token) => EnqueueAsync(message, context, null, token),
                ct).ConfigureAwait(false);

        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = Task.Run(
            () => PumpAsync(channel.Reader, handler, maxBatchSize, delay, onHandlerError, pumpCts.Token),
            pumpCts.Token);
        return new BatchSubscription(inner, () => channel.Writer.TryComplete(), pumpCts, pump);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "批量 handler 的任意异常由 onHandlerError 记录后继续后续批次，与逐条 broker 消费循环的异常隔离契约一致。")]
    private static async Task PumpAsync<TMessage>(
        ChannelReader<PumpItem<TMessage>> reader,
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, IMessageAck, CancellationToken, ValueTask> handler,
        int maxBatchSize,
        TimeSpan maxDelay,
        Action<Exception> onHandlerError,
        CancellationToken ct)
    {
        // batch 是暴露给 handler 的视图，batchAcks 与之同序（用于聚合批次级确认器）。
        // 两者在每轮 finally 中清空复用，不随批数增长。
        var batch = new List<ConsumedMessage<TMessage>>(maxBatchSize);
        var batchAcks = new List<IMessageAck>(maxBatchSize);
        Task<PumpItem<TMessage>>? pendingRead = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PumpItem<TMessage> first;
                if (pendingRead is not null)
                {
                    first = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                }
                else
                {
                    try
                    {
                        first = await reader.ReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }
                }

                Add(first, batch, batchAcks);
                // 单调时间戳而非墙钟：组批窗口只度量相对间隔，不受系统时钟调整影响。
                var deadline = Stopwatch.GetTimestamp() + (long)(maxDelay.TotalSeconds * Stopwatch.Frequency);
                while (batch.Count < maxBatchSize)
                {
                    if (reader.TryRead(out var next))
                    {
                        Add(next, batch, batchAcks);
                        continue;
                    }

                    // 单读者语义：挂起读只能有一个。时间界先到时保留 pendingRead，
                    // 下一轮作为首条消费，避免遗留读任务吞掉下一条消息。
                    pendingRead = reader.ReadAsync(ct).AsTask();
                    var remainingTicks = deadline - Stopwatch.GetTimestamp();
                    if (remainingTicks <= 0)
                        break;

                    var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
                    var delayTask = Task.Delay(remaining, ct);
                    var completed = await Task.WhenAny(pendingRead, delayTask).ConfigureAwait(false);
                    if (completed != pendingRead)
                        break;

                    Add(await pendingRead.ConfigureAwait(false), batch, batchAcks);
                    pendingRead = null;
                }

                var batchAck = batchAcks.Count == 0
                    ? MessageBatchAck.Empty
                    : new MessageBatchAck([.. batchAcks]);
                try
                {
                    await handler(batch, batchAck, ct).ConfigureAwait(false);
                    // 兜底（批次 A）：handler 未显式结算时整批确认。空确认器（旧重载路径）
                    // 下这是无操作，因此旧行为逐字不变。
                    if (!batchAck.IsSettled)
                        await batchAck.AcknowledgeAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    onHandlerError(ex);
                    // 兜底：整批弃置，与逐条路径的 at-most-once 默认一致；需要重投须由
                    // handler 自己 catch 后显式 RejectAsync(requeue: true)。
                    try
                    {
                        if (!batchAck.IsSettled)
                            await batchAck.RejectAsync(requeue: false, ct: CancellationToken.None).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // 弃置动作的失败不得掩盖原始 handler 异常
#pragma warning disable RCS1075 // 空 catch 是有意设计：原始 handler 异常已交给 onHandlerError，此处吞掉结算失败
                    catch (Exception)
#pragma warning restore RCS1075
#pragma warning restore CA1031
                    {
                        // 结算失败（channel 已关/连接断开）由 broker 侧自身日志记录，
                        // 此处不得再抛——原始 handler 异常已交给 onHandlerError。
                    }
                }
                finally
                {
                    batch.Clear();
                    batchAcks.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常关停。
        }
        catch (ChannelClosedException)
        {
            // 订阅先于取消结束通道：正常收尾。
        }
        finally
        {
            if (pendingRead is not null)
            {
                try
                {
                    await pendingRead.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException)
                {
                    // 关停路径：待读任务的取消/通道关闭不阻止订阅释放。
                }
            }
        }
    }

    private static void Add<TMessage>(
        PumpItem<TMessage> item,
        List<ConsumedMessage<TMessage>> batch,
        List<IMessageAck> batchAcks)
    {
        batch.Add(new ConsumedMessage<TMessage>(item.Message, item.Context));
        if (item.Ack is not null)
            batchAcks.Add(item.Ack);
    }

    private sealed class BatchSubscription(
        IAsyncDisposable inner,
        Action completeWriter,
        CancellationTokenSource pumpCts,
        Task pump) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // 先停止逐条订阅（不再有新消息），再取消泵并完成通道，最后等待泵收尾。
            await inner.DisposeAsync().ConfigureAwait(false);
            await pumpCts.CancelAsync().ConfigureAwait(false);
            completeWriter();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 泵在启动前被取消：正常关停。
            }
            pumpCts.Dispose();
        }
    }
}
