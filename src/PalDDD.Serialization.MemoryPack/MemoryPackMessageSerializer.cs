// ─────────────────────────────────────────────────────────────
// 📦 MemoryPackMessageSerializer — MemoryPack 二进制序列化器
// ─────────────────────────────────────────────────────────────
// AOT 安全：MemoryPack 内置 source generator，零反射。
// 与 JsonMessageSerializer 并行，通过 DI 切换。
// ─────────────────────────────────────────────────────────────

using MemoryPack;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Serialization.MemoryPack;

/// <summary>
/// MemoryPack 二进制消息序列化器。<br/>
/// AOT 兼容、零反射、比 JSON 快 3-5x、payload 小 2-4x。<br/>
/// 通过 <c>AddPalMemoryPackSerialization()</c> 注册，替换默认 JSON 序列化器。
/// </summary>
/// <remarks>
/// 📐 与 JsonMessageSerializer 互斥注册（均为 IMessageSerializer Singleton）。
/// 💡 泛型路径使用 <c>MemoryPackSerializer.Serialize&lt;T&gt;()</c>，编译时类型安全。
/// 💡 非泛型路径使用 <c>MemoryPackSerializer.Serialize(value.GetType())</c> + descriptor 回退。
/// </remarks>
public sealed class MemoryPackMessageSerializer : IMessageSerializer
{
    public MemoryPackMessageSerializer()
    {
    }

    /// <inheritdoc />
    public string ContentType => ContentTypes.MemoryPack;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null)
    {
        return MemoryPackSerializer.Serialize(message);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(descriptor);

        // 使用运行时类型序列化——MemoryPack 内部查找注册的 Formatter
        var bytes = MemoryPackSerializer.Serialize(message.GetType(), message);
        return bytes ?? throw new InvalidOperationException(
            $"MemoryPack serialization failed for type '{descriptor.ClrType.FullName}'. " +
            "Ensure the type is registered with [MemoryPackable] and a MemoryPack generator.");
    }

    /// <inheritdoc />
    public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return MemoryPackSerializer.Deserialize(descriptor.ClrType, payload);
    }

    /// <inheritdoc />
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
    public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
    {
        return MemoryPackSerializer.Deserialize<TMessage>(payload);
    }
}
