# 使用指南

本文档展示 Pal.DDD 的常见使用路径。示例聚焦 API 形状；实际项目可按需要拆分到不同文件。

## 安装和注册核心服务

```csharp
using Microsoft.Extensions.DependencyInjection;
using PalDDD.DependencyInjection;

var services = new ServiceCollection();

services.AddPalCoreStack();
```

`AddPalCoreStack()` 是推荐的核心入口，等价于 `AddPalDDD()` + `AddPalPipelineBehaviors()`。`AddPalFullStack()` 当前也等价于核心栈：它不会自动引用序列化、持久化、Broker 或 ASP.NET Core 适配器，避免基础设施依赖越过 Clean Architecture 边界。

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

JSON 是默认消息序列化器，零反射且跨语言友好。当追求**更高吞吐 / 更小 payload**（典型 3-5x 加速、payload 缩减 2-4x），可切换到 MemoryPack 二进制序列化器。MemoryPack 内置 source generator，同样 AOT 安全、零反射。

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

```csharp
using PalDDD.Transactions;

services.AddPalOutbox();
```

调用方还必须注册：

- `IMessageSerializer`
- `IMessageCatalog`
- `IMessageBroker`
- `IPalOutboxStore`

`IPalOutboxStore.LeasePendingMessagesAsync` 必须提供原子租约语义。SQL Server EF Core base context 提供了基于 `UPDLOCK` / `READPAST` 的实现。

生产环境可从 `PalDDD.Transactions.EFCore` 派生 `OutboxDbContext`，或在 SQL Server 上派生 `SqlServerOutboxDbContext` 以复用原子租约获取。适配器会配置 pending 查询索引、payload 必填、trace/correlation 字段长度和错误字段长度；`MarkProcessed` 会清理 lease/retry 状态，`ReleaseForRetry` 会释放 lease 并设置 `NextAttemptAt`。

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
using PalDDD.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(PalActivitySource.Name));
```

`Outbox Process` span 包含 `pal.outbox.batch_size`、`pal.outbox.processed`、`pal.outbox.dead` 和 `pal.outbox.retried` 标签。

同一批处理还会记录 `paldd.outbox.processed` 和 `paldd.outbox.failed` metrics，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集。

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

`InboxProcessor` 会发出 `Inbox Process` span。它使用同一个 `PalActivitySource.Name`，包含 `pal.inbox.consumer`、`pal.inbox.message_id` 和 `pal.inbox.result` 标签；结果值为 `processed`、`skipped` 或 `failed`。

同一幂等消费边界还会记录 `paldd.inbox.processed`、`paldd.inbox.skipped` 和 `paldd.inbox.failed` metrics，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集。

生产环境可引用 `PalDDD.Transactions.EFCore` 程序集，`using PalDDD.Transactions;` 后派生 `InboxDbContext`，并通过 DI 将该上下文作为 `IInboxStore` 使用。适配器会配置 `(ConsumerName, MessageId)` 唯一索引、processed 时间索引和 lease 状态索引；已处理消息返回 `null`，未超时 `Processing` 消息返回 `null`，失败或超时消息会重新进入 `Processing` 以支持 broker 重投递。

## 使用 Saga EF Core Store

生产环境可引用 `PalDDD.Transactions.EFCore` 程序集，`using PalDDD.Transactions;` 后派生 `SagaStateDbContext<TState>`，并通过 DI 将该上下文作为 `ISagaStateStore<TState>` 使用。适配器会配置 `SagaId` 主键、active/lease 查询索引、`Version` 并发令牌，并用 source-generated JSON converter 持久化 `StepStartedAt` 和 `ExecutedStepKeys`，避免 reflection-based serialization fallback。Dapper 适配器需要完整快照时，构造 `DapperSagaStateStore<TState>` 时传入 source-generated `JsonTypeInfo<TState>`，即可把派生状态写入 `saga_data`。

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
            Guid.NewGuid(),
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

生产环境可从 `PalDDD.EventLog.EFCore` 派生 `EventLogDbContext`，并通过 DI 将该上下文作为 `IEventLog` 使用：

```csharp
using Microsoft.EntityFrameworkCore;
using PalDDD.EventLog;

