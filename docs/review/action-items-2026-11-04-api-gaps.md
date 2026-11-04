# 2026-11-04 公共 API 缺口修复方案与任务清单

> 来源：2026-11-04 全量 API 审计（36 个生产项目、公共 API 金标、三方向只读探索：快照与 Saga 读侧 / 运维面 / 消费失败路径）。
> 版本窗口：`VersionPrefix=5.0.0` / `VersionSuffix=preview.1`，最新 tag `v5.0.0-preview.1`，**GA 未发布**。
> 因此本方案的破坏性变更可直接进 5.0.0，不需要 `[Obsolete]` 过渡窗口。
> 本文只写「做什么、为什么、怎么验收」，不复述审计过程。

---

## 一、前提与约束

| 约束 | 内容 | 对本方案的影响 |
|---|---|---|
| 版本窗口 | 5.0.0 GA 未发布，preview.1 之后仍可破坏 | 可删 API；不加 `[Obsolete]` 过渡 |
| AOT | 抽象层零反射；不允许 `MakeGenericType` / `Activator` | 新 API 一律用泛型或显式注册，不做运行时类型分派 |
| 三方一致 | 公共 API / 行为 / 计数变更须同提交同步代码、文档、注释 | 每批提交必须带 CHANGELOG 与快照 |
| 三栈对称 | EF / Dapper / PalORM 实现同一组抽象接口 | 读侧 API 必须三栈都实现，不能只做 EF |
| G23 | 公共 API 快照变更须同提交改 CHANGELOG | 每批提交前跑 `gate.cs` |
| count-audit | 源文件数、包数等 16 项推导真值 | 新增源文件须同步 `docs/conventions.md` 源文件数 |
| 编码 | 新建文件 CRLF；`.md` 受 encoding-gate E5 约束 | 本文件及新增 `.cs` 写后做字节校验 |

**一个已修正的前提**：上一轮判定「`ISagaManager` 含 internal 成员、外部无法替代实现」依据的是 `ISagaManager.cs:48` 的 remarks。核实后发现该 remarks 已过期：同文件 `:13-16` 明确写「该成员已移除」，`grep -c internal` 只剩 3 处注释文本。接口现可由外部实现。这改变了 B2 的修法，见 §三 B2。

---

## 二、总览：四批

| 批次 | 主题 | 性质 | 破坏性 | 依赖 |
|---|---|---|---|---|
| A | 消费确认契约 | 新 API，补契约缺失维度 | 否（纯增量） | 无 |
| B | Saga 契约一致 | 删假 API + 删死枚举 + 加读侧 | 是（5.0.0 窗口内） | 无 |
| C | Outbox/Inbox 读侧 | 新 API，补三栈对称 | 否（加接口成员，需 4 实现） | B 的模式 |
| D | 文档与决策 | ADR + 文档纠偏 | 否 | A/B/C 终态 |

排序依据三条，不是工作量：

1. **A 最先**：它是唯一「使用者想做也做不成」的缺口。其余批次都是一致性或便利性。
2. **B 必须趁 5.0.0 GA 前**：删除公共 API 与枚举只在 Major 窗口合法，错过要等到 6.0.0。
3. **C 在 B 后**：B 的 Saga 读侧先落一个完整样板（抽象 + 四实现 + 矩阵测试），C 直接套用同一模式，省一轮设计返工。

串行执行。项目规则禁止写型任务并行共享 checkout。

---

## 三、批次设计

### A 批：消费确认契约

#### 问题

`IMessageBroker.SubscribeAsync` 的两种 handler 形态都不携带确认能力（`MessageBroker.cs:100-113`）。`KafkaBroker` 的 `IConsumer` 是订阅方法内的局部变量，构造只保留 `_consumerConfig`（`KafkaBroker.cs:64-100`）；消费循环全路径无 `StoreOffset`/`Commit`；handler 异常一律 `catch` 后记日志继续（`KafkaBroker.cs:652-656`）。

