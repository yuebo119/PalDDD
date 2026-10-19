// ─────────────────────────────────────────────────────────────
// 💾 ISagaStateStore<T> — Saga 状态持久化抽象
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>Saga 状态持久化抽象 — 解耦 Saga 存储与具体数据库实现</summary>
/// <remarks>
/// EF Core implementation is provided by the PalDDD.Transactions.EFCore adapter package.<br/>
/// 其他实现（MongoDB / Redis / DynamoDB）只需实现此接口即可接入 Saga 处理器。
/// </remarks>
/// <typeparam name="TState">Saga 状态类型</typeparam>
public interface ISagaStateStore<TState> where TState : SagaState
{    /// <summary>
    /// 获取一批活跃的 Saga 状态（<see cref="SagaStatus.Active"/> 与
    /// <see cref="SagaStatus.AwaitingHumanDecision"/>——三十四轮起中断态纳入扫描，
    /// 用于中断步骤超时兜底补偿；v43 勘正：IsTimedOut 判据含"步骤未成功完成"排除
    /// （ExecutedStepKeys 排除）——同状态已成功步骤的残留时间戳不再触发中断态兜底补偿）。
    /// </summary>
    ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct);

    /// <summary>
    /// 租约获取一批活跃 Saga，避免多实例后台扫描器重复处理同一状态。<br/>
    /// 扫描集含 <see cref="SagaStatus.Active"/> 与 <see cref="SagaStatus.AwaitingHumanDecision"/>
    ///（三十四轮中断态超时兜底）——是否补偿由 <c>SagaTimeoutProcessor.IsTimedOut</c> 门控：
    /// 中断步骤配置了 Timeout 且超期才触发补偿，否则仅经历租约获取/释放（显式无限等待）。
    /// </summary>
    ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct);

    /// <summary>根据 ID 获取 Saga 状态</summary>
    ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct);

    /// <summary>持久化 Saga 状态更改。</summary>
    /// <param name="state">被修改的状态实例。
    /// Dapper 适配器：必需，无变更跟踪。
    /// EF Core 适配器：内部使用 DbContext 变更跟踪，但建议传入以保持接口一致。</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>受影响行数：<c>1</c> 表示写入生效；<c>0</c> 表示目标行不存在或乐观锁冲突
    /// （他实例已写同一 Saga），调用方据此判定本实例内存快照作废（P3 修复·十七轮：
    /// 统一 EF/Dapper/内存三实现的返回值语义，避免调用方按 0 行误判或漏判冲突）。</returns>
    ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct);
}

/// <summary>
/// 到期时间租约能力（2026-10-19 性能迁移 M1）——只租约 <see cref="SagaState.NextCheckAt"/>
/// 已到期或为空的活跃 Saga。实现方在 SQL/查询层下推过滤；未实现本接口的自定义 store
/// 继续走 <see cref="ISagaStateStore{TState}.LeaseActiveSagasAsync"/> 全量路径。
/// <para>
/// 空值语义：NULL 表示“当前状态没有待检查的超时步骤”（等待事件），可安全跳过；
/// 回填完成前，调用方应保持 <c>SagaProcessorOptions.EnableDueTimeFilter=false</c>，
/// 让旧路径先为存量行补齐 NextCheckAt。
/// </para>
/// </summary>
public interface IDueTimeSagaStore<TState> : ISagaStateStore<TState> where TState : SagaState
{
    /// <summary>租约一批 NextCheckAt 已到期的活跃 Saga。</summary>
    ValueTask<IReadOnlyList<TState>> LeaseDueSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <summary>
/// Saga 租约批量释放能力（EPSL 地毯审计 2026-10-11 B-1，能力子接口——对齐
/// <c>IImmediateWriteOutboxStore</c> 先例：不破坏 <see cref="ISagaStateStore{TState}"/> 既有实现方）。
/// <para>超时扫描器对未超时 Saga 的租约释放（<c>SagaTimeoutProcessor.CheckTimeoutsAsync</c>
/// 非超时分支的常态路径）由此前的逐 saga <see cref="ISagaStateStore{TState}.SaveChangesAsync"/>
///（每 saga 一次 DB 往返 + 全量快照序列化；EF 栈还叠加全跟踪实体的 DetectChanges）改为
/// 单语句批量释放：仅清租约列并换代（version+1），以 <c>leased_by = owner</c> 为防抢占守卫
/// ——租约已被他实例抢占（重租换代）或已终态保存时，该行正确退化为不匹配（计入 0 行）。</para>
/// <para>⚠️ <b>已裁决取舍</b>：冲突信号粒度由「逐 saga Warning」收粗为「批次计数 Warning」
///（expected 与 affected 的差值，无法定位具体 saga）——P3（八轮）声明的排障信号保留、
/// 粒度收粗；需要逐条定位时存储未实现本接口即回落逐条路径。</para>
/// </summary>
public interface ISagaLeaseBatchStore<TState> : ISagaStateStore<TState> where TState : SagaState
{
    /// <summary>批量释放本 owner 持有的 Saga 租约（仅租约列 + 换代，不写业务字段）。</summary>
    /// <param name="owner">租约持有者（与 <see cref="ISagaStateStore{TState}.LeaseActiveSagasAsync"/> 的 owner 一致）。</param>
    /// <param name="sagas">待释放的 Saga 状态列表（只消费其 <see cref="SagaState.SagaId"/>；Dapper 栈按 Ulid 文本绑定）。</param>
    /// <param name="ct">取消令牌（终态写入语义下调用方传 <see cref="CancellationToken.None"/>）。</param>
    /// <returns>实际释放行数（<c>0 ≤ n ≤ sagas.Count</c>；<c>sagas.Count - n</c> = 被抢占/已换代的冲突数）。</returns>
    ValueTask<int> ReleaseLeasesAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct);
}
