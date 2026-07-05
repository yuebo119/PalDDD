# Pal.DDD

**面向 .NET 11 的 DDD/CQRS/Event Sourcing 基础设施框架 —— 零运行时反射、Native AOT 链路完整、无过度抽象。**

[![NuGet](https://img.shields.io/badge/nuget-v1.0.0--preview.1-blue)](https://www.nuget.org/packages/PalDDD.Base)
[![.NET](https://img.shields.io/badge/.NET-11.0-purple)](https://dotnet.microsoft.com/)
[![CI](https://img.shields.io/badge/build-0_errors_0_warnings-brightgreen)]()
[![AOT](https://img.shields.io/badge/Native_AOT-✅_Core_+_Dapper-green)](docs/aot.md)
[![License](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue)](LICENSE)

---

Pal.DDD 将 Entity 的 equality 语义、领域事件的零分配收集、Outbox 的租约锁并发与死信恢复、Saga 的补偿编排与超时检测——标准化为 30 个独立 NuGet 包。不做 `IRepository<T>`、不定义 `IIntegrationEvent`、不实施装配扫描。业务代码保持纯 C#，框架只提供基础设施。

开箱即用：**零反射命令分发 · 租约锁并发 Outbox · 自动补偿 Saga · 不可变 EventLog · 断点续传 Projection · 编译时 DDD 合规检查。**

---

---

## 核心价值

### DDD 战术模式完整落地

Entity / AggregateRoot / DomainEvent / ValueObject / SmartEnum / Specification / Saga / EventLog / Projection 全覆盖，且无过度抽象——不做 `IRepository<T>`、不定义 `IIntegrationEvent`、不实施装配扫描。

DbContext *是* 工作单元+仓储。DomainEvent *是* 集成事件。`AddPalCommandHandler<T>` 替代装配扫描。框架不应发明概念来包装已有概念——它应该消除重复，而非增加间接层。

### AOT 作为一等公民

DIM 桥接消除反射、源码生成器注册类型、FrozenDictionary 替代字典查找、非 AOT 项目三属性（`IsAotCompatible` / `IsTrimmable` / `VerifyReferenceAotCompatibility`）透明化。

`IsAotCompatible=true` 在核心层和 Dapper 适配层强制执行。非 AOT 安全的第三方依赖（EF Core、Kafka、RabbitMQ）被隔离在显式声明 `IsAotCompatible=false` 的适配器项目中。AOT 不是附加功能——它是启动延迟、内存占用和部署安全性的架构决策。

### 性能契约工程化

- **零分配快速路径**：`ValueTask` + `IsCompletedSuccessfully` 同步完成零堆分配
- **零闭包管道**：`PipelineStateMachine` 替代闭包链，每次请求仅 ~40B
- **零拷贝读取**：`RehydrateFromBytes` 引用赋值消除 2 次 `ToArray`
- **ref struct 枚举器**：`DomainEventEnumerable` 单链表 O(1) 追加，foreach 零分配

### 架构约束编译时执行

15 条 Roslyn 分析器规则（PDDD001-015）在编译阶段检查领域模型的合规性。DomainEvent 未声明 sealed → 编译错误。ProcessManager 缺少 `[BoundedContext]` → 编译错误。消息契约命名不符合 lowercase-kebab 规范 → 编译警告。约束不依赖文档纪律或 Code Review 记忆——编译器替代了这两者。

---

## 与现有方案的差异

| 方案 | 定位 | Pal.DDD 的增量 |
|------|------|:---------------|
| **MediatR** | 进程内命令/查询分发 | 增加 Outbox、Inbox、Saga、EventLog、Projection。分发是起点，不是终点。 |
| **MassTransit / NServiceBus** | 分布式消息总线 | 不绑定特定传输。Outbox 通过 `IMessageBroker` 抽象适配任意 Broker。消息所有权在应用侧。 |
| **EventStoreDB / Marten** | 事件存储 | 提供 `IEventLog` 抽象，存储层可替换为 Dapper 或 EF Core 实现。不锁定供应商。 |
| **手写 DDD** | 完全定制 | 消除每个项目中 Entity、DomainEvent、Dispatcher、Outbox、Saga 的重复实现。基础设施不应成为差异化代码。 |

---

## 安装

```bash
# 基础层：领域基元 + 序列化 + 压缩 + 编译时分析器
dotnet add package PalDDD.Base

# 扩展层：CQRS + EventLog + Outbox/Inbox/Saga + Projections + DI 装配
dotnet add package PalDDD.Extension

# Dapper 持久化 — AOT 兼容
dotnet add package PalDDD.Dapper
dotnet add package PalDDD.Dapper.PostgreSql

# EF Core 持久化 — 功能完整，复杂查询场景
dotnet add package PalDDD.EntityFrameworkCore

# 消息代理 — 可选，InMemory 实现覆盖全部接口
dotnet add package PalDDD.Messaging.Kafka
dotnet add package PalDDD.Messaging.RabbitMQ
```

InMemory 实现覆盖全部抽象接口，单元测试和原型开发无需外部依赖。

---

## 快速开始

### 领域模型

```csharp
using PalDDD.Core;

[GenerateId(typeof(PalUlid))] public readonly partial record struct OrderId;

public sealed class Order : AggregateRoot<OrderId>
{
    public void Submit(string name, decimal amount)
        => RaiseEvent(new OrderSubmitted(Id.Value, name, amount));
}

[GenerateMessage(Name = "ordering.order-submitted.v1")]
public sealed record OrderSubmitted(
    PalUlid OrderId, string Name, decimal Amount
) : DomainEvent, IDomainEvent
{
    static string IDomainEvent.EventName => "ordering.order-submitted.v1";
}
```

### 命令处理器

```csharp
using PalDDD.CQRS;

public sealed record SubmitOrder(OrderId Id, string Name, decimal Amount) : ICommand;

public sealed class SubmitOrderHandler : ICommandHandler<SubmitOrder, Unit>
{
    public async ValueTask<Unit> HandleAsync(SubmitOrder cmd, CancellationToken ct)
    {
        var order = new Order();
        order.Submit(cmd.Name, cmd.Amount);
        return Unit.Value;
    }
}
```

### 注册与分发

```csharp
services.AddPalCoreStack();
services.AddPalCommandHandler<SubmitOrder, Unit, SubmitOrderHandler>();
services.AddPalDapper(DapperDbType.PostgreSql, connectionString);
services.AddPalOutbox();

var dispatcher = provider.GetRequiredService<Dispatcher>();
await dispatcher.SendAsync(new SubmitOrder(new OrderId(), "Customer", 100m));
```

---

## 功能矩阵

### 领域建模
| 组件 | 实现策略 |
|------|---------|
| Entity / AggregateRoot | 单链表事件存储，支持零分配 `foreach` 枚举，线程安全的事件收集 |
| DomainEvent | 不可变 sealed record，静态 `EventName` 契约，`[GenerateMessage]` 源生成注册 |
| ValueObject / SmartEnum | 强类型 ID（Ulid 推荐），FrozenDictionary O(1) 查找 |
| ISpecification | ExpressionVisitor 参数替换组合 And/Or/Not，与 EF Core LINQ 完全兼容 |
| 诊断 | 内建 `PalActivitySource`（11 个 Start 方法）+ `PalMetrics`（24 个计数器） |

### CQRS
| 组件 | 实现策略 |
|------|---------|
| Dispatcher | FrozenDictionary 路由表，`IHandler.HandleAsync` DIM 桥接，零 MakeGenericType |
| PipelineBehavior | 开放泛型注册，内建 ValidationBehavior + LoggingBehavior |
| Handler 注册 | `AddPalCommandHandler<T>` 编译时类型常量，无装配扫描 |

### 消息基础设施
| 组件 | 核心机制 |
|------|---------|
| **Outbox** | 数据库事务内原子写入消息行，租约锁（LockedBy + LockedUntil）多实例并发发布，指数退避重试，死信队列 + 操作重注入 |
| **Inbox** | `(ConsumerName, MessageId)` 复合唯一约束，四态生命周期（Pending → Processing → Processed/Failed），僵尸记录超时回收 |
| **Saga** | 显式状态/事件转换注册 → FrozenDictionary 查找，可配置重试+退避，Backward/Forward/None 三种补偿策略，超时检测后台服务，人工审批中断+恢复 |
| **EventLog** | 命名流 + 乐观并发（ExpectedStreamVersion），全局单调递增位置，`RehydrateFromBytes` 零拷贝读取路径 |
| **Projection** | `IProjectionCheckpointStore` 断点存储，`EventLogReplaySource<T>` 全量重放，独立于存储适配器 |

### 持久化适配器
| 适配器 | AOT | 数据库 | 覆盖范围 |
|--------|:--:|:--:|------|
| PalDDD.Dapper | ✅ | PG / MySQL / SQLite | Outbox / Inbox / Saga / EventLog / Projection / UnitOfWork |
| PalDDD.EntityFrameworkCore | ❌ | PG / MySQL / SQLite | 同上 + Hi/Lo 位置分配器 |

### 数据库方言扩展
| 方言 | 特有能力 |
|------|---------|
| PostgreSQL | COPY 批量写入、Pipeline 单往返批处理、LISTEN/NOTIFY 事件推送、一致性哈希分片、JSONB 操作符、软删除、审计日志 |
| MySQL | 多主机故障转移（FailOver/RoundRobin/LeastConnections）、InnoDB 会话调优（锁超时、隔离级别、SQL 模式） |
| SQLite | WAL 模式 + PRAGMA 优化（三级调优）、FTS5 全文搜索、JSON1 函数 |

---

## AOT 兼容性

| 层 | 状态 | 说明 |
|----|:--:|------|
| PalDDD.Core · Serialization · Compression | ✅ | `IsAotCompatible=true` 全局继承 |
| PalDDD.CQRS · EventLog · Messaging · Transactions · Projections · DI | ✅ | 同上 |
| PalDDD.Dapper + PostgreSql / MySql / Sqlite | ✅ | Dapper.AOT 源生成器接入 |
| PalDDD.EntityFrameworkCore | ❌ | EF Core 运行时限制 |
| PalDDD.Messaging.Kafka · RabbitMQ | ❌ | Confluent.Kafka / RabbitMQ.Client 限制 |
| PalDDD.Hosting.AspNetCore | ❌ | FrameworkReference 限制 |

详见 [AOT 指南](docs/aot.md)。

---

## 性能指标

> ⚠️ 以下为 `--smoke` 烟测数据（Stopwatch + GC 分配，单次运行），非正式 BenchmarkDotNet 报告。BenchmarkDotNet 在当前 .NET 11 Preview 工具链下存在兼容问题，正式基准报告待 BDN 发布兼容版本后补充。烟测用于趋势检查，不能替代统计严谨的基准测试。

| 操作 | 次数 | 耗时 | 分配 |
|------|:--:|------|:--:|
| PalValidationResult.Success | 1M | 15.06 ms | 88 B |
| SmartEnum.FromValue（FrozenDictionary） | 1M | 19.01 ms | 40 B |
| PalValidationResult.Failed | 1M | 43.41 ms | ~40 MB |
| Entity.RaiseEvent（单链表追加） | 1M | 148.45 ms | ~128 MB |

验证命令：
```bash
dotnet run --configuration Release --project bench/PalDDD.Benchmarks -- --smoke
```

完整数据及 BenchmarkDotNet 历史基线见 [性能记录](docs/performance.md)。

---

## 项目结构

```
src/                         30 源项目 · Clean Architecture
├── Domain/                  Core · SourceGen · Analyzers
├── App-Abstractions/        Serialization · Messaging · Compression
├── App-Core/                CQRS · EventLog · Idempotency · Projections · Transactions
├── Infra-Dapper/            Dapper · PostgreSql · MySql · Sqlite
├── Infra-EFCore/            EF Core
├── Infra-Messaging/         Kafka · RabbitMQ
├── Hosting/                 DI · AspNetCore
└── Metapackages/            Base · Extension · Prompts

test/                        15 测试项目（TUnit）
bench/                       BenchmarkDotNet 性能基准
samples/                     AOT / ECommerce / MinimalApi 示例
docs/                        架构 · 使用指南 · 教程 · ADR
```

依赖方向：Domain → App → Infra → Hosting。每个 src/ 项目对应一个独立 NuGet 包。

```mermaid
flowchart TB
    Core --> CQRS
    Core --> EventLog
    Core --> Idempotency
    Core --> Projections
    Serialization --> Messaging
    Core --> Messaging
    Messaging --> Transactions
    CQRS --> DI[Messaging + DI]
    Messaging --> DI
    Transactions --> Dapper
    EventLog --> Dapper
    Projections --> Dapper
    Transactions --> EF[EF Core]
    Dapper --> PG[PostgreSql]
    Dapper --> MySQL
    Dapper --> SQLite
```


---

## 文档

| 文档 | 说明 |
|------|------|
| [架构说明](docs/architecture.md) | 分层、依赖方向、项目职责 |
| [使用指南](docs/usage.md) | 各组件完整代码示例 |
| [教程](docs/tutorial.md) | 从零构建 DDD 应用 |
| [工程规范](docs/conventions.md) | 命名、文件组织、DI、AOT |
| [AOT 指南](docs/aot.md) | Native AOT 规则与检查清单 |
| [性能记录](docs/performance.md) | 基准测试数据 |
| [架构决策](docs/decisions/) | 16 份 ADR |

---

## FAQ

**和 MediatR 什么关系？**
MediatR 是进程内命令分发器。Pal.DDD 内置与之等价的 Dispatcher + PipelineBehavior，并在此基础上提供 Outbox、Inbox、Saga、EventLog、Projection。如果你只需要命令分发，Pal.DDD 的 CQRS 层可以替代 MediatR。如果你还需要可靠消息投递和 Saga 编排，Pal.DDD 提供整条链路。

**和 MassTransit 什么关系？**
MassTransit 是分布式消息总线，绑定特定传输（RabbitMQ/Azure Service Bus/Amazon SQS）。Pal.DDD 的 Outbox 通过 `IMessageBroker` 抽象适配任意 Broker——你可以注入 MassTransit、Raw RabbitMQ、Kafka 或 InMemory 实现。框架不绑定传输。

**和 EF Core 什么关系？共存还是替代？**
共存。Pal.DDD 不替代 EF Core——两者解决不同层次的问题。EF Core 负责对象-关系映射和查询；Pal.DDD 负责 DDD 战术模式（Entity、DomainEvent、CQRS 分发、Outbox 投递、Saga 编排）。Pal.DDD 提供 Dapper 和 EF Core 两套持久化适配器，选型取决于你的 AOT 需求和查询复杂度。

**可以用在现有项目中吗？渐进式引入？**
可以。Pal.DDD 的每个 NuGet 包独立可安装。你可以从 `PalDDD.Base`（领域基元）开始，在现有的 Service 层旁边逐步引入 CQRS Dispatcher，再按需添加 Outbox 或 Saga。不需要一次性重写整个项目。

**为什么要单目标 net11.0？**
依赖 .NET 11 的静态特性（JsonSerializerContext 源生成增强、Runtime Async 状态机优化、新 AOT 分析器），多目标在技术上不可行。详见 [ADR-005](docs/decisions/005-net11-single-target.md)。

**Dapper 和 EF Core 怎么选？**
如果需要 Native AOT 部署（微服务、CLI 工具、边缘计算）→ 选 Dapper。如果需要复杂查询（多表 Join、GroupBy、投影）、已有 EF Core 迁移代码或 DbContext 生态 → 选 EF Core。两者可以在同一个项目中混用——例如 Dapper 做写路径（Outbox/Saga），EF Core 做读路径（Projection）。

**有哪些已知限制？**
不支持 .NET 8/9/10（单目标 net11.0）。Saga 的 ChildSaga 和 DynamicStep 依赖 `MakeGenericType`，在 AOT 发布时不可用（标注了 `[RequiresDynamicCode]`）。不含内置的 EventStore 快照机制——需要快照策略的项目需要自行实现。

**生产环境有谁在用？**
Pal.DDD 处于 v1.0.0-preview 阶段，尚未公开发布 NuGet 包。核心层（Entity、DomainEvent、CQRS Dispatcher、Outbox、Inbox）在多个内部项目的集成测试套件中验证通过，测试覆盖 750+ 用例。欢迎在非生产环境中试用并反馈。

---

## 许可证

[GNU Affero General Public License v3.0 or later](LICENSE)

Copyright (C) 2026 PalDDD

本项目使用 AGPL-3.0-or-later 许可证。AGPL v3 在 GPL v3 基础上增加第 13 条网络交互条款——通过网络提供服务时，必须向用户提供修改后版本的完整源代码。详见 [LICENSE](LICENSE) 文件或 <https://www.gnu.org/licenses/agpl-3.0.html>。
