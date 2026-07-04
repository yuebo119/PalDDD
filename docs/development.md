# 开发指南

## 环境要求

- .NET SDK 11.0.x (Preview 5+)
- Windows / PowerShell 可直接使用本文命令
- NuGet 源默认使用 `https://api.nuget.org/v3/index.json`

确认环境：

```bash
dotnet --info
```

## 常用命令

```bash
dotnet restore PalDDD.slnx
dotnet build PalDDD.slnx --no-restore
dotnet test PalDDD.slnx --no-restore -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"
dotnet run --project samples/PalDDD.AotSample/PalDDD.AotSample.csproj
```

包版本检查：

```bash
dotnet list PalDDD.slnx package --outdated
```

## 质量门禁

仓库默认：

- `TreatWarningsAsErrors=true`
- `AnalysisLevel=latest-all`
- nullable enabled
- AOT/trim analyzers enabled
- System.Text.Json reflection defaults disabled

任何生产代码改动至少运行：

```bash
dotnet test PalDDD.slnx --no-restore -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"
dotnet build PalDDD.slnx --no-restore
```

> ⚠️ `.NET 11 Preview 5 SDK` 的 `dotnet test` 与 `Microsoft.Testing.Platform` 2.x 存在协议版本不兼容。必须附加 `-e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"` 环境变量才能正常发现并运行测试。此问题预计在后续 SDK Preview 中修复，届时可移除该标志。

公共 API 快照由 `PalDDD.Core.Tests` 覆盖。新增或调整核心包 public API 后，先确认变更合理，再更新快照：

```bash
PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1 dotnet test test/PalDDD.Core.Tests/PalDDD.Core.Tests.csproj --no-restore -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"
```

### Stryker 突变测试

