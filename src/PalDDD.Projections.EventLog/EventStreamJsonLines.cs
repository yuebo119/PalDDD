// ─────────────────────────────────────────────────────────────
// 📜 EventStreamJsonLines — RecordedEvent 流 JSON Lines 导出/导入
// ─────────────────────────────────────────────────────────────
using PalDDD.EventLog;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PalDDD.Projections.EventLog;

// ─────────────────────────────────────────────────────────────
// JSON Lines 事件流导出/导入 — 与 IEventLog 集成
// ─────────────────────────────────────────────────────────────
//
// 💡 使用场景：
//   ｜ 导出：EventLog.ReadAllAsync() → JSON Lines 文件（备份/迁移/分析）
//   ｜ 导入：JSON Lines 文件 → EventData[] → IEventLog.AppendAsync()（恢复/迁移）
//   ｜ 内存峰值 O(1) — 流式处理，不整批加载，百万事件不 OOM
// ─────────────────────────────────────────────────────────────

/// <summary>事件流 JSON Lines 导入导出工具</summary>
public static class EventStreamJsonLines
{
    /// <summary>
    /// 将 RecordedEvent 流导出为 JSON Lines 格式到输出流。<br/>
    /// 每事件一行 JSON，N 个事件产生 N 行。<br/>
    /// 内存峰值 O(1) — 不整批加载。
    /// </summary>
    /// <example>
    /// await using var file = File.Create("backup.jsonl");
    /// await EventStreamJsonLines.ExportAsync(log.ReadAllAsync(ct), file, ct);
    /// </example>
    public static async ValueTask ExportAsync(
        IAsyncEnumerable<RecordedEvent> events,
        Stream output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(output);

        await foreach (var evt in events.WithCancellation(ct))
        {
            var line = SerializeEventLine(evt);
            await output.WriteAsync(line, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 从 JSON Lines 流导入事件。<br/>
    /// 流式解析——每读一行就 yield 一条 EventData。<br/>
    /// 内存峰值 O(1)。
    /// </summary>
    public static IAsyncEnumerable<EventData> ImportAsync(
        Stream input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ImportAsyncCore(input, ct);
    }

    private static async IAsyncEnumerable<EventData> ImportAsyncCore(
        Stream input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(input, leaveOpen: true);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var evt = DeserializeEventLine(line);
            if (evt is not null)
                yield return evt;
        }
    }

    // ── 序列化辅助 ──

    private static ReadOnlyMemory<byte> SerializeEventLine(RecordedEvent evt)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);
        using var json = new Utf8JsonWriter(buffer);

        json.WriteStartObject();
        json.WriteString("eventId", evt.EventId);
        json.WriteString("eventName", evt.EventName);
        json.WriteString("streamName", evt.StreamName);
        json.WriteNumber("streamVersion", evt.StreamVersion);
        json.WriteNumber("globalPosition", evt.GlobalPosition);
        json.WriteNumber("schemaVersion", evt.SchemaVersion);
        json.WriteString("contentType", evt.ContentType);
        json.WriteBase64String("payload", evt.Payload.Span);
        if (evt.Metadata.Length > 0)
            json.WriteBase64String("metadata", evt.Metadata.Span);
        json.WriteEndObject();
        json.Flush();

        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        return buffer.WrittenSpan.ToArray();
    }

    private static EventData? DeserializeEventLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        var metadata = root.TryGetProperty("metadata", out var m)
            ? m.GetBytesFromBase64()
            : ReadOnlyMemory<byte>.Empty;

        return new EventData(
            root.GetProperty("eventId").GetGuid(),
            root.GetProperty("eventName").GetString()!,
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("contentType").GetString()!,
            root.GetProperty("payload").GetBytesFromBase64(),
            metadata,
            EventAuditMetadata.Empty);
    }
}