public sealed class AppEventLogDbContext(DbContextOptions<AppEventLogDbContext> options)
    : EventLogDbContext(options);

services.AddDbContext<AppEventLogDbContext>(options =>
{
    options.UseSqlServer(connectionString);
});
services.AddScoped<IEventLog>(sp => sp.GetRequiredService<AppEventLogDbContext>());
```

适配器会配置 `GlobalPosition` 主键、`(StreamName, StreamVersion)` 唯一索引和 `EventId` 唯一索引，并持久化 payload、metadata、审计字段和 trace context。`GlobalPosition` 由 `EventLogPositionReserver` 的 Hi/Lo 段分配器管理，而非数据库自增 identity。分配器缓存 chunk（默认 100 个位置）在进程内，仅当 chunk 耗尽时通过乐观 CAS（Revision 并发令牌）更新持久化 allocator 行；关系型 provider 下 append 使用默认隔离级别（ReadCommitted），stream 级别并发由唯一索引保障。

`AppendAsync` 还会发出 `EventLog Append` span。它使用同一个 `PalActivitySource.Name`，包含 `pal.eventlog.stream`、`pal.eventlog.event_count`、`pal.eventlog.first_stream_version`、`pal.eventlog.last_stream_version`、`pal.eventlog.first_global_position` 和 `pal.eventlog.last_global_position` 标签。

同一 append 成功边界还会记录 `paldd.eventlog.appended` metric，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集事件日志写入吞吐。

`ReadStreamAsync` 和 `ReadAllAsync` 分别会发出 `EventLog ReadStream` / `EventLog ReadAll` span。读取 span 包含起始 stream version 或 global position，以及 `pal.eventlog.read_count` 标签。

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

生产环境可从 `PalDDD.Projections.EFCore` 派生 `ProjectionCheckpointDbContext`，并通过 DI 将该上下文作为 `IProjectionCheckpointStore` 使用。适配器会配置 `(ProjectionName, SourceName, Position)` 复合主键、projection/source/status 查询索引和 `UpdatedAt` 并发令牌，用于跨实例投影幂等处理与重建 checkpoint reset。

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

`IdempotencyProcessor` 会发出 `Idempotency Execute` span。它使用同一个 `PalActivitySource.Name`，包含 `pal.idempotency.operation`、`pal.idempotency.key` 和 `pal.idempotency.result` 标签；结果值为 `executed`、`cached`、`skipped` 或 `failed`。

同一执行边界还会记录 `paldd.idempotency.executed`、`paldd.idempotency.cached`、`paldd.idempotency.skipped` 和 `paldd.idempotency.failed` metrics，应用层可通过 OpenTelemetry `AddMeter(PalActivitySource.Name)` 采集。

生产环境可从 `PalDDD.Idempotency.EFCore` 派生 `IdempotencyDbContext`，并通过 DI 将该上下文作为 `IIdempotencyStore` 使用。适配器会配置 `(OperationName, Key)` 复合主键、过期时间索引、lease 状态索引和 `UpdatedAt` 并发令牌，用于跨实例幂等消费与 API retry 去重。

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
        When("Initial", typeof(OrderSubmitted), new SagaStep(
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

## Kafka / RabbitMQ

broker adapter 实现 `IMessageBroker`。它们只依赖 `PalDDD.Messaging` 和 `PalDDD.Serialization`，不会引用 `PalDDD.Serialization.Json`，因此可以替换为其他序列化实现。

非泛型发布路径要求调用方提供 `messageId`：

```csharp
await broker.PublishAsync(message, descriptor, messageId, ct);
```

Outbox processor 会使用 `OutboxMessage.Id` 作为 `messageId`，并把 `OutboxMessage` 上的 correlation/causation/trace metadata 作为 `MessagePublishContext` 传给 broker。消息 payload 不需要实现基础设施 marker interface；wire name、schema version 和 content type 来自 `MessageDescriptor`。
