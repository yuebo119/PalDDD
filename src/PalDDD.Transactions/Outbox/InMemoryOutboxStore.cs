using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 内存发件箱存储 — 测试和开发用
// ─────────────────────────────────────────────────────────────
//
// 💡 租约模式：
//   ｜ LeasePendingMessagesAsync 原子地获取消息处理权并设置 LockedBy + LockedUntil。
//   ｜ 其他实例在租约未过期前无法获取相同消息——实现多实例去重。
//   ｜
// 💡 RetryCount 递增：
//   ｜ ReleaseForRetry 内递增计数——确保与状态变更在同一逻辑操作中原子化。
//   ｜ 调用方（OutboxBatchProcessor）无需单独维护计数。
// ─────────────────────────────────────────────────────────────

/// <summary>内存发件箱存储 — 用于测试和单进程原型。</summary>
/// <remarks>
/// 💡 <b>时间抽象</b>：构造时可选注入 <see cref="TimeProvider"/>（默认 <see cref="TimeProvider.System"/>），
/// 测试中可传入 <c>FakeTimeProvider</c> 实现确定性租约过期/重试时序，
/// 与 <c>OutboxBatchProcessor</c>、<c>SagaTimeoutProcessor</c>、<c>OutboxDbContext.GetUtcNow()</c> 的时间抽象设计对齐。
/// </remarks>
public sealed class InMemoryOutboxStore : IPalOutboxStore
{
    private readonly Lock _lock = new();
    private readonly List<OutboxMessage> _messages = [];
    private readonly TimeProvider _timeProvider;

    /// <summary>创建内存发件箱存储。</summary>
    /// <param name="timeProvider">时间提供者（默认 <see cref="TimeProvider.System"/>），测试中可注入 <c>FakeTimeProvider</c></param>
    public InMemoryOutboxStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        // ITM-204 修复（三十一轮）：ct 对齐姊妹（同文件 LeasePendingMessagesAsync:57 与
        // InMemoryIdempotencyStore.GetAsync 均检查）——已取消请求不返回批次快照。
        ct.ThrowIfCancellationRequested();
        lock (_lock)
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(
                QueryPending(batchSize, maxRetryCount));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var pending = QueryPending(batchSize, maxRetryCount);

