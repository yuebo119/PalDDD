// ─────────────────────────────────────────────────────────────
// 🏗️ 全局 DI 注册 — AddPalDDD / AddPalOutbox / AddPalPipelineBehaviors 等
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.DependencyInjection.Logging;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ZLogger;

namespace PalDDD.DependencyInjection;

/// <summary>PalDDD 核心 DI 注册 — 只注册内存总线、CQRS 分发和默认空消息代理</summary>
/// <remarks>
/// 所有 Handler 通过显式注册 API 或源码生成器注册，<b>不使用程序集扫描</b>——100% Native AOT 兼容。
/// 具体序列化、持久化、Outbox/Inbox、Broker 和 ASP.NET Core 能力由对应适配包显式注册。
/// </remarks>
public static class ServiceRegistration
{
    /// <summary>注册 PalDDD 核心：事件总线 + 分发器 + 空消息代理 + 迭代事件派发</summary>
    public static IServiceCollection AddPalDDD(this IServiceCollection services)
    {
        services.TryAddSingleton<CQRS.Dispatcher>();
        services.TryAddScoped<Message.IDomainEventDispatcher, Message.IterativeDomainEventDispatcher>();
        services.TryAddSingleton<Message.IMessageBroker, Message.NullMessageBroker>();
        services.TryAddSingleton<Message.DomainEventDispatcherOptions>();

        services.TryAddSingleton<HandlerCollector>();
        services.AddHostedService<HandlerRegistrar>();

        return services;
    }

