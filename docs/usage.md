# 使用指南

本文档展示 Pal.DDD 的常见使用路径。示例聚焦 API 形状；实际项目可按需要拆分到不同文件。

## 安装和注册核心服务

```csharp
using Microsoft.Extensions.DependencyInjection;
using PalDDD.DependencyInjection;

var services = new ServiceCollection();

services.AddPalCoreStack();
```

`AddPalCoreStack()` 是推荐的核心入口，等价于 `AddPalDDD()` + `AddPalPipelineBehaviors()` + `AddPalIdentity()`。它不会自动引用序列化、持久化、Broker 或 ASP.NET Core 适配器，避免基础设施依赖越过 Clean Architecture 边界。原 `AddPalFullStack()` 已在 v5.0 契约窗口移除——它从未注册基础设施，名字容易被误读为“一键到位”，统一入口只有 `AddPalCoreStack()`。

管道行为（验证/日志）依赖 `IPalLogger<T>`：`AddPalCoreStack()` 与 `AddPalPipelineBehaviors()` 会自带该门面的最小注册（依赖宿主的 `ILogger<T>`，无 Provider 时零输出），因此「核心栈 + handler」即可完成首次 `SendAsync`。组件级注册 `AddPalOutbox()` / `AddPalInbox()` / `AddPalSaga<,>()` / `AddPalPostgreSqlOutboxNotifier()` 同样各自登记该门面（2026-10-21 R7，R8 补齐通知器），因此不依赖 `AddPalCoreStack()` 先行，单独使用不会在宿主启动时抛「Unable to resolve service for type IPalLogger<T>」；宿主的 `ILogger<T>` 仍由标准宿主（Generic Host / WebApplicationBuilder）提供。`AddPalLogging()` 是可选的输出配置，只负责追加 ZLogger 控制台 Provider 与日志级别策略。

领域事件 dispatcher 通过 `HashSet<Guid>` 去重循环事件，并通过 `while` 循环替代递归防止深层事件链导致栈溢出。

领域事件 dispatcher 在调用 handler 时会创建 `Event Dispatch` activity，并带有 `pal.event` tag；handler 调用结果会记录 `paldd.event_handlers.handled` / `paldd.event_handlers.failed`。应用层可通过 OpenTelemetry `AddSource(PalActivitySource.Name)` 将事件处理 trace 与命令、Outbox/Inbox、投影或 saga 链路关联。

`AddPalPipelineBehaviors()` 只注册验证和日志行为。Pal.DDD 不提供 `[Transaction]` attribute、自动事务管道或通用 Repository 包装；命令需要事务时，在 handler 内显式调用 `IUnitOfWork`，或把一致性边界交给 EF Core transaction、`SaveChangesInterceptor` 和 Outbox。

## 定义领域模型

```csharp
using PalDDD.Core;

[GenerateId(typeof(Guid))]
public readonly partial record struct OrderId;

[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.order-created.v1", SchemaVersion = 1)]
public sealed class OrderCreated(OrderId orderId) : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.order-created.v1";
    public OrderId OrderId { get; } = orderId;
}

public sealed class Order : AggregateRoot<OrderId>
{
    public string CustomerName { get; private set; }

    private Order(OrderId id, string customerName) : base(id)
    {
        CustomerName = customerName;
    }

    public static Order Create(string customerName)
    {
        var order = new Order(OrderId.New(), customerName);
        order.RaiseEvent(new OrderCreated(order.Id));
        return order;
    }
}
```

`RaiseEvent` 是 protected API，领域事件由实体内部产生，并由 EF Core interceptor 或应用层收集派发。

## 定义命令和 handler

```csharp
using PalDDD.CQRS;

public sealed record CreateOrderCommand(string CustomerName, decimal Amount) : ICommand<OrderId>;

public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand, OrderId>
{
    public ValueTask<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct)
    {
        var order = Order.Create(command.CustomerName);
        return ValueTask.FromResult(order.Id);
    }
}
```

注册：

```csharp
services.AddPalCommandHandler<CreateOrderCommand, OrderId, CreateOrderHandler>();
```

分发：

```csharp
// HandlerRegistrar 是 IHostedService：必须先启动宿主，Handler 才会注册进 Dispatcher。
var builder = Host.CreateApplicationBuilder();
builder.Services.AddPalCoreStack();
builder.Services.AddPalCommandHandler<CreateOrderCommand, OrderId, CreateOrderHandler>();

using var host = builder.Build();
await host.StartAsync();
var dispatcher = host.Services.GetRequiredService<Dispatcher>();
var orderId = await dispatcher.SendAsync(new CreateOrderCommand("Contoso", 100m));
await host.StopAsync();
```

> **前提**：Handler 由 `HandlerRegistrar`（`IHostedService`）在**宿主启动时**自动注册并 `Freeze()`。纯 `BuildServiceProvider()` 后直接 `SendAsync` 会抛 `HandlerNotFoundException`（`IHostedService` 未执行）；单测/控制台工具请复用宿主形态（如上例），或直接构造 `Dispatcher` 并显式调用 `Register`。

## 定义查询和 handler

```csharp
public sealed record GetOrderQuery(OrderId OrderId) : IQuery<string>;

public sealed class GetOrderHandler : IQueryHandler<GetOrderQuery, string>
{
    public ValueTask<string> HandleAsync(GetOrderQuery query, CancellationToken ct)
        => ValueTask.FromResult($"Order:{query.OrderId}");
}

services.AddPalQueryHandler<GetOrderQuery, string, GetOrderHandler>();
```

## 验证

实现 `IPalValidator<T>`，并启用 pipeline behaviors：

```csharp
using PalDDD.Core;

public sealed class CreateOrderValidator : IPalValidator<CreateOrderCommand>
{
    public PalValidationResult Validate(CreateOrderCommand instance)
        => string.IsNullOrWhiteSpace(instance.CustomerName)
            ? PalValidationResult.Failed(nameof(instance.CustomerName), "Customer name is required.")
            : PalValidationResult.Success();
}

services.AddScoped<IPalValidator<CreateOrderCommand>, CreateOrderValidator>();
services.AddPalPipelineBehaviors();
```

验证失败时会抛出 `PalValidationException`。

> ⚠️ **AOT 注意**：无类型参数重载 `AddPalPipelineBehaviors()` 是开放泛型注册，Native AOT 下值类型响应（`Unit`/`int`/`Guid`）会触发 `AotCannotCreateGenericValueType`。AOT 应用请改用 `AddPalCommandHandler<T...>()` / `AddPalQueryHandler<T...>()`（内部自动闭合注册管道行为），或显式闭合注册 `AddPalPipelineBehaviors<TRequest, TResponse>()`。两种注册先到先得、互斥——旧代码同时调用两者时解析结果收敛为 2 个 behavior，不会重复执行。详见 [aot.md](aot.md)。

### 与 HTTP 请求验证的分工（两层，别混）

Pal.DDD 的验证在 **CQRS 管线**里（`IPalValidator<T>` + `ValidationBehavior`），因此覆盖**所有分发路径**——HTTP 端点、Kafka / RabbitMQ 消费者、后台任务触发的同一条命令都走同一套验证。

HTTP **请求形态**（query / header / body 的必填、长度、范围）是另一层，属宿主应用职责：

