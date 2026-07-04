// ─────────────────────────────────────────────────────────────
// 📡 IMessageBroker + NullMessageBroker — 消息代理抽象
// ─────────────────────────────────────────────────────────────

using PalDDD.Core.Logging;
using PalDDD.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging;

// ─────────────────────────────────────────────────────────────
// 跨进程消息代理抽象
// ─────────────────────────────────────────────────────────────

/// <summary>跨进程消息代理抽象 — AOT 安全的消息发布与订阅。</summary>
public interface IMessageBroker
{
    /// <summary>发布消息（泛型，编译时已知类型）</summary>
    ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default);

    /// <summary>发布消息（非泛型，使用预注册消息描述符和外部消息 ID）</summary>
    ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default);

    /// <summary>发布消息（非泛型，使用预注册消息描述符、外部消息 ID 和跨上下文追踪元数据）</summary>
    ValueTask PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
        => PublishAsync(message, descriptor, messageId, ct);

    /// <summary>异步订阅消息 — 完全异步，零 Task.Run，零死锁风险</summary>
    ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default);
}

/// <summary>
/// 空实现 — 单节点部署时使用，不发送任何消息。<br/>
/// Debug 级日志记录发布/订阅调用，便于诊断"消息未发送"问题（如误注册 NullMessageBroker）。
/// </summary>
public sealed class NullMessageBroker : IMessageBroker
{
    private readonly IPalLogger<NullMessageBroker>? _logger;

    /// <summary>创建 NullMessageBroker，可选注入日志器。</summary>
    public NullMessageBroker(IPalLogger<NullMessageBroker>? logger = null)
        => _logger = logger;

    ValueTask IMessageBroker.PublishAsync<TMessage>(TMessage message, CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: publish of '{typeof(TMessage).Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBroker.PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: publish of '{descriptor.Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBroker.PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: publish of '{descriptor.Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask<IAsyncDisposable> IMessageBroker.SubscribeAsync<TMessage>(Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: subscribe to '{typeof(TMessage).Name}' ignored (single-node mode)");
        return new ValueTask<IAsyncDisposable>(NullAsyncDisposable.Instance);
    }

    private sealed class NullAsyncDisposable : IAsyncDisposable
    {
        public static readonly NullAsyncDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
