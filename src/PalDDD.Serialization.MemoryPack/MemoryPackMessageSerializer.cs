// ─────────────────────────────────────────────────────────────
// 📦 MemoryPackMessageSerializer — MemoryPack 二进制序列化器
// ─────────────────────────────────────────────────────────────
// AOT 限制：非泛型 Serialize(object) 路径依赖运行时 Type 查找 Formatter，
//           NativeAOT 下需 [MemoryPackable] 源生成器支持；泛型 API 才是 AOT 友好路径。
// 与 JsonMessageSerializer 并行，通过 DI 切换。
// ─────────────────────────────────────────────────────────────

using MemoryPack;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Serialization.MemoryPack;

/// <summary>
/// MemoryPack 二进制消息序列化器。<br/>
/// 泛型路径无反射、配合 <c>[MemoryPackable]</c> 源生成器可 AOT 工作，比 JSON 快 3-5x、payload 小 2-4x；
/// 非泛型 <c>object</c> 重载走运行时类型查找，<b>非</b> AOT 安全。<br/>
/// 通过 <c>AddPalMemoryPackSerialization()</c> 注册，替换默认 JSON 序列化器。
/// </summary>
/// <remarks>
/// ⚠️ v65 P3（AOT 声明勘正）：本类型所在包 <c>IsAotCompatible=false</c>（csproj）——本类
/// <b>不声明整包 NativeAOT 兼容</b>（源生成器全链路验证未完成，见 P0 硬约束 #3）。上文"泛型路径
/// 可 AOT 工作"指该路径无反射调用，不等同于本类的 AOT 兼容性承诺；原摘要"（整类）AOT 兼容、
/// 零反射"与非泛型路径的反射事实及包级设置矛盾，已收窄。
/// 📐 与 JsonMessageSerializer 互斥注册（均为 IMessageSerializer Singleton）。
/// 💡 泛型路径使用 <c>MemoryPackSerializer.Serialize&lt;T&gt;()</c>，编译时类型安全。
/// 💡 非泛型路径使用 <c>MemoryPackSerializer.Serialize(message.GetType(), message)</c>——
/// 按运行时类型直接序列化；未标 <c>[MemoryPackable]</c> 的派生类型会失败，无 descriptor
/// 类型回退（2026-10-21 勘正：原文「+ descriptor 回退」不存在）。
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
        // v41 P3（对齐 Json 姊妹 v25 C7）：补 null 守卫——null 引用消息交给 MemoryPack 会静默
        // 产出 nil 载荷（反序列化得 null，错误远离序列化点），现入口抛 ArgumentNullException。
        // TMessage 为值类型时 ThrowIfNull 为 no-op（JIT 消除）
        ArgumentNullException.ThrowIfNull(message);

        // P3 修复（八轮评审）：泛型路径 descriptor 可选——非 null 时校验 ContentType 断链
        if (descriptor is not null)
            ValidateDescriptorContentType(descriptor);

        return MemoryPackSerializer.Serialize(message);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ITM-167 声明（非泛型路径）：本重载以 <c>MemoryPackSerializer.Serialize(runtimeType, message)</c>
    /// 运行时类型查找 Formatter——非 AOT 友好路径。在裁剪/NAOT 发布下，运行时类型若未在
    /// 源码生成器静态注册（[MemoryPackable]），序列化会抛 <see cref="InvalidOperationException"/>
    /// 或 MissingFormatterException（安全失败，不静默降级）。AOT 场景必须走泛型
    /// <see cref="Serialize{TMessage}"/> 路径（编译时类型安全）。错误消息见下方运行时类型提示。
    /// </remarks>
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

    // R8/P1-1（ADR-029，2026-10-14）：本实现**不覆写**直写重载 Serialize(object, descriptor,
    // IBufferWriter<byte>)——MemoryPack 1.21.4 的 IBufferWriter 重载仅有泛型形态
    //（Serialize<T>(writer, value)），无 (writer, Type, object) 非 AOT 路径对应重载
    //（本地编译实证 CS1501/CS1503）；T=object 泛型动态序列化语义未经探针验证（P0 #4
    // 纪律不臆测）。走接口 DIM 默认回退（内存形态 + 单次拷贝进 destination），语义与
    // 非泛型内存重载严格一致；原生直写待验证后跟进（ADR-029 延后段）。

    /// <inheritdoc />
    /// <remarks>
    /// ITM-167 声明（非泛型路径）：本重载以 <c>MemoryPackSerializer.Deserialize(descriptor.ClrType, payload)</c>
    /// 运行时类型查找 Formatter——同 <see cref="Serialize(object, MessageDescriptor)"/> 的
    /// 裁剪/NAOT 降级声明：运行时类型未静态注册时安全失败（抛异常），AOT 场景走泛型
    /// <see cref="Deserialize{TMessage}"/> 路径。
    /// </remarks>
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
        // v39 P3：补 null 守卫（对齐姊妹非泛型 Deserialize 同款）——原 null descriptor
        // 静默放行（ContentType 校验被 if 跳过、反序列化本体不使用 descriptor 照常成功），
        // 与接口契约（descriptor 必传）不符，null 应入口快速失败
        ArgumentNullException.ThrowIfNull(descriptor);
        // P3 修复（八轮评审）：ContentType 断链入口校验——descriptor 未参与反序列化本体，
        // 但传入错误 descriptor 应尽早暴露（v39 P3 起 null 已由上方守卫排除）
        ValidateDescriptorContentType(descriptor);

        return MemoryPackSerializer.Deserialize<TMessage>(payload);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MemoryPackSerializer.Serialize 返回独立 byte[]；命中整段数组时直接交还，
    /// 避免 Outbox 持久化边界的第二次整载荷复制。
    /// </remarks>
    public byte[] SerializeToArray(object message, MessageDescriptor descriptor)
    {
        var payload = Serialize(message, descriptor);
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(payload, out var segment)
            && segment.Array is { } array
            && segment.Offset == 0
            && segment.Count == array.Length)
            return array;
        return payload.ToArray();
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
