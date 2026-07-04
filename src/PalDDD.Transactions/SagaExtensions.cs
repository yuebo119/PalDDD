// ─────────────────────────────────────────────────────────────
// 🧩 SagaExtensions — Saga<TState> 的 Agent 增强能力扩展方法
// ─────────────────────────────────────────────────────────────
//
// 💡 为 Saga<TState> 添加 FanOut / ChildSaga / Interrupt / Dynamic 步骤的便捷注册。
//   ｜ 这些步骤类型继承自 SagaStep，可直接传给 When 方法。
//   ｜ 扩展方法提供更语义化的 API：saga.WhenFanOut(...) 而非 saga.When(state, fanOutStep)。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>Saga 增强能力扩展方法。</summary>
public static class SagaExtensions
{
    // ═══════════════════════════════════════════════════════════════
    // WhenFanOut
    // ═══════════════════════════════════════════════════════════════

    /// <summary>注册 Fan-out 步骤——将一批子任务并行分发执行。</summary>
    public static void WhenFanOut<TState, TItem, TResult>(
        this Saga<TState> saga,
        string state,
        FanOutStep<TItem, TResult> step)
        where TState : SagaState, new()
        where TItem : notnull
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, step);
    }

    /// <summary>注册 Fan-out 步骤（事件精确匹配版本）。</summary>
    public static void WhenFanOut<TState, TEvent, TItem, TResult>(
        this Saga<TState> saga,
        string state,
        FanOutStep<TItem, TResult> step)
        where TState : SagaState, new()
        where TItem : notnull
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, typeof(TEvent), step);
    }

    // ═══════════════════════════════════════════════════════════════
    // WhenChild
    // ═══════════════════════════════════════════════════════════════

    /// <summary>注册子 Saga 步骤——嵌套调用另一个 Saga。</summary>
    public static void WhenChild<TState, TChildState, TInput, TOutput>(
        this Saga<TState> saga,
        string state,
        ChildSagaStep<TChildState, TInput, TOutput> step)
        where TState : SagaState, new()
        where TChildState : SagaState, new()
        where TInput : notnull
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, step);
    }

    /// <summary>注册子 Saga 步骤（事件精确匹配版本）。</summary>
    public static void WhenChild<TState, TEvent, TChildState, TInput, TOutput>(
        this Saga<TState> saga,
        string state,
        ChildSagaStep<TChildState, TInput, TOutput> step)
        where TState : SagaState, new()
        where TChildState : SagaState, new()
        where TInput : notnull
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, typeof(TEvent), step);
    }

    // ═══════════════════════════════════════════════════════════════
    // WhenInterrupt
    // ═══════════════════════════════════════════════════════════════

    /// <summary>注册 HITL 中断步骤——挂起 Saga 等待人工决策。</summary>
    public static void WhenInterrupt<TState>(
        this Saga<TState> saga,
        string state,
        InterruptStep step)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, step);
    }

    /// <summary>注册 HITL 中断步骤（事件精确匹配版本）。</summary>
    public static void WhenInterrupt<TState, TEvent>(
        this Saga<TState> saga,
        string state,
        InterruptStep step)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, typeof(TEvent), step);
    }

    // ═══════════════════════════════════════════════════════════════
    // WhenDynamic
    // ═══════════════════════════════════════════════════════════════

    /// <summary>注册动态路由步骤——运行时根据状态决定下一步 key。</summary>
    public static void WhenDynamic<TState>(
        this Saga<TState> saga,
        string state,
        DynamicStep step)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, step);
    }

    /// <summary>注册动态路由步骤（事件精确匹配版本）。</summary>
    public static void WhenDynamic<TState, TEvent>(
        this Saga<TState> saga,
        string state,
        DynamicStep step)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(saga);
        saga.When(state, typeof(TEvent), step);
    }
}
