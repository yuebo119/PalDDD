# ADR 039：事件流版本查询与保留清理扩面（A1/A2 等五项 API 增补）

> 状态：**已采纳（2026-11-07）**，随 v5.0.0 预览线交付（**Minor：五项均为纯加法，无破坏性签名变更**，见「兼容性」）
> 日期：2026-11-07
> 关联：2026-11-06 全量功能审计（`docs/review/audit-2026-11-06-full.md` §四 A1–A5、G4）、修复方案 v4（`docs/review/fix-plan-2026-11-07-v4.md` §3 批 G）、ADR-037（事件日志只追加不删除）、ADR-020（持久化栈退役路线）

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-11-07 |
| 评审状态 | 已采纳（审计提出 5 项 API，方案 v4 经红队复核后收敛形态与范围） |
| 兼容性 | **五项均为纯加法（Minor）**：A1 走能力子接口 `IEventLogVersionReader`（`IEventLog` 不变）、A2 保留旧 3 参成员并用 DIM 提供新重载（`includeFailed:true` 对未实现者 fail-visible）、A3 用 `init` 属性而非位置参数（ctor/Deconstruct 不变）；A4/A5 本就纯增量。无破坏性签名变更、无迁移成本 |
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
| **A1** | 新增**能力子接口** `IEventLogVersionReader.GetStreamVersionAsync(string streamName, CancellationToken)` → `ValueTask<long>`（无流返回 `-1`，与 `ExpectedStreamVersion.NoStream` 语义对齐）。`IEventLog` **不变**；四栈（InMemory / EF Core / Dapper / PalORM）均实现子接口 | **零破坏**：既有自定义实现者只需继续实现 `IEventLog`；需要该能力时显式 `is IEventLogVersionReader`（对齐 `TProvider.SupportsReturningClause` 先例）。刻意不用 DIM——DIM 返回 -1 会给出「流不存在」的假结果（同 2026-11-04 C 批拒绝 DIM 的理由） |
| **A2** | `IRetentionStore` **保留**旧 3 参 `DeleteExpiredInboxAsync`（仍为必需成员），新增 4 参重载（`bool includeFailed`）以 **DIM 默认实现**提供：`includeFailed:false` 转发旧成员，`true` 抛 `NotSupportedException`（fail-visible，不静默按 false 执行造成假成功）。三个内置实现（Dapper / PalORM / EF Core，**无 InMemory**）均覆写新重载 | **零破坏**：既有实现者无需改动；`RetentionSql.InboxDelete` 与 `EfCoreRetentionStore.BuildInboxDelete` 同步扩面（`status = 2` vs `IN (2,3)`） |
| **A3** | `MessagePublishContext` **保持 4 位置参数主构造器**，新增 `IReadOnlyDictionary<string,string?>? ExtraHeaders { get; init; }`；两 broker 的 `CreateHeaders` 追加写入（内部方法，不进公共面） | **零破坏**：ctor 与自动生成的 `Deconstruct` 均不变（源码 + 二进制兼容），用法 `new MessagePublishContext(...) { ExtraHeaders = headers }`；`Empty` 保持零分配 |
| **A4** | `Saga<TState>.GetRegisteredSteps()` 返回 `IReadOnlyList<SagaStepDescriptor>`（`Key` / `DispatchKind` / `Timeout`）；`Steps` 保持 `protected` | 纯增量 |
| **A5** | `PalActivitySource.StartBrokerPublish(string topicOrExchange, string eventName)` 与 `StartBrokerConsume(string topicOrExchange, string eventName)`，两 broker 的发布/消费入口各接一个 | 纯增量；**联动**：12→14 个 Activity 工厂，README/features/tutorial 的「12」声明同提交同步 |

### 被否决的形态

| 形态 | 否决理由 |
|------|---------|
| A1 把 `GetStreamVersionAsync` 加进 `IEventLog`（初版形态） | 接口新增成员会破坏所有既有自定义实现者（源码 + 二进制），把本可 Minor 的变更抬成 Major；能力子接口零破坏且不产生假结果 |
| A1 用 DIM 返回 -1（零破坏的替代） | DIM 会让未更新的实现静默给出「流不存在」——假 API 形态，与 2026-11-04 C 批 `GetByStatusAsync` 拒绝 DIM 的既有裁决冲突 |
| A1 改 `ReadStreamAsync` 返回版本（而非新增方法） | 会改既有方法的返回类型/语义，破坏面更大且与 `IAsyncEnumerable` 的惰性语义冲突 |
| A2 直接给既有成员增参（初版形态） | 破坏所有既有调用方与实现者；保留旧成员 + DIM 新重载可零破坏，且 `includeFailed:true` 对未实现者 fail-visible |
| A2 用 `IInboxStore` 加 reset/requeue 成员（方向二） | Failed→Pending 回炉需重新设计认领语义（`ProcessingStartedAt` 令牌、Attempts 上限），属独立设计项；`includeFailed` 对齐既有先例、成本低、可立即闭合无界增长 |
| A3 把 `ExtraHeaders` 做成第 5 个位置参数（初版形态） | 改主构造器与自动 `Deconstruct`，破坏既有构造/解构调用方；`init` 属性保持 ctor/Deconstruct 不变 |
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
