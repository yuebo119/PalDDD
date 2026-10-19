# ADR 033：Outbox 自动投递策略（时间界批量标记 + 能力校验升窗 + raw publish）

> 状态：**已采纳（2026-10-19 性能实施轮）**
> 日期：2026-10-19
> 关联：ADR-027（终态批量标记能力接口）、ADR-026（Outbox 异步化与 fencing）、
> DECISION-2026-10-16-outbox-eventlog-defaults-adjudication、docs/performance.md「T3.1 / R4」段

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-10-19 |
| 评审状态 | **已采纳并实施**：`OutboxDeliveryMode.Auto` 成为默认；`Serial` 保留旧行为作为兼容回退 |
| 取代关系 | 仅取代 2026-10-16 裁决中「`MaxInFlightPublishes` 与 `BatchMarkThreshold` 默认值维持 1」的默认部分。该裁决对纯计数窗口的重复放大与 Kafka 乱序的反驳仍然成立，本 ADR 用时间界与能力校验消解这两个前提 |
| 兼容性 | 公开 API 新增枚举与选项；旧配置仍可显式 `DeliveryMode = Serial` 复现逐条行为 |

## 决策

**D1 默认 Auto，能力不足自动回落。** `OutboxOptions.DeliveryMode` 默认 `Auto`。
Auto 只在 broker 声明 `SupportsConcurrentPublish` 时把发布窗口提升到至少 16；只在 store
实现 `IBatchMarkOutboxStore` 时把终态标记阈值提升到至少 16。自定义 broker/store 不做
能力声明时保持 1，不会替不支持并发的实现擅自升窗。

**D2 批量标记加时间上界。** 成功路径的终态标记在「满阈值」或「距上次冲刷满
`BatchMarkFlushInterval`（默认 1 秒）」时冲刷，批末强制冲刷。重复窗口由「1 秒 + 在途
发布时长」界定，不再由整批时长决定。启动期校验冲刷间隔不超过 `LeaseDuration` 的三分之一。

**D3 跳过空 SaveChanges。** `IPalOutboxStore.RequiresSaveAfterMark` 默认 `true`；内置四栈
（Dapper / EF Core / InMemory / PalORM）的 Mark* 已在调用内即时写库，覆写为 `false`，
处理器不再逐消息补一次空 `SaveChangesAsync`。自定义延迟写 store 保持旧行为。

**D4 raw publish。** `IMessageBroker` 新增 `SupportsRawPublish` 与 `PublishRawAsync`。
Outbox 在 broker 支持、且消息与描述符的 ContentType 都与当前序列化器一致时，直接把持久化
字节交给 broker，跳过「反序列化成对象 + broker 再序列化」。SchemaVersion 已由
`IMessageCatalog.Find(name, version)` 按存储版本解析，payload 与该描述符逐字节对应。
不支持 raw 的 broker 走原对象路径。

**D5 终止标记与发布解耦的边界。** 本 ADR 先落地时间界冲刷与能力校验；把标记完全移到
独立 flusher（发布循环与标记 RTT 重叠）登记为后续优化，需先测出「窗口 16 + 批量标记 16」
组合下标记仍阻塞发布的比例。

**D6 兼容回退。** `DeliveryMode = Serial` 时窗口固定 1、批量阈值取用户配置（默认 1）、
不做时间界冲刷，与 4.0.0 行为逐字一致。

## 证据锚点表

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| 窗口 16 在 RTT 1ms/20ms 下的实测收益 | docs/performance.md「T3.1」表：10,052→776ms（12.9×）、25,647→1,971ms（13.0×） | ☑ |
| 远程库批量标记实测收益 | docs/performance.md「R4 补测」：PG 6.37×、MySQL 6.4×，标记段占排空 84% | ☑ |
| 默认值现状与回落开关 | src/PalDDD.Transactions/TransactionOptions.cs `DeliveryMode` / `BatchMarkFlushInterval` | ☑ |
| 处理器策略解析与时间界冲刷 | src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs `ResolveDeliveryPolicy` / `FlushMarksAsync` | ☑ |
| 内置四栈即时写库 | `RequiresSaveAfterMark => false`（InMemory / Dapper / EF OutboxDbContext / PalORM） | ☑ |
| 能力接口 | src/PalDDD.Messaging/MessageBroker.cs `SupportsConcurrentPublish` / `SupportsRawPublish` / `PublishRawAsync` | ☑ |
| 锁定测试 | `OutboxBatchMarkProcessorTests`（Auto/Serial/时间界/raw）与 `RawPublish_SkipsDeserializeAndReserialize` | ☑ |

## 开放决策点

| # | 决策点 | 状态 | 触发条件 |
|---|--------|------|----------|
| 1 | 标记 flusher 与发布循环完全解耦 | 未实施 | 组合 A/B 显示标记段仍占排空 >5% 且窗口 16 已启用 |
| 2 | Kafka 管道化发布（Produce + delivery callback） | 未实施 | 真实 broker 下单实例发布吞吐受 RTT 限制，且 Outbox 窗口已开 |
| 3 | RabbitMQ 每订阅独立 consumer channel | 未实施 | 混合发布/消费压测出现 channel 级串行瓶颈 |
| 4 | 投影检查点高水位、Saga 到期索引、EventId 二进制列 | 未实施 | 对应路径的回放/扫描/索引成本进入实测热点，按独立 ADR 立项 |
