# 性能优化点全量清单（源码深读 · 2026-09-20）

> 方法：对发布器/处理器/租约/序列化/EventLog/SagaProcessor 热路径逐文件 Read 全文，
> 按五维度（并发/延迟/内存/可靠性/事务完整可靠性）列全所有可行优化点。
> 标注：[并发] [延迟] [内存] [可靠性] [事务] + 严重性 + 工作量。**不评判已声明的取舍**（如 M1-1 噪音、InMemory 无淘汰——另册）。

---

## 一、并发维度（提升吞吐）

| # | 点 | 位置 | 现状 → 方案 | 影响 | 工作量 |
|---|---|---|---|---|---|
| C1 | **Outbox 发布器单线程逐条串行** | OutboxBatchProcessor.cs:94-190 | `foreach` 逐条 反序列化→Publish→Mark→Persist。broker publish 是网络 IO——改为有界 `Parallel.ForEachAsync`（maxDOP 可配，默认 4~8）或 `Channel` 流水线（反序列化 CPU 与 publish IO 重叠） | 吞吐 ~min(DOP, broker 并发能力) 倍提升；Mark* 已有租约/fencing 天然并发安全 | M（需保批内 `checked` 计数与 now 原子时间戳语义；PersistSingleAsync 的 SaveChanges 需每 worker 独立 scope 或加锁——EF 栈 DbContext 非线程安全，是主要改造点） |
| C2 | **SagaProcessor 逐 saga 串行补偿** | SagaProcessor.cs:136-245 | 超时补偿逐 saga 顺序执行；改为 `Parallel.ForEachAsync`（DOP 可配）——各 saga 状态独立、租约互斥，天然可并行 | 多实例死信积压消化的水平扩展 | M（需确认共享 `_store` 连接的线程安全约束——Dapper conn 非线程安全，需 per-worker session） |
| C3 | **OutboxProcessor 单 worker 轮询** | OutboxProcessor.cs:59-64 | 每 tick 单 scope 单批；多实例横向扩已有（租约互斥），单实例内可加 `MaxConcurrency` options（N 个并行 tick，各租各批不冲突——租约语义天然支持） | 单实例吞吐线性扩展（受 DB 连接池上限约束） | M |
| C4 | **PalORM Outbox 7 处 sync-over-async** | PalOrmOutboxStore.cs:160,196,203,239,244,307,310 | `GetAwaiter().GetResult()` 阻塞线程池线程（已声明+挂 B2） | 线程池饥饿风险 | 并入 B2（v3.0） |
| C5 | **EventLog 追加 N 事件 N 往返** | DapperEventLog.cs:112-175 | 循环内逐事件 `QuerySingleAsync<long>`；PG 可多行 `VALUES…RETURNING` 一趟取回（v2 P-3，维持） | 追加批量事件延迟 N→1 | L |

## 二、延迟维度（降低单次操作延迟）

| # | 点 | 位置 | 现状 → 方案 | 影响 | 工作量 |
|---|---|---|---|---|---|
| L1 | **SagaProcessor 每非超时 saga 2 次往返纯为清租约** | SagaProcessor.cs:238 + DapperSagaStateStore.cs:16-17 | 逐 saga SaveChanges；改单语句租约释放（`UPDATE … WHERE version=@v` RETURNING），或按批收集后一次批量 UPDATE | 每 tick 往返 N→1；30s 轮询 × N saga 的固定税 | M（三栈 SQL 各一条，形态已有 Saga 租约获取先例） |
| L2 | **Outbox EF 栈逐条 PersistSingleAsync** | OutboxBatchProcessor.cs:211-215 | 每消息一次 SaveChanges（v2 P-1）；改为批末 flush（EF 栈一次 SaveChanges 持久化本批全部 Mark*） | 批大小 N 时 EF 往返 N→1；批失败影响面=整批（与 Dapper/PalORM 栈现状一致） | S（契约已允许：前三栈返回 0） |
| L3 | **DapperSagaStateStore 「先查询后决定」UPSERT** | DapperSagaStateStore.cs:16-17,169-172 | 每次 SaveChangesAsync = SELECT + INSERT/UPDATE 两往返；SQLite/PG 可 `INSERT … ON CONFLICT … DO UPDATE RETURNING`（PalORM Inbox 已用此形态），MySQL 用 `INSERT…ON DUPLICATE KEY UPDATE` | Saga 高频保存路径往返 ×0.5 | M（三方言三形态 + 回读语义对齐） |
| L4 | **MessageCatalog.Find 已 O(1)——无需优化** | MessageCatalog.cs:80-100 | FrozenDictionary 直查 [事实] | — | 不做 |
| L5 | **Kafka/Rabbit 发布无框架级超时** | KafkaBroker.cs:73-80、RabbitMqBroker.cs:173-179 | 半坏 broker 下发布时长悬置于客户端配置（v2 A 片 P3-4） | 类 remarks 补配置指引；或框架级 CTS 包装（可选超时 options） | S（指引）/M（包装） |

