// ═══════════════════════════════════════════════════════════════
// 📡 SseFrameReader — SSE 帧读取助手（用户测自己的流式端点用）
// ═══════════════════════════════════════════════════════════════
// 解析 text/event-stream 响应体为帧对象：data 行拼接、event/id 行、注释行（心跳）识别。
// 纯 Stream 输入，不依赖 HttpClient（可喂 MemoryStream / TestServer 响应流 / 真网络流）。
//
// 为什么需要：手写解析每处都要处理多行 data 拼接、CRLF/LF、帧终止空行、注释帧——
// 本仓流式测试即为此手写过多轮（见 StreamQueryEndpointTests 的 ReadBody/行过滤形态）。
// 助手只做解析不做断言，断言留在用户测试里。
//
// 注：PalDDD.Testing 仅依赖 Core；本助手是纯 System.IO 实现，无 Hosting 依赖。

using System.Text;

namespace PalDDD.Testing;

/// <summary>SSE 帧（一次 <c>\n\n</c> 分隔的完整事件）。</summary>
/// <param name="EventName">event 行的值；缺省为 null（表示未指定事件名）。</param>
/// <param name="Id">id 行的值；缺省为 null。</param>
/// <param name="Data">data 行内容（多行按规范以 \n 拼接）；无 data 行时为空串。</param>
/// <param name="IsComment">是否为注释帧（以 <c>:</c> 开头，如心跳 <c>:keepalive</c>）。</param>
public sealed record SseFrame(string? EventName, string? Id, string Data, bool IsComment);

/// <summary>SSE 帧读取助手（详见文件头设计说明）。</summary>
public static class SseFrameReader
{
    /// <summary>从响应流读取全部帧直到流结束。</summary>
    public static async Task<IReadOnlyList<SseFrame>> ReadAllAsync(
        Stream responseStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseStream);

        var frames = new List<SseFrame>();
        using var reader = new StreamReader(responseStream, Encoding.UTF8, leaveOpen: true);
        await foreach (var frame in ReadFramesAsync(reader, cancellationToken).ConfigureAwait(false))
            frames.Add(frame);
        return frames;
    }

    /// <summary>逐帧增量读取（适合"读到第 N 帧即可断言"的场景，避免等流自然结束）。</summary>
    public static async IAsyncEnumerable<SseFrame> ReadFramesAsync(
        TextReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? eventName = null;
        string? id = null;
        var dataLines = new List<string>();
        var isComment = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                // 帧终止空行：有内容才产出帧（连续空行不产空帧）
                if (eventName is not null || id is not null || dataLines.Count > 0 || isComment)
                {
                    yield return new SseFrame(eventName, id, string.Join('\n', dataLines), isComment);
                    eventName = null;
                    id = null;
                    dataLines.Clear();
                    isComment = false;
                }
                continue;
            }

            if (line[0] == ':')
            {
                isComment = true; // 注释行（心跳 :keepalive）整行忽略，只标记帧性质
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0
                ? string.Empty
                : line[(colon + 1)..].TrimStart(' '); // 规范：冒号后单个空格可选

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "id":
                    id = value;
                    break;
                case "data":
                    dataLines.Add(value);
                    break;
                default:
                    // 未知字段按规范忽略（retry 等本次不建模）
                    break;
            }
        }

        // 流结束未跟空行：规范建议丢弃不完整帧——但真实断线场景常留半帧，
        // 消费方断言"截断"需要看到它，故产出（IsComment/Data 如实反映）
        if (eventName is not null || id is not null || dataLines.Count > 0 || isComment)
            yield return new SseFrame(eventName, id, string.Join('\n', dataLines), isComment);
    }
}
