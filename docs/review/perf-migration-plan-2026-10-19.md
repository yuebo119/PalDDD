# 性能迁移立项清单（ADR-033 开放决策点）

> 日期：2026-10-19
> 状态：立项草案，逐项独立 ADR/迁移脚本/真库验证后实施
> 关联：ADR-033（Outbox 自动投递）、ADR-027（批量标记）、`docs/performance.md`「ADR-033 Auto 组合 A/B」

## 排序原则

四项都涉及 schema 或 broker 拓扑，不能与普通代码优化同批提交。统一采用 expand/contract：
先加可空列或新表并双写，再回填，再切读，最后在下一个发布窗口收缩。每项必须有独立回滚路径
和真库车道（PG/MySQL/SQLite，必要时 SQL Server）验证，不以单机 SQLite 结果外推。

顺序按「可回滚性 × 收益 × 风险」：Saga 到期索引（最小 schema 改动）→ 投影高水位（新表/契约）
→ RabbitMQ 通道拓扑（无 schema，但需要真 broker 压测）→ EventId 二进制列（最大迁移面，最后做）。

## M1 Saga 到期索引

现状：`SagaProcessor.CheckTimeoutsAsync` 每 30 秒租出最多 256 条活跃 Saga，全部做
`IsTimedOut` 检查后释放。10k 活跃时全量一轮约 20 分钟，尾部超时无上界；每轮租约与释放
合计约 2×活跃数写。

目标：`SagaState` 增加 `NextCheckAt`（当前状态各步骤 `StepStartedAt + Timeout` 的最小值），
索引 `(Status, NextCheckAt)`；租约只取到期行，满批续扫，非到期行不写。

阶段：
1. DDL：加可空 `next_check_at`；PG `CREATE INDEX CONCURRENTLY`，MySQL online DDL，SQLite
   `ALTER TABLE ADD COLUMN` + 普通索引；先不建唯一约束。
2. 双写：`SaveChangesAsync` 与租约释放路径写入 `NextCheckAt`；旧行保持 NULL。
3. 回填：按主键分批计算并写入，批间限流；回填完成前租约查询保持旧路径。
4. 切读：新路径用 `WHERE Status IN (...) AND NextCheckAt <= @now ORDER BY NextCheckAt`
   租约；特性开关回退旧全量扫描。
5. 收缩：一个发布窗口后删除旧扫描分支与 NULL 回填逻辑；列改 NOT NULL（如需）。

验收：10k 活跃夹具下，单 tick 写量不超过批量；尾 Saga 超时不超过 2 个轮询周期；三方言
租约语句数与旧路径对照；回填中断可从断点续跑。风险：回填长事务、索引膨胀、跨节点时钟偏差
（`NextCheckAt` 用 DB 时间还是应用时间必须单点定义）。

实施状态（2026-10-19）：expand 已落地。`SagaState.NextCheckAt`、`SagaTimeoutDetector.ComputeNextCheckAt`、
四栈 `IDueTimeSagaStore`（EF/Dapper/PalORM/InMemory）与 `SagaProcessorOptions.EnableDueTimeFilter`
（默认 false）已提交；DDL 见 `docs/migration/saga-next-check-at.md`。切读为显式开关，
回填走旧全量路径的惰性写入；收缩（删除旧扫描分支）留到下一个发布窗口。
真库演练（`--m1-probe-pg` / `--m1-probe-mysql`，2026-10-19）：旧 schema 上 ALTER 加列/索引后，
两方言均 `due=1, active-non-due=2, index=idx_saga_due`。
10k 规模演练（`--m1-load-pg` / `--m1-load-mysql`，2026-10-20）：10,000 条 Active Saga、
`NextCheckAt` 在 100s 内均匀到期、2s 轮询/256 批量。两方言均通过：PG p99 1996ms/max 2101ms，
MySQL p99 2003ms/max 2034ms，阈值 2 个轮询周期 4000ms；maxPerTick 202 ≤ 256。

