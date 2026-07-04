// ─────────────────────────────────────────────────────────────
// 📊 PalDiagnostics — OpenTelemetry Activity/Metrics 定义
// ─────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace PalDDD.Core.Diagnostics;

// PalDDD 可观测性基础设施 — ActivitySource + Meter
// 预分配常量 + 编译时 Log + 零分配 Tags
// 所有组件设计为 AOT 安全，无运行时反射

// --- ActivitySource ---

/// <summary>分布式追踪源 — 标准 OpenTelemetry 兼容</summary>
public static class PalActivitySource
{
    public const string Name = "PalDDD";

    /// <summary>
    /// 版本号 — 从程序集 AssemblyName.Version 读取，与 NuGet 包版本一致。<br/>
    /// 💡 AOT 安全：Assembly.GetName() 只读程序集清单元数据，不依赖反射。<br/>
    /// 🛡️ 容错：静态构造函数 try-catch 包裹，异常时回退到 "0.0.0"，
    ///    避免元数据异常导致 TypeInitializationException 使整个类型不可用。
    /// </summary>
    public static readonly string Version = ReadAssemblyVersion();

    public static readonly ActivitySource Source = new(Name, Version);

    [SuppressMessage("Design", "CA1031:Do not catch general exception",
        Justification = "启动期读取程序集版本的兜底路径，任何反射异常都应降级为默认版本号而非阻断启动。")]
    private static string ReadAssemblyVersion()
    {
        try
        {
            return typeof(PalActivitySource).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>创建命令分派 Activity</summary>
    public static Activity? StartCommandDispatch(string commandName, string handlerName)
        => Start("Command Dispatch",
            ("pal.command", commandName),
            ("pal.handler", handlerName));

    /// <summary>创建事件派发 Activity</summary>
    public static Activity? StartEventDispatch(string eventName)
        => Start("Event Dispatch",
            ("pal.event", eventName));

    /// <summary>创建 Saga 转换 Activity</summary>
    public static Activity? StartSagaTransition(string sagaType, string fromState, string toState)
        => Start("Saga Transition",
            ("pal.saga", sagaType),
            ("pal.saga.from", fromState),
            ("pal.saga.to", toState));

    /// <summary>创建发件箱处理 Activity</summary>
    public static Activity? StartOutboxProcess(int batchSize)
        => Start("Outbox Process",
            ("pal.outbox.batch_size", batchSize));

    /// <summary>创建收件箱幂等消费 Activity</summary>
    public static Activity? StartInboxProcess(string consumerName, string messageId)
        => Start("Inbox Process",
            ("pal.inbox.consumer", consumerName),
            ("pal.inbox.message_id", messageId));

    /// <summary>创建幂等执行 Activity</summary>
    public static Activity? StartIdempotencyExecute(string operationName, string key)
        => Start("Idempotency Execute",
            ("pal.idempotency.operation", operationName),
            ("pal.idempotency.key", key));

    /// <summary>创建投影重建 Activity</summary>
    public static Activity? StartProjectionRebuild(string projectionName, string sourceName)
        => Start("Projection Rebuild",
            ("pal.projection.name", projectionName),
            ("pal.projection.source", sourceName));

    /// <summary>创建事件回放读取 Activity</summary>
    public static Activity? StartEventReplayRead(string sourceName, string eventName, string messageType)
        => Start("Event Replay Read",
            ("pal.replay.source", sourceName),
            ("pal.replay.event", eventName),
            ("pal.replay.message_type", messageType));

    /// <summary>创建事件日志追加 Activity</summary>
    public static Activity? StartEventLogAppend(string streamName, int eventCount)
        => Start("EventLog Append",
            ("pal.eventlog.stream", streamName),
            ("pal.eventlog.event_count", eventCount));

    /// <summary>创建事件日志单流读取 Activity</summary>
    public static Activity? StartEventLogReadStream(string streamName, long fromVersion)
        => Start("EventLog ReadStream",
            ("pal.eventlog.stream", streamName),
            ("pal.eventlog.from_stream_version", fromVersion));

    /// <summary>创建事件日志全局读取 Activity</summary>
    public static Activity? StartEventLogReadAll(long fromPosition)
        => Start("EventLog ReadAll",
            ("pal.eventlog.from_global_position", fromPosition));

    // ── 内部辅助 ──
    // 11 个公共 Start* 方法遵循完全相同的模式：Source.StartActivity → SetTag → return。
    // 使用 params ReadOnlySpan<(string,object?)>（C# 13+ / .NET 11）压缩重复代码，
    // 编译器对少量参数生成栈分配 span，消除原 params 数组的堆分配——零 GC 压力。
    // 命名元组 Key/Value 而非 key/value（避免与 System.Collections.Generic.KeyValuePair 混淆）。

    private static Activity? Start(string name, params ReadOnlySpan<(string Key, object? Value)> tags)
    {
        var activity = Source.StartActivity(name);
        if (activity is null) return null;
        foreach (var tag in tags)
            activity.SetTag(tag.Key, tag.Value);
        return activity;
    }
}

// --- Metrics ---

/// <summary>PalDDD 指标 — 标准 OpenTelemetry 兼容</summary>
public static class PalMetrics
{
    private static readonly Meter Meter = new(PalActivitySource.Name, PalActivitySource.Version);

    /// <summary>命令执行总数（按命令名和结果分类）</summary>
    public static readonly Counter<long> CommandTotal = Meter.CreateCounter<long>(
        "paldd.commands.total", description: "命令执行总数");

    /// <summary>命令执行耗时（毫秒）</summary>
    public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "paldd.commands.duration_ms", "ms", "命令执行耗时");

    /// <summary>领域事件发布总数</summary>
    public static readonly Counter<long> EventsPublished = Meter.CreateCounter<long>(
        "paldd.events.published", description: "领域事件发布总数");

    /// <summary>领域事件消费总数</summary>
    public static readonly Counter<long> EventsConsumed = Meter.CreateCounter<long>(
        "paldd.events.consumed", description: "领域事件消费总数");

    /// <summary>领域事件处理器成功调用总数</summary>
    public static readonly Counter<long> EventHandlersHandled = Meter.CreateCounter<long>(
        "paldd.event_handlers.handled", description: "领域事件处理器成功调用总数");

    /// <summary>领域事件处理器失败调用总数</summary>
    public static readonly Counter<long> EventHandlersFailed = Meter.CreateCounter<long>(
        "paldd.event_handlers.failed", description: "领域事件处理器失败调用总数");

    /// <summary>事件日志追加事件数</summary>
    public static readonly Counter<long> EventLogAppended = Meter.CreateCounter<long>(
        "paldd.eventlog.appended", description: "事件日志追加事件数");

    /// <summary>事件日志读取事件数</summary>
    public static readonly Counter<long> EventLogRead = Meter.CreateCounter<long>(
        "paldd.eventlog.read", description: "事件日志读取事件数");

    /// <summary>发件箱待处理消息数</summary>
    public static readonly UpDownCounter<long> OutboxPending = Meter.CreateUpDownCounter<long>(
        "paldd.outbox.pending", description: "发件箱待处理消息数");

    /// <summary>发件箱处理成功数</summary>
    public static readonly Counter<long> OutboxProcessed = Meter.CreateCounter<long>(
        "paldd.outbox.processed", description: "发件箱成功处理数");

    /// <summary>发件箱处理失败数</summary>
    public static readonly Counter<long> OutboxFailed = Meter.CreateCounter<long>(
        "paldd.outbox.failed", description: "发件箱处理失败数");

    /// <summary>收件箱处理成功数</summary>
    public static readonly Counter<long> InboxProcessed = Meter.CreateCounter<long>(
        "paldd.inbox.processed", description: "收件箱成功处理数");

    /// <summary>收件箱跳过重复消息数</summary>
    public static readonly Counter<long> InboxSkipped = Meter.CreateCounter<long>(
        "paldd.inbox.skipped", description: "收件箱跳过重复消息数");

    /// <summary>收件箱处理失败数</summary>
    public static readonly Counter<long> InboxFailed = Meter.CreateCounter<long>(
        "paldd.inbox.failed", description: "收件箱处理失败数");

    /// <summary>幂等执行实际执行数</summary>
    public static readonly Counter<long> IdempotencyExecuted = Meter.CreateCounter<long>(
        "paldd.idempotency.executed", description: "幂等请求实际执行数");

    /// <summary>幂等执行缓存命中数</summary>
    public static readonly Counter<long> IdempotencyCached = Meter.CreateCounter<long>(
        "paldd.idempotency.cached", description: "幂等请求缓存命中数");

    /// <summary>幂等执行跳过数</summary>
    public static readonly Counter<long> IdempotencySkipped = Meter.CreateCounter<long>(
        "paldd.idempotency.skipped", description: "幂等请求跳过数");

    /// <summary>幂等执行失败数</summary>
    public static readonly Counter<long> IdempotencyFailed = Meter.CreateCounter<long>(
        "paldd.idempotency.failed", description: "幂等请求失败数");

    /// <summary>投影重建回放事件数</summary>
    public static readonly Counter<long> ProjectionReplayed = Meter.CreateCounter<long>(
        "paldd.projection.replayed", description: "投影重建回放事件数");

    /// <summary>投影重建失败数</summary>
    public static readonly Counter<long> ProjectionFailed = Meter.CreateCounter<long>(
        "paldd.projection.failed", description: "投影重建失败数");

    /// <summary>事件回放读取事件数</summary>
    public static readonly Counter<long> ReplayRead = Meter.CreateCounter<long>(
        "paldd.replay.read", description: "事件回放读取事件数");

    /// <summary>事件回放失败数</summary>
    public static readonly Counter<long> ReplayFailed = Meter.CreateCounter<long>(
        "paldd.replay.failed", description: "事件回放失败数");

    /// <summary>活跃 Saga 数量</summary>
    public static readonly UpDownCounter<long> SagaActive = Meter.CreateUpDownCounter<long>(
        "paldd.saga.active", description: "活跃 Saga 数量");

    /// <summary>Saga 完成数</summary>
    public static readonly Counter<long> SagaCompleted = Meter.CreateCounter<long>(
        "paldd.saga.completed", description: "Saga 完成总数");

    /// <summary>Saga 补偿数</summary>
    public static readonly Counter<long> SagaCompensated = Meter.CreateCounter<long>(
        "paldd.saga.compensated", description: "Saga 补偿总数");

    /// <summary>Saga 补偿失败数</summary>
    public static readonly Counter<long> SagaCompensationFailed = Meter.CreateCounter<long>(
        "paldd.saga.compensation_failed", description: "Saga 补偿失败总数");

    /// <summary>管道行为执行耗时（按行为名分类）</summary>
    public static readonly Histogram<double> BehaviorDuration = Meter.CreateHistogram<double>(
        "paldd.pipeline.behavior_duration_ms", "ms", "管道行为执行耗时");
}
