// ─────────────────────────────────────────────────────────────
// 📝 IMessageSerializer — 消息序列化抽象（AOT 安全）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Serialization;

// ─────────────────────────────────────────────────────────────
// 消息序列化接口
// ─────────────────────────────────────────────────────────────

/// <summary>持久化消息的序列化与反序列化抽象。</summary>
public interface IMessageSerializer
{
    /// <summary>此序列化器产出的内容类型。</summary>
    string ContentType { get; }

    /// <summary>序列化强类型消息。</summary>
    ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null);

    /// <summary>序列化运行时类型已知的消息。</summary>
    ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor);

    /// <summary>使用描述符元数据反序列化消息负载。</summary>
    object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor);

    /// <summary>反序列化强类型消息。值类型零装箱。</summary>
    TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor);
}
