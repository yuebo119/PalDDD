// ─────────────────────────────────────────────────────────────
// ✅🔍 内置管道行为 — ValidationBehavior + LoggingBehavior
// ─────────────────────────────────────────────────────────────
// Validation：调用 IPalValidator<T> 验证请求，失败抛 PalValidationException
// Logging：记录 Handler 名称和耗时（IPalLogger 门面 + ZLogger 实现）
//
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 内置管道行为 — 验证 + 日志
// 💡 保留理由：DDD 验证拦截 + IPalLogger 日志门面。
//    详见 docs/decisions/004-core-type-retention.md
// ─────────────────────────────────────────────────────────────
//
// 性能设计：
//    - ValidationBehavior 的 errors 列表延迟分配（errors ??= []），
//      无验证失败时零堆分配。这是比 FluentValidation 更轻量的设计。
//    - LoggingBehavior 使用 IPalLogger<T> 门面，底层由 ZLogger 提供
//      零分配 UTF8 结构化日志。仅在 _logger.IsEnabled(LogLevel.Debug) 时才记录。
//    - LoggingBehavior 的有意义日志采用字符串插值而非 LoggerMessage 源生成：
//      日志经 IPalLogger<T> 门面发出，门面刻意隐藏底层 ILogger（见 IPalLogger.cs 设计原则），
//      与 LoggerMessage.Define 所需的 ILogger 入参不兼容；且插值仅在 IsEnabled(Debug) 门控后执行，
//      生产路径零分配。改为 LoggerMessage 会泄漏门面抽象、增加复杂度，故保持现状（YAGNI）。
//
// 可扩展性：用户可以通过实现 IPipelineBehavior<TRequest,TResponse>
//    添加自定义行为（如事务、缓存、限流），无需修改框架代码。
//    这两个内置行为是"开箱即用"的合理默认，不是强制的。

/// <summary>验证管道行为 — 调用所有 IPalValidator&lt;TRequest&gt; 进行验证</summary>
/// <remarks>
/// 使用 <see cref="Core.IPalValidator{T}"/> 抽象，不依赖任何特定验证库。<br/>
/// errors 列表延迟分配——无验证失败时零堆分配。<br/>
/// 注册方式：<c>services.AddScoped(typeof(IPipelineBehavior&lt;,&gt;), typeof(ValidationBehavior&lt;,&gt;))</c>
/// </remarks>
internal sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    // EPSL 地毯审计 2026-10-16 B 批 #9：数组型字段——构造期一次物化（MS DI 多服务解析
    // 常态返回 T[]，引用恒等转型零分配零拷贝；非数组形态回落单次 ToArray）。原接口型
    // 字段 foreach 每请求经接口取枚举器（非空 validator 集实测 ~32B/请求；零 validator
    // 常见场景 JIT 对空数组形态已消除故本就免费——探针实证）。与 Dispatcher.cs
    // MaterializeBehaviors（R1/A-F1）同款先例。
    private readonly Core.IPalValidator<TRequest>[] _validators;

    public ValidationBehavior(IEnumerable<Core.IPalValidator<TRequest>> validators)
    {
        ArgumentNullException.ThrowIfNull(validators); // v26 P3（ITM-284 对齐）：全仓构造守卫
        _validators = validators as Core.IPalValidator<TRequest>[] ?? validators.ToArray();
    }

    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next)
    {
        var errors = PalValidatorRunner.Validate(_validators, request);
        if (errors is { Count: > 0 })
            throw new PalValidationException([.. errors]);

        return await next().ConfigureAwait(false);
    }

    // R2（A-F2，2026-10-14 性能实施轮）：显式非泛型实现——内置行为经管道热路径时免 DIM 桥
    // 的闭包+Func 两笔分配（PipelineBehavior.cs v25 勘正自认形态）。验证同步完成、next
    // 直转发零额外分配；结果 object? 透传不转型（底部桥的箱对象原样上浮，Unit 链路免
    // 拆箱-再装箱往返）。与泛型路径共用 Validate 核心，改动须双侧同步。
    ValueTask<object?> IPipelineBehavior.HandleAsync(IBaseRequest request, CancellationToken ct, Func<ValueTask<object?>> next)
    {
        var errors = PalValidatorRunner.Validate(_validators, (TRequest)request);
        if (errors is { Count: > 0 })
            throw new PalValidationException([.. errors]);
        return next();
    }
}

