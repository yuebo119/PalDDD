# 投影批量检查点（M2）

> 日期：2026-10-19
> 关联：`docs/review/perf-migration-plan-2026-10-19.md` M2

## 变更

`ProjectionProcessor<TMessage>.CheckpointBatchSize` 默认 **1000**（回放按 1000 条一组提交）；
在线逐消息 `ProcessAsync` 不受影响。设为 1 恢复逐事件检查点。
> 2026-10-20 调整：原默认 1，回放每事件两次检查点写；改为默认 100 以消除回放主瓶颈。
> 需要旧行为时显式设 1（迁移测试仍以 1 覆盖逐事件语义）。
> 2026-10-22 调整：默认 100 → 1000。回放吞吐上限 = 批宽 ÷（每批 4 次检查点往返 × RTT）：
> 批宽 100 时 10ms RTT 下每事件约 600μs 纯往返开销，批宽 1000 摊到约 60μs。崩溃重放窗口
> 同步变为 1000 条（handler 幂等是既有契约）；组内时长由租约心跳（1/3 租约周期续期）兜住，
> 不随批宽放大。下文 2026-10-19 的 100k 事件基准仍按批宽 100 计量（历史口径，未重测）。

设置为 N>1 后，`ProjectionRebuilder` 按 N 条一组调用 `ProcessBatchAsync`：组内只对
**最后位置**做一次 `TryStart`/`MarkCompleted`，组内事件直接调用 handler。

`ProjectionRebuilder.CheckpointFlushInterval` 默认 1 秒：即使未攒满 N 条，距组首满该间隔
也提交一次，避免慢 handler 让单组拖过 processingTimeout。时间界时钟可经
`ProjectionRebuilder.TimeProvider` 注入，测试用 FakeTimeProvider 驱动。

无 schema 变更：复用现有 `projection_checkpoints` 表；组内中间位置不写行，续读仍由
`GetResumePositionAsync` 的 `MAX(Completed position)` 给出下一位置。

## 语义与边界

- 成功：最后位置 Completed；续读从下一事件开始，整组跳过。
- handler 失败：最后位置标 Failed，并把组首位置标 Failed；续读从组首开始重放整组，
  不会从组末之后开始而跳过中间事件。
- 进程崩溃：最后位置处于 Processing；租约到期前其他 worker 无法接管该组，到期后
  从上一个已完成位置重放，最多 N 条。投影 handler 必须幂等（既有 at-least-once 契约）。
- 组内处理时长必须小于 `processingTimeout`（默认 5 分钟），否则租约可能过期被并行接管。
  默认 1 秒时间界把单组处理时长限制在约 1 秒加最后一项 handler 时长；慢 handler 仍应
  使用较小的 N 或增大 processingTimeout。
- 部分写入副作用在失败后会重放整组，按 at-least-once 处理，不做 exactly-once 承诺。
- 回滚：`CheckpointBatchSize = 1` 恢复逐事件路径；时间界只影响回放，不影响在线处理。

## 用法

```csharp
var processor = new ProjectionProcessor<OrderEvent>(handler, checkpointStore); // 默认 1000
var rebuilder = new ProjectionRebuilder<OrderEvent>(
    handler.ProjectionName, "orders", replaySource, checkpointStore, processor)
{
    CheckpointFlushInterval = TimeSpan.FromSeconds(1), // 默认值，慢回放的提交上界
};
await rebuilder.ReplayAsync(ct);
```

需要逐事件检查点时：

```csharp
var processor = new ProjectionProcessor<OrderEvent>(handler, checkpointStore)
{
    CheckpointBatchSize = 1,
};
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

> 2026-10-22 勘正：上表是 2026-10-19 的时点数据（F7 之前，每组只写组末）。F7 把组首锚点
> 前移到 handler 之前后，每组变为 2 次 TryStart + 2 次 MarkCompleted，batch=100 的结构真值
> 是 4,000 次；默认批宽改 1000 后为 400 次。现行读数见 `docs/performance.md`
> 「实测：回放批宽」，`--m2-probe` 已扩为 1/100/1000 三档并加了结构性断言。

减少 100×，达到「100k 事件回放命令数不超过 2k」的验收线；探针把该阈值写成硬断言
（batch=100 时两类调用各不得超过 1,000，超出即非零退出）。复现：

```bash
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m2-probe
# M2 PASS: events=100000, batch=1 calls=200000 (120ms), batch=100 calls=2000 (31ms), reduction=100x
```

口径说明：数字是检查点调用次数，不是数据库延迟。真实 Dapper/EF/PalORM store 的每次
检查点调用至少含一次数据库往返（部分实现可能更多），故调用数下降是往返数下降的下界
证据，不是逐次往返的精确换算。耗时只用于确认量级，不构成真库性能承诺。
