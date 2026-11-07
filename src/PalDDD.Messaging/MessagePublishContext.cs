// ─────────────────────────────────────────────────────────────
// 📦 MessagePublishContext — 发布追踪上下文
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging;

/// <summary>消息代理发布所携带的跨消息关联与 W3C 追踪上下文。</summary>
public readonly record struct MessagePublishContext(
    PalUlid? CorrelationId,
    PalUlid? CausationId,
    string? TraceParent,
    string? TraceState)
{
    /// <summary>
    /// ADR-039 / 审计 A3：调用方自定义业务头（租户/版本等）。<c>null</c>（默认）表示无额外头——
    /// 默认路径零分配（不构造字典）。
    /// </summary>
    /// <remarks>
    /// <b>刻意做成 <c>init</c> 属性而非第 5 个位置参数</b>：位置参数会改主构造器与自动生成的
    /// <c>Deconstruct</c>，破坏所有既有构造/解构调用方（SemVer Major）。<c>init</c> 属性
    /// 保持 ctor 与 Deconstruct 不变（源码 + 二进制兼容），用法：
    /// <code>new MessagePublishContext(c, ca, tp, ts) { ExtraHeaders = headers }</code>
    /// 两 broker 的写侧按需追加；键名冲突时<b>框架头优先</b>
    /// （traceparent/tracestate/correlation-id/causation-id 不可被覆盖）。
    /// 注：record struct 的相等性包含本属性——两个 context 仅字典引用不同则不等（可接受）。
    /// </remarks>
    public IReadOnlyDictionary<string, string?>? ExtraHeaders { get; init; }

    /// <summary>空发布上下文，供无关联元数据的调用方使用。</summary>
    public static MessagePublishContext Empty { get; } = new(null, null, null, null);
}