| 层 | 负责方 | 机制 |
|----|--------|------|
| 请求形态 | 宿主应用（ASP.NET Core 平台） | .NET 10 起内置 Minimal API 验证：宿主侧 `builder.Services.AddValidation()` + `DataAnnotations` 特性（`[Required]` / `[StringLength]` / `[Range]` …）。源生成器驱动，自动发现处理器参数类型并逐端点挂验证过滤器（`SkipValidationAttribute` 跳过指定参数、`ValidatableTypeAttribute` 强制生成静态推导不到的类型信息——两者 .NET 10 已随 `Microsoft.Extensions.Validation` 包发布，.NET 11 起不再标记 experimental） |
| 领域 / 业务规则 | Pal.DDD | `IPalValidator<T>` + `ValidationBehavior`，见上文 |

两种误用都要避免：

- **把业务规则写进端点的 `IEndpointFilter`**：非 HTTP 路径触发的同一条命令会绕过该验证——这正是本框架把验证放在管线而不是端点的原因。
- **只给请求模型打 `[Required]` 等特性、却不启用 `AddValidation()`**：那些特性**不会生效**。Minimal API 参数不会被平台自动验证，这正是 .NET 10 引入该特性的原因（官方文档明确：未正确注册时请求返回 200 而非期望的 400）。

`AddValidation()` 与 `Microsoft.Extensions.Validation` 是宿主侧的依赖选择，**本框架不引用它们**——框架内验证继续用不绑定任何验证库的 `IPalValidator<T>`。Web SDK 宿主无需额外引包（该 API 随 ASP.NET Core 提供），纯类库需自行引 `Microsoft.Extensions.Validation`。

## 显式事务边界

CQRS 包不依赖 repository，也不隐式开启数据库事务。需要事务的命令 handler 应直接表达一致性需求：

```csharp
using PalDDD.Core.Repository;

public sealed class CreateOrderHandler(IUnitOfWork unitOfWork)
    : ICommandHandler<CreateOrderCommand, OrderId>
{
    public async ValueTask<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct)
    {
        await unitOfWork.BeginTransactionAsync(ct);
        try
        {
            var order = Order.Create(command.CustomerName);
            await unitOfWork.SaveChangesAsync(ct);
            await unitOfWork.CommitAsync(ct);
            return order.Id;
        }
        catch
        {
            await unitOfWork.RollbackAsync(ct);
            throw;
        }
    }
}
```

如果使用 `OutboxDomainEventInterceptor`，领域事件会在 `SaveChanges` 事务中写入 Outbox，再由后台 processor 发布。不要把事务策略藏在 request attribute 中，也不要把 EF Core 的查询能力包进通用 `IRepository<T>`；需要封装持久化时，定义面向业务语义的专用仓储或直接使用应用 `DbContext`。

> ⚠️ **不支持嵌套事务（3.0.0 起三栈统一 fail-fast，ADR-023）**：事务已活动时再调 `BeginTransactionAsync` 会抛 `InvalidOperationException`（此前 EF Core 与 PalORM 两栈为静默 no-op——嵌套调用时内层 `CommitAsync` 提交的是外层事务，导致静默原子性破坏）。需要在既有事务内执行工作的调用方，请直接执行工作委托或自行编排提交边界，不要嵌套调用本方法。

## 注册 JSON 消息序列化

```csharp
using System.Text.Json.Serialization;
using PalDDD.Serialization.Json;

public sealed record OrderSubmitted(Guid OrderId, decimal Amount);

[JsonSerializable(typeof(OrderSubmitted))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

services.AddPalJsonSerialization(catalog =>
{
    catalog.Add(AppJsonContext.Default.OrderSubmitted, name: "order-submitted");
});
```

消息发布和 Outbox 都依赖同一个 `IMessageCatalog`。未注册类型会快速失败。

## 使用 MemoryPack 二进制序列化（AOT 场景推荐）

JSON 是默认消息序列化器，零反射且跨语言友好。当追求**更高吞吐 / 更小 payload**（典型 3-5x 加速、payload 缩减 2-4x），可切换到 MemoryPack 二进制序列化器。MemoryPack 内置 source generator，运行时零反射；但本适配器包 IsAotCompatible=false（AOT 声明未经验证），AOT 场景请实测 PublishAot 后再用。

```csharp
using MemoryPack;
using PalDDD.Serialization.MemoryPack;

[MemoryPackable]
public sealed partial record OrderSubmitted(Guid OrderId, decimal Amount);

services.AddPalMemoryPackSerialization(catalog =>
{
    catalog.Add<OrderSubmitted>(name: "order-submitted");
});
```

要点：

- `[MemoryPackable] partial record` 触发 MemoryPack 源生成器，编译期生成 Formatter，运行时零反射。
- 与 `AddPalJsonSerialization()` **互斥**注册——`IMessageSerializer` 是 Singleton，后注册者覆盖前者。Outbox/Inbox/EventLog 全局共用同一序列化器，切换前请确认历史 payload 的兼容性（ContentType 不同则需迁移）。
- 二进制 payload 不再可读，运维排查时通过 `ContentType: application/x-memorypack` 区分。

## 使用 Outbox

> ⚠️ **事务前提（TX1，2026-09-20）**：Outbox 模式的原子性由「业务数据写入与消息行写入在**同一数据库事务**内提交」保证——这是**使用方职责**：调用方必须在业务 DbContext 事务/UnitOfWork 内写入 outbox 消息行（v4.0 ③ 起为 `await AddMessageAsync` + 同事务 `SaveChanges`），框架的后台发布器只负责事务提交后的可靠投递。若消息行与业务数据不同事务，将失去 exactly-once-write 保证（业务回滚但消息已入队 → 幽灵消息）。

> ⚠️ **跨栈误配警示（2026-09-20 补）**：EF 业务上下文配 Dapper/PalORM 的 `IPalOutboxStore` 时，`AddMessageAsync` 是**即时 INSERT**——无活动 ambient 事务即独立自动提交，与业务写入**不在同一事务**：业务回滚后 outbox 行仍在（孤儿消息）。这是跨栈混配的误配场景，不是默认路径（默认 EF + EF 流里 outbox 行进同一 `SaveChanges` 事务）。要么保持栈一致，要么确保 Dapper/PalORM store 与业务写入共享同一 `IUnitOfWork`/`DbTransaction`。

```csharp
using PalDDD.Transactions;

services.AddPalOutbox();
```

调用方还必须注册：

- `IMessageSerializer`
- `IMessageCatalog`
- `IMessageBroker`
- `IPalOutboxStore`

`IPalOutboxStore.LeasePendingMessagesAsync` 必须提供原子租约语义。SQL Server EF Core base context 提供了基于 `UPDLOCK` / `READPAST` 的实现（**未验证/实验性**，见下）。

生产环境可从 `PalDDD.Transactions.EFCore` 派生 `OutboxDbContext`，或按方言派生 `PostgreSqlOutboxDbContext`/`MySqlOutboxDbContext`/`SqliteOutboxDbContext`（ADR-012 方言粒度）以复用原子租约获取。`SqlServerOutboxDbContext` 当前标 `[Obsolete]`（源码声明 v4.0 移除）且零测试覆盖，属**实验性/未验证**，生产请优先使用已验证方言。适配器会配置 pending 查询索引、payload 必填、trace/correlation 字段长度和错误字段长度；`MarkProcessedAsync` 会清理 lease/retry 状态，`ReleaseForRetryAsync` 会释放 lease 并设置 `NextAttemptAt`。终态写的 fencing 契约自 v4.0 ②（ADR-026）跨栈统一：租约 token 拒绝时零 DB 写 + 零内存变异；成功时 DB 全套更新 + 入参对象全套回写（EF 栈仅对 Detached 快照回写，跟踪实体自动跳过——并发令牌守卫）。

