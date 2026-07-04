// ─────────────────────────────────────────────────────────────
// 📝 JsonMessageSerializer — JSON 序列化实现（JsonTypeInfo 强类型）
// ─────────────────────────────────────────────────────────────
using System.Buffers;
using System.Text.Json;

namespace PalDDD.Serialization.Json;

// ─────────────────────────────────────────────────────────────
// JSON 消息序列化器 — AOT-first，支持 GetTypeInfo<T>() 强类型路径 + 池化 Writer
// ─────────────────────────────────────────────────────────────

/// <summary>
/// System.Text.Json 消息序列化器，AOT 兼容，零反射。<br/>
/// 支持通过 <see cref="JsonSerializerOptions"/> 注入实现 .NET 11 的强类型
/// <c>GetTypeInfo&lt;T&gt;()</c> 路径，消除值类型序列化的装箱开销。<br/>
/// 通过 <c>Utf8JsonWriter.Reset(IBufferWriter&lt;byte&gt;)</c> 复用 Writer，减少热路径分配。
/// </summary>
public sealed class JsonMessageSerializer : IMessageSerializer
{
    private readonly IMessageCatalog _messageCatalog;
    private readonly JsonSerializerOptions? _options;

    // ThreadLocal Writer 池 — 每线程最多缓存一个 Utf8JsonWriter 和 ArrayBufferWriter
    [ThreadStatic]
    private static Utf8JsonWriter? _tlsWriter;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _tlsBufferWriter;

    /// <summary>
    /// 使用默认构造路径创建序列化器。<br/>
    /// 泛型方法通过 <c>(JsonTypeInfo&lt;TMessage&gt;)descriptor.JsonTypeInfo</c>
    /// 强制转换获取 metadata，对引用类型无额外开销。
    /// </summary>
    /// <param name="messageCatalog">消息元数据目录，用于无 descriptor 参数的重载</param>
    public JsonMessageSerializer(IMessageCatalog messageCatalog)
    {
        ArgumentNullException.ThrowIfNull(messageCatalog);
        _messageCatalog = messageCatalog;
        _options = null; // 走原始强制转换路径
    }

    /// <summary>
    /// 使用指定 <paramref name="options"/> 创建序列化器。<br/>
    /// .NET 11 上 <c>options.GetTypeInfo&lt;TMessage&gt;()</c> 返回强类型
    /// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{TMessage}"/>，
    /// 对值类型消息实现零装箱。<br/>
    /// 同时使用 <c>Utf8JsonWriter.Reset()</c> 复用 Writer，减少热路径分配。
    /// </summary>
    /// <param name="messageCatalog">消息元数据目录</param>
    /// <param name="options">JSON 序列化选项，必须使用源生成上下文实例</param>
    public JsonMessageSerializer(IMessageCatalog messageCatalog, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(messageCatalog);
        ArgumentNullException.ThrowIfNull(options);

        _messageCatalog = messageCatalog;
        _options = options;
    }

    /// <inheritdoc />
    public string ContentType => ContentTypes.Json;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null)
    {
        descriptor ??= _messageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");

        if (_options is not null)
        {
            // .NET 11 强类型路径：GetTypeInfo<T>() 返回 JsonTypeInfo<TMessage>，零装箱
            var typedInfo = _options.GetTypeInfo<TMessage>();
            return SerializePooled(message, typedInfo);
        }

        // 默认路径：通过 descriptor 的 JsonTypeInfo 强制转换（引用类型无额外开销）
        var legacyInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<TMessage>)descriptor.JsonTypeInfo;
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, legacyInfo);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(descriptor);

        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, descriptor.JsonTypeInfo);
    }

    /// <inheritdoc />
    public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return System.Text.Json.JsonSerializer.Deserialize(payload, descriptor.JsonTypeInfo);
    }

    /// <inheritdoc />
    public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_options is not null)
        {
            // .NET 11 强类型路径：GetTypeInfo<T>() 零装箱
            var typedInfo = _options.GetTypeInfo<TMessage>();
            return System.Text.Json.JsonSerializer.Deserialize(payload, typedInfo);
        }

        // 默认路径：强制转换
        var legacyInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<TMessage>)descriptor.JsonTypeInfo;
        return System.Text.Json.JsonSerializer.Deserialize(payload, legacyInfo);
    }

    // ── 池化序列化路径 ─────────────────────────────────────

    /// <summary>
    /// 使用 <c>Utf8JsonWriter.Reset(IBufferWriter&lt;byte&gt;)</c> 复用 Writer 的池化序列化路径。<br/>
    /// 消除每次 <c>SerializeToUtf8Bytes</c> 内部创建的 Writer 对象分配。
    /// </summary>
    private static ReadOnlyMemory<byte> SerializePooled<TMessage>(
        TMessage message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TMessage> typeInfo)
    {
        var bufferWriter = _tlsBufferWriter;
        if (bufferWriter is null)
        {
            bufferWriter = new ArrayBufferWriter<byte>(256);
            _tlsBufferWriter = bufferWriter;
        }
        else
        {
            bufferWriter.Clear();
        }

        var writer = _tlsWriter;
        if (writer is null)
        {
            writer = new Utf8JsonWriter(bufferWriter);
            _tlsWriter = writer;
        }
        else
        {
            writer.Reset(bufferWriter);
        }

        System.Text.Json.JsonSerializer.Serialize(writer, message, typeInfo);
        writer.Flush();

        return bufferWriter.WrittenSpan.ToArray();
    }
}
