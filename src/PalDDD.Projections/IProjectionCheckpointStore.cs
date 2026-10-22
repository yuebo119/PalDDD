namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影检查点存储抽象
// ─────────────────────────────────────────────────────────────

public interface IProjectionCheckpointStore
{
    ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName,
        string sourceName,
        string position,
        CancellationToken ct = default);

    ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct = default);

    ValueTask MarkCompletedAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset completedAt,
        CancellationToken ct = default);

    ValueTask MarkFailedAsync(
        ProjectionCheckpoint checkpoint,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default);

    ValueTask ResetAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default);
}

/// <summary>
/// 增量回放续读位置能力（R6/P0-3，ADR-028，2026-10-14 性能实施轮）——检查点存储的
/// 高水位查询，供 <c>ProjectionRebuilder.ReplayAsync</c> 把增量回放从 O(全流长度)
/// 降为 O(增量)：按续读位置下推 <c>IEventReplaySource</c> 的起始读取位置，
/// 免除对已完成前缀的全量重读与逐事件 checkpoint 探查。
/// <para>
/// <b>语义（返回值三态）</b>：① 存在未完成位置（Failed / 僵尸 Processing）→ 返回其中
/// <b>最小数值位置</b>（首个洞——回放从该位置重试，与 ReplayAsync 既有契约
/// 「仅处理缺失或失败的检查点」逐字一致）；② 全部已完成 → 返回 (最大位置 + 1).ToString()；
/// ③ 无任何行（首跑 / <c>ResetAsync</c> 后）→ 返回 null（从头全量回放）。
/// </para>
/// <para>
/// <b>Position 数值契约</b>：管线内 Position 恒为数值串（<c>EventLogReplaySource</c> 产
/// StreamVersion.ToString()，流版本 0..N 连续），且顺序回放保证检查点行按位置前缀式产生
/// （缺失行先于既有行不可达——序回放先 TryStart 低位置）。实现遇<b>非数值 Position</b>
/// 或方言 CAST 失败时返回 null 回退全量回放（本查询是优化面，语义兜底恒安全）；
/// 手工篡改检查点行造成的洞位低于水位时超出契约——需完整重建请用
/// <c>ProjectionRebuilder.RebuildAsync</c>（先 Reset）。
/// </para>
/// <para><b>实现</b>：InMemory / Dapper / EF Core（ProjectionCheckpointDbContext）/
/// PalORM 四栈。自定义 <c>IProjectionCheckpointStore</c> 实现不标注本接口时，
/// Rebuilder 自动保持全量回放路径（非破坏，镜像 ISagaLeaseBatchStore /
/// IBatchMarkOutboxStore 能力接口先例）。</para>
/// </summary>
public interface IResumableProjectionCheckpointStore : IProjectionCheckpointStore
{
    /// <summary>获取增量回放的续读位置——语义见接口 remarks。</summary>
    ValueTask<string?> GetResumePositionAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default);
}

/// <summary>
/// R12 C-02：检查点租约续期能力（可选实现）。处理器在 handler 执行期间按
/// <c>processingTimeout / 3</c> 心跳续租，避免单个慢 handler 超过租约后被另一
/// worker 重租同一位置并发投影（重复副作用）。未实现本接口的自定义存储保持
/// 现有语义（无续租，依赖 handler 在 processingTimeout 内完成）。
/// </summary>
public interface IProjectionLeaseRenewalStore
{
    /// <summary>
    /// 续租一个仍由本 worker 持有的 Processing 检查点。<br/>
    /// 返回 true = 续租成功；false = 租约已丢失（被抢占/已完成），调用方应停止续租。
    /// 实现必须以 <c>Revision</c>（或等价所有权令牌）为守卫，且只更新租约/更新时间，
    /// 不推进 Revision（否则调用方持有的 checkpoint 令牌会失效）。
    /// </summary>
    ValueTask<bool> RenewLeaseAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        CancellationToken ct = default);
}
