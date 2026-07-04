# Pal.DDD 架构精炼分析

> 分析 168 源文件 · 31 项目 · 逐文件判定必要性

---

## 一、可删除文件（11 个·内容移至 csproj）

| 文件 | 内容 | 移至 |
|------|------|------|
| `PalDDD.Core/AssemblyInfo.cs` | InternalsVisibleTo | csproj `<InternalsVisibleTo Include="PalDDD.Core.Tests"/>` |
| `PalDDD.CQRS/AssemblyInfo.cs` | InternalsVisibleTo ×2 | csproj |
| `PalDDD.EventLog/AssemblyInfo.cs` | InternalsVisibleTo ×2 | csproj |
| `PalDDD.Messaging/AssemblyInfo.cs` | InternalsVisibleTo ×2 | csproj |
| `Properties/AssemblyInfo.cs` | InternalsVisibleTo ×1 | csproj |
| `PalDDD.CQRS/GlobalUsings.cs` | global using | csproj `<Using Include="PalDDD.Core" Alias="Core"/>` |
| `PalDDD.DependencyInjection/GlobalUsings.cs` | global using | csproj |
| `PalDDD.Messaging/GlobalUsings.cs` | global using | csproj |
| `PalDDD.Hosting.AspNetCore/GlobalUsings.cs` | global using | csproj |
| `PalDDD.Repository.EFCore/GlobalUsings.cs` | global using | csproj |
| `PalDDD.Transactions/GlobalUsings.cs` | global using | csproj |

**收益**：11 个纯基础设施文件消失。InternalsVisibleTo 和 global using 归属 csproj（项目元数据），不属于源代码。

---

## 二、可合并文件（2 个·保持语义不变）

| 合并 | 理由 |
|------|------|
| `IValueObject.cs`(2行) → `ValueObject.cs` | 仅含 `public interface IValueObject { }`——标记接口，紧耦合于 ValueObject，独立文件无价值。ADR-003 保留的是**接口本身**，不是独立文件。 |
| `ValueTypes.cs` → `ValueObject.cs` | `Deleted`/`DeletedTime`/`UpdateTime`/`RowVersion` 均为 readonly record struct 值类型，与 ValueObject 同属"值对象"概念 |

**收益**：2 文件合并入 ValueObject.cs。减少文件碎片，不改任何 public API。

---

## 三、不可删除文件（155 个·逐类论证）

### 领域层 (14/16 保留·2 移除)

| 文件 | 判定 | 理由 |
|------|:--:|------|
| AggregateRoot.cs | ✅ | DDD 聚合根基类——框架核心抽象 |
| Entity.cs | ✅ | 实体基类 + 单链表事件存储 |
| DomainEvent.cs | ✅ | 领域事件基类 + TimeProvider |
| DomainEventEnumerable.cs | ✅ | ref struct 零分配枚举器 |
| IUnitOfWork.cs | ✅ | 事务抽象——Clean Architecture 依赖反转 |
| ISpecification.cs | ✅ | 规约模式 + ParameterReplacer |
| SmartEnum.cs | ✅ | 智能枚举 + 线程安全注册 |
| Attributes.cs | ✅ | SourceGen 契约标记 |
| PalDiagnostics.cs | ✅ | OTel ActivitySource + Meter |
| IPalIdentity.cs | ✅ | 强类型 ID 接口 |
| IPalValidator.cs | ✅ | 验证器接口 |
| Unit.cs | ✅ | Unit 类型（void 替代） |
| IValueObject.cs | ➡️ | 合并入 ValueObject.cs |
| ValueObject.cs | ✅ | 泛型数值值对象基类 |
| ValueTypes.cs | ➡️ | 合并入 ValueObject.cs |
| AssemblyInfo.cs | ❌ | 移至 csproj |

### CQRS 层 (9/11 保留·2 移除)

| CommandHandler.cs | ✅ | 命令处理器 DIM 桥接 |
| Dispatcher.cs | ✅ | 分发器——零反射核心 |
| IRequest.cs | ✅ | ICommand/IQuery 基接口 |
| PipelineBehavior.cs | ✅ | 管道行为接口 |
| PipelineBehaviors.cs | ✅ | 日志/验证行为实现 |
| PipelineStateMachine.cs | ✅ | 状态机替代闭包链 |
| QueryHandler.cs | ✅ | 查询处理器 DIM 桥接 |
| HandlerNotFoundException.cs | ✅ | 专用异常类型 |
| PalValidationException.cs | ✅ | 验证异常 |
| AssemblyInfo.cs | ❌ | 移至 csproj |
| GlobalUsings.cs | ❌ | 移至 csproj |

