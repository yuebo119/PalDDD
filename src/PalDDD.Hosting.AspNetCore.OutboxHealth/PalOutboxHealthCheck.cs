using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalDDD.Transactions;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Hosting.AspNetCore.OutboxHealth;

/// <summary>Outbox 存储健康检查（可选包）：对 <see cref="IPalOutboxStore"/> 做一次轻量查询。</summary>
/// <remarks>
/// 与 <c>PalDDD.Hosting.AspNetCore</c> 内置的同名检查行为一致；本包让不使用其他 Hosting
/// 能力的应用可以只引用健康检查所需的 Transactions 依赖。
/// 新老入口二选一，同时注册会产生两个名为 <c>outbox</c> 的检查。
/// </remarks>
public sealed class PalOutboxHealthCheck(IPalOutboxStore? store = null) : IHealthCheck
{
    private readonly IPalOutboxStore? _store = store;

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "健康检查需将依赖探针失败转换为 unhealthy 结果，需捕获 Exception 基类。")]
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_store is null)
            return HealthCheckResult.Degraded("发件箱存储未注册");

        try
        {
            await _store.GetPendingMessagesAsync(
                1, IPalOutboxStore.DefaultMaxRetryCount, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("发件箱存储可用（DB 查询成功）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("发件箱存储不可用", ex);
        }
    }
}
