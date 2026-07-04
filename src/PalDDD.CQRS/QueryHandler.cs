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
    /// <remarks>同步完成路径零分配（跳过 async 状态机），异步路径按需分配</remarks>
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
