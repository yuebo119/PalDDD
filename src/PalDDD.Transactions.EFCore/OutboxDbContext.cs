// ─────────────────────────────────────────────────────────────
// 📤 OutboxDbContext — EF Core 发件箱存储（租约 + RetryCount 原子递增）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;
using PalDDD.Core; // v30 P3：Outbox 截断点接入 FailureReason.Truncate 共享收口
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>EF Core 发件箱存储基础上下文。</summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class OutboxDbContext(DbContextOptions options) : DbContext(options), IPalOutboxStore
{
    /// <summary>发件箱消息表</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <inheritdoc/>
    void IPalOutboxStore.AddMessage(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message); // v13 姊妹对称：对齐 AddMessagesAsync/InMemory/PalORM 单条守卫（ITM-163 漏网）
        OutboxMessages.Add(message);
    }

    /// <inheritdoc/>
    async ValueTask<int> IPalOutboxStore.AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;
        // 优化（二十五轮 API 扫描 EF-10）：AddRangeAsync 仅对依赖异步值生成（HiLo/Sequence）
        // 的主键必要——OutboxMessage 主键 Ulid 预设（HasConversion 字符串存储，无 DB 生成），
        // 同步 AddRange 免逐实体 async 状态机开销。
        OutboxMessages.AddRange(messages);
        try
        {
            // 注：接口 IPalOutboxStore.AddMessagesAsync 无 CancellationToken 参数（v23 轮已
            // 裁决接口异步化属 v3.0 破坏性变更），取消信号不传播为已知契约限制
            return await SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // v27 P2 修复：失败上抛前全批 Detach——Added 实体滞留 ChangeTracker 会被同
            // DbContext 下次无关 SaveChanges 幽灵 INSERT（调用方以为失败已放弃的消息被写入，
            // 或重发后双份）——镜像 EventLogDbContext.DetachAddedEvents（三十八轮 ITM-226
            // "残留批次污染后续追加"同型同文件族）
            foreach (var message in messages)
                Entry(message).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠️ <b>基类默认 LINQ 的 provider 可译性（v25 P3 勘正族 C6）</b>：下方默认查询的
    /// <c>NextAttemptAt &lt;= now</c> / <c>LockedUntil &lt;= now</c>（DateTimeOffset 有序比较）与
    /// <c>OrderBy(CreatedAt)</c>（DateTimeOffset 排序）在 EF Core 11 的 SQLite provider 下
    /// <b>不可翻译</b>（运行时抛 "could not be translated"，见 SqliteOutboxDbContext 的 ITM-261 实证）——
    /// 派生类必须 override（对照 <c>SqliteOutboxDbContext.QueryEligibleAsync</c> 的物化后内存
    /// 过滤 + Id 排序翻页形态；当前 Sqlite/PostgreSql/MySql/SqlServer 四方言适配器均已
    /// override 兜住）。EF InMemory 测试走基类可正常求值（客户端评估），<b>会掩盖该限制</b>——
    /// SQLite 系派生类漏 override 将延迟到首个真实查询才暴露。
    /// </remarks>
    public virtual async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        // v16 姊妹对称：batchSize 非正守卫（对齐 Saga 族）——EF Take(0/负) 空返回静默无诊断
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var now = GetUtcNow();
        // 优化（二十五轮 API 扫描 EF-1）：AsNoTracking 跳过 ChangeTracker 物化（免快照 +
        // 身份解析开销）。只读契约（IPalOutboxStore.GetPendingMessagesAsync doc：
        // "只用于观测/健康检查，不获取租约"，保证不进 Mark*+SaveChanges）；
        // 违反契约的突变将静默丢失（非跟踪实体不经 SaveChangesAsync 持久化）。
        return await OutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending
                && m.RetryCount < maxRetryCount
                && (m.NextAttemptAt == null || m.NextAttemptAt <= now)
                && (m.LockedUntil == null || m.LockedUntil <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public abstract ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct);

    /// <inheritdoc/>
    /// <remarks>
    /// ITM-210 修复（三十二轮守卫 → 三十四轮 token 化）：关系型 provider 用 ExecuteUpdate 带租约守卫；
    /// v30 P3 勘正（原"内存突变 + SaveChanges"失实）：非 SQL 可翻译 provider（InMemory 测试）的
    /// 回退路径<b>仅变异 tracked 实体，不做任何 SaveChanges</b>——持久化由调用方经
    /// <see cref="IPalOutboxStore.SaveChangesAsync"/> 完成（OutboxBatchProcessor 在每条 Mark 后
    /// 调用 PersistSingleAsync 触发，非本方法职责）。<br/>
    /// <b>租约 token（三十四轮）</b>：持租调用方（<c>message.LockedBy</c> 非空）的终态写要求行内
    /// <c>(LockedBy, LockedUntil)</c> 与租约时捕获的标识对<b>完全匹配</b>——租约过期被重租（同 owner
    /// 复用或他 owner 接手）后，旧 worker 的终态写影响 0 行（<c>LockedUntil</c> 随每次租约单调变化，
    /// 充当 fencing token，免 DDL 加列）；无租约直呼（LockedBy 为 null，运维/测试路径）当行当前
    /// 未被租（<c>LockedBy IS NULL</c>）且 <c>RetryCount</c> 与入参快照一致时放行（v30 P3 补快照
    /// 守卫；v33 P3 起持租分支同要求 RetryCount 快照一致，见 <see cref="FencedTarget"/>）。
    /// </remarks>
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            // ITM-283（R45）：token 拒绝（affected=0 且无异常）提前 return——原兜底查询用同款
            // FencedTarget 守卫必然返回 null，纯冗余 DB 往返；兜底仅 provider 不支持路径需要。
            // v41 P3 清理：原 `if (affected > 0) return;` 后紧跟无条件 return，恒死分支删除
            //（affected>0 与 =0 均提前返回，语义不变），返回值不再接收
            FencedTarget(message)
                .ExecuteUpdate(s => s
                    .SetProperty(m => m.ProcessedAt, processedAt)
                    .SetProperty(m => m.Status, OutboxStatus.Processed)
                    .SetProperty(m => m.Error, (string?)null)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
            return;
        }
        catch (InvalidOperationException)
        {
            // ExecuteUpdate 不支持的 provider（EF InMemory 等）——回退到条件加载路径
        }

        // 兜底路径：仅 provider 不支持 ExecuteUpdate 时到达（ITM-283 收窄）
        {
            var tracked = FencedTarget(message).FirstOrDefault();
            if (tracked is not null)
            {
                tracked.ProcessedAt = processedAt;
                tracked.Status = OutboxStatus.Processed;
                tracked.Error = null;
                tracked.NextAttemptAt = null;
                tracked.LockedBy = null;
                tracked.LockedUntil = null;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ITM-210 修复（三十二轮守卫 → 三十四轮 token 化）：同 <see cref="MarkProcessed"/>——
    /// 租约 token 匹配守卫 + 非关系型回退。
    /// </remarks>
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        // v30 P3：改经 FailureReason.Truncate 共享收口——裸 [..2040] 切片可能切半 UTF-16
        // 代理对（超长含 emoji 的消息），末位高代理回退一位防孤立高代理入库
        //（MarkDead/ReleaseForRetry + retriedBy 同款，见 FailureReason.Truncate）
        var error = FailureReason.Truncate(failureReason, 2040);

        try
        {
            // ITM-283：同 MarkProcessed——token 拒绝提前 return，兜底仅 provider 不支持。
            // v41 P3 清理：同款死 if（`if (affected > 0) return;` + 无条件 return）删除
            FencedTarget(message)
                .ExecuteUpdate(s => s
                    .SetProperty(m => m.ProcessedAt, deadAt)
                    .SetProperty(m => m.Status, OutboxStatus.Dead)
                    .SetProperty(m => m.Error, error)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
            return;
        }
        catch (InvalidOperationException)
        {
            // 同 MarkProcessed——非关系型 provider 回退
        }

        // 兜底路径：同 MarkProcessed（清理原 `!translated || true` 恒真条件）
        {
            var tracked = FencedTarget(message).FirstOrDefault();
            if (tracked is not null)
            {
                tracked.ProcessedAt = deadAt;
                tracked.Status = OutboxStatus.Dead;
                tracked.Error = error;
                tracked.NextAttemptAt = null;
                tracked.LockedBy = null;
                tracked.LockedUntil = null;
            }
        }
    }

    /// <summary>
    /// 租约 token 守卫目标集——持租调用方匹配 (LockedBy, LockedUntil) 标识对且 RetryCount
    /// 与入参快照一致；无租约直呼（LockedBy 为 null）放行当前未被租的行，且要求 RetryCount
    /// 快照一致。
    /// </summary>
    /// <remarks>三十四轮 ITM-210 落地：原 <c>LockedBy IS NULL OR LockedBy == 原持有者</c> 守卫的
    /// "NULL 放行"分支正是 fencing 缺口——租约被释放（ReleaseForRetry/RequeueDead）后旧 worker
    /// 的终态写仍会命中；同 owner 复用（worker 重启）亦无防护。<c>LockedUntil</c> 随每次租约
    /// 单调变化（重租必在过期后，<c>新 until = 更晚的 now + duration &gt; 旧 until</c>），以
    /// 微秒精度存储（PG timestamptz / SQLite TEXT "O" 格式）下充当免 DDL 的 fencing token。<br/>
    /// v30 P3：无租约分支补 <c>RetryCount == message.RetryCount</c> 快照守卫（对齐 PalORM 版
    /// 全分支的 <c>retry_count = {retry}</c> 形态，PalOrmOutboxStore.MarkProcessed）——无租约
    /// 直呼的行从 Pending 被 ReleaseForRetry（RetryCount+1）或 RequeueDead 推进后，持旧快照的
    /// 调用方终态写不再命中（与持租分支的 LockedUntil token 同向收口）。<br/>
    /// v33 P3：持租分支同补 <c>RetryCount == 快照</c> 守卫（全分支对齐 PalORM 版
    /// MarkProcessed/MarkDead/ReleaseForRetry 的 retry_count 快照形态）——快照在捕获点冻结为
    /// 局部变量，lambda 不再引用可变实体属性；持旧 RetryCount 快照的调用方在行被并发推进后
    /// 其写不再命中。本方法为
    /// MarkProcessed/MarkDead/ReleaseForRetry 三方法共享目标集，一处守卫三方法生效。</remarks>
    private IQueryable<OutboxMessage> FencedTarget(OutboxMessage message)
    {
        var originalOwner = message.LockedBy;
        var originalUntil = message.LockedUntil;
        // v33 P3：RetryCount 快照在捕获点冻结（原无租约分支 lambda 直接引用 message.RetryCount
        // 可变属性）——两分支共用同一快照，对齐 PalORM 版全分支 retry_count 快照守卫
        var originalRetry = message.RetryCount;
        var target = OutboxMessages.Where(m => m.Id == message.Id);
        return originalOwner is null
            ? target.Where(m => m.LockedBy == null && m.RetryCount == originalRetry)
            : target.Where(m => m.LockedBy == originalOwner && m.LockedUntil == originalUntil && m.RetryCount == originalRetry);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// P2 修复（八轮评审）：<c>ExecuteUpdate</c> 生成带守卫的单条 UPDATE；三十四轮 ITM-210
    /// 升级为租约 token 守卫（<c>(LockedBy, LockedUntil)</c> 标识对匹配，见
    /// <see cref="FencedTarget"/>）——租约过期被重租/释放后，旧 worker 的失败释放影响 0 行，
    /// 不再清掉新租约或误增 retry_count。<br/>
    /// RetryCount 递增与状态变更在同一 SQL 内原子完成；入参 <paramref name="message"/>
    /// 不被修改——若其为 ChangeTracker 跟踪实体，同步改内存会在后续 SaveChangesAsync
    /// 因 RetryCount 并发令牌失配抛出假冲突。<br/>
    /// ⚠️ <b>Provider 约束（九轮评审声明）</b>：<c>ExecuteUpdate</c> 需要关系型 provider
    /// （SQLite/PG/MySQL/SqlServer 等）；EF InMemory/Cosmos 不支持，本方法会抛
    /// <see cref="InvalidOperationException"/>——非关系型测试场景请用 Dapper/PalORM/InMemory 适配器。
    /// </remarks>
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        // ITM-082 修复：存储层兜底截断（同款于 MarkDead 的 2040 截断）——Error 列 HasMaxLength(2048)，
        // 超长失败原因此前让 ExecuteUpdate 生成的 UPDATE 抛 provider 截断异常（PG 整条 UPDATE 失败 →
        // 消息滞留 Processing 且租约已过期 → 下轮重租后重试计数丢失；对齐 MarkDead 十七轮防御）
        // v30 P3：改经 FailureReason.Truncate 共享收口（代理对守卫，见 MarkDead 注释）
        var error = FailureReason.Truncate(failureReason, 2040);

        FencedTarget(message)
            // v33 P3：Status == Pending 守卫——无租约直呼（运维/测试路径）时防把 Processed/Dead
            // 行复活为 Pending（对齐同族 RequeueDeadAsync 的 Status == Dead 守卫；三栈同轮收口：
            // PalOrmOutboxStore.ReleaseForRetry / Dapper SqlTemplates.OutboxReleaseForRetry 同款）。
            // 合法持租路径不误伤：租约只落在 Pending 行上，持租处理中的行恒为 Pending。
            .Where(m => m.Status == OutboxStatus.Pending)
            .ExecuteUpdate(s => s
                .SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Error, error)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LockedBy, (string?)null)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 使用 <c>ExecuteUpdateAsync</c>（<c>RelationalQueryableExtensions</c> 扩展）
    /// 直接生成 UPDATE SQL，绕过 ChangeTracker，AOT 友好且无追踪开销。<br/>
    /// RetryCount 保留失败历史不重置；仅作用于 Status == Dead 的行。
    /// <para>
    /// ⚠️ <b>Provider 约束（v38 P3 声明，对齐 MarkProcessed/MarkDead/ReleaseForRetry
    /// 的"ExecuteUpdate 需关系型 provider"声明形态）</b>：<c>ExecuteUpdateAsync</c> 需要
    /// 关系型 provider（SQLite/PG/MySQL/SqlServer 等）；EF InMemory/Cosmos 不支持，本方法
    /// 会抛 <see cref="InvalidOperationException"/>——非关系型测试场景请用
    /// Dapper/PalORM/InMemory 适配器。
    /// </para>
    /// </remarks>
    public async ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        // ITM-216 修复（三十二轮）：retriedBy 截断兜底（截断族 2040）——Error 列上限 2048，
        // 超长 retriedBy 使 audit 串超列，ExecuteUpdateAsync 抛截断异常（对齐 MarkDead/ReleaseForRetry）
        // v30 P3：改经 FailureReason.Truncate 共享收口——裸 [..256] 切片可能切半 UTF-16 代理对
        //（超长含 emoji 的操作者标识），四栈 retriedBy 截断点同款
        var owner = FailureReason.Truncate(retriedBy, 256);
        var now = GetUtcNow();
        var audit = $"requeued by {owner} at {now:O}";

        return await OutboxMessages
            .Where(m => m.Id == messageId && m.Status == OutboxStatus.Dead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Error, audit)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LockedBy, (string?)null)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    async ValueTask<int> IPalOutboxStore.SaveChangesAsync(CancellationToken ct)
        => await SaveChangesAsync(ct).ConfigureAwait(false);

    /// <summary>获取当前 UTC 时间，派生测试上下文可重写以控制时间</summary>
    protected virtual DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    /// <summary>获取数据库特定的 NOW 函数（用于原始 SQL 查询），派生 provider 子类可重写</summary>
    /// <remarks>
    /// ⚠️ <b>派生类必须 override（P3-SRC-103）</b>：默认值 <c>CURRENT_TIMESTAMP</c> 仅供派生类
    /// 参考，直接沿用有已知坑——(a) MySQL 的 <c>CURRENT_TIMESTAMP</c> 返回会话时区时间而非 UTC
    ///（<c>MySqlOutboxDbContext</c> override 为 <c>UTC_TIMESTAMP()</c>）；(b) 默认
    /// <see cref="BuildPendingSql"/> 模板的列名无引号，在 PG（未加引号的标识符折叠为小写）下与
    /// 引号建表的混合大小写列不匹配（<c>PostgreSqlOutboxDbContext</c> override 为双引号列名 +
    /// <c>NOW()</c>）。新方言派生类必须同时 override 本方法与 <see cref="BuildPendingSql"/>
    ///（参考 <c>MySqlOutboxDbContext</c> / <c>PostgreSqlOutboxDbContext</c> / <c>SqliteOutboxDbContext</c>）。
    /// </remarks>
    protected virtual string GetNowSql() => "CURRENT_TIMESTAMP";

    /// <summary>
    /// 构建待处理消息的公共 WHERE 模板（无分页——排序/限行由 EF 可组合 LINQ 生成）。<br/>
    /// 💡 优化（二十四轮 OP-5）：手工 LIMIT/TOP/OFFSET 分页曾引发十七轮 P1（T-SQL TOP 位置
    /// 非法）——改为 FromSqlRaw + OrderBy + Take 让 EF provider 生成各方言分页，消灭整类缺陷面。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>派生类必须 override（P3-SRC-103）</b>：默认模板的裸列名（无引号）与
    /// <c>CURRENT_TIMESTAMP</c> 默认值仅供派生类参考——裸列名在 PG 下折叠为小写（与引号建表的
    /// 混合大小写列不匹配）、<c>CURRENT_TIMESTAMP</c> 在 MySQL 下返回会话时区时间而非 UTC。
    /// 见 <see cref="GetNowSql"/> 的同款警示与 <c>MySqlOutboxDbContext</c> /
    /// <c>PostgreSqlOutboxDbContext</c> 的 override 样例。
    /// </remarks>
    protected virtual string BuildPendingSql() => $$"""
        SELECT * FROM OutboxMessages
        WHERE Status = 0 AND RetryCount < {0}
          AND (NextAttemptAt IS NULL OR NextAttemptAt <= {{GetNowSql()}})
          AND (LockedUntil IS NULL OR LockedUntil <= {{GetNowSql()}})
        """;

    /// <summary>配置发件箱消息实体。</summary>
    /// <remarks>
    /// ⚠️ <b>派生类注意（P3-4）</b>：重写 <c>OnModelCreating</c> 时必须调用
    /// <c>base.OnModelCreating(modelBuilder)</c>，否则 <c>RetryCount.IsConcurrencyToken()</c>
    /// 等配置会静默失效。参考 <c>EventLogDbContext.OnModelCreating</c> 的调用模式。
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt });
            e.Property(x => x.Id).HasConversion(v => v.ToString(), v => PalUlid.Parse(v));
            e.Property(x => x.CorrelationId).HasConversion(v => v.HasValue ? v.Value.ToString() : default(string?), v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
            e.Property(x => x.CausationId).HasConversion(v => v.HasValue ? v.Value.ToString() : default(string?), v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
            e.Property(x => x.Type).HasMaxLength(512);
            e.Property(x => x.ContentType).HasMaxLength(128);
            e.Property(x => x.TraceParent).HasMaxLength(128);
            e.Property(x => x.TraceState).HasMaxLength(512);
            e.Property(x => x.LockedBy).HasMaxLength(256);
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.Error).HasMaxLength(2048);

            // 🔴 P1 修复 (2026-07-28): RetryCount 作为并发令牌，与 PalORM 的 [ConcurrencyCheck]RetryCount 对齐。
            // OutboxMessage 领域类型无 Version 字段，使用 RetryCount（int，PALORM012 兼容）作为乐观并发版本号。
            // 当两个 processor 同时拉取并 lease 同一条消息时，SaveChangesAsync 第二次提交会因 RetryCount 不匹配
            // 抛出 DbUpdateConcurrencyException，从而避免重复处理。
            e.Property(x => x.RetryCount).IsConcurrencyToken();
        });
    }
}