而接口注释指导用户「关闭 EnableAutoOffsetStore 并在 handler 成功后 StoreOffset」（`MessageBroker.cs:26`）。用户照做后 offset 永不前进，重启行为由 `AutoOffsetReset` 决定：`earliest` 全量重放，`latest` 静默跳过全部未提交消息。

这不是文档措辞问题。缺的是订阅契约里的一个维度：消费确认。

#### 设计

A1. 新增 `IMessageAck`（`src/PalDDD.Messaging/MessageAck.cs`）

```csharp
public interface IMessageAck
{
    /// <summary>处理成功，允许推进消费位置。</summary>
    ValueTask AcknowledgeAsync(CancellationToken ct = default);

    /// <summary>处理失败。requeue=true 请求重投；false 表示放弃该消息。</summary>
    ValueTask RejectAsync(bool requeue, CancellationToken ct = default);
}
```

A2. 新增订阅重载，DIM 默认转发到旧重载并传无操作确认器

```csharp
ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
    Func<TMessage, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)
    => SubscribeAsync<TMessage>(
        (m, c, token) => handler(m, c, NoOpMessageAck.Instance, token), ct);
```

批量订阅加同构重载，确认器为批次级（一个 `IMessageAck` 对应整批）。`ConsumedMessage<T>` 的 record 签名不动，避免破坏。

A3. Kafka 实现 `KafkaMessageAck`

- `AcknowledgeAsync`：仅当 `_consumerConfig.EnableAutoOffsetStore == false` 时调 `consumer.StoreOffset(result)`；否则 no-op（保持自动提交语义）。
- `RejectAsync(requeue: true)`：`consumer.Seek(result.TopicPartitionOffset)` 后短退避（200ms 起，指数到 5s），避免紧循环。
- `RejectAsync(requeue: false)`：调 `StoreOffset` 跳过该条。
- 调用方不调任何方法时，行为与现在逐字一致（异常记日志继续，正常返回不 store）。

A4. RabbitMQ 实现 `RabbitMqMessageAck`

- 现有路径已是 manual ack（成功 `BasicAck`、异常 `Nack(false)`，`RabbitMqBroker.cs:499-501`）。
- 新确认器：`AcknowledgeAsync` → `BasicAck`；`RejectAsync(true)` → `Nack(requeue: true)`；`RejectAsync(false)` → `Nack(requeue: false)`。
- 加「已确认」原子标志防 double ack。handler 未显式调用时按现有兜底规则处理。

A5. 文档改写

`MessageBroker.cs:26` 改为如实描述：默认 at-most-once；需要 at-least-once 用带 `IMessageAck` 的重载并关闭自动存储。

A6. 验收

| 测试 | 类型 | 断言 |
|---|---|---|
| `KafkaAckTests.AckAdvancesOffset` | 集成（wslc Kafka） | 关自动存储 + Ack 后重启，不重投 |
| `KafkaAckTests.RequeueRedelivers` | 集成 | `Reject(true)` 后同一消息再次交付 |
| `KafkaAckTests.RejectSkips` | 集成 | `Reject(false)` 后不重投 |
| `RabbitAckTests` 三态 | 集成（wslc RabbitMQ） | Ack / Requeue / Reject 对应 broker 状态 |
| `NoOpAckTests` | 单元 | 旧重载行为零变化，`NoOpMessageAck` 不抛 |

变异验证：把 `AcknowledgeAsync` 的 `StoreOffset` 调用注释掉，`AckAdvancesOffset` 必须转红。

A7. 风险

- Kafka `Seek` 路径是首次实现，可能出现「seek 后立即重新 poll 同一批」的紧循环。退避是必需的，不是优化。
- 集成测试依赖 wslc 容器。容器不可用时按项目既有惯例 Skip，但必须**在本机实跑一次**并记录结果，不能只靠 CI。
- `EnableAutoOffsetStore=false` 与 `EnableAutoCommit=true` 的组合行为需实测确认，不凭文档假设。

---

### B 批：Saga 契约一致

#### 问题

