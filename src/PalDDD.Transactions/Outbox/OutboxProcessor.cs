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
    /// <summary>被唤醒但租到 0 条时的自愈重试间隔（2026-10-22 性能轮）。</summary>
    /// <remarks>
    /// 写入方可能把 Outbox 写入包在自己的事务里，信号先于提交到达。这一轮租不到消息时，
    /// 用 20ms 的短间隔再试一次即可覆盖正常提交延迟；仍为空则回归 <see cref="OutboxOptions.PollInterval"/>。
    /// 取值只需大于「提交与信号之间的典型间隔」，小于它就没有收益。
    /// </remarks>
    private const int SignalCommitRetryMilliseconds = 20;

    private readonly IPalLogger<OutboxProcessor> _logger;
    private readonly IOutboxWakeUpSignal? _wakeUpSignal;
    private bool _lastTickFullBatch; // T1.2：上一轮满批信号（ExecuteTickAsync 内赋值，基类循环经 TickHasPendingWork 读取）
    private bool _wokeBySignal;      // 上一轮空闲等待是否由信号唤醒
    private bool _retrySoon;         // 被唤醒但没租到消息：下一轮用短间隔

    public OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<OutboxOptions> options,
        IPalLogger<OutboxProcessor> logger,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null,
        IOutboxWakeUpSignal? wakeUpSignal = null)
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
        _wakeUpSignal = wakeUpSignal;
    }

    // T1.2：满批信号上浮——基类循环据此进入自适应排空（满批立即续批）。
    // 方法首行置 false：异常路径不带出上一轮的陈旧 true。
    protected override bool TickHasPendingWork => Volatile.Read(ref _lastTickFullBatch);

    /// <summary>
    /// 空闲等待（2026-10-22 性能轮）：注册了 <see cref="IOutboxWakeUpSignal"/> 时改为
    /// 「等信号或到轮询间隔，先到者生效」；未注册时保持基类的纯 <c>Task.Delay</c> 形态。
    /// </summary>
    protected override async ValueTask WaitForIntervalAsync(TimeSpan interval, CancellationToken ct)
    {
        if (_wakeUpSignal is null)
        {
            await base.WaitForIntervalAsync(interval, ct).ConfigureAwait(false);
            return;
        }

        // 自愈：上一轮被信号唤醒但租到 0 条（写入方事务尚未提交）时，本轮用短间隔再试，
        // 避免消息白等一个完整的 PollInterval。仅缩短、不放大用户配置的间隔。
        if (_retrySoon)
        {
            _retrySoon = false;
            var retry = TimeSpan.FromMilliseconds(SignalCommitRetryMilliseconds);
            if (retry < interval)
                interval = retry;
        }

        _wokeBySignal = await _wakeUpSignal.WaitAsync(interval, ct).ConfigureAwait(false);
    }

    protected override async ValueTask ExecuteTickAsync(CancellationToken ct)
    {
        Volatile.Write(ref _lastTickFullBatch, false);
        using var scope = ScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        Volatile.Write(ref _lastTickFullBatch, await processor.ProcessBatchWithBacklogAsync(ct).ConfigureAwait(false));
        // 被信号唤醒但一条都没租到 = 写入方事务可能尚未提交，下一轮短间隔重试一次。
        _retrySoon = _wokeBySignal && processor.LastLeasedCount == 0;
        _wokeBySignal = false;
    }

    protected override void OnTickFailed(Exception ex)
        => _logger.Error(ex, "Outbox processing failed");
}
