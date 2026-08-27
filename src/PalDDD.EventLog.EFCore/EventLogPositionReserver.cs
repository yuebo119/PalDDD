// ─────────────────────────────────────────────────────────────
// 🎯 EventLogPositionReserver — Hi/Lo 全局位置分配器（CAS 重试）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

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
/// ⚠️ <b>倒挂-跳过风险（三十八轮 P2 声明）</b>：Hi/Lo 使 GlobalPosition 的<b>分配序</b>与
/// <b>事务提交序</b>可倒挂——事务 B 预留更高块 [110-119] 先提交、事务 A 预留 [100-109] 后提交时，
/// 按 <c>ReadAllAsync(fromPosition)</c> 检查点消费的消费方读到 110 即推进检查点，A 提交后其
/// 事件被永久跳过。检查点用法要求所有追加方提交延迟相近，或改用 <c>ReadStreamAsync</c>
/// （流内 StreamVersion 严格连续，不受 Hi/Lo 影响）。
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
            // ITM-166 声明（理论不可达）：_lo + count 在 long.MaxValue 附近可能溢出。
            // 实际不可达：_lo 由 DB 分配器行的 NextGlobalPosition 单调递增而来，
            // 达到 long.MaxValue 前需要追加 2^63 个事件（以 1M events/s 计约 29 万年）。
            // 若未来把 GlobalPosition 迁移为 32 位或分配器可外部改写，需在此加 checked。
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
                catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException
                    && !IsUniqueConstraintViolation(ex))
                {
                    // v10 P3-1：瞬时异常（连接断/超时）上抛前 Detach——长驻 context 重试 Add
                    // 同键分配器抛 identity conflict（对齐 EventLogDbContext 三十八轮姊妹）
                    context.Entry(allocator).State = EntityState.Detached;
                    throw;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // v29 P2 修复（镜像 v28 唯一冲突分支的快照快速失败手法）：CAS 失败后原实现
                    // 直接 continue，但 MySQL REPEATABLE READ（外层显式事务内快照固定）下，
                    // 下一迭代重查恒返回快照旧 Revision → 再 UPDATE 必再败 → 5 次空转后抛
                    // 误导性 "optimistic concurrency retries" IOE（与 v28 唯一冲突分支同机理）。
                    // 修复：CAS 失败本身即证明 DB 真值 Revision 已被并发提交推进（UPDATE 是
                    // 当前读），再以 AsNoTracking 探测——若探测仍返回与本轮 CAS 基准（Original
                    // Revision）相同的旧值，说明本事务读视图看不到并发提交，同事务内重试不可能
                    // 成功，直接抛带快照语义的 IOE（外层调用方以新事务重试可恢复——新事务建立
                    // 新快照即可见）；若探测已见不同（更新）Revision（如 PG ReadCommitted 语句级
                    // 快照 / 无外层事务的自动提交），读视图新鲜，continue 正常重试。
                    // [推断] 基于 MySQL 一致性读语义推演，未跑真 MySQL 探针——CI 环境以
                    // InMemory mock 同构场景验证（见 ReserveAsync_CasFailureStaleSnapshot 测试）。
                    var snapshotRevision = context.Entry(allocator).Property(a => a.Revision).OriginalValue;
                    context.Entry(allocator).State = EntityState.Detached;
                    EventLogGlobalPositionAllocator? probe = null;
                    try
                    {
                        probe = await context.GlobalPositionAllocators
                            .AsNoTracking()
                            .SingleOrDefaultAsync(a => a.Id == EventLogGlobalPositionAllocator.SingletonId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (DbException)
                    {
                        // PG aborted transaction（25P02）等探测失败——无法判定快照新鲜度，
                        // 退回原 continue 重试语义（最多 5 次后以既有 IOE 失败），
                        // 不掩盖原始 DbUpdateConcurrencyException
                    }
                    if (probe is not null && probe.Revision == snapshotRevision)
                        throw new InvalidOperationException(
                            "The allocator revision was advanced by a concurrent commit (the failed CAS proves it), but the probe still returns the same stale revision — under MySQL REPEATABLE READ the concurrently committed revision is not visible to the current transaction snapshot; retrying inside the same transaction cannot succeed. The caller should retry with a new transaction, which establishes a fresh snapshot and can see the new revision.");
                    continue;
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    // 主键冲突 —— 另一个进程先插入了分配器行。重试。
                    // v27 P3 修复（镜像 EventLogDbContext ITM-126 :270-273 aborted 重查降级）：
                    // PG 显式事务内本 23505 使事务进入 aborted 状态，直接 continue 后下一迭代
                    // SingleOrDefaultAsync 在 aborted 事务上抛 25P02（DbException）逃逸重试循环。
                    // 形态：就地探测重查——失败（aborted）则 throw; 保留本原始冲突异常上抛
                    // （不掩盖为误导性 retries-exhausted），成功则 continue 正常重试
                    context.Entry(allocator).State = EntityState.Detached;
                    var requerySucceeded = false;
                    var allocatorRowVisible = false;
                    try
                    {
                        allocatorRowVisible = await context.GlobalPositionAllocators
                            .AsNoTracking()
                            .AnyAsync(a => a.Id == EventLogGlobalPositionAllocator.SingletonId, cancellationToken)
                            .ConfigureAwait(false);
                        requerySucceeded = true;
                    }
                    catch (DbException)
                    {
                        // PG aborted transaction（25P02）等重查失败——保留原始 DbUpdateException 语义
                    }
                    if (!requerySucceeded)
                        throw;
                    // v28 P3 修复：探测 false 不再 continue 空转——唯一冲突（唯一性检查为当前读）
                    // 已证明分配器行存在，探测 false 只能是本事务快照看不到并发提交的行
                    //（MySQL REPEATABLE READ 固定快照：同事务内 continue 重试的
                    // SingleOrDefaultAsync 依然返回 null，必然再撞冲突再探测 false，5 次
                    // 空转后抛误导性的 "optimistic concurrency retries" IOE）。改为直接抛
                    // 带语义异常——外层调用方以新事务重试可恢复（新事务建立新快照即可见）。
                    if (!allocatorRowVisible)
                        throw new InvalidOperationException(
                            "Allocator row exists (the unique-constraint conflict proves it) but is invisible to the current transaction snapshot — under MySQL REPEATABLE READ the concurrently committed allocator row is not visible to this transaction's snapshot; retrying inside the same transaction cannot succeed. The caller should retry with a new transaction, which establishes a fresh snapshot and can see the row.");
                    continue;
                }

                // ITM-226 修复（三十二轮）：检测活动事务——外层事务回滚时 allocator UPDATE
                // 与事件 INSERT 一同回滚，但内存 _lo/_hi 已超前发布。重启后 DB allocator
                // 重新取块落在已用区间→唯一约束报错。
                // 修复策略：有活动事务时不更新内存游标（下次调用重读 DB，慢但正确）；
                // 无事务时 SaveChanges 原子提交，立即发布安全。
                // ⚠️ ITM-226 盲区（P3-SRC-405）：CurrentTransaction 只能检测 EF 显式事务
                // （BeginTransaction）——调用方以 TransactionScope 包裹时为 null，本检测
                // 失明：ambient 事务随后回滚会使 Hi/Lo 内存游标超前持久化 allocator（同
                // 类头"回滚窗口 (b)"场景），依赖 events 表唯一索引兜底报错。
                var hasActiveTransaction = context.Database.CurrentTransaction is not null;
                // P3-SRC-104：_initialized 写入统一在 _lock 内——原 else 分支持锁写而本分支裸写，
                // 双锁设施徒增读者困惑（语义等价：bool 原子写 + _dbSemaphore 已串行化本方法，
                // 不存在可见性缺口，纯纪律性收敛）
                lock (_lock)
                {
                    if (hasActiveTransaction)
                    {
                        // 不发布到内存——下次 ReserveAsync 走 AllocateNewChunkAsync 重读 DB
                        // （DB allocator 已推进，只是本进程不缓存它——正确但慢一个往返）
                        _initialized = false;
                    }
                    else
                    {
                        _lo = first + count;
                        _hi = first + chunkSize;
                        _initialized = true;
                    }
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

    /// <summary>
    /// 判定 <see cref="DbUpdateException"/> 内部异常是否为唯一约束冲突（ITM-071 修复）。
    /// <para>
    /// 与 <see cref="EventLogDbContext.IsUniqueConstraintViolation"/> 同型（ITM-003/ITM-065 家族第五处）；
    /// 通过反射鸭子类型读取 provider 异常属性，避免对具体 provider 包的硬依赖。
    /// 非唯一约束的 DbUpdateException（连接断开/字段溢出/超时等）不被捕获，原样向上传播——
    /// 此前把任意 DbUpdateException 当"主键冲突"重试 5 次，连接故障被掩盖为误导性的
    /// "optimistic concurrency retries" 异常。
    /// </para>
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            var typeName = type.Name;

            // PostgreSQL: Npgsql.PostgresException.SqlState == "23505"
            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(inner) is string sqlState
                && sqlState == "23505")
            {
                return true;
            }

            // MySQL: MySqlException.Number == 1062（ER_DUP_ENTRY）或 1586
            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int mysqlNumber
                && (mysqlNumber == 1062 || mysqlNumber == 1586))
            {
                return true;
            }

            // SQL Server: SqlException.Number == 2601 (unique index) 或 2627 (unique constraint / PK)
            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
            {
                return true;
            }

            // SQLite: Microsoft.Data.Sqlite.SqliteException 消息包含 "UNIQUE constraint"
            // 类型限定：裸消息匹配会把文案恰好含该词组的非唯一约束异常误判（镜像 InboxDbContext 十七轮修复）
            var message = inner.Message;
            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
