using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// EF Core 检查点持久化
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core 投影检查点存储基础上下文。</summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class ProjectionCheckpointDbContext(DbContextOptions options) : DbContext(options), IProjectionCheckpointStore, IResumableProjectionCheckpointStore
{
    /// <summary>投影 checkpoint 表</summary>
    public DbSet<ProjectionCheckpoint> ProjectionCheckpoints => Set<ProjectionCheckpoint>();

    // A5（S1 轮，2026-10-15）：固定形状单行查询编译化——EF Core 每次执行都把表达式树
    // 重新翻译成 SQL（无自动计划缓存；EF.CompileAsyncQuery 的官方存在理由即消除重复
    // 编译开销）。两个查询是投影路径每消息的固定形状热点（读路径 AsNoTracking、
    // TryStart 跟踪查询两形态分别编译，跟踪语义随委托固化）。按 IModel 引用各缓存一份
    // 委托：编译委托与单一模型绑定（EF 官方约束），同上下文类型跨 provider 模型实例
    // 不同，按模型分持。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Microsoft.EntityFrameworkCore.Metadata.IModel,
        Func<ProjectionCheckpointDbContext, string, string, string, CancellationToken, Task<ProjectionCheckpoint?>>> GetCheckpointNoTrackingByModel = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Microsoft.EntityFrameworkCore.Metadata.IModel,
        Func<ProjectionCheckpointDbContext, string, string, string, CancellationToken, Task<ProjectionCheckpoint?>>> GetCheckpointTrackedByModel = new();

    /// <inheritdoc/>
    /// <remarks>
    /// 三十八轮 P3 修复：变更追踪无界增长——长驻 scope 下每次查询的跟踪条目滞留
    /// ChangeTracker。本方法为纯读契约（结果不进 Mark*+SaveChanges 写回路径，接口
    /// doc 与全部调用方已核实），改 AsNoTracking 后零跟踪残留。
    /// </remarks>
    public async ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName,
        string sourceName,
        string position,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);

        // A5（S1 轮）：读路径走按模型缓存的编译委托（AsNoTracking 形态随委托固化）
        return await GetCheckpointNoTrackingByModel.GetOrAdd(Model, static _ =>
            EF.CompileAsyncQuery<ProjectionCheckpointDbContext, string, string, string, ProjectionCheckpoint?>(
                (ctx, projectionName, sourceName, position, ct) => ctx.ProjectionCheckpoints
                    .AsNoTracking()
                    .SingleOrDefault(
                        x => x.ProjectionName == projectionName
                            && x.SourceName == sourceName
                            && x.Position == position)))(this, projectionName, sourceName, position, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 三十八轮 P3 修复：变更追踪无界增长——本方法保持跟踪查询（僵尸回收分支的
    /// MarkProcessing 突变依赖 ChangeTracker 持久化，AsNoTracking 会使保存静默丢失），
    /// 但在每条返回路径上 Detach 清理跟踪条目；调用方后续的
    /// MarkCompletedAsync/MarkFailedAsync 自带 AttachIfDetached 兜底，Detach 后语义不变。
    /// </remarks>
    public async ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);

        // v33 P3：processingTimeout 必须非负（四栈 Checkpoint 族唯一漏网——镜像 v29 S8
        // InboxDbContext.cs:50-51 / DapperProjectionCheckpointStore ITM-107 姊妹守卫）：
        // 领域方法 MarkProcessing 无守卫，负值使 LeaseUntil = startedAt + timeout < startedAt，
        // 下方"Processing 且 LeaseUntil > startedAt"防抢占判定恒假，刚启动的 Processing
        // 检查点被误判僵尸可抢占。允许 TimeSpan.Zero（仅禁负值，对齐姊妹口径）。
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

        // B7(a)（EPSL 地毯审计 2026-10-16 B 批 #7，镜像 InboxDbContext A9 直插优先）：
        // 新位路径 3 语句（跟踪 SELECT + INSERT + 调用方终态 UPDATE）降为 2（INSERT +
        // UPDATE）。仅关系型 provider 生效——(ProjectionName, SourceName, Position) 唯一
        // 约束的强制是关系型保证；InMemory 等无唯一约束 provider 保持预 SELECT 路径
        // （否则重复位静默建第二行，幂等失效）。唯一冲突（TryCreate 返回 null）才走下方
        // 既有跟踪 SELECT 分类路径（Completed/活跃 Processing → null；僵尸/失败 → 回收）——
        // 回查即原预 SELECT 的职责，仅在冲突路径执行。权衡同 A9：重复位路径由 1 往返
        // （预查命中）变为 INSERT 失败 + 回查（2 起），回放负载以新位为主（ADR-028 增量
        // 续读）净胜；纯重放既有行场景持平偏负。
        if (Database.IsRelational())
        {
            var created = await TryCreateCheckpointAsync(
                projectionName, sourceName, position, startedAt, processingTimeout, ct).ConfigureAwait(false);
            if (created is not null)
                return created;
        }

        // A5（S1 轮）：跟踪查询走按模型缓存的编译委托（僵尸回收分支的 MarkProcessing
        // 突变依赖 ChangeTracker 持久化，保持跟踪形态；编译只固化查询形状，跟踪语义不变）
        var checkpoint = await GetCheckpointTrackedByModel.GetOrAdd(Model, static _ =>
            EF.CompileAsyncQuery<ProjectionCheckpointDbContext, string, string, string, ProjectionCheckpoint?>(
                (ctx, projectionName, sourceName, position, ct) => ctx.ProjectionCheckpoints
                    .SingleOrDefault(
                        x => x.ProjectionName == projectionName
                            && x.SourceName == sourceName
                            && x.Position == position)))(this, projectionName, sourceName, position, ct).ConfigureAwait(false);

        if (checkpoint is null)
            return await TryCreateCheckpointAsync(projectionName, sourceName, position, startedAt, processingTimeout, ct).ConfigureAwait(false);

        // 已完成的位置永远不会重新处理。
        if (checkpoint.Status == ProjectionCheckpointStatus.Completed)
        {
            // 三十八轮 P3 修复：未命中写回路径的只读分支同样清理跟踪条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }

        // 活跃的工作器 —— 租约尚未过期。
        if (checkpoint.Status == ProjectionCheckpointStatus.Processing && checkpoint.LeaseUntil > startedAt)
        {
            // 三十八轮 P3 修复：同上——清理跟踪条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }

        // 僵尸（处理中 + 已过期）或失败 —— 通过 MarkProcessing 回收。
        checkpoint.MarkProcessing(startedAt, processingTimeout);

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后立即 Detach——租约已落库，后续 Mark* 走
            // AttachIfDetached 重挂，ChangeTracker 不残留本条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return checkpoint;
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }
        catch (DbUpdateException)
        {
            // v26 P2 修复：非并发瞬时故障（连接闪断/超时）上抛前 Detach——checkpoint 已被
            // MarkProcessing 变异为 Modified（含 Revision++），滞留 ChangeTracker 会被下次
            // 无关 SaveChanges 提交为从未成功获取的幽灵租约（WHERE Revision=orig 匹配 DB
            // 真值必成功），锁死该投影位置至 LeaseDuration 过期——镜像 IdempotencyDbContext
            // 三十八轮 P2 全修样板
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
        catch
        {
            // ITM-632：保存失败（含 OCE 与非 DbUpdate 异常）逃逸前 Detach——checkpoint 在进入
            // try 前已被 MarkProcessing 变异为 Modified（含 Revision++），异常逃逸后滞留
            // ChangeTracker 会被下次无关 SaveChanges 提交为从未成功获取的幽灵租约（WHERE
            // Revision=orig 匹配 DB 真值必成功），锁死该投影位置至 LeaseDuration 过期。
            // 与上方 DbUpdateException 分支同款 Detach 手法；异常（含 OCE）原样上抛，
            // 不吞取消、不转成功语义。
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask MarkCompletedAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // v53 P3：仅 Processing 可完成（对齐 InMemory 姊妹的状态机守卫）——直调 Store 绕过
        // 管线时，Failed/Completed 实例的 MarkCompleted 不再翻转状态落库；幂等无害
        if (checkpoint.Status != ProjectionCheckpointStatus.Processing)
            return;

        AttachIfDetached(checkpoint);
        checkpoint.MarkCompleted(completedAt);
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后 Detach（与 TryStartAsync 无界增长修复同轮）——
            // 状态已落库，ChangeTracker 不残留本条目
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 修复（八轮评审）：Revision 并发令牌冲突 = 租约已被其他工作器回收，被抢占者的
            // 标记不生效——Detach 清理跟踪状态后静默返回，不掩盖原始业务异常、不中止回放
            // （对齐上方 TryStartAsync 的既有捕获模式）。
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // v27 P2 修复：非并发瞬时故障上抛前 Detach（与 MarkFailedAsync 同型，样板同上）
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
        catch
        {
            // ITM-632：同 TryStartAsync——OCE 及非 DbUpdate 异常逃逸前 Detach，避免已
            // MarkCompleted 的内存态滞留被后续无关 SaveChanges 提交为幽灵终态；
            // 异常原样上抛（不吞取消、不改语义）
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask MarkFailedAsync(
        ProjectionCheckpoint checkpoint,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        // v54 P3 口径声明：EFCore 仅拦 Completed（Failed 重复 MarkFailed 允许更新错误信息并落库），
        // InMemory 姊妹的 IsCurrentLeaseHolder 更严格（非 Processing 全拦）——系统性口径差非孤例，
        // 失败信息更新是有用行为，保留宽松口径。
        // v35 P3：Completed 终态防御——Completed 不可翻转为 Failed（终态语义）。镜像
        // PalORM 版 PalOrmProjectionCheckpointStore.MarkFailedAsync 的 SQL
        // `AND status <> Completed` 守卫与 InMemoryInboxStore.MarkProcessedAsync 的
        // 所有权/状态守卫先例；EFCore 版此前缺内存侧防御，同 scope 复用或竞态场景下
        // 直调本方法可把 Completed 检查点改写为 Failed 并落库（重建回放被误触发）。
        if (checkpoint.Status == ProjectionCheckpointStatus.Completed)
            return;

        AttachIfDetached(checkpoint);
        // v26 P3 修复：存储层截断兜底（FailureReason.Normalize）——Error 列 HasMaxLength(2048)，
        // 超长原因使 MarkFailed 持久化自身抛 DbUpdateException 掩盖原始投影失败（十七轮
        // InboxDbContext 同型场景）；调用层 ProjectionProcessor 已 Normalize，此处防御
        // 直调 Store 路径（对齐 v22 B 批 DapperProjectionCheckpointStore 2040 先例）
        checkpoint.MarkFailed(Core.FailureReason.Normalize(failureReason), failedAt);
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后 Detach（与 MarkCompletedAsync 同型，无界增长）
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 修复（八轮评审）：Revision 并发令牌冲突 = 租约已被其他工作器回收，被抢占者的
            // 标记不生效——Detach 清理跟踪状态后静默返回，不掩盖原始业务异常、不中止回放
            // （对齐上方 TryStartAsync 的既有捕获模式）。
            Entry(checkpoint).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // v27 P2 修复：非并发瞬时故障上抛前 Detach——checkpoint 已被变异为 Modified
            // （Revision 已递增），滞留 ChangeTracker 会使同 scope 下次 TryStart 的 identity
            // resolution 返回内存 Status=Completed 而 DB 未写的实例，误判"已完成"静默跳过
            // （重启/他节点接管后重复投影）——镜像 IdempotencyDbContext 全修样板
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
        catch
        {
            // ITM-632：同 MarkCompletedAsync——OCE 及非 DbUpdate 异常逃逸前 Detach，
            // 避免已 MarkFailed 的内存态（Status=Failed + Error）滞留为幽灵终态；
            // 异常原样上抛（不吞取消、不改语义）
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask ResetAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var matching = ProjectionCheckpoints
            .Where(x => x.ProjectionName == projectionName && x.SourceName == sourceName);

        if (Database.IsRelational())
        {
            // 关系型 provider：单条 SQL DELETE，零内存加载、零变更跟踪。
            await matching.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return;
        }

        // 非关系型 provider（如 InMemory）：回退到加载+RemoveRange 路径。
        // ExecuteDeleteAsync 在 InMemory provider 上会抛 InvalidOperationException。
        var checkpoints = await matching.ToListAsync(ct).ConfigureAwait(false);
        ProjectionCheckpoints.RemoveRange(checkpoints);
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ITM-632：保存失败（含 OCE 与非 DbUpdate 异常）逃逸前 Detach——RemoveRange 已把
            // 全部匹配条目变异为 Deleted，异常逃逸后滞留 ChangeTracker 会被同 context 下次
            // 无关 SaveChanges 提交为延迟 DELETE（窗口内他节点重建/新持有的检查点被误删）。
            // 对齐同文件 TryStartAsync/MarkCompletedAsync/MarkFailedAsync 的 ITM-632 手法；
            // 异常原样上抛（不吞取消、不改语义）。
            foreach (var checkpoint in checkpoints)
                Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <summary>增量回放续读位置（R6/P0-3，ADR-028）——语义详见
    /// <see cref="IResumableProjectionCheckpointStore"/>。两档执行：服务端聚合优先
    /// （<c>Convert.ToInt64(string)</c> 经 EF 翻译为方言 CAST——PG/SqlServer 族支持；
    /// 实测 SQLite provider 不翻译），翻译失败回退<b>最小投影客户端聚合</b>（仅取
    /// Position/Status 两列轻量行、AsNoTracking 零跟踪——无 payload 物化，仍免全流重读）。
    /// 数值 CAST 失败（脏数据）返回 null 回退全量回放。</summary>
    public async ValueTask<string?> GetResumePositionAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        // R11 F4：MySQL 的 CAST 对 "99abc" 截断为 99（不抛错），服务端聚合会把脏位置
        // 当数值并返回 max+1，续读跳过中间事件。MySQL 直接走下方客户端聚合（逐行解析，
        // 遇非数值返回 null）；SQLite 本就因 LINQ 翻译失败落到该档；PG 服务端 CAST 抛
        // 22P02 由 catch 返回 null。检查点行远轻于事件行，MySQL 的 O(检查点) 物化可接受。
        var providerName = Database.ProviderName;
        if (providerName is null
            || !providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var firstIncomplete = await ProjectionCheckpoints
                    .AsNoTracking()
                    .Where(x => x.ProjectionName == projectionName
                        && x.SourceName == sourceName
                        && x.Status != ProjectionCheckpointStatus.Completed)
                    .MinAsync(x => (long?)Convert.ToInt64(x.Position, System.Globalization.CultureInfo.InvariantCulture), ct)
                    .ConfigureAwait(false);
                if (firstIncomplete is { } incomplete)
                    return incomplete.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var max = await ProjectionCheckpoints
                    .AsNoTracking()
                    .Where(x => x.ProjectionName == projectionName && x.SourceName == sourceName)
                    .MaxAsync(x => (long?)Convert.ToInt64(x.Position, System.Globalization.CultureInfo.InvariantCulture), ct)
                    .ConfigureAwait(false);
                return max is { } value
                    ? (value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            }
            catch (System.Data.Common.DbException)
            {
                // 数值 CAST 失败（如 PG 对非数值 Position 串 22007 族）——续读是优化面，
                // 回退全量回放（语义兜底恒安全，对齐 Dapper 实现同款口径）
                return null;
            }
            catch (InvalidOperationException)
            {
                // LINQ→SQL 翻译失败（SQLite provider 不映射 Convert.ToInt64(string)，实测）——
                // 落下方最小投影客户端聚合（非全量回放：检查点行远轻于事件行）
            }
        }

        // 回退档：最小投影客户端聚合——两列轻量行 + AsNoTracking（零跟踪残留、零 payload）
        var rows = await ProjectionCheckpoints
            .AsNoTracking()
            .Where(x => x.ProjectionName == projectionName && x.SourceName == sourceName)
            .Select(x => new { x.Position, x.Status })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        long? minIncomplete = null;
        long? maxAny = null;
        foreach (var row in rows)
        {
            if (!long.TryParse(row.Position, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var rowPosition))
                return null; // 非数值 Position——回退全量回放
            maxAny = maxAny is null || rowPosition > maxAny ? rowPosition : maxAny;
            if (row.Status != ProjectionCheckpointStatus.Completed
                && (minIncomplete is null || rowPosition < minIncomplete))
                minIncomplete = rowPosition;
        }

        var resume = minIncomplete ?? (maxAny is { } maxRow ? maxRow + 1 : (long?)null);
        return resume is { } resumeValue
            ? resumeValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>配置投影 checkpoint 实体</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ProjectionCheckpoint>(e =>
        {
            e.HasKey(x => new { x.ProjectionName, x.SourceName, x.Position });
            e.Property(x => x.ProjectionName).HasMaxLength(256);
            e.Property(x => x.SourceName).HasMaxLength(256);
            e.Property(x => x.Position).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.UpdatedAt);
            e.Property(x => x.LeaseUntil);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Error).HasMaxLength(2048);
            e.HasIndex(x => new { x.ProjectionName, x.SourceName, x.Status });
        });
    }

    private async ValueTask<ProjectionCheckpoint?> TryCreateCheckpointAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct)
    {
        // v33 P3：同 TryStartAsync 的非负守卫——本方法是租约创建的唯一汇聚点，防御未来
        // 新增调用路径绕过上游守卫（当前仅 TryStartAsync 可达，纯纵深防御）
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

        var checkpoint = new ProjectionCheckpoint(
            projectionName,
            sourceName,
            position,
            ProjectionCheckpointStatus.Processing,
            startedAt);
        checkpoint.MarkProcessing(startedAt, processingTimeout); // set LeaseUntil + Revision
        ProjectionCheckpoints.Add(checkpoint);

        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // 三十八轮 P3 修复：保存成功后立即 Detach——新建租约已落库，后续 Mark* 走
            // AttachIfDetached 重挂，ChangeTracker 不残留本条目（无界增长）
            Entry(checkpoint).State = EntityState.Detached;
            return checkpoint;
        }
        // P2 修复（ITM-065 同型）：仅唯一约束冲突返回 null（语义=他人已持有租约）；
        // 连接故障等其他 DbUpdateException 上抛，避免基础设施故障被误判为租约竞争。
        catch (DbUpdateException ex) when (!IsUniqueConstraintViolation(ex))
        {
            // v10 P3-1：瞬时异常上抛前 Detach（对齐 EventLogDbContext 三十八轮姊妹形态）
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            Entry(checkpoint).State = EntityState.Detached;
            return null;
        }
        catch
        {
            // ITM-632：保存失败（含 OCE 与非 DbUpdate 异常）逃逸前 Detach——新建 checkpoint
            // 已 Add 为 Added，异常逃逸后滞留 ChangeTracker 会被下次无关 SaveChanges 以
            // INSERT 落库为从未成功获取的幽灵租约；异常原样上抛（不吞取消、不改语义）
            Entry(checkpoint).State = EntityState.Detached;
            throw;
        }
    }

    /// <summary>
    /// 判定 DbUpdateException 是否为唯一约束冲突（跨 provider 鸭子类型）。
    /// <para>与 EventLogDbContext/InboxDbContext/IdempotencyDbContext 对齐（ITM-003 同型）。</para>
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
            // ITM-193 修复（三十轮）：补 SqliteException 类型限定（对齐全仓姊妹，PD17）——
            // 裸消息匹配会在 TryCreateCheckpointAsync 主路径把文案含该词组的非唯一约束
            // 误判为租约竞争返回 null，租约被静默让出。原"P3-3 已知局限"声明在姊妹统一
            // 修复后已过时，随本修复删除。
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

    private void AttachIfDetached(ProjectionCheckpoint checkpoint)
    {
        if (Entry(checkpoint).State == EntityState.Detached)
            ProjectionCheckpoints.Attach(checkpoint);
    }

    private static void ValidateKeyParts(string projectionName, string sourceName, string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
    }
}
