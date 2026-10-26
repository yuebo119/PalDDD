# 包与库 API 审计（2026-10-20）

> 范围：`Directory.Packages.props` 的全部中央钉扎 + 各项目直接 `PackageReference`。
> 触发：用户令「审计所有引用的包或者库，都使用最新的 API、最新的特性，充分优化」。
> 方法：`dotnet list package --outdated --include-transitive --include-prerelease` 实测；
> NuGet flatcontainer / GitHub Releases 核对版本；逐包源码检索实际 API 使用面；
> 分析器 opt-in 构建（`EnableQualityAnalyzers=true`）复核。

## 一、结论

绝大多数包已在最新版本，并在代码中使用其现代 API。真正可落地的增量集中在两类：

1. **PalORM 6.6.0 新 API**（前提交 `583cd03`）：`IDataSessionFactory<TProvider>` 会话池
   （`useSessionPool` 可选）与 `BulkInsertAsync` 单参重载（PG 单次 COPY）。
2. **存量质量/性能收口**（本提交）：分离流心跳死代码修复、`System.Threading.Lock`、
   `CancelAsync`、结构体 `LayoutKind.Auto`、重载归位与 S2077 定向抑制。
   第三方分析器基线从 11 条回到 0。

没有发现需要跨包重写的大项。以下「未采用」均附具体原因，避免重复提案。

## 二、逐包状态

### 持久化与数据访问

| 包 | 版本 | 当前使用 | 最新 API/特性状态 |
|---|---|---|---|
| PalORM.Core / SourceGen / Sqlite / PostgreSql / MySql | 6.6.0 | 6 Store + UnitOfWork + 三方言 DI | 已用 `IDataSessionFactory`/`DataSessionFactory`（`useSessionPool`）、`BulkInsertAsync` 单参、`PreWarmAsync`、`WithRetry`/`WithCircuitBreaker`、`ForRead` 路由（EventLog `readFromReplica:true`）、源生成 CRUD |
| Dapper / Dapper.AOT | 2.1.89 / 1.1.0 | Outbox/Inbox/EventLog/Idempotency/Saga | 已用 AOT 拦截器、`CommandDefinition`（ct 传递）、`DbDataReader` 真流式、`NpgsqlBinaryImporter` COPY、`MySqlBulkCopy`；无未用的高价值新 API |
| EF Core（含 InMemory/Relational/Sqlite） | 11.0.0-rc.2 | 五组 EF Store | 已用 `ExecuteUpdateAsync`/`ExecuteDeleteAsync`、`AsNoTracking`、按模型缓存编译委托；`AddDbContextPool` 因 `OutboxDomainEventInterceptor` 的 ITM-640 约束不可用 |
| Npgsql | 10.0.3 | PG 连接/COPY/Batch/多主机 | 已用 `NpgsqlDataSource`/`NpgsqlDataSourceBuilder`、`CreateBatch()`、`BeginBinaryImport`；多路复用（Multiplexing）与事务/会话状态不兼容，不启用 |
| MySqlConnector | 2.6.2 | MySQL 连接/批量 | 已用 `MySqlDataSource`/`MySqlDataSourceBuilder`、`MySqlBulkCopy`；`MySqlBatch` 对同构多行 INSERT 无增益（现路径已单语句单往返） |
| Microsoft.Data.Sqlite / SQLitePCLRaw | 11.0.0-rc.2 / 3.0.5 | SQLite 三栈 | 已用原生 `SqliteConnection` + FTS5/JSON1；无新 API 缺口 |
| Microsoft.Data.SqlClient | 7.1.1 | 零引用声明（ITM-711） | 仅中央声明，无消费面 |

### 消息与事件