## M2 投影检查点高水位

现状：`ProjectionProcessor` 每事件 `TryStart` + `MarkCompleted` 两次 DB 往返，检查点表按事件
增长。1M 事件回放约 2M 次往返；仓库已有「远程重放 21 分钟 → 1.3 秒」的窗口化分析，但因
崩溃重放面 1→N 与无消费方而未实施。

目标：新增可选 `IBatchedProjectionCheckpointStore`，按区间 `[p, p+K)` 或高水位提交；
`ProjectionOptions.CheckpointBatchSize` 默认 1，保持旧路径；K=100 或每 1 秒提交一次。
崩溃后最多重放 K 条，handler 必须幂等（现有契约已要求）。

阶段：
1. 能力接口 + 默认回退（旧 store 零迁移）。
2. 新表/列：`projection_checkpoints` 增加 `HighWaterPosition` 与区间租约列；旧逐位置行保留。
3. 双写：rebuilder 在 opt-in 时同时写逐位置与高水位；校验两者一致。
4. 切读：rebuilder 优先高水位；特性开关回退逐位置。
5. 收缩：确认消费方全部 opt-in 后，停止逐位置写；旧表在下一 major 删除。

验收：100k 事件回放命令数不超过 2k；崩溃恢复重放不超过 K；多投影并发不互相覆盖；
SQLite/PG/MySQL 三方言真库。风险：区间租约与现有 fencing 语义、长 handler 的租约续期、
部分提交的边界。

实施状态（2026-10-19）：采用“组末位置检查点”变体，无 schema 变更。`ProjectionProcessor.CheckpointBatchSize`
（默认 1）>1 时按组调用 `ProcessBatchAsync`：组内只在最后位置 TryStart/MarkCompleted；
失败时同时把组首标 Failed，保证续读从组首重放而不是组末之后；崩溃后最多重放 N 条。
`docs/migration/projection-batch-checkpoints.md` 记录语义与验收。原计划的
`IBatchedProjectionCheckpointStore`/高水位列未建，因为组末位置已提供等价的续读水位；
若未来需要“组内部分提交”或按时间冲刷，再按能力接口扩展。
正式基准（`--m2-probe`，2026-10-19）：100,000 条事件回放，检查点调用
200,000 → 2,000（100×），进程内耗时 120ms → 31ms；验收线 ≤2k 由探针硬断言。

## M3 RabbitMQ 通道拓扑

现状：一个 `IChannel` 同时承担发布、publisher confirms、全部订阅的 QoS/consume/ack。
混合读写下 channel 级串行是吞吐上限；prefetch 也是 channel 级。

目标：保留单 channel 构造兼容；新增接收 `IConnection` + channel 工厂的构造。发布用独立
confirm channel，每订阅独立 consumer channel；共享 `IConnection`。prefetch 按订阅配置。

阶段：
1. 构造重载与生命周期（channel 由订阅句柄释放）。
2. 集成测试：多订阅、发布/消费混合、慢消费者、取消/重连、publisher confirm 路由失败。
3. 压测：1/4/8 订阅下的 p50/p99 与 ack 帧数；对照单 channel。
4. 文档与模板切换；单 channel 保留为兼容模式。

验收：混合负载下 8 订阅无跨订阅阻塞；慢消费者不影响发布确认；取消后无 channel 泄漏。
风险：RabbitMQ.Client 7.x 的 channel 线程模型、confirm 窗口、匿名队列生命周期。

