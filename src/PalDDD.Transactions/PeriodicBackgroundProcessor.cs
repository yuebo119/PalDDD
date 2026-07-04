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
                catch (OperationCanceledException) { /* 下游取消但 Host 未关停，静默忽略 */ }
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
