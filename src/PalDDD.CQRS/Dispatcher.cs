// ═══════════════════════════════════════════════════════════════
// 🎯 分发器 — CQRS 命令/查询路由中心（零反射、零 MakeGenericType）
// ═══════════════════════════════════════════════════════════════
//
// 💡 怎么做？Handler 通过 Register<TCmd,TResp,THandler>() 注册，
//   FrozenDictionary<Type,HandlerEntry> 存储，Freeze() 后不可变。
//   O(1) 查找 + 零 GC — 比 ConcurrentDictionary 更快。
//
// 💡 为什么 Freeze() → GetFrozenEntries()？消除重复的双重检查锁定。
//
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// CQRS 分发器 — 增量注册 + FrozenDictionary + 管道执行
// ─────────────────────────────────────────────────────────────

/// <summary>为具体请求类型生成或注册的 AOT 安全请求执行器。</summary>
public delegate ValueTask<object?> RequestExecutor(IServiceProvider services, IBaseRequest request, CancellationToken ct);

/// <summary>命令/查询分发器 — 零 MakeGenericType、AOT 安全、管道行为管线集成</summary>
/// <remarks>
/// 通过 <see cref="IPipelineBehavior"/> 非泛型接口 + DIM 编译时类型标识属性，<br/>
/// 完全消除 <c>MakeGenericType</c> 运行时反射。<br/>
/// 同步完成路径低分配 —<br/>
/// 使用可重用 <see cref="PipelineStateMachine"/> 替代闭包链，降低每请求分配。
/// </remarks>
public sealed class Dispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private Dictionary<Type, HandlerEntry> _entries = [];
    private FrozenDictionary<Type, HandlerEntry>? _frozen;
    private readonly Lock _freezeLock = new();

    internal bool IsFrozen => _frozen is not null;

    /// <summary>冻结注册表 — 转换字典为不可变只读格式，注册完成后调用。</summary>
    internal void Freeze() => GetFrozenEntries();

    /// <summary>获取当前只读注册表 — 冻结后返回 <see cref="FrozenDictionary"/>，线程安全</summary>
    private FrozenDictionary<Type, HandlerEntry> GetFrozenEntries()
    {
        if (_frozen is { } f) return f;

        lock (_freezeLock)
        {
            if (_frozen is { } f2) return f2;
            _frozen = _entries.ToFrozenDictionary();
            _entries = null!;
            return _frozen;
        }
    }

    public Dispatcher(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 注册请求类型到预生成 executor 的映射（启动时调用）。<br/>
    /// ⚠️ <b>调用约束</b>：
    /// <para>
    /// 1. <b>单线程调用</b>：_entries 是普通 Dictionary，非线程安全。必须在启动期单线程注册，禁止运行时并发 Register。<br/>
    /// 2. <b>冻结前完成</b>：调用 Freeze() 后 _entries 转为 FrozenDictionary，再调用 Register 抛 ObjectDisposedException。<br/>
    /// 3. <b>由 HandlerRegistrar 自动调用</b>：Handler 注册由 HandlerMarker + HandlerRegistrar（IHostedService）在启动时触发，应用层通常无需手动调用。
    /// </para>
    /// </summary>
    public void Register(Type requestType, Type handlerType, Type responseType, RequestExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(responseType);
        ArgumentNullException.ThrowIfNull(executor);
        ObjectDisposedException.ThrowIf(_frozen is not null, this);

        _entries[requestType] = new HandlerEntry(handlerType, responseType, executor);
    }

    /// <summary>
    /// 注册请求类型到 Handler 类型的映射（泛型 AOT 安全路径）。<br/>
    /// ⚠️ 同 <see cref="Register(Type, Type, Type, RequestExecutor)"/> 的启动期单线程调用约束。
    /// </summary>
    public void Register<TRequest, TResponse, THandler>()
        where TRequest : IRequest<TResponse>
        where THandler : class, IHandler
        => Register(typeof(TRequest), typeof(THandler), typeof(TResponse), ExecutePipelineAsync<TRequest, TResponse, THandler>);

    /// <summary>发送命令（无返回值）— 同步完成零分配</summary>
    public ValueTask SendAsync(ICommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var vt = ExecutePipelineAsync(cmd.GetType(), cmd, ct);
        return vt.IsCompletedSuccessfully ? ValueTask.CompletedTask : DiscardResultAsync(vt);
    }

    /// <summary>发送命令（有返回值）</summary>
    [OverloadResolutionPriority(1)]
    public async ValueTask<TResponse> SendAsync<TResponse>(ICommand<TResponse> cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var result = await ExecutePipelineAsync(cmd.GetType(), cmd, ct);
        return (TResponse)result!;
    }

    /// <summary>执行查询</summary>
    public async ValueTask<TResponse> QueryAsync<TResponse>(IQuery<TResponse> q, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(q);

        var result = await ExecutePipelineAsync(q.GetType(), q, ct);
        return (TResponse)result!;
    }

    /// <summary>执行完整的管道链：行为1 → 行为2 → ... → Handler</summary>
    /// <remarks>
    /// 使用 <see cref="PipelineStateMachine"/> 替代闭包链：<br/>
    /// — 零闭包分配（原每个行为 ~72B 的编译器生成闭包类）<br/>
    /// — 零 LINQ 迭代器分配（原 Where() ~40B）<br/>
    /// — 每次请求创建新状态机实例（~40B），确保线程安全（Dispatcher 为 Singleton）
    /// </remarks>
    private async ValueTask<object?> ExecutePipelineAsync(Type requestType, IBaseRequest request, CancellationToken ct)
    {
        if (!GetFrozenEntries().TryGetValue(requestType, out var entry))
            throw new HandlerNotFoundException(requestType);

        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        return await entry.Executor(services, request, ct);
    }

    /// <summary>执行完整的泛型管道链，供源码生成器或显式注册 API 绑定。</summary>
    public static async ValueTask<object?> ExecutePipelineAsync<TRequest, TResponse, THandler>(
        IServiceProvider services,
        IBaseRequest request,
        CancellationToken ct)
        where TRequest : IRequest<TResponse>
        where THandler : class, IHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(request);

        var allBehaviors = services.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        // 转换为非泛型 IPipelineBehavior 数组——PipelineStateMachine 通过非泛型接口构建管道链。
        // behavior 数量通常 ≤2（Validation+Logging），用集合表达式 + ToImmutableArray 简洁清晰。
        var matching = allBehaviors.Select(b => (IPipelineBehavior)b).ToImmutableArray();

        var handler = (IHandler)services.GetRequiredService<THandler>();

        var pipeline = new PipelineStateMachine();
        pipeline.Reset(matching, handler, request, ct);
        return await pipeline.ExecuteNextAsync();
    }

    private static async ValueTask DiscardResultAsync(ValueTask<object?> vt)
    {
        await vt;
    }

    private sealed record HandlerEntry(Type HandlerType, Type ResponseType, RequestExecutor Executor);
}
