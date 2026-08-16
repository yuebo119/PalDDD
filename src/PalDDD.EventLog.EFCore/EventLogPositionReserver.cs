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
/// ⚠️ <b>生命周期前提（P3·二十一轮明示）</b>："单例"是<b>注入前提</b>而非默认事实——
/// <see cref="EventLogDbContext"/> 构造默认 <c>positionReserver ?? new EventLogPositionReserver()</c>，
/// 而 DbContext 按 Scoped 解析，<b>默认路径下每个 Scoped 上下文各持一个独立预留器</b>：
/// chunk 缓存不跨 scope 共享，每 scope 首次追加都要走一次分配器行往返（Hi/Lo 收益退化为
/// 单上下文批内），且进程内 chunk 取块次数 = scope 数（间隙随 scope 数放大，正确性不受影响）。
/// 要兑现类头声明的"1/N 追加触及分配器行"收益，<b>必须以单例注入</b>：
/// 预留器构造为单例传入各上下文（内部 <c>_lock</c>/<c>_dbSemaphore</c> 已保证跨上下文线程安全）。
/// </para>
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
/// <para>
/// ⚠️ <b>回滚窗口（P2/P3 修复·十七轮声明）</b>：调用方事务回滚时，本预留器的进程内
/// <c>_lo/_hi</c> 游标<b>不回退</b>——
/// (a) 区块推进已随 <c>SaveChangesAsync</c> 提交而事件 INSERT 被回滚：已预留位置成为永久间隙
/// （无正确性影响，ReadAll 的 <c>&gt;=</c> 过滤兼容）；
/// (b) 调用方以显式事务包裹追加（allocator 推进与事件 INSERT 一同回滚）：进程内游标超前于
/// 持久化的 allocator 状态，进程存活期间继续分配高位（仅扩大间隙），但<b>重启后</b>从 DB
/// allocator 重新取块可能落在本进程先前已成功提交批次用过的位置区间——依赖 events 表
/// 唯一约束兜底报错（Hi/Lo 固有权衡，非缺陷修复项）。
/// </para>
/// </remarks>
public sealed class EventLogPositionReserver
{
    private readonly int _chunkSize;
    private long _lo; // next available position in the current chunk (inclusive)
    private long _hi; // upper bound of the current chunk (exclusive)
    private bool _initialized;
    private readonly Lock _lock = new();

    // 🔴 P1 修复 (2026-07-28): DbContext 非线程安全。
    // 即使 ReserveAsync 的快路径用 _lock 保护了进程内 chunk 缓存，
    // 当缓存耗尽时多个调用线程可能同时进入 AllocateNewChunkAsync，
    // 对同一个注入的 EventLogDbContext 并发执行 SaveChangesAsync / SingleOrDefaultAsync，
    // 导致 ChangeTracker / DbContext 内部状态损坏。
    // 用 SemaphoreSlim 串行化所有数据库访问。
    private readonly SemaphoreSlim _dbSemaphore = new(1, 1);

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
        // 🔴 P1 修复 (2026-07-28): DbContext 非线程安全 —— 串行化数据库访问。
        await _dbSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        finally
        {
            _dbSemaphore.Release();
        }
    }
}
