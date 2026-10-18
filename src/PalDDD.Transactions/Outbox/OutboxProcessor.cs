// ─────────────────────────────────────────────────────────────
// 📤 OutboxPublisher — 租约模式 + 重试 + 死信的 Outbox 发布
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 发件箱后台发布器
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 发件箱后台轮询服务。<br/>
/// 💡 <b>工作流程：</b><br/>
/// 1. 定期轮询 outbox_messages 表<br/>
/// 2. 原子租约获取一批待发送消息（多实例安全）<br/>
/// 3. 反序列化 → 发布到 Broker → 标记成功/失败<br/>
/// 4. 失败重试（最多10次）→ 超限标记为死信<br/>
/// ⚡ 使用 ConfigureAwait(false) 避免同步上下文捕获。
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "BackgroundService 需隔离轮询循环失败并继续处理后续批次，需捕获 Exception 基类。")]
public sealed class OutboxProcessor : PeriodicBackgroundProcessor
{
    private readonly IPalLogger<OutboxProcessor> _logger;
    private bool _lastTickFullBatch; // T1.2：上一轮满批信号（ExecuteTickAsync 内赋值，基类循环经 TickHasPendingWork 读取）

    public OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<OutboxOptions> options,
        IPalLogger<OutboxProcessor> logger,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
        // P3 修复（十七轮）：options 空守卫前移到 base 实参——原实参 pollInterval 为 null 时
        // 先在 options.CurrentValue 解引用 NRE，构造体内的 ThrowIfNull 永不可达
        // v29 P3：?? 短路勘正——pollInterval 非 null 时 (options ?? throw) 不求值，base 构造
        // 成功创建了 PeriodicTimer 后构造体 ThrowIfNull 才抛（构造中断 Dispose 不可达）→ timer
        // 泄漏。改为三元显式判 options：null 时在任何路径上都先于 base 调用求值抛出，
        // 不创建 timer
        // v30 P3（logger 轴扩展）：构造体 ThrowIfNull(logger) 同理在 base 成功后抛——timer
        // 已创建、Dispose 不可达 → 泄漏。logger 守卫合并进 base 第一实参（v29 options 轴
        // 三元形态扩展）：实参按出现顺序先于 base 体求值，logger/options 任一为 null 时
        // 均在 PeriodicTimer 创建前抛出；scopeFactory 轴无此问题（基类构造体在创建 timer
        // 之前先 ThrowIfNull(scopeFactory)）
        : base(
               logger is null
                   ? throw new ArgumentNullException(nameof(logger))
                   : scopeFactory,
               // T1.2（2026-10-11 优化实施轮）：改热读 provider 构造形态——PollInterval 经
               // IOptionsMonitor 每轮取当前值，运行时热更新即时生效（原冷快照声明就此废止，
               // OutboxOptions.PollInterval XML doc 同步）。守卫形态不变：options 为 null 时
               // 仍在 base 体求值前抛出（provider lambda 不会执行）；显式 pollInterval 覆盖
               // 值经 lambda 捕获恒定返回，等价旧形态。
               options is null
                   ? throw new ArgumentNullException(nameof(options))
                   : () => pollInterval ?? options.CurrentValue.PollInterval,
               timeProvider)
    {
        _logger = logger;
    }

    // T1.2：满批信号上浮——基类循环据此进入自适应排空（满批立即续批）。
    // 方法首行置 false：异常路径不带出上一轮的陈旧 true。
    protected override bool TickHasPendingWork => Volatile.Read(ref _lastTickFullBatch);

    protected override async ValueTask ExecuteTickAsync(CancellationToken ct)
    {
        Volatile.Write(ref _lastTickFullBatch, false);
        using var scope = ScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        Volatile.Write(ref _lastTickFullBatch, await processor.ProcessBatchWithBacklogAsync(ct).ConfigureAwait(false));
    }

    protected override void OnTickFailed(Exception ex)
        => _logger.Error(ex, "Outbox processing failed");
}
