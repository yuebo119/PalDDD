// ─────────────────────────────────────────────────────────────
// 🔗 IPipelineBehavior — 管道行为 DIM 桥接
// ─────────────────────────────────────────────────────────────
// 泛型接口 + 非泛型桥接：行为作者只关心泛型版本，
// Dispatcher 通过非泛型 IPipelineBehavior 零反射构建管道链。
//
namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 管道行为 DIM 桥接
// ─────────────────────────────────────────────────────────────

/// <summary>非泛型管道行为接口 — 分发器通过此接口零反射构建管道链（AOT 安全）</summary>
/// <remarks>
/// <see cref="IPipelineBehavior{TRequest,TResponse}"/> 通过默认接口方法（DIM）自动实现此接口。<br/>
/// 行为作者无需关心此接口的存在。<br/>
/// <see cref="RequestType"/> / <see cref="ResponseType"/> 通过 DIM 编译时常量提供，消除运行时 <c>MakeGenericType</c>。
/// </remarks>
public interface IPipelineBehavior
{
    /// <summary>执行管道行为 — 调用 next() 继续管道</summary>
    ValueTask<object?> HandleAsync(IBaseRequest request, CancellationToken ct, Func<ValueTask<object?>> next);

    /// <summary>此行为处理的请求类型 — DIM 编译时常量，零反射</summary>
    Type RequestType { get; }

    /// <summary>此行为处理的响应类型 — DIM 编译时常量，零反射</summary>
    Type ResponseType { get; }
}

/// <summary>泛型管道行为接口 — 通过 DIM 桥接 <see cref="IPipelineBehavior"/>（AOT 安全）</summary>
/// <typeparam name="TRequest">请求类型</typeparam>
/// <typeparam name="TResponse">响应类型</typeparam>
public interface IPipelineBehavior<TRequest, TResponse> : IPipelineBehavior
    where TRequest : IRequest<TResponse>
{
    /// <summary>处理管道步骤 — 调用 next() 继续下一个行为或 Handler</summary>
    ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next);

    /// <summary>非泛型桥接 — 默认实现，行为作者无需覆盖</summary>
    /// <remarks>同步完成路径零分配（跳过 async 状态机），异步路径按需分配</remarks>
    ValueTask<object?> IPipelineBehavior.HandleAsync(IBaseRequest request, CancellationToken ct, Func<ValueTask<object?>> next)
    {
        var task = HandleAsync(
            (TRequest)request, ct,
            async () =>
            {
                var result = await next().ConfigureAwait(false);
                return (TResponse)result!;
            });

        if (task.IsCompletedSuccessfully)
            return new ValueTask<object?>(task.Result);
        return AwaitAndBox(task);

        static async ValueTask<object?> AwaitAndBox(ValueTask<TResponse> vt)
            => await vt.ConfigureAwait(false);
    }

    /// <summary>AOT 安全请求类型标识 — typeof(TRequest) 是编译时常量</summary>
    Type IPipelineBehavior.RequestType => typeof(TRequest);

    /// <summary>AOT 安全响应类型标识 — typeof(TResponse) 是编译时常量</summary>
    Type IPipelineBehavior.ResponseType => typeof(TResponse);
}
