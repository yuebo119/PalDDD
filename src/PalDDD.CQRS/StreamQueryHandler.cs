// ─────────────────────────────────────────────────────────────
// 📡 流式读接口层次 — IStreamQuery<TItem> / IStreamQueryHandler<TQuery,TItem>
// ─────────────────────────────────────────────────────────────
// ADR-030 D1 独立读形态：标记接口不继承 IBaseRequest、不经 Dispatcher 路由。
// 根因：Dispatcher 的 per-request scope 随管道完成销毁（Dispatcher.cs ExecutePipelineAsync
// 的 using scope），而 IAsyncEnumerable 的枚举晚于销毁——枚举期触碰任何 scoped 依赖
// （仓储、DbContext、流结束追加 EventLog）即 ObjectDisposedException，且故障形态取决于
// handler 体内触碰什么，属运行期地雷。
// 端点即作用域边界（D2）：handler 从 HttpContext.RequestServices 解析，枚举在端点
// 方法执行期内完成，请求作用域天然覆盖全枚举期。
//
// 与 CommandHandler.cs / QueryHandler.cs 的差异：无 IHandler 非泛型桥接——流不经
// Dispatcher.ExecutePipelineAsync，不存在零反射分派需求；DI 直接解析闭合泛型接口
// （AddPalStreamQueryHandler 的 TryAddScoped），闭合泛型 AOT 安全。
//
// 二元性规则（ADR-030 被否方案末行）：IStreamQuery 与 IQuery 是两条独立读路径，
// 与 BCL 的 Task/IAsyncEnumerable 同构——"单值响应用 IQuery、流式响应用 IStreamQuery"
// 一句话可治理，不做继承统一（统一会把流降格为普通返回类型，踩中上述 scope 地雷）。

namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 流式读标记与处理器
// ─────────────────────────────────────────────────────────────

/// <summary>流式查询标记接口（ADR-030 D1）——独立读形态，刻意<b>不</b>继承 <see cref="IBaseRequest"/>。</summary>
/// <remarks>
/// 不继承 <see cref="IBaseRequest"/> 是类型系统级防线：<see cref="Dispatcher.QueryAsync{TResponse}(IQuery{TResponse}, CancellationToken)"/>
/// 因此无法接受流式查询，"禁止经 QueryAsync 路由流式查询"（ADR-030 D2 成文契约）由编译器
/// 强制而非用户纪律。同一查询类型可同时实现 <see cref="IQuery{TResponse}"/> 与
/// <see cref="IStreamQuery{TItem}"/>（两入口各自注册），但两者走完全不同的执行路径。
/// </remarks>
/// <typeparam name="TItem">流元素类型——承载 <see cref="IStreamQueryHandler{TQuery, TItem}"/> 的编译时类型契约。</typeparam>
public interface IStreamQuery<TItem> { }

/// <summary>流式查询处理器（ADR-030 D1）——由端点原语从请求作用域解析并在端点执行期内枚举。</summary>
/// <typeparam name="TQuery">流式查询类型</typeparam>
/// <typeparam name="TItem">流元素类型</typeparam>
public interface IStreamQueryHandler<TQuery, TItem>
    where TQuery : IStreamQuery<TItem>
{
    /// <summary>
    /// 产出流元素。<b>契约：必须响应 <paramref name="ct"/></b>（ADR-030 D5）——端点传
    /// <c>HttpContext.RequestAborted</c>，客户端断开时取消传播进本方法的 finally
    ///（停计费、释放 provider 连接）；枚举侧同时以 <c>WithCancellation</c> 兜底。
    /// </summary>
    /// <remarks>
    /// 实现约定：迭代器方法的 <paramref name="ct"/> 形参应标注
    /// <c>[EnumeratorCancellation]</c>（System.Runtime.CompilerServices）——消费者
    /// <c>await foreach (... .WithCancellation(ct))</c> 传入的令牌经该标注优先于实现
    /// 内部令牌；接口层无法携带该特性（CS8424：非异步迭代器方法上不起作用），故以本
    /// 约定成文。
    /// </remarks>
    /// <param name="query">流式查询</param>
    /// <param name="ct">取消令牌——[EnumeratorCancellation] 语义：await foreach 消费者传入的令牌优先于实现内部令牌</param>
    /// <returns>流元素序列——枚举必须发生在端点执行期内（D2），禁止把序列传出端点</returns>
    IAsyncEnumerable<TItem> HandleAsync(TQuery query, CancellationToken ct);
}
