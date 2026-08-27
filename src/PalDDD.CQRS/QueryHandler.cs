// ─────────────────────────────────────────────────────────────
// 🔍 IQueryHandler<TQ,TR> — 查询处理器 DIM 桥接
// ─────────────────────────────────────────────────────────────
// 与 ICommandHandler 同模式。Dispatcher 通过非泛型 IHandler
// 调用泛型 HandleAsync，无需 MakeGenericType。
//
namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 查询处理器 DIM 桥接
// ─────────────────────────────────────────────────────────────

/// <summary>查询处理器接口 — 通过 DIM 桥接 <see cref="IHandler"/>（AOT 安全）</summary>
/// <typeparam name="TQuery">查询类型</typeparam>
/// <typeparam name="TResult">结果类型</typeparam>
public interface IQueryHandler<TQuery, TResult> : IHandler
    where TQuery : IQuery<TResult>
{
    /// <summary>处理查询</summary>
    ValueTask<TResult> HandleAsync(TQuery query, CancellationToken ct);

    /// <summary>非泛型桥接 — 默认实现，Handler 作者无需覆盖</summary>
    /// <remarks>
    /// v26 P3 勘正：原"同步完成路径零分配"声明对值类型响应失实——快路径
    /// <c>new ValueTask&lt;object?&gt;(task.Result)</c> 在 TResult 为值类型时有 1 次装箱
    /// 分配，引用类型响应才零分配（对齐 PipelineBehavior.cs v25 勘正措辞）。快路径的实际
    /// 收益是跳过 AwaitAndBox 状态机分配，非零分配；异步路径另按需分配状态机。
    /// </remarks>
    ValueTask<object?> IHandler.HandleAsync(IBaseRequest request, CancellationToken ct)
    {
        var task = HandleAsync((TQuery)request, ct);
        if (task.IsCompletedSuccessfully)
            return new ValueTask<object?>(task.Result);
        return AwaitAndBox(task);

        static async ValueTask<object?> AwaitAndBox(ValueTask<TResult> vt)
            => await vt.ConfigureAwait(false);
    }
}