**并行发布（3.0.0 起）**：`OutboxOptions.MaxDegreeOfParallelism`（默认 1，经 `AddPalOutbox(o => o.MaxDegreeOfParallelism = n)` 配置）>1 时，批内消息按分区交由 per-worker scope（独立 store/DbContext 实例）并行完成「反序列化 → 发布 → 标记」，吞吐随并行度提升；租约/fencing 互斥不受影响。**前提**：① broker 发布须线程安全（Kafka producer 天然支持；RabbitMQ 单 channel 并发发布的 7.x 语义已核实——publisher-confirms 仅护帧发送段、确认等待不串行）；② 消费方幂等（并行加速下乱序提交概率上升）。直构造 `OutboxBatchProcessor` 的调用方不受影响（`IServiceScopeFactory` 为可选尾参；并行度 >1 且缺失时运行期 fail-fast 并给出指引）。

**自动投递策略（2026-10-19，ADR-033）**：`DeliveryMode.Auto` 默认开启。broker 声明 `SupportsConcurrentPublish` 时发布窗口至少 16（`MaxInFlightPublishes` 显式更大的值参与取大）；store 实现 `IBatchMarkOutboxStore` 时终态标记阈值提升到 `min(BatchSize, 400)`（`BatchSize` 默认 100，即有效值 100；上限 400 是现有分块宽），并在 `BatchMarkFlushInterval`（默认 1 秒）到点时冲刷，重复窗口不再由整批时长放大。两个选项自身的默认值都是 1，`Serial` 模式下才逐字生效；Auto 会按能力提升，不是「默认逐条确认」。broker 同时实现 `SupportsRawPublish` 时，Outbox 直接发送持久化字节，跳过反序列化与再序列化。需要逐条确认的旧行为时显式配置 `DeliveryMode = Serial`。Kafka 窗口化要求幂等 producer：调用方未显式配置 `EnableIdempotence`/`Acks`/`MaxInFlight`/`MessageSendMaxRetries` 时框架默认开启幂等（acks=all），因此 Kafka 默认具备升窗条件；显式设置任一项则保持调用方取舍，此时仅在 `EnableIdempotence=true` 时升窗。

EF 栈的 `IPalOutboxStore` / `IInboxStore` / `ISagaStateStore<T>` / `IIdempotencyStore` / `IProjectionCheckpointStore` 由各适配器包的 `AddPal*EfCore` 扩展注册（与 Dapper/PalORM 栈的 `AddPal*` 入口对称）。这些扩展**不**注册 `DbContext` 本身（provider 选择权在调用方），需与 `AddDbContext` 配套：

```csharp
services.AddPalOutboxEfCore<AppOutboxDbContext>();       // IPalOutboxStore
services.AddPalInboxEfCore<AppInboxDbContext>();         // IInboxStore
services.AddPalSagaStateEfCore<AppSagaDbContext, OrderSagaState>();  // ISagaStateStore<T>
services.AddPalIdempotencyEfCore<AppIdempotencyDbContext>();         // IIdempotencyStore
services.AddPalProjectionsEfCore<AppProjectionDbContext>();          // IProjectionCheckpointStore

services.AddDbContext<AppOutboxDbContext>(o => o.UseNpgsql(connectionString));
// ... 其余上下文同理
```

Store 映射的生命周期为 **Scoped**，与 `AddDbContext` 默认一致（`DbContext` 非线程安全，不得 Singleton）。

Dapper 栈的 store 构造函数（`new DapperOutboxStore(...)` 等）在构造期校验两项配置：连接串非空、`DapperDbType` 与连接类型一致（`SqliteConnection`/`NpgsqlConnection`/`MySqlConnection`）。不一致会立即抛 `ArgumentException` 并给出期望形态——避免等到首次查询才以 provider 语法错误或 `no such table` 的形式暴露。连接串必须在构造 store 前设置。

### 死信语义与运维（F4 补，2026-09-19）

消息投递失败（broker 不可达、反序列化失败、类型未注册）时按指数退避重试；**重试耗尽（默认 `MaxRetryCount = 10`，经 `AddPalOutbox` 的 options 可配）后消息进入 `Dead` 状态并停止投递**——这是静默停止，不会抛异常到宿主。运维要点：

- **查看死信**：查询 `IPalOutboxStore`（各栈 `outbox_messages` 表 `status = 2` 即 Dead，`error` 字段含最后失败原因，`retry_count` 可达上限值）。
- **重新投递**：`RequeueDeadAsync(messageId, retriedBy)` 把 Dead 行重置为 Pending（清租约、`retry_count` 保留失败历史）。⚠️ **幂等前提**：下游消费者必须幂等——重投的消息可能已部分处理过（at-least-once 语义，ADR-011）。
- **调整重试上限**：`services.AddPalOutbox(o => o.MaxRetryCount = 5)`（`OutboxOptions`，启动期校验）。

Outbox message 可以携带跨上下文追踪元数据：

```csharp
var message = new OutboxMessage
{
    Type = descriptor.Name,
    Payload = payload,
    ContentType = descriptor.ContentType,
    SchemaVersion = descriptor.SchemaVersion,
    CorrelationId = correlationId,
    CausationId = commandId,
    TraceParent = Activity.Current?.Id,
    TraceState = Activity.Current?.TraceStateString
};
```

`OutboxBatchProcessor` 会把这些字段转换为 `MessagePublishContext`。Kafka/RabbitMQ adapter 会把 `traceparent`、`tracestate`、`x-correlation-id` 和 `x-causation-id` 写入传输 headers/properties。

Outbox 批处理还会发出 `PalActivitySource` span：

```csharp
using PalDDD.Core.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(PalActivitySource.Name));
```

`Outbox Process` span 包含 `pal.outbox.batch_size`、`pal.outbox.processed`、`pal.outbox.dead` 和 `pal.outbox.retried` 标签。

同一批处理还会记录 `paldd.outbox.processed` 和 `paldd.outbox.failed` metrics，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集。重试耗尽进入死信的消息另有独立的 `paldd.outbox.dead` 计数（3.0.0 起，建议按它建死信积压告警）；`paldd.outbox.persist_failed` 计数状态持久化失败（标记已尝试但未落库，下轮轮询重试）。

## 使用 Inbox

```csharp
services.AddPalInbox();
```

调用方还必须注册 `IInboxStore`。处理消息：

```csharp
var processed = await inbox.TryProcessAsync(
    consumerName: "orders",
    messageId: messageId,
    handler: static async (OrderSubmitted message, CancellationToken ct) =>
    {
        await ValueTask.CompletedTask;
    },
    message: message,
    ct);
```

`false` 表示该消息已处理或仍在其他消费者处理中。

**处理租约契约**：`InboxOptions.ProcessingTimeout`（默认 5 分钟）必须大于最慢 handler 的执行时长。单次处理超过该值后，同一 `(ConsumerName, MessageId)` 可被第二个消费者重新认领并并发执行，副作用的重复发生不会自动去重。`InboxProcessor` 在 handler 时长达到该值时记录 `InboxHandlerLeaseExceeded`（EventId 1006）告警：逐条路径每条一条，批量路径按批聚合一条（含超限条数与最大耗时，计时起点为整批认领时刻）；长耗时 handler 请显式调大该选项。