三处公开面与实现不一致：

1. `SagaStatus.DeadLettered` 无任何赋值路径（状态机只有 `Compensated` / `CompensationFailed` / `AwaitingHumanDecision` 三处），但 `DefaultSagaManager.cs:126-131` 已把它当终态处理，持久化层会从库里读回 `(SagaStatus)row.Status`。
2. `ISagaManager.GetInterruptedSagasAsync` 恒返回空（`DefaultSagaManager.cs:224-228`），注释自认「误信 = 静默假空」。
3. 读侧只有 `GetActiveSagasAsync(batchSize)`，状态过滤写死 `Active || AwaitingHumanDecision`（`SagaStateDbContext.cs:55-61`），其余四态无法列举。

#### 设计

B1. `ISagaStateStore<TState>` 增读侧方法

```csharp
ValueTask<IReadOnlyList<TState>> GetByStatusAsync(
    SagaStatus status, int maxCount, CancellationToken ct = default);
```

四个实现：`InMemorySagaStateStore<TState>`、`SagaStateDbContext<TState>`（EF）、`DapperSagaStateStore<TState>`、`PalOrmSagaStateStore<TProvider,TState>`。三栈 SQL 均为 `WHERE status = @status ORDER BY saga_id LIMIT @max`，走既有 `(status)` 索引。

B2. 删 `ISagaManager.GetInterruptedSagasAsync`

理由三条，任一成立即够：

1. 类型上无法正确实现：`DefaultSagaManager` 是非泛型类，`ISagaStateStore<TState>` 是泛型接口，非泛型 manager 拿不到 `TState`，只能靠反射，违反零反射约束。
2. 原有保留理由已失效：接口 internal 成员已移除（`ISagaManager.cs:13-16`），「外部无法替代实现」不再成立。
3. 同需求已有更好的入口：B1 的泛型读侧 API 精确表达「查某种状态下的某类 Saga」。

连带改动：删 `DefaultSagaManager` 的实现与注释、删 `ISagaManager.cs:45-52` 的过期 remarks、CHANGELOG `Removed` 段写迁移说明（改用 `ISagaStateStore<TState>.GetByStatusAsync(SagaStatus.AwaitingHumanDecision, n)`）。

B3. 删 `SagaStatus.DeadLettered`

理由：语义位置已被 `CompensationFailed` 占据。DeadLetter 是消息中间件术语，Saga 域的对应概念是补偿失败。留着的代价是每个使用者的 `switch` 多一个必须处理但永不进入的分支。

连带改动四处：

| 位置 | 改动 |
|---|---|
| `SagaStatus.cs:18-19` | 删枚举值 |
| `DefaultSagaManager.cs:127-128` | 终态判断删一项 |
| `SqlTemplates.cs:313` | 注释中的终态清单同步 |
| 三栈水合点 | `(SagaStatus)row.Status` 改为带 `Enum.IsDefined` 校验的映射，未知值抛可行动异常 |

水合点必须改，否则库里的 4 会被静默读成未定义枚举值。这是 P0 #4「不静默」的直接应用。

B4. 验收

| 测试 | 类型 | 断言 |
|---|---|---|
| `SagaStatusQueryTests`（四实现矩阵） | 单元 + 集成 | 每种状态都能按 `GetByStatusAsync` 取出，`maxCount` 生效，其余状态不混入 |
| `SagaHydrationTests.UnknownStatusThrows` | 单元 | 库里存 4 时抛 `InvalidOperationException`，消息含 sagaId 与值 |
| 现有 Saga 测试全量 | 回归 | 补偿、超时、恢复路径零变化 |

变异验证：把 `GetByStatusAsync` 的状态过滤改成恒真，矩阵测试必须转红。

B5. 风险

- 删 `DeadLettered` 后，若有生产库手工写入过 4，水合会 fail-fast。这是有意选择，但 CHANGELOG 必须写清「升级前检查 `SELECT COUNT(*) FROM saga WHERE status = 4`」。
- 四个实现的 SQL 方言差异：SQLite 的 `LIMIT` 与 PG/MySQL 一致，但列名大小写按各栈既有约定，不能新造第三套。

