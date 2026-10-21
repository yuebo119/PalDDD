// ─────────────────────────────────────────────────────────────
// 📦 MessageBatchPump — 批量订阅的组批泵（数量界 + 时间界）
// ─────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.Threading.Channels;

namespace PalDDD.Messaging;

/// <summary>
/// 批量订阅的组批泵 — 复用逐条 <see cref="IMessageBroker.SubscribeAsync{TMessage}(Func{TMessage, MessageConsumeContext?, CancellationToken, ValueTask}, CancellationToken)"/>，
/// 按数量或时间界组批后交给批量 handler。批内消息按到达顺序；批列表在 flush 后清空复用。
/// </summary>
internal static class MessageBatchPump
{
    public static async ValueTask<IAsyncDisposable> StartAsync<TMessage>(
        IMessageBroker broker,
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler,
        int maxBatchSize,
        TimeSpan? maxDelay,
        Action<Exception> onHandlerError,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(onHandlerError);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
        var delay = maxDelay ?? TimeSpan.FromMilliseconds(50);
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "maxDelay must be greater than zero.");

        var channel = Channel.CreateBounded<ConsumedMessage<TMessage>>(
            new BoundedChannelOptions(Math.Max(maxBatchSize * 4, 16))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        var inner = await broker.SubscribeAsync<TMessage>(
            async (message, context, token) =>
            {
                await channel.Writer.WriteAsync(
                    new ConsumedMessage<TMessage>(message, context), token).ConfigureAwait(false);
            },
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
        ChannelReader<ConsumedMessage<TMessage>> reader,
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler,
        int maxBatchSize,
        TimeSpan maxDelay,
        Action<Exception> onHandlerError,
        CancellationToken ct)
    {
        var batch = new List<ConsumedMessage<TMessage>>(maxBatchSize);
        Task<ConsumedMessage<TMessage>>? pendingRead = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumedMessage<TMessage> first;
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

                batch.Add(first);
                // 单调时间戳而非墙钟：组批窗口只度量相对间隔，不受系统时钟调整影响。
                var deadline = Stopwatch.GetTimestamp() + (long)(maxDelay.TotalSeconds * Stopwatch.Frequency);
                while (batch.Count < maxBatchSize)
                {
                    if (reader.TryRead(out var next))
                    {
                        batch.Add(next);
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

                    batch.Add(await pendingRead.ConfigureAwait(false));
                    pendingRead = null;
                }

                try
                {
                    await handler(batch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    onHandlerError(ex);
                }
                finally
                {
                    batch.Clear();
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
            pumpCts.Cancel();
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
