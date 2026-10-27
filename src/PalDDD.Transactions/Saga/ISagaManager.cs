// ─────────────────────────────────────────────────────────────
// 🎛️ ISagaManager — Saga 管理器接口（恢复中断的 Saga + 子 Saga 执行）
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>
/// Saga 管理器——用于恢复中断的 Saga、查询待决策 Saga、以及执行子 Saga。
/// </summary>
/// <remarks>
/// v5.0 接口公开化（2026-10-27，兑现 v65 P3 声明的「破坏性变更中开放外部实现」窗口）：
/// 本接口原含 <c>internal</c> 成员使程序集外类型无法实现（CS0535），现该成员（非泛型
/// 子 Saga 分派）整体移出接口——反射路由与 manager 实例状态无关（纯 object →
/// ProcessEventAsync 路由），平移至框架内 <c>SagaChildDispatch</c> 静态分派器，不再属于
/// manager 契约；若留在接口上（无论 internal 还是 public），public 面的 DAM 标注都会
/// 与「AOT 兼容程序集公共面零 <c>RequiresDynamicCode</c>」红线冲突。
/// <see cref="InvalidateInterrupted"/>/<see cref="AcquireInvalidationFenceAsync"/> 为 DIM
/// 空默认（无进程内中断条目语义的实现可直接放行）。外部程序集可实现本接口提供持久化
/// manager；进程内语义（中断条目、失效集、决策栅栏）见 <see cref="DefaultSagaManager"/>。
/// </remarks>
public interface ISagaManager
{
    /// <summary>
    /// 恢复一个中断的 Saga——传入人工决策后继续执行。
    /// </summary>
    /// <typeparam name="TDecision">决策数据类型</typeparam>
    /// <param name="sagaId">Saga ID</param>
    /// <param name="decision">人工决策数据</param>
    /// <param name="ct">取消令牌</param>
    /// <remarks>
    /// ⚠️ ITM-633 契约声明——<b>恢复后的状态不会由本方法自动持久化</b>：默认实现
    /// （<see cref="DefaultSagaManager"/>）在中断时捕获的 Saga 实例上派发决策，方法返回
    /// <see cref="ValueTask"/>（无状态）。调用方若要保留恢复结果，必须自行持有该
    /// Saga 实例引用并调用 <c>ISagaStateStore.SaveChangesAsync</c> 落库，否则 store 中该
    /// Saga 仍为 <see cref="SagaStatus.AwaitingHumanDecision"/>，决策效果在下次读回时丢失。
    /// 另：中断条目为进程内字典，多实例部署下决策须路由到中断发生的实例。
    /// </remarks>
    ValueTask ResumeAsync<TDecision>(PalUlid sagaId, TDecision decision, CancellationToken ct)
        where TDecision : notnull;

    /// <summary>
    /// 获取所有处于 <see cref="SagaStatus.AwaitingHumanDecision"/> 状态的 Saga。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>M1-5 表态（v2 审计 A-5，2026-09-19）：默认实现恒返回空列表</b>——
    /// <see cref="DefaultSagaManager"/> 为进程内实现，无持久化查询能力；且本接口含
    /// internal 成员，外部程序集无法提供替代实现。当前架构下 HITL 运营待办查询
    /// <b>不经由此 API</b>——生产运营面须直接查询 Saga 状态存储
    ///（<c>ISagaStateStore</c> 按 <see cref="SagaState.Status"/> 过滤）。
    /// 误信本 API 的空返回 = 误判「无人工待办」（静默假空）。
    /// </remarks>
    ValueTask<IReadOnlyList<SagaState>> GetInterruptedSagasAsync(CancellationToken ct);

    /// <summary>
    /// 执行子 Saga——将子 Saga 作为父 Saga 的一个步骤执行。
    /// </summary>
    /// <typeparam name="TChildState">子 Saga 状态类型</typeparam>
    /// <param name="childSaga">子 Saga 编排器实例</param>
    /// <param name="childState">子 Saga 初始状态</param>
    /// <param name="triggerEvent">触发子 Saga 的事件</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>执行后的子 Saga 状态</returns>
    ValueTask<TChildState> ExecuteChildSagaAsync<TChildState>(
        Saga<TChildState> childSaga,
        TChildState childState,
        object triggerEvent,
        CancellationToken ct)
        where TChildState : SagaState, new();

    /// <summary>
    /// 失效指定 Saga 的中断条目——超时兜底补偿把 Saga 置终态后调用，使迟到决策可见失败。
    /// </summary>
    /// <remarks>
    /// DIM 空默认：无进程内中断条目语义的实现（外部持久化 manager）无物可失效；
    /// <see cref="DefaultSagaManager"/> 覆写为「先写失效集再移除条目」（v29 语义）。
    /// v5.0 前此调用点以 <c>is DefaultSagaManager</c> 模式匹配绕过接口（外部实现被排除），
    /// 公开化后纳入接口契约。
    /// </remarks>
    void InvalidateInterrupted(PalUlid sagaId) { }

    /// <summary>
    /// 获取失效栅栏——超时补偿**开始前**调用：先写失效集（拒绝新决策注册与派发），
    /// 再持有 per-saga 互斥门，直至返回句柄 Dispose。
    /// </summary>
    /// <remarks>
    /// v5.0 manager 侧 fencing（2026-10-27，根治 v37 P2 声明的「补偿进行中」半边窗口）：
    /// <see cref="DefaultSagaManager"/> 的 <see cref="ResumeAsync{TDecision}"/> 决策派发全程
    /// 持同一 per-saga 门——补偿持栅栏期间到达的决策等待补偿完成后按「无条目/已失效」
    /// 可见失败，不再在正被回滚的 Saga 上产生副作用并假成功。
    /// DIM 空默认（空栅栏）：外部持久化 manager 无进程内条目，补偿不受本地栅栏约束。
    /// </remarks>
    ValueTask<IAsyncDisposable> AcquireInvalidationFenceAsync(PalUlid sagaId, CancellationToken ct)
        => new(NullInvalidationFence.Instance);
}
