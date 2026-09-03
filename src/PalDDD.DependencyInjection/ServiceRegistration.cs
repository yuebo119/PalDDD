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
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<CQRS.Dispatcher>();
        services.TryAddScoped<Message.IDomainEventDispatcher, Message.IterativeDomainEventDispatcher>();
        services.TryAddSingleton<Message.IMessageBroker, Message.NullMessageBroker>();
        services.TryAddSingleton<Message.DomainEventDispatcherOptions>();

        services.TryAddSingleton<HandlerCollector>();
        // P3 修复：TryAddEnumerable 防重——AddPalDDD 双调不双 Registrar
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HandlerRegistrar>());

        return services;
    }

    /// <summary>注册 ByteAether.Ulid 统一 ID 生成器。</summary>
    /// <remarks>将 <see cref="Core.Identity.IPalIdGenerator"/> 注册为单例，供应用层替换 ID 生成策略消费
    ///（v37 P3 勘正：框架核心类型不经 DI 消费 ID 生成器——框架核心直调 <c>PalUlid.New</c>，
    /// 原声明"提供给 DomainEvent、OutboxMessage 等核心类型使用"失实，IPalIdGenerator 在框架内无核心消费方）。</remarks>
    public static IServiceCollection AddPalIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Core.Identity.IPalIdGenerator, Core.Identity.ByteAetherUlidGenerator>();
        return services;
    }

    /// <summary>注册推荐的核心栈：<see cref="AddPalDDD"/> + <see cref="AddPalPipelineBehaviors"/> + <see cref="AddPalIdentity"/>。</summary>
    /// <remarks>只包含 AOT 安全的核心、CQRS、内存消息能力；序列化、持久化、Broker、ASP.NET Core 适配器仍由对应包显式注册。</remarks>
    public static IServiceCollection AddPalCoreStack(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddPalDDD().AddPalPipelineBehaviors().AddPalIdentity();
    }

    /// <summary>注册 PalDDD 默认栈；当前等价于 <see cref="AddPalCoreStack"/>。</summary>
    /// <remarks>为新用户提供低认知入口，同时不越过 Clean Architecture 边界自动引用基础设施适配器。</remarks>
    public static IServiceCollection AddPalFullStack(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddPalCoreStack();
    }

    /// <summary>添加常用管道行为（验证 + 日志）— 开放泛型注册（仅 JIT / 非 Native AOT 场景）</summary>
    /// <remarks>
    /// 注册两个开放泛型管道行为：<br/>
    /// - <see cref="CQRS.ValidationBehavior{TRequest,TResponse}"/>：自动调用所有 IPalValidator<br/>
    /// - <see cref="CQRS.LoggingBehavior{TRequest,TResponse}"/>：编译时日志记录
    /// <para>
    /// ⚠️ <b>Native AOT 限制</b>：开放泛型注册在 AOT 下对<b>值类型响应</b>（Unit/int/Guid 等）抛
    /// <c>AotCannotCreateGenericValueType</c>（DI CallSiteFactory 硬校验，无配置可关）。AOT 场景请改用
    /// 闭合注册版 <c>AddPalPipelineBehaviors&lt;TRequest, TResponse&gt;()</c>，或
    /// <c>AddPalCommandHandler&lt;,&gt;()</c> / <c>AddPalQueryHandler&lt;,&gt;()</c>（内部自动闭合注册）。
    /// </para>
    /// <para><b>互斥语义</b>：与闭合注册版先到先得——服务集合已存在开放泛型 <c>IPipelineBehavior&lt;,&gt;</c>
    /// 注册（本方法重复调用）或闭合注册（AddPalCommandHandler/AddPalQueryHandler 自动注册）时本方法跳过，
    /// 避免两种注册叠加导致 behavior 重复执行（验证/日志各跑两次）。
    /// v25 P3 勘正族 C10：闭合注册的匹配范围是<b>任意</b> <c>IPipelineBehavior&lt;,&gt;</c> 闭合注册——
    /// 含用户自定义 behavior，不仅限内置 Validation/Logging；用户先注册任意闭合 behavior 即抑制
    /// 内置 Validation/Logging 开放注册（先到先得）。匹配行为保持不变，仅显式声明。</para>
    /// </remarks>
    public static IServiceCollection AddPalPipelineBehaviors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // 互斥（替代十七轮哨兵 PalPipelineBehaviorsMarker，见 P0 决策）：检查服务集合实际状态而非
        // 代理标记——开放注册已存在（防双调）或闭合注册已存在（AddPalCommandHandler/AddPalQueryHandler
        // 已自动闭合注册）时跳过，避免 GetServices<IPipelineBehavior<T,R>>() 叠加出 4 个 behavior
        // （验证/日志重复执行）。哨兵是注册进容器但永不消费的死类，且无法防开放版+闭合版混用叠加；
        // 互斥检查同时解决自去重与跨方法协作两个问题。
        if (HasOpenGenericPipelineBehaviors(services) || HasClosedGenericPipelineBehaviors(services))
            return services;

        // v25 P3 勘正族 C4：AOT 门禁移至互斥检查之后——原顺序先 ThrowIfAotNotSupported() 再查互斥，
        // 被互斥跳过（已有闭合注册、无需开放泛型，AOT 下合法的用法组合）的调用也会触发
        // AOT 抛异常；互斥先行后，AOT 门禁仅在确定要注册开放泛型时生效。
        ThrowIfAotNotSupported();

        services.AddScoped(typeof(CQRS.IPipelineBehavior<,>), typeof(CQRS.ValidationBehavior<,>));
        services.AddScoped(typeof(CQRS.IPipelineBehavior<,>), typeof(CQRS.LoggingBehavior<,>));
        return services;
    }

    /// <summary>添加常用管道行为（验证 + 日志）— 闭合泛型注册（Native AOT 安全，值类型响应可用）</summary>
    /// <remarks>
    /// 与开放版 <c>AddPalPipelineBehaviors()</c> 的区别：<br/>
    /// - <b>闭合注册</b>（本方法）：注册 <c>IPipelineBehavior&lt;TRequest,TResponse&gt;</c> 的闭合实现，<br/>
    ///   DI 走 <c>TryCreateExact</c> 路径——闭合类型在编译期可见，native code 已生成，<br/>
    ///   <b>不经值类型校验</b>（AotCannotCreateGenericValueType 只发生在开放泛型解析路径）。<br/>
    /// - <b>开放注册</b>（<c>AddPalPipelineBehaviors()</c>）：运行时开放泛型解析，AOT 下值类型响应抛异常。
    /// <para><b>推荐用法</b>：AOT 场景用 <see cref="AddPalCommandHandler{TCommand, TResponse, THandler}"/> /
    /// <see cref="AddPalQueryHandler{TQuery, TResponse, THandler}"/>（内部自动调用本方法闭合注册），
    /// 或为每个命令/查询显式调用本方法。</para>
    /// <para><b>互斥语义</b>：与开放版先到先得——服务集合已存在开放泛型 <c>IPipelineBehavior&lt;,&gt;</c>
    /// 注册（旧代码显式调用开放版）时本方法跳过，避免两种注册叠加导致 behavior 重复执行。</para>
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

        // TryAddEnumerable：按 ServiceType+ImplementationType 对去重，闭合注册多次调用不重复
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

    /// <summary>注册 ZLogger + IPalLogger&lt;T&gt; 日志门面（追加语义）。</summary>
    /// <remarks>
    /// v2.0.0 二进制兼容重载（二轮评审 P2-NEW-2）：主签名改为 3 参后，已针对 1 参
    /// <c>AddPalLogging(IServiceCollection)</c> 编译的消费者升级本包会 MissingMethodException——
    /// 本重载委托至主签名，恢复二进制兼容；行为与主签名默认参数一致（追加，不清除）。
    /// 注意：v2.0.0 发布时的旧行为（ClearProviders + SetMinimumLevel(Information)）<b>未</b>
    /// 恢复——独占接管请显式传 <c>clearProviders: true</c>。
    /// </remarks>
    public static IServiceCollection AddPalLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services); // v17 片E 假修勘正
        return services.AddPalLogging(clearProviders: false, minimumLevel: null);
    }

    /// <summary>注册 ZLogger + IPalLogger&lt;T&gt; 日志门面。</summary>
    /// <remarks>
    /// 追加 ZLogger 控制台 JSON Provider 并注册 <see cref="IPalLogger{T}"/> → <see cref="PalLogger{T}"/> 单例适配。<br/>
    /// <b>追加语义（评审 P2-2 修复）</b>：默认<b>不</b>清除用户已配置的日志 Provider、
    /// <b>不</b>覆盖最低级别——旧行为 <c>ClearProviders()</c> 会静默丢弃调用方的全部日志配置，
    /// 属隐式破坏性副作用。如需独占式接管日志管道，传 <paramref name="clearProviders"/>: true；
    /// 如需指定最低级别，传 <paramref name="minimumLevel"/>（未传时不触碰，尊重宿主默认/appsettings）。
    /// <para>
    /// ⚠️ <b>已知可观测变化（二轮评审验证轮）</b>：ASP.NET Core 默认宿主自带 Console
    /// Provider（<c>WebApplication.CreateBuilder</c> 默认添加 Console/Debug/EventSource 等，
    /// 官方文档"Logging providers"节）——追加语义下宿主 plain-text Console 与本方法添加的
    /// ZLoggerConsole JSON 并存，<b>每条日志双份输出</b>。需要单输出时传 <c>clearProviders: true</c>。
    /// <para>
    /// v35 P3 防重：同一 <see cref="IServiceCollection"/> 重复调用本方法为幂等 no-op——
    /// ZLogger provider 只注册一次（哨兵 descriptor 检测）；不同集合（多 Host / 测试各自
    /// 建 host）互不影响，各自正常注册。重复调用时的 <paramref name="clearProviders"/>/
    /// <paramref name="minimumLevel"/> 不再生效（首次配置为准）。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPalLogging(
        this IServiceCollection services,
        bool clearProviders = false,
        LogLevel? minimumLevel = null)
    {
        ArgumentNullException.ThrowIfNull(services); // v17 片E 假修勘正（第 9 处守卫）
        // v35 P3 防重：AddZLoggerConsole 非幂等——同一 IServiceCollection 重复调用会注册
        // 第二个 ZLogger provider，每条日志双份输出（区别于上方 remarks 声明的"宿主自带
        // Console 与 ZLogger 并存"——那是不同 provider 共存，这是同 provider 自我重复）。
        // 防重形态：哨兵 descriptor——同一集合二次调用检测到 PalLoggingMarker 即整体幂等
        // 跳过。刻意不用进程级静态 Interlocked 标志位：AddPalLogging 按集合生效（多 Host /
        // 测试各建独立 host 均应获得 ZLogger），静态标志会静默吞掉第二个宿主的注册
        // （测试套件 13 处各自 new ServiceCollection 即此场景）。
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(PalLoggingMarker))
                return services; // 同集合已调用过——幂等跳过，防 ZLogger 双份输出
        }
        services.AddLogging(logging =>
        {
            if (clearProviders)
                logging.ClearProviders();
            if (minimumLevel is { } level)
                logging.SetMinimumLevel(level);
            logging.AddZLoggerConsole(options => options.UseJsonFormatter());
        });
        // 哨兵最后注册——仅当 ZLogger 注册路径完整走完后标记本集合"已处理"
        services.TryAddSingleton<PalLoggingMarker>();
        // v26 P3 声明：开放泛型注册在 AOT 下对值类型 T 解析会抛 AotCannotCreateGenericValueType
        // （同 AddPalPipelineBehaviors 的 ThrowIfAotNotSupported 注释自述机制——DI CallSiteFactory
        // 硬校验，开放泛型 + 值类型实参组合无 native code 可用）。T 惯例为引用类型
        // （logger 类别，如 IPalLogger<LoggingBehavior<,>>），值类型类别无业务意义，
        // 故不设 AOT 门禁仅此声明。
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
    /// <remarks>
    /// ITM-167 声明：命令处理器注册刻意使用 <c>TryAddScoped</c>（而非 <c>TryAddEnumerable</c>）——
    /// CQRS 命令/查询按请求类型单一处理器（Dispatcher 按具体 THandler 解析），同一请求的第二个
    /// 不同 THandler 属配置错误；TryAddScoped 保首注册胜出（确定性）。事件处理器
    /// （<see cref="AddPalEventHandler{TEvent,THandler}"/>）语义不同：同一事件允许多个 Handler，
    /// 故用 TryAddEnumerable 按 ServiceType+ImplementationType 对去重。
    /// </remarks>
    public static IServiceCollection AddPalCommandHandler<TCommand, TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.Interfaces)]
    THandler>(this IServiceCollection services)
        where TCommand : CQRS.IRequest<TResponse>
        where THandler : class, CQRS.ICommandHandler<TCommand, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        // 评审 P2-1 修复：自动确保核心注册（AddPalDDD 全 TryAdd，幂等零覆盖）——
        // 漏调 AddPalDDD 时旧实现把错误延迟到首个请求（HandlerRegistrar 缺失 →
        // Freeze 不发生 → 一律 HandlerNotFound 404），现在注册期即完成闭环。
        services.AddPalDDD();

        // ITM-220 修复（三十二轮）：同一命令的不同 Handler 在注册期快速失败——
        // 原实现静默追加 Marker，Dispatcher 后注册者覆盖先注册者（配置错误无诊断）。
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(HandlerMarker)
                && descriptor.ImplementationInstance is HandlerMarker existing
                && existing.RequestType == typeof(TCommand))
            {
                if (existing.HandlerType != typeof(THandler))
                    throw new InvalidOperationException(
                        $"Duplicate handler for {typeof(TCommand).Name}: {existing.HandlerType.Name} and {typeof(THandler).Name}. A request type must have exactly one handler.");
                // v54 P2：ResponseType 纵深防御校验——"同 Handler 跨 command/query 角色"场景
                // 经编译实证不可构造（两个 HandleAsync(TRequest, CT) 签名冲突 CS0111 + DIM 桥接
                // IHandler 无最具体实现 CS8705），本校验防御接口未来演化（如 DIM 形态变化
                // 解除签名冲突）时静默吞注册的回归；当前不可达，代价一次类型比较
                if (existing.ResponseType != typeof(TResponse))
                    throw new InvalidOperationException(
                        $"Handler {typeof(THandler).Name} is already registered for {typeof(TCommand).Name} with response {existing.ResponseType.Name}; cannot re-register with response {typeof(TResponse).Name} (command/query role conflict).");
                return services; // 同一 Handler 同角色重复注册——幂等跳过
            }
        }

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
    /// <remarks>
    /// ITM-167 声明：同 <see cref="AddPalCommandHandler{TCommand,TResponse,THandler}"/>——
    /// 查询处理器按请求类型单一处理器，TryAddScoped 保首注册胜出；多处理器聚合语义
    /// 请用事件处理器注册（TryAddEnumerable）。
    /// </remarks>
    public static IServiceCollection AddPalQueryHandler<TQuery, TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.Interfaces)]
    THandler>(this IServiceCollection services)
        where TQuery : CQRS.IQuery<TResponse>
        where THandler : class, CQRS.IQueryHandler<TQuery, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        // 评审 P2-1 修复：同命令——自动确保核心注册（幂等），消除漏调 AddPalDDD 的 fail-late
        services.AddPalDDD();

        // ITM-220 修复（三十二轮）：同命令——查询的不同 Handler 也注册期快速失败
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(HandlerMarker)
                && descriptor.ImplementationInstance is HandlerMarker existing
                && existing.RequestType == typeof(TQuery))
            {
                if (existing.HandlerType != typeof(THandler))
                    throw new InvalidOperationException(
                        // v53 P3：中性措辞——同一类型同时实现 ICommand 与 IQuery 时，先注册的命令 Handler 被误标为 query handler
                        $"Duplicate handler for {typeof(TQuery).Name}: {existing.HandlerType.Name} and {typeof(THandler).Name}. A request type must have exactly one handler.");
                // v54 P2：ResponseType 纵深防御校验（对称 command 侧，场景实证见 command 侧注释）
                if (existing.ResponseType != typeof(TResponse))
                    throw new InvalidOperationException(
                        $"Handler {typeof(THandler).Name} is already registered for {typeof(TQuery).Name} with response {existing.ResponseType.Name}; cannot re-register with response {typeof(TResponse).Name} (command/query role conflict).");
                return services; // 同一 Handler 同角色重复注册——幂等跳过
            }
        }

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
        ArgumentNullException.ThrowIfNull(services);
        // v22 E-2：对齐 Command/Query 的 P2-1 修复——自动补核心注册（幂等）
        services.AddPalDDD();
        // P3 修复（八轮评审）→ P1 修正（十七轮）：八轮把三个注册统一 TryAddScoped 防重，
        // 但 TryAdd 按 ServiceType 去重——同事件第二个不同 THandler 的接口注册被静默吞掉
        // （运行探针实证 GetServices<IEventHandler<T>> 只剩首个 handler，破坏注释自述的
        // IEnumerable 聚合语义；PD19 变体：修复时未枚举多 handler 场景）。
        // 正确形态：接口注册用 TryAddEnumerable（按 ServiceType+ImplementationType 对去重——
        // 同 handler 双调仍防重、不同 handler 均保留）；具体注册保持 TryAddScoped。
        services.TryAddScoped<THandler>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Message.IEventHandler<TEvent>, THandler>());
        // 注册到非泛型接口以便 IterativeDomainEventDispatcher 通过 IEnumerable<IEventHandler> 聚合
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Message.IEventHandler, THandler>());
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
        // v27 P3（v26 M2 批次漏网）：全仓构造守卫——同文件姊妹 HandlerRegistrar:391 已有，本构造补齐
        ArgumentNullException.ThrowIfNull(markers);
        Markers = markers.ToImmutableArray();
    }
}

