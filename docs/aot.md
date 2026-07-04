# AOT 与性能约束

Pal.DDD 的默认设计是 AOT-first。`Directory.Build.props` 对全仓库启用：

```xml
<IsAotCompatible>true</IsAotCompatible>
<IsTrimmable>true</IsTrimmable>
<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
```

这意味着新增代码必须优先选择可被 trim/AOT analyzer 静态分析的实现。

## JSON 序列化规则

允许：

```csharp
[JsonSerializable(typeof(OrderSubmitted))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

builder.Add(AppJsonContext.Default.OrderSubmitted, name: "order-submitted");
```

禁止在生产路径使用：

```csharp
JsonSerializer.Serialize(message);
JsonSerializer.Deserialize<OrderSubmitted>(payload);
```

原因：

- 默认反射序列化不适合 Native AOT。
- 仓库已关闭 `JsonSerializerIsReflectionEnabledByDefault`，反射路径会在运行时快速失败。
- `MessageDescriptor` 要求 `JsonTypeInfo`，使消息类型、wire name、schema version 和 serializer metadata 在启动时显式绑定。

## Handler 注册规则

允许：

```csharp
services.AddPalCommandHandler<CreateOrderCommand, Guid, CreateOrderHandler>();
services.AddPalQueryHandler<GetOrderQuery, string, GetOrderHandler>();
services.AddPalEventHandler<OrderCreated, OrderCreatedHandler>();
```

禁止：

- 按程序集扫描 handler。
- 使用 `Activator.CreateInstance` 动态创建 handler。
- 使用 `MakeGenericType` 在运行时拼接泛型 handler。
- 使用 request attribute 隐式声明事务策略。

`PalDDD.CQRS` 只保留 request/handler/dispatcher/pipeline 核心能力。事务由应用层或外层持久化适配层显式处理，避免 CQRS 包依赖 repository 抽象，也避免 attribute 驱动的隐藏策略。

## 源码生成器

### 强类型 ID

```csharp
[GenerateId(typeof(Guid))]
public readonly partial record struct CustomerId;

var id = CustomerId.New();
var parsed = CustomerId.From(Guid.Parse("3d58a70e-cb5c-4abc-9693-a765f8fb4a88"));
```

生成器会补充 factory、`Value`、`JsonConverter` 和 `TypeConverter`。

### 智能枚举

```csharp
[GenerateEnum]
public sealed partial class OrderStatus : SmartEnum<OrderStatus, int>
{
    public static readonly OrderStatus Draft = new(1, nameof(Draft));
    public static readonly OrderStatus Submitted = new(2, nameof(Submitted));

    private OrderStatus(int value, string name) : base(value, name) { }
}
```

生成器会在静态构造路径注册所有已知值，使 `FromValue` 和 `TryFromValue` 使用 `FrozenDictionary` 查找。

### 消息

`[GenerateMessage]` 用于标记可生成消息目录辅助代码的类型。消息必须声明稳定 wire name；`SchemaVersion` 必须大于等于 1，且同一编译单元中的 wire name 不能重复。稳定 wire name 只允许小写字母、数字、`.` 和 `-`，并且必须以 `.v{SchemaVersion}` 结尾。违反这些规则时，源码生成器会报告 `PALMSG001`、`PALMSG002`、`PALMSG003`、`PALMSG004` 或 `PALMSG005` 编译期错误。

领域事件类型必须是 `sealed`，否则 analyzer 会报告 `PDDD012`；领域事件也必须声明 `[GenerateMessage]`，否则 analyzer 会报告 `PDDD005`。这些规则保证 Outbox、broker、EventLog replay 和 schema evolution 不依赖可继承事件层级、CLR 类型名或运行时反射推断消息契约。
领域事件的 message name 必须是稳定小写 wire name，否则 analyzer 会报告 `PDDD009`。
领域事件的 message name 必须以 `.v{SchemaVersion}` 结尾，否则 analyzer 会报告 `PDDD010`。
领域事件的 `SchemaVersion` 必须大于等于 1，否则 analyzer 会报告 `PDDD011`。
领域事件的 message name 还必须以 bounded context 为前缀；例如 `[BoundedContext("orders")]` 对应 `orders.order-submitted.v1`。不匹配时 analyzer 会报告 `PDDD008`。
领域事件的 `IDomainEvent.EventName` 必须是 string literal，并与 `[GenerateMessage(Name = "...")]` 完全一致；不匹配时 analyzer 会报告 `PDDD015`。

