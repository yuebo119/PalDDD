// ─────────────────────────────────────────────────────────────
// 📽️ ProjectionProcessor — Checkpoint 幂等投影处理
// ─────────────────────────────────────────────────────────────
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影处理器 — 逐事件处理并记录检查点
// ─────────────────────────────────────────────────────────────

[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "投影处理器需在重新抛出前持久化任意用户投影失败信息，需捕获 Exception 基类。")]
public sealed class ProjectionProcessor<TMessage>
{
    // ITM-167 修复：失败原因入库截断（Core.FailureReason.Normalize，2000 上限——与
    // InboxProcessor/OutboxBatchProcessor 同口径）——checkpoint.error
    // 列上限 2048，超长 ex.Message 会让 MarkFailedAsync 的持久化本身失败，掩盖原始投影失败。

    private readonly IProjectionHandler<TMessage> _handler;

    /// <summary>投影显示名称 — handler 契约成员的透传（Rebuilder 名称一致性守卫的比对源）。</summary>
    public string ProjectionName => _handler.ProjectionName;
    private readonly IProjectionCheckpointStore _checkpointStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _processingTimeout;
    // v25 P3 行为族 B7：pending-confirmation 信号补日志通道（原仅 Activity 事件，无 Trace
    // listener 时静默）；可选参数默认 null 向后兼容，DI 注册处无需改动
    private readonly IPalLogger<ProjectionProcessor<TMessage>>? _logger;

    public ProjectionProcessor(
        IProjectionHandler<TMessage> handler,
        IProjectionCheckpointStore checkpointStore,
        TimeProvider? timeProvider = null,
        TimeSpan processingTimeout = default,
        IPalLogger<ProjectionProcessor<TMessage>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        // ITM-166 修复：checkpoint 租约时长（processingTimeout）负值集中校验——负值使
        // LeaseUntil = startedAt + timeout < startedAt（租约即刻过期，僵尸抢占语义失效）。
        // Projection 无独立 Options 类，构造函数是进程内配置入口（Options 层等价物）
        // （对齐 DapperProjectionCheckpointStore.TryStartAsync 同款非负约束）。
        // v27 P3 勘正：原注释"允许 TimeSpan.Zero：租约即刻过期"与实现矛盾——
        // default(TimeSpan) == Zero，下方 `processingTimeout == default ? 5min : ...`
        // 把显式 Zero 替换为 5 分钟默认，"即刻过期"语义当前不可达；如需支持须改用
        // nullable 参数（TimeSpan?，破坏性变更留 v3.0）。仅勘正注释，不改行为。
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

        _handler = handler;
        _checkpointStore = checkpointStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _processingTimeout = processingTimeout == default ? TimeSpan.FromMinutes(5) : processingTimeout;
        _logger = logger;
    }

    public async ValueTask<bool> ProcessAsync(
        TMessage message,
        ProjectionContext context,
        CancellationToken ct = default)
    {
        // v33 P3 补落 v31 承诺的入口 null 校验（仅 message 半边成立）：引用类型消息
        // 实例化可为 null，原实现直通 ProjectAsync，失败点远离入口。不校验 context——
        // ProjectionContext 是 readonly record struct（值类型不可能为 null，除非调用方
        // 传 Nullable<T>，而那在非 nullable 形参处过不了 NRT 警告，本项目 warnaserror
        // 编译不过），ThrowIfNull(context) 是死代码且触发 CA2264（v31 的"入口 null
        // 校验"承诺里 context 半边本就无法成立）。
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        var checkpoint = await _checkpointStore.TryStartAsync(
            _handler.ProjectionName,
            context.SourceName,
            context.Position,
            _timeProvider.GetUtcNow(),
            _processingTimeout,
            ct).ConfigureAwait(false);

        if (checkpoint is null)
            return false;

        try
        {
            await _handler.ProjectAsync(message, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ITM-092 修复：MarkFailedAsync 本身失败不得掩盖主异常——内层捕获挂 Data 后仍以主异常优先。
            try
            {
                // ITM-167 修复：ex.Message 截断再入库（FailureReason.Normalize，2000 上限）
                var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
                await _checkpointStore.MarkFailedAsync(checkpoint, failureReason, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                ex.Data["MarkFailedError"] = markEx.Message;
            }
            throw;
        }

        // ITM-211 修复：handler 已成功——MarkCompletedAsync 失败不得进入 MarkFailedAsync
        // （那会把"已成功投影"降级为"可重试失败"→同一位置重放副作用）。
        // 镜像 ITM-191（IdempotencyProcessor）/ ITM-180（InboxProcessor）的管线孪生修复。
        try
        {
            await _checkpointStore.MarkCompletedAsync(checkpoint, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markEx)
        {
            // 副作用已发生，按 at-least-once 语义返回成功；区分性事件供运维介入。
            // v25 P3 行为族 B7：补日志通道——原仅 Activity.Current?.AddEvent，无 Trace
            // listener 时该信号完全静默；可选 logger 补 Warning（默认 null 不启用）。
            // ITM-653（v66 镜像 InboxProcessor）：移除原 `when (markEx is not
            // OperationCanceledException)` 过滤，对齐 ITM-092 口径——MarkCompletedAsync 以
            // CancellationToken.None 调用，其抛 OCE 属存储异常形态（而非请求级取消传播），
            // 原过滤让 OCE 逃逸给调用方，而投影 handler 实际已成功——调用方按取消处理
            // 触发重试重放路径；捕获后统一按 completed-pending-confirmation 处理（与上方
            // MarkFailedAsync 内层 catch 的不过滤口径对称）。
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "projection.completed-pending-confirmation",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = markEx.Message }));
            _logger?.Warning(
                $"Projection '{_handler.ProjectionName}' handler succeeded but checkpoint completion could not be persisted (pending confirmation); error: {markEx.Message}");
        }
        return true;
    }
}