---

### C 批：Outbox/Inbox 读侧

#### 问题

归属原则：框架自己写入的状态（`OutboxStatus`、`InboxStatus`、`RetryCount`、`LastError`），框架应提供读侧。现状是 EF 栈暴露 `DbSet` 让用户查（`OutboxDbContext.cs:38`、`InboxDbContext.cs:31`），Dapper 与 PalORM 栈没有，只有 SQL 常量。这打穿了 `features.md:148` 声明的「三栈语义对齐」。

具体缺失：

- Outbox 只能按 ID 单条重投，无法列 Dead、无法按 `CorrelationId` 检索。
- Inbox 只有三个写方法，无失败列表、无手动重置。
- 运维无任何读侧入口，只能连库写裸 SQL。

#### 设计

C1. `IPalOutboxStore` 增一个方法

```csharp
ValueTask<IReadOnlyList<OutboxMessage>> GetByStatusAsync(
    OutboxStatus status, int maxCount, CancellationToken ct = default);
```

C2. `IInboxStore` 增一个方法

```csharp
ValueTask<IReadOnlyList<InboxMessage>> GetByStatusAsync(
    InboxStatus status, int maxCount, CancellationToken ct = default);
```

C3. 各四个实现（含 InMemory）

- Outbox：`InMemoryOutboxStore`、`OutboxDbContext`（EF）、`DapperOutboxStore`、`PalOrmOutboxStore<TProvider>`
- Inbox：`InMemoryInboxStore`、`InboxDbContext`（EF）、`DapperInboxStore`、`PalOrmInboxStore<TProvider>`

C4. 索引核查

新增查询按 `status` 过滤。执行前查现有索引是否覆盖 `(status)` 或 `(status, next_attempt_at)`。缺则本批一并补迁移脚本，不留给下一轮。

C5. 验收

矩阵测试四实现 × 两接口 ×（有数据 / 空表 / maxCount 边界 / 状态隔离）。变异验证同上。

C6. 判断依据

这条在上一轮我给的优先级偏低，理由是「用户查库也行」。修正：该理由只在 EF 栈成立。Dapper 栈恰恰是性能敏感场景的选择，也是最需要运维入口的地方。按三栈对称承诺推导，读侧应进抽象层。

如果维护预算只够做一部分，**Saga 必做**（它同时消掉 B2 的假 API），Outbox/Inbox 可拆出独立批次延后。

---

### D 批：文档与决策

D1. 新增 ADR-037：事件日志只追加不删除

三段结构（V11 要求）：

- 背景：四个 EventLog 实现只有 append 与 read；`RetentionSql` 只打 outbox/inbox/idempotency 三表（`RetentionSql.cs:100-124`）；36 份 ADR 无一份涉及事件日志生命周期。
- 决策：事件日志刻意不提供删除、归档、截断 API。合规删除权用加密擦除（crypto-shredding）实现，由使用者在 payload 加密层处理。
- 后果：存储随业务增长单调上升，需要归档时导出到冷存储（`EventStreamJsonLines` 已提供导入导出）；框架不承诺行级删除。

D2. `features.md` 第 8 节标题改为「事件日志与投影」

加边界声明：当前只到日志层，聚合重放器与快照不在范围。`architecture.md:243` 已把 Event Sourcing Adapter 列为未来可选外圈，文档与之一致。

D3. CHANGELOG `[Unreleased]` 段

按既有格式：Added（`IMessageAck`、三个 `GetByStatusAsync`）、Removed（`GetInterruptedSagasAsync`、`SagaStatus.DeadLettered`）、Changed（`MessageBroker` 投递语义描述）、Documentation（ADR-037）。

D4. 计数联动

新增源文件（`MessageAck.cs` 等）后跑 `count-audit`，同步 `docs/conventions.md` 源文件数与 `features.md` 的项目内类型清单。

