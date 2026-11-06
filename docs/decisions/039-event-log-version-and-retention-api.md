# ADR 039：事件流版本查询与保留清理扩面（A1/A2 等五项 API 增补）

> 状态：**已采纳（2026-11-07）**，随 v5.0.0 预览线交付（Minor + 破坏性签名变更，见「兼容性」）
> 日期：2026-11-07
> 关联：2026-11-06 全量功能审计（`docs/review/audit-2026-11-06-full.md` §四 A1–A5、G4）、修复方案 v4（`docs/review/fix-plan-2026-11-07-v4.md` §3 批 G）、ADR-037（事件日志只追加不删除）、ADR-020（持久化栈退役路线）

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-11-07 |
| 评审状态 | 已采纳（审计提出 5 项 API，方案 v4 经红队复核后收敛形态与范围） |
| 兼容性 | **A1/A2/A3 含破坏性签名变更**（接口新增成员 / 方法增参 / 主构造器 4→5 参），按 SemVer 属 Major 面；当前版本线 `5.0.0-preview.*`，随预览线交付并在 CHANGELOG 标注迁移说明。A4/A5 为纯增量 |
| 触发原因 | 审计指出五处能力缺口：跨进程追加无法只读流版本、Inbox Failed 行无生命周期管理、发布侧无自定义头入口、Saga 步骤表无公开枚举、broker 跳无 span |

## 背景

五项各自的事实依据（审计 + 源码实核）：

1. **A1**：`IEventLog` 只有 `AppendAsync`/`ReadStreamAsync`/`ReadAllAsync`。向已有流追加并要求 `ExpectedStreamVersion.Exact` 时，调用方只能 `ReadAllAsync` 全量物化才能算出 `LastStreamVersion`——10k 事件的流单次追加付 10k 条物化。
2. **A2/G4**：`IRetentionStore.DeleteExpiredInboxAsync` 无 `includeFailed`，而 `RetentionSql` 的 Inbox 删除只打 `status = 2`（Processed）；`InboxStatus.Failed = 3` 的行永不清理且无 reset/requeue 入口。Outbox 侧已有 `includeDead` 先例（`status IN (1,2)`）。
3. **A3**：`MessagePublishContext` 只有 4 个位置参数（CorrelationId/CausationId/TraceParent/TraceState），使用者想传租户/版本等业务头无入口。
4. **A4**：`Saga<TState>.Steps` 是 `protected`，框架无公开枚举入口；`DynamicStep.Route` 未知 key 零信号，拼写错误与滚动发布窗口期未注册新 key 在观测上不可区分。
5. **A5**：broker 发布/消费跳本身无 span（traceparent 头虽传播但跳不可见）；`PalActivitySource` 的 12 个工厂中 `StartCommandDispatch`/`StartSagaTransition` 预留未接线（ITM-627），而 broker 两跳同样缺。

## 决策

| 项 | 形态 | 兼容性 |
|----|------|--------|
| **A1** | `IEventLog.GetStreamVersionAsync(string streamName, CancellationToken)` 返回 `ValueTask<long>`（无流返回 `-1`，与 `ExpectedStreamVersion.NoStream` 语义对齐）。四栈同加：InMemory / EF Core / Dapper / PalORM | 接口新增成员 → **破坏自定义实现者**（源码 + 二进制）；随 Major 预览线 |
| **A2** | `IRetentionStore.DeleteExpiredInboxAsync` 增 `bool includeFailed` 参数（对齐 Outbox `includeDead` 先例）；`RetentionSql.InboxDelete` 同步扩面（`status = 2` vs `status IN (2,3)`）。三实现同步（Dapper / PalORM / EF Core，**无 InMemory 实现**） | 方法增参 → 破坏调用方；随 Major 预览线 |
| **A3** | `MessagePublishContext.ExtraHeaders`（`IReadOnlyDictionary<string,string?>?`，默认 null 零分配）作为第 5 个主构造器参数；两 broker 的 `CreateHeaders` 追加写入（内部方法，不进公共面） | 主构造器 4→5 参 → 破坏直接构造者；`Empty` 保持零分配 |
| **A4** | `Saga<TState>.GetRegisteredSteps()` 返回 `IReadOnlyList<SagaStepDescriptor>`（`Key` / `DispatchKind` / `Timeout`）；`Steps` 保持 `protected` | 纯增量 |
| **A5** | `PalActivitySource.StartBrokerPublish(string topicOrExchange, string eventName)` 与 `StartBrokerConsume(string topicOrExchange, string eventName)`，两 broker 的发布/消费入口各接一个 | 纯增量；**联动**：12→14 个 Activity 工厂，README/features/tutorial 的「12」声明同提交同步 |

