// ─────────────────────────────────────────────────────────────
// 📡 IMessageAck — 消费确认契约（批次 A，2026-11-04）
// ─────────────────────────────────────────────────────────────
//
// 💡 背景：旧订阅形态（handler 不携带确认能力）只能提供 at-most-once。
//   ｜ 需要 at-least-once 时，调用方无从表达「这条处理完了」与「这条要重投」。
//   ｜ 本契约把「消费确认」补成订阅契约里的一个维度，而非让调用方自行实现 broker。
//
// ✅ AOT 安全：纯接口 + 两个无状态实现，零反射。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Messaging;

/// <summary>
/// 单条消息的消费确认器 —— 由 broker 在交付时提供，handler 决定何时确认或拒绝。
/// </summary>
/// <remarks>
/// ⚠️ <b>默认语义不因本契约改变</b>：不调用任何方法时，两个内置 broker 的行为与
/// v5.0.0-preview.1 逐字一致（RabbitMQ 成功自动 ack / 异常 nack 弃置；Kafka
/// offset 按自动提交推进、失败消息丢弃）。要真正获得 at-least-once 必须同时满足：
/// ① 使用携带 <see cref="IMessageAck"/> 的订阅重载；② Kafka 侧把
/// <c>ConsumerConfig.EnableAutoOffsetStore</c> 设为 <c>false</c>（否则
/// <see cref="AcknowledgeAsync"/> 为无操作，保持自动存储语义）。
/// <para>
/// 一个确认器只对<b>一次交付</b>有效，且只应结算一次（重复结算被实现忽略并留
/// Warning，不抛异常——消费循环内的异常会打断投递）。
/// </para>
/// </remarks>
public interface IMessageAck
{
    /// <summary>
    /// 处理成功，允许推进消费位置。<br/>
    /// Kafka：手动存储模式（<c>EnableAutoOffsetStore=false</c>）下存储该条 offset；
    /// 否则无操作。RabbitMQ：<c>BasicAck</c>。
    /// </summary>
    ValueTask AcknowledgeAsync(CancellationToken ct = default);

    /// <summary>
    /// 处理失败。<paramref name="requeue"/> 为<c>true</c> 请求重投（Kafka 用
    /// <c>Seek</c> 回到本条 offset 并指数退避；RabbitMQ 用 <c>Nack(requeue:true)</c>）；
    /// 为<c>false</c> 表示放弃该消息（Kafka 存储 offset 跳过；RabbitMQ <c>Nack(requeue:false)</c>）。
    /// </summary>
    ValueTask RejectAsync(bool requeue, CancellationToken ct = default);
}

/// <summary>
/// 无操作确认器 —— 供不携带确认能力的旧订阅重载适配使用。
/// </summary>
/// <remarks>
/// 旧重载（<c>Func&lt;TMessage, CancellationToken, ValueTask&gt;</c> 与含 context 的双参形态）
/// 经默认接口实现转发到携带确认器的重载时传入本实例，因此旧调用方的可观测行为零变化：
/// 两个方法都不抛、不产生任何 broker 侧副作用。
/// </remarks>
public sealed class NoOpMessageAck : IMessageAck
{
    /// <summary>共享单例（无状态）。</summary>
    public static readonly NoOpMessageAck Instance = new();

    /// <inheritdoc />
    /// <remarks>
    /// 刻意<b>不</b>检查 / 抛出取消：本类型的全部职责是「让旧重载的调用方观察不到任何确认
    /// 副作用」，包括异常。若在此抛 <see cref="OperationCanceledException"/>，旧重载路径
    /// 会在 handler 成功返回后凭空多出一条取消异常，破坏「旧重载行为零变化」的验收条件。
    /// </remarks>
    public ValueTask AcknowledgeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    /// <remarks>不抛语义同 <see cref="AcknowledgeAsync"/>。</remarks>
    public ValueTask RejectAsync(bool requeue, CancellationToken ct = default) => ValueTask.CompletedTask;
}

/// <summary>
/// 批次级确认器 —— 一个实例对应一整批交付，委托给逐条确认器。
/// </summary>
/// <remarks>
/// <para>
/// <b>逆序委派（<see cref="RejectAsync"/>）</b>：Kafka 的 <c>Seek</c> 语义为「最后一次生效」，
/// 若按批内顺序逐条 seek，最终位置会落在最后一条而不是最早一条，导致重投只回放尾部。
/// 逆序委派使最后一次 seek 指向批内最早 offset，整批重新投递；RabbitMQ 每个 delivery 独立
/// nack，与顺序无关，逆序同样正确。
/// </para>
/// <para>
/// <b>批次级单次结算</b>：整批要么全部确认要么全部拒绝，不支持批内部分确认——批量 handler
/// 的契约是「整批成功或整批失败」，逐条差异化处置应使用逐条订阅重载。
/// </para>
/// </remarks>
internal sealed class MessageBatchAck(IMessageAck[] inner) : IMessageAck
{
    /// <summary>
    /// 零子确认器的共享实例——批量泵在旧重载路径（逐条订阅不带确认器）复用，避免每批
    /// 分配一个空数组。两个方法在空集上立即返回且**不占用** <c>_settled</c>，因此
    /// 同一实例可跨批次安全共享（无状态可泄漏）。
    /// </summary>
    public static readonly MessageBatchAck Empty = new([]);

    private readonly IMessageAck[] _inner = inner;
    private int _settled;

    /// <summary>是否已被显式结算（批量泵据此决定兜底动作）。</summary>
    public bool IsSettled => Volatile.Read(ref _settled) != 0;

    /// <inheritdoc />
    public async ValueTask AcknowledgeAsync(CancellationToken ct = default)
    {
        if (_inner.Length == 0 || Interlocked.Exchange(ref _settled, 1) != 0)
            return;

        for (var i = 0; i < _inner.Length; i++)
            await _inner[i].AcknowledgeAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RejectAsync(bool requeue, CancellationToken ct = default)
    {
        if (_inner.Length == 0 || Interlocked.Exchange(ref _settled, 1) != 0)
            return;

        for (var i = _inner.Length - 1; i >= 0; i--)
            await _inner[i].RejectAsync(requeue, ct).ConfigureAwait(false);
    }
}
