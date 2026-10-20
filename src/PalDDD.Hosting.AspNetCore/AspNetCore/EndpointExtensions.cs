using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PalDDD.Hosting.AspNetCore;

// ─────────────────────────────────────────────────────────────
// Minimal API 端点映射
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Minimal API 命令/查询/流式查询端点映射扩展。调用方必须传入源生成 JSON metadata 以保持 AOT 安全。
/// <para>
/// ⚠️ <b>滥用控制声明（三十八轮 P2）</b>：本类映射的端点默认不含速率限制与请求体大小约束——
/// 生产部署须在宿主层配置（ASP.NET Core 内置 RateLimiter 中间件 + RequestSizeLimit），
/// 或置于网关限流之后。传输强制（TLS/HSTS）同样由宿主/反向代理层承担。
/// </para>
/// </summary>
public static class EndpointExtensions
{
    /// <summary>映射无返回值命令到 HTTP POST 端点。⚠️ 滥用控制见类 doc 声明。</summary>
    public static IEndpointConventionBuilder MapCommand<TCommand>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        JsonTypeInfo<TCommand> commandJsonTypeInfo)
        where TCommand : CQRS.ICommand
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(commandJsonTypeInfo);

        return endpoints.MapPost(pattern, async context =>
        {
            // 反序列化段（ITM-799 收口，2026-09-19）：415/畸形 JSON/验证异常/空 body
            // 四种错误的编排原在 MapCommand 两个重载（MapCommand 与
            // MapCommand<TCommand,TResponse>）逐字重复（约 40 行）——R54 勘正：原注释
            // 误写为「MapCommand/MapQuery」，MapQuery 是 GET 无反序列化段——收口为
            // ReadJsonBodyOrWriteErrorAsync 单一出处，错误语义注释随迁
            var (ok, cmd) = await ReadJsonBodyOrWriteErrorAsync(context, commandJsonTypeInfo).ConfigureAwait(false);
            if (!ok) return;

            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            try
            {
                // ITM-091 修复：SendAsync 纳入本地 try——PalValidationException 由 Dispatcher 派发时
                // 抛出（原 catch 仅覆盖 ReadFromJsonAsync），此前验证失败会逃逸为 500；
                // JsonException catch 语义保持不变（仍只覆盖反序列化）
                await dispatcher.SendAsync(cmd!, context.RequestServices, ct).ConfigureAwait(false);
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // 验证轮返工：与 ExceptionMiddleware 同款 ProblemDetails 响应体（裸 400 无 body
                // 会让客户端拿不到错误明细；日志由派发管线内记录）
                // v33 P3：派发段三处逐字重复收口到 WriteValidationProblemAsync（ct 透传）
                await WriteValidationProblemAsync(context, ex, ct).ConfigureAwait(false);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
        });
    }

    /// <summary>映射有返回值命令到 HTTP POST 端点</summary>
    public static IEndpointConventionBuilder MapCommand<TCommand, TResponse>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        JsonTypeInfo<TCommand> commandJsonTypeInfo,
        JsonTypeInfo<TResponse> responseJsonTypeInfo)
        where TCommand : CQRS.ICommand<TResponse>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(commandJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(responseJsonTypeInfo);

        return endpoints.MapPost(pattern, async context =>
        {
            // 反序列化段（ITM-799 收口）：错误编排走 ReadJsonBodyOrWriteErrorAsync 单一出处
            var (ok, cmd) = await ReadJsonBodyOrWriteErrorAsync(context, commandJsonTypeInfo).ConfigureAwait(false);
            if (!ok) return;

            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            TResponse result;
            try
            {
                // ITM-091 修复：SendAsync 纳入本地 try——PalValidationException 由 Dispatcher 派发时
                // 抛出（原 catch 仅覆盖 ReadFromJsonAsync），此前验证失败会逃逸为 500；
                // JsonException catch 语义保持不变（仍只覆盖反序列化）
                result = await dispatcher.SendAsync(cmd!, context.RequestServices, ct).ConfigureAwait(false);
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // 验证轮返工：与 ExceptionMiddleware 同款 ProblemDetails 响应体
                // v33 P3：派发段三处逐字重复收口到 WriteValidationProblemAsync（ct 透传）
                await WriteValidationProblemAsync(context, ex, ct).ConfigureAwait(false);
                return;
            }
            await context.Response.WriteAsJsonAsync(
                result,
                responseJsonTypeInfo,
                contentType: null,
                cancellationToken: ct).ConfigureAwait(false);
        });
    }

    /// <summary>映射查询到 HTTP GET 端点。查询绑定由调用方显式提供，避免运行时模型绑定反射。</summary>
    /// <remarks>
    /// v26 P3 契约声明（bindQuery 异常映射）：<paramref name="bindQuery"/> 抛
    /// <see cref="CQRS.PalValidationException"/> → 400（ValidationProblemResponse 体）；
    /// 其他异常（如原生 query string 解析的 <c>FormatException</c>/<c>UriFormatException</c>）
    /// → 500（逃逸至全局 ExceptionMiddleware）——<b>调用方应自行包装输入错误</b>：
    /// 需要将解析失败映射为 400 时请在 bindQuery 内捕获并抛 PalValidationException。
    /// </remarks>
    public static IEndpointConventionBuilder MapQuery<TQuery, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TQuery> bindQuery,
        JsonTypeInfo<TResult> responseJsonTypeInfo)
        where TQuery : CQRS.IQuery<TResult>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(bindQuery);
        ArgumentNullException.ThrowIfNull(responseJsonTypeInfo);

        return endpoints.MapGet(pattern, async context =>
        {
            TQuery query;
            try
            {
                // v25 P3 行为族 B8：bindQuery 移入本地 try——绑定函数（调用方提供，如
                // query string 解析）抛 PalValidationException 时原在 try 外逃逸为 500；
                // 对齐 MapCommand 反序列化段的 400 映射（ITM-283 形态）
                query = bindQuery(context);
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                await WriteValidationProblemAsync(context, ex, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            if (query is null)
            {
                // v65 P3：对称 MapCommand 的 `cmd is null` 守卫（:76-85）——bindQuery 在缺必需
                // 查询参数等场景可能返回 null（接口类型的 TQuery），此前直接流入
                // Dispatcher.QueryAsync 触发下游 NRE 或 HandlerNotFoundException 逃逸为 500；
                // 语义应为输入缺失（400），与命令路径统一 ProblemDetails 形态。
                // （GET 无 body，错误文案指向 query 参数而非请求体）
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    ValidationProblemResponseFactory.CreateInvalidBody(
                        "Query binding returned null. Ensure all required query parameters are provided."),
                    PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                    contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                return;
            }
            var dispatcher = context.RequestServices.GetRequiredService<CQRS.Dispatcher>();
            var ct = context.RequestAborted;
            TResult result;
            try
            {
                // ITM-168 修复：QueryAsync 纳入本地 try——PalValidationException 由 Dispatcher
                // 派发时抛出，此前验证失败逃逸为 500（MapCommand 两个方法均已映射 400，
                // 查询端点缺失同族映射）。
                result = await dispatcher.QueryAsync(query, context.RequestServices, ct).ConfigureAwait(false);
            }
            catch (PalDDD.CQRS.PalValidationException ex)
            {
                // 严格对齐 MapCommand 两个方法的响应体写法：与 ExceptionMiddleware 同款
                // ProblemDetails 响应体（裸 400 无 body 会让客户端拿不到错误明细）。
                // v33 P3：派发段三处逐字重复收口到 WriteValidationProblemAsync（ct 透传）
                await WriteValidationProblemAsync(context, ex, ct).ConfigureAwait(false);
                return;
            }
            await context.Response.WriteAsJsonAsync(
                result,
                responseJsonTypeInfo,
                contentType: null,
                cancellationToken: ct).ConfigureAwait(false);
        });
    }

    /// <summary>映射流式查询到 HTTP GET 端点（SSE，ADR-030 D8 GET 形态——浏览器原生 EventSource 兼容）。</summary>
    /// <remarks>
    /// 端点即作用域边界（D2）：handler 从 <see cref="HttpContext.RequestServices"/> 解析，
    /// 枚举在本端点方法执行期内完成，请求作用域天然覆盖全枚举期。<br/>
    /// 流程：bindQuery → <see cref="CQRS.PalValidatorRunner"/> 验证（失败 400 且在 SSE 头写出前，
    /// 状态码可回退）→ 写 SSE 头 → await foreach 枚举写帧。<br/>
    /// ⚠️ 浏览器原生 EventSource 仅支持 GET；POST 形态（承载 prompt 类请求体）须用 fetch 加
    /// ReadableStream 手工解析，见 usage.md 流式读一节。
    /// </remarks>
    /// <param name="formatFrame">完整帧定制委托（优先级最高）——返回的文本即完整帧（可含 event/id 行）；与 <paramref name="eventSelector"/>/<paramref name="idSelector"/> 同时提供时以本委托为准。</param>
    /// <param name="eventSelector">事件名选择器——提供时默认帧富化为 <c>event: {name}\n...data: {json}\n\n</c>（分型事件流，如 token/done/error）。</param>
    /// <param name="idSelector">事件 ID 选择器——提供时默认帧富化 <c>id: {id}\n</c> 行；客户端断线重连时 EventSource 经 <c>Last-Event-ID</c> 请求头回带末个 ID（读取入口见 <see cref="StreamResumeExtensions.GetLastEventId"/>）。</param>
    /// <param name="keepAliveInterval">心跳间隔——距上次心跳（流开始算首次）超过该间隔时写注释帧 <c>:keepalive</c>（规范内客户端忽略，代理/LB 空闲计时器重置）；null（默认）关闭。agent 场景的首 token 等待与工具轮间隙属结构性静默，建议 15-30 秒。实现为 MoveNext 与共享延时竞速：同步完成项走快路径零开销，异步项每项一次竞速（实测开销见 docs/performance.md 流式读链路轮）。</param>
    public static IEndpointConventionBuilder MapStreamQuery<TQuery, TItem>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TQuery> bindQuery,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        Func<TItem, string>? formatFrame = null,
        Func<TItem, string>? eventSelector = null,
        Func<TItem, string>? idSelector = null,
        TimeSpan? keepAliveInterval = null)
        where TQuery : CQRS.IStreamQuery<TItem>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(bindQuery);
        ArgumentNullException.ThrowIfNull(itemJsonTypeInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveInterval ?? TimeSpan.MaxValue, TimeSpan.Zero);

        return endpoints.MapGet(pattern, async context =>
        {
            TQuery query;
            try
            {
                query = bindQuery(context);
            }
            catch (CQRS.PalValidationException ex)
            {
                // 同 MapQuery v25 P3 行为族 B8：绑定异常 400 而非逃逸 500
                await WriteValidationProblemAsync(context, ex, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            if (query is null)
            {
                // 同 MapQuery v65 P3 对称守卫：绑定返回 null（缺必需查询参数）→ 400
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    ValidationProblemResponseFactory.CreateInvalidBody(
                        "Query binding returned null. Ensure all required query parameters are provided."),
                    PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                    contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                return;
            }
            await StreamQueryCoreAsync(
                context, query, itemJsonTypeInfo, formatFrame, eventSelector, idSelector, keepAliveInterval)
                .ConfigureAwait(false);
        });
    }

    /// <summary>映射流式查询到 HTTP POST 端点（SSE，ADR-030 D8 POST 形态——JSON body 承载 prompt 类请求体）。</summary>
    /// <remarks>
    /// 与 GET 形态同构：反序列化段复用 <see cref="ReadJsonBodyOrWriteErrorAsync{T}"/>（415/畸形
    /// JSON/空 body 错误形态不变），其后验证、SSE 头、枚举写帧全链路与 GET 形态逐字一致。<br/>
    /// ⚠️ 浏览器侧 EventSource 不发 POST——消费方须用 fetch 加 ReadableStream 手工解析帧。
    /// </remarks>
    /// <param name="formatFrame">完整帧定制委托（优先级最高）——与 <paramref name="eventSelector"/>/<paramref name="idSelector"/> 同时提供时以本委托为准。</param>
    /// <param name="eventSelector">事件名选择器——语义同 GET 重载。</param>
    /// <param name="idSelector">事件 ID 选择器——语义同 GET 重载（POST 无 EventSource 自动重连，ID 行供消费方自建续传）。</param>
    /// <param name="keepAliveInterval">心跳间隔——语义同 GET 重载；null（默认）关闭。</param>
    public static IEndpointConventionBuilder MapStreamQuery<TQuery, TItem>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        JsonTypeInfo<TQuery> queryJsonTypeInfo,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        Func<TItem, string>? formatFrame = null,
        Func<TItem, string>? eventSelector = null,
        Func<TItem, string>? idSelector = null,
        TimeSpan? keepAliveInterval = null)
        where TQuery : CQRS.IStreamQuery<TItem>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(queryJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(itemJsonTypeInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveInterval ?? TimeSpan.MaxValue, TimeSpan.Zero);

        return endpoints.MapPost(pattern, async context =>
        {
            var (ok, query) = await ReadJsonBodyOrWriteErrorAsync(context, queryJsonTypeInfo).ConfigureAwait(false);
            if (!ok) return;
            await StreamQueryCoreAsync(
                context, query!, itemJsonTypeInfo, formatFrame, eventSelector, idSelector, keepAliveInterval)
                .ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 流式端点共享核心（ADR-030 端点流程）：验证前置 → handler 解析 → SSE 头 → 枚举写帧。
    /// </summary>
    /// <remarks>
    /// 顺序说明（两处对 ADR 流程的显式偏离，均为错误语义改进、happy path 零差异）：
    /// ①handler 解析前置于 SSE 头写出之前——handler 未注册时经全局 ExceptionMiddleware
    /// 返回干净 500；若按「头先行」顺序，异常将落在头已写出之后（Kestrel 只能中断连接）；
    /// ②<see cref="CQRS.PalValidationException"/> 在此构造而非在验证段抛出——400 响应体与
    /// MapCommand/MapQuery 同形态（WriteValidationProblemAsync 单一出处）。
    /// <para>帧优先级：formatFrame（完整帧覆写）&gt; eventSelector/idSelector（框架富化帧）&gt;
    /// data-only 默认帧。</para>
    /// <para>心跳循环（keepAliveInterval 非空时启用手动枚举器竞速）：默认路径（无心跳）
    /// 保持 Phase 1 的 await foreach 快路径零额外开销；心跳路径每挂起项一次 Task 竞速，
    /// 同步完成项仍走 IsCompletedSuccessfully 快路径。取消经 Task.Delay 取消态优先于心跳
    /// 抛出，统一走 OCE 静默收尾。</para>
    /// </remarks>
    [SuppressMessage("Design", "CA1031",
        Justification = "中途异常无法回退 HTTP 状态码（SSE 头已写出），须捕获后补 event: error 帧并记录日志；取消异常单独静默收尾。")]
    private static async Task StreamQueryCoreAsync<TQuery, TItem>(
        HttpContext context,
        TQuery query,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        Func<TItem, string>? formatFrame,
        Func<TItem, string>? eventSelector,
        Func<TItem, string>? idSelector,
        TimeSpan? keepAliveInterval)
        where TQuery : CQRS.IStreamQuery<TItem>
    {
        // D3 验证前置：SSE 头写出前跑验证——失败走既有 WriteValidationProblemAsync
        // （400 ProblemDetails，与 MapCommand/MapQuery 同形态；头未写出故状态码可回退）
        var validators = context.RequestServices.GetServices<Core.IPalValidator<TQuery>>();
        var errors = CQRS.PalValidatorRunner.Validate(
            validators as Core.IPalValidator<TQuery>[] ?? validators.ToArray(), query);
        if (errors is { Count: > 0 })
        {
            await WriteValidationProblemAsync(
                context, new CQRS.PalValidationException([.. errors]), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // D6 生命周期日志（机械部分）：流开始/结束/item 数/错误——响应内容审计由 handler
        // 在流结束后追加 EventLog（框架不自动捕获载荷：隐私、体积、领域语义三样均属应用层）。
        // 与 LoggingBehavior 语义对齐：IsEnabled 门控（关闭时零插值分配）+ TimeProvider 可注入；
        // 未注册 IPalLogger（未调 AddPalLogging）时 GetService 返回 null，整体跳过。
        var logger = context.RequestServices.GetService<Core.Logging.IPalLogger<StreamQueryEndpoint<TQuery, TItem>>>();
        var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var startTimestamp = timeProvider.GetTimestamp();

        // handler 解析（请求作用域）——置于 SSE 头写出前，见方法 remarks 偏离说明①
        var handler = context.RequestServices.GetRequiredService<CQRS.IStreamQueryHandler<TQuery, TItem>>();

        // SSE 响应头（验证已通过、handler 已解析，此后状态码不可回退）
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { NoCache = true };
        context.Response.Headers["X-Accel-Buffering"] = "no";

        if (logger is not null && logger.IsEnabled(LogLevel.Debug))
            logger.Debug($"Stream {typeof(TQuery).Name}: started");

        var ct = context.RequestAborted;
        var bodyWriter = context.Response.BodyWriter;
        var itemCount = 0;

        // 遥测（2026-10-18）：span 覆盖全枚举期；TTFT 由首个 first-item 事件时间戳给出。
        // 计数器零 tag（PalMetrics 纪律）；listener 未订阅时 Start 返回 null，Add 仍执行
        // （Counter.Add 无 listener 时内部短路，零分配）
        var activity = Diagnostics.PalActivitySource.StartStreamQuery(typeof(TQuery).Name, typeof(TItem).Name);
        Diagnostics.PalMetrics.StreamStarted.Add(1);
        var firstItemRecorded = false;

        // 帧写出单一出处（默认/富化/定制三分支，心跳与快路径共用）
        void WriteItemFrame(TItem item)
        {
            if (!firstItemRecorded)
            {
                // TTFT 标记：只在订阅方存在时记事件（AddEvent 会分配事件对象与链表节点）
                firstItemRecorded = true;
                activity?.AddEvent(new System.Diagnostics.ActivityEvent("first-item"));
            }
            if (formatFrame is not null)
            {
                // D4 定制帧：委托返回完整帧文本（event/id 行），支持分型事件流
                StreamFrameWriter.WriteRawFrame(bodyWriter, formatFrame(item));
                return;
            }
            if (eventSelector is null && idSelector is null)
            {
                // D4 默认帧：data-only JSON——P1 池化序列化（稳态每项零堆分配，
                // StreamFrameSerializer）+ framing span 直写（无字符串中转）
                var json = StreamFrameSerializer.Serialize(item, itemJsonTypeInfo);
                StreamFrameWriter.WriteDataFrame(bodyWriter, json);
                return;
            }
            // Phase 2 富化帧：event/id 行由选择器产出（字符串 UTF-8 直写 span，零分配）
            var richJson = StreamFrameSerializer.Serialize(item, itemJsonTypeInfo);
            StreamFrameWriter.WriteDataFrame(
                bodyWriter, richJson,
                eventSelector?.Invoke(item), idSelector?.Invoke(item));
        }

        try
        {
            if (keepAliveInterval is { } heartbeatInterval)
            {
                // Phase 2 心跳路径：手动枚举器 + MoveNext 与心跳延时竞速。
                // D5 取消贯穿：ct 为 RequestAborted，GetAsyncEnumerator(ct) 与
                // WithCancellation 同机制（[EnumeratorCancellation] 语义——忽略形参令牌的
                // 实现也会在 MoveNext 处观测取消）；不用 WithCancellation 包装是为了拿到
                // 普通 ValueTask<bool>（已配置包装器的 MoveNextAsync 返回不可竞速的
                // ConfiguredValueTaskAwaitable，AsTask 不可用）
                var enumerator = handler.HandleAsync(query, ct).GetAsyncEnumerator(ct);
                try
                {
                    Task<bool>? pendingMoveNext = null;
                    // 共享延时：每流一个在途 Task.Delay，只在心跳真正写出时重建——
                    // 项胜出竞速时旧延时留用（间隔自上次心跳起算，保活语义等价），
                    // 消除每挂起项一个被丢弃延时的计时器堆积；随 ct 取消整体释放
                    var delay = Task.Delay(heartbeatInterval, ct);
                    while (true)
                    {
                        Task<bool> moveNext;
                        if (pendingMoveNext is not null)
                        {
                            moveNext = pendingMoveNext;
                            pendingMoveNext = null;
                        }
                        else
                        {
                            var valueTask = enumerator.MoveNextAsync();
                            if (valueTask.IsCompletedSuccessfully)
                            {
                                // 同步完成快路径：无 Task 竞速开销（对齐 Phase 1 await foreach 快路径）
                                if (!valueTask.Result) break;
                                itemCount++;
                                WriteItemFrame(enumerator.Current);
                                // 写合并：首帧立即提交（TTFT 与断连可见性），其后连续同步产出的帧
                                // 累计到阈值才 flush；下一项若挂起，会在进入竞速前先 flush。
                                if (itemCount == 1 || bodyWriter.UnflushedBytes >= StreamFrameWriter.FlushThresholdBytes)
                                    await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                                continue;
                            }
                            // 即将挂起等待下一项：先把已写帧刷出，保证心跳路径的逐项延迟语义。
                            await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                            moveNext = valueTask.AsTask();
                        }

                        if (await Task.WhenAny(moveNext, delay).ConfigureAwait(false) == delay)
                        {
                            if (delay.IsCanceled)
                            {
                                // 取消优先于心跳：等挂起项落地——正常路径下它随 ct 取消抛
                                // OCE（迭代器 finally 由状态机执行），统一走下方 catch 的
                                // 静默收尾；await 而非丢弃挂起任务，避免未观测任务异常
                                await moveNext.ConfigureAwait(false);
                                continue;
                            }
                            // 心跳到点：写注释帧（规范内客户端忽略），挂起项留待下轮竞速；
                            // 共享延时重建（间隔自上次心跳起算——保活语义等价且每流一个
                            // 在途延时，消除「每挂起项一个被丢弃的 Task.Delay」的计时器堆积：
                            // 项胜出时旧延时留用，只在真正写出心跳时换代）
                            pendingMoveNext = moveNext;
                            StreamFrameWriter.WriteHeartbeat(bodyWriter);
                            await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                            delay = Task.Delay(heartbeatInterval, ct);
                            continue;
                        }

                        if (await moveNext.ConfigureAwait(false))
                        {
                            itemCount++;
                            WriteItemFrame(enumerator.Current);
                            if (itemCount == 1 || bodyWriter.UnflushedBytes >= StreamFrameWriter.FlushThresholdBytes)
                                await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            break;
                        }
                    }

                    // 收尾：最后一批未达阈值的帧在此刷出（空流也提交一次，保持原每流至少一次 flush）。
                    await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                // Phase 1 默认路径：手动枚举 + 写合并。连续同步产出的帧累计到阈值才 flush；
                // MoveNext 挂起前与流结束时各 flush 一次，低速率与首帧延迟保持逐项语义。
                var enumerator = handler.HandleAsync(query, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        var moveNext = enumerator.MoveNextAsync();
                        if (moveNext.IsCompletedSuccessfully)
                        {
                            if (!moveNext.Result)
                                break;
                        }
                        else
                        {
                            // 即将等待下一项：先把已写帧刷出，客户端立刻可见。
                            await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                            if (!await moveNext.ConfigureAwait(false))
                                break;
                        }

                        itemCount++;
                        WriteItemFrame(enumerator.Current);
                        if (itemCount == 1 || bodyWriter.UnflushedBytes >= StreamFrameWriter.FlushThresholdBytes)
                            await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                    }

                    await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }

            Diagnostics.PalMetrics.StreamCompleted.Add(1);
            Diagnostics.PalMetrics.StreamItems.Add(itemCount);
            if (logger is not null && logger.IsEnabled(LogLevel.Debug))
                logger.Debug($"Stream {typeof(TQuery).Name}: completed {itemCount} item(s) in {timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds:F2}ms");
        }
        catch (OperationCanceledException)
        {
            // D5：客户端断开静默收尾（头已写出，无法回退状态码；消费方按流截断处理）。
            // 取消不是错误——按 LoggingBehavior 的 OCE 过滤同款语义不记 Error。
            // 遥测：取消独立计数（混入 failed 会污染错误率告警）；已产出项仍计入吞吐
            Diagnostics.PalMetrics.StreamCancelled.Add(1);
            Diagnostics.PalMetrics.StreamItems.Add(itemCount);
            if (logger is not null && logger.IsEnabled(LogLevel.Debug))
                logger.Debug($"Stream {typeof(TQuery).Name}: cancelled after {itemCount} item(s)");
        }
        catch (Exception ex)
        {
            // 中途异常：补 event: error 帧后结束（头已写出无法回退 500；异常详情只进日志，
            // 帧内文案通用化——对齐 ExceptionMiddleware 的 500 响应不泄露内部细节口径）
            Diagnostics.PalMetrics.StreamFailed.Add(1);
            Diagnostics.PalMetrics.StreamItems.Add(itemCount);
            // span 状态置错：异常类型名进状态描述（类型名基数有界，不含消息/业务数据）
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.GetType().Name);
            try
            {
                StreamFrameWriter.WriteErrorFrame(bodyWriter);
                await bodyWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 客户端已断开时 error 帧无法送达——消费方本就按截断处理，静默
            }
            if (logger is not null && logger.IsEnabled(LogLevel.Error))
                logger.Error(ex, $"Stream {typeof(TQuery).Name}: failed after {itemCount} item(s) in {timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds:F2}ms");
        }
        finally
        {
            activity?.Dispose();
        }
    }

    /// <summary>读取 JSON 请求体，错误时写入对应错误响应并返回 (false, null)。
    /// ITM-799（2026-09-19）：415/畸形 JSON/验证异常/空 body 四种错误编排原在
    /// MapCommand/MapQuery 两处逐字重复（约 40 行）——收口为单一出处，历史注释随迁。
    /// ADR-030 D8：泛化版（类型参数 TCommand → T）——MapStreamQuery POST 形态复用，
    /// 415/畸形 JSON/空 body 错误形态不变。
    /// ADR-031 补强（2026-10-19）：MapDetachedStreamQuery POST 形态复用同一出处，
    /// internal 仅供同程序集端点映射类调用（非公共 API）。</summary>
    internal static async Task<(bool Success, T? Body)> ReadJsonBodyOrWriteErrorAsync<T>(
        HttpContext context, JsonTypeInfo<T> bodyJsonTypeInfo)
    {
        // v40 P3：非 JSON Content-Type 的 POST 原由 ReadFromJsonAsync 抛
        // InvalidOperationException（非 JsonException）逃逸为 500——语义应为
        // 415 Unsupported Media Type（RFC 9110 §15.5.16，对齐 ASP.NET Core MVC
        // [ApiController] 的 Content-Type 约束行为），带 ProblemDetails 形态体
        if (!context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await context.Response.WriteAsJsonAsync(
                ValidationProblemResponseFactory.CreateUnsupportedContentType(),
                PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return (false, default);
        }
        T? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync(
                bodyJsonTypeInfo,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            // P3 修复：畸形 JSON 是用户输入错误 → 400 而非未捕获 500
            // v8 对齐：补 ProblemDetails body（原裸 400 与验证 400 形态分叉，客户端拿不到错误明细）
            // v31 P3：泛化文案——ex.Message 含 .NET 内部类型名/JSON 路径与字节位置，
            // 向任意 HTTP 客户端暴露实现细节（对齐 P3-SRC-107 HandlerNotFound 收窄口径）
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                ValidationProblemResponseFactory.CreateInvalidBody("Request body is not valid JSON."),
                PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return (false, default);
        }
        catch (CQRS.PalValidationException ex)
        {
            // P2 修复：验证失败异常映射 400（框架意图与实现一致化）
            // ITM-283（R45）：统一走 Factory 带 ProblemDetails body——原裸 400 与
            // 派发段（ITM-168）形态分叉（自定义 JsonConverter 抛此异常时客户端拿不到错误明细）
            await WriteValidationProblemAsync(context, ex, context.RequestAborted).ConfigureAwait(false);
            return (false, default);
        }
        if (body is null)
        {
            // v9 E1：空 body 裸 400 是第三处形态分叉——统一 ProblemDetails（对齐 ITM-283/v8 畸形 JSON）
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                ValidationProblemResponseFactory.CreateInvalidBody("Request body is required."),
                PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
                contentType: null, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return (false, default);
        }
        return (true, body);
    }

    /// <summary>写入 400 + ValidationProblemResponse 体（ITM-283：反序列化段与派发段统一形态——
    /// 消除裸 400 无 body 与派发段 ITM-168 形态的分叉）。v33 P3：派发段三处逐字重复的
    /// catch 收口到本方法（可选 ct 透传——派发段原样携带 RequestAborted，写响应可随
    /// 客户端断连取消；反序列化/绑定段不传 ct，与原 default(CancellationToken) 等价）。</summary>
    private static async Task WriteValidationProblemAsync(
        HttpContext context,
        CQRS.PalValidationException ex,
        CancellationToken ct = default)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        var response = ValidationProblemResponseFactory.Create(ex);
        await context.Response.WriteAsJsonAsync(
            response,
            PalAspNetCoreJsonContext.Default.ValidationProblemResponse,
            contentType: null,
            cancellationToken: ct).ConfigureAwait(false);
    }
}

internal static class ValidationProblemResponseFactory
{
    /// <summary>畸形 JSON 请求体构造 ValidationProblemResponse（v8：对齐验证 400 的 ProblemDetails 形态）。</summary>
    internal static ValidationProblemResponse CreateInvalidBody(string message)
        => new(
            "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1",
            "Invalid Request Body",
            StatusCodes.Status400BadRequest,
            [new ValidationProblemError("requestBody", message)]);

    /// <summary>非 JSON Content-Type 构造 ValidationProblemResponse（v40 P3：415 短路路径的
    /// ProblemDetails 形态体，链接 RFC 9110 §15.5.16）。</summary>
    internal static ValidationProblemResponse CreateUnsupportedContentType()
        => new(
            "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.16",
            "Unsupported Media Type",
            StatusCodes.Status415UnsupportedMediaType,
            [new ValidationProblemError("contentType", "Request Content-Type is not application/json.")]);

    /// <summary>按 PalValidationException 构造规范 ValidationProblemResponse（RFC 9110 §15.5.1）。</summary>
    internal static ValidationProblemResponse Create(CQRS.PalValidationException ex)
        => new(
            "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1",
            "Validation Failed",
            StatusCodes.Status400BadRequest,
            ex.Errors.Select(e => new ValidationProblemError(e.PropertyName, e.Message)).ToArray());
}

// ─────────────────────────────────────────────────────────────
// SSE 帧写出 helper（ADR-030 D4）与流式端点生命周期日志标记类别（D6）
// ─────────────────────────────────────────────────────────────

/// <summary>
/// SSE 帧写出（ADR-030 D4 + Phase 2 收窄实施）——默认帧为 data-only JSON
/// （<c>data: {json}\n\n</c>），framing 字节段经 <see cref="PipeWriter"/> span 直写
/// （GetSpan/Advance），无字符串中转；定制帧由调用方 formatFrame 委托产出完整帧文本
/// （可含 event/id 行）；event/id 选择器产出框架富化帧（<c>event:</c>/<c>id:</c> 行）；
/// 心跳为注释帧（<c>:keepalive</c>，SSE 规范内客户端忽略）。
/// </summary>
/// <remarks>
/// 分配形态（精确声明，防过声称）：framing 段零分配（ReadOnlySpan 拷贝 +
/// <see cref="Encoding.GetBytes(ReadOnlySpan{char}, Span{byte})"/> 直写 span，无中间 byte[]）；
/// JSON 载荷字节由 <see cref="StreamFrameSerializer"/> 池化序列化产出（稳态每项零堆分配，
/// P1 实施前为 SerializeToUtf8Bytes 的 byte[]/item，实测 56B/项）。零拷贝下推
/// （Utf8JsonWriter 直写 PipeWriter）属 ADR-030 分期 Phase 2「PipeWriter 化」项，
/// 经 bench 实测证伪（CPU 持平而分配 2.4×）后收窄，暂不实施。
/// <para>变异红证明（在案，Phase 1）：WriteDataFrame 替换为字符串形态（Serialize → 插值帧串 →
/// GetBytes）后，零分配断言按 ~3× 载荷尺寸判红，恢复 span 形态转绿。</para>
/// </remarks>
internal static class StreamFrameWriter
{
    /// <summary>
    /// 写合并阈值（字节）。连续同步产出的帧累计到此值才提交一次管道；MoveNext 挂起前
    /// 与流结束时仍各 flush 一次，因此低速率流的逐项可见性不受影响。取值覆盖常见
    /// 小帧（几十到几百字节）的十余项批量，同时避免单帧高峰值驻留。
    /// </summary>
    internal const int FlushThresholdBytes = 4096;

    private static ReadOnlySpan<byte> DataPrefix => "data: "u8;

    private static ReadOnlySpan<byte> FrameTerminator => "\n\n"u8;

    private static ReadOnlySpan<byte> EventPrefix => "event: "u8;

    private static ReadOnlySpan<byte> IdPrefix => "id: "u8;

    private static ReadOnlySpan<byte> ErrorFrame =>
        "event: error\ndata: {\"message\":\"The stream failed unexpectedly.\"}\n\n"u8;

    /// <summary>心跳注释帧（SSE 注释行——规范内客户端忽略；代理/LB 侧重置空闲计时器）。</summary>
    private static ReadOnlySpan<byte> HeartbeatFrame => ":keepalive\n\n"u8;

    /// <summary>
    /// 写默认帧：<c>data: {json}\n\n</c>；传 <paramref name="eventName"/> / <paramref name="id"/>
    /// 时产出富化帧 <c>event: {name}\nid: {id}\ndata: {json}\n\n</c>（行序固定，缺项行省略）。
    /// framing span 直写零分配（event/id 字符串经 UTF-8 直写 span，无中间 byte[]）。
    /// </summary>
    public static void WriteDataFrame(
        PipeWriter writer, ReadOnlySpan<byte> json, string? eventName = null, string? id = null)
    {
        var eventBytes = eventName is null ? 0 : Encoding.UTF8.GetByteCount(eventName);
        var idBytes = id is null ? 0 : Encoding.UTF8.GetByteCount(id);
        var total = DataPrefix.Length + json.Length + FrameTerminator.Length
            + (eventName is null ? 0 : EventPrefix.Length + eventBytes + 1)
            + (id is null ? 0 : IdPrefix.Length + idBytes + 1);
        var span = writer.GetSpan(total);
        var written = 0;
        if (eventName is not null)
        {
            EventPrefix.CopyTo(span[written..]);
            written += EventPrefix.Length;
            written += Encoding.UTF8.GetBytes(eventName.AsSpan(), span[written..]);
            span[written++] = (byte)'\n';
        }
        if (id is not null)
        {
            IdPrefix.CopyTo(span[written..]);
            written += IdPrefix.Length;
            written += Encoding.UTF8.GetBytes(id.AsSpan(), span[written..]);
            span[written++] = (byte)'\n';
        }
        DataPrefix.CopyTo(span[written..]);
        written += DataPrefix.Length;
        json.CopyTo(span[written..]);
        written += json.Length;
        FrameTerminator.CopyTo(span[written..]);
        writer.Advance(total);
    }

    /// <summary>写定制帧（formatFrame 委托返回的完整帧文本——契约：须自行携带帧终止空行）。</summary>
    public static void WriteRawFrame(PipeWriter writer, string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var byteCount = Encoding.UTF8.GetByteCount(frame);
        var span = writer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(frame.AsSpan(), span);
        writer.Advance(byteCount);
    }

    /// <summary>写 <c>event: error</c> 帧（中途异常补偿帧——头已写出无法回退 500，
    /// 以事件帧加流终止表达；文案通用化，异常详情只进 Error 日志）。</summary>
    public static void WriteErrorFrame(PipeWriter writer)
    {
        var span = writer.GetSpan(ErrorFrame.Length);
        ErrorFrame.CopyTo(span);
        writer.Advance(ErrorFrame.Length);
    }

    /// <summary>写心跳注释帧 <c>:keepalive\n\n</c>（span 直写零分配；不计入 item 数）。</summary>
    public static void WriteHeartbeat(PipeWriter writer)
    {
        var span = writer.GetSpan(HeartbeatFrame.Length);
        HeartbeatFrame.CopyTo(span);
        writer.Advance(HeartbeatFrame.Length);
    }
}

/// <summary>
/// 帧载荷序列化的 ThreadStatic 池（ADR-030 P1，2026-10-18 性能实施轮）——消除默认帧路径
/// 每项一笔 <see cref="JsonSerializer.SerializeToUtf8Bytes{T}(T, JsonTypeInfo{T})"/> 的
/// byte[]（实测 56B/项；探针与 BDN 对照见 docs/performance.md 流式读链路首测节）。
/// </summary>
/// <remarks>
/// 形态复用 <c>PalDDD.Serialization.JsonMessageSerializer</c> 的 R8/ADR-029 池化路径
/// （ThreadStatic writer + ArrayBufferWriter，Clear/Reset 后 Serialize + Flush），输出落
/// 池内缓冲、取 <see cref="ArrayBufferWriter{T}.WrittenSpan"/> 交 framing span 直写，
/// 稳态每项零堆分配（探针实测 0.0 B/项 vs 56.0 B/项，CPU 持平到更快）。
/// <para>⚠️ 重入限制（同 JsonMessageSerializer v27/v29 声明）：同线程嵌套调用（半帧序列化
/// 期间再次进入本类）会 Clear 掉外层已写内容。流式端点半帧序列化期间不存在嵌套路径
/// （自定义 TItem 的 JsonConverter 回调本类属极端形态），此处为防御性文档。</para>
/// <para>容量驻留：ArrayBufferWriter 按历史峰值保留，超 256KB 弃槽重建（R9 同族纪律，
/// 与 JsonMessageSerializer.MaxResidentBufferBytes 同阈值）。返回的 span 仅在同线程
/// 下一次 Serialize 前有效——调用方须同步拷贝（framing 直写即此形态）。</para>
/// </remarks>
internal static class StreamFrameSerializer
{
    private const int MaxResidentBufferBytes = 256 * 1024;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _tlsBufferWriter;

    [ThreadStatic]
    private static Utf8JsonWriter? _tlsWriter;

    /// <summary>序列化单项并返回 UTF-8 字节视图（池内缓冲，同线程下次调用前有效）。</summary>
    public static ReadOnlySpan<byte> Serialize<TItem>(TItem item, JsonTypeInfo<TItem> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var bufferWriter = _tlsBufferWriter;
        if (bufferWriter is null || bufferWriter.Capacity > MaxResidentBufferBytes)
        {
            // R9/P2-1 同族：超阈值弃槽重建（初始 512B，按需增长）
            bufferWriter = new ArrayBufferWriter<byte>(512);
            _tlsBufferWriter = bufferWriter;
        }
        else
        {
            bufferWriter.Clear();
        }

        var writer = _tlsWriter;
        if (writer is null)
        {
            writer = new Utf8JsonWriter(bufferWriter);
            _tlsWriter = writer;
        }
        else
        {
            writer.Reset(bufferWriter);
        }

        JsonSerializer.Serialize(writer, item, jsonTypeInfo);
        writer.Flush();

        return bufferWriter.WrittenSpan;
    }
}

/// <summary>
/// D6：流式端点生命周期日志的内部标记类别——作 <see cref="Core.Logging.IPalLogger{T}"/>
/// 门面的类别实参（类别名即本类型名，含查询/元素类型信息）；永不实例化（CA1812——
/// 泛型实参形态由 DI 开放泛型注册解析，分析器不可见，Hosting csproj 已在 NoWarn 清单）。
/// </summary>
#pragma warning disable S2094 // 类别标记类型（IPalLogger 泛型实参，永不实例化）
internal sealed class StreamQueryEndpoint<TQuery, TItem>;
#pragma warning restore S2094