### 应用层 (保留全部·理由明确)

| 项目 | 文件 | 判定 | 理由 |
|------|------|:--:|------|
| EventLog | 8/9保留 | ✅ | IEventLog 抽象 + ExpectedStreamVersion 值对象 + InMemory 实现——事件溯源核心 |
| EventLog | AssemblyInfo | ❌ | 移至 csproj |
| Messaging | 6/7保留 | ✅ | IMessageBroker 抽象 + DomainEventDispatcher + MessageBrokerBase |
| Messaging | AssemblyInfo/GlobalUsings | ❌ | 移至 csproj |
| Serialization | 7/7保留 | ✅ | IMessageSerializer + Json + MessageCatalog——AOT 序列化核心 |
| Serialization.Evolution | 10/10保留 | ✅ | 消息版本升级链——分布式系统核心 |
| Transactions | 20/22保留 | ✅ | Outbox/Inbox/Saga/PeriodicBackground——框架价值核心 |
| Transactions | GlobalUsings | ❌ | 移至 csproj |
| Projections | 10/10保留 | ✅ | 读模型投影——CQRS 核心 |
| Idempotency | 6/6保留 | ✅ | API 幂等——分布式系统核心 |

### 基础设施适配器 (全部保留·Clean Architecture 依赖反转必须)

| 项目 | 文件数 | 判定 | 理由 |
|------|:-----:|:--:|------|
| *.Dapper | 7 项目·各 1-11 | ✅ | Dapper AOT 路径适配器 |
| *.EFCore | 5 项目·各 1-7 | ✅ | EF Core 功能路径适配器 |
| Kafka/RabbitMQ | 2 项目·各 1 | ✅ | Broker 适配器 |
| MemoryPack | 2 文件 | ✅ | 可选序列化适配器 |
| Hosting.AspNetCore | 4/6保留 | ✅ | 异常中间件+健康检查 |
| Hosting | GlobalUsings | ❌ | 移至 csproj |
| DI | 2/2 | ✅ | 组合根 |
| DependencyInjection | GlobalUsings | ❌ | 移至 csproj |

### 分析器+源生成器 (全部保留·AOT 编译期治理必须)

| 项目 | 文件 | 判定 | 理由 |
|------|------|:--:|------|
| Analyzers | StrategicDddAnalyzer.cs | ✅ | PDDD001-015 编译期治理 |
| Analyzers.CodeFixes | StrategicDddCodeFixProvider.cs | ✅ | IDE 自动修复 |
| Core.SourceGen | 3 生成器+Polyfills | ✅ | Identity/Enum/MessageRegistry 源生成 |

---

## 四、项目级别不可合并

| 项目 | 判定 | 理由 |
|------|:--:|------|
| Dapper 三方言 (PG/MySQL/SQLite) | ✅ 独立 | ADR-012——按需引用，避免拉入无关方言 |
| EFCore 适配器 (5 个) | ✅ 独立 | 每个适配不同存储——EventLog/Idempotency/Projections/Repository/Transactions |
| Kafka/RabbitMQ | ✅ 独立 | 消费者按需引用 Broker |
| MemoryPack | ✅ 独立 | 可选序列化适配器 |

---

## 五、总结

| 操作 | 数量 | 收益 |
|------|:---:|------|
| **删除**(移至csproj) | 11 文件 | 5 AssemblyInfo + 6 GlobalUsings 消失 |
| **合并**(保留内容) | 2 文件 → 1 | IValueObject + ValueTypes → ValueObject.cs |
| **保留** | 155 文件 | 全部有不可替代的架构价值 |
| **净减少** | 168→155 | **-13 文件 (-7.7%)** |

**不可再减的边界**：155 个保留文件中的每一个都承载独立的 DDD/Clean Architecture 职责——聚合根、值对象、领域事件、仓储抽象、应用服务、适配器、源生成器。进一步合并会破坏"接口隔离"和"依赖反转"原则。
