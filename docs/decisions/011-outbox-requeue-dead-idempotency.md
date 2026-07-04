# ADR 011：Outbox 死信重投递入口 `RequeueDeadAsync` 的幂等前提

> 状态：已采纳  
> 日期：2026-06-29  

## 背景

`OutboxStatus.Dead` 表示一条 outbox 消息重试耗尽后进入死信终态，等待人工介入。原设计无重建 / 重投递 API，生产场景需 ops 直接 `UPDATE outbox_messages SET status='Pending'`，存在以下风险：

1. 越权把 `Processed` / 已 Pending 的消息重置，导致已发布消息二次发布；
2. 不留操作审计串，无法追溯谁在何时重投；
3. ops 直接写库绕过框架语义校验。

评审 ITM-002 提出两种方案：A 暴露 `IPalOutboxStore.RequeueDeadAsync` 由 Inbox/Idempotency 保证幂等；B 文档化 ops 人工 SQL 路径与幂等前提。

## 决策

采纳**方案 A**——在 `IPalOutboxStore` 暴露 `RequeueDeadAsync` 公共方法，三实现（`InMemoryOutboxStore` / `DapperOutboxStore` / `OutboxDbContext`）均落地。

### 方法签名

```csharp
ValueTask<int> RequeueDeadAsync(Guid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct);
```

### 语义约束

1. **仅作用于 `Status == Dead` 的消息**：SQL `WHERE id=@id AND status='Dead'` / EF Core `Where(m => m.Status == OutboxStatus.Dead)` 强制约束——返回 0 表示消息不存在或当前状态非 Dead，调用方据此判断是否需要排查。
2. **`RetryCount` 不重置**：保留失败历史供可观测性查询。若希望重投后给完整重试预算，ops 可在调用前单独 `UPDATE retry_count=0`（属运维操作，不在框架 API 范围）。
3. **写入操作审计串**：`Error` 列写入 `"requeued by {retriedBy} at {now:O}"`，留可追溯的操作链。
4. **`ProcessedAt` 清 NULL、`LockedBy`/`LockedUntil` 清 NULL**：与 `ReleaseForRetry` 一致，确保进入 Pending 后可被正常租约获取。
5. **`NextAttemptAt` 由调用方传入**：ops 可控制首次重投时间窗（如延后 30s 错开高峰）。

### 幂等前提（关键约束）

`RequeueDeadAsync` 只保证 outbox 消息被重新发布到 Broker，**不保证下游处理幂等**。调用方必须满足以下前提之一，否则重投递可能导致重复副作用：

- **下游消费者通过 Inbox 模式**（`IInboxStore.TryStartProcessingAsync`）保证 `(consumer_name, message_id)` 联合唯一——重复投递的消息在 Inbox 层被去重；
- **下游消费者通过 Command Idempotency**（`IIdempotencyStore`）保证 `(operation_name, key)` 幂等——重复执行被幂等层拦截；
- **Handler 本身是天然幂等的**（如 upsert by key、聚合状态机等）。

若下游未接入任一幂等机制，**禁止调用 `RequeueDeadAsync`**——应先修复根因后人工评估是否重投。

## 取舍

- **优点**
  - 框架统一入口，语义校验集中——避免 ops 直写库绕过校验导致的越权重置；
  - 三实现统一签名，调用方与存储后端解耦；
  - 操作审计串留存，可观测性可追溯。
- **代价**
  - 新增公共 API 表面（`IPalOutboxStore` 接口 + 3 实现 + 快照 + stub），需长期维护 API 稳定性；
  - 幂等前提是调用方责任，框架无法在编译期强制——需通过 ADR + XML doc 显式约束。

## 验证

- `PalDDD.Transactions.Tests/OutboxRequeueTests`：覆盖 InMemoryOutboxStore 的 Dead→Pending 闭环、非 Dead 拒绝重投、RetryCount 保留、审计串写入 4 个场景。
- 公共 API 快照（`test/PalDDD.Core.Tests/Snapshots/core-packages-public-api.txt`）已同步 `RequeueDeadAsync` 签名。
- `IPalOutboxStore` 三实现的 fake/stub 已补齐方法签名，编译期零错。

## 关联

- 接口：`src/PalDDD.Transactions/OutboxStore.cs`（`IPalOutboxStore.RequeueDeadAsync`）
- 实现：`InMemoryOutboxStore` / `DapperOutboxStore` / `OutboxDbContext`
- SQL 模板：`src/PalDDD.Dapper/SqlTemplates.cs`（`OutboxRequeueDead`）
- 评审来源：`docs/review/audit-2026-06-29-v2.md` ITM-002
- 幂等基础：`src/PalDDD.Transactions/InboxStore.cs`（Inbox）/ `src/PalDDD.Idempotency/`（Command Idempotency）