## 三、内存维度（降低分配/驻留）

| # | 点 | 位置 | 现状 → 方案 | 影响 | 工作量 |
|---|---|---|---|---|---|
| M1 | **PalOrmOutboxStore.Materialize 列清单逐字符构建 reader** | PalOrmOutboxStore.cs:108/115（14 列 name-value 对） | 每行 14 次 `reader.GetXxx` 序列调用属 PalORM 引擎面——记录为引擎边界，不适配层修 | — | 不做（上游） |
| M2 | **FanOut semaphore 每 per-call 分配** | FanOutStep.cs:148 | 每次执行 `new SemaphoreSlim(MaxConcurrency)`；若同一 Saga 实例高频执行可缓存为实例字段（`MaxConcurrency` init 后不可变，构造期建一次） | 单次分配减少（ns 级）；仅高频场景有感 | S |
| M3 | **BenchEvent.Instance 模式可推广**（新增基准验证的零分配事件单例） | bench SagaLaneBenchmarks.cs | 生产侧事件不可单例（携带数据），此点无生产化空间 | — | 不做 |
| M4 | **DIspatcher 空 FrozenDictionary 每次派发分配** | Dispatcher.cs:75（v91 片4 N4 已录） | 空表缓存为 `FrozenDictionary<string, …>.Empty` 等价形态 | 启动窗口微点 | S |
| M5 | **InMemory 三存储无淘汰** | InMemoryOutboxStore.cs:27 等 | 已声明仅限测试/文档 fixture（v2 P-4）——维持「文档标注」处置 | — | 已处置 |

## 四、可靠性维度

| # | 点 | 位置 | 现状 → 方案 | 影响 | 工作量 |
|---|---|---|---|---|---|
| R1 | **Outbox 死信静默停止投递** | OutboxBatchProcessor.cs:154-171 | 死信只落库+计数，无日志 Error 级提示、无 metric 区分 dead vs retried（合并入 OutboxFailed） | 死信积压不可见（用户需主动查表）；`PalMetrics.OutboxDead.Add(dead)` 独立计数 + 首 dead Warning | S |
| R2 | **PersistSingleAsync 失败仅 Warning** | OutboxBatchProcessor.cs:211-222 | 持久化失败 → 下轮重试（at-least-once 兜底）但 `processed++` 已计入指标——指标宣称「已处理」而 DB 状态未变 | 指标与 DB 真相漂移；改：失败时从 processed 退回并计入新计数 `pal.outbox.persist_failed` | S |
| R3 | **Inbox EF 抢占成功后 handler 执行前崩溃 → 等 LeaseDuration** | InboxDbContext 抢占→handler 间隙 | 抢占成功即 Processing；worker 崩溃后该消息要等超时回收——租约时长即最坏重复延迟 | 已是 at-least-once 设计语义；可选缩短 processingTimeout 或文档声明（usage.md Inbox 段补一句） | S（文档） |
| R4 | **PostgreSqlOutboxNotifier 裸 catch 无日志** | PostgreSqlOutboxNotifier.cs:265-268 | 自唤醒失败零可见（v2 P3 已录） | 补 Warning | S |
| R5 | **IdempotencyProcessor 毒载荷降级零日志** | IdempotencyProcessor.cs:219-225 | private static 无法记日志（结构性） | static→实例注入 logger | S |
| R6 | **Outbox notifier 1s 固定重连无退避计数** | PostgreSqlOutboxNotifier.cs:180-189 | 内部超时反复时 1 次/秒重连风暴 | 指数退避 + 计数上限 Warning | S |

