# 投影批量检查点（M2）

> 日期：2026-10-19
> 关联：`docs/review/perf-migration-plan-2026-10-19.md` M2

## 变更

`ProjectionProcessor<TMessage>.CheckpointBatchSize` 默认 1（逐事件检查点，旧行为）。
设置为 N>1 后，`ProjectionRebuilder` 按 N 条一组调用 `ProcessBatchAsync`：组内只对
**最后位置**做一次 `TryStart`/`MarkCompleted`，组内事件直接调用 handler。

无 schema 变更：复用现有 `projection_checkpoints` 表；组内中间位置不写行，续读仍由
`GetResumePositionAsync` 的 `MAX(Completed position)` 给出下一位置。

## 语义与边界

- 成功：最后位置 Completed；续读从下一事件开始，整组跳过。
- handler 失败：最后位置标 Failed，并把组首位置标 Failed；续读从组首开始重放整组，
  不会从组末之后开始而跳过中间事件。
- 进程崩溃：最后位置处于 Processing；租约到期前其他 worker 无法接管该组，到期后
  从上一个已完成位置重放，最多 N 条。投影 handler 必须幂等（既有 at-least-once 契约）。
- 组内处理时长必须小于 `processingTimeout`（默认 5 分钟），否则租约可能过期被并行接管。
  慢 handler 应使用较小的 N 或增大 processingTimeout。
- 部分写入副作用在失败后会重放整组，按 at-least-once 处理，不做 exactly-once 承诺。

## 用法

```csharp
var processor = new ProjectionProcessor<OrderEvent>(handler, checkpointStore)
{
    CheckpointBatchSize = 100,
};
var rebuilder = new ProjectionRebuilder<OrderEvent>(
    handler.ProjectionName, "orders", replaySource, checkpointStore, processor);
await rebuilder.ReplayAsync(ct);
```

## 验收

- 100k 事件、N=100：检查点写次数从约 2×100k 降为约 2×1k（TryStart + MarkCompleted 每组各一次）。
- 组内失败在位置 p：`GetResumePositionAsync` 返回组首，而不是组末之后。
- 崩溃恢复：从上一个已完成位置重放，重放条数 ≤ N。
- N=1 时行为与旧路径逐字一致（回归测试全绿）。