    /// <summary>注册 ByteAether.Ulid 统一 ID 生成器。</summary>
    /// <remarks>将 <see cref="Core.Identity.IPalIdGenerator"/> 注册为单例，提供给 DomainEvent、OutboxMessage 等核心类型使用。</remarks>
    public static IServiceCollection AddPalIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton<Core.Identity.IPalIdGenerator, Core.Identity.ByteAetherUlidGenerator>();
        return services;
    }

    /// <summary>注册推荐的核心栈：<see cref="AddPalDDD"/> + <see cref="AddPalPipelineBehaviors"/> + <see cref="AddPalIdentity"/>。</summary>
    /// <remarks>只包含 AOT 安全的核心、CQRS、内存消息能力；序列化、持久化、Broker、ASP.NET Core 适配器仍由对应包显式注册。</remarks>
    public static IServiceCollection AddPalCoreStack(this IServiceCollection services)
        => services.AddPalDDD().AddPalPipelineBehaviors().AddPalIdentity();

    /// <summary>注册 PalDDD 默认栈；当前等价于 <see cref="AddPalCoreStack"/>。</summary>
    /// <remarks>为新用户提供低认知入口，同时不越过 Clean Architecture 边界自动引用基础设施适配器。</remarks>
    public static IServiceCollection AddPalFullStack(this IServiceCollection services)
        => services.AddPalCoreStack();

    /// <summary>添加常用管道行为（验证 + 日志）— 开放泛型注册（仅 JIT/非 AOT 场景）</summary>
    /// <remarks>
    /// 注册两个开放泛型管道行为：<br/>
    /// - <see cref="CQRS.ValidationBehavior{TRequest,TResponse}"/>：自动调用所有 IPalValidator<br/>
    /// - <see cref="CQRS.LoggingBehavior{TRequest,TResponse}"/>：编译时日志记录
    /// <para>
    /// ⚠️ <b>Native AOT 限制</b>：开放泛型注册在 AOT 下对<b>值类型响应</b>（Unit/int/Guid 等）抛
    /// <c>AotCannotCreateGenericValueType</c>（DI CallSiteFactory 硬校验，无配置可关）。<br/>
    /// <b>AOT 场景请使用</b>：闭合注册版 <c>AddPalPipelineBehaviors&lt;TRequest, TResponse&gt;()</c>
    /// 或 <c>AddPalCommandHandler&lt;,&gt;()</c> / <c>AddPalQueryHandler&lt;,&gt;()</c>（内部自动闭合注册）。
    /// </para>
    /// <para><b>互斥语义</b>：与闭合注册版先到先得——若服务集合已存在闭合
    /// <c>IPipelineBehavior&lt;,&gt;</c> 注册（AddPalCommandHandler/AddPalQueryHandler 自动注册），本方法跳过注册，
    /// 避免两种注册叠加导致 behavior 重复执行（验证/日志各跑两次）。</para>
    /// </remarks>
    public static IServiceCollection AddPalPipelineBehaviors(this IServiceCollection services)
    {
        ThrowIfAotNotSupported();
        // 互斥：闭合注册已存在（AddPalCommandHandler/AddPalQueryHandler 已自动闭合注册）时跳过开放版——
        // 避免 GetServices<IPipelineBehavior<T,R>>() 叠加出 4 个 behavior（验证/日志重复执行）。
        if (HasClosedGenericPipelineBehaviors(services)) return services;
        services.AddScoped(typeof(CQRS.IPipelineBehavior<,>), typeof(CQRS.ValidationBehavior<,>));
        services.AddScoped(typeof(CQRS.IPipelineBehavior<,>), typeof(CQRS.LoggingBehavior<,>));
        return services;
    }

    /// <summary>添加常用管道行为（验证 + 日志）— 闭合泛型注册（AOT 安全，值类型响应可用）</summary>
    /// <remarks>
    /// 与开放版 <c>AddPalPipelineBehaviors()</c> 的区别：<br/>
    /// - <b>闭合注册</b>（本方法）：注册 <c>IPipelineBehavior&lt;TRequest,TResponse&gt;</c> 的闭合实现，<br/>
    ///   DI 走 <c>TryCreateExact</c> 路径——闭合类型在编译期可见，native code 已生成，<br/>
    ///   <b>不经值类型校验</b>（AotCannotCreateGenericValueType 只发生在开放泛型解析路径）。<br/>
    /// - <b>开放注册</b>（<c>AddPalPipelineBehaviors()</c>）：运行时开放泛型解析，AOT 下值类型响应抛异常。
    /// <para><b>推荐用法</b>：AOT 场景用 <see cref="AddPalCommandHandler{TCommand, TResponse, THandler}"/> /
    /// <see cref="AddPalQueryHandler{TQuery, TResponse, THandler}"/>（内部自动调用本方法闭合注册），
    /// 或为每个命令/查询显式调用本方法。</para>
    /// <para><b>互斥语义</b>：与开放版先到先得——若服务集合已存在开放泛型
    /// <c>IPipelineBehavior&lt;,&gt;</c> 注册（旧代码显式调用开放版），本方法跳过注册，
    /// 避免两种注册叠加导致 behavior 重复执行。</para>
    /// </remarks>
    /// <typeparam name="TRequest">请求类型</typeparam>
    /// <typeparam name="TResponse">响应类型（值类型如 Unit 也可，AOT 安全）</typeparam>
    public static IServiceCollection AddPalPipelineBehaviors<TRequest, TResponse>(this IServiceCollection services)
        where TRequest : CQRS.IRequest<TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);
        // 互斥：开放泛型注册已存在时跳过闭合注册（先到先得）——
        // 避免 GetServices<IPipelineBehavior<T,R>>() 叠加出 4 个 behavior（验证/日志重复执行）。
        if (HasOpenGenericPipelineBehaviors(services)) return services;

        // TryAddEnumerable：闭合类型注册去重，多次调用不重复注册
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CQRS.IPipelineBehavior<TRequest, TResponse>, CQRS.ValidationBehavior<TRequest, TResponse>>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CQRS.IPipelineBehavior<TRequest, TResponse>, CQRS.LoggingBehavior<TRequest, TResponse>>());
        return services;
    }

    /// <summary>是否已注册开放泛型管道行为（<c>IPipelineBehavior&lt;,&gt;</c> 开放定义）</summary>
    private static bool HasOpenGenericPipelineBehaviors(IServiceCollection services)
        => services.Any(sd => sd.ServiceType == typeof(CQRS.IPipelineBehavior<,>));

    /// <summary>是否已注册任何闭合管道行为（<c>IPipelineBehavior&lt;,&gt;</c> 闭合实例，排除开放泛型定义）</summary>
    private static bool HasClosedGenericPipelineBehaviors(IServiceCollection services)
        => services.Any(sd => sd.ServiceType.IsGenericType
                              && !sd.ServiceType.IsGenericTypeDefinition
                              && sd.ServiceType.GetGenericTypeDefinition() == typeof(CQRS.IPipelineBehavior<,>));

    /// <summary>Native AOT 检测 — 开放泛型注册在 AOT 下对值类型响应不可用，提前给出清晰错误。</summary>
    /// <exception cref="NotSupportedException">Native AOT 发布时抛出，提示使用闭合注册。</exception>
    private static void ThrowIfAotNotSupported()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                "AddPalPipelineBehaviors()（开放泛型注册）在 Native AOT 下对值类型响应（Unit/int/Guid）不可用——" +
                "DI CallSiteFactory 会抛 AotCannotCreateGenericValueType。请改用闭合注册：" +
                "AddPalPipelineBehaviors<TRequest, TResponse>() 或 AddPalCommandHandler<TCommand, TResponse, THandler>() / AddPalQueryHandler<TQuery, TResponse, THandler>()（内部自动闭合注册）。");
        }
    }

    /// <summary>注册 ZLogger + IPalLogger&lt;T&gt; 日志门面。</summary>
    /// <remarks>
    /// 清除已有 Provider，设置最低级别为 Information，添加 ZLogger 控制台 JSON 格式化器。<br/>
    /// 注册 <see cref="IPalLogger{T}"/> → <see cref="PalLogger{T}"/> 单例适配。
    /// </remarks>
    public static IServiceCollection AddPalLogging(this IServiceCollection services)
    {
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddZLoggerConsole(options => options.UseJsonFormatter());
        });
        services.TryAddSingleton(typeof(IPalLogger<>), typeof(PalLogger<>));
        return services;
    }

    // ═══════════════════════════════════════════════════════════════
    // 显式 Handler 注册 API — AOT 安全，零反射
    // typeof(T) 均为编译时常量，源码生成器自动调用这些方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>显式注册命令处理器（AOT 安全）</summary>
    /// <typeparam name="TCommand">命令类型</typeparam>
    /// <typeparam name="TResponse">响应类型</typeparam>
    /// <typeparam name="THandler">处理器类型</typeparam>
    public static IServiceCollection AddPalCommandHandler<TCommand, TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.Interfaces)]
    THandler>(this IServiceCollection services)
        where TCommand : CQRS.IRequest<TResponse>
        where THandler : class, CQRS.ICommandHandler<TCommand, TResponse>
    {
        services.TryAddScoped<THandler>();
        services.TryAddScoped<CQRS.ICommandHandler<TCommand, TResponse>, THandler>();
        // 闭合注册内置管道行为（AOT 安全——值类型响应如 Unit 不触发 AotCannotCreateGenericValueType）
        services.AddPalPipelineBehaviors<TCommand, TResponse>();
        // 注册标记：typeof(TCommand) 和 typeof(THandler) 均为编译时常量
        services.AddSingleton(new HandlerMarker(
            requestType: typeof(TCommand),
            handlerType: typeof(THandler),
            responseType: typeof(TResponse),
            executor: CQRS.Dispatcher.ExecutePipelineAsync<TCommand, TResponse, THandler>));
        return services;
    }

    /// <summary>显式注册查询处理器（AOT 安全）</summary>
    /// <typeparam name="TQuery">查询类型</typeparam>
    /// <typeparam name="TResponse">响应类型</typeparam>
    /// <typeparam name="THandler">处理器类型</typeparam>
    public static IServiceCollection AddPalQueryHandler<TQuery, TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.Interfaces)]
    THandler>(this IServiceCollection services)
        where TQuery : CQRS.IQuery<TResponse>
        where THandler : class, CQRS.IQueryHandler<TQuery, TResponse>
    {
        services.TryAddScoped<THandler>();
        services.TryAddScoped<CQRS.IQueryHandler<TQuery, TResponse>, THandler>();
        // 闭合注册内置管道行为（AOT 安全——值类型响应如 int/Guid 不触发 AotCannotCreateGenericValueType）
        services.AddPalPipelineBehaviors<TQuery, TResponse>();
        services.AddSingleton(new HandlerMarker(
            requestType: typeof(TQuery),
            handlerType: typeof(THandler),
            responseType: typeof(TResponse),
            executor: CQRS.Dispatcher.ExecutePipelineAsync<TQuery, TResponse, THandler>));
        return services;
    }

    /// <summary>显式注册领域事件处理器（AOT 安全）</summary>
    /// <remarks>事件处理器默认注册为 Scoped，允许处理器安全依赖仓储、DbContext 或 Unit of Work。</remarks>
    /// <typeparam name="TEvent">领域事件类型</typeparam>
    /// <typeparam name="THandler">处理器类型</typeparam>
    public static IServiceCollection AddPalEventHandler<TEvent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    THandler>(this IServiceCollection services)
        where TEvent : Core.DomainEvent
        where THandler : class, Message.IEventHandler<TEvent>
    {
        services.TryAddScoped<THandler>();
        services.AddScoped<Message.IEventHandler<TEvent>>(sp => sp.GetRequiredService<THandler>());
        // 注册到非泛型接口以便 IterativeDomainEventDispatcher 通过 IEnumerable<IEventHandler> 聚合
        services.AddScoped<Message.IEventHandler>(sp => sp.GetRequiredService<THandler>());
        return services;
    }
}

