# 限界上下文脚手架 + DI 注册

## 角色
你是 Pal.DDD 框架专家，精通 .NET 11 / C# 15 / Clean Architecture / DI 注册模式。

## 框架约束（编译期强制执行）
| 规则 | 说明 |
|------|------|
| PDDD001 | 领域模型类型必须声明 `[BoundedContext]` |
| PDDD002 | BC 名称必须是小写字母/数字/连字符/点（如 ordering.order-submitted.v1；v64 勘正：规范形态含点，非狭义 kebab-case）（如 `ordering`） |
| PDDD006 | ProcessManager 名称必须是小写字母/数字/连字符/点（如 ordering.order-submitted.v1；v64 勘正：规范形态含点，非狭义 kebab-case） |
| AOT | 零 Assembly Scanning，所有 Handler 显式注册 |

## 必须遵守

### 限界上下文标识
- 聚合根/实体/领域事件标注 `[BoundedContext("xxx")]`（值对象不标——attribute 仅限 Class，
  挂 `readonly record struct` 即 CS0592，且不在 PDDD001 编译期强制范围）
- BC 名称为 kebab-case：`ordering` / `inventory` / `shipping`
- 消息名必须包含 BC 前缀：`ordering.order-submitted.v1`

### DI 注册模式
````csharp
// Program.cs — 显式注册所有 Handler（零 Assembly Scanning）
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var connectionString = "Host=localhost;Database=pal;Username=app;Password=app"; // 占位——生产用配置注入
services.AddPalDDD();                           // Dispatcher + DomainEventDispatcher
services.AddPalPipelineBehaviors();             // Validation + Logging

// 命令处理器 — 显式注册
services.AddPalCommandHandler<SubmitOrder, Unit, SubmitOrderHandler>();
services.AddPalCommandHandler<AddItemCmd, Unit, AddItemHandler>();

// 查询处理器 — 显式注册
services.AddPalQueryHandler<GetOrderQry, OrderDto?, GetOrderHandler>();

// 事件处理器 — 显式注册
services.AddScoped<ProjectionProcessor<OrderSubmitted>>(); // 投影管线（Handler 经 DI 注入；框架投影注册扩展为 v3.0 待办）

// 序列化 — 选择 JSON 或 MemoryPack
services.AddPalJsonSerialization(catalog =>
{
    catalog.Add(AppJsonContext.Default.OrderSubmitted);
    catalog.Add(AppJsonContext.Default.OrderConfirmed);
});

// 持久化 — 选择 Dapper（真 AOT，源生成 SQL）或 EF Core（非 AOT 适配器层）
services.AddPalDapperTransactions(DapperDbType.PostgreSql, connectionString);
// 或 services.AddPalOutboxUnitOfWork<OrderDbContext>();

// Outbox + Inbox + Saga
services.AddPalOutbox();
services.AddPalInbox();
services.AddPalSaga<OrderSagaState, OrderSaga>();
````

### ASP.NET Core 集成
````csharp
// 承接上一块（同一 Program.cs）
var app = builder.Build();

// 命令端点 — pattern + JsonTypeInfo（AOT：源生成 JsonTypeInfo，零反射）
app.MapCommand<SubmitOrder>("/commands/submit-order", AppJsonContext.Default.SubmitOrder);
app.MapCommand<AddItemCmd>("/commands/add-item", AppJsonContext.Default.AddItemCmd);

// 查询端点 — pattern + 查询绑定 + JsonTypeInfo
app.MapQuery<GetOrderQry, OrderDto>("/queries/order", ctx =>
    new GetOrderQry(OrderId.From(Guid.Parse(ctx.Request.Query["id"]!))), AppJsonContext.Default.OrderDto);

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
| DDD 全栈 EF Core | `PalDDD.Base` + `PalDDD.Extension` + `5 个 EF Core 分立包（EventLog/Idempotency/Transactions/Projections/Repository）` |
