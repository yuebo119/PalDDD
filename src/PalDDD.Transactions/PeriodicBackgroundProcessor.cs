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
/// 封装 PeriodicTimer 生命周期 + 循环 + 异常隔离，子类只需实现每轮逻辑。
/// </summary>
public abstract partial class PeriodicBackgroundProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PeriodicTimer _timer;

    protected PeriodicBackgroundProcessor(
        IServiceScopeFactory scopeFactory,
        TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        _timer = new PeriodicTimer(pollInterval);
    }

    protected IServiceScopeFactory ScopeFactory => _scopeFactory;

    [SuppressMessage("Design", "CA1031:Do not catch general exception",
        Justification = "后台轮询循环必须隔离任意异常以防止循环中断；OperationCanceledException 已由前两个 catch 分支处理，此分支兜底非取消异常并回调 OnTickFailed。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await ExecuteTickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                // ITM-166 修复（声明）：此分支为 OCE 吞弃的边界声明——当前 ExecuteTickAsync
                // 只接收 stoppingToken，tick 内部无 linked-CTS 超时（OutboxBatchProcessor/
                // SagaProcessor 均未创建带独立超时的 CTS），因此"下游取消但 Host 未关停"
                // 的 OCE 理论不可达。保留该分支是防御性边界：若未来 tick 引入内部超时
                // linked-CTS，其 OCE 不应计入 OnTickFailed（失败指标/日志不应记录取消），
                // 也不应中断整个轮询循环。语义：静默忽略是设计性吞弃，非异常处理遗漏。
                catch (OperationCanceledException) { /* 下游取消但 Host 未关停，静默忽略（见上方边界声明） */ }
                catch (Exception ex) { OnTickFailed(ex); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host 停止会取消 WaitForNextTickAsync，这是后台循环的正常退出路径。
        }
    }

    /// <summary>每轮执行的逻辑（在 scope 内调用，异常被基类隔离）。</summary>
    protected abstract ValueTask ExecuteTickAsync(CancellationToken ct);

    /// <summary>每轮异常回调（子类记录日志）。基类保证循环不中断。</summary>
    protected abstract void OnTickFailed(Exception ex);

    public override void Dispose()
    {
        _timer.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }
}