// ═══════════════════════════════════════════════════════════════
// 内部类型：Handler 标记收集器 + 启动注册器
// ═══════════════════════════════════════════════════════════════

/// <summary>Handler 类型映射标记 — 启动时由 HandlerRegistrar 消费</summary>
internal sealed class HandlerMarker
{
    public Type RequestType { get; }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    public Type HandlerType { get; }

    public Type ResponseType { get; }

    public CQRS.RequestExecutor Executor { get; }

    public HandlerMarker(
        Type requestType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type handlerType,
        Type responseType,
        CQRS.RequestExecutor executor)
    {
        RequestType = requestType;
        HandlerType = handlerType;
        ResponseType = responseType;
        Executor = executor;
    }
}

/// <summary>收集所有通过显式 API 注册的 Handler 标记 — 构造函数注入聚合所有标记</summary>
internal sealed class HandlerCollector
{
    public ImmutableArray<HandlerMarker> Markers { get; }

    public HandlerCollector(IEnumerable<HandlerMarker> markers)
    {
        Markers = markers.ToImmutableArray();
    }
}

/// <summary>启动时注册 Handler 到 Dispatcher — 零反射，仅消费编译时已知的类型标记</summary>
internal sealed class HandlerRegistrar : IHostedService
{
    private readonly CQRS.Dispatcher _dispatcher;
    private readonly HandlerCollector _collector;

    public HandlerRegistrar(CQRS.Dispatcher dispatcher, HandlerCollector collector)
    {
        _dispatcher = dispatcher;
        _collector = collector;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var marker in _collector.Markers)
        {
            _dispatcher.Register(marker.RequestType, marker.HandlerType, marker.ResponseType, marker.Executor);
        }

        _dispatcher.Freeze();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
