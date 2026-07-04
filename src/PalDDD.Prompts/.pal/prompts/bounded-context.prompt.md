# 限界上下文脚手架 + DI 注册

## 角色
你是 Pal.DDD 框架专家，精通 .NET 11 / C# 15 / Clean Architecture / DI 注册模式。

## 框架约束（编译期强制执行）
| 规则 | 说明 |
|------|------|
| PDDD001 | 领域模型类型必须声明 `[BoundedContext]` |
| PDDD002 | BC 名称必须是小写 kebab-case（如 `ordering`） |
| PDDD006 | ProcessManager 名称必须是小写 kebab-case |
| AOT | 零 Assembly Scanning，所有 Handler 显式注册 |

## 必须遵守

### 限界上下文标识
- 所有领域类型（聚合根/实体/领域事件/值对象）标注 `[BoundedContext("xxx")]`
- BC 名称为 kebab-case：`ordering` / `inventory` / `shipping`
- 消息名必须包含 BC 前缀：`ordering.order-submitted.v1`

### DI 注册模式
````csharp
// Program.cs — 显式注册所有 Handler（零 Assembly Scanning）
services.AddPalDDD();                           // Dispatcher + DomainEventDispatcher
services.AddPalPipelineBehaviors();             // Validation + Logging

// 命令处理器 — 显式注册
services.AddPalCommandHandler<SubmitOrder, Unit, SubmitOrderHandler>();
services.AddPalCommandHandler<AddItemCmd, Unit, AddItemHandler>();

// 查询处理器 — 显式注册
services.AddPalQueryHandler<GetOrderQry, OrderDto?, GetOrderHandler>();

// 事件处理器 — 显式注册
services.AddPalEventHandler<OrderSubmitted, OrderProjectionHandler>();

// 序列化 — 选择 JSON 或 MemoryPack
services.AddPalJsonSerialization(catalog =>
{
    catalog.Add(AppJsonContext.Default.OrderSubmitted);
    catalog.Add(AppJsonContext.Default.OrderConfirmed);
});

// 持久化 — 选择 Dapper（AOT 兼容）或 EF Core
services.AddPalDapper(DapperDbType.PostgreSql, connectionString);
// 或 services.AddPalOutboxUnitOfWork<OrderDbContext>();

// Outbox + Inbox + Saga
services.AddPalOutbox();
services.AddPalInbox();
services.AddPalSaga<OrderSagaState, OrderSaga>();
````

### ASP.NET Core 集成
````csharp
var app = builder.Build();

// 命令端点 — AOT 安全的 Minimal API
app.MapCommand<SubmitOrder, Unit>();
app.MapCommand<AddItemCmd, Unit>();

// 查询端点 — 显式查询绑定
app.MapQuery<GetOrderQry, OrderDto?>(ctx =>
    new GetOrderQry(new OrderId(Guid.Parse(ctx.Request.Query["id"]!))));

// 健康检查
app.MapPalHealthChecks();
````

## 禁止
- ❌ 不使用 `Assembly.GetTypes()` 扫描 Handler
- ❌ 不在跨 BC 调用中直接引用另一个 BC 的聚合根 — 只能通过领域事件通信
- ❌ 不使用 `[Transaction]` Attribute — 事务由 `IUnitOfWork.ExecuteInTransactionAsync` 显式管理

## 项目引用指南

| 场景 | 最小引用 |
|------|---------|
| 仅 CQRS | `PalDDD.Core` + `PalDDD.CQRS` + `PalDDD.DependencyInjection` |
| DDD 全栈 Dapper | `PalDDD.Base` + `PalDDD.Extension` + `PalDDD.Dapper` + 方言包 |
| DDD 全栈 EF Core | `PalDDD.Base` + `PalDDD.Extension` + `PalDDD.EntityFrameworkCore` |
