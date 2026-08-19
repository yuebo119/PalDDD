// ─────────────────────────────────────────────────────────────
// 📜 EventStreamJsonLines — RecordedEvent 流 JSON Lines 导出/导入
// ─────────────────────────────────────────────────────────────
using PalDDD.EventLog;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Projections.EventLog;

// ─────────────────────────────────────────────────────────────
// JSON Lines 事件流导出/导入 — 与 IEventLog 集成
// ─────────────────────────────────────────────────────────────
//
// 💡 使用场景：
//   ｜ 导出：EventLog.ReadAllAsync() → JSON Lines 文件（备份/迁移/分析）
//   ｜ 导入：JSON Lines 文件 → EventData[] → IEventLog.AppendAsync()（恢复/迁移）
//   ｜ 内存峰值 O(1) — 流式处理，不整批加载，百万事件不 OOM
//
// 📐 导入语义（P2 定案，设计意图声明）：
//   ｜ 导入即重建——streamName/streamVersion/globalPosition 仅随导出记录（溯源），
//   ｜ 导入侧刻意丢弃：目标流的边界与版本由 AppendAsync 的乐观并发重新分配，
//   ｜ GlobalPosition 由目标库自增生成本次序。跨库迁移不会（也不应）保留
//   ｜ 源库的全局位置。需要按流恢复时，按导出文件分组后逐流 Append。
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

        // ITM-218 修复（三十二轮）：导出迭代补 ConfigureAwait(false)——库代码续体不应回捕调用方上下文
        await foreach (var evt in events.WithCancellation(ct).ConfigureAwait(false))
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

    /// <summary>单行最大字符数——ITM-218 修复：无上限 ReadLineAsync 可被超长无换行输入耗尽内存。</summary>
    public const int MaxLineChars = 16 * 1024 * 1024; // 16MB/行（Base64 payload 后合法事件已宽裕）

    private static async IAsyncEnumerable<EventData> ImportAsyncCore(
        Stream input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(input, leaveOpen: true);

        var lineNumber = 0L;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            lineNumber++; // 空行也计入——行号须对齐文件物理行，坏行才定位得准
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // ITM-218 修复：超长行在上抛带行号的受控异常——不让恶意输入以 OOM 崩溃进程
            if (line.Length > MaxLineChars)
                throw new EventReplayException(
                    $"Event import failed at line {lineNumber}: line exceeds {MaxLineChars} character limit.");

            EventData? evt;
            try
            {
                evt = DeserializeEventLine(line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new EventReplayException(
                    $"Event import failed at line {lineNumber}: line is not a valid exported event record.", ex);
            }

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
        json.WriteString("eventId", evt.EventId.ToString());
        json.WriteString("eventName", evt.EventName);
        json.WriteString("streamName", evt.StreamName);
        json.WriteNumber("streamVersion", evt.StreamVersion);
        json.WriteNumber("globalPosition", evt.GlobalPosition);
        json.WriteNumber("schemaVersion", evt.SchemaVersion);
        json.WriteString("contentType", evt.ContentType);
        json.WriteBase64String("payload", evt.Payload.Span);
        if (evt.Metadata.Length > 0)
            json.WriteBase64String("metadata", evt.Metadata.Span);
        // P1 修复：审计与追踪元数据（原版完全丢失，备份/迁移往返后审计链断）
        if (!string.IsNullOrEmpty(evt.Audit.ActorId))
            json.WriteString("actorId", evt.Audit.ActorId);
        if (!string.IsNullOrEmpty(evt.Audit.Reason))
            json.WriteString("reason", evt.Audit.Reason);
        if (evt.Audit.CorrelationId is not null)
            json.WriteString("correlationId", evt.Audit.CorrelationId.Value.ToString());
        if (evt.Audit.CausationId is not null)
            json.WriteString("causationId", evt.Audit.CausationId.Value.ToString());
        if (!string.IsNullOrEmpty(evt.Audit.TraceParent))
            json.WriteString("traceParent", evt.Audit.TraceParent);
        if (!string.IsNullOrEmpty(evt.Audit.TraceState))
            json.WriteString("traceState", evt.Audit.TraceState);
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

        // P1 修复：读回审计与追踪元数据
        var actorId = root.TryGetProperty("actorId", out var a) ? a.GetString() : null;
        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
        PalUlid? correlationId = null;
        if (root.TryGetProperty("correlationId", out var c) && c.GetString() is string cs)
            correlationId = PalUlid.Parse(cs);
        PalUlid? causationId = null;
        if (root.TryGetProperty("causationId", out var cau) && cau.GetString() is string caus)
            causationId = PalUlid.Parse(caus);
        var traceParent = root.TryGetProperty("traceParent", out var tp) ? tp.GetString() : null;
        var traceState = root.TryGetProperty("traceState", out var ts) ? ts.GetString() : null;

        var audit = string.IsNullOrEmpty(actorId) && string.IsNullOrEmpty(reason)
            && correlationId is null && causationId is null
            && string.IsNullOrEmpty(traceParent) && string.IsNullOrEmpty(traceState)
            ? EventAuditMetadata.Empty
            : new EventAuditMetadata(actorId, reason, correlationId, causationId, traceParent, traceState);

        return new EventData(
            PalUlid.Parse(root.GetProperty("eventId").GetString()!),
            root.GetProperty("eventName").GetString()!,
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("contentType").GetString()!,
            root.GetProperty("payload").GetBytesFromBase64(),
            metadata,
            audit);
    }
}
