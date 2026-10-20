# 2026-10-21 增量审计完整修复

## 1. 范围

本轮按增量审计落地未被近期实现覆盖的性能/可靠性项，不重复 Outbox 可用时刻折叠、EF EventLog 二进制投影、Saga 批量保存等已完成项。未发现新的 P0。

## 2. 已落地

### 2.1 EF Outbox 扫描回归

- `OutboxDbContext` 从 `SavingChanges + ChangeTracker.Entries<OutboxMessage>()` 改为
  `ChangeTracker.Tracked` 逐实体事件；新增 Pending 行才归一 `NextAttemptAt`。
- 消除了每次 `SaveChanges` 触发全量 `DetectChanges` 的回归，业务跟踪实体数不再进入成本。

### 2.2 断线续跑批次分配与 flush

- `StreamRunRegistry.StreamToAsync` 的批次 `List` 提升到循环外复用，live 每 token 不再新建 List。
- 回放大批次按 64 项或 16KB 未 flush 字节分段 flush；live `take=1` 仍在批末 flush，保持实时语义。

### 2.3 PalORM EventLog RETURNING 分块

- PG/SQLite RETURNING 批量按参数上限分块（PG 4095 行、SQLite 2047 行，16 参/行）。
- 无 ambient 事务时自建本地事务，多个块全成全败；有 ambient 时由调用方回滚。
- 新增 SQLite 2500 事件跨块测试；Dapper 同语义 4500 行探针保持。

### 2.4 MySQL 批量 17 行悬崖

- `DapperBulkCopy` 在 `AllowLoadLocalInfile=false` 且超过 16 行时，回退参数化多行 VALUES，
  按 60,000 参数预算分块并在本地事务内执行，不再直接抛异常。
- Dapper AOT 探针新增 20 行批量断言；MySQL wslc 33/33 通过。

### 2.5 Dapper/PalORM 二进制读投影

- Dapper 增加文本/二进制两套列清单，二进制模式不 SELECT `event_id`；NULL bin 直接抛错。
- PalORM 新增内部 `EventLogBinaryRow`，二进制模式不映射/解析文本 ULID；NULL bin 直接抛错。
- 保持 `QueryAsyncEnumerable` 与读副本路由，不改 raw reader。
- SQLite 二进制读回、NULL 严格失败测试通过。

### 2.6 InMemory 复杂度

- Outbox `MarkProcessedBatchAsync` 改为一次引用集合 + 单次 `RemoveAll`，从 O(m·n) 降到 O(n+m)。
- Saga 租约用反向 `PriorityQueue` 保留前 N 小，避免对全部活跃项全排序。

### 2.7 消息头与日志

- RabbitMQ `FromHeaders` 只立即解码四个追踪键，`Headers` 读取时才物化全量字典。
- Kafka 消费同样只立即解码已知追踪键，完整 Headers 按需物化；移除逐消息 eager 字典。
- Kafka librdkafka 日志按 `IsEnabled` 门控，关闭 Debug/Warning 时不再构造插值字符串。

### 2.8 部署面

- `EventLogPositionReserver` 默认块 100 → 1000，降低每 100 次追加一次的 2-RTT 串行停摆频率。
- PG Notifier 文档改为 statement-level transition-table 触发器：INSERT 每语句最多一次通知，
  UPDATE 仅在重新入队（非租约心跳）时通知。
- EF Inbox 删除 `(Status)`、`(ProcessedAt)` 冗余索引；Saga 删除无读者的
  `(Status, CurrentState)` 索引。

## 3. 验证

- 全量 1780 个测试：1703 通过、77 跳过、0 失败。
- `check-all`：IDE 0、CA 0、编译 0。
- Dapper AOT 探针 PG/MySQL 各 33/33（含 20 行批量回退）；NativeAOT SQLite 33/33。
- PalORM SQLite 2500 事件跨块、二进制投影、NULL 严格失败测试通过。
- 公共 API 快照无变化。

## 4. 未做

| 项 | 原因 |
|---|---|
| 后台轮询抖动 | 改变多实例时序，需要真实多实例 p99 与业务可接受度验证，不在本轮盲改 |
| PositionReserver 单语句分配 | 当前只调默认块；单项 `UPDATE ... RETURNING` 需 EF raw command 与跨 provider 验证 |
| 冗余索引在线迁移脚本 | EF 模型已收口；已有部署需按各自迁移工具生成 drop index，未伪造跨方言脚本 |
| 全量 PG/MySQL 并发与 A/B | 需要独立基准车道；本轮只做代码路径与可复现探针 |

## 5. 风险

- PalORM 分块只在 SQLite 本地跑 2500 事件；PG 4095 行边界需真库探针补测。
- Kafka/Rabbit Headers 改为按需物化，若 handler 把 `Headers` 保存到消费回调之后使用，依赖
  broker 头对象生命周期；当前契约是消费回调内使用。
- EF 冗余索引删除影响 EF 迁移创建的新库；已有库需要手工迁移确认后再删。
