namespace PalDDD.Projections;

/// <summary>
/// 增量回放结果。
/// </summary>
/// <param name="Processed">本次实际处理的投影条数（与 <see cref="ProjectionRebuilder{TMessage}.ReplayAsync"/> 返回值一致）。</param>
/// <param name="StoppedForLease">
/// true 表示批模式在组首/组末遇到其他 worker 持有的租约而提前结束——该组会在下一次
/// 回放中重试，调用方应尽快再次调用；false 表示已追平（或逐条模式无组租约语义）。
/// </param>
public readonly record struct ProjectionReplayResult(int Processed, bool StoppedForLease);
