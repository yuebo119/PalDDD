using System.Buffers;

namespace PalDDD.Serialization.Json;


// ─────────────────────────────────────────────────────────────
// JSON Lines 逐行事件读写器（.NET 11 Preview 5）
// ─────────────────────────────────────────────────────────────
// 运行时集成路径：OutboxBatchProcessor 批量序列化时可使用
// JsonLinesEventWriter 替代逐条 Serialize，消除整批内存峰值。
// 示例：new JsonLinesEventWriter(buffer).Write(msg, payload)
// ─────────────────────────────────────────────────────────────

/// <summary>
/// JSON Lines (.jsonl) 逐行序列化器，专为事件流/Inbox 批量消费设计。<br/>
/// 每行一条事件，天然适配逐行解析，消除整批反序列化的内存峰值。<br/>
/// 使用 Phase A1 的 <c>GetTypeInfo&lt;T&gt;()</c> 强类型路径和
/// Phase A2 的池化 <c>Utf8JsonWriter</c> 实现高效逐行写出。
/// </summary>
#pragma warning disable CA1822 // Mark members as static — 设计为实例 API 供未来 DI/状态管理
public sealed class JsonLinesEventWriter
{
    /// <summary>序列化一条事件为 JSON Lines 格式的一行（含尾部 \n）。</summary>
    public ReadOnlyMemory<byte> SerializeLine<TMessage>(
        TMessage message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TMessage> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var bufferWriter = GetOrCreateBufferWriter();
        var writer = GetOrCreateWriter(bufferWriter);

        System.Text.Json.JsonSerializer.Serialize(writer, message, typeInfo);
        writer.Flush();

        // JSON Lines 格式规范要求 \n 作为行分隔符（jsonlines.org），
        // 与 JsonSerializerOptions.NewLine（影响缩进输出的换行符）是不同概念。
        // 此处不使用 NewLine 是因为 JSON Lines 标准固定为 \n。
        bufferWriter.GetSpan(1)[0] = (byte)'\n';
        bufferWriter.Advance(1);

        return bufferWriter.WrittenSpan.ToArray();
    }

    // ── ThreadLocal 池化（复用 Phase A2 机制）──

    [ThreadStatic]
    private static System.Text.Json.Utf8JsonWriter? _tlsWriter;
    [ThreadStatic]
    private static System.Buffers.ArrayBufferWriter<byte>? _tlsBuffer;

    private static System.Buffers.ArrayBufferWriter<byte> GetOrCreateBufferWriter()
    {
        var buf = _tlsBuffer;
        if (buf is null)
        {
            buf = new System.Buffers.ArrayBufferWriter<byte>(256);
            _tlsBuffer = buf;
        }
        else
        {
            buf.Clear();
        }
        return buf;
    }

    private static System.Text.Json.Utf8JsonWriter GetOrCreateWriter(
        System.Buffers.IBufferWriter<byte> bufferWriter)
    {
        var w = _tlsWriter;
        if (w is null)
        {
            w = new System.Text.Json.Utf8JsonWriter(bufferWriter);
            _tlsWriter = w;
        }
        else
        {
            w.Reset(bufferWriter);
        }
        return w;
    }
}

/// <summary>
/// JSON Lines (.jsonl) 逐行反序列化器。<br/>
/// 一次读取整块 payload，按 \n 拆分，每行独立反序列化为一条事件。<br/>
/// 使用 <see cref="SearchValues{Byte}"/> 硬件加速换行检测（SIMD）。
/// </summary>
public sealed class JsonLinesEventReader
{
    private static readonly SearchValues<byte> s_newline = SearchValues.Create("\n"u8);

    /// <summary>从 JSON Lines payload 中逐行反序列化出所有事件。</summary>
    /// <param name="payload">UTF-8 JSON Lines 数据，事件间以 \n 分隔</param>
    /// <param name="typeInfo">源生成的强类型 metadata</param>
    /// <returns>反序列化后的事件列表</returns>
    public IReadOnlyList<TMessage> DeserializeAll<TMessage>(
        ReadOnlyMemory<byte> payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TMessage> typeInfo)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (payload.IsEmpty)
            return [];

        var span = payload.Span;
        List<TMessage> result = [];

        var start = 0;
        while (start < span.Length)
        {
            var remaining = span.Slice(start);
            var idx = remaining.IndexOfAny(s_newline);
            var lineLen = idx < 0 ? remaining.Length : idx;

            if (lineLen > 0)
            {
                var line = remaining.Slice(0, lineLen);
                var msg = System.Text.Json.JsonSerializer.Deserialize(line, typeInfo);
                if (msg is not null)
                    result.Add(msg);
            }

            start += lineLen + (idx < 0 ? 0 : 1); // skip \n
        }

        return result;
    }
}
#pragma warning restore CA1822