### 被否决的形态

| 形态 | 否决理由 |
|------|---------|
| A1 改 `ReadStreamAsync` 返回版本（而非新增方法） | 会改既有方法的返回类型/语义，破坏面更大且与 `IAsyncEnumerable` 的惰性语义冲突 |
| A2 用 `IInboxStore` 加 reset/requeue 成员（方向二） | Failed→Pending 回炉需重新设计认领语义（`ProcessingStartedAt` 令牌、Attempts 上限），属独立设计项；`includeFailed` 对齐既有先例、成本低、可立即闭合无界增长 |
| A3 用 `Dictionary<string,string>` 非空默认值 | 默认路径（`MessagePublishContext.Empty`）会产生每消息一次字典分配；`null` + 按需构造是既有 B-5 性能纪律 |
| A4 把 `Steps` 改 public | 暴露内部元组结构（`SagaStepRegistration`），且 `SagaStep` 可变性未受控；新增只读 DTO 更稳 |
| A5 直接接线 `StartCommandDispatch`/`StartSagaTransition` | ITM-627 已有裁决「两个工厂都不接线，统一披露预留」；本 ADR 只补 broker 两跳，不推翻既有裁决 |

## 证据锚点表

| 主张 | 锚点 |
|------|------|
| `IEventLog` 仅 3 成员 | `src/PalDDD.EventLog/IEventLog.cs:10-32` |
| 四栈实现位置 | `InMemoryEventLog.cs:14`、`EventLogDbContext.cs:23-26`、`DapperEventLog.cs:45`、`PalOrmEventLog.cs:44` |
| Inbox 删除只打 status=2 | `src/PalDDD.Transactions/Retention/RetentionSql.cs:106,115` |
| Outbox includeDead 先例 | `src/PalDDD.Transactions/Retention/RetentionSql.cs:43-48`、`IRetentionStore.cs:19-23` |
| IRetentionStore 仅 3 实现 | `DapperRetentionStore.cs:15`、`PalOrmRetentionStore.cs:16`、`EfCoreRetentionStore.cs:22`（无 InMemory） |
| MessagePublishContext 4 参数 | `src/PalDDD.Messaging/MessagePublishContext.cs:9-17` |
| Saga.Steps 为 protected | `src/PalDDD.Transactions/Saga/Saga.cs:187-191` |
| Activity 工厂 12 个 | `src/PalDDD.Core/PalDiagnostics.cs:48,54,61,71,79,87,93,99,109,119,127,140` |
| broker 发布/消费入口 | `KafkaBroker.cs:196`、`KafkaBroker.cs:796`、`RabbitMQ.cs:233/283`、`RabbitMQ.cs:519` |

## 开放决策点

1. **A1 的四栈实现形态**：EF/PalORM/Dapper 用 `MAX(stream_version)` 单行查询（走 `(StreamName, StreamVersion)` 唯一索引），InMemory 直接读列表末位。四栈语义须一致（空流 = -1）。
2. **A2 的 EF Core 实现**：`EfCoreRetentionStore.BuildInboxDelete()` 需与 `RetentionSql` 两方言形态对齐（MySQL 直删 / 其他子查询）。
3. **A5 的 tag 基数**：topic/exchange 与事件名属低基数（按消息类型而非实例），符合 ITM-229 的零高基数 tag 纪律。