批量路径的其余告警与逐条路径同口径：handler 失败按批聚合记 `InboxMessageFailed`（1003）；完成标记整批抛错或部分未命中记 `InboxMessageProcessedMarkFailed`（1004），此时 span 结果为 `processed-pending-confirmation`，且不发 `InboxBatchProcessed`（1005，仅在全部成功项都标记落库时输出）。其中部分未命中为 Warning（无异常可携带），整批抛错为 Error（携带异常）；两类均用 1004，告警规则应按 EventId 过滤而非按级别过滤。`pal.inbox.skipped` 统计去重后未被认领的消息数（已处理或仍在租约内），批内重复 MessageId 不计入。

`InboxProcessor` 会发出 `Inbox Process` span。它使用同一个 `PalActivitySource.Name`，标签为 `pal.inbox.consumer` 与 `pal.inbox.result`，批量路径在批内存在跳过条目时额外带 `pal.inbox.skipped`（跳过条数）；`pal.inbox.message_id` 已按 ITM-229 标准从 tag 移除（高基数：每消息唯一，且可能含业务 ID）。结果值为 `processed`、`skipped`、`failed`，以及 handler 成功但完成标记失败时的 `processed-pending-confirmation`（ITM-180：不得降级为可重试失败，否则重试会重放副作用；由下一轮循环与监控补正）。

同一幂等消费边界还会记录 `paldd.inbox.processed`、`paldd.inbox.skipped` 和 `paldd.inbox.failed` metrics，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集。

批量消费（2026-10-21）：

```csharp
services.AddPalInbox();
services.AddScoped<InboxProcessor>();   // InboxBatchConsumer 每批创建一个 scope

var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
var options = provider.GetRequiredService<IOptionsMonitor<InboxOptions>>();
var consumer = new InboxBatchConsumer<OrderSubmitted>(broker, scopeFactory, options);
await using var subscription = await consumer.StartAsync(
    handler: static async (OrderSubmitted message, CancellationToken ct) =>
    {
        await ValueTask.CompletedTask;
    },
    maxBatchSize: 64,
    maxDelay: TimeSpan.FromMilliseconds(50));
```

消息 ID 默认取 `MessageConsumeContext.MessageId`（Kafka 消息 Key / RabbitMQ `BasicProperties.MessageId`，发布侧由框架写入）；broker 不提供消息 ID 时传入 `messageIdSelector`，两者都无则启动后首批快速失败。store 实现 `IBatchInboxStore` 时批量路径按实现方策略执行：Dapper PG/SQLite 每批一次认领 + 一次完成往返，Dapper MySQL 为逐条 CAS（每消息 2 次往返，2026-10-22 R14 起：原块级时间戳令牌在同刻碰撞下会重复认领）；否则自动回落逐条 `TryProcessAsync`。**当前实现 `IBatchInboxStore` 的是 Dapper 与 InMemory；PalORM 与 EF 的 Inbox store 回落逐条**（每消息 2 次往返），`InboxProcessor` 会在该 processor 实例首次批量调用时记录一次 Information 说明回落（含 store 类型与消费者名），此后不再重复。broker 仍是 at-most-once：批内失败的条目被标记 Failed，不会由 broker 重投。

生产环境可引用 `PalDDD.Transactions.EFCore` 程序集，`using PalDDD.Transactions;` 后派生 `InboxDbContext`，并通过 DI 将该上下文作为 `IInboxStore` 使用。适配器会配置 `(ConsumerName, MessageId)` 唯一索引、processed 时间索引和 lease 状态索引；已处理消息返回 `null`，未超时 `Processing` 消息返回 `null`，失败或超时消息会重新进入 `Processing` 以支持 broker 重投递。

## 使用 Saga EF Core Store

生产环境可引用 `PalDDD.Transactions.EFCore` 程序集，`using PalDDD.Transactions;` 后派生 `SagaStateDbContext<TState>`，并通过 DI 将该上下文作为 `ISagaStateStore<TState>` 使用。适配器会配置 `SagaId` 主键、active/lease 查询索引、`Version` 并发令牌，并用 source-generated JSON converter 持久化 `StepStartedAt` 和 `ExecutedStepKeys`，避免 reflection-based serialization fallback。

> ⚠️ **Dapper 栈快照是必传项而非可选项**（decision-2026-09-19-saga-snapshot-failfast）：`DapperSagaStateStore<TState>` 不注册 `JsonTypeInfo<TState>` 时，`SaveChangesAsync` 会**抛异常**（fail-fast）——未注册的旧版本会静默把 `saga_data` 写 NULL（业务字段全部丢失，2026-09-19 收口）。注册方式：DI 路径 `services.AddPalDapperSagaSnapshot(jsonTypeInfo)`，或构造函数第三参直传。EF 栈用 source-generated converter，无此项要求。

## 使用 EventLog

EventLog 用于记录 append-only 事件流，并通过 expected version 提供乐观并发控制：

```csharp
using System.Text;
using PalDDD.EventLog;

var eventLog = new InMemoryEventLog();
var result = await eventLog.AppendAsync(
    streamName: "ordering-order-42",
    expectedVersion: ExpectedStreamVersion.NoStream,
    events:
    [
        new EventData(
            PalUlid.New(),
            "orders.order-submitted.v1",
            schemaVersion: 1,
            contentType: "application/json",
            payload: Encoding.UTF8.GetBytes("""{"orderId":"42"}"""),
            metadata: ReadOnlyMemory<byte>.Empty,
            audit: EventAuditMetadata.Capture(
                actorId: "user-123",
                reason: "submit order"))
    ],
    ct);
```

第二次写入同一 stream 时使用上一条事件的 stream version：

```csharp
await eventLog.AppendAsync(
    "ordering-order-42",
    ExpectedStreamVersion.Exact(result.LastStreamVersion),
    [nextEvent],
    ct);
```

读取时，`ReadStreamAsync` 按 stream version 回放单流事件，`ReadAllAsync` 按 global position 回放全局事件。生产 store 必须把 expected version 检查实现为原子操作，避免并发写入丢失更新。

> ⚠️ **两种消费路径的提交序约束（2026-09-20 补）**：
> - **`ReadStreamAsync` 的 stream version 流内严格连续**，是检查点消费的安全路径。
> - **`ReadAllAsync` 的 global position 分配序可与事务提交序倒挂**：事务 A 先分配到低位、事务 B 后分配到高位但先提交时，按全局位置推进检查点的消费方读到 B 的高位即推进，A 提交后其事件被永久跳过。EF 栈因分配器行锁使并发分配串行化而不可达；**PalORM/Dapper 栈的 global position 为 DB 自增（自增不加行锁），该窗口可达**（两栈已分别以 v29 P3 声明）。用全局位置做检查点时，要求各追加方提交延迟相近，或改用 `ReadStreamAsync`。
> - **Dapper/PalORM 栈的批量追加在未传事务时，中途失败会留下前半批**（部分写入，`DapperEventLog` P2 定案声明）——批量追加必须包在调用方事务内；EF 栈内部事务自动回滚，不受此影响。

生产环境可从 `PalDDD.EventLog.EFCore` 派生 `EventLogDbContext`，并通过 DI 将该上下文作为 `IEventLog` 使用：

```csharp
using Microsoft.EntityFrameworkCore;
using PalDDD.EventLog;

// ⚠️ 派生上下文必须显式接收并转发 reserver——否则每实例新建一个，Hi/Lo chunk 缓存
// 永不跨请求共享（每次 append 都走 allocator 行 SELECT + CAS UPDATE，且每次消耗
// 整个 chunk 的位置）。详见下方 AddPalEventLogEfCore 说明。
public sealed class AppEventLogDbContext(
    DbContextOptions<AppEventLogDbContext> options,
    EventLogPositionReserver reserver)
    : EventLogDbContext(options, positionReserver: reserver);

services.AddPalEventLogEfCore<AppEventLogDbContext>();   // 把 reserver 注册为 Singleton
services.AddDbContext<AppEventLogDbContext>(options =>
{
    options.UseNpgsql(connectionString);   // 示例用已验证方言 PostgreSQL
});
services.AddScoped<IEventLog>(sp => sp.GetRequiredService<AppEventLogDbContext>());
```

