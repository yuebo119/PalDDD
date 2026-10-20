# 2026-10-21 Outbox 可用时刻折叠与 EF EventLog 二进制读投影

## 1. 结论

本轮把两项结构优化落地，均未新增表列：

1. Outbox 的 `next_attempt_at` 升格为唯一可用时刻，租约时写 `locked_until`，查询从
   两列 OR 谓词降为单列范围谓词，索引改为 `(status, next_attempt_at, created_at)`。
2. EF EventLog 的 `UseBinaryEventId=true` 改为严格二进制投影：只 SELECT `EventIdBin`，
   不再加载文本列；切换前用 `CountMissingBinaryEventIdsAsync` 预检，NULL 行直接抛错。

## 2. Outbox 可用时刻折叠

### 2.1 行为变化

- 新建消息：`next_attempt_at = 显式值 ?? 存储层写入时刻`（四栈与 InMemory 同口径）。
- 租约：同一 UPDATE 写 `locked_until = until` 与 `next_attempt_at = until`。
- 重试/重新入队：写新的 `next_attempt_at`；终态清空。
- 查询：
  ```sql
  WHERE status = 0 AND retry_count < @max AND next_attempt_at <= @now
  ORDER BY next_attempt_at, created_at
  LIMIT @n
  ```
- `locked_until` 仅保留 fencing 与持租回读职责。

### 2.2 四栈改动

- Dapper：`SqlTemplates.OutboxInsert` 增加 `next_attempt_at`；五个租约模板去掉
  `next/locked` 双 OR，SET 同步写 `next_attempt_at=@until`。
- PalORM：`AddMessage/AddMessages` 设置默认可用时刻；三方言租约 SQL 同步。
- EF Core：四方言 `LeasePendingMessagesAsync` 写 `NextAttemptAt`；`BuildPendingSql`、
  `SqliteOutboxDbContext`、`InMemoryOutboxStore.QueryPending` 改单谓词；EF 直接
  `DbSet.Add` 的种子行在 `SaveChanges` 前归一化。
- InMemory：新增/租约 successor 写可用时刻。

### 2.3 索引与迁移

- PG/SQLite：`(next_attempt_at, created_at) WHERE status = 0`
- MySQL/SQL Server：`(status, next_attempt_at, created_at)`
- 迁移、回填、零违例校验、回滚与验收见
  `docs/sql/migration-2026-10-21-outbox-next-attempt-due.md`。
- 升级前必须回填：旧版本只写 `locked_until`，新查询不再读取该列判断到期。

### 2.4 验证

- Dapper/PalORM/EF/InMemory Outbox 测试全绿；新增跨块与可用时刻断言。
- Dapper AOT 探针：PG/MySQL 各 32/32，新增“新建写可用时刻”“租约折叠”断言；
  NativeAOT SQLite 32/32。
- 全量测试 1777 个，1700 通过、77 跳过、0 失败。

## 3. EF EventLog 严格二进制投影

### 3.1 读取行为

- 文本模式：只投影 `EventId`，不读 `EventIdBin`。
- 二进制模式：只投影 `EventIdBin`，不读文本 `EventId`；NULL 行抛
  `InvalidOperationException`，不再静默回退成空 ID。
- `EventReadRow` 与 `StoredEvent` 共用 `StoredEvent.CreateRecordedEvent`，审计字段空值
  单例与零拷贝载荷引用逻辑不分叉。

### 3.2 切换前置

新增 `EventLogDbContext.CountMissingBinaryEventIdsAsync`。宿主应在
`UseBinaryEventId=true` 前确认返回 0；回填配方继续使用
`docs/migration/event-id-binary-column.md`。

### 3.3 验证

- SQLite 真库：二进制读回 EventId 与原值相等；命令拦截器确认 SELECT 文本列不存在。
- NULL 回填缺失行：读取抛 `InvalidOperationException`，预检计数为 1。
- 全量测试与 `check-all` 见第 2.4 节。

## 4. 明确未做

| 项 | 原因 |
|---|---|
| PG/MySQL 真库 `EXPLAIN` 与 100k 积压 A/B | 需要独立基准车道与积压数据；迁移文档已给出 EXPLAIN 验收命令，CI 真库车道后续补 |
| `event_id_bin` 改 `NOT NULL` 与 EF `IsRequired` | 属 M4 contract 阶段，须在全量回填、所有写入方升级后执行；当前严格读 + 预检已阻止静默错误 |
| 文本 `event_id` 列删除 | 保留一个 major 作为旧版本兼容与诊断面 |

## 5. 残余风险

- 滚动升级若混跑旧实例，旧实例新建/租约的行可能没有 `next_attempt_at`；新实例不会
  误租旧活跃租约（旧行 `next` 为 NULL，范围谓词不命中），但这些行在新实例不可见。
  必须按迁移文档先回填并排空旧实例，或在窗口内循环回填。
- SQLite 仍存在 Dapper `"O"` 与 EF/PalORM provider 空格两种时间文本；本轮沿用既有按栈
  编码，未统一底层格式。
- EF `SaveChanges` 归一化仅覆盖 Added 实体；绕过 Store 直接 UPDATE 仍可能写入 NULL，
  迁移文档的零违例查询是最终防线。
