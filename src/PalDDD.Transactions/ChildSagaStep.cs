// ─────────────────────────────────────────────────────────────
// 🧒 ChildSagaStep — 子 Saga 嵌套步骤
// ─────────────────────────────────────────────────────────────
//
// 💡 什么是 Child Saga？
//   ｜ 将一个完整 Saga 作为父 Saga 的一个步骤执行（嵌套编排）。
//   ｜ 例如：订单 Saga 中"支付"本身也是一个子 Saga（预授权→扣款→确认）。
//   ｜
// 💡 设计决策：
//   ｜ 输入/输出通过选择器函数从父/子状态中提取，保持类型安全。
//   ｜ 子 Saga 完成后父 Saga 继续执行后续步骤。
//   ｜ 子 Saga 失败时触发父 Saga 的补偿链。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>内部子 Saga 步骤接口——非泛型调度，提取输入/输出为 object。</summary>
internal interface IInternalChildSagaStep
{
    object? ExtractInput(SagaState parentState);
    void ApplyOutput(SagaState parentState, SagaState childState);
    Type ChildStateType { get; }
}

/// <summary>
/// 子 Saga 包装步骤——允许一个 Saga 嵌套调用另一个 Saga。
/// </summary>
/// <typeparam name="TChildState">子 Saga 状态类型</typeparam>
/// <typeparam name="TInput">从父状态提取的输入</typeparam>
/// <typeparam name="TOutput">子 Saga 完成后提取到父状态的输出</typeparam>
public sealed class ChildSagaStep<TChildState, TInput, TOutput> : SagaStep, IInternalChildSagaStep
    where TChildState : SagaState, new()
    where TInput : notnull
    where TOutput : notnull
{
    private readonly Func<SagaState, TInput> _inputSelector;
    private readonly Func<TChildState, TOutput> _outputSelector;
    private readonly Action<SagaState, TOutput>? _outputApplier;

    /// <inheritdoc/>
    public override StepDispatchKind DispatchKind => StepDispatchKind.ChildSaga;

    /// <inheritdoc/>
    Type IInternalChildSagaStep.ChildStateType => typeof(TChildState);

    /// <summary>
    /// 创建子 Saga 步骤。
    /// </summary>
    /// <param name="key">步骤 key</param>
    /// <param name="inputSelector">从父 Saga 状态提取子 Saga 输入</param>
    /// <param name="outputSelector">从子 Saga 状态提取输出写回父状态</param>
    /// <param name="outputApplier">可选的输出应用器——将输出写回父状态。若为 null，输出仅被提取但不自动应用。</param>
    /// <param name="compensate">补偿动作（可选）</param>
    public ChildSagaStep(
        string key,
        Func<SagaState, TInput> inputSelector,
        Func<TChildState, TOutput> outputSelector,
        Action<SagaState, TOutput>? outputApplier = null,
        Func<SagaState, CancellationToken, ValueTask>? compensate = null)
        : base(key, execute: null!, compensate)
    {
        _inputSelector = inputSelector;
        _outputSelector = outputSelector;
        _outputApplier = outputApplier;
    }

    /// <summary>从父 Saga 状态提取输入。</summary>
    internal TInput ExtractInput(SagaState parentState) => _inputSelector(parentState);

    /// <summary>从子 Saga 状态提取输出。</summary>
    internal TOutput ExtractOutput(TChildState childState) => _outputSelector(childState);

    /// <inheritdoc/>
    object? IInternalChildSagaStep.ExtractInput(SagaState parentState)
        => _inputSelector(parentState);

    /// <inheritdoc/>
    void IInternalChildSagaStep.ApplyOutput(SagaState parentState, SagaState childState)
    {
        var output = _outputSelector((TChildState)childState);
        if (_outputApplier is not null)
            _outputApplier(parentState, output);
    }
}
