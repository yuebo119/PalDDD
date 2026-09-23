# 使用指南

本文档展示 Pal.DDD 的常见使用路径。示例聚焦 API 形状；实际项目可按需要拆分到不同文件。

## 安装和注册核心服务

```csharp
using Microsoft.Extensions.DependencyInjection;
using PalDDD.DependencyInjection;

var services = new ServiceCollection();

services.AddPalCoreStack();
```

`AddPalCoreStack()` 是推荐的核心入口，等价于 `AddPalDDD()` + `AddPalPipelineBehaviors()` + `AddPalIdentity()`。`AddPalFullStack()` 当前也等价于核心栈：它不会自动引用序列化、持久化、Broker 或 ASP.NET Core 适配器，避免基础设施依赖越过 Clean Architecture 边界。

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
var dispatcher = provider.GetRequiredService<Dispatcher>();
var orderId = await dispatcher.SendAsync(new CreateOrderCommand("Contoso", 100m));
```

> **前提**：Handler 由 `HandlerRegistrar`（`IHostedService`）在**宿主启动时**自动注册并 `Freeze()`。因此需运行在 `IHost` 宿主（`WebApplicationBuilder` / `HostBuilder`）下；纯 `BuildServiceProvider()` 场景（单测/控制台工具）`IHostedService` 不执行，需手动 `AddPalCommandHandler` 后显式触发注册或直接构造 Dispatcher。

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

> ⚠️ **事务前提（TX1，2026-09-20）**：Outbox 模式的原子性由「业务数据写入与消息行写入在**同一数据库事务**内提交」保证——这是**使用方职责**：调用方必须在业务 DbContext 事务/UnitOfWork 内写入 outbox 消息行（`AddMessage` + 同事务 `SaveChanges`），框架的后台发布器只负责事务提交后的可靠投递。若消息行与业务数据不同事务，将失去 exactly-once-write 保证（业务回滚但消息已入队 → 幽灵消息）。

> ⚠️ **跨栈误配警示（2026-09-20 补）**：EF 业务上下文配 Dapper/PalORM 的 `IPalOutboxStore` 时，`AddMessage` 是**即时 INSERT**——无活动 ambient 事务即独立自动提交，与业务写入**不在同一事务**：业务回滚后 outbox 行仍在（孤儿消息）。这是跨栈混配的误配场景，不是默认路径（默认 EF + EF 流里 outbox 行进同一 `SaveChanges` 事务）。要么保持栈一致，要么确保 Dapper/PalORM store 与业务写入共享同一 `IUnitOfWork`/`DbTransaction`。

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

生产环境可从 `PalDDD.Transactions.EFCore` 派生 `OutboxDbContext`，或按方言派生 `PostgreSqlOutboxDbContext`/`MySqlOutboxDbContext`/`SqliteOutboxDbContext`（ADR-012 方言粒度）以复用原子租约获取。`SqlServerOutboxDbContext` 当前标 `[Obsolete]`（源码声明 v4.0 移除）且零测试覆盖，属**实验性/未验证**，生产请优先使用已验证方言。适配器会配置 pending 查询索引、payload 必填、trace/correlation 字段长度和错误字段长度；`MarkProcessed` 会清理 lease/retry 状态，`ReleaseForRetry` 会释放 lease 并设置 `NextAttemptAt`。⚠️ **Dapper 栈 `MarkProcessed` 被租约 token 拒阻时不改写入参对象**（3.1.0 起与 PalORM 一致）：此前被拒路径会把传入消息对象的租约字段清空，使调用方持有的对象与数据库实际状态不一致。

**并行发布（3.0.0 起）**：`OutboxOptions.MaxDegreeOfParallelism`（默认 1，经 `AddPalOutbox(o => o.MaxDegreeOfParallelism = n)` 配置）>1 时，批内消息按分区交由 per-worker scope（独立 store/DbContext 实例）并行完成「反序列化 → 发布 → 标记」，吞吐随并行度提升；租约/fencing 互斥不受影响。**前提**：① broker 发布须线程安全（Kafka producer 天然支持；RabbitMQ 单 channel 并发发布的 7.x 语义已核实——publisher-confirms 仅护帧发送段、确认等待不串行）；② 消费方幂等（并行加速下乱序提交概率上升）。直构造 `OutboxBatchProcessor` 的调用方不受影响（`IServiceScopeFactory` 为可选尾参；并行度 >1 且缺失时运行期 fail-fast 并给出指引）。

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

`InboxProcessor` 会发出 `Inbox Process` span。它使用同一个 `PalActivitySource.Name`，仅包含 `pal.inbox.consumer` 和 `pal.inbox.result` 标签——`pal.inbox.message_id` 已按 ITM-229 标准从 tag 移除（高基数：每消息唯一，且可能含业务 ID）；结果值为 `processed`、`skipped` 或 `failed`。

同一幂等消费边界还会记录 `paldd.inbox.processed`、`paldd.inbox.skipped` 和 `paldd.inbox.failed` metrics，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集。

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

适配器会配置 `GlobalPosition` 主键、`(StreamName, StreamVersion)` 唯一索引和 `EventId` 唯一索引，并持久化 payload、metadata、审计字段和 trace context。`GlobalPosition` 由 `EventLogPositionReserver` 的 Hi/Lo 段分配器管理，而非数据库自增 identity。分配器缓存 chunk（默认 100 个位置）在进程内，仅当 chunk 耗尽时通过乐观 CAS（Revision 并发令牌）更新持久化 allocator 行；关系型 provider 下 append 使用默认隔离级别（ReadCommitted），stream 级别并发由唯一索引保障。

`AppendAsync` 还会发出 `EventLog Append` span。它使用同一个 `PalActivitySource.Name`，仅包含 `pal.eventlog.event_count` 标签——流名、stream version 与 global position 等高基数信息（含聚合 ID）已按 ITM-229 标准从 tag 移除，位置信息由 `AppendEventsResult` 返回值承载。

同一 append 成功边界还会记录 `paldd.eventlog.appended` metric，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集事件日志写入吞吐。

`ReadStreamAsync` 和 `ReadAllAsync` 分别会发出 `EventLog ReadStream` / `EventLog ReadAll` span（无高基数 tag——起始 stream version / global position 与读取数量均已按 ITM-229 标准移除）。

完整枚举读取结果后还会记录 `paldd.eventlog.read` metric，应用层可用于观察审计回放、投影修复和跨上下文诊断的事件读取吞吐。

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
```