仓库根 `stryker-config.json` 配置了 [Stryker.NET](https://stryker-mutator.io/) 突变测试，对 `PalDDD.Core` 项目做验证测试强度。

- **门禁阈值**：`high=80` / `low=60` / `break=50`
  - 突变分数 < 50% 时 Stryker 以非零退出码失败（CI 阻断）
  - 50%–60% 之间为低分（需人工核查并加分支/边界测试）
  - ≥ 80% 为高分（理想区间）
- **报告**：`reporters` 配置为 `html` / `progress` / `dashboard`，HTML 报告生成到 `StrykerOutput/<timestamp>/` 目录
- **覆盖分析**：`coverage-analysis: perTest` 启用按测试用例的覆盖率归因，定位未杀死的突变
- **运行**：
  ```bash
  dotnet tool install -g dotnet-stryker
  dotnet stryker --config-file stryker-config.json
  ```
- **集成建议**：CI 中将上述命令接入 nightly/PR 流水线，突变分数 < 50% 视为质量门禁失败。

新增/修改核心生产代码时，建议本地运行一次 Stryker 检查测试是否已杀死新引入的突变，避免"代码分支无测试覆盖"的环境突变残留。

### Testcontainers 集成测试 CI 配置

Kafka / RabbitMQ / PostgreSQL / MySQL / SQLite 五个 Broker 与数据库集成测试基于 [Testcontainers](https://dotnet.testcontainers.org/)，需 Docker 环境接入。GitHub Actions 接入模板（PR 动态合并自 `.github/workflows/`，ADR-010 采纳）：

```yaml
jobs:
  integration-tests:
    runs-on: ubuntu-latest
    services:
      docker:
        image: docker:dind
        options: --privileged
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '11.0.x'
      - run: dotnet restore PalDDD.slnx
      - run: dotnet test test/PalDDD.Messaging.Integration.Tests --filter "Category=Integration" -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"
      - run: dotnet test test/PalDDD.Integration.Tests --filter "Category=Integration" -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"
```

要点：

- Testcontainers 自动拉起容器并暴露随机端口，无需预置 service container；仅需 runner 支持 Docker daemon。
- Windows runner 不支持 Testcontainers，集成测试 CI 必须在 `ubuntu-latest` 上运行。
- 单元测试（不依赖 Docker）仍可跨平台运行，可拆为独立 job 在 PR 触发；集成测试建议 nightly / 合并到 main 时触发以缩短 PR 周转。
- 本地预跑：`docker info` 确保 Docker Desktop 已启动，再执行 `dotnet test --filter "Category=Integration" -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"`。

修改 package 或 restore assets 后先运行：

```bash
dotnet restore PalDDD.slnx
```

## Git 提交流程

提交前检查：

```bash
git status --short --branch
git diff
git diff --check
```

只暂存本次任务相关文件。本地开发工具产物（`.claude/`、`.serena/`、`.depwire/` 等）已配置 `.gitignore`，不会被意外提交。

## 新增项目的边界规则

新增核心项目时：

- 默认继承根目录 `Directory.Build.props`。
- 不引用 EF Core、ASP.NET Core、Kafka、RabbitMQ 等适配层。
- 不引入反射扫描作为默认注册机制。
- 公开 API 需要 XML doc，避免 `CS1591`。

新增适配项目时：

- 明确说明它属于外圈 adapter。
- 如果依赖不满足 AOT/trim analyzer，项目文件中显式设置 AOT metadata，并添加边界测试。
- 不把 adapter 类型暴露回核心抽象。

## 新增消息类型

1. 定义消息类型。
2. 添加稳定 `[GenerateMessage(Name = "...", SchemaVersion = n)]`。
3. 添加 `[JsonSerializable(typeof(MessageType))]` 到应用或测试的 `JsonSerializerContext`。
4. 在 `AddPalJsonSerialization` 中调用 `catalog.Add(...)`。
5. 添加序列化 round-trip 测试。

`Name` 只允许小写字母、数字、`.` 和 `-`。不要使用 CLR 类型名、空白、下划线、大写或路径分隔符作为 wire name；source generator 会用 `PALMSG004` 快速失败。`Name` 还必须以 `.v{SchemaVersion}` 结尾，例如 `orders.order-submitted.v2` 必须声明 `SchemaVersion = 2`，否则报告 `PALMSG005`。

示例：

```csharp
[JsonSerializable(typeof(OrderSubmitted))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

services.AddPalJsonSerialization(catalog =>
{
    catalog.Add(AppJsonContext.Default.OrderSubmitted, name: "order-submitted");
});
```

## 战略 DDD 编译期治理

领域模型类型必须声明所属 bounded context：

```csharp
[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.order-submitted.v1", SchemaVersion = 1)]
public sealed class OrderSubmitted : DomainEvent, IDomainEvent
{
    public static string EventName => "ordering.order-submitted.v1";
}
```

`BoundedContext` 名称必须使用稳定小写形式：小写字母、数字、`-` 和 `.`。例如 `ordering`、`payments.refunds`、`order-fulfillment`。

领域事件类型必须是 `sealed`，避免事件契约通过继承扩展导致 replay、serializer descriptor 和 handler 分派语义漂移；未 sealed 会触发 `PDDD012`。领域事件还必须声明 `[GenerateMessage]`，让 Outbox、broker、EventLog replay 和 schema evolution 使用稳定 `MessageDescriptor`。`GenerateMessage` 的 wire name 和 schema version 由 source generator 继续执行 `PALMSG001`-`PALMSG005` 校验；领域事件的 wire name 和 schema version 还会由 analyzer 用 `PDDD009` / `PDDD010` / `PDDD011` 执行稳定小写命名、版本后缀和正数版本治理。
`IDomainEvent.EventName` 必须是 string literal，并与 `[GenerateMessage(Name = "...")]` 完全一致；不一致或使用 `nameof` / 运行时拼接会触发 `PDDD015`，避免 dispatcher、trace、EventLog 和 broker 使用不同事件名称。
领域事件的 wire name 还必须属于同一个 bounded context：`[BoundedContext("ordering")]` 的事件应使用 `ordering.*`，例如 `ordering.order-submitted.v1`，不能漂移到 `billing.*`。

Process Manager 必须是 sealed、声明 `[BoundedContext]`，并实现 `IEventHandler<TEvent>`：

```csharp
[BoundedContext("ordering")]
[ProcessManager("ordering.order-fulfillment")]
public sealed class OrderFulfillmentProcessManager : IEventHandler<OrderSubmitted>
{
    public ValueTask HandleAsync(OrderSubmitted @event, CancellationToken ct)
        => ValueTask.CompletedTask;
}
```

`ProcessManager` 名称必须和 bounded context / message wire name 一样使用稳定小写形式，并以 `[BoundedContext]` 为前缀，例如 `ordering.order-fulfillment`，便于流程实例、追踪标签和治理报表长期引用。

`PalDDD.Analyzers` 当前提供：

- `PDDD001`：领域模型类型缺少 `[BoundedContext]`。
- `PDDD002`：bounded context 名称不是稳定小写形式。
- `PDDD003`：`[ProcessManager]` 类型不是 sealed bounded event handler。
- `PDDD004`：`IProjectionHandler<T>` 类型不是 sealed bounded context component。
- `PDDD005`：领域事件缺少 `[GenerateMessage]` 稳定消息契约。
- `PDDD006`：process manager 名称不是稳定小写形式。
- `PDDD007`：projection handler 的 `ProjectionName` 不是稳定小写字面量。
- `PDDD008`：领域事件 `[GenerateMessage(Name = "...")]` 不属于声明的 bounded context。
- `PDDD009`：领域事件 `[GenerateMessage(Name = "...")]` 不是稳定小写 wire name。
- `PDDD010`：领域事件 `[GenerateMessage(Name = "...", SchemaVersion = n)]` 的 wire name 缺少匹配 `.v{n}` 后缀。
- `PDDD011`：领域事件 `[GenerateMessage(SchemaVersion = n)]` 使用了小于 1 的 schema version。
- `PDDD012`：领域事件类型未声明为 `sealed`。
- `PDDD013`：projection handler 的 `ProjectionName` 不属于声明的 bounded context。
- `PDDD014`：process manager 名称不属于声明的 bounded context。
- `PDDD015`：领域事件 `EventName` 与 `[GenerateMessage(Name = "...")]` 不一致，或不是 string literal。

## 新增 handler

1. 定义 `ICommand<TResponse>` 或 `IQuery<TResponse>`。
2. 实现 `ICommandHandler<TCommand,TResponse>` 或 `IQueryHandler<TQuery,TResponse>`。
3. 用显式 API 注册。
4. 添加 dispatcher 行为测试。

不要添加程序集扫描。

不要在 CQRS 层新增事务 attribute、事务 pipeline 或 repository/messaging 引用。事务必须由应用 handler、`IUnitOfWork`、EF Core transaction 或 Outbox 明确表达。

不要重新引入通用 `IRepository<TAggregate,TKey>`、`RepositoryBase` 或 `IUnitOfWork.Query<T>()`。EF Core 查询能力应通过应用层 `DbContext` 或显式业务仓储使用，避免低价值包装和 service-provider-backed repository cache。

## 新增 messaging 能力

- `EventBus` 已被移除。**统一使用 Outbox 模式进行可靠事件发布，不再使用进程内事件总线。**
- `IterativeDomainEventDispatcher` 派发领域事件时必须通过 `PalActivitySource.StartEventDispatch` 创建 activity；handler 失败时必须标记 `ActivityStatusCode.Error` 并保持异常传播。
- 领域事件 handler 成功调用后必须记录 `paldd.event_handlers.handled`；handler 抛出异常时必须记录 `paldd.event_handlers.failed`。
- 不要新增未接入 `IterativeDomainEventDispatcher` 或 broker adapter 执行链的消息中间件、filter 或 marker。

## 新增 public API

新增 public API 前确认它会被真实运行时路径调用。未接入执行链的扩展点不要先占位；如果未来需要 event bus middleware、message filter 或 transaction decorator，应同时提交执行模型、注册 API、测试和文档。

不要新增 `IIntegrationEvent`、`IUpcaster` 这类 payload marker 或 schema evolution 占位。跨进程消息元数据属于 `MessageDescriptor`、Outbox 和 broker envelope；payload 保持普通 CLR 类型。

不要新增 `ContextCarrier`、自定义 `AsyncLocal` 上下文容器或 `UsePalTracing` HTTP tracing middleware。HTTP、HttpClient、runtime 和 exporter 配置应放在应用层 OpenTelemetry/Aspire Service Defaults。

## 新增 Projection / Idempotency 能力

Projection 相关代码必须满足：

- checkpoint key 明确包含 `ProjectionName + SourceName + Position`。
- 重复消息必须可跳过。
- handler 失败必须持久化失败状态后重新抛出。
- projection handler 必须是 sealed，并声明 `[BoundedContext]`。
- `ProjectionName` 必须是稳定小写字面量，并以 `[BoundedContext]` 为前缀，例如 `ordering.order-summary`，便于 checkpoint、重建任务、追踪标签和治理报表长期引用。
- 核心 projection 包不引用 broker、EF Core 或具体 read model store。
- EF Core checkpoint 持久化只能放在 `PalDDD.Projections.EFCore` 可选适配器中。
- Dapper checkpoint 持久化只能放在 `PalDDD.Dapper` 统一适配器中。
- EventLog 回放桥接放在 `PalDDD.Projections.EventLog`，使用 `MessageDescriptor` 校验契约并通过 `IMessageSerializer` 反序列化。
- Projection rebuild 必须通过 `PalActivitySource` 发出可采集 activity，并保留 projection name / source / replayed 标签。
- Projection rebuild 必须通过 `PalMetrics` 记录 replayed metric，便于监控重建窗口和事件回放吞吐。

Idempotency 相关代码必须满足：

- key 明确包含 `OperationName + IdempotencyKey`。
- 成功结果可缓存和重放。
- 处理中请求不能重复执行 handler。
- 结果序列化由调用方显式提供，不在核心包绑定 JSON 或 MessagePack。
- Command/API idempotency 必须通过 `PalActivitySource` 发出可采集 activity，并保留 operation / key / result 标签。
- Command/API idempotency 必须通过 `PalMetrics` 记录 executed / cached / skipped / failed metrics，便于监控 retry 行为和缓存命中率。
- EF Core 持久化只能放在 `PalDDD.Idempotency.EFCore` 可选适配器中，核心幂等包不得引用 EF Core。

Schema evolution 相关代码必须满足：

- 使用 `MessageDescriptor.Name + SchemaVersion` 查找升级步骤。
- 领域事件 message name 必须是稳定小写 wire name，避免大小写、下划线或空白导致契约漂移。
- 领域事件 message name 必须以 `.v{SchemaVersion}` 结尾，避免 wire name 版本和 descriptor version 脱节。
- 领域事件 schema version 必须大于等于 1，避免不可回放的无效契约版本。
- 领域事件 message name 必须以 bounded context 为前缀，避免 schema evolution 跨上下文误连。
- converter 是显式函数，测试覆盖旧 payload 到当前消息的升级。
- 不新增 payload marker interface 或没有执行链的 upcaster 占位。

EventLog 相关代码必须满足：

- stream name 是调用方显式提供的稳定业务流标识。
- append 必须带 `ExpectedStreamVersion`，生产 store 必须原子校验 expected version。
- `EventData` 只保存 bytes + metadata，不绑定具体 serializer。
- `EventAuditMetadata` 保留 actor、reason、correlation/causation 和 W3C trace context。
- replay API 必须按 stream version 或 global position 稳定排序。
- append 成功路径必须通过 `PalActivitySource` 发出可采集 activity，并保留 stream / event count / version range / global position range 标签。
- append 成功路径必须通过 `PalMetrics` 记录 appended metric，便于监控事件日志写入吞吐。
- read/replay 路径必须通过 `PalActivitySource` 发出可采集 activity，并保留 from position / read count 标签。
- read/replay 路径必须通过 `PalMetrics` 记录 read metric，便于监控审计回放和投影修复吞吐。
- EF Core 持久化只能放在 `PalDDD.EventLog.EFCore` 可选适配器中，必须配置 `(StreamName, StreamVersion)` 和 `EventId` 唯一约束，`GlobalPosition` 不允许使用数据库自动生成值。

## 新增 store

Outbox store 必须满足：

- `LeasePendingMessagesAsync` 是原子租约获取。
- 多实例并发时不会重复发布同一条 message。
- 失败后可释放租约并按 `NextAttemptAt` 重试。
- 保留 `OutboxMessage` 的 correlation、causation、`TraceParent` 和 `TraceState` 字段，发布时通过 `MessagePublishContext` 传给 broker。
- Outbox 批处理必须通过 `PalActivitySource` 发出可采集 activity，并保留 batch size / processed / dead / retried 标签。
- Outbox 批处理必须通过 `PalMetrics` 记录 processed / failed metrics，便于生产告警和趋势分析。
- EF Core 持久化只能放在 `PalDDD.Transactions.EFCore` 可选适配器中，核心 `PalDDD.Transactions` 包不得引用 EF Core。

Inbox store 必须满足：

- `ConsumerName + MessageId` 唯一。
- 已处理消息返回 `null`。
- 未超时的 `Processing` 消息返回 `null`。
- 超时或失败消息可重新进入 `Processing`。
- Inbox 幂等消费必须通过 `PalActivitySource` 发出可采集 activity，并保留 consumer / message id / result 标签。
- Inbox 幂等消费必须通过 `PalMetrics` 记录 processed / skipped / failed metrics，便于监控重复投递、吞吐和失败率。
- EF Core 持久化只能放在 `PalDDD.Transactions.EFCore` 可选适配器中，核心 `PalDDD.Transactions` 包不得引用 EF Core。

Saga store 必须满足：

- `GetActiveSagasAsync(int batchSize, CancellationToken ct)` 有界返回。
- `LeaseActiveSagasAsync(string owner, TimeSpan leaseDuration, int batchSize, CancellationToken ct)` 必须设置 `LeasedBy` / `LeasedUntil`，避免多实例后台扫描重复补偿。
- 按稳定顺序返回，避免批量扫描饥饿。
- `SaveChangesAsync` 持久化补偿、完成和死信状态。
- Saga 从未完成变为完成时必须通过 `PalMetrics` 记录 completed metric，便于监控流程管理完成量。
- Saga 超时补偿成功后必须通过 `PalMetrics` 记录 compensated metric，便于监控流程管理补偿吞吐。
- EF Core 持久化只能放在 `PalDDD.Transactions.EFCore` 可选适配器中，并且复杂集合字段必须使用 source-generated serialization，不允许反射 JSON fallback。

## 文档维护

公共 API、项目边界、注册方式、AOT 策略、验证命令变化时，同步更新：

- `README.md`
- `docs/architecture.md`
- `docs/aot.md`
- `docs/usage.md`
- `docs/development.md`

文档中的命令必须能在仓库根目录执行。