`AddPalEventLogEfCore<TContext>()` 把 `EventLogPositionReserver` 注册为 **Singleton**（`TryAdd` 语义，不覆盖调用方自己的注册）。EF Core 经 `ActivatorUtilities` 从 DI 解析派生上下文的构造参数，因此注册后所有请求上下文共享同一 chunk 缓存——这正是 Hi/Lo 分配器的设计前提。区块大小可调：`AddPalEventLogEfCore<AppEventLogDbContext>(chunkSize: 500)`。

适配器会配置 `GlobalPosition` 主键、`(StreamName, StreamVersion)` 唯一索引和 `EventId` 唯一索引，并持久化 payload、metadata、审计字段和 trace context。`GlobalPosition` 由 `EventLogPositionReserver` 的 Hi/Lo 段分配器管理，而非数据库自增 identity。分配器缓存 chunk（默认 1000 个位置）在进程内，仅当 chunk 耗尽时通过乐观 CAS（Revision 并发令牌）更新持久化 allocator 行；关系型 provider 下 append 使用默认隔离级别（ReadCommitted），stream 级别并发由唯一索引保障。

`AppendAsync` 还会发出 `EventLog Append` span。它使用同一个 `PalActivitySource.Name`，仅包含 `pal.eventlog.event_count` 标签——流名、stream version 与 global position 等高基数信息（含聚合 ID）已按 ITM-229 标准从 tag 移除，位置信息由 `AppendEventsResult` 返回值承载。

同一 append 成功边界还会记录 `paldd.eventlog.appended` metric，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集事件日志写入吞吐。

`ReadStreamAsync` 和 `ReadAllAsync` 分别会发出 `EventLog ReadStream` / `EventLog ReadAll` span（无高基数 tag——起始 stream version / global position 与读取数量均已按 ITM-229 标准移除）。

完整枚举读取结果后还会记录 `paldd.eventlog.read` metric，应用层可用于观察审计回放、投影修复和跨上下文诊断的事件读取吞吐。

## 使用 SQLite FTS5 与 JSON1

`PalDDD.Dapper.Sqlite` 与 `PalDDD.PalORM.Sqlite` 提供同语义的 FTS5/JSON1 片段与索引定义 API（ADR-032）。两个包共享 `src/PalDDD.Shared` 内部核心，PalORM 包不依赖 Dapper。

### 建索引（Dapper）

```csharp
var definition = SqliteFts.CreateOutboxIndex("outbox_messages");
await SqliteFts.ValidateAsync(connection, definition); // 校验源表列与主键形态
foreach (var statement in definition.Statements)
    await connection.ExecuteAsync(statement);
```

`CreateOutboxIndex` / `CreateEventLogIndex` 使用 standalone 索引（映射表 + 触发器），TEXT/ULID 主键与 BLOB payload 都可用；`payload` 默认以 `CAST(... AS TEXT)` 入索引。需要 INTEGER 主键 + TEXT 列的 external-content 索引时用 `CreateExternalContentIndex`，`ValidateAsync` 会在 TEXT 主键上提前抛错。standalone 索引只由后续 DML 触发器维护，源表已有数据时创建后须执行 `definition.RebuildStatements()`（PalORM 为 `RebuildIndexAsync`）回填，否则存量行搜不到。

### 查询（Dapper）

```csharp
var sql = $"SELECT s.* FROM outbox_messages s {definition.JoinClause()} " +
          $"WHERE {SqliteFts.MatchParameter(definition.IndexName, "q")} " +
          SqliteFts.OrderByRank(definition.IndexName);
var rows = await connection.QueryAsync<OutboxRow>(sql, new { q = "urgent" });
```

`MatchParameter` 生成 `MATCH @q`，用户输入不要用 `MatchLiteral`。JSON 谓词：

```csharp
var sql = $"SELECT * FROM outbox_messages WHERE {SqliteJson.EqualsParameter("payload", "type", "Type")}";
var rows = await connection.QueryAsync<OutboxRow>(sql, new { type = "Order.Created" });
```

JSON 路径始终引号化，键可以直接包含 `.`、`[`、`]`、空格；键内双引号与反斜杠按 JSON 路径规则转义，单引号按 SQL 字面量规则翻倍。

### 建索引与查询（PalORM）

```csharp
await using var session = await DataSession<SqliteProvider>.CreateAsync(
    DbOptions.Development(connectionString));

var definition = SqliteFts.CreateEventLogIndex("events");
await session.EnsureIndexAsync(definition); // 校验 + 逐条执行 DDL

var format = "SELECT COUNT(*) FROM events s "
    + definition.JoinClause()
    + " WHERE "
    + SqliteFts.MatchExpression(definition.IndexName)
    + " {0}";
var sql = FormattableStringFactory.Create(format, "order");
var count = await session.ScalarAsync<long>(sql);
```

PalORM 的 `FormattableString` 会把插值洞变成参数。SQL 片段必须用字符串拼接进格式串，不要把片段作为 `{...}` 洞传入（PD18）。JSON 谓词同理：

```csharp
var predicate = SqliteJson.Extract("payload", "Type") + " = {0}";
var sql = FormattableStringFactory.Create(
    "SELECT COUNT(*) FROM outbox_messages WHERE " + predicate,
    "Order.Created");
```

也可以走原生命令：

```csharp
using var command = session.GetRawConnection().CreateCommand();
command.CommandText = "SELECT COUNT(*) FROM outbox_messages WHERE "
    + SqliteJson.EqualsParameter("payload", "type", "Type");
var parameter = command.CreateParameter();
parameter.ParameterName = "@type";
parameter.Value = "Order.Created";
command.Parameters.Add(parameter);
```

### 生命周期

`SqliteFtsIndexDefinition` 提供 `RebuildStatements()`、`OptimizeStatements()`、`DropStatements()`；PalORM 侧对应 `RebuildIndexAsync`、`OptimizeIndexAsync`、`DropIndexAsync`、`IndexExistsAsync`。v4 旧索引升级先执行 `SqliteFts.DropLegacyStatements(indexName)`，顺序见 [v5 迁移指南](migration/sqlite-fts-json-v5.md)。

### 约束

- FTS5/JSON1 只处理文本 JSON。压缩后的 payload 不是文本，需先解压，或只索引 `event_name`/`type`。
- external-content 索引要求 INTEGER 主键；TEXT 主键请用 standalone。
- 独立索引会额外创建 `{indexName}_map` 映射表，`DropStatements()` 会一并删除。

## 使用 Projection

Projection 用于从 Outbox、broker 或事件流构建 Read Model。`ProjectionProcessor<TMessage>` 通过 checkpoint 保证同一个 projection/source/position 只成功处理一次：

```csharp
using PalDDD.Core;
using PalDDD.Projections;

public sealed record OrderSubmitted(Guid OrderId, decimal Amount);

[BoundedContext("ordering")]
public sealed class OrderSummaryProjection : IProjectionHandler<OrderSubmitted>
{
    public string ProjectionName => "ordering.order-summary";

    public ValueTask ProjectAsync(OrderSubmitted message, ProjectionContext context, CancellationToken ct = default)
    {
        // 更新 read model
        return ValueTask.CompletedTask;
    }
}

var store = new InMemoryProjectionCheckpointStore();
var processor = new ProjectionProcessor<OrderSubmitted>(new OrderSummaryProjection(), store);

var processed = await processor.ProcessAsync(
    new OrderSubmitted(orderId, 100m),
    new ProjectionContext("orders-outbox", "offset-42", DateTimeOffset.UtcNow),
    ct);
```

