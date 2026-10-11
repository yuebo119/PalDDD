// ─────────────────────────────────────────────────────────────
// ⏱ PeriodicBackgroundProcessor — 定时轮询后台服务基类
// ─────────────────────────────────────────────────────────────
//
// 💡 OutboxProcessor 与 SagaProcessor 共享同一模板：
//   ｜ PeriodicTimer + IServiceScopeFactory + while 循环 + try/catch 隔离
//   ｜ 提取基类消除 ~40 行重复，子类只实现 ExecuteTickAsync + OnTickFailed。
//
// ✅ AOT 安全：零反射。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

/// <summary>
/// 定时轮询后台服务基类。<br/>
/// 封装轮询生命周期 + 循环 + 异常隔离，子类只需实现每轮逻辑。
/// </summary>
public abstract partial class PeriodicBackgroundProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan>? _pollIntervalProvider; // T1.2：热读形态（非 null 时每轮取当前值）
    private readonly TimeProvider _timeProvider;
    private volatile bool _disposed; // v19 P2 + v20 volatile：Dispose 线程写/循环线程读的 stale 窗口收口

    protected PeriodicBackgroundProcessor(
        IServiceScopeFactory scopeFactory,
        TimeSpan pollInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _pollInterval = pollInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 热读轮询间隔构造形态（T1.2，2026-10-11 优化实施轮）：间隔由 provider 每轮动态提供
    ///（IOptionsMonitor.CurrentValue 热更新即时生效），替代构造期一次定格的冷快照形态。
    /// </summary>
    protected PeriodicBackgroundProcessor(
        IServiceScopeFactory scopeFactory,
        Func<TimeSpan> pollIntervalProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(pollIntervalProvider);
        _scopeFactory = scopeFactory;
        _pollIntervalProvider = pollIntervalProvider;
        _pollInterval = TimeSpan.Zero; // 不参与（provider 非 null 分支恒走 provider）
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>本轮轮询等待的当前间隔（热读形态生效点）。</summary>
    private TimeSpan CurrentPollInterval => _pollIntervalProvider?.Invoke() ?? _pollInterval;

    protected IServiceScopeFactory ScopeFactory => _scopeFactory;

    /// <summary>
    /// 子类在 <see cref="ExecuteTickAsync"/> 返回前报告「本轮拿到了满批（可能还有积压）」——
    /// 为 true 时基类跳过整轮轮询间隔、仅让步 DrainTickDelay 后立即续批（自适应排空，
    /// T1.2）；为 false（含异常路径的默认值）时按 CurrentPollInterval 正常轮询。
    /// 默认 false = 既有子类（SagaProcessor 等）行为逐位不变。
    /// </summary>
    protected virtual bool TickHasPendingWork => false;

    /// <summary>
    /// 排空模式的批间让步：仅用于避免连续满批时的零延迟忙转（每轮 tick 本身含真实
    /// DB I/O，毫秒级），不是退避语义——满批即视为有积压，全速排空。
    /// </summary>
    private static readonly TimeSpan DrainTickDelay = TimeSpan.FromMilliseconds(1);

    [SuppressMessage("Design", "CA1031:Do not catch general exception",
        Justification = "后台轮询循环必须隔离任意异常以防止循环中断；OperationCanceledException 已由前两个 catch 分支处理，此分支兜底非取消异常并回调 OnTickFailed。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 审计 2026-09-17 T-2：原 PeriodicTimer 不接受 TimeProvider，测试只能挂钟等待。
            // 改为 Task.Delay(interval, timeProvider, ct)——生产行为等价（系统时钟），
            // 测试可注入 FakeTimeProvider 实现确定性 tick。
            // T1.2（2026-10-11 优化实施轮）：自适应排空——上一轮满批（TickHasPendingWork）
            // 时仅让步 DrainTickDelay 立即续批（积压期吞吐从 batchSize/(tick+interval) 提到
            // batchSize/tick），否则按 CurrentPollInterval 轮询（热读形态在 provider 构造下生效）。
            while (!stoppingToken.IsCancellationRequested)
            {
                var drainNext = TickHasPendingWork;
                var interval = drainNext ? DrainTickDelay : CurrentPollInterval;
                try
                {
                    await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try { await ExecuteTickAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                // ITM-166 修复（声明）：此分支为 OCE 吞弃的边界声明——当前 ExecuteTickAsync
                // 只接收 stoppingToken，tick 内部无 linked-CTS 超时（OutboxBatchProcessor/
                // SagaProcessor 均未创建带独立超时的 CTS），因此"下游取消但 Host 未关停"
                // 的 OCE 理论不可达。保留该分支是防御性边界：若未来 tick 引入内部超时
                // linked-CTS，其 OCE 不应计入 OnTickFailed（失败指标/日志不应记录取消），
                // 也不应中断整个轮询循环。语义：静默忽略是设计性吞弃，非异常处理遗漏。
                catch (OperationCanceledException) { /* 下游取消但 Host 未关停，静默忽略（见上方边界声明） */ }
                catch (ObjectDisposedException) when (_disposed)
                {
                    // v20 A-P3-1 机理勘正：Dispose 与等待/执行竞态——_disposed 为 true
                    // 证明由本服务停机引发，归类为正常退出（对齐上方 OCE 分支）。
                    break;
                }
                catch (Exception ex)
                {
                    // 全仓扫描修复（契约对齐）：本方法的 CA1031 抑制理由写明「后台轮询循环必须
                    // 隔离任意异常以防止循环中断」，但 OnTickFailed 自身抛出时（日志 sink 故障、
                    // 已释放的 logger、指标序列化失败等）异常会从 catch 块内逃逸，把整个轮询循环
                    // 打死——与声明相反（也与 OnTickFailed 的「基类保证循环不中断」契约相反）。
                    // 独立隔离该回调调用，使契约成立。空 catch 由本方法的 CA1031 抑制覆盖。
                    try { OnTickFailed(ex); }
                    catch { /* 失败回调自身异常不得中断轮询——见上方契约 */ }
                }
            }
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // 停机期 ODE 正常退出。
        }
    }

    /// <summary>每轮执行的逻辑（在 scope 内调用，异常被基类隔离）。</summary>
    protected abstract ValueTask ExecuteTickAsync(CancellationToken ct);

    /// <summary>每轮异常回调（子类记录日志）。基类保证循环不中断。</summary>
    protected abstract void OnTickFailed(Exception ex);

    public override void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
        base.Dispose();
    }
}
