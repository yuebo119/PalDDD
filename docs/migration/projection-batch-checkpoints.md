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

## 正式基准（2026-10-19）

量具：`--m2-probe`（`bench/PalDDD.Benchmarks/ProjectionProbe.cs`）。100,000 条事件经
`GeneratorSource` 回放，`CountingStore` 包在 `InMemoryProjectionCheckpointStore` 外，
统计 `TryStartAsync` 与 `MarkCompletedAsync` 的调用次数（每次调用对应真实 store 的
至少一次数据库往返）；计时为进程内回放墙钟，不含数据库 I/O。

| CheckpointBatchSize | TryStart + MarkCompleted | 每事件调用数 | 回放耗时 |
|---|---|---|---|
| 1（旧行为） | 200,000 | 2.00 | 120ms |
| 100 | 2,000 | 0.02 | 31ms |

减少 100×，达到「100k 事件回放命令数不超过 2k」的验收线；探针把该阈值写成硬断言
（batch=100 时两类调用各不得超过 1,000，超出即非零退出）。复现：

```bash
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m2-probe
# M2 PASS: events=100000, batch=1 calls=200000 (120ms), batch=100 calls=2000 (31ms), reduction=100x
```

口径说明：数字是检查点调用次数，不是数据库延迟。真实 Dapper/EF/PalORM store 的每次
检查点调用至少含一次数据库往返（部分实现可能更多），故调用数下降是往返数下降的下界
证据，不是逐次往返的精确换算。耗时只用于确认量级，不构成真库性能承诺。
