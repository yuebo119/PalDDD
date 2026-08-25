// ─────────────────────────────────────────────────────────────
// 🔀 DynamicStep — 动态路由步骤
// ─────────────────────────────────────────────────────────────
//
// 💡 什么是 Dynamic Routing？
//   ｜ 运行时根据 Saga 状态决定下一步执行的步骤 key。
//   ｜ 例如：审批 Saga 中"金额 ≤ 1000 → 自动批准；> 1000 → 等待人工审批"。
//   ｜
// 💡 设计决策：
//   ｜ 路由函数在步骤执行时求值，而非注册时，确保获取最新状态。
//   ｜ 返回的 key 必须在 Saga 中已注册，否则视为未匹配（状态不变）。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// 动态路由步骤——运行时根据状态决定下一步 key。
/// </summary>
public sealed class DynamicStep : SagaStep
{
    private readonly Func<SagaState, string> _router;

    /// <inheritdoc/>
    public override StepDispatchKind DispatchKind => StepDispatchKind.Dynamic;

    /// <summary>
    /// 创建动态路由步骤。
    /// </summary>
    /// <param name="key">步骤 key</param>
    /// <param name="router">路由函数——根据当前状态返回下一步骤 key</param>
    /// <param name="compensate">补偿动作（可选）</param>
    public DynamicStep(
        string key,
        Func<SagaState, string> router,
        Func<SagaState, CancellationToken, ValueTask>? compensate = null)
        : base(key, execute: null!, compensate)
    {
        // P3 修复：入参校验（此前 null 延迟到 Route 调用点 NRE）
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
    }

    /// <summary>路由到下一步骤 key。</summary>
    /// <remarks>
    /// P3 声明（十七轮）：返回值必须是与 <see cref="Saga{TState}"/> 注册键完全一致的
    /// 字符串——普通步骤 key 格式为 <c>"State|EventName"</c>（事件精确匹配）或
    /// <c>"State"</c>（通配）；key 事件段只含类型名（不含命名空间），同名事件类型的
    /// 碰撞限制见 <c>SagaKey</c> 的 XML doc。路由目标必须是普通步骤（特殊步骤会被
    /// 编排器显式拒绝），未注册的 key 视为未匹配（状态不变）。
    /// </remarks>
    internal string Route(SagaState state) => _router(state);
}
