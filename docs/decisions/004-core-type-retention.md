# ADR 004：核心类型保留论证

> 状态：已采纳  
> 日期：2026-06-28  

## 背景

Pal.DDD 框架中有多个核心类型当前没有内部引用（或仅有少量引用），但基于框架库面向外部使用者的特性，不应仅凭内部引用图判定为可删除。

## 涉及类型

### 1. DomainEventEnumerable — 领域事件零分配遍历器

- **文件**：`src/PalDDD.Core/DomainEventEnumerable.cs`
- **类型**：`ref struct DomainEventEnumerable` + `ref struct DomainEventEnumerator`
- **保留理由**：基于单链表的栈分配枚举器，零 GC 压力。ASP.NET Core Minimal API 中 `IEnumerable<DomainEvent>` 的 LINQ 扩展点依赖此枚举器。`ref struct` 不能在接口上表达，但提供零分配遍历路径。

### 2. IPalIdentity — 强类型 ID 标记接口

- **文件**：`src/PalDDD.Core/IPalIdentity.cs`
- **类型**：`IPalIdentity<T>` 泛型标记接口
- **保留理由**：源码生成器 (`IdentityGenerator`) 生成的 `record struct` 实现此接口，提供编译时类型安全。框架使用者可通过 `where T : IPalIdentity<TId>` 编写通用仓库/验证器。

### 3. SmartEnum — 强类型枚举基类

- **文件**：`src/PalDDD.Core/SmartEnum.cs`
- **类型**：`SmartEnum<TEnum, TValue>` 抽象基类
- **保留理由**：提供编译时安全的枚举替代方案（enum 无法附加行为/数据）。`FrozenDictionary` O(1) 查找。框架使用者派生此类型定义领域枚举。

### 4. PipelineBehaviors — 管道行为实现

- **文件**：`src/PalDDD.CQRS/PipelineBehaviors.cs`
- **类型**：`PalValidationPipelineBehavior` / `PalLoggingPipelineBehavior` / `PalDiagnosticPipelineBehavior`
- **保留理由**：`IPipelineBehavior` 接口的具体实现，提供验证/日志/诊断横切关注点。通过非泛型 DIM 桥接消除 `MakeGenericType`。

### 5. PipelineStateMachine — 管道状态机

- **文件**：`src/PalDDD.CQRS/PipelineStateMachine.cs`
- **类型**：`PipelineStateMachine` 结构体
- **保留理由**：替代 lambda 闭包链的可重用状态机。每请求分配从 N×72B（lambda 闭包）降为 ~40B（单一结构体）。

### 6. ExpectedStreamVersion — 事件流版本预期

- **文件**：`src/PalDDD.EventLog/ExpectedStreamVersion.cs`
- **类型**：`ExpectedStreamVersion` 结构体
- **保留理由**：事件日志乐观并发控制的核心抽象。`NoStream` / `Exact` / `Any` 三种语义封装在一个值类型中，零分配。

### 7. OutboxDomainEventInterceptor — EFCore 领域事件拦截器

- **文件**：`src/PalDDD.Repository.EFCore/OutboxDomainEventInterceptor.cs`
- **类型**：`OutboxDomainEventInterceptor`
- **保留理由**：EF Core `SaveChangesInterceptor` 实现，自动在 `SaveChangesAsync` 时收集领域事件并写入 Outbox。这是 DDD + EF Core + Outbox 模式的关键桥梁。
- **2026-06-28 更新**：`IPalEntity` 接口已移除——拦截器现在直接通过 `entry.Entity is Entity` 类型检查收集事件，消除了领域层的架构违规。

## 决策

**保留所有 7 个类型，不删除。**

框架库 API 面向外部使用者，早期无内部消费方是常态而非死代码信号。上述类型的保留论证基于：

1. 公共 API 文档定位明确
2. 长期演化路线图中有明确角色
3. 测试覆盖证实设计意图
4. 框架库本质：API 面向外部使用者

## 后果

- 各源文件的保留论证注释精简为一行 why + 指向本 ADR
- 未来如某个类型确实需要移除，走独立的废弃声明 + 迁移路径