```csharp
[BoundedContext("orders")]
[GenerateMessage(Name = "orders.order-submitted.v1", SchemaVersion = 1)]
public sealed record OrderSubmitted(Guid OrderId) : DomainEvent, IDomainEvent
{
    public static string EventName => "orders.order-submitted.v1";
}
```

实际运行时仍需要 source-generated `JsonTypeInfo` 加入 `MessageCatalogBuilder`。

## 后台服务规则

`BackgroundService` 默认是 singleton。需要 scoped store、DbContext 或业务服务时，必须使用 `IServiceScopeFactory` 创建 scope，再在 scope 中解析 scoped processor。

Pal.DDD 当前做法：

- `OutboxProcessor` -> scoped `OutboxBatchProcessor`
- `SagaProcessor<TState>` -> scoped `SagaTimeoutProcessor<TState>`
- `InboxProcessor` 本身是 scoped processor，不是 hosted service

## EF Core 适配层

`PalDDD.Repository.EFCore`、`PalDDD.Transactions.EFCore`、`PalDDD.EventLog.EFCore`、`PalDDD.Projections.EFCore` 和 `PalDDD.Idempotency.EFCore` 显式关闭 AOT compatibility metadata：

```xml
<IsAotCompatible>false</IsAotCompatible>
<IsTrimmable>false</IsTrimmable>
<VerifyReferenceAotCompatibility>false</VerifyReferenceAotCompatibility>
```

这是有意的边界选择。核心抽象保持 AOT-safe，EF Core 的动态模型构建和 provider 限制隔离在可选适配层。

## 性能策略

- `FrozenDictionary` 用于冻结后的 handler、message、smart enum 和事件处理器查找。
- `ValueTask` 用于高频 async API。
- source-generated logging 用于热路径日志。
- `TimeProvider` 用于可测试时间。
- Outbox 使用租约批处理，Saga timeout 使用有界批量扫描。
- EventLog 使用显式 stream/version/position，不依赖运行时类型扫描或 serializer 发现。
- EventLog append ActivitySource instrumentation 使用静态 `PalActivitySource`，不依赖 OpenTelemetry runtime package。
- EventLog read ActivitySource instrumentation 使用静态 `PalActivitySource`，不依赖 OpenTelemetry runtime package。
- EventLog projection adapter 使用 `MessageDescriptor` 和 `IMessageSerializer`，不启用反射反序列化 fallback。
- EventLog EF Core 持久化隔离在 `PalDDD.EventLog.EFCore` 可选适配器；核心事件日志包不引用 EF Core。
- Projection 和 Idempotency store 使用显式 key，不依赖运行时扫描。
- Outbox trace propagation 使用显式 `MessagePublishContext`，不依赖 ambient 自定义上下文或运行时扫描。
- Outbox ActivitySource instrumentation 使用静态 `PalActivitySource`，不依赖 OpenTelemetry runtime package。
- Outbox EF Core 持久化隔离在 `PalDDD.Transactions.EFCore` 可选适配器；核心 transactions 包不引用 EF Core。
- Inbox ActivitySource instrumentation 使用静态 `PalActivitySource`，不依赖 OpenTelemetry runtime package。
- Inbox EF Core 持久化隔离在 `PalDDD.Transactions.EFCore` 可选适配器；核心 transactions 包不引用 EF Core。
- Saga EF Core 持久化隔离在 `PalDDD.Transactions.EFCore` 可选适配器；Saga 集合字段使用 source-generated JSON converter，不启用反射 fallback。
- Idempotency ActivitySource instrumentation 使用静态 `PalActivitySource`，不依赖 OpenTelemetry runtime package。
- Idempotency EF Core 持久化隔离在 `PalDDD.Idempotency.EFCore` 可选适配器；核心幂等包不引用 EF Core。
- Projection checkpoint EF Core 持久化隔离在 `PalDDD.Projections.EFCore` 可选适配器；Dapper 持久化隔离在 `PalDDD.Dapper` 可选适配器；核心 projection 包不引用 EF Core 或 Dapper。
- Projection rebuild ActivitySource instrumentation 使用静态 `PalActivitySource`，不依赖 OpenTelemetry runtime package。
- Schema Evolution 使用已注册 `JsonTypeInfo` 和显式 converter，不启用反射 fallback。

