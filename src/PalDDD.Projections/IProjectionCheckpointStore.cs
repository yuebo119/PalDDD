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
