// ─────────────────────────────────────────────────────────────
// 📨 ICommandHandler<T,R> — 命令处理器 DIM 桥接
// ─────────────────────────────────────────────────────────────
// 💡 开发者只需实现 ICommandHandler<TCmd,TResp>。
// DIM（默认接口方法）自动提供 IHandler.HandleAsync 桥接，
// 使 Dispatcher 可以零反射地调用泛型处理器。
//
namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 非泛型处理器桥接接口 — 分发器零反射调用 Handler 的基础
// ─────────────────────────────────────────────────────────────
//
// IHandler 是 DIM（Default Interface Method）桥接架构的核心基础设施接口，
// 不是 DDD 领域概念。ICommandHandler<TCommand,TResponse> 和
// IQueryHandler<TQuery,TResult> 通过 DIM 自动实现此接口。
//
// 将其定义在 CommandHandler.cs 而非独立文件，是因为它只在 CommandHandler.cs
// 和 QueryHandler.cs 中被引用。独立的文件会让开发者误以为这是需要关注
// 的领域抽象；它实际上是 AOT 安全分发器内部实现细节。

/// <summary>非泛型处理器接口 — 分发器通过此接口零反射调用 Handler（AOT 安全）</summary>
/// <remarks>
/// <see cref="ICommandHandler{TCommand,TResponse}"/> 和 <see cref="IQueryHandler{TQuery,TResult}"/>
/// 通过默认接口方法（DIM）自动实现此接口。Handler 作者无需关心此接口的存在。
/// </remarks>
public interface IHandler
{
    /// <summary>处理请求 — 泛型桥接到具体 Handler</summary>
    ValueTask<object?> HandleAsync(IBaseRequest request, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────
// 命令处理器 DIM 桥接 — 用默认接口方法消除 MakeGenericType 反射
// ─────────────────────────────────────────────────────────────

/// <summary>命令处理器接口 — 通过 DIM 桥接 <see cref="IHandler"/>（AOT 安全）</summary>
/// <typeparam name="TCommand">命令类型</typeparam>
/// <typeparam name="TResponse">响应类型</typeparam>
public interface ICommandHandler<TCommand, TResponse> : IHandler
    where TCommand : IRequest<TResponse>
{
    /// <summary>处理命令</summary>
    ValueTask<TResponse> HandleAsync(TCommand command, CancellationToken ct);

    /// <summary>非泛型桥接 — 默认实现，Handler 作者无需覆盖</summary>
    /// <remarks>同步完成路径零分配（跳过 async 状态机），异步路径按需分配</remarks>
    ValueTask<object?> IHandler.HandleAsync(IBaseRequest request, CancellationToken ct)
    {
        var task = HandleAsync((TCommand)request, ct);
        if (task.IsCompletedSuccessfully)
            return new ValueTask<object?>(task.Result);
        return AwaitAndBox(task);

        static async ValueTask<object?> AwaitAndBox(ValueTask<TResponse> vt)
            => await vt.ConfigureAwait(false);
    }
}