生产环境应提供数据库实现的 `IProjectionCheckpointStore`，用唯一约束保护 `(ProjectionName, SourceName, Position)`。

处理器在 handler 执行期间按 `processingTimeout / 3` 心跳续租（store 实现 `IProjectionLeaseRenewalStore` 时；内建 InMemory/Dapper/PalORM/EF 四栈均已实现），慢 handler 不会因超过 `processingTimeout` 被另一 worker 重租并发投影。自定义 store 未实现该能力时保持原语义：handler 必须在 `processingTimeout` 内完成，否则租约到期后可能被重租。

从 EventLog stream 重建 projection 时，使用 adapter 包把已记录事件转换为 replay source：

```csharp
using PalDDD.Projections;
using PalDDD.Projections.EventLog;
using PalDDD.Serialization;

var descriptor = MessageDescriptor.Create(
    AppJsonContext.Default.OrderSubmitted,
    name: "orders.order-submitted.v1");
var source = new EventLogReplaySource<OrderSubmitted>(eventLog, serializer, descriptor);
var rebuilder = new ProjectionRebuilder<OrderSubmitted>(
    "ordering.order-summary",
    "ordering-order-42",
    source,
    checkpointStore,
    processor);

var replayed = await rebuilder.RebuildAsync(ct);
```

adapter 会校验 `RecordedEvent` 的 wire name、schema version 和 content type，再通过 `IMessageSerializer` 反序列化 payload。checkpoint position 使用 stream version，因此同一 projection/source/version 可幂等重放。

`ProjectionRebuilder<TMessage>` 还会发出 `Projection Rebuild` span。它使用同一个 `PalActivitySource.Name`，包含 `pal.projection.name`、`pal.projection.source` 和 `pal.projection.replayed` 标签。

同一重建边界还会记录 `paldd.projection.replayed` metric，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集回放量。

生产环境可从 `PalDDD.Projections.EFCore` 派生 `ProjectionCheckpointDbContext`，并通过 DI 将该上下文作为 `IProjectionCheckpointStore` 使用。适配器会配置 `(ProjectionName, SourceName, Position)` 复合主键、projection/source/status 查询索引和 `Revision` 单调并发令牌（v53 勘正：原称 UpdatedAt 时间戳令牌，与代码不符），用于跨实例投影幂等处理与重建 checkpoint reset。

## 使用 Command Idempotency

Idempotency 用于 API/command retry，不替代 Inbox。调用方显式提供结果序列化和反序列化函数：

```csharp
using System.Text;
using PalDDD.Idempotency;

var processor = new IdempotencyProcessor(new InMemoryIdempotencyStore());

var execution = await processor.ExecuteAsync(
    operationName: "CreateOrder",
    key: request.IdempotencyKey,
    handler: async ct => await dispatcher.SendAsync(command, ct),
    serializeResult: static id => Encoding.UTF8.GetBytes(id.ToString()),
    deserializeResult: static payload => OrderId.From(Guid.Parse(Encoding.UTF8.GetString(payload.Span))),
    cancellationToken: ct);

if (execution.Status == IdempotencyExecutionStatus.Cached)
{
    return execution.Result;
}
```

`Executed` 表示本次请求执行了 handler；`Cached` 表示返回之前成功执行的结果；`Skipped` 表示同 key 当前仍在处理中或没有可重放结果。

`IdempotencyProcessor` 会发出 `Idempotency Execute` span。它使用同一个 `PalActivitySource.Name`，仅包含 `pal.idempotency.operation` 和 `pal.idempotency.result` 标签——`pal.idempotency.key` 已按 ITM-229 标准从 tag 移除（高基数，且可能含敏感业务标识）；结果值为 `executed`、`cached`、`skipped` 或 `failed`。

同一执行边界还会记录 `paldd.idempotency.executed`、`paldd.idempotency.cached`、`paldd.idempotency.skipped` 和 `paldd.idempotency.failed` metrics，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集。

生产环境可从 `PalDDD.Idempotency.EFCore` 派生 `IdempotencyDbContext`，并通过 DI 将该上下文作为 `IIdempotencyStore` 使用。适配器会配置 `(OperationName, Key)` 复合主键、过期时间索引、lease 状态索引和 `Revision` 单调并发令牌（v53 勘正：原称 `UpdatedAt` 时间戳令牌，与代码不符），用于跨实例幂等消费与 API retry 去重。过期记录的物理清理由应用侧负责（框架不启动后台清理任务，仅保证逻辑过期）。

## 使用 Schema Evolution

消息版本升级必须有显式执行链：

```csharp
using PalDDD.Serialization;
using PalDDD.Serialization.Evolution;

var oldDescriptor = MessageDescriptor.Create(AppJsonContext.Default.OrderSubmittedV1, "order-submitted", 1);
var currentDescriptor = MessageDescriptor.Create(AppJsonContext.Default.OrderSubmittedV2, "order-submitted", 2);

var pipeline = new MessageEvolutionBuilder()
    .Add<OrderSubmittedV1, OrderSubmittedV2>(
        oldDescriptor,
        currentDescriptor,
        old => new OrderSubmittedV2(old.OrderId, Amount: 0m))
    .Build();

var current = pipeline.Upgrade(payload.Span, oldDescriptor, currentDescriptor, serializer);
```

不要通过 `IIntegrationEvent`、`IUpcaster` 或 payload marker 表达版本策略。wire name 和 schema version 属于 `MessageDescriptor`。

## 使用 Saga

```csharp
public sealed class OrderSagaState : SagaState
{
    public Guid OrderId { get; set; }
}

public sealed class OrderSaga : Saga<OrderSagaState>
{
    public OrderSaga()
    {
        When<OrderSubmitted>("Initial", new SagaStep(
            "ReserveInventory",
            static (state, @event, ct) =>
            {
                state.CurrentState = "InventoryReserved";
                state.Version++;
                return ValueTask.FromResult(state);
            },
            compensate: static (state, ct) => ValueTask.CompletedTask,
            timeout: TimeSpan.FromMinutes(5)));
    }
}

services.AddPalSaga<OrderSagaState, OrderSaga>();
```

调用方还必须注册 `ISagaStateStore<OrderSagaState>`。`SagaTimeoutProcessor` 会按 `SagaProcessorOptions.TimeoutScanBatchSize` 批量扫描活跃 saga。

Saga 从未完成变为完成时会记录 `paldd.saga.completed` metric，超时补偿成功时会记录 `paldd.saga.compensated` metric；应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集流程管理完成量和补偿吞吐。

## ASP.NET Core 集成

```csharp
using PalDDD.Hosting.AspNetCore;

builder.Services.AddPalHealthChecks();

var app = builder.Build();
app.UsePalExceptionHandler();
app.MapPalHealthChecks("/health");
// 生产部署：/health 暴露组件拓扑与依赖状态，按授权/限流约定保护——
// 用返回 IEndpointConventionBuilder 的变体（R13 F10）：
// app.MapPalHealthChecksWithConventions("/health").RequireAuthorization();
```

异常中间件将 `PalValidationException` 映射为 400，将 `HandlerNotFoundException` 映射为 404，未处理异常映射为 500。

