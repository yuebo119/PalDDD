// ─────────────────────────────────────────────────────────────
// 🔌 流式读重连辅助 — Last-Event-ID 请求头读取入口（ADR-030 Phase 2 收窄实施）
// ─────────────────────────────────────────────────────────────
// EventSource 断线重连时自动携带 Last-Event-ID 请求头（值为末个收到的 id 行内容）。
// GET 形态在 bindQuery 内读取并填入查询，handler 按该 ID 跳过已产出前缀续传。
//
// 续传语义边界（防误用）：框架只提供请求头读取入口，不缓冲已产出项——实时生成流的
// 中途缓冲属 ADR-030 分期节收窄的「per-stream Channel 缓冲」项（触发条件未满足暂不建）。
// 该头的典型受益场景是持久流回放重连（如已完成 agent run 的 EventLog 有序读，位置游标
// 即天然的事件 ID）；实时生成的失败恢复走客户端整轮重开。

using Microsoft.AspNetCore.Http;

namespace PalDDD.Hosting.AspNetCore;

/// <summary>流式读重连辅助（ADR-030 Phase 2 收窄实施）。</summary>
public static class StreamResumeExtensions
{
    /// <summary>SSE 重连环的 <c>Last-Event-ID</c> 请求头名（EventSource 规范头，大小写不敏感匹配）。</summary>
    private const string LastEventIdHeader = "Last-Event-ID";

    /// <summary>
    /// 读取 SSE 重连环带来的 <c>Last-Event-ID</c> 请求头；缺失或空串返回 null。
    /// </summary>
    /// <remarks>
    /// 用法（GET 形态 bindQuery 内）：
    /// <code>
    /// app.MapStreamQuery&lt;TokenStreamQuery, TokenChunk&gt;(
    ///     "/tokens",
    ///     ctx => new TokenStreamQuery(ctx.Request.Query["prompt"].ToString(), ctx.GetLastEventId()),
    ///     StreamJsonContext.Default.TokenChunk,
    ///     idSelector: chunk => chunk.Id.ToString());
    /// </code>
    /// handler 按查询携带的末 ID 跳过已产出前缀（如 EventLog 位置游标定位到该位置之后续读）。
    /// 续传逻辑属应用层责任，框架只做请求头读取与 id 行写出。
    /// </remarks>
    /// <param name="context">HTTP 上下文。</param>
    /// <returns>末个事件 ID；无重连上下文时 null。</returns>
    public static string? GetLastEventId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Headers[LastEventIdHeader].ToString() is { Length: > 0 } lastEventId
            ? lastEventId
            : null;
    }
}