D5. 验收

`dotnet run scripts/gate-audit.cs` 全 OK；`doc-consistency`、`count-audit`、`encoding-gate`、`verify-conventions --quick`、`license-policy` 全过。

---

## 四、任务清单

预估按单人计，单位为小时，含测试与文档同步。

### A 批（消费确认）

| ID | 任务 | 依赖 | 预估 | 验收 |
|---|---|---|---|---|
| A1 | 新增 `IMessageAck` + `NoOpMessageAck` | 无 | 2 | 编译通过；单元测试覆盖默认 no-op |
| A2 | `IMessageBroker` 加两条订阅重载（DIM 转发） | A1 | 2 | 旧重载行为零变化（回归测试） |
| A3 | `KafkaMessageAck` + 消费循环接线 | A2 | 8 | 关自动存储时 Ack/Reject 三态集成测试 |
| A4 | `RabbitMqMessageAck` + double-ack 防护 | A2 | 5 | 三态集成测试 |
| A5 | `MessageBroker.cs:26` 文档改写 + `InboxBatchConsumer` remarks 同步 | A3, A4 | 1 | 三方一致检查 |
| A6 | `MessageBatchPump` 批量确认语义 | A2 | 4 | 批次级 ack 的集成测试 |
| A7 | 本机 wslc 实跑记录（Kafka + RabbitMQ 各一轮） | A3, A4 | 3 | 输出粘贴进交付报告 |

A 批合计 25 小时。

### B 批（Saga 契约一致）

| ID | 任务 | 依赖 | 预估 | 验收 |
|---|---|---|---|---|
| B1 | `ISagaStateStore<TState>.GetByStatusAsync` 抽象 | 无 | 1 | 编译通过 |
| B2 | InMemory 实现 | B1 | 1 | 矩阵测试子集 |
| B3 | EF 实现（`SagaStateDbContext`） | B1 | 2 | 矩阵测试子集 |
| B4 | Dapper 实现 + SQL 模板 | B1 | 2 | 矩阵测试子集 |
| B5 | PalORM 实现 + 源生成物化 | B1 | 3 | 矩阵测试子集（PalORM 物化需方言签名） |
| B6 | 删 `ISagaManager.GetInterruptedSagasAsync` 及过期 remarks | 无 | 1 | 全仓零引用；CHANGELOG Removed 段 |
| B7 | 删 `SagaStatus.DeadLettered` 及连带四处 | 无 | 1 | 全仓零引用 |
| B8 | 三栈水合点加 `Enum.IsDefined` 校验 | B7 | 2 | 未知值抛异常测试 |
| B9 | 公共 API 快照更新 + CHANGELOG | B1-B8 | 1 | G23 转绿 |

B 批合计 14 小时。

### C 批（Outbox/Inbox 读侧）

| ID | 任务 | 依赖 | 预估 | 验收 |
|---|---|---|---|---|
| C1 | `IPalOutboxStore.GetByStatusAsync` 抽象 + InMemory | B1 模式 | 2 | 单元测试 |
| C2 | Outbox EF / Dapper / PalORM 三实现 | C1 | 5 | 矩阵测试 |
| C3 | `IInboxStore.GetByStatusAsync` 抽象 + InMemory | C1 | 2 | 单元测试 |
| C4 | Inbox EF / Dapper / PalORM 三实现 | C3 | 5 | 矩阵测试 |
| C5 | 索引核查与迁移脚本（如需） | C2, C4 | 2 | `EXPLAIN` 或索引存在性断言 |
| C6 | 公共 API 快照 + CHANGELOG | C1-C5 | 1 | G23 转绿 |

C 批合计 17 小时。

### D 批（文档与决策）

| ID | 任务 | 依赖 | 预估 | 验收 |
|---|---|---|---|---|
| D1 | ADR-037（三必填段） | 无 | 1 | V11 通过；ADR 计数 36→37 四处同步 |
| D2 | `features.md` 标题与边界声明 | A-C | 0.5 | count-audit |
| D3 | CHANGELOG 四段 | A-C | 1 | G23 |
| D4 | 计数联动（源文件数、类型清单） | A-C | 1 | count-audit |