端点映射器（`MapCommand<TCommand>` / `MapCommand<TCommand, TResponse>` / `MapQuery<TQuery, TResult>`）把 HTTP 端点绑定到 CQRS 分发，完整用法见[教程](tutorial.md)；其请求体的**形态验证**（必填/长度/范围）由宿主侧的平台内置验证负责，与框架的 `IPalValidator<T>` 分工见[验证](#验证)一节。

## 流式读（SSE）

流式响应（LLM token 流、大结果集分段回传）用独立读形态 `IStreamQuery<TItem>`，不经 Dispatcher、不走管道行为，由 `MapStreamQuery` 端点原语驱动。设计动机与生命周期约定见 [ADR-030](decisions/030-streaming-read-shape.md)，规范见 [conventions.md](conventions.md) §1.10。

### 1. 定义查询与 handler

```csharp
// 查询：标记接口，刻意不实现 IQuery<T>（两条读路径互不路由）
public sealed record TokenStreamQuery(string Prompt) : IStreamQuery<TokenChunk>;

public sealed record TokenChunk(int Index, string Text);

public sealed class TokenStreamHandler : IStreamQueryHandler<TokenStreamQuery, TokenChunk>
{
    // ct 契约（D5）：必须响应取消——客户端断开时 finally 里停计费、释放 provider 连接。
    // [EnumeratorCancellation] 让 await foreach 消费者传入的令牌优先于实现内部令牌
    public async IAsyncEnumerable<TokenChunk> HandleAsync(
        TokenStreamQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var token in llmClient.StreamAsync(query.Prompt, ct))
            yield return new TokenChunk(token.Index, token.Text);
    }
}
```

### 2. 注册与 JSON metadata

```csharp
builder.Services.AddPalStreamQueryHandler<TokenStreamQuery, TokenChunk, TokenStreamHandler>();

// 源生成 metadata（AOT 安全——仓库全局禁用反射序列化）
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TokenChunk))]
[JsonSerializable(typeof(TokenStreamQuery))]
internal sealed partial class StreamJsonContext : JsonSerializerContext;
```

同一 `(查询, 元素)` 注册两个不同 handler 会在注册期抛 `InvalidOperationException`（静默覆盖是配置错误）。验证沿用 `IPalValidator<TQuery>`：端点在 SSE 头写出前跑验证，失败返回 400 ProblemDetails（与 `MapCommand`/`MapQuery` 同形态，状态码可回退）。

### 3. 映射端点（GET 与 POST 双形态）

```csharp
using PalDDD.Hosting.AspNetCore;

// GET 形态：浏览器原生 EventSource 兼容
app.MapStreamQuery<TokenStreamQuery, TokenChunk>(
    "/tokens",
    ctx => new TokenStreamQuery(ctx.Request.Query["prompt"].ToString()),
    StreamJsonContext.Default.TokenChunk);

// POST 形态：JSON body 承载 prompt 类请求体
app.MapStreamQuery<TokenStreamQuery, TokenChunk>(
    "/tokens/stream",
    StreamJsonContext.Default.TokenStreamQuery,
    StreamJsonContext.Default.TokenChunk);
```

两个重载的后四个参数都可选传 `formatFrame: item => "..."` 定制帧文本（支持 `event:`/`id:` 行的分型事件流）；不传时默认帧为 data-only JSON（`data: {json}\n\n`）。

### 4. 消费方约束（浏览器）

- **GET 形态**可用原生 `EventSource`：`new EventSource("/tokens?prompt=hi")`，`onmessage` 收到 `JSON.parse(event.data)`。
- **POST 形态** `EventSource` 不发 POST——须用 fetch 加 ReadableStream 手工解析：

```javascript
const response = await fetch("/tokens/stream", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ prompt: "hi" })
});
const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
let buffer = "";
while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += value;
    // 按 "\n\n" 切帧，去掉 "data: " 前缀后 JSON.parse
}
```

### 5. 中断与错误语义

- **客户端断开**：端点传 `context.RequestAborted` 给 handler，取消传播进迭代器；流按截断静默收尾（无 error 帧、无 500）。
- **中途异常**：SSE 头已写出、状态码无法回退，端点补一个 `event: error` 帧（`data: {"message":"The stream failed unexpectedly."}`）后结束流。消费方必须同时处理「截断」（无终止帧的连接中断）与「error 帧」两种形态。
- **压缩中间件**：响应压缩中间件会缓冲事件流，破坏流式语义——流式端点前的压缩需实测，必要时端点级禁用并记载中间件排序（ADR-030 已知约束）。

### 6. 审计模式（D6）

框架只记机械生命周期（流开始/结束/item 数/错误，走 `IPalLogger<T>`，`AddPalLogging` 后可见）。**响应内容审计由应用层负责**：在 handler 流结束后追加 EventLog——框架不自动捕获载荷（隐私、体积、领域语义三样均属应用层）：

```csharp
public async IAsyncEnumerable<TokenChunk> HandleAsync(
    TokenStreamQuery query, [EnumeratorCancellation] CancellationToken ct)
{
    var count = 0;
    try
    {
        await foreach (var token in llmClient.StreamAsync(query.Prompt, ct))
        {
            count++;
            yield return new TokenChunk(token.Index, token.Text);
        }
    }
    finally
    {
        // 流结束（含取消）后审计：只记计数与结果，不记载荷
        await eventLog.AppendAsync(new StreamCompleted(query.Prompt, count), ct: default);
    }
}
```

### 7. agent 场景：分型事件、心跳与重连

agent 流的三个结构特征各有一个对应原语（ADR-030 Phase 2 收窄实施）。

**分型事件（event 名 + id 行）**：流里混着正文章 token、工具进度、最终 usage 元数据时，用 `eventSelector` 让客户端按事件名分流，免解析 JSON 体；`idSelector` 给每项一个事件 ID（断线重连的续传游标）：

```csharp
app.MapStreamQuery<TokenStreamQuery, TokenChunk>(
    "/tokens",
    ctx => new TokenStreamQuery(ctx.Request.Query["prompt"].ToString()),
    StreamJsonContext.Default.TokenChunk,
    eventSelector: chunk => chunk.Kind switch
    {
        ChunkKind.Tool => "tool",
        ChunkKind.Final => "done",
        _ => "token"
    },
    idSelector: chunk => chunk.Index.ToString());
```

产出帧为 `event: {名}\nid: {id}\ndata: {json}\n\n`（缺项行省略）。浏览器侧按事件名监听：`es.addEventListener("done", e => renderFinal(JSON.parse(e.data)))`。`formatFrame` 委托仍可用作完整帧覆写（优先级高于两个选择器）。

**心跳（keepAliveInterval）**：agent 流的静默间隙属于常态，首 token 前可停几十秒，工具调用轮的间隙达分钟级；nginx 默认 60 秒、部分云 LB 四分钟空闲即断连，长 run 会在工具轮之间被中间设备掐断。传 `keepAliveInterval`（如 `TimeSpan.FromSeconds(15)`）后，流静默超过该间隔即写注释帧 `:keepalive`（SSE 规范内客户端忽略，中间设备的重连判定被重置），不计入 item 数、不影响审计。默认关闭（null）。

**重连续传（Last-Event-ID）**：EventSource 断线重连自动携带 `Last-Event-ID` 请求头（末个收到的 id 行内容）。GET 形态在 `bindQuery` 内经 `GetLastEventId()` 读入查询，handler 按该 ID 定位续读点：

```csharp
app.MapStreamQuery<TokenStreamQuery, TokenChunk>(
    "/tokens",
    ctx => new TokenStreamQuery(ctx.Request.Query["prompt"].ToString(), ctx.GetLastEventId()),
    StreamJsonContext.Default.TokenChunk,
    idSelector: chunk => chunk.Index.ToString());
```

续传语义的边界（防误用）：框架只提供请求头读取与 id 行写出，不缓冲已产出项。实时生成流的中途缓冲属于另一设计（per-stream Channel，ADR-030 分期节收窄暂不建），实时生成的失败恢复走客户端整轮重开；该头的典型受益场景是持久流回放重连，比如回看某次已完成的 agent run（EventLog 有序读的位置游标即天然的事件 ID）。

**回放与断点续看（EventLog 参考实现）**：一轮 agent run 结束后由 handler 追加一条 EventLog 记录（完整记录，不逐 token 写），回放端点用同帧形状重新发布，`Last-Event-ID` 直接当作 EventLog 的 `fromVersion`：

```csharp
public sealed class AgentReplayHandler(IEventLog eventLog)
    : IStreamQueryHandler<AgentReplayQuery, AgentChunk>
{
    public async IAsyncEnumerable<AgentChunk> HandleAsync(
        AgentReplayQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        // Last-Event-ID 即 EventLog 流版本：+1 从下一条开始（含断线重连的续看）
        var fromVersion = long.TryParse(query.LastEventId, out var last) ? last + 1 : 0;
        await foreach (var recorded in eventLog.ReadStreamAsync(
            $"agent-run-{query.Run}", fromVersion, cancellationToken: ct))
        {
            yield return ToChunk(recorded);   // 应用层把记录映射回帧元素
        }
    }
}
```

完整可运行样本见 `samples/PalDDD.AgentSample`（流式产出 + 工具幂等 + EventLog 审计 + 断点续看四项接线，17 条断言）。

**遥测（2026-10-18 接入，2026-10-19 扩到断线续跑）**：端点在验证通过、进入枚举后开启 `Stream Query` Activity（tag `pal.stream.query`/`pal.stream.item` 为类型名），首个元素写出时记 `first-item` 事件——**TTFT = 该事件时间戳 − span 起点**，无需新增直方图。七个零 tag 计数器：`paldd.stream.started/completed/failed/cancelled/items` 五个终态与吞吐计数（取消独立于失败：客户端断开是正常终止，混入 failed 会污染错误率告警），加断线续跑专用的 `paldd.stream.attached`（成功附着数，含首次、重连与接管）与 `paldd.stream.gap`（gap 帧写出次数）。普通流式读不产生后两项；断线续跑的 run 与普通流式读共用前五项，run 未附着时也计 started（产出由注册表泵驱动，与客户端是否在看无关）。订阅方式与其他遥测一致（引用 `PalDDD` ActivitySource/Meter）。

### 8. 断线续跑（ADR-031）

`MapStreamQuery` 的语义是**连接断开即取消整轮**。长 agent run 需要「切页/弱网后回来接着看」时改用断线续跑端点组：run 独立于 HTTP 请求存在，断开只解除附着。

```csharp
builder.Services.AddDetachedStreamRuns(options =>
{
    options.MaxBufferedItems = 1024;                    // 环形缓冲容量（项）
    options.DetachGrace = TimeSpan.FromMinutes(5);      // 无附着宽限，到期取消（不白烧 token）
    options.MaxRunDuration = TimeSpan.FromMinutes(10);  // 单 run 最长存活
});

// 启动并附着：响应头 X-Run-Id 供重连
app.MapDetachedStreamQuery<TokenStreamQuery, TokenChunk>(
    "/agent/run",
    ctx => new TokenStreamQuery(ctx.Request.Query["prompt"].ToString()),
    StreamJsonContext.Default.TokenChunk,
    eventSelector: chunk => chunk.Kind,
    idSelector: chunk => chunk.Index.ToString());

// POST 启动形态：prompt 类请求体超过 URL 长度时用（浏览器侧 fetch + ReadableStream 手工解析）
app.MapDetachedStreamQuery<TokenStreamQuery, TokenChunk>(
    "/agent/run/post",
    StreamJsonContext.Default.TokenStreamQuery,
    StreamJsonContext.Default.TokenChunk,
    eventSelector: chunk => chunk.Kind,
    idSelector: chunk => chunk.Index.ToString());

// 重连附着（遵守 Last-Event-ID：先回放缓冲，再转 live）
app.MapStreamRunAttach<TokenChunk>("/agent/runs", StreamJsonContext.Default.TokenChunk);

// 显式取消（幂等，终态返回 204）
app.MapStreamRunCancel("/agent/runs");
```

POST 启动的错误形态与 `MapStreamQuery` POST 重载一致：非 JSON Content-Type 415、畸形 JSON 400、空 body 400，均在 SSE 头写出前返回。

**生产部署（授权与限流）**：三个端点返回 `IEndpointConventionBuilder`，可直接链 `RequireAuthorization()` / `RequireRateLimiting()`。框架只按 `runId` 做存在性检查（128 位随机 ID，不可猜测），**不校验 run 所有权**；多租户应用须在端点过滤器或应用层把 run 绑定到当前用户。浏览器跨域读 `X-Run-Id` 需在 CORS 中 `WithExposedHeaders("X-Run-Id")`。

客户端流程：

```javascript
// 1. 启动：拿 X-Run-Id
const first = await fetch("/agent/run?prompt=hi");
const runId = first.headers.get("X-Run-Id");
// ... 读取流，中途断线（取消请求即 abort 连接；不要用 response.dispose 等待）

// 2. 重连：EventSource 会自动带 Last-Event-ID；fetch 则手工带
const reattach = await fetch(`/agent/runs/${runId}/stream`, {
    headers: { "Last-Event-ID": lastSeenId }
});
```

**语义边界**：缓冲按项计数，溢出丢最旧并在重连首帧发 `event: gap`（携带 `oldestIndex`，客户端自行决定重开还是接受缺口）；终态 run 在 `RetentionAfterCompletion` 内可回看，收尾帧为 `event: done|cancelled|error`。保留是**尽力而为**：总跟踪数达到 `2 × MaxConcurrentRuns` 时最旧的终态 run 提前逐出（回看得 404），内存上界由此收敛为运行中与保留终态各至多 `MaxConcurrentRuns`。注册表是**进程内**状态：重启丢进行中 run；多实例部署需会话亲和。完成态审计与回放仍走 EventLog（见上一节）。

**客户端两个坑**：SSE 流未读完时 `response.Dispose()` 会同步等连接关闭（与仍在写的服务端互相等待），断线请用取消请求；重连用新连接（连接池在 abort 后可能异常）；`done`/`cancelled` 收尾帧不带 `id`，按 id 解析须过滤。

完整可运行示例见 `samples/PalDDD.AgentSample` 第 6 段。

## Kafka / RabbitMQ

broker adapter 实现 `IMessageBroker`。它们只依赖 `PalDDD.Messaging` 和 `PalDDD.Serialization`，不会引用 `PalDDD.Serialization.Json`，因此可以替换为其他序列化实现。

非泛型发布路径要求调用方提供 `messageId`：

```csharp
await broker.PublishAsync(message, descriptor, messageId, ct);
```

Outbox processor 会使用 `OutboxMessage.Id` 作为 `messageId`，并把 `OutboxMessage` 上的 correlation/causation/trace metadata 作为 `MessagePublishContext` 传给 broker。消息 payload 不需要实现基础设施 marker interface；wire name、schema version 和 content type 来自 `MessageDescriptor`。
