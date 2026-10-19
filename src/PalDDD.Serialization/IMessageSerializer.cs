// ─────────────────────────────────────────────────────────────
// 📝 IMessageSerializer — 消息序列化抽象（AOT 安全）
// ─────────────────────────────────────────────────────────────
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    /// 序列化运行时类型已知的消息到调用方提供的缓冲区（R8/P1-1，ADR-029，2026-10-14
    /// 性能实施轮）——<b>直写零拷贝形态</b>：实现把输出直接写入 <paramref name="destination"/>，
    /// 免除内存重载（<see cref="Serialize(object, MessageDescriptor)"/>）在返回前的
    /// 精确尺寸数组分配 + 整载荷拷贝（每消息一笔，量级 = payload 尺寸）。
    /// <para>
    /// DIM 默认实现回退内存形态（一次拷贝进 destination）——自定义序列化器零迁移成本；
    /// <c>JsonMessageSerializer</c>（池化 Utf8JsonWriter 直写）覆写为真零拷贝。
    /// <c>MemoryPackMessageSerializer</c> 当前<b>不覆写</b>（走 DIM 默认回退）：MemoryPack
    /// 1.21.4 的 IBufferWriter 重载仅有泛型形态，无 (writer, Type, object) 非 AOT 路径
    /// 对应重载，T=object 动态序列化语义未验证——原生直写待验证后跟进（ADR-029 延后段）。
    /// 勘误（EPSL 地毯审计 2026-10-16 A 批 #26）：原注释称其「（原生 writer 重载）覆写为
    /// 真零拷贝」与实现的明确不覆写声明直接矛盾，自定义序列化器作者据此做迁移决策会被误导。
    /// 消费端：<c>RabbitMqBroker</c> 以 TLS 池缓冲 + <c>WrittenMemory</c> 直传发布路径
    /// （Kafka 受 Confluent API 的 byte[] 契约约束不适用，见其发布处注释）。
    /// </para>
    /// <para>⚠️ <b>缓冲生命周期契约</b>：<paramref name="destination"/> 的已写内容在调用
    /// 返回后立即可读，且本调用不持有其引用（Json 实现复用 TLS Utf8JsonWriter 经
    /// <c>Reset(destination)</c> 直写——同线程嵌套序列化的既有非重入限制同样适用，
    /// 见 JsonMessageSerializer 类头声明）。</para>
    /// </summary>
    void Serialize(object message, MessageDescriptor descriptor, System.Buffers.IBufferWriter<byte> destination)
    {
        // DIM 默认：内存形态 + 单次拷贝（对既有 Serialize 实现零破坏）
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(destination);
        var payload = Serialize(message, descriptor);
        destination.Write(payload.Span);
    }

    /// <summary>使用描述符元数据反序列化消息负载。</summary>
    object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor);

    /// <summary>反序列化强类型消息。值类型零装箱。</summary>
    TMessage? Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] TMessage>(
        ReadOnlySpan<byte> payload, MessageDescriptor descriptor);
    // P3 修复（二十三轮验证轮 V3）：接口泛型参数补 DAM 标注——实现侧（MemoryPack）的标注
    // 经接口虚调用不传播，trimmer 不为 TMessage 保留构造函数（半截修复补全）
}