## 五、事务完整可靠性维度

| # | 点 | 位置 | 现状 → 方案 | 影响 | 工作量 |
|---|---|---|---|---|---|
| TX1 | **Outbox 业务事务→消息行写入的原子性依赖使用方** | 仓外（业务 DbContext 同事务写 outbox 行） | 框架侧已保证租约/fencing/重投；业务侧「写业务表+写消息行必须同事务」是 Outbox 模式的**使用前提**，框架无法代管 | 文档声明（usage.md Outbox 段补「必须与业务写入同事务/同 UoW」⚠️ 段） | S（文档） |
| TX2 | **MarkProcessed 与 broker Publish 非同事务（本质限制）** | OutboxBatchProcessor.cs:123-126 | Publish 成功但 Mark 失败（崩溃）→ 消息重发；Publish 失败但 Mark 成功不可能发生（顺序保证）——at-least-once 语义的固有窗口 | 已有正确方向（Mark 在 Publish 后）；无动作，文档声明「handler 必须幂等」 | 已声明 |
| TX3 | **Saga 补偿自身失败的兜底面** | Saga 骨架 + SagaCompensation.RunAsync | 补偿失败已并入 AggregateException（可见）+ SagaTimeoutProcessor 兜底扫描（滞留补偿）；链路完整 | — | 无 |
| TX4 | **OutboxBatchProcessor 与 SagaProcessor 无事务边界的 store 混用** | OutboxBatchProcessor.PersistSingleAsync | batch 内多条 Mark* 共享同一 scope 的 SaveChanges——前一条失败 Warning 后继续下一条，但 EF 栈 ChangeTracker 中前条变更仍在（下条 SaveChanges 会连带提交前条！幽灵提交窗口）| **验证项**：EF 栈 PersistSingle 失败后，同批下一条的 SaveChanges 是否连带提交前条未落库变更——若是，前条 Mark 未丢（反而被补交），行为正确性待测 | S（先写并发测试验证） |
| TX5 | **SagaProcessor 取消路径 CancellationToken.None 的写穿透** | SagaProcessor.cs:171,215,238 | 关停时不响应取消的保存——保证租约/补偿不悬置，正确且已声明 | 无 | 已声明 |

---

## 优先级汇总（工作量 × 影响排序）

| 优先 | 点 | 维度 | 理由 |
|---|---|---|---|
| ① | L2 批末 flush（S） | 延迟 | EF 栈 N→1 往返，契约已允许，最快见效 |
| ② | R1 死信独立指标（S） | 可靠性 | 死信积压可见性缺失是运维盲区 |
| ③ | TX4 幽灵提交验证（S） | 事务 | 先验证再定修不修 |
| ④ | R2 指标漂移修正（S） | 可靠性 | 指标可信度 |
| ⑤ | C1 Outbox 并行发布（M） | 并发 | 最大吞吐杠杆，但 EF DbContext 线程安全是硬改造点 |
| ⑥ | L1/L3 Saga 租约与 UPSERT 单语句（M） | 延迟 | 往返税削减 |
| ⑦ | C2 Saga 补偿并行（M） | 并发 | 死信积压消化 |
| ⑧ | M1-4/M3-2/R4/R5/R6（S 批） | 混合 | 触碰时顺带 |

**不做清单**：M1 PalORM reader（上游）、C4（=B2 v3.0）、L4（已 O(1)）、M3（无生产空间）、TX2/TX5（已声明语义）、M5（已处置）。

---

## 实施建议

- 快速获胜批（①②④⑧，合计 <1 天）：三个 S 级可靠性修正 + 一个验证测试
- 结构改造批（⑤⑥⑦）：C1 是核心，需先出 DbContext 并发安全的 scope 设计（per-worker scope 或 store 加锁），连带 C2/C3 复用同一设计
- 全部实施后跑 `--persist` 全量 + `--saga` 基准做 before/after