实施状态（2026-10-19）：默认每订阅独立 consumer channel（`IConnection.CreateChannelAsync`），
发布 channel 只承担发布/confirm；`UseSharedConsumerChannel=true` 复现单 channel 旧行为。
订阅句柄释放时 cancel → dispose consumer channel → dispose linked CTS；失败路径同样释放。
本地 RabbitMQ（wslc rabbit436，palddd-test）Messaging.Integration 18/18 全绿。
`docs/migration/rabbitmq-channel-topology.md` 记录回滚与验收。
压测（`--m3-probe`，2026-10-19）：1/4/8 订阅两场景。baseline 下 per-sub 相对 shared 的
p99 为 1.05~1.19×（绝对差 ≤0.16ms）、吞吐 0.86~0.99×；isolation 场景（一个订阅每次
延迟 2ms）shared 把其余订阅 p99 拖到 30.8s/30.4s，per-sub 为 0.70ms/1.22ms
（低约 2.5~4.4 万倍）；发布确认两拓扑均不受慢订阅阻塞（publish 0.7~1.6s）。
默认维持每订阅 channel。

## M4 EventId 二进制列

现状：`event_id` 以 26 字符文本存储并建唯一索引；每行写入 `ToString`、读取 `Parse`，
索引键宽约为 16 字节二进制的 1.6 倍。

目标：增加 16 字节二进制列（PG `uuid` 或 `bytea(16)`，MySQL `binary(16)`，SQLite `blob`），
双写后切换读取；文本列保留一个 major 作为兼容/诊断面。

阶段：
1. 加列与索引（先不唯一），双写。
2. 分批回填；校验行数、抽样比对文本解析结果。
3. 读路径切二进制；唯一冲突分类仍以二进制索引为准。
4. 收缩：旧文本列改可空、停止写；下一个 major 删除。

验收：索引体积下降（实测 10 万行 PG 59.2%、MySQL 22.1%；原「约 2/3」按方言修正）；
回放每事件少一次 Parse/ToString；唯一约束冲突仍能定位；
四方言（含 SQL Server 实验性路径）DDL 可回滚。风险：列迁移时间、索引重建锁、旧客户端
读取兼容、Ulid 字节序与文本序不一致（排序语义必须逐查询确认）。

实施状态（2026-10-19）：expand 已落地。`event_id_bin`（16 字节）列与普通索引已加入
PG/MySQL/SQLite DDL；EF/Dapper/PalORM 三栈追加时双写，读取默认文本、`UseBinaryEventId=true`
切二进制（NULL 行回退文本）。文本唯一索引保留，冲突分类仍走文本索引；二进制唯一索引
待应用侧分批回填后创建。回填配方/切读/回滚/验收见 `docs/migration/event-id-binary-column.md`。
收缩（停写文本列、删旧索引）留到下一 major。
真库演练（`--m4-probe-pg` / `--m4-probe-mysql`，2026-10-19）：两方言 500 行旧数据回填、
10 条抽样、二进制读回 10 条全过（PG 1095ms、MySQL 2825ms）。演练抓出 PG 上
`SequentialAccess` 的物理列序缺陷，`DapperEventLog` 已改 `SingleResult`（仍逐行流式）。
10 万行规模演练（`--m4-scale-pg` / `--m4-scale-mysql`，2026-10-20）：两方言回填
100,000/100,000 行；PG 回填 41,031ms、索引 42ms、体积下降 59.2%；MySQL 回填 31,956ms、
索引 398ms、体积下降 22.1%。回填吞吐约 2.4k~3.1k 行/s。原「1M 行下降约 2/3」不成立于
MySQL，已按方言修正。

## 非迁移类后续（不占本轮迁移窗口）

- 标记 flusher 与发布循环完全解耦：需组合 A/B 证明标记段仍占排空 >5%。
- Kafka 管道化发布（Produce + delivery callback）：需真实 broker 与 `enable.idempotence` 前提。
- 这两项不涉及 DDL，可在 M1/M2 之间插入，但仍需独立提交与真库/真 broker 证据。

## 立项产物清单

每项实施前补齐：独立 ADR（含被否方案与复活触发器）、expand/contract 迁移脚本与回滚脚本、
三方言真库测试、`docs/performance.md` 前后 A/B、`CHANGELOG` 消费者可见变更、
`perf-budgets.md` 结构指标（每消息/每事件往返数、索引大小、崩溃重放上界）。