            var now = _timeProvider.GetUtcNow();
            var leased = new List<OutboxMessage>(pending.Count);
            // 三十八轮 P3 修复：引用索引表替代逐条 IndexOf 线性扫——批量租约 O(n²) → O(n)。
            // pending 元素即 _messages 内的同一引用（QueryPending 直接筛选），引用比较安全。
            var indexMap = new Dictionary<OutboxMessage, int>(pending.Count * 2, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < _messages.Count; i++)
                indexMap.TryAdd(_messages[i], i);
            foreach (var msg in pending)
            {
                // ITM-174 修复（二十九轮）：successor 替换——对齐 InMemoryInboxStore/
                // InMemoryIdempotencyStore 模式（ITM-105）。原实现原地改写并返回同一实例：
                // worker A 租约到期后 worker B 重租同一实例，A 的 MarkProcessed/MarkDead
                // 无任何校验即可覆盖 B 的活跃租约（僵尸标记）。替换后旧引用不再是列表
                // 持有者，其 Mark 被 IsCurrentLeaseHolder 守卫忽略。
                var successor = new OutboxMessage
                {
                    Id = msg.Id,
                    Type = msg.Type,
                    Payload = msg.Payload,
                    ContentType = msg.ContentType,
                    SchemaVersion = msg.SchemaVersion,
                    CorrelationId = msg.CorrelationId,
                    CausationId = msg.CausationId,
                    TraceParent = msg.TraceParent,
                    TraceState = msg.TraceState,
                    CreatedAt = msg.CreatedAt,
                    RetryCount = msg.RetryCount,
                    Status = OutboxStatus.Pending,
                    LockedBy = owner,
                    LockedUntil = now.Add(leaseDuration),
                    NextAttemptAt = null,
                    Error = null,
                    ProcessedAt = null
                };
                _messages[indexMap[msg]] = successor;
                leased.Add(successor);
            }

            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(leased);
        }
    }

    /// <inheritdoc/>
    public void AddMessage(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock) { _messages.Add(message); }
    }

    /// <inheritdoc/>
    public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (_lock)
        {
            // v25 P3 行为族 B2：两段式——先全量校验再添加。原单循环边校验边添加，
            // 中途遇 null 条目抛出时前面条目已写入（部分写入残留），调用方重试整批
            // 会产生重复消息；校验前置后失败批次零写入（全有全无语义）。
            // v30 P3（重复 Id 防护）：同批次内重复 Id（典型形态：同一 OutboxMessage 实例
            // Add 两次）在 LeasePending 的引用索引表（indexMap.TryAdd 保留首索引）下产生
            // 错位租约——successor 覆盖首位置后，重复条目的第二位置残留 Pending 原始引用，
            // 下轮 Lease 再次租出 → 重复发布。对齐 DB 栈 PK 约束行为（Ulid 主键冲突拒绝
            // 写入）：批内 Id 去重失败即抛 ArgumentException，零写入。
            // ⚠️ 边界声明：仅校验批内重复（最小修复）；跨批次重复（AddMessage 后再
            // AddMessagesAsync 同 Id）需全表 Id 索引，超出本修复范围——本栈定位为
            // 测试/单进程原型，批内拒绝已兜住主流误用形态。
            var seenIds = new HashSet<PalUlid>(messages.Count * 2);
            foreach (var msg in messages)
            {
                // v13 姊妹对称：单条 null 对齐同文件 AddMessage 的 ThrowIfNull——null 延迟到
                // QueryPending lambda 的 NRE 更难定位
                ArgumentNullException.ThrowIfNull(msg);
                if (!seenIds.Add(msg.Id))
                    throw new ArgumentException(
                        $"Duplicate OutboxMessage id '{msg.Id}' in the messages batch (Id must be unique, mirroring the DB primary-key constraint).",
                        nameof(messages));
            }
            foreach (var msg in messages)
            {
                _messages.Add(msg);
            }
        }
        return ValueTask.FromResult(messages.Count);
    }

    /// <inheritdoc/>
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        // P2 修复：三个状态变更方法与同文件其余 7 个方法对齐持锁，
        // 不再依赖字段书写顺序这一隐式契约保证可见性
        lock (_lock)
        {
            // ITM-174 修复（二十九轮）：所有权守卫——仅列表当前持有者可标记
            // （对齐 InMemoryInboxStore.IsCurrentLeaseHolder）。被 successor 替换后的
            // 旧引用（租约被其他 worker 重租）标记静默忽略，不覆盖新持有者状态。
            if (!IsCurrentLeaseHolder(message))
                return;

            message.ProcessedAt = processedAt;
            message.Status = OutboxStatus.Processed;
            message.Error = null;
            message.NextAttemptAt = null;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    /// <inheritdoc/>
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        lock (_lock)
        {
            if (!IsCurrentLeaseHolder(message))
                return;

            message.ProcessedAt = deadAt;
            message.Status = OutboxStatus.Dead;
            message.Error = failureReason;
            message.NextAttemptAt = null;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    /// <inheritdoc/>
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        lock (_lock)
        {
            if (!IsCurrentLeaseHolder(message))
                return;

            message.RetryCount++;
            message.Status = OutboxStatus.Pending;
            message.ProcessedAt = null;
            message.Error = failureReason;
            message.NextAttemptAt = nextAttemptAt;
            message.LockedBy = null;
            message.LockedUntil = null;
        }
    }

    /// <inheritdoc/>
    public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        // ITM-215 修复（三十二轮）：ct 对齐（同 GetPending/Lease 的 :45/:60——ITM-204 修复时漏本成员）
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        // ITM-216 修复（三十二轮）：retriedBy 截断兜底——Error 列上限 2048（同款于
        // OutboxDbContext.RequeueDeadAsync 的 2040 截断族）——v22 勘正：此处实际截 256（内存列上限声明不适用，
        // 与 2040 族的差异为防御性收窄：retriedBy 是运维标识不会接近 2048）
        // v30 P3：改经 FailureReason.Truncate 共享收口（项目全局别名 Core.）——裸 [..256]
        // 切片可能切半 UTF-16 代理对（超长含 emoji 的操作者标识），四栈 retriedBy 截断点同款
        var owner = Core.FailureReason.Truncate(retriedBy, 256);
        // v17 声明：now 取值在 lock 外——与 lock 内使用有微 TOCTOU，但仅影响审计时间戳精度
        // （不影响 fencing/token 正确性，对齐 Lease 路径可随统一重构处理）。
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == messageId && m.Status == OutboxStatus.Dead);
            if (msg is null) return ValueTask.FromResult(0);
            // 📐 P2 定案（三轮评审裁决）：retry_count 保留失败历史不重置（既有测试固化 +
            // PalORM 版 PD14 对齐）。语义：RequeueDeadAsync 是运维干预 API——重排后
            // GetPendingMessagesAsync 的 RetryCount < maxRetryCount 过滤仍生效，调用方
            // （运维工具）需确保 maxRetryCount > 消息当前 RetryCount 才能被拾取。
            msg.Status = OutboxStatus.Pending;
            msg.ProcessedAt = null;
            msg.Error = $"requeued by {owner} at {now:O}";
            msg.NextAttemptAt = nextAttemptAt;
            msg.LockedBy = null;
            msg.LockedUntil = null;
            return ValueTask.FromResult(1);
        }
    }

    /// <summary>
    /// 判定传入 message 是否仍为列表当前持有的活跃租约实例。
    /// 对齐 InMemoryInboxStore.IsCurrentLeaseHolder 守卫强度：引用一致（未被 successor
    /// 替换）+ 仍处租约中（LockedBy 非空）——不校验 LockedUntil 是否过期：真库语义
    /// （DapperOutboxStore.MarkProcessed 仅 WHERE id AND locked_by）允许处理时长超过
    /// 租约期限时仍标记（须在 <see cref="_lock"/> 内调用）。
    /// </summary>
    private bool IsCurrentLeaseHolder(OutboxMessage message)
        // P3-SRC-105（R44 ITM-280 勘正动机）：O(1) 谓词前置短路——实际命中场景是终态重复
        // Mark 与未租约引用（Status≠Pending 或 LockedBy=null 的 O(1) 拒绝）。原注释"僵尸引用
        // 在此 O(1) 短路"不成立：successor 替换换的是列表元素，旧引用自身字段冻结在租约快照
        //（Pending+非空 LockedBy），仍需走到 Contains 拒绝。
        // 未复用 LeasePendingMessagesAsync 的引用索引表：indexMap 为局部变量，租约后即
        // 丢弃；提升为字段需在 _messages 全部变更点同步维护 List+Dictionary 双结构，
        // 超出最小改动。活跃租约正常路径仍走 Contains（每消息批一次，规模=测试负载）。
        // v74 P3（P4-S2 收口）：Contains 显式传 ReferenceEqualityComparer——原默认比较器
        // 的正确性依赖 OutboxMessage 永不重写 Equals 的类型外部隐式契约（一旦改 record/
        // 加值相等，successor 替换后旧引用按 Id 值相等命中 Contains，旧 worker 覆盖新
        // 持有者——ITM-174 僵尸标记守卫静默击穿）。显式引用语义对齐同文件 :70 indexMap
        // 与姊妹 InMemoryInboxStore/InMemoryIdempotencyStore/InMemoryProjectionCheckpointStore
        // 的 ReferenceEquals/显式引用字典形态（三姊妹轴唯一漏网处）
        => message.Status == OutboxStatus.Pending
            && message.LockedBy is not null
            && _messages.Contains(message, ReferenceEqualityComparer.Instance);

    private List<OutboxMessage> QueryPending(int batchSize, int maxRetryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize); // v22 A 批：对齐 InMemorySagaStateStore :27/:50
        // v35 P3（EA3）行为声明：maxRetryCount 经 Options 每 tick 热读取——运行时调小后，
        // RetryCount >= 新值的 Pending 消息被本谓词永久过滤（既不派发也不转 Dead：Dead
        // 仅在 OutboxBatchProcessor 发布失败路径产生），消息滞留无出口；RequeueDeadAsync
        // 只处理 Dead 状态同样不可达。运维止血后需手工清理或调回 MaxRetryCount（声明见
        // OutboxOptions.MaxRetryCount）。本行为与 DB 栈（Dapper/EF 的同款拾取谓词）对齐，
        // 接口签名与行为不变。
        var now = _timeProvider.GetUtcNow();
        return _messages
            .Where(m => m.Status == OutboxStatus.Pending
                && m.RetryCount < maxRetryCount
                && (m.NextAttemptAt == null || m.NextAttemptAt <= now)
                && (m.LockedUntil == null || m.LockedUntil <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToList();
    }

    /// <inheritdoc/>
    public ValueTask<int> SaveChangesAsync(CancellationToken ct)
        => ValueTask.FromResult(0);
}