## Dapper 适配层 IL3058 抑制

Dapper 适配项目使用项目级 IL3058 抑制（`<NoWarn>$(NoWarn);IL3058</NoWarn>`），把 Dapper 的动态能力边界限制在外圈 adapter。`Dapper.AOT` 1.0.52 已接入 ``PalDDD.Dapper`、`PalDDD.Dapper.MySql`、`PalDDD.Dapper.PostgreSql` 和 `PalDDD.Dapper.Sqlite` 四个 Dapper 项目：项目文件显式引用包并启用 `<InterceptorsPreviewNamespaces>...;Dapper.AOT</InterceptorsPreviewNamespaces>`，保留分析器诊断。

当前不全局启用 `[module:DapperAot]`。SQLite TEXT 列上的 `Guid` / `DateTimeOffset` 兼容性仍需按具体查询路径验证，EventLog 和 Projection checkpoint 读取路径使用 DTO 物化并通过集成测试覆盖。待 Dapper.AOT RowFactory 自定义类型映射成熟后，再逐方法启用预编译物化。

所有库代码的 await 调用（143+ 处）均使用 `ConfigureAwait(false)`。所有时间获取（44+ 处）通过 `TimeProvider` 而非 `DateTimeOffset.UtcNow`。

## 检查清单

新增生产代码时确认：

- 没有新增 `JsonSerializer.Serialize(value)` / `Deserialize<T>()` 反射重载。
- 没有新增 `Assembly.GetTypes()` / `Activator.CreateInstance()` / `MakeGenericType()` 动态路径。
- 没有把 EF Core、repository、messaging 或 transaction 引用引入 `PalDDD.CQRS`。
- 没有把 `PalDDD.Core` 引用引入 `PalDDD.Repository.EFCore`、`PalDDD.Dapper`（`IUnitOfWork` 已并入 `PalDDD.Core` 的 `PalDDD.Core.Repository` 命名空间）。
- 没有新增未接入执行链的 public 抽象。
- 没有新增通用 repository wrapper 或 service-provider-backed repository cache。
- 没有新增跨进程消息 payload marker interface 或孤立 upcaster 占位。
- 没有新增自定义 ambient context carrier 或 HTTP tracing middleware。
- 新 EventLog API 只保存 bytes + metadata，不引用 EF Core、broker 或具体 serializer。
- 新 Projection/Idempotency API 不引用 EF Core、broker 或具体 serializer。
- EventLog 到 Projection 的桥接必须放在 `PalDDD.Projections.EventLog` adapter，不反向污染核心 projection 包。
- 新 schema evolution step 有真实执行链和测试。
- 新消息已声明稳定 `[GenerateMessage(Name = "...", SchemaVersion = n)]`，并加入 `JsonSerializerContext` 和 `MessageCatalogBuilder`。
- 新 handler 使用显式 DI 注册。
- 新后台服务正确处理 scoped dependency。

## 官方参考

- [Dapper.AOT](https://github.com/DapperLib/DapperAOT) — Dapper 源码生成器，替换运行时 IL 发射为编译时拦截器
- [MemoryPack](https://github.com/Cysharp/MemoryPack) — 零反射二进制序列化器，设计目标 NativeAOT（见 [ADR 002](decisions/002-non-json-serialization.md)）

- [System.Text.Json source generation](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation)
- [Reflection versus source generation in System.Text.Json](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation)
- [Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
- [Prepare .NET libraries for trimming](https://learn.microsoft.com/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)
- [Use scoped services within a BackgroundService](https://learn.microsoft.com/dotnet/core/extensions/scoped-service)
