// ─────────────────────────────────────────────────────────────
// 📜 EventStreamJsonLines — RecordedEvent 流 JSON Lines 导出/导入
// ─────────────────────────────────────────────────────────────
using PalDDD.EventLog;
using System.Buffers;
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
//   ｜ 导入即重建——streamName/streamVersion/globalPosition/recordedAt 仅随导出记录（溯源），
//   ｜ 导入侧刻意丢弃：目标流的边界与版本由 AppendAsync 的乐观并发重新分配，
//   ｜ GlobalPosition 由目标库自增生成本次序。跨库迁移不会（也不应）保留
//   ｜ 源库的全局位置。需要按流恢复时，按导出文件分组后逐流 Append。
//   ｜ recordedAt（v25 P3 勘正族 C5 补导出）：导出侧保留记录时 UTC 时间戳（备份/分析
//   ｜ 完整性）；导入侧忽略重建——目标流的重录时间即新的记录时间，EventData 无该字段。
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
        // 2026-10-20 性能轮：单实例 Utf8JsonWriter + 单块 ArrayBufferWriter 复用（不是池化——
        // 每次 ExportAsync 固定分配 64KB，属单次导出内的复用），按 64KB 批量写流；
        // 原实现每事件新建 ArrayBufferWriter + Utf8JsonWriter + ToArray，三次分配/次。
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });
        await foreach (var evt in events.WithCancellation(ct).ConfigureAwait(false))
        {
            // 每个根对象前复位 writer 状态（Flush 后 writer 仍保留上一对象的位置/深度状态，
            // 直接在原状态上写第二个根对象会产出非法行）；Reset 不清理 buffer，跨事件累积。
            var lineStart = buffer.WrittenCount;
            json.Reset(buffer);
            WriteEvent(json, evt);
            await json.FlushAsync(ct).ConfigureAwait(false);
            buffer.Write("\n"u8);
            // 2026-10-21 评审 F2：导出侧与导入侧应用同一字节上限。原实现只限导入，
            // 13MB payload 以 base64 膨胀为约 17.3MB 单行——导出成功、导入失败。
            if (buffer.WrittenCount - lineStart - 1 > MaxLineBytes)
                throw new EventReplayException(
                    $"Event export failed: a single JSON line exceeds the {MaxLineBytes}-byte limit (base64 payloads expand by 4/3). Export aborted before writing the oversized line.");
            if (buffer.WrittenCount >= 64 * 1024)
            {
                await output.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        if (buffer.WrittenCount > 0)
            await output.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
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

    /// <summary>单行最大字节数（导入与导出共用）——ITM-218 修复超长无换行输入的内存耗尽；
    /// 2026-10-21 评审 F2 明确单位为 UTF-8 字节并补导出侧上限。</summary>
    public const int MaxLineBytes = 16 * 1024 * 1024;

    /// <summary>旧名称（语义失实：实现一直按字节计）。保留为字节上限的别名，供已编译消费方过渡。</summary>
    [Obsolete("Renamed to MaxLineBytes — the value is a UTF-8 byte limit, not a character count.")]
    public const int MaxLineChars = MaxLineBytes;

    private static async IAsyncEnumerable<EventData> ImportAsyncCore(
        Stream input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 2026-10-20 性能轮：UTF-8 字节直读——原 StreamReader(char) + StringBuilder + string
        // + JsonDocument.Parse(string) 每行至少三次整行拷贝（UTF-8→UTF-16→UTF-8）与一次
        // string 分配。本实现按字节找 '\n'，Utf8JsonReader 直接在字节上解析；行缓冲复用，
        // 峰值仍是 O(单行)。
        var readBuffer = new byte[8192];
        var line = new byte[8192];
        var lineLength = 0;
        var lineNumber = 0L;
        int bufferLength = 0, bufferPosition = 0;

        while (true)
        {
            if (bufferPosition >= bufferLength)
            {
                bufferLength = await input.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct).ConfigureAwait(false);
                bufferPosition = 0;
                if (bufferLength == 0)
                {
                    if (lineLength == 0)
                        yield break;
                    lineNumber++;
                    if (!IsAllWhitespace(line.AsSpan(0, lineLength)))
                    {
                        EventData? tail;
                        try
                        {
                            tail = DeserializeEventLine(line.AsSpan(0, lineLength));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            throw new EventReplayException(
                                $"Event import failed at line {lineNumber}: line is not a valid exported event record.", ex);
                        }
                        if (tail is not null)
                            yield return tail;
                    }
                    yield break;
                }
            }

            var newline = Array.IndexOf(readBuffer, (byte)'\n', bufferPosition, bufferLength - bufferPosition);
            var chunkEnd = newline >= 0 ? newline : bufferLength;
            var chunkLength = chunkEnd - bufferPosition;
            if (chunkLength > 0)
            {
                if (lineLength + chunkLength > MaxLineBytes)
                    throw new EventReplayException(
                        $"Event import failed at line {lineNumber + 1}: line exceeds {MaxLineBytes} byte limit.");
                EnsureLineCapacity(ref line, lineLength + chunkLength);
                Buffer.BlockCopy(readBuffer, bufferPosition, line, lineLength, chunkLength);
                lineLength += chunkLength;
            }

            if (newline < 0)
            {
                bufferPosition = bufferLength;
                continue;
            }

            bufferPosition = newline + 1;
            // 对齐 ReadLineAsync 语义：剥离 CRLF 的 '\r'
            if (lineLength > 0 && line[lineLength - 1] == (byte)'\r')
                lineLength--;

            lineNumber++;
            if (lineLength > 0 && !IsAllWhitespace(line.AsSpan(0, lineLength)))
            {
                EventData? evt;
                try
                {
                    evt = DeserializeEventLine(line.AsSpan(0, lineLength));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new EventReplayException(
                        $"Event import failed at line {lineNumber}: line is not a valid exported event record.", ex);
                }
                if (evt is not null)
                    yield return evt;
            }
            lineLength = 0;
        }
    }

    private static void EnsureLineCapacity(ref byte[] line, int required)
    {
        if (line.Length >= required)
            return;
        var next = Math.Min(MaxLineBytes, Math.Max(required, line.Length * 2));
        Array.Resize(ref line, next);
    }

    private static bool IsAllWhitespace(ReadOnlySpan<byte> line)
    {
        foreach (var value in line)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;
        }
        return true;
    }

    // ── 序列化辅助 ──

    private static void WriteEvent(Utf8JsonWriter json, RecordedEvent evt)
    {
        json.WriteStartObject();
        json.WriteString("eventId", evt.EventId.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        json.WriteString("eventName", evt.EventName);
        json.WriteString("streamName", evt.StreamName);
        json.WriteNumber("streamVersion", evt.StreamVersion);
        json.WriteNumber("globalPosition", evt.GlobalPosition);
        // v25 P3 勘正族 C5：补 recordedAt 导出（RecordedEvent.RecordedAt 的记录时 UTC 时间戳）——
        // 备份/分析完整性；导入侧忽略（头注释"导入即重建"丢弃清单），EventData 无对应字段
        json.WriteString("recordedAt", evt.RecordedAt);
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
            json.WriteString("correlationId", evt.Audit.CorrelationId.Value.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        if (evt.Audit.CausationId is not null)
            json.WriteString("causationId", evt.Audit.CausationId.Value.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(evt.Audit.TraceParent))
            json.WriteString("traceParent", evt.Audit.TraceParent);
        if (!string.IsNullOrEmpty(evt.Audit.TraceState))
            json.WriteString("traceState", evt.Audit.TraceState);
        json.WriteEndObject();
    }

    private static EventData? DeserializeEventLine(ReadOnlySpan<byte> line)
    {
        // Utf8JsonReader 直接在 UTF-8 字节上解析，不经过 string/UTF-16 转码；JsonDocument
        // 仅提供按已知字段名读取的结构视图（未知字段自然跳过，兼容未来新增导出字段）。
        var reader = new Utf8JsonReader(line);
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var metadata = root.TryGetProperty("metadata", out var m)
            ? m.GetBytesFromBase64()
            : [];

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

        return EventData.FromOwned(
            PalUlid.Parse(root.GetProperty("eventId").GetString()!),
            root.GetProperty("eventName").GetString()!,
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("contentType").GetString()!,
            root.GetProperty("payload").GetBytesFromBase64(),
            metadata,
            audit);
    }
}
