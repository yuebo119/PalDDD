// ─────────────────────────────────────────────────────────────
// 🎯 EventLogPositionReserver — Hi/Lo 全局位置分配器（CAS 重试）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;

namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// Hi/Lo 全局位置分配器 — 消除 Serializable 事务瓶颈
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 单例 Hi/Lo 位置预留器 —— 在进程内缓存全局位置区块，
/// 仅在区块耗尽时才访问持久化分配器行。
/// </summary>
/// <remarks>
/// <para>
/// 这消除了旧设计中每次 <c>AppendAsync</c> 都需要在 <c>Serializable</c>
/// 事务内读取和更新单个分配器行所带来的全局序列化瓶颈。当区块大小为 N 时，
/// 只有 1/N 的追加操作触及分配器行；其余操作从进程内缓存分配位置，
/// 零数据库往返。
/// </para>
/// <para>
/// 区块耗尽时使用乐观并发（通过 <see cref="EventLogGlobalPositionAllocator.Revision"/> 的 CAS）。
/// 如果两个进程同时耗尽各自区块，失败方会重新加载分配器并分配新块进行重试。
/// </para>
/// <para>
/// GlobalPosition 值单调递增但可能存在间隙（进程崩溃时区块末尾的未用位置）。
/// ReadAll 使用 <c>&gt;= fromPosition</c> 过滤，因此间隙不影响正确性。
/// </para>
/// </remarks>
public sealed class EventLogPositionReserver
{
    private readonly int _chunkSize;
    private long _lo; // next available position in the current chunk (inclusive)
    private long _hi; // upper bound of the current chunk (exclusive)
    private bool _initialized;
    private readonly Lock _lock = new();

    /// <summary>使用指定的区块大小创建预留器。</summary>
    /// <param name="chunkSize">每次持久化分配的位置数量。值越大数据库往返越少，但崩溃时潜在间隙也越多。</param>
    public EventLogPositionReserver(int chunkSize = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        _chunkSize = chunkSize;
    }

    /// <summary>
    /// 预留 <paramref name="count"/> 个连续的全局位置。
    /// 返回预留范围的第一个位置。
    /// </summary>
    public async ValueTask<long> ReserveAsync(
        EventLogDbContext context,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        long? cached;
        lock (_lock)
        {
            if (_initialized && _lo + count <= _hi)
            {
                cached = _lo;
                _lo += count;
            }
            else
            {
                cached = null;
            }
        }

        if (cached is { } fastPath)
            return fastPath;

        // 区块已耗尽（或尚未初始化）—— 必须访问数据库。
        return await AllocateNewChunkAsync(context, count, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> AllocateNewChunkAsync(
        EventLogDbContext context,
        int count,
        CancellationToken cancellationToken)
    {
        const int maxRetries = 5;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var allocator = await context.GlobalPositionAllocators
                .SingleOrDefaultAsync(a => a.Id == EventLogGlobalPositionAllocator.SingletonId, cancellationToken)
                .ConfigureAwait(false);

            if (allocator is null)
            {
                allocator = EventLogGlobalPositionAllocator.Create();
                context.GlobalPositionAllocators.Add(allocator);
            }

            // Chunk must be large enough for this request.
            var chunkSize = Math.Max(count, _chunkSize);
            var first = allocator.AllocateChunk(chunkSize);

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // CAS 失败（Revision 不匹配）—— 另一个进程同时分配了区块。重试。
                context.Entry(allocator).State = EntityState.Detached;
                continue;
            }
            catch (DbUpdateException)
            {
                // 主键冲突 —— 另一个进程先插入了分配器行。重试。
                context.Entry(allocator).State = EntityState.Detached;
                continue;
            }

            lock (_lock)
            {
                _lo = first + count;
                _hi = first + chunkSize;
                _initialized = true;
            }

            return first;
        }

        throw new InvalidOperationException(
            $"Failed to allocate a global position chunk after {maxRetries} optimistic concurrency retries.");
    }
}
