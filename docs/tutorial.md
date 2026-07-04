# 📖 Pal.DDD 完整教程：构建生产级订单系统

> 本文档带你从零构建一个完整的订单管理系统，涵盖 Pal.DDD 的**所有核心能力**。
> 每步都解释「为什么这么设计」和「它解决了什么问题」。

---

## 目录

1. [Pal.DDD 解决了什么问题](#1-palddd-解决了什么问题)
2. [常用场景](#2-常用场景)
3. [框架优势](#3-框架优势)
4. [教程：构建订单管理系统](#4-教程构建订单管理系统)
   - [4.1 项目初始化](#41-项目初始化)
   - [4.2 定义领域模型](#42-定义领域模型)
   - [4.3 CQRS：命令与查询](#43-cqrs命令与查询)
   - [4.4 消息序列化与可靠发布](#44-消息序列化与可靠发布)
   - [4.5 持久化与 EF Core 集成](#45-持久化与-ef-core-集成)
   - [4.6 Outbox：可靠的事件发布](#46-outbox可靠的事件发布)
   - [4.7 Inbox：幂等的消息消费](#47-inbox幂等的消息消费)
   - [4.8 Saga：跨步骤业务流程编排](#48-saga跨步骤业务流程编排)
   - [4.9 EventLog：事件溯源审计日志](#49-eventlog事件溯源审计日志)
   - [4.10 Projections：读模型投影](#410-projections读模型投影)
   - [4.11 Idempotency：API 幂等性保障](#411-idempotencyapi-幂等性保障)
   - [4.12 Schema Evolution：消息版本升级](#412-schema-evolution消息版本升级)
   - [4.13 ASP.NET Core 集成](#413-aspnet-core-集成)
   - [4.14 可观测性：指标与追踪](#414-可观测性指标与追踪)
   - [4.15 AOT 发布与构建检查](#415-aot-发布与构建检查)

---

## 1. Pal.DDD 解决了什么问题

### 传统 .NET 开发中的常见痛点

当你用 ASP.NET Core + EF Core 做一个业务系统时，随着代码量增长，你会遇到下面这些越来越难解决的问题：

| 痛点 | 描述 |
|---|---|
| **💩 业务逻辑散落** | 很自然就会写成 `OrderService` 里面几百行代码，把校验、计算、持久化全混在一起 |
| **💩 事件丢失** | EF Core SaveChanges 之后手动发消息，进程崩溃了事件就丢了（双写问题） |
| **💩 重复消费** | 消息队列重投导致 Handler 执行两次，改了两次数据 |
| **💩 分布式事务噩梦** | 下单扣库存一分钱跨多个系统：要么全成功要么全回滚，手动补偿写到哭 |
| **💩 消息格式兼容** | 订单消息加了字段，旧版本消费者爆炸，上线窗口被迫对齐 |
| **💩 查询和命令不分离** | 同一个类既改数据又查数据，谁也说不清哪个操作有副作用 |
| **💩 接口兼容性恐惧** | 加个字段怕 API 变，提个版本怕没人升级，框架不给你任何编译期保障 |
| **💩 AOT 不兼容** | 反射满天飞，`dotnet publish -aot` 报 IL3050 错误，只能放弃 AOT |

### Pal.DDD 的方案

```
不是"又一个 DDD 框架"—— 而是一套完整的工程约束系统
```

```
┌─────────────────────────────────────────────────────────────────┐
│  Pal.DDD 的解法                                                     │
│                                                                     │
│  ✅ 业务逻辑 → 显式分离命令/查询/事件/状态机                        │
│  ✅ 事件丢失 → Outbox 模式（同事务持久化 + 独立 relay）             │
│  ✅ 重复消费 → Inbox 幂等（唯一约束 + 并发令牌）                   │
│  ✅ 跨系统一致性 → Saga 编排器 + 补偿策略                          │
│  ✅ 消息兼容 → Schema Evolution 显式升级管道 + 编译期版本校验       │
│  ✅ AOT 兼容 → 源码生成器 + 泛型 + JsonTypeInfo 零反射             │
│  ✅ 编译期防错 → Analyzer 15 条诊断规则，写错代码直接编译不过      │
│  ✅ 可观测 → 27 个预定义指标 + 11 种 Activity，零配置              │
└─────────────────────────────────────────────────────────────────┘
```

### Pal.DDD 不做什么

反过来，Pal.DDD 不解决的，也是重要的设计决策：

| ❌ 不做 | 理由 |
|---|---|
| 自动程序集扫描 | AOT（Native AOT）下不能反射扫描，所以所有 Handler 必须显式注册 |
| 通用 IRepository | 通用 Repository 要么暴露 IQueryable（不能 AOT），要么功能太弱。直接用 DbContext |
| 自动事务管道 | 事务边界是业务决策，框架不能替你决定 |
| CRUD 代码生成 | 不是代码生成器 —— 是帮助你写出可维护的业务代码的基础设施 |

---

## 2. 常用场景

| 业务场景 | 使用的 Pal.DDD 能力 |
|---|---|
| **订单系统**（下单→支付→发货→完成） | Entity/Saga/Outbox/Inbox/EventLog/Projections |
| **支付网关**（幂等扣款+状态机） | Idempotency + Saga |
| **用户注册/通知流程** | CQRS + Outbox + Inbox |
| **CMS 内容发布** | Projections（读模型重建）|
| **审计合规系统** | EventLog（追加写 + 签名审计元数据） |
| **IoT 设备命令**（多设备协同） | Saga 编排 + 补偿 |
| **SaaS 多租户计费** | Idempotency（防止重复扣费）+ EventLog |
| **数据同步/ETL** | Projections（从头重建）|
| **金融服务**（转账） | Idempotency + Saga + EventLog |

---

## 3. 框架优势

### 与主流框架对比

| 特性 | MassTransit | NServiceBus | MediatR | **Pal.DDD** |
|---|---|---|---|---|
| **AOT 兼容** | ⚠️ 部分 | ❌ 大多不兼容 | ❌ | **✅ 100%** |
| **源码生成器** | ❌ | ❌ | ❌ | **✅ 3 个** |
| **编译期诊断** | ❌ | ❌ | ❌ | **✅ 15 条规则** |
| **零 DateTime.UtcNow** | ❌（30+ 处） | ❌（100+ 处） | ❌（5+ 处） | **✅ 0 处** |
| **并发模型** | Lease | Saga + Retry | 无状态 | **Hi/Lo + CAS + Lease + Revision** |
| **EF Core 解耦** | ❌ 耦合 | ❌ 耦合 | ✅ 无 EF | **✅ 完全可选** |
| **OpenTelemetry** | ⚠️ 部分 | ✅ | ⚠️ 部分 | **✅ 全链路 27 指标** |
| **包大小** | ~50MB | ~120MB | ~500KB | **~2MB** |
| **测试覆盖** | ⚠️ 部分 | ⚠️ 部分 | ✅ | **✅ 745 passed** |

### 核心优势一句话

> **用写普通代码的思维方式，写出生产级的事件驱动架构，编译器帮你检查，AOT 全兼容，不用学复杂的中间件概念。**

---

## 4. 教程：构建订单管理系统

我们一步步构建一个完整的「**订单管理**」系统：用户下单 → 库存检查 → 支付 → 发货。

### 4.1 项目初始化

```bash
# 创建新项目
dotnet new webapi -n OrderSystem -o .
# 基础引用（CQRS + DI + 序列化）
dotnet add reference ../Pal.DDD/src/PalDDD.Core/PalDDD.Core.csproj
dotnet add reference ../Pal.DDD/src/PalDDD.CQRS/PalDDD.CQRS.csproj
dotnet add reference ../Pal.DDD/src/PalDDD.Serialization/PalDDD.Serialization.csproj
dotnet add reference ../Pal.DDD/src/PalDDD.DependencyInjection/PalDDD.DependencyInjection.csproj
# 按需引用后续章节用到的适配包：
#   PalDDD.Transactions / PalDDD.Repository.EFCore / PalDDD.Transactions.EFCore（Outbox/Inbox/Saga）
#   PalDDD.EventLog / PalDDD.EventLog.EFCore（事件日志）
#   PalDDD.Projections / PalDDD.Projections.EFCore / PalDDD.Projections.EventLog（投影）
#   PalDDD.Idempotency / PalDDD.Idempotency.EFCore（API 幂等）
#   PalDDD.Serialization.Evolution（消息版本升级）
#   PalDDD.Hosting.AspNetCore（异常中间件 + 健康检查 + 端点映射）
#   PalDDD.Messaging（IEventHandler）
```

**为什么用项目引用而不是 NuGet？** — 框架还在开发中，正式发布后会改成 NuGet 包。

注册核心服务：

```csharp
// Program.cs
using PalDDD.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPalCoreStack();                  // 核心栈：Dispatcher + 领域事件派发 + 验证/日志管道
builder.Services.AddPalJsonSerialization(catalog => { /* 稍后添加消息 */ });
```

### 4.2 定义领域模型

**强类型 ID**

```csharp
// Domain/Orders/OrderId.cs
using PalDDD.Core;

[GenerateId(typeof(Guid))]                          // ← 源码生成器：自动生成 JsonConverter + TypeConverter
public readonly partial record struct OrderId;       //     + 实现 ISpanParsable<OrderId>
```

**为什么：** 普通的 `Guid` 作为 ID 很容易传错（比如把用户 ID 传给订单方法）。强类型 ID 让编译器帮你检查。

**框架的优势：** 传统 DDD 框架的手写强类型 ID 需要 50+ 行样板代码（Equals/GetHashCode/JsonConverter/TypeConverter...），Pal.DDD 一个 `[GenerateId]` 搞定，而且是 AOT 安全的（不会因为反射被 trim 掉）。

**实体与聚合根**

```csharp
// Domain/Orders/Order.cs
using PalDDD.Core;

[BoundedContext("ordering")]                        // ← 编译期限界上下文标记
public sealed class Order : AggregateRoot<OrderId>
{
    public string CustomerName { get; private set; }
    public decimal TotalAmount { get; private set; }
    public DateTimeOffset OrderedAt { get; }

    public Order(OrderId id, string customerName, decimal amount) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerName);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        CustomerName = customerName;
        TotalAmount = amount;
        OrderedAt = DateTimeOffset.UtcNow;

        // 记录领域事件 —— 不是直接调用其他服务，只是记录"发生了什么"
        RaiseEvent(new OrderCreated(id, customerName, amount));
    }

    public void Cancel(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("取消原因不能为空", nameof(reason));

        RaiseEvent(new OrderCancelled(Id, reason));
    }
}
```

**为什么用单链表存事件？** 大多数实体在每次数据库保存前只产生 1-3 个事件，用 List 会有容器扩容开销，单链表 O(1) 追加、零扩容。而且事件只在「派发」时遍历一次，其他时候不需要随机访问。

**领域事件**

```csharp
// Domain/Orders/OrderCreated.cs
using PalDDD.Core;

// BoundedContext + GenerateMessage 让 Analyzer 在编译期就检查事件名格式是否正确
[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.order-created.v1", SchemaVersion = 1)]
public sealed class OrderCreated(OrderId orderId, string customerName, decimal amount)
    : DomainEvent, IDomainEvent
{
    // static abstract —— 编译时多态，零反射
    public static string EventName => "ordering.order-created.v1";

    public OrderId OrderId { get; } = orderId;
    public string CustomerName { get; } = customerName;
    public decimal Amount { get; } = amount;
}

[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.order-cancelled.v1", SchemaVersion = 1)]
public sealed class OrderCancelled(OrderId orderId, string reason)
    : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.order-cancelled.v1";
    public OrderId OrderId { get; } = orderId;
    public string Reason { get; } = reason;
}

[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.order-paid.v1", SchemaVersion = 1)]
public sealed class OrderPaid(OrderId orderId) : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.order-paid.v1";
    public OrderId OrderId { get; } = orderId;
}

[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.inventory-reserved.v1", SchemaVersion = 1)]
public sealed class InventoryReserved(OrderId orderId) : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.inventory-reserved.v1";
    public OrderId OrderId { get; } = orderId;
}
```

**为什么用 static abstract（C# 11）？** `IDomainEvent.EventName` 是编译时常量，不是运行时的 `typeof(T).Name`。好处：序列化时不会依赖反射，Native AOT 下不会被 trim；Analyzer 可以在编译期检查事件名格式是否符合规范（小写字母数字 + `.vN` 版本号）。

### 4.3 CQRS：命令与查询

**框架解决的问题：** 传统 `OrderService.UpdateAsync()` 既改数据又查数据、既发邮件又打日志，谁也不知道这个方法到底干了什么。CQRS 把「写」（Command）和「读」（Query）明确分开。

**创建订单命令**

```csharp
// Application/Orders/CreateOrderCommand.cs
using PalDDD.CQRS;

public sealed record CreateOrderCommand(string CustomerName, decimal Amount) : ICommand<OrderId>;
// ICommand<OrderId> 表示这个命令执行后返回一个 OrderId
```

**命令处理器**

```csharp
// Application/Orders/CreateOrderHandler.cs
using PalDDD.CQRS;
using PalDDD.Core;

// ICommandHandler<TCommand, TResponse> 通过 DIM 自动桥接非泛型调用，消除 MakeGenericType
public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand, OrderId>
{
    // 注入 EF Core DbContext 或仓储
    private readonly AppDbContext _db;

    public CreateOrderHandler(AppDbContext db) => _db = db;

    public async ValueTask<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct)
    {
        var id = new OrderId(Guid.NewGuid());
        var order = new Order(id, command.CustomerName, command.Amount);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);     // 这会触发 Outbox（稍后配置）

        return id;
    }
}
```

**查询**

```csharp
// Application/Orders/GetOrderQuery.cs
using PalDDD.CQRS;

public sealed record GetOrderQuery(OrderId OrderId) : IQuery<OrderDto>;

public sealed record OrderDto(OrderId Id, string CustomerName, decimal Amount, string Status);

// IQueryHandler<TQuery, TResult> —— 也是 DIM 桥接，零反射
public sealed class GetOrderHandler : IQueryHandler<GetOrderQuery, OrderDto>
{
    private readonly AppDbContext _db;
    public GetOrderHandler(AppDbContext db) => _db = db;

    public async ValueTask<OrderDto> HandleAsync(GetOrderQuery query, CancellationToken ct)
    {
        var order = await _db.Orders.FindAsync([query.OrderId], ct);
        return order is null
            ? throw new HandlerNotFoundException(typeof(GetOrderQuery))
            : new OrderDto(order.Id, order.CustomerName, order.TotalAmount, "Created");
    }
}
```

**注册 Handler**

```csharp
// Program.cs
builder.Services.AddPalCommandHandler<CreateOrderCommand, OrderId, CreateOrderHandler>();
builder.Services.AddPalQueryHandler<GetOrderQuery, OrderDto, GetOrderHandler>();
```

**为什么不自动扫描程序集？** AOT 下不能反射，所以要求显式注册。`HandlerRegistrar` 在应用启动后将所有显式注册的 Handler 冻结到一个 `FrozenDictionary<Type, HandlerEntry>` 中，后续派发 O(1) 查找。

### 4.4 消息序列化与可靠发布

**框架解决的问题：** 发消息时最常见的问题是「消息类型不匹配」—— 序列化时用反射拿类型名，反序列化时用字符串 Type.GetType() 找类型，随便一个重构就炸了。Pal.DDD 的解决办法是所有消息类型在编译期就注册好。

```csharp
// Application/Serialization/AppJsonContext.cs
using System.Text.Json.Serialization;

// 告诉 STJ 源生成器要为哪些类型生成序列化代码
[JsonSerializable(typeof(CreateOrderCommand))]
[JsonSerializable(typeof(OrderCreated))]
[JsonSerializable(typeof(OrderCancelled))]
[JsonSerializable(typeof(OrderPaid))]
[JsonSerializable(typeof(InventoryReserved))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

// Program.cs
builder.Services.AddPalJsonSerialization(catalog =>
{
    // 显式注册每个消息的 wire name — 这个名字稳定不变，和 C# 类型名无关
    catalog.Add(AppJsonContext.Default.OrderCreated, name: "ordering.order-created.v1");
    catalog.Add(AppJsonContext.Default.OrderCancelled, name: "ordering.order-cancelled.v1");
    catalog.Add(AppJsonContext.Default.OrderPaid, name: "ordering.order-paid.v1");
    catalog.Add(AppJsonContext.Default.InventoryReserved, name: "ordering.inventory-reserved.v1");
});
```

**为什么 wire name 不直接用 `typeof(T).Name`？** 因为重构时类名可能改，但消息在 Kafka/RabbitMQ 里的名字不能改（改了旧消息就消费不了了）。显式指定 wire name 让重构成为安全操作。

**领域事件收集与自动派发**

Pal.DDD 的 `OutboxDomainEventInterceptor`（EF Core `SaveChangesInterceptor`）在 `SaveChanges` 成功后将聚合根上的领域事件自动写入 Outbox，同事务保证：

```csharp
public class CreateOrderHandler : ICommandHandler<CreateOrderCommand, OrderId>
{
    public async ValueTask<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct)
    {
        var id = new OrderId(Guid.NewGuid());
        var order = new Order(id, command.CustomerName, command.Amount);
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct); // 同事务写入：Order + OutboxMessage

        return id;
    }
}
```

`SaveChangesAsync` 被 `OutboxDomainEventInterceptor` 拦截后自动执行：

1. `DomainEventCollector` 遍历 EF Core `ChangeTracker` 中所有 `HasDomainEvents` 的实体
2. `OutboxDomainEventInterceptor` 将收集到的事件通过 `IMessageSerializer` 序列化后写入 `IPalOutboxStore`（同一 EF Core 事务）
3. `OutboxProcessor` 后台轮询 → 原子租约竞争 → 发布到 Broker → 标记已处理

**为什么不用进程内事件总线？** 之前版本包含一个基于 `Channel<T>` 的进程内事件总线（`EventBus`），但它存在几个问题：
- `SingleReader=true` 限制整进程只能有一个消费者，不能支持多 Handler 广播
- Channel 写成功不代表消费者处理成功，失败后事件丢失
- 实际事件派发已由 `IterativeDomainEventDispatcher` 覆盖（支持多 Handler + 去重 + 防栈溢出）

因此 EventBus 已被移除，建议统一使用 Outbox 实现可靠事件发布。

### 4.5 持久化与 EF Core 集成

**框架解决的问题：** 事务边界应该由谁控制？Pal.DDD 的原则是——框架不替你决定。`IUnitOfWork` 只封装事务的基本操作，你用或不用完全自由。

```csharp
// Infrastructure/AppDbContext.cs
public class AppDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();
}

// Program.cs
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// 注册工作单元（可选 —— 直接用 DbContext 也行）
builder.Services.AddScoped<IUnitOfWork, UnitOfWork<AppDbContext>>();
```

**UnitOfWork 提供了两个使用方法：**

```csharp
// 方式一：精确控制（显式事务）
public async ValueTask<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct)
{
    await _uow.BeginTransactionAsync(ct);
    // ... 增删改
    await _uow.SaveChangesAsync(ct);
    await _uow.CommitAsync(ct);
}

// 方式二：ExecuteInTransactionAsync 一步到位
public async ValueTask<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct)
{
    var id = new OrderId(Guid.NewGuid());
    await _uow.ExecuteInTransactionAsync(async ct2 =>
    {
        _db.Orders.Add(new Order(id, command.CustomerName, command.Amount));
        await _uow.SaveChangesAsync(ct2);
    }, ct);
    return id;
}
```

**为什么不提供通用 IRepository<T>？** 通用 Repository 要么暴露 `IQueryable`（AOT 不兼容），要么提供的查询方法太少。实践中直接用 `DbContext` 最灵活。`IUnitOfWork` 只封装事务边界，不做查询。

### 4.6 Outbox：可靠的事件发布

**框架解决的问题：「双写问题」** — 数据库写入成功了，但发消息时进程崩溃，事件就丢了。Outbox 模式把事件先写到同一个数据库事务里，再由一个后台进程独立 relay。

**看这个经典 bug：**

```csharp
// ❌ 经典错误 —— 数据库成功，消息丢了
await _db.SaveChangesAsync(ct);
await _eventBus.PublishAsync(new OrderCreated(...), ct);  // 这里崩溃 → 事件丢失
```

**Outbox 模式：**

```csharp
// 1. 注册 Outbox 拦截器（在 SaveChanges 事务内写领域事件到 Outbox）
builder.Services.AddPalOutboxUnitOfWork<AppDbContext>();
// 2. 注册 Outbox 后台处理器（轮询 OutboxMessages 表 → 发布到 Broker）
builder.Services.AddPalOutbox();
// 3. 注册 Outbox Store（EF Core 方式：继承 OutboxDbContext）
//    首先定义 AppOutboxDbContext：
//    public sealed class AppOutboxDbContext(DbContextOptions<AppOutboxDbContext> options)
//        : OutboxDbContext(options)
//    {
//        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
//    }
builder.Services.AddDbContext<AppOutboxDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddScoped<IPalOutboxStore>(sp => sp.GetRequiredService<AppOutboxDbContext>());
```

**发生了什么？** 当 `SaveChangesAsync` 被调用时：

1. `OutboxDomainEventInterceptor` 在 `SavingChanges` 阶段收集实体的领域事件
2. 把事件序列化为 `OutboxMessage` 写入同一数据库事务（`Events` 表 + `OutboxMessages` 表在同一个事务里）
3. 事务提交后，DB 里既有业务数据、又有待发送的事件

```sql
-- 最终写入 DB 的是什么：
BEGIN TRANSACTION
  INSERT INTO Orders ...                               -- 业务数据
  INSERT INTO OutboxMessages (Type, Payload, Status)   -- 待发送的事件
    VALUES ('ordering.order-created.v1', '{"orderId":"..."}', 'Pending')
COMMIT
-- 即使在这之后进程崩溃，事件不会丢
```

**后台 OutboxProcessor 自动运行：**

```csharp
// OutboxProcessor 继承 PeriodicBackgroundProcessor（后台轮询基类），自动轮询 OutboxMessages 表
// 它做的事：
while (true)
{
    // 1. 租约锁定一批待发送消息（UPDLOCK + READPAST，多实例竞争安全）
    var messages = await _store.LeasePendingMessagesAsync(100, _leaseOwner, TimeSpan.FromMinutes(2), maxRetryCount: 5, ct);

    foreach (var msg in messages)
    {
        // 2. 反序列化
        var descriptor = _messageCatalog.Find(msg.Type);
        var @event = _serializer.Deserialize(msg.Payload, descriptor);

        // 3. 发布到 Kafka/RabbitMQ
        await _broker.PublishAsync(@event, descriptor, msg.Id, ct);

        // 4. 标记成功
        _store.MarkProcessed(msg, now);
        await _store.SaveChangesAsync(ct);
    }
}
```

**重试机制：** 如果发布失败，`RetryCount++`，下次轮询时跳过还在租约内的，过期的重新尝试。超过 10 次失败 → 标记 Dead，不阻塞其他消息。

### 4.7 Inbox：幂等的消息消费

**框架解决的问题：** 消息队列保证 at-least-once 投递 —— 同一条消息可能收到两次。如果 Handler 不是幂等的，就会产生重复数据。

```csharp
// Application/Orders/OrderPaidHandler.cs
using PalDDD.Messaging;
using PalDDD.Transactions;

public sealed class OrderPaidHandler(IInboxStore inbox, InboxProcessor inboxProcessor, AppDbContext db) : IEventHandler<OrderPaid>
{
    private readonly IInboxStore _inbox = inbox;
    private readonly InboxProcessor _inboxProcessor = inboxProcessor;
    private readonly AppDbContext _db = db;

    public async ValueTask HandleAsync(OrderPaid @event, CancellationToken ct)
    {
        // Inbox 保证：同一条 messageId 只会被执行一次
        // InboxProcessor 通过 AddPalInbox() 注册为 Scoped，直接注入使用
        await _inboxProcessor.TryProcessAsync(
            "orders",                        // consumerName —— 消费者标识
            $"pay-{@event.OrderId}",         // messageId —— 唯一约束的基础
            async (OrderPaid evt, CancellationToken ct2) =>
            {
                var order = await _db.Orders.FindAsync([evt.OrderId], ct2);
                if (order is null) return;

                // 更新订单状态 —— 由于 Inbox 去重，这段代码即使执行两次也没影响
                // （因为第二次 TryStartProcessingAsync 会返回 null）
                order.MarkAsPaid();
                await _db.SaveChangesAsync(ct2);
            },
            @event, ct);
    }
}
```

**Inbox 做了什么：**

```
首次收到消息：
  1. INSERT InboxMessages (ConsumerName='orders', MessageId='pay-xxx', Status='Processing')
  2. 执行业务逻辑
  3. UPDATE InboxMessages SET Status='Processed'

重复收到消息：
  1. SELECT 发现已有记录
  2. Status = Processed → 返回 null → TryProcessAsync 返回 false → 跳过
```

**唯一约束保证：** `(ConsumerName, MessageId)` 上的唯一索引确保即使第一条 INSERT 和第二条 INSERT 并发，也只有一个成功。

**僵尸检测：** 如果 Handler 崩溃在 INSERT 和 UPDATE 之间，记录会永远停留在 `Processing` 状态。`ProcessingStartedAt` + `ProcessingTimeout` 超时后，下一条重复消息会抢占它。

### 4.8 Saga：跨步骤业务流程编排

**框架解决的问题：** 下单扣库存一分钱三个步骤，任何一步失败都需要「回滚」已经完成的步骤。手动写补偿逻辑容易漏。

```csharp
// Application/Orders/OrderFulfillmentSaga.cs
using PalDDD.Transactions;

public sealed class OrderState : SagaState
{
    public OrderId? OrderId { get; set; }
    public decimal Amount { get; set; }
    public bool PaymentCompleted { get; set; }
    public bool InventoryReserved { get; set; }
}

public sealed class OrderFulfillmentSaga : Saga<OrderState>
{
    public OrderFulfillmentSaga()
    {
        // 下单后：尝试预留库存
        // SagaStep 构造：name + executeAsync(Func<SagaState, object, CT, ValueTask<SagaState>>) + 可选 compensate
        When("Pending", typeof(OrderCreated), new SagaStep("ReserveInventory",
            async (state, evt, ct) =>
            {
                var created = (OrderCreated)evt;
                state.OrderId = created.OrderId;
                state.Amount = created.Amount;
                state.CurrentState = "ReservingInventory";
                return state;
            },
            compensate: async (state, ct) =>
            {
                // 逆向操作：释放库存
                Console.WriteLine($"  ↩️ 补偿: 释放订单 {state.OrderId} 的库存");
                return state;
            }));

        // 库存预留成功 → 处理支付
        When("ReservingInventory", typeof(InventoryReserved), new SagaStep("ProcessPayment",
            async (state, evt, ct) =>
            {
                state.InventoryReserved = true;
                state.CurrentState = "ProcessingPayment";
                return state;
            },
            compensate: async (state, ct) =>
            {
                Console.WriteLine($"  ↩️ 补偿: 取消支付");
                return state;
            }));

        // 支付成功 → 订单完成
        When("ProcessingPayment", typeof(OrderPaid), new SagaStep("Complete",
            async (state, evt, ct) =>
            {
                state.PaymentCompleted = true;
                state.CurrentState = "Completed";
                state.Status = SagaStatus.Completed;
                return state;
            }));
    }
}
```

**驱动 Saga 执行：** Saga 不直接发布事件，而是通过 `ProcessEventAsync` 驱动状态转换：

```csharp
// 在 Handler 中注入 Saga<OrderState> 并调用
var newState = await saga.ProcessEventAsync(sagaState, domainEvent, ct);
await sagaStateStore.SaveChangesAsync(newState, ct);
```

**补偿机制：** 补偿通过 `SagaStep` 构造函数的 `compensate` 参数传入，补偿策略由 `CompensationPolicy` 控制：

**框架的优势：** 手动写 Saga 很容易漏掉边界情况（超时、重复补偿、部分补偿...）。Pal.DDD 的 Saga 引擎自动处理超时扫描、补偿顺序（Backward 逆序）、幂等性：

- `PeriodicTimer` 定时扫描超时 Saga
- 补偿按已执行步骤的逆序逐一执行
- `FrozenDictionary<string, SagaStep>` 提供 O(1) 状态转换查找

### 4.9 EventLog：事件溯源审计日志

**框架解决的问题：** 谁在什么时候做了什么？EventLog 提供追加写的事件流，每一条记录都带有审计元数据（ActorId、Reason、CorrelationId），适合审计合规、数据回放、跨系统诊断。

```csharp
// 注册 EF Core EventLog 存储
public sealed class AppEventLogDbContext(DbContextOptions<AppEventLogDbContext> options)
    : EventLogDbContext(options);

builder.Services.AddDbContext<AppEventLogDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddScoped<IEventLog>(sp =>
    sp.GetRequiredService<AppEventLogDbContext>());
```

**写入事件流：**

```csharp
public async ValueTask<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct)
{
    var orderId = new OrderId(Guid.NewGuid());
    var data = new EventData(
        Guid.NewGuid(),
        "ordering.order-created.v1",
        schemaVersion: 1,
        contentType: "application/json",
        payload: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { orderId, command.CustomerName, command.Amount })),
        metadata: ReadOnlyMemory<byte>.Empty,
        audit: EventAuditMetadata.Capture(
            actorId: "user-123",
            reason: "submit order",
            correlationId: correlationId));

    await _eventLog.AppendAsync(
        $"ordering-order-{orderId}",           // stream name — 一个订单一个流
        ExpectedStreamVersion.NoStream,          // 期望版本（防止并发写入覆盖）
        [data],
        ct);

    // ... 业务逻辑
}
```

**Hi/Lo 位置分配器：**

```
EventLog 的 GlobalPosition 是全局单调递增的位置号，用于全量回放时定位。
过去用 Serializable 事务保护分配 → 全局串行化瓶颈。
现在用 Hi/Lo 段分配器 → 进程内缓存 100 个位置，仅 1/100 触发 DB。
CAS（Revision 令牌）替代 Serializable → 事务降为 ReadCommitted。
```

### 4.10 Projections：读模型投影

**框架解决的问题：** 订单列表页需要显示「订单金额 + 状态 + 物流信息」，这些数据来自多个服务。每次查询都跨服务 join 太慢。Projections 把事件流转换成专门给查询用的读模型。

```csharp
// Application/Projections/OrderSummaryProjection.cs
using PalDDD.Projections;

public sealed class OrderSummaryProjection : IProjectionHandler<OrderCreated>
{
    private readonly AppDbContext _db;

    public string ProjectionName => "order-summary";     // 投影名称，用于检查点

    public async ValueTask ProjectAsync(OrderCreated message, ProjectionContext ctx, CancellationToken ct)
    {
        // 从事件数据构建读模型
        var summary = new OrderSummary
        {
            Id = message.OrderId,
            CustomerName = message.CustomerName,
            TotalAmount = message.Amount,
            Status = "Created",
            OccurredAt = ctx.OccurredAt
        };
        _db.OrderSummaries.Add(summary);
        await _db.SaveChangesAsync(ct);
    }
}
```

**增量回放 vs 全量重建：**

```csharp
// 增量回放（推荐 — 安全可重试）
// ReplayAsync 不 reset 检查点，只处理缺失的 position，中途失败旧数据完整。
var replayed = await rebuilder.ReplayAsync(ct);     // 安全模式

// 全量重建（ResetAsync → ReplayAsync）
var replayed = await rebuilder.RebuildAsync(ct);     // 先清空再重建
```

**检查点机制：** 每个 position 处理成功后记录一个 `ProjectionCheckpoint`。如果崩溃，重启后跳过已完成的 position（`LeaseUntil` 超时后可被抢占），只补处理缺失的。这样可以安全地增量回放。

### 4.11 Idempotency：API 幂等性保障

**框架解决的问题：** 支付接口的「重复请求」—— 客户端重试导致同一笔钱扣了两次。强制要求调用方在请求头里带一个幂等键（Idempotency-Key），服务器记录这个键是否已经执行过。

```csharp
// Program.cs — IdempotencyProcessor 通过手动注册（无便捷扩展方法）
builder.Services.AddDbContext<AppIdempotencyDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddScoped<IIdempotencyStore>(sp => sp.GetRequiredService<AppIdempotencyDbContext>());
builder.Services.AddScoped<IdempotencyProcessor>();

// 在 Minimal API 中使用
app.MapPost("/orders", async (
    CreateOrderCommand command,
    [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
    IdempotencyProcessor processor) =>
{
    var result = await processor.ExecuteAsync(
        operationName: "create-order",
        key: idempotencyKey,
        handler: async ct =>
        {
            // 这里才是真实业务逻辑 —— 只会在第一次请求时执行
            var orderId = await mediator.Send(command, ct);
            return orderId;
        },
        serializeResult: id => Encoding.UTF8.GetBytes(id.ToString()),
        deserializeResult: bytes => new OrderId(Guid.Parse(Encoding.UTF8.GetString(bytes.Span))));

    return result.Status switch
    {
        IdempotencyExecutionStatus.Executed => Results.Created($"/orders/{result.Result}", result.Result),
        IdempotencyExecutionStatus.Cached => Results.Ok(result.Result),    // 重复请求，返缓存的结果
        IdempotencyExecutionStatus.Skipped => Results.Conflict(),           // 别人在处理中
        _ => Results.StatusCode(500)
    };
});
```

**原理：**
```
请求 → 查 IdempotencyRecords 表
  ├─ 无记录 → INSERT Processing → 执行业务 → UPDATE Completed（缓存响应）
  ├─ Completed → 直接返回缓存的响应
  ├─ Processing + LockedUntil 未过期 → 返回 409 Conflict（占用中）
  └─ Processing + LockedUntil 过期 → 抢占（CAS retry）
```

### 4.12 Schema Evolution：消息版本升级

**框架解决的问题：** 消息加了字段，旧消费者爆炸。Pal.DDD 的解法是相邻版本逐个升级。

```csharp
// 假设 订单创建消息 从 v1 升级到 v2（加了 CouponCode 字段）

// v1 版本
public sealed record OrderSubmittedV1(Guid OrderId, decimal Amount);

// v2 版本（新增 CouponCode）
public sealed record OrderSubmittedV2(Guid OrderId, decimal Amount, string? CouponCode);

// 注册升级规则 — 通过 AddPalMessageContractVerification 回调注册，启动时自动校验
services.AddPalMessageContractVerification(b => b.Add<OrderSubmittedV1, OrderSubmittedV2>(
    AppJsonContext.Default.OrderSubmittedV1,        // v1 描述符（JsonTypeInfo）
    AppJsonContext.Default.OrderSubmittedV2,        // v2 描述符
    old => new OrderSubmittedV2(old.OrderId, old.Amount, null)));  // 兼容旧数据：CouponCode = null

// PalPlatformVerificationHostedService 会检查所有升级路径是否完整
// 如果 v1→v2 缺了升级步骤，服务会启动失败，聚合所有错误抛 PalPlatformVerificationException
```

**升级流程：**
```
v1 payload → deserialize v1 → step1.convert(v1) → v2 → step2.convert(v2) → v3
            ↑                        ↑                          ↑
       (只支持相邻版本)       (一步只升一个版本)         (直到目标版本)
```

### 4.13 ASP.NET Core 集成

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// 注册推荐核心栈与需要的适配器
builder.Services.AddPalCoreStack();
builder.Services.AddPalJsonSerialization(catalog => { ... });
builder.Services.AddPalCommandHandler<...>();
builder.Services.AddPalOutboxUnitOfWork<AppDbContext>();
builder.Services.AddPalOutbox();
builder.Services.AddPalHealthChecks();      // 注册 Broker + Outbox 健康检查（Build 前调用）

var app = builder.Build();

// 异常中间件（必须放在最前面）
// PalValidationException → 400 + errors[]
// HandlerNotFoundException → 404
// 其他异常 → 500（不泄露内部消息！）
app.UsePalExceptionHandler();

// 健康检查端点
app.MapPalHealthChecks();               // 映射 GET /health

// 映射命令端点（有返回值的命令需用双 JsonTypeInfo 重载）
app.MapCommand<CreateOrderCommand, OrderId>(
    "/orders",
    AppJsonContext.Default.CreateOrderCommand,
    AppJsonContext.Default.OrderId);

app.MapQuery<GetOrderQuery, OrderDto>(
    "/orders/{orderId}",
    ctx => new GetOrderQuery(OrderId.Parse(ctx.Request.RouteValues["orderId"]?.ToString() ?? "")),
    AppJsonContext.Default.OrderDto);

app.Run();
```

**为什么异常中间件不泄露内部消息？** 验证过的：任何未预期的 Exception（比如数据库连接字符串），中间件只返回 `Internal Server Error`，不会把异常消息暴露到 HTTP 响应体中。测试用例 `InvokeAsync_UnhandledException_Returns500WithGenericProblemDetails` 明确验证了这个安全约束。

### 4.14 可观测性：指标与追踪

**框架解决的问题：** 生产环境出问题了，你先要知道的是：命令执行了多少？失败了几个？发件箱积压了多少？Pal.DDD 内建了 27 个 OpenTelemetry 指标和 11 种 Activity，零配置即可采集。

```csharp
// 一行代码接入
builder.Services.AddOpenTelemetry()
    .WithMetrics(meterProviderBuilder =>
        meterProviderBuilder
            .AddMeter("PalDDD")                          // 内建 27 个指标
            .AddPrometheusExporter())
    .WithTracing(tracerProviderBuilder =>
        tracerProviderBuilder
            .AddSource("PalDDD")                         // 内建 11 种 Activity
            .AddConsoleExporter());
```

**内建的 27 个指标：**

```
paldd.commands.total             // 命令执行总数
paldd.commands.duration_ms       // 命令执行耗时（直方图）
paldd.events.published           // 领域事件发布数
paldd.events.consumed            // 事件消费数
paldd.event_handlers.handled     // 事件处理器成功调用数
paldd.event_handlers.failed      // 事件处理器失败调用数
paldd.eventlog.appended          // 事件日志追加数
paldd.eventlog.read              // 事件日志读取数
paldd.outbox.pending             // 发件箱待处理数（UpDownCounter）
paldd.outbox.processed           // 发件箱成功处理数
paldd.outbox.failed              // 发件箱失败数
paldd.inbox.processed            // 收件箱成功数
paldd.inbox.skipped              // 收件箱去重跳过数
paldd.inbox.failed               // 收件箱失败数
paldd.idempotency.executed       // 幂等执行数
paldd.idempotency.cached         // 幂等缓存命中数
paldd.idempotency.skipped        // 幂等跳过数
paldd.idempotency.failed         // 幂等失败数
paldd.projection.replayed        // 投影重建事件数
paldd.projection.failed          // 投影重建失败数
paldd.replay.read                // 事件回放读取数
paldd.replay.failed              // 事件回放失败数
paldd.saga.active               // 活跃 Saga 数（UpDownCounter）
paldd.saga.completed             // Saga 完成数
paldd.saga.compensated           // Saga 补偿数
paldd.saga.compensation_failed   // Saga 补偿失败数
paldd.pipeline.behavior_duration_ms  // 管道行为执行耗时（直方图）
```

### 4.15 AOT 发布与构建检查

**为什么 Pal.DDD 能 AOT：** 关键差异在——不用反射、不用程序集扫描、不用 `MakeGenericType`、不用 `Activator.CreateInstance`。

所有运行时类型路由都通过编译时注册的 `FrozenDictionary` + 显式的泛型代码路径完成。

```bash
# 以 AOT 方式发布示例项目
dotnet publish samples/PalDDD.AotSample -c Release -r win-x64

# 检查你的项目（启用 AOT 分析）
# 在 csproj 中添加：
# <PublishAot>true</PublishAot>
# <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
```

**零警告：** 整个 Pal.DDD 框架零 IL2026、零 IL3050 反射警告。

---

## 附录：完整的 Program.cs

```csharp
using PalDDD.CQRS;
using PalDDD.DependencyInjection;
using PalDDD.Hosting.AspNetCore;
using PalDDD.Repository.EFCore;
using PalDDD.Serialization.Json;
using PalDDD.Transactions;

var builder = WebApplication.CreateBuilder(args);

// ── Pal.DDD 核心 ──
builder.Services.AddPalDDD();
builder.Services.AddPalPipelineBehaviors();

// ── 序列化 ──
builder.Services.AddPalJsonSerialization(catalog =>
{
    catalog.Add(AppJsonContext.Default.OrderCreated, name: "ordering.order-created.v1");
    catalog.Add(AppJsonContext.Default.OrderCancelled, name: "ordering.order-cancelled.v1");
    catalog.Add(AppJsonContext.Default.OrderPaid, name: "ordering.order-paid.v1");
});

// ── Handler ──
builder.Services.AddPalCommandHandler<CreateOrderCommand, OrderId, CreateOrderHandler>();
builder.Services.AddPalQueryHandler<GetOrderQuery, OrderDto, GetOrderHandler>();
builder.Services.AddPalEventHandler<OrderPaid, OrderPaidHandler>();

// ── Outbox（可靠事件发布）──
builder.Services.AddPalOutboxUnitOfWork<AppDbContext>();
builder.Services.AddPalOutbox();

// ── EF Core ──
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddDbContext<AppOutboxDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddPalHealthChecks();

var app = builder.Build();

app.UsePalExceptionHandler();
app.MapPalHealthChecks();

// 有返回值的命令需用双 JsonTypeInfo 重载
app.MapCommand<CreateOrderCommand, OrderId>("/orders",
    AppJsonContext.Default.CreateOrderCommand,
    AppJsonContext.Default.OrderId);
app.MapQuery<GetOrderQuery, OrderDto>("/orders/{id}", ctx =>
{
    var id = Guid.Parse(ctx.Request.RouteValues["id"]?.ToString()!);
    return new GetOrderQuery(new OrderId(id));
}, AppJsonContext.Default.OrderDto);

app.Run();
```

---

## 总结：Pal.DDD 的核心价值

```
❌ 不再写：
    - OrderService 巨型类
    - catch(Exception) 里手动重发消息
    - if(已处理) return 的重复代码
    - 手动写 JsonConverter/TypeConverter
    - 手动拼事件名、版本号
    - 部署前检查"消息格式兼容吗？"

✅ 换成：
    - 显式的 Command/Query/EventHandler
    - OutboxProcessor 自动 relay
    - InboxProcessor 自动去重
    - [GenerateId] / [GenerateMessage] 自动生成
    - static abstract EventName 编译时检查
    - Schema Evolution 启动时验证
```