异常中间件将 `PalValidationException` 映射为 400，将 `HandlerNotFoundException` 映射为 404，未处理异常映射为 500。

端点映射器（`MapCommand<TCommand>` / `MapCommand<TCommand, TResponse>` / `MapQuery<TQuery, TResult>`）把 HTTP 端点绑定到 CQRS 分发，完整用法见[教程](tutorial.md)；其请求体的**形态验证**（必填/长度/范围）由宿主侧的平台内置验证负责，与框架的 `IPalValidator<T>` 分工见[验证](#验证)一节。

## Kafka / RabbitMQ

broker adapter 实现 `IMessageBroker`。它们只依赖 `PalDDD.Messaging` 和 `PalDDD.Serialization`，不会引用 `PalDDD.Serialization.Json`，因此可以替换为其他序列化实现。

非泛型发布路径要求调用方提供 `messageId`：

```csharp
await broker.PublishAsync(message, descriptor, messageId, ct);
```

Outbox processor 会使用 `OutboxMessage.Id` 作为 `messageId`，并把 `OutboxMessage` 上的 correlation/causation/trace metadata 作为 `MessagePublishContext` 传给 broker。消息 payload 不需要实现基础设施 marker interface；wire name、schema version 和 content type 来自 `MessageDescriptor`。
