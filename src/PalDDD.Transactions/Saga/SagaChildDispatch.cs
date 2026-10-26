// ─────────────────────────────────────────────────────────────
// SagaChildDispatch — 非泛型子 Saga 分派器（v5.0 接口公开化，2026-10-27）
// ─────────────────────────────────────────────────────────────
// 原为 ISagaManager 的 internal 成员 ExecuteChildSagaNonGenericAsync（DefaultSagaManager
// 显式实现）——v5.0 公开化时整体移出接口：反射路由与 manager 实例状态无关，不属于
// manager 契约；且 DAM 标注在接口 public 面上与「AOT 兼容程序集公共面零标注」红线
// 冲突。ChildSagaStep 的非泛型上下文改经本静态分派器直调（行为与原实现逐字一致）。
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace PalDDD.Transactions;

/// <summary>非泛型子 Saga 分派器——object 上下文路由到 Saga.ProcessEventAsync（反射 + 类型缓存）。</summary>
internal static class SagaChildDispatch
{
    /// <summary>把非泛型上下文的子 Saga 调用路由到 ProcessEventAsync 直调（反射）。</summary>
    [RequiresUnreferencedCode("Non-generic child saga dispatch relies on reflection to call ProcessEventAsync; child saga types must be preserved for AOT.")]
    [RequiresDynamicCode("Uses MakeGenericMethod/MakeGenericType which requires dynamic code generation for AOT compatibility.")]
    public static async ValueTask<SagaState> ExecuteAsync(
        object childSaga, SagaState childState, object triggerEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(childSaga);
        ArgumentNullException.ThrowIfNull(childState);
        ArgumentNullException.ThrowIfNull(triggerEvent);

        var stateType = childState.GetType();
        var sagaType = childSaga.GetType();

        // Resolve ProcessEventAsync(TState, object, CancellationToken) on the saga type
        // P1 修复（七轮评审）：ProcessEventAsync 是非泛型方法（Saga.cs 签名
        // ProcessEventAsync(TState, object, CancellationToken)）——MakeGenericMethod 对非泛型
        // MethodInfo 必抛 ArgumentException。直接调用即可。
        // A8（S1 轮，2026-10-15）：查找结果按 saga 类型缓存——ChildSaga 每 attempt 一次
        // 反射链（本查找 + Invoke），MethodInfo 与类型稳定对应，按类型缓存消重复查找。
        var method = ProcessEventAsyncBySagaType.GetOrAdd(sagaType, static t => t.GetMethod(
            "ProcessEventAsync",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Saga type '{t.Name}' does not have a public ProcessEventAsync method. Ensure it inherits Saga<TState>."));

        // Invoke: returns boxed ValueTask<TState>（非泛型方法直接 Invoke）
        var boxedValueTask = method.Invoke(childSaga, [childState, triggerEvent, ct])!;

        return await UnboxAndAwaitValueTaskAsync(boxedValueTask, method.ReturnType, stateType).ConfigureAwait(false);
    }

    // A8（S1 轮）：反射查找缓存——键为稳定类型（saga 类型 / ValueTask<T> 类型 / Task<T>
    // 类型），值为对应成员；ChildSaga 每 attempt 的固定反射税（3 次 GetMethod + 2 次
    // Invoke + 1 次 GetValue）中的查找部分归零，Invoke 语义不变。
    private static readonly ConcurrentDictionary<Type, MethodInfo> ProcessEventAsyncBySagaType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> AsTaskByValueTaskType = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo> ResultByTaskType = new();

    /// <summary>拆箱并 await 反射调用的 ValueTask&lt;TState&gt;，返回 SagaState 结果。</summary>
    [RequiresDynamicCode("Uses MakeGenericType to construct ValueTask<TState> when reflection return type is not pre-constructed; not compatible with native AOT.")]
    [UnconditionalSuppressMessage("Aot", "IL2075:RequiresDynamicallyAccessedMembers",
        Justification = "整个方法已声明 RequiresDynamicCode 边界——GetProperty 位于该边界内，消费方已获得编译期 AOT 警告。")]
    // IL2070 同族补齐（2026-10-17 CI aot-verify 实证）：rc.2 SDK 修剪分析对
    // t.GetMethod("AsTask")/t.GetProperty("Result")（this=缓存键 Type）报 IL2070
    // （'this' 实参不满足 DynamicallyAccessedMemberTypes），与 IL2075 同属
    // RequiresDynamicCode 边界内的反射家族——查找目标为框架稳定成员
    // （ValueTask<T>.AsTask / Task<T>.Result），随 IL2075 同边界压制。
    [UnconditionalSuppressMessage("Aot", "IL2070:RequiresDynamicallyAccessedMembers",
        Justification = "方法已声明 RequiresDynamicCode 边界；GetMethod/GetProperty 的 this（缓存键 Type）位于该边界内，查找目标为框架稳定成员，消费方已获编译期 AOT 警告。")]
    private static async ValueTask<SagaState> UnboxAndAwaitValueTaskAsync(
        object boxedValueTask, Type? methodReturnType, Type stateType)
    {
        // P3 修复（八轮评审）：优先直接使用 method.ReturnType（已是构造完的 ValueTask<T>）——
        // MakeGenericType(stateType) 在方法实际声明类型与运行时 stateType 不一致时
        // （如 saga 类型继承链上的隐藏/协变场景）会构造出错误类型导致 AsTask 绑定失败
        var valueTaskType = methodReturnType is { IsConstructedGenericType: true } constructed
            && constructed.GetGenericTypeDefinition() == typeof(ValueTask<>)
                ? constructed
                : typeof(ValueTask<>).MakeGenericType(stateType); // AOT 兜底路径（RequiresDynamicCode 已声明）

        // A8（S1 轮）：AsTask/Result 查找按类型缓存（ValueTask<T> 与 Task<T> 形态稳定）
        var asTaskMethod = AsTaskByValueTaskType.GetOrAdd(valueTaskType, static t =>
            t.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)!);
        var task = (Task)asTaskMethod.Invoke(boxedValueTask, null)!;
        await task.ConfigureAwait(false);

        // Extract Result property (Task<T>.Result)
        var taskType = task.GetType();
        var resultProp = ResultByTaskType.GetOrAdd(taskType, static t =>
            t.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)!);
        return (SagaState)resultProp.GetValue(task)!;
    }
}

/// <summary>
/// 空失效栅栏——ISagaManager.AcquireInvalidationFenceAsync 的 DIM 默认实现返回值：
/// 无进程内中断条目语义的 manager（外部持久化实现）无物可失效，直接放行。
/// </summary>
internal sealed class NullInvalidationFence : IAsyncDisposable
{
    public static readonly NullInvalidationFence Instance = new();

    private NullInvalidationFence() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
