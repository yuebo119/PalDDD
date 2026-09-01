// ─────────────────────────────────────────────────────────────
// ⚡ PipelineStateMachine — 替代逐行为 async lambda 链的显式管道推进
// ─────────────────────────────────────────────────────────────
// v27 P3 勘正（原"零堆分配/每请求节省 ~360B"声称与 IL 不符）：每经过一个行为
// 分配一个 Func 委托（ExecuteNextAsync 实例方法组无缓存，~64B/行为）；
// 快路径省 AwaitAndBox 状态机装箱；总收益相对逐行为 async 链约省状态机分配
//
using System.Collections.Immutable;

namespace PalDDD.CQRS;

/// <summary>管道状态机 — 可重用实例，替代逐行为 async lambda 链</summary>
/// <remarks>
/// 💡 保留理由：替代逐行为 async lambda 链。每行为一次 Func 委托分配（~64B，实例方法组
///    不缓存——v27 P3 勘正，原"零闭包/每请求节省 ~360B"声称与 IL 不符），快路径省
///    AwaitAndBox 状态机装箱；总收益相对逐行为 async 链约省状态机分配。
///    详见 docs/decisions/004-core-type-retention.md
/// <para>
/// ⚠️ <b>单请求独占语义：</b>实例的字段在每个 <see cref="Reset"/> 调用被完整覆盖（ behaviors/handler/request/ct/index 全部重置），
/// 但调用方必须<b>禁止跨请求复用同一实例的并发执行</b>——字段无任何同步保护，
/// 多请求交错调用 <see cref="ExecuteNextAsync"/> 会互相污染 <c>_index</c> 游标与 <c>_behaviors</c> 引用。
/// </para>
/// <para>
/// 当前实现：Dispatcher 每请求 new 一个实例（~40B，v13 勘正措辞——原文"对象池/线程局部借用"
/// 为设计畅想非现状，易误读为已池化）；单请求生命周期内一次 Reset + 一次链执行，不跨请求保留。
/// 若未来基准证明 40B 分配成为瓶颈，再评估池化（YAGNI——以 bench 数据为准）。
/// </para>
/// </remarks>
internal sealed class PipelineStateMachine
{
    private ImmutableArray<IPipelineBehavior> _behaviors;
    private IHandler? _handler;
    private IBaseRequest? _request;
    private CancellationToken _ct;
    private int _index;

    /// <summary>
    /// 重置状态机以处理新请求（重用同一实例）。<br/>
    /// ⚠️ 调用方必须保证在本次管道完成（<see cref="ExecuteNextAsync"/> 链到达终点）
    /// 之前不复用本实例处理其他请求——字段无同步保护，并发交错会破坏 <c>_index</c> 游标。
    /// </summary>
    public void Reset(
        ImmutableArray<IPipelineBehavior> behaviors,
        IHandler handler,
        IBaseRequest request,
        CancellationToken ct)
    {
        // v39 P3（ITM-284 全仓构造守卫惯例漏网）：补引用参数守卫——handler/request 此前
        // 零守卫，null 延迟到 ExecuteNextAsync 的 _handler!/_request! 处才 NRE，堆栈不指向
        // 配置错误源头。behaviors（ImmutableArray 结构体）与 ct（值类型）无 null 语义不守卫
        //（ThrowIfNull 对非可空 struct 为无操作，CA2264 在 warnaserror 下禁止）
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(request);
        _behaviors = behaviors;
        _handler = handler;
        _request = request;
        _ct = ct;
        _index = 0;
    }

    /// <summary>执行管道中的下一个行为，或到达终点时执行 Handler</summary>
    /// <remarks>
    /// v30 P3 契约声明：传给 <see cref="IPipelineBehavior.HandleAsync"/> 的 next 委托（即本方法
    /// 的方法组）在每次管道执行中<b>恰好调用一次</b>——本方法用 <c>_index++</c> 推进游标，
    /// 多次调用会跳过后续 behavior（每次调用都消费一个游标位）。重试场景应在 behavior 内
    /// 缓存首次 next 结果后复用，而非重调 next；需要多次执行后续段的语义应改用独立的
    /// 管道实例（Reset 后重新执行）。
    /// </remarks>
    public ValueTask<object?> ExecuteNextAsync()
    {
        if (_index < _behaviors.Length)
        {
            var behavior = _behaviors[_index++];
            return behavior.HandleAsync(_request!, _ct, ExecuteNextAsync);
        }
        return _handler!.HandleAsync(_request!, _ct);
    }
}