| 包 | 版本 | 当前使用 | 最新 API/特性状态 |
|---|---|---|---|
| RabbitMQ.Client | 7.2.2 | RabbitMqBroker | 已用异步 `IChannel`/`CreateChannelAsync`/`BasicPublishAsync`/`BasicConsumeAsync`；`CreateChannelOptions` 开启 publisher confirms + tracking + `ThrottlingRateLimiter`；body 走 `ArrayPool` + `IBufferWriter` |
| Confluent.Kafka | 2.16.0 | KafkaBroker | 已用 `IProducer`/`IConsumer`、`ProduceAsync`、`Consume(ct)` 专用线程 + Channel 交给异步 worker；无未用的高价值新 API |
| DotNetCore.CAP ×3 | 10.1.0-preview-308323494 | 仅 bench 对比车道 | 版本即最新预发布；不进生产包 |

### 序列化、压缩与日志

| 包 | 版本 | 当前使用 | 最新 API/特性状态 |
|---|---|---|---|
| MemoryPack | 1.21.4 | 可选二进制序列化 | 泛型源生成路径已用；非泛型 `IBufferWriter` 直写上游无对应重载（头注实证），保持 DIM 回退 |
| System.Text.Json | 11.0.0-rc.2（框架） | 默认 JSON 序列化/流式帧 | 已用源生成 `JsonSerializerContext`、`GetTypeInfo<T>()` 强类型路径、池化 `Utf8JsonWriter` + 直写 `IBufferWriter` |
| NativeCompressions | 1.2.0 | LZ4/ZStandard/OpenZL | 已用 span 压缩/流式解压、`ArrayPool` scratch、三档 options 静态缓存、`DecompressBufferPool` |
| ZLogger | 2.5.10 | `IPalLogger` 门面底层 | 日志门面刻意不接 `ZLoggerMessage`/`LoggerMessage` 源生成（CQRS 头注：IsEnabled 门控后零分配，接入会泄漏门面抽象）；保持现状 |
| ByteAether.Ulid | 1.4.2 | 主键/EventId | 已用 `Ulid.NewUlid()`/`ToByteArray`/`Parse`；无优化缺口 |
| Microsoft.Extensions.* | 11.0.0-rc.2 | DI/Logging/Options/Hosting | 已用标准 DI、`TimeProvider`、Options 校验；无新 API 缺口 |

### 分析器与源生成

| 包 | 版本 | 当前使用 | 最新 API/特性状态 |
|---|---|---|---|
| Microsoft.CodeAnalysis.CSharp / Workspaces / Analyzers | 5.9.0 | 分析器与源生成器 | 三个源生成器均为 `IIncrementalGenerator`；分析器测试用 `CSharpAnalyzerTest` 现代 API |
| Roslynator.Analyzers / Meziantou.Analyzer / SonarAnalyzer.CSharp | 5.0.1 / 3.0.315 / 10.35.0.4138 | opt-in 质量门禁 | 已按 `scripts/quality-analyzer-baseline.txt` 棘轮；本轮把 11 条存量告警清零 |

### 测试与基准

| 包 | 版本 | 当前使用 | 最新 API/特性状态 |
|---|---|---|---|
| TUnit / TUnit.FsCheck | 1.75.0 | 16 测试项目 | MTP 原生模式、`--treenode-filter`；无运行时优化面 |
| FsCheck | 3.5.0 | 属性测试 | 已用 TUnit 集成 |
| Verify.TUnit | 32.0.1 | 快照测试 | 33.x 为 SponsorCheck 商业门，按 2026-09-25 裁决维持 32.0.1 |
| Testcontainers ×4 | 4.16.0 | PG/MySQL/Kafka/RabbitMQ 集成 | 测试夹具，无运行时优化面 |
| BenchmarkDotNet | 0.16.0-preview.2 | bench 车道 | 自身即最新预发布；InProcess 入口已适配 |
| MediatR | 12.5.0 | bench 对比车道 | 14.x 为 Lucky Penny 商业许可，按裁决维持 12.5.0 |

### System.* 传递钉扎

