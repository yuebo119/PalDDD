// ─────────────────────────────────────────────────────────────
// 📋 请求接口层次 — IRequest<T> / ICommand / IQuery<T>
// ─────────────────────────────────────────────────────────────
// ICommand : IRequest<Unit> 表示不返回数据的命令。
// Dispatcher 通过 IRequest<T> 的泛型参数确定返回类型。
//
namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 请求/命令/查询标记接口
// ─────────────────────────────────────────────────────────────

/// <summary>非泛型请求标记，供基础设施层使用</summary>
public interface IBaseRequest
{ }

/// <summary>请求标记接口。命令和查询都需要实现此接口。</summary>
/// <typeparam name="TResponse">
/// 响应类型——接口体中未直接使用，但 DIM 桥接（<see cref="ICommandHandler{TCommand, TResponse}"/> /
/// <see cref="IQueryHandler{TQuery, TResponse}"/>）依赖此泛型参数确定编译时返回类型。
/// </typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S2326",
    Justification = "TResponse 是 DIM 桥接的类型级契约。泛型参数在接口体中无引用但在 ICommandHandler/IQueryHandler 中承载编译时类型信息。")]
public interface IRequest<TResponse> : IBaseRequest
{ }

/// <summary>命令标记接口，无返回值（返回 Unit）</summary>
public interface ICommand : IRequest<Core.Unit>
{ }

/// <summary>命令标记接口，有返回值</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S2326",
    Justification = "TResponse 是类型级契约，用于 ICommandHandler<T,R> 的 DIM 桥接。")]
public interface ICommand<TResponse> : IRequest<TResponse>
{ }

/// <summary>查询标记接口</summary>
public interface IQuery<TResponse> : IRequest<TResponse>
{ }
