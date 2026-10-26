// ─────────────────────────────────────────────────────────────
// 🧹 RetentionProcessor — 分批清理执行器
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Options;

namespace PalDDD.Transactions;

/// <summary>按 <see cref="RetentionOptions"/> 执行一轮分批清理（Scoped）。</summary>
public sealed class RetentionProcessor
{
    private readonly IRetentionStore _store;
    private readonly IOptionsMonitor<RetentionOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造清理执行器。</summary>
    public RetentionProcessor(
        IRetentionStore store,
        IOptionsMonitor<RetentionOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>执行一轮清理；每个表最多 <see cref="RetentionOptions.MaxBatchesPerTick"/> 批。</summary>
    public async ValueTask<RetentionResult> RunOnceAsync(CancellationToken ct = default)
    {
        var options = _options.CurrentValue;
        options.Validate();
        var now = _timeProvider.GetUtcNow();

        var outbox = await RunBatchesAsync(
            (cutoff, n, token) => _store.DeleteExpiredOutboxAsync(cutoff, n, options.DeleteDeadOutboxMessages, token),
            now - options.OutboxRetention, options, ct).ConfigureAwait(false);
        var inbox = await RunBatchesAsync(
            (cutoff, n, token) => _store.DeleteExpiredInboxAsync(cutoff, n, token),
            now - options.InboxRetention, options, ct).ConfigureAwait(false);
        var idempotency = await RunBatchesAsync(
            (cutoff, n, token) => _store.DeleteExpiredIdempotencyAsync(cutoff, n, token),
            now, options, ct).ConfigureAwait(false);

        return new RetentionResult(outbox, inbox, idempotency);
    }

    private static async ValueTask<int> RunBatchesAsync(
        Func<DateTimeOffset, int, CancellationToken, ValueTask<int>> delete,
        DateTimeOffset cutoff,
        RetentionOptions options,
        CancellationToken ct)
    {
        var total = 0;
        for (var batch = 0; batch < options.MaxBatchesPerTick; batch++)
        {
            var deleted = await delete(cutoff, options.BatchSize, ct).ConfigureAwait(false);
            total += deleted;
            if (deleted < options.BatchSize)
                break;
        }
        return total;
    }
}