`System.Composition ×6`、`System.IO.Pipelines`、`System.Threading.Channels`、`System.IO.Hashing`、
`System.Memory.Data`、`System.Security.Cryptography.ProtectedData` 均为 11.0.0-rc.2.26504.105；
`System.Buffers/Memory/Numerics.Vectors/Tasks.Extensions` 等为各自最新稳定线；
`Unsafe` 6.1.2 与 `Microsoft.NETCore.Platforms` 8.0.0-preview.7 为停更/废弃线，仅存在于
netstandard2.0 分析器传递图，无替代品（勿重开）。

## 三、本轮落地的优化

1. **分离流心跳死代码修复**：`StreamRun.StreamToAsync` 等待期满分支原先把 `heartbeat`
   赋值后无条件 `continue`，写心跳的 `if (heartbeat)` 分支永远不可达。现改为「唤醒直接
   continue，等待期满落下去写 `:keepalive` 并 flush」；新增
   `StreamToAsync_IdleWithKeepAlive_WritesHeartbeat` 锁定（旧码红、新码绿）。
2. **`System.Threading.Lock`**：`StreamRunRegistry._gate` 由 `object` 改 `Lock`，与全仓其余
   锁形态一致，消除 MA0158。
3. **`await CancelAsync`**：`StreamRun.FinishAsync` 的 `CancellationTokenSource.Cancel()`
   改 `await CancelAsync()`，消除 S6966。
4. **结构体布局**：`DapperEventLog.ReadOrdinals`、`OutboxBatchProcessor.DeliveryPolicy`
   补 `[StructLayout(LayoutKind.Auto)]`，消除 MA0008。
5. **重载归位**：`Dispatcher.SendAsync/QueryAsync`、`JsonMessageSerializer.Deserialize`、
   `MemoryPackMessageSerializer.Deserialize` 重载相邻，消除 S4136。
6. **S2077 定向抑制**：`PalOrmEventLog` 两处动态 SQL 赋值以
   `#pragma warning disable S2077` 定点抑制并注依据（SQL 仅含 `@pN` 占位符，值全部经
   `AddParam` 参数化绑定）；不做全局关闭。

## 四、未采用项与原因

- **PalORM 版本化 `BulkUpdateAsync`**：Outbox 的 `[ConcurrencyCheck]` 列是 `RetryCount`，
  新路由会自增 version 列，把「标记已处理」变成「重试次数 +1」，破坏重试语义；其余批量写
  无整行更新调用点。
- **PalORM `ToPageAsync` DbBatch**：本仓无分页调用点。
- **PalORM `BulkDeleteAsync`/`BulkMergeAsync`**：现有删除均为单语句 `DELETE ... WHERE`
  （如投影检查点清理），无逐行循环可替换。
- **PalORM `ForRead()`**：EventLog 回放已用 `readFromReplica: true`；其余读路径有
  read-after-write 敏感性，不批量改走副本。
- **Npgsql Multiplexing**：与事务/会话状态不兼容（本仓 Store 大量使用显式事务）。
- **EF `AddDbContextPool`**：与 `OutboxDomainEventInterceptor` 的 ITM-640 约束冲突。
- **MemoryPack 非泛型 `IBufferWriter` 直写**：上游仅提供泛型重载，非泛型路径无对应 API。
- **ZLogger 源生成日志**：门面设计使 `IPalLogger` 不暴露 `ILogger`；IsEnabled 门控后
  生产路径零分配，接入会泄漏抽象（CQRS 头注 YAGNI 裁决）。
- **MediatR 14 / Verify 33**：商业许可，license-policy 禁令维持。
- **TUnit / Testcontainers / Verify / BDN / CAP**：测试或基准专用，不在运行时优化面。

## 五、验证

- Release 全量构建 0 警告 0 错误。
- 全量测试 **1749 用例 = 1672 通过 + 0 失败 + 77 跳过**（新增心跳锁定 1 件）。
- `dotnet run scripts/quality-baseline.cs`：分析器警告 **0 == 基线 0**（修复前 11）。
- 两个 Native AOT 样本 `PublishAot` + 二进制实跑通过；`--smoke` 分配预算 6/6。
- 公共 API 快照未变化（本轮仅内部实现与注释；`useSessionPool` 已在前提交更新）。
