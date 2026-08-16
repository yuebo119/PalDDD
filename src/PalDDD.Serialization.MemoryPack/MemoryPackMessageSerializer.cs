// ─────────────────────────────────────────────────────────────
// 📦 MemoryPackMessageSerializer — MemoryPack 二进制序列化器
// ─────────────────────────────────────────────────────────────
// AOT 限制：非泛型 Serialize(object) 路径依赖运行时 Type 查找 Formatter，
//           NativeATO 下需 [MemoryPackable] 源生成器支持；泛型 API 才是 AOT 友好路径。
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
/// <para>
/// 📐 <b>契约（八轮评审 P2 修复）</b>：传入非 null <see cref="MessageDescriptor"/> 时，
/// 其 <c>ContentType</c> 必须为 <see cref="ContentTypes.MemoryPack"/>，否则抛
/// <see cref="InvalidOperationException"/>——ContentType 断链（如沿用 Json 默认值）会让
/// 消费方按错误格式解析 payload，入口显式校验将失败提前到序列化时刻。
/// 注册 descriptor 时需显式传 <c>contentType: ContentTypes.MemoryPack</c>。
/// </para>
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
        // P3 修复（八轮评审）：泛型路径 descriptor 可选——非 null 时校验 ContentType 断链
        if (descriptor is not null)
            ValidateDescriptorContentType(descriptor);

        return MemoryPackSerializer.Serialize(message);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(descriptor);
        // P3 修复（八轮评审）：ContentType 断链入口校验（详见类 remarks 契约说明）
        ValidateDescriptorContentType(descriptor);

        // 使用运行时类型序列化——MemoryPack 的多态序列化依赖运行时类型的 Formatter
        var runtimeType = message.GetType();
        var bytes = MemoryPackSerializer.Serialize(runtimeType, message);
        // P3 修复（八轮评审）：错误消息改报实际尝试的运行时类型（原报 descriptor.ClrType，
        // 多态场景下与真实序列化类型不一致，误导排查）
        return bytes ?? throw new InvalidOperationException(
            $"MemoryPack serialization failed for runtime type '{runtimeType.FullName}' " +
            $"(descriptor CLR type '{descriptor.ClrType.FullName}'). " +
            "Ensure the type is registered with [MemoryPackable] and a MemoryPack generator.");
    }

    /// <inheritdoc />
    public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        // P3 修复（八轮评审）：ContentType 断链入口校验
        ValidateDescriptorContentType(descriptor);

        return MemoryPackSerializer.Deserialize(descriptor.ClrType, payload);
    }

    /// <inheritdoc />
    // P3 修复（二十一轮）：DAM 标注位置勘正——[return: DynamicallyAccessedMembers] 标注在
    // 返回值上无效（返回的 TMessage? 实例不作为反射目标，trimmer 对返回值不追踪成员保留），
    // 移到类型参数声明：TMessage 流入 MemoryPack 的运行时 Formatter 解析路径，其构造函数
    // 成员须被 trimmer 保留，意图声明在泛型参数上才被 ILLink 消费。
    public TMessage? Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] TMessage>(
        ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
    {
        // P3 修复（八轮评审）：泛型路径 descriptor 未参与反序列化，但非 null 时仍校验
        // ContentType 断链（调用方传入错误 descriptor 应尽早暴露）
        if (descriptor is not null)
            ValidateDescriptorContentType(descriptor);

        return MemoryPackSerializer.Deserialize<TMessage>(payload);
    }

    /// <summary>
    /// 校验 descriptor 的 ContentType 与本序列化器匹配——不匹配时抛
    /// <see cref="InvalidOperationException"/>，将"注册时漏传 contentType"的断链
    /// 失败从"消费方解析失败"提前到"序列化入口"。
    /// </summary>
    private static void ValidateDescriptorContentType(MessageDescriptor descriptor)
    {
        if (descriptor.ContentType != ContentTypes.MemoryPack)
            throw new InvalidOperationException(
                $"MessageDescriptor '{descriptor.Name}' was registered with ContentType " +
                $"'{descriptor.ContentType}', but MemoryPackMessageSerializer requires " +
                $"'{ContentTypes.MemoryPack}'. Pass contentType: ContentTypes.MemoryPack " +
                "when creating the MessageDescriptor.");
    }
}
