namespace PalDDD.Transactions;

/// <summary>步骤调度类型——决定 ProcessEventAsync 如何分发执行</summary>
public enum StepDispatchKind
{
    /// <summary>标准步骤——通过 ExecuteAsync 委托执行</summary>
    Normal,

    /// <summary>Fan-out 并行步骤</summary>
    FanOut,

    /// <summary>子 Saga 嵌套步骤</summary>
    ChildSaga,

    /// <summary>HITL 中断步骤</summary>
    Interrupt,

    /// <summary>动态路由步骤</summary>
    Dynamic
}

/// <summary>Saga 步骤定义</summary>
public class SagaStep
{
    /// <summary>步骤名称</summary>
    public string Name { get; }

    /// <summary>前向动作</summary>
    public Func<SagaState, object, CancellationToken, ValueTask<SagaState>> ExecuteAsync { get; }

    /// <summary>补偿动作（可选）</summary>
    /// <remarks>
    /// ⚠️ 补偿动作是直接委托调用，不经过 Outbox。<br/>
    /// 若补偿需发布领域事件（如"库存已恢复"），实现者应自行通过
    /// <c>IOutboxStore</c> 写入 Outbox 以保证至少一次语义。<br/>
    /// 框架不强制补偿路径的事件发布策略——由应用程序设计者决定。
    /// </remarks>
    public Func<SagaState, CancellationToken, ValueTask>? CompensateAsync { get; }

    /// <summary>超时时间（可选）</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>步骤调度类型——子类重写以声明特殊执行路径</summary>
    public virtual StepDispatchKind DispatchKind => StepDispatchKind.Normal;

    public SagaStep(
        string name,
        Func<SagaState, object, CancellationToken, ValueTask<SagaState>> execute,
        Func<SagaState, CancellationToken, ValueTask>? compensate = null,
        TimeSpan? timeout = null)
    {
        Name = name;
        ExecuteAsync = execute;
        CompensateAsync = compensate;
        Timeout = timeout;
    }
}