/// <summary>
/// 验证执行核心（ADR-030 D3 抽取）——<see cref="ValidationBehavior{TRequest, TResponse}"/>
/// 泛型/非泛型两条路径与流式端点（MapStreamQuery）验证前置三处共用的单一出处。
/// </summary>
/// <remarks>
/// 抽取动因：流式端点在 SSE 头写出前必须跑同一套验证（失败 400 且可回退状态码），
/// 若在端点侧复写验证逻辑，「IsValid=false 且 Errors 为空时构造占位条目」语义
/// （v25/v48 两轮勘正定案）会出现第二份定义并漂移。本类为 internal static——
/// 经仓内 IVT 链（PalDDD.CQRS → PalDDD.Hosting.AspNetCore）对端点原语可见，
/// 不进公共 API 快照。
/// </remarks>
internal static class PalValidatorRunner
{
    /// <summary>
    /// 对 request 跑全部 validators；返回错误列表（无验证失败返回 null——调用方按
    /// <c>errors is { Count: &gt; 0 }</c> 判定，无失败时零堆分配）。
    /// </summary>
    public static List<Core.PalValidationError>? Validate<TRequest>(
        Core.IPalValidator<TRequest>[] validators, TRequest request)
    {
        List<Core.PalValidationError>? errors = null;
        foreach (var validator in validators)
        {
            var result = validator.Validate(request);
            if (!result.IsValid)
            {
                // 三十八轮 P2 修复（v48 P3 勘正）：原表述"用户验证器 return default 时 Errors 为
                // default(ImmutableArray)，AddRange 直接 NRE"已失实——v47 PalValidationResult.Errors
                // getter 归一（IPalValidator.cs：_errors 为 default 时返回 Empty）后 IsDefault
                // 分支不可达，NRE 触发条件不复存在。IsDefaultOrEmpty 防御存续的真实理由仅剩
                // IsEmpty 分支：归一后的空 Errors 若直接 AddRange 会静默吞掉 IsValid=false 的
                // 失败事实（next 照常执行，验证拦截被绕过），构造占位错误条目保留失败并给出来源诊断
                // v25 P2-2 语义修正：IsValid=false 是验证器明确表达的失败判定——default/空 Errors
                // 时 continue 会吞掉失败事实静默放行（default 验证器 = IsValid false + Errors
                // default，验证拦截被绕过，next 直接执行）；构造占位错误条目 fail-fast
                errors ??= [];
                if (result.Errors.IsDefaultOrEmpty)
                    errors.Add(new(string.Empty,
                        $"Validator {validator.GetType().Name} reported an invalid result without error details."));
                else
                    errors.AddRange(result.Errors);
            }
        }
        return errors;
    }
}

/// <summary>日志管道行为 — IPalLogger 门面日志记录</summary>
/// <remarks>
/// 记录命令/查询的执行时间和结果。<br/>
/// 注册方式：<c>services.AddScoped(typeof(IPipelineBehavior&lt;,&gt;), typeof(LoggingBehavior&lt;,&gt;))</c>（P3 修复：与 ServiceRegistration 实际注册对齐）
/// </remarks>
[SuppressMessage("Design", "CA1031", Justification = "需记录任意 handler 失败后重新抛出，cancel 不记录。")]
internal sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IPalLogger<LoggingBehavior<TRequest, TResponse>> _logger;
    private readonly TimeProvider _timeProvider;

    // P3 修复（时钟双轨清零）：可选注入，默认 System——测试可传 FakeTimeProvider
    public LoggingBehavior(IPalLogger<LoggingBehavior<TRequest, TResponse>> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger); // v26 P3（ITM-284 对齐）：全仓构造守卫（timeProvider 可选 null 不守卫）
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next)
    {
        // ITM-199 修复（三十轮）：统一 "Request" 中性前缀——QueryAsync 也走本行为，
        // 原固定 "Command" 前缀在日志中误导排障；按 IQuery 反射区分需 DAM 注解（IL2090，
        // AOT 反对），改用中性措辞零反射安全。

        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.Debug($"Request {typeof(TRequest).Name}: dispatching");

        var start = _timeProvider.GetTimestamp();
        try
        {
            var result = await next().ConfigureAwait(false);
            var elapsed = _timeProvider.GetElapsedTime(start).TotalMilliseconds;

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.Debug($"Request {typeof(TRequest).Name}: completed in {elapsed:F2}ms");

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // R1（A-F7）：失败分支补 IsEnabled 门——原 Error 插值
            //（double 格式化 + 拼接）无条件分配，Error 级关闭/NullLogger 时纯浪费（对齐
            // 上方两处 Debug 门的既有形态，语义等价：门内关闭时日志本就被丢弃）
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error))
            {
                var elapsed = _timeProvider.GetElapsedTime(start).TotalMilliseconds;
                _logger.Error(ex, $"Request {typeof(TRequest).Name}: failed after {elapsed:F2}ms");
            }
            throw;
        }
    }

    // R2（A-F2，2026-10-14 性能实施轮）：显式非泛型实现——免 DIM 桥闭包+Func 两笔分配。
    // 耗时日志的 try/catch 需跨 await，落实例异步方法（logger/timeProvider 为 this 字段
    // 直读、TRequest 为泛型常量——零闭包捕获；同步完成路径 async ValueTask 无挂起零分配）。
    // 与泛型路径行为逐项对齐（dispatching 日志/计时/成功与失败日志门），改动须双侧同步。
    ValueTask<object?> IPipelineBehavior.HandleAsync(IBaseRequest request, CancellationToken ct, Func<ValueTask<object?>> next)
    {
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.Debug($"Request {typeof(TRequest).Name}: dispatching");
        return LogAndDispatchAsync(next);
    }

    private async ValueTask<object?> LogAndDispatchAsync(Func<ValueTask<object?>> next)
    {
        var start = _timeProvider.GetTimestamp();
        try
        {
            var result = await next().ConfigureAwait(false);
            var elapsed = _timeProvider.GetElapsedTime(start).TotalMilliseconds;

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                _logger.Debug($"Request {typeof(TRequest).Name}: completed in {elapsed:F2}ms");

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error))
            {
                var elapsed = _timeProvider.GetElapsedTime(start).TotalMilliseconds;
                _logger.Error(ex, $"Request {typeof(TRequest).Name}: failed after {elapsed:F2}ms");
            }
            throw;
        }
    }
}
