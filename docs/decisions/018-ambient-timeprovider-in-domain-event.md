# ADR 018：DomainEvent 时间源采用 Ambient TimeProvider（AsyncLocal），不注入构造函数

> 状态：已采纳
> 日期：2026-08-26
> 关联：ADR-021（同轮评审产出的姊妹决策）、二轮架构评审"辩证反思"

## 背景

`DomainEvent.OccurredOn` 与 `EventId` 在事件构造时生成。时间来源有三个候选：

1. **DI 构造注入**：`DomainEvent(TimeProvider timeProvider)` 或工厂方法注入。
2. **static 可变字段**：`DomainEvent.TimeProvider = fake;`（全局单值）。
3. **AsyncLocal 字段**（现行）：`internal static AsyncLocal<TimeProvider>`，按执行上下文隔离（`DomainEvent.cs:75-81`）。

二轮评审指出这是"Ambient Context 模式 vs 显式依赖"的架构张力：领域构造函数读取隐藏的环境状态，不是显式依赖——该取舍此前只有代码注释论证，无 ADR 存档（本 ADR 补档）。

## 决策

**维持 AsyncLocal Ambient TimeProvider（internal），不改构造注入。**

## 理由

1. **构造注入污染全部领域签名**：DomainEvent 构造发生在聚合方法深处（`Order.Submit()` 内部 `new OrderSubmitted(...)`）。注入 TimeProvider 要求每个聚合方法透传时间源或每事件携带参数——领域 API 被基础设施关注点入侵，违背"业务代码保持纯 C#"的框架定位。
2. **static 全局字段不可行**：并行测试互相干扰（TUnit 并行执行是常态），单值全局时间源会让 FakeTimeProvider 的确定性时间在测试间泄漏。
3. **AsyncLocal 的隔离恰好匹配测试需求**：每个测试上下文独立设置时间源，40+ 轮审计与 963 个测试中该机制零事故。
4. **可见性收窄为 internal**：领域使用者无法感知/修改时间策略（第三方经 `InternalsVisibleTo` 或应用层 Processor 的 TimeProvider 参数注入），时间戳生成保持框架内部关注点。
5. **已知边界（诚实声明）**：AsyncLocal 沿 ExecutionContext 流动，`ExecutionContext.SuppressFlow()`/`UnsafeQueueUserWorkItem` 等显式抑制上下文的 API 不流动——当前框架使用场景（聚合方法内构造事件）均在调用方上下文内完成（`DomainEvent.cs:68-72` 注释论证含 Task.Run 流动性的勘误史）。

## 触发重评条件

- 领域事件构造出现在显式抑制上下文的执行路径（自定义线程池调度/SuppressFlow 管道）导致时间戳回退 System 时间。
- 测试框架改为跨 ExecutionContext 捕获执行（AsyncLocal 语义失真）。
- C#/.NET 提供更轻量的按调用方隔离机制。

## 后果

- 领域方法签名保持纯净（无 TimeProvider 参数）。
- 时间确定性测试依赖 AsyncLocal 语义——文档（`DomainEvent.cs` remarks 五点论证）与本 ADR 双重存档。
- 应用层时间控制走显式路径：OutboxProcessor/InboxProcessor 等的 `TimeProvider?` 构造参数。
