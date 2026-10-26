// ─────────────────────────────────────────────────────────────
// 🔔 IOutboxWakeUpSignal — 出站投递的进程内唤醒信号
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要它？
//   ｜ OutboxProcessor 默认按 PollInterval（默认 500ms）轮询。写入方把消息落库后，
//   ｜ 消息平均要等 pollInterval/2、最坏等满一个间隔才开始租约与投递。
//   ｜ 进程内的写入方本来就知道「刚写了消息」，把这一事实直接告诉处理循环，
//   ｜ 端到端延迟就从「半个轮询周期」降到「一次租约往返 + 一次发布」。
//
// 💡 为什么不做成投递保证？
//   ｜ 跨进程写入（另一实例写同一张表）不会触发本进程的信号；信号丢失、合并、重复
//   ｜ 都不影响正确性（一次批处理取走当前全部可租消息）。真正兜底仍是轮询。
//
// ✅ AOT 安全：纯同步原语，零反射。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// 出站投递唤醒信号（2026-10-22 性能轮）——写入方在 Outbox 语句执行后通知处理循环立即续批，
/// 把「等下一轮 <see cref="OutboxOptions.PollInterval"/>」的延迟（默认 500ms 量级）压到一次
/// 租约往返加一次发布的时间。
/// </summary>
/// <remarks>
/// <para>
/// <b>语义边界</b>：本信号是同进程、尽力而为的加速通道，<b>不是投递保证</b>。跨进程写入不会
/// 触发本地信号，消息仍由 <see cref="OutboxOptions.PollInterval"/> 轮询兜底；信号丢失、重复、
/// 合并都安全（处理循环一次批处理会取走当前全部可租消息）。
/// </para>
/// <para>
/// <b>事务时序</b>：调用方把 Outbox 写入包在自己的事务里时，信号可能在提交前到达，处理循环
/// 那一轮租不到消息。<see cref="OutboxProcessor"/> 对此自愈（被信号唤醒但租到 0 条时用短间隔
/// 重试一次），因此实现方不必等待提交即可通知。
/// </para>
/// <para>
/// <b>实现要求</b>：<see cref="Notify"/> 必须可从任意线程调用且不阻塞；
/// <see cref="WaitAsync"/> 必须保持「到时返回 false、取消抛 <see cref="OperationCanceledException"/>」
/// 的计时语义，处理循环依赖它与轮询间隔等价。
/// </para>
/// </remarks>
public interface IOutboxWakeUpSignal
{
    /// <summary>通知处理循环「可能有新消息可投递」。可从任意线程调用，不阻塞。</summary>
    void Notify();

    /// <summary>
    /// 等待下一次通知，最长等待 <paramref name="maxWait"/>。
    /// </summary>
    /// <param name="maxWait">最长等待时长（调用方保证为正；实现须钳到计时器上限内）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns><c>true</c> = 被通知唤醒；<c>false</c> = 等待超时（调用方按正常轮询继续）。</returns>
    ValueTask<bool> WaitAsync(TimeSpan maxWait, CancellationToken ct);
}

/// <summary>
/// <see cref="IOutboxWakeUpSignal"/> 的默认实现：容量 1 的信号量。
/// </summary>
/// <remarks>
/// 容量 1 而非计数队列：消费者每轮取走当前全部可租消息，多发一次通知没有额外信息量。
/// <see cref="Notify"/> 在已有未消费信号时不重复 Release（合并语义）。
/// </remarks>
internal sealed class OutboxWakeUpSignal : IOutboxWakeUpSignal, IDisposable
{
    /// <summary><see cref="SemaphoreSlim"/> 计时器上限：int.MaxValue-1 毫秒（约 24.8 天）。</summary>
    private static readonly TimeSpan s_maxWait = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        // CurrentCount 预检只是省一次异常路径的尝试；并发下两线程都可能看到 0 并各自
        // Release，第二个抛 SemaphoreFullException，属预期合并语义（吞掉）。
        if (_signal.CurrentCount != 0)
            return;

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // 停机路径：信号已释放，唤醒语义由宿主关停取代。
        }
        catch (SemaphoreFullException)
        {
            // 并发 Notify 的合并：已有一枚未消费信号。
        }
    }

    public ValueTask<bool> WaitAsync(TimeSpan maxWait, CancellationToken ct)
    {
        if (maxWait > s_maxWait)
            maxWait = s_maxWait;
        return new ValueTask<bool>(_signal.WaitAsync(maxWait, ct));
    }

    public void Dispose() => _signal.Dispose();
}
