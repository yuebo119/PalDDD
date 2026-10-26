# ADR 034：性能迁移四项（Saga 到期索引、投影批量检查点、Rabbit 通道、EventId 二进制）

> 状态：**expand 已实施（2026-10-19）**；contract（删旧列/旧扫描分支/旧索引）留到下一发布窗口
> 日期：2026-10-19
> 关联：ADR-033（Outbox 自动投递）、`docs/review/perf-migration-plan-2026-10-19.md`、
> 各迁移文档 `docs/migration/*.md`

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-10-19 |
| 评审状态 | **已采纳并实施 expand 阶段**：四项均为可回滚的加法变更，旧路径保留为默认或回退开关 |
| 兼容性 | M1/M4 为新增列/索引；M2 为默认 1 的新选项；M3 为可 `UseSharedConsumerChannel=true` 回退的默认拓扑 |
| 收缩条件 | 回填完成、真库/真 broker 验收、下一 major 窗口 |

## 决策

**D1 Saga 到期索引（M1）**：`SagaState.NextCheckAt` 由 `Saga<TState>` 在步骤开始/完成时维护；
`SagaProcessorOptions.EnableDueTimeFilter` 默认 false，开启后四栈 `IDueTimeSagaStore` 只租
到期行。`saga_states` 加 `next_check_at` 与 `(status, next_check_at)` 索引。回填走旧全量
路径的惰性写入；收缩时删除旧扫描分支。详见 `docs/migration/saga-next-check-at.md`。

**D2 投影批量检查点（M2）**：`ProjectionProcessor.CheckpointBatchSize` 默认 1；N>1 时按组
处理，只在组末位置 TryStart/MarkCompleted，失败时同时标组首 Failed，崩溃最多重放 N 条。
不建新表/高水位列；组末位置已提供等价续读水位。详见
`docs/migration/projection-batch-checkpoints.md`。

**D3 RabbitMQ 通道拓扑（M3）**：发布 channel 只承担发布/confirm；每订阅经
`IConnection.CreateChannelAsync` 创建独立 consumer channel；订阅句柄负责 cancel 与 channel
释放。`UseSharedConsumerChannel=true` 回退单 channel。详见
`docs/migration/rabbitmq-channel-topology.md`。

**D4 EventId 二进制列（M4）**：`events` 加可空 `event_id_bin`（16 字节）与普通索引；
EF/Dapper/PalORM 双写；读取默认文本，`UseBinaryEventId=true` 切二进制，NULL 行回退文本。
文本唯一索引保留，冲突分类不变；应用侧分批回填后创建二进制唯一索引。收缩在下一 major
停止写文本列。详见 `docs/migration/event-id-binary-column.md`。

## 证据锚点表

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| M1 四栈到期租约与惰性回填 | src/PalDDD.Transactions/Saga/*、src/PalDDD.Transactions.EFCore/SagaStateDbContext.cs、Dapper/PalORM 对应 Store | ☑ |
| M2 组末水位与失败组首标记 | src/PalDDD.Projections/ProjectionProcessor.cs `ProcessBatchAsync`、ProjectionRebuilder.cs | ☑ |
| M3 每订阅 channel 与释放顺序 | src/PalDDD.Messaging.RabbitMQ/RabbitMqBroker.cs | ☑ |
| M4 三栈双写与读开关 | src/PalDDD.EventLog.EFCore/StoredEvent.cs、DapperEventLog.cs、PalOrmEventLog.cs | ☑ |
| 验证面板 | Transactions 234、PalORM 114（54 跳过）、Integration 413（14 跳过）、Messaging.Integration 18/18、EventLog 44 | ☑ |
| M1/M4 真库探针 | bench/PalDDD.Benchmarks/MigrationProbes.cs；PG/MySQL M1 due=1/active=2；M4 500/500 回填、sample=10、read=10 | ☑ |
| M2/M3 量具 | bench/PalDDD.Benchmarks/ProjectionProbe.cs、RabbitChannelProbe.cs；M2 100k 检查点调用 200,000→2,000；M3 isolation 快订阅 p99 30.8s→0.70ms | ☑ |
| M1/M4 规模演练 | 2026-10-20：M1 10k 活跃 Saga（PG/MySQL）p99 2.0s、max 2.1s，阈值 4s；M4 100k 行回填 PG 41.0s / MySQL 32.0s，索引体积下降 PG 59.2% / MySQL 22.1% | ☑ |
| 迁移文档 | docs/migration/saga-next-check-at.md、projection-batch-checkpoints.md、rabbitmq-channel-topology.md、event-id-binary-column.md | ☑ |

## 开放决策点

| # | 决策点 | 状态 | 触发条件 |
|---|--------|------|----------|
| 1 | M1 切换 `EnableDueTimeFilter=true` | 已实施（2026-10-21 默认 true，见实施附记） | 存量活跃行回填完成、10k 活跃压测尾部超时 ≤ 2 个轮询周期 |
| 2 | M1 收缩旧全量扫描分支 | 未实施 | 下一发布窗口 |
| 3 | M2 组内部分提交/时间界冲刷 | 未实施 | 组内处理时长接近 processingTimeout，或消费方要求更细的续读粒度 |
| 4 | M3 1/4/8 订阅压测与单 channel 对照 | 完成（2026-10-19 本机真 broker 两场景） | 拓扑/客户端版本变更，或生产消费者延迟分布显著变化时复测 |
| 5 | M4 应用侧回填与二进制唯一索引 | 演练通过（PG/MySQL，100k 行；体积下降 PG 59.2% / MySQL 22.1%）；生产库待执行 | 生产库迁移窗口 |
| 6 | M4 收缩停写文本列 | 未实施 | 回填完成 + 一个 major 兼容期 |

## 实施附记（2026-10-21）

开放决策点 1 落地：`EnableDueTimeFilter` 默认改为 `true`。前置条件由「先完成活跃行回填」
改为「存量行按到期处理 + 运行时懒回填」——活动 Saga 的 `NextCheckAt` 无待检查超时时写
哨兵 `SagaState.NoPendingTimeout`（9999-12-31 UTC，MySQL `DATETIME(6)` 可存；
`DateTimeOffset.MaxValue` 的小数秒与偏移会越界），四栈到期谓词改为
`next_check_at IS NULL OR next_check_at <= now`（NULL = 迁移前存量行），首次扫描后
`SagaProcessor` 回填真实时刻或哨兵。旧全量扫描分支（决策点 2）保留为
`EnableDueTimeFilter=false` 回退通道，收缩仍留到下一发布窗口。