/// <summary>启动时注册 Handler 到 Dispatcher — 零反射，仅消费编译时已知的类型标记</summary>
/// <remarks>
/// ⚠️ 本注册器是 <c>IHostedService</c>——仅在 IHost 宿主（HostBuilder/WebApplicationBuilder）
/// 启动时执行。纯 <c>BuildServiceProvider()</c> 场景（单元测试/控制台工具）IHostedService
/// 不会运行：Dispatcher 不 Freeze、Marker 不消费，<c>SendAsync</c> 将抛 HandlerNotFound。
/// 此类场景请改用 IHost 宿主，或手动解析 HandlerCollector 并调用 Dispatcher.Register/Freeze。
/// </remarks>
internal sealed class HandlerRegistrar : IHostedService
{
    private readonly CQRS.Dispatcher _dispatcher;
    private readonly HandlerCollector _collector;

    public HandlerRegistrar(CQRS.Dispatcher dispatcher, HandlerCollector collector)
    {
        ArgumentNullException.ThrowIfNull(dispatcher); // v26 P3（ITM-284 对齐）：全仓构造守卫
        ArgumentNullException.ThrowIfNull(collector);
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

/// <summary>
/// v35 P3：<see cref="ServiceRegistration.AddPalLogging(Microsoft.Extensions.DependencyInjection.IServiceCollection, bool, Microsoft.Extensions.Logging.LogLevel?)"/> 防重哨兵——仅作
/// ServiceDescriptor 标记（同集合二次调用据此幂等跳过 ZLogger 注册），无运行时行为。
/// internal + 无依赖，不影响容器解析。
/// </summary>
internal sealed class PalLoggingMarker
{
}