D 批合计 3.5 小时。

### 总计

| 批次 | 小时 | 说明 |
|---|---|---|
| A | 25 | 含首次实现 Kafka 手动提交与两轮真库实跑 |
| B | 14 | 四实现 + 两处删除 + 水合校验 |
| C | 17 | 六实现 + 索引核查 |
| D | 3.5 | 文档与门禁 |
| 合计 | 59.5 | 约 7.5 个工作日（按 8 小时/天，含返工余量 15%） |

估算依据：每个存储实现按 1 到 2 小时（含单测），Kafka 手动提交按 8 小时（首次路径 + 紧循环风险），真库实跑按 3 小时（容器启动 + 两轮）。

---

## 五、明确不做

| 项 | 结论 | 依据 |
|---|---|---|
| 内置 DLQ 与 `InboxOptions.MaxAttempts` | 不做 | A 批落地后，用户可用显式 Reject 配 RabbitMQ DLX 或 Kafka 重投策略自行实现。推给用户的前提是「给了手段」，A 批补的正是手段。 |
| admin HTTP 端点 | 不做 | C 批读侧 API 已覆盖运维查询需求。端点属于宿主自定义，框架提供会引入路由与授权面。 |
| 事件流删除或归档 API | 不做 | 用加密擦除替代，见 D1。 |
| `AggregateRehydrator<T>` + `ISnapshotStore` | 暂不做 | 事件溯源是少数派用法。立项条件：有实际消费方要求，且能列出「聚合重放 + 快照」的最小 API 形状。当前只在文档收窄宣称范围（D2）。 |
| 四套 AsyncLocal 收敛、结构化日志进一步改造、`AddPalFullStack` 更名 | 不做 | 已有裁决，见 `action-items-2026-10-21-merged.md` 第三节。 |

---

## 六、风险与回退

| 风险 | 概率 | 影响 | 处置 |
|---|---|---|---|
| Kafka `Seek` 造成紧循环 | 中 | 高（CPU 打满） | 退避强制实现；A3 验收含「连续 100 次 Reject 不超 10 秒」 |
| 删 `DeadLettered` 导致生产库水合失败 | 低 | 中 | CHANGELOG 写升级前置检查；水合异常含 sagaId 与值 |
| 新增读侧 SQL 缺索引 | 中 | 中 | C5 强制索引核查，不通过则本批补迁移 |
| 集成测试只在 CI 跑、本机未验 | 中 | 中 | A7 强制本机记录；按项目规则「全绿引用本轮实测」 |
| 公共 API 快照未同步 | 低 | 低（门禁会拦） | 每批提交前跑 `gate.cs` |

回退策略：四批互相独立，任意一批可单独 `git revert` 而不影响其他批次。A 批的回退点是「删两条重载」，B/C 的回退点是「恢复接口方法」。

---

## 七、需要裁决的两点

1. **C 批是否整批做**。建议做，理由是它补齐 `features.md:148` 已声明的三栈对称。若维护预算紧张，可只保留 Saga 部分（B 批已含），Outbox/Inbox 延后。
2. **`DeadLettered` 直接删还是保留并补赋值路径**。建议直接删。若要保留，需要先定义它与 `CompensationFailed` 的语义差（例如「补偿失败且人工介入次数超限」），那会引入新字段与新状态转移，属于特性而非修复。

---

## 八、执行顺序建议

A → B → C → D，每批一个提交，提交信息按项目格式：

- `修复：消费确认契约补齐（A 批）`
- `修复：Saga 契约一致（B 批，删假 API 与死枚举）`
- `新增：Outbox/Inbox 读侧查询（C 批）`
- `文档：ADR-037 与事件溯源范围收窄（D 批）`

每批提交前：`dotnet build` 零警告、相关测试全绿、`gate.cs`、`gate-audit` 全 OK。
