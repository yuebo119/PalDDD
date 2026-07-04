// ─────────────────────────────────────────────────────────────
// ⚡ PipelineStateMachine — 替代 lambda 闭包链（零堆分配）
// ─────────────────────────────────────────────────────────────
// 传统做法：每个行为产生一个编译器生成的闭包类（~72B/请求）
// 状态机方案：1 个可复用实例（~40B），N×72B → 1×40B
//
using System.Collections.Immutable;

namespace PalDDD.CQRS;

/// <summary>管道状态机 — 可重用实例，消除每个请求的闭包分配</summary>
/// <remarks>
/// 💡 保留理由：替代 lambda 闭包链，每请求节省 ~360B 堆分配（5 行为典型场景）。
///    详见 docs/decisions/004-core-type-retention.md
/// <para>
/// ⚠️ <b>单请求独占语义：</b>实例的字段在每个 <see cref="Reset"/> 调用被完整覆盖（ behaviors/handler/request/ct/index 全部重置），
/// 但调用方必须<b>禁止跨请求复用同一实例的并发执行</b>——字段无任何同步保护，
/// 多请求交错调用 <see cref="ExecuteNextAsync"/> 会互相污染 <c>_index</c> 游标与 <c>_behaviors</c> 引用。
/// </para>
/// <para>
/// 推荐用法：每个 <see cref="Dispatcher"/> 请求通过对象池/线程局部借用实例，
/// 一次 <see cref="Reset"/> + 一次 <see cref="ExecuteNextAsync"/> 链完成后立即归还，
/// 不跨请求保留中间状态。详见 <see cref="Reset"/> 与 <see cref="ExecuteNextAsync"/> 注释。
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
        _behaviors = behaviors;
        _handler = handler;
        _request = request;
        _ct = ct;
        _index = 0;
    }

    /// <summary>执行管道中的下一个行为，或到达终点时执行 Handler</summary>
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
