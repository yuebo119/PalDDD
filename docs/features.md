# 功能特性全景

> 本文档是 `src/` 全部生产项目的功能特性清单，逐条带源码锚点（类型名 + 文件名），由全量源码通读产出。
> 定位分工：[架构说明](architecture.md) 讲分层与依赖方向；[使用指南](usage.md) 讲怎么用；本文档讲「有什么」。
> 计数声明（项目数/包数/指标数/诊断数等）受 `scripts/count-audit.cs` 门禁与源码推导核对。基线：v4.0.0。

## 总览

`src/` 下共 36 个项目（35 个可打包 NuGet 包 + 1 个共享源码目录），另有 `test/` 下 16 个测试项目。产品面按清洁架构分为 7 层：

| 层 | 项目 | 职责 |
|---|---|---|
| 领域基元 | PalDDD.Core | 战术 DDD 原语 + 可观测性门面，唯一外部依赖 ByteAether.Ulid |
| 编译期治理 | Core.SourceGen · Analyzers · Analyzers.CodeFixes · Shared | 源生成器、诊断分析器、代码修复 |
| 应用抽象 | Messaging · Serialization · Compression · CQRS | 零反射抽象层 |
| 应用核心 | EventLog · Idempotency · Projections · Transactions | 端口接口 + 内存测试替身 |
| 基础设施 | PalORM 系×4 · Dapper 系×4 · EFCore 系×5 · Kafka · RabbitMQ · MemoryPack · Compression.Native · Serialization.Evolution · Projections.EventLog · Repository.EFCore | 存储与传输适配器 |
| 宿主装配 | DependencyInjection · Hosting.AspNetCore | DI 装配入口、ASP.NET Core 端点 |
| 元包与资产 | Base · Extension · Prompts | 聚合元包、AI 提示模板 |

## 1. 领域建模基元（PalDDD.Core）

| 特性 | 说明 | 锚点 |
|---|---|---|
| 实体与聚合根 | 领域事件单链表存储（双指针 O(1) 追加）、RaiseEvent 自引用环防御、基于 Id 的相等语义 | `Entity<TId>` / `AggregateRoot<TId>`（Entity.cs） |
| 零分配事件遍历 | foreach 全程栈分配的 ref struct 迭代器 | `DomainEventEnumerable`（DomainEventEnumerable.cs） |
| 领域事件基类 | EventId 自动 Ulid、OccurredOn UTC、EventName 为 static abstract 编译时常量 | `DomainEvent`（DomainEvent.cs） |
| 值对象 | 泛型数学值对象（`INumber` 约束 + `IUtf8SpanFormattable` 零装箱）、软删除/更新时间/RowVersion 轻量类型 | `ValueObject<T>`（ValueObject.cs） |
| 智能枚举 | FrozenDictionary O(1) 查找，`[ModuleInitializer]` 编译期注册消除反射 | `SmartEnum<,>`（SmartEnum.cs） |
| 规约模式 | 内存判定与 EF 表达式翻译双形态，And/Or/Not 组合统一表达式参数 | `ISpecification<T>` / `Spec<T>`（ISpecification.cs） |
| 验证 | `IPalValidator<T>` + ImmutableArray 错误集的 `PalValidationResult`（结果式，非异常流） | IPalValidator.cs |
| 工作单元端口 | Begin/Commit/Rollback/SaveChanges，不支持嵌套（ADR-023），模板方法扩展 | `IUnitOfWork`（IUnitOfWork.cs） |
| 强类型标识 | `IPalIdentity<TKey>` 契约 + Ulid 生成器抽象（RFC 9562 合规实现） | `IPalIdGenerator`（Identity/） |
| 日志门面 | 五成员接口隔离具体日志后端，空实现 NullPalLogger | `IPalLogger<T>`（Logging/） |
| 可观测门面 | 30 个指标计数器 + 12 个 Activity 工厂（命令/事件/Saga/Outbox/Inbox/幂等/投影/事件日志读写/流式查询） | `PalDiagnostics`（PalDiagnostics.cs） |
| 失败原因归一 | 五处理器共用：截断 2000 字符 + UTF-16 代理对完整性守卫 | `FailureReason`（FailureReason.cs） |
| 契约标记 | GenerateId / GenerateEnum / GenerateMessage / BoundedContext / ProcessManager 五个 Attribute | Attributes.cs |

## 2. 编译期治理（源生成器 + 分析器 + 修复器）

| 特性 | 说明 | 锚点 |
|---|---|---|
| 强类型 ID 生成器 | 为 `[GenerateId]` 生成 `IPalIdentity<T>` + `ISpanParsable` + TypeConverter/JsonConverter 全套零反射代码 | IdentityGenerator.cs |
| 智能枚举生成器 | 跨 partial 收集静态字段，生成 `[ModuleInitializer]` 注册代码 | EnumGenerator.cs |
| 消息注册生成器 | 为 `[GenerateMessage]` 生成消息目录注册代码 | MessageRegistryGenerator.cs |
| 生成器诊断 | 23 条（PALID 7 条 · PALENUM 9 条 · PALMSG 7 条），诊断 ID 全仓共 38 条 | 三生成器各自实现 |
| 战略 DDD 分析器 | 15 条规则（PDDD001-015）：限界上下文必标、领域事件必 sealed 且挂消息契约、消息名稳定格式与上下文前缀、Saga/投影处理器形状 | StrategicDddAnalyzer.cs（按规则族分 partial） |
| 代码修复 | 4 个一键修复（补上下文前缀/版本后缀/投影名前缀/EventName 匹配），全部支持 Fix All | AddBoundedContextPrefixCodeFix.cs 等 |
| 防漂移共享源 | 稳定名谓词以链接编译方式供生成器与分析器共用 | `StableNameValidation`（Shared/StableNameValidation.cs） |

## 3. 进程内 CQRS（PalDDD.CQRS）

| 特性 | 说明 | 锚点 |
|---|---|---|
| 命令/查询模型 | ICommand / ICommand\<T> / IQuery\<T> 三形态，默认接口方法桥接非泛型接口 | IRequest.cs、CommandHandler.cs |
| 零反射分发器 | FrozenDictionary 注册表 + Freeze 冻结，无 MakeGenericType；空注册表不冻结防启动竞态 | `Dispatcher`（Dispatcher.cs） |
| 管道行为链 | `IPipelineBehavior` 扩展缝；可重用状态机替代 lambda 链省状态机分配；零行为快路径直呼 handler | `PipelineStateMachine`（PipelineStateMachine.cs） |
| 内置验证行为 | 调用全部 `IPalValidator`，失败抛携带错误集的 `PalValidationException` | ValidationBehavior（PipelineBehaviors.cs） |
| 内置日志行为 | 派发耗时与失败记录，TimeProvider 可注入测试时钟 | LoggingBehavior（PipelineBehaviors.cs） |
| 实例注册形态 | 免 scope 的实例注册（根容器解析，单请求省一次 scope 分配，ADR-026） | `Dispatcher.Register` 三重载 |
| DTO 异常 | HandlerNotFoundException（供 HTTP 404 映射）、PalValidationException | 各自文件 |

## 4. 跨进程消息（Messaging + Kafka + RabbitMQ）

| 特性 | 说明 | 锚点 |
|---|---|---|
| 消息代理契约 | `IMessageBroker` 泛型/非泛型发布 + 逐条订阅 + 批量订阅（数量/时间界组批，`ConsumedMessage<T>` 携带含 `MessageId` 的消费上下文）；接口 remarks 显式声明投递语义（当前两实现均为 at-most-once、不保证顺序、多订阅者语义分叉），消费幂等由 Inbox 保证 | MessageBroker.cs、MessageBatchPump.cs |
| 追踪信封闭环 | 发布侧 W3C TraceParent/CorrelationId/CausationId，消费侧 FromHeaders 还原，头键名常量锁定读写一致 | MessagePublishContext.cs、MessageConsumeContext.cs |
| 空实现 | 单节点模式零发送 | `NullMessageBroker` |
| Kafka 适配器 | 按消息类型名路由同名 Topic、后台线程阻塞消费循环、订阅句柄幂等释放 + Flush 排空、librdkafka 错误/统计/日志接线 | `KafkaBroker`（KafkaBroker.cs） |
| RabbitMQ 适配器 | Fanout Exchange 广播拓扑、publisher confirms + 未确认上限限流、mandatory 丢弃即抛、声明任务缓存消 404 竞态、manual-ack + prefetch、NO_ROUTE 诊断 | `RabbitMqBroker`（RabbitMqBroker.cs） |
| 进程内事件派发 | 迭代替代递归防栈溢出、EventId 去重防循环事件、MaxIterations 上限（默认 1000）fail-fast | `IterativeDomainEventDispatcher`（DomainEventDispatcher.cs） |
| 零反射 Handler 索引 | 按编译时常量 EventType 分组 FrozenDictionary，精确类型匹配 | 同上 + EventHandler.cs |

## 5. 序列化与契约演进

| 特性 | 说明 | 锚点 |
|---|---|---|
| 消息序列化抽象 | `IMessageSerializer` + `MessageDescriptor`（线缆名/类型/JsonTypeInfo/版本）+ 不可变三索引目录 | MessageCatalog.cs |
| AOT-first JSON 实现 | System.Text.Json 零反射、ThreadStatic 池化写入热路径、JSON Lines 事件流读写器（SIMD 换行检测） | JsonMessageSerializer.cs、JsonLinesEventStream.cs |
| 二进制格式适配 | MemoryPack 序列化器与 JSON 互斥替换，ContentType 断链防护 | MemoryPackMessageSerializer.cs |
| Schema 升级链 | `MessageUpgradeStep` 逐步 Convert 管道，构造期校验相邻步类型衔接防断链 | MessageEvolutionPipeline.cs |
| 启动期合约门禁 | 从目录派生合约清单，IHostedService 在宿主开始服务前验证全部升级路径可达 | PalPlatformVerificationHostedService（Serialization.Evolution/ServiceCollectionExtensions.cs） |

## 6. 压缩

| 特性 | 说明 | 锚点 |
|---|---|---|
| 统一抽象 | `ICompressor` + 算法/级别枚举；Compress 无上限而 Decompress 有上限的不对称契约 | ICompressor.cs、CompressionAlgorithm.cs |
| System 实现 | Brotli（encoder 原语直压）、GZip、Deflate 三适配器 | SystemCompressor.cs |
| 原生实现 | LZ4 / ZStandard / OpenZL（占位，底层实为 Zstd，前向兼容已声明） | NativeCompressors.cs |
| 解压炸弹防护 | 输入 8MB / 输出 64MB 双上限，常量为 public 单一事实源跨程序集共用 | `DecompressionGuard`（SystemCompressor.cs） |
| DI 注册 | `AddPalCompression()` 与 `AddPalCompressionNative()`（后者幂等含前者） | 各 ServiceCollectionExtensions |

## 7. 事务消息与 Saga 编排（PalDDD.Transactions）

### Outbox 发件箱

| 特性 | 说明 | 锚点 |
|---|---|---|
| 后台调度 | 周期轮询 + 满批自适应排空（让步 1ms 立即续批），PollInterval 支持热读 | `OutboxProcessor` / `PeriodicBackgroundProcessor` |
| 原子租约 | LeasePending 原子认领，MaxDegreeOfParallelism 分区并行且每 worker 独立 DI scope | `OutboxBatchProcessor`、`IPalOutboxStore`（OutboxStore.cs） |
| 发布确认流水线 | `DeliveryMode.Auto`（默认）：broker 声明 `SupportsConcurrentPublish` 时窗口至少 16；`Serial` 保留逐条确认旧行为 | ProcessPartitionPipelinedAsync（OutboxBatchProcessor.cs） |
| 重试与死信 | 指数退避（2^n 秒、上限封顶、可选 jitter 防惊群）/固定退避两种策略；重试耗尽转 Dead 状态；`RequeueDeadAsync` 运维重投（保留失败历史） | RetryBackoffPolicy.cs、OutboxMessage.cs |
| fencing 契约 | (LockedBy, LockedUntil) 租约 token + RetryCount 快照 + Status 守卫三重防护；v4.0 全接口异步化（ADR-026） | `IPalOutboxStore`（OutboxStore.cs） |

### Inbox 收件箱

`(ConsumerName, MessageId)` 粒度幂等消费：TryStart 抢占、僵尸 Processing 超时可夺、handler 失败可重试。批量路径（`IBatchInboxStore` + `InboxProcessor.TryProcessBatchAsync`）每批一次认领/完成往返，`InboxBatchConsumer<T>` 把 broker 批量订阅、每批 DI scope 与幂等处理器串成开箱即用消费者。锚点：`InboxProcessor`（InboxProcessor.cs）、`InboxBatchConsumer`（InboxBatchConsumer.cs）、`InboxMessage`（InboxMessage.cs）。

### Saga 编排

| 特性 | 说明 | 锚点 |
|---|---|---|
| 步骤 DSL | When 泛型/非泛型/通配三种注册，精确 + 通配双级路由 | `Saga<TState>`（Saga.cs） |
| 五种步骤车道 | Normal / FanOut（并发门 + 整批重放）/ ChildSaga（子 Saga 分发）/ Interrupt（人工介入）/ Dynamic（运行时路由） | SagaStep.cs、FanOutStep.cs、ChildSagaStep.cs、InterruptStep.cs、DynamicStep.cs |
| 重试补偿骨架 | 每车道共享：MaxRetries 默认 3，耗尽后逆序补偿全部已执行步骤（失败步骤优先），补偿失败并入 AggregateException | RunRetryLaneAsync（Saga.cs）、SagaCompensation.cs |
| 超时治理 | 按 StepStartedAt 起算的检测器 + 后台扫描处理器（租约式扫描、超时自动补偿、批量租约释放走 `ISagaLeaseBatchStore`；内置四栈实现 `ISagaScanCursorStore`，释放写回 leased_until 作轮转游标，避免固定前 N 条饿死） | SagaTimeoutDetector.cs、SagaProcessor.cs |
| 中断与恢复 | `DefaultSagaManager` 中断登记 + ResumeAsync 以决策为事件重入，幽灵条目 TOCTOU 防护 | DefaultSagaManager.cs |
| 状态与并发 | SagaState 版本乐观锁（租约即换代）、CloneForLease successor、执行序字典作为补偿顺序唯一依据 | SagaState.cs |
| 执行观察者 | 五观察点零分配事件 + AsyncLocal 传播，Sink 异常与业务隔离 | SagaExecutionObserver.cs |

**配置面**：`OutboxOptions`（BatchSize=100、LeaseDuration=2min、PollInterval=500ms、MaxRetryCount=10、DeliveryMode=Auto、BatchMarkFlushInterval=1s 等）、`InboxOptions`、`SagaProcessorOptions`，全部 ValidateOnStart 校验。DI 入口：`AddPalOutbox` / `AddPalInbox` / `AddPalSaga<TState,TOrchestrator>`（Transactions/ServiceCollectionExtensions.cs）。

## 8. 事件溯源与投影

| 特性 | 说明 | 锚点 |
|---|---|---|
| 事件日志端口 | 按流追加 + 4 态期望版本（NoStream/Any/StreamExists/Exact）乐观并发；流读与全局读均为游标语义 | `IEventLog`、`ExpectedStreamVersion`（EventLog/） |
| 审计元数据 | ActorId/Reason/Correlation/Causation/TraceParent/TraceState，可从 Activity.Current 自动捕获 | EventAuditMetadata.cs |
| EFCore 事件日志 | Hi/Lo 全局位置分配器（chunk 1000 + DB 行 CAS 重试）、唯一索引冲突分类为并发异常、零拷贝重水化 | EventLogDbContext.cs、EventLogPositionReserver.cs |
| 投影处理器 | 检查点租约式逐事件投影，at-least-once 契约（投影必须幂等） | `ProjectionProcessor<T>`（Projections/） |
| 投影重建 | RebuildAsync 全量重建（先清检查点）/ ReplayAsync 增量补齐，名称一致性守卫 | `ProjectionRebuilder<T>`（ProjectionRebuilder.cs） |
| 事件日志驱动回放源 | 按消息契约三重精确匹配反序列化；每流单事件类型约束已声明 | EventLogReplaySource.cs（Projections.EventLog） |
| 事件流导入导出 | JSON Lines 流式导出（O(1) 内存）与导入（导入即重建，坏行报物理行号） | EventStreamJsonLines.cs |
| 仓储层拦截器 | SaveChanges 拦截器把实体领域事件写入 Outbox 表，与业务数据同事务原子（双 store 分派、失败自清理） | `OutboxDomainEventInterceptor`（Repository.EFCore） |

## 9. 幂等（PalDDD.Idempotency）

| 特性 | 说明 | 锚点 |
|---|---|---|
| 幂等执行框架 | (OperationName, Key) 粒度，三态结果 Executed/Cached/Skipped；毒载荷降级 Skipped、MarkCompleted 失败按 Executed 返回（at-least-once 边界均声明） | IdempotencyProcessor.cs |
| 策略与并发 | ProcessingTimeout 默认 5 分钟、Retention 默认 24h 且 Retention ≥ ProcessingTimeout 校验；Revision 单调 CAS 令牌替代时间戳（防 DB 精度截断同刻双写） | IdempotencyPolicy.cs、IdempotencyRecord.cs |

## 10. 双持久化栈与方言增强

两条平行栈实现同一组抽象层接口（Outbox/Inbox/Saga/EventLog/Checkpoint/Idempotency/UnitOfWork），租约与 fencing 语义跨栈对齐（三栈共存裁决见 [ADR-020](decisions/020-persistence-stack-retirement-roadmap.md)）：

| 栈 | 分方言机制 | 方言包 |
|---|---|---|
| PalORM | static abstract TProvider 编译期特化（SupportsReturningClause 静态分派）+ PalORM 源生成物化 | Sqlite / PostgreSql / MySql 三装配包 |
| Dapper | `[module:DapperAOT]` 调用点拦截器 + DapperDbType 枚举运行时分发 | Sqlite / PostgreSql / MySql 三增强包 |
| EF Core | 抽象基类 + 方言虚方法覆写 | Transactions.EFCore（Outbox 四方言变体）/ EventLog.EFCore / Idempotency.EFCore / Projections.EFCore / Repository.EFCore |

存储层共性能力：原子租约三分支（PG FOR UPDATE SKIP LOCKED RETURNING / MySQL 版本探针 JOIN / SQLite RETURNING）、三重 fencing 标记、原生批量导入（PG Binary COPY / MySQL BulkCopy / SQLite 批 INSERT）、SQL 错误分类器（四方言唯一约束码集）、MySQL ≥8.0.18 SKIP LOCKED 版本探针（`MySqlLeasePolicy`，ADR-025）、Saga jsonb CAST 适配、连接池预热 HostedService。

Dapper 侧独有的方言增强面（PalORM 方言包暂未携带，待后续迁移）；SQLite FTS5/JSON1 例外，已按 ADR-032 双栈提供：

| 方言 | 能力 | 锚点 |
|---|---|---|
| SQLite | 分级 PRAGMA 优化（WAL/Production/InMemory 自动降级）、内存库生命周期自动判定（Dapper 独有）；FTS5 全文检索（external-content 与 standalone 双形态、三同步触发器、TEXT 主键映射表）、JSON1 函数与引号路径、参数化查询片段（双栈同语义，共享 `src/PalDDD.Shared` 内部核心） | SqlitePerformanceOptimizer.cs、SqliteFtsExtensions.cs、SqliteJsonExtensions.cs、SqliteFtsSessionExtensions.cs（PalORM） |
| PostgreSQL | 多主机故障转移/读写分离路由（含 IPv6 形态守卫）、LISTEN/NOTIFY 实时 Outbox 通知器（背压合并 + 指数退避重连）、JSONB 操作符族 + GIN 索引、一致性哈希分库分表、软删除、行级审计触发器、管道批量执行、CSV/JSONL 流式报表导出 | PostgreSqlMultiHost.cs、PostgreSqlOutboxNotifier.cs、PostgreSqlJsonbExtensions.cs、PostgreSqlSharding.cs、PostgreSqlSoftDelete.cs、PostgreSqlAuditor.cs、PostgreSqlReportHelper.cs |
| MySQL | InnoDB 会话优化（锁超时/隔离级别/utf8mb4）、多主机故障转移/负载均衡/最少连接三入口（内嵌端口语法等守卫族） | MySqlPerformanceOptimizer.cs、MySqlMultiHost.cs |

## 11. 宿主与一站式装配

| 特性 | 说明 | 锚点 |
|---|---|---|
| 核心装配 | `AddPalDDD`（分发器 + 空 broker + 启动期 Registrar）、`AddPalCoreStack`、`AddPalIdentity`（Ulid 生成器）、`AddPalLogging`（ZLogger JSON 控制台 + 门面适配） | ServiceRegistration.cs（DependencyInjection） |
| Handler 显式注册 | `AddPalCommandHandler` / `AddPalQueryHandler` / `AddPalEventHandler` 三族：自动补漏调、重复注册快速失败、启动期 HostedService 零反射装配后冻结 Dispatcher | 同上 |
| 管道行为注册 | 开放泛型（非 AOT 值类型守卫）与闭合泛型（AOT 安全）两形态，互斥防重复执行 | `AddPalPipelineBehaviors` 两重载 |
| ASP.NET Core 端点 | `MapCommand`（POST，调用方传 JsonTypeInfo 保 AOT）、`MapQuery`（GET + 显式绑定委托）、415/400/404/500 全套 ProblemDetails 编排 | EndpointExtensions.cs（Hosting.AspNetCore） |
| 流式读端点（SSE） | `IStreamQuery`/`IStreamQueryHandler` + `MapStreamQuery` GET/POST：验证前置 400、SSE 帧（data-only / `formatFrame` / event/id 选择器）、心跳、`Last-Event-ID` 读取入口、取消贯穿、遥测（TTFT `first-item` 事件 + 五个计数器） | EndpointExtensions.cs、StreamResumeExtensions.cs（Hosting.AspNetCore） |
| 断线续跑端点 | `AddDetachedStreamRuns` + `MapDetachedStreamQuery`（GET/POST 双启动形态）+ `MapStreamRunAttach` + `MapStreamRunCancel`：单写者 + 单附着 + 有界环形缓冲、`event: gap`、接管 `event: detached`、run 自有 scope、`paldd.stream.attached/gap` 计数 | DetachedStreamEndpoints.cs、StreamRunRegistry.cs（Hosting.AspNetCore） |
| 全局异常中间件 | PalValidationException→400、HandlerNotFound→404（类型级信息不泄露内部）、响应已开始分支 rethrow 补日志 | ExceptionMiddleware.cs |
| 健康检查 | message_broker 注册检查 + outbox 存储探测两个内置检查，自定义响应体（逐组件明细 + 耗时） | HealthCheckExtensions.cs |

## 12. 元包与 AI 资产

| 项目 | 说明 |
|---|---|
| PalDDD.Base | 最小组合元包：Core + Serialization + Compression + 三个编译期资产 |
| PalDDD.Extension | 全量组合元包：CQRS + EventLog + Idempotency + Projections + Messaging + Transactions + DI 装配 |
| PalDDD.Prompts | 9 类 DDD 构件的 AI 编码提示模板（聚合根/限界上下文/命令/查询/事件/投影/Saga/值对象/任务进件），把 PDDD 编译期规则前置写进 AI 约束 |

## 横切能力

- **AOT 分层矩阵**：抽象层（Core/CQRS/Messaging/Serialization/Compression/EventLog/Idempotency/Projections/Transactions 抽象面）零反射；基础设施 4 包（Kafka、RabbitMQ、Compression.Native、Serialization.MemoryPack）加 Transactions 的子 Saga 反射路径显式豁免，豁免均有 csproj/注释理由。详见 [AOT 指南](aot.md)。
- **可观测统一**：12 个 Activity 工厂 + 30 个指标计数器贯穿全部处理器；五处理器共用的 FailureReason 归一化。
- **并发防护三形态**：Outbox 用租约 token + RetryCount 快照；Saga 用 Version 换代乐观锁；Checkpoint/Idempotency 用单调 Revision CAS。
- **全端口 InMemory 替身**：事件日志、Outbox、Inbox、Saga、检查点、幂等存储均有内存实现（替身与真实实现的语义分叉均以注释定案）。

## 边界与刻意取舍

1. Broker 层投递语义是 at-most-once，at-least-once 由应用侧 Inbox/幂等组合达成（`IMessageBroker` remarks 明示）。
2. `SagaStatus.DeadLettered` 枚举在案但框架内无赋值路径，Saga 实际失败终态为 Compensated/CompensationFailed。
3. `AddPalFullStack` 当前等价于 AddPalCoreStack（更名留给 v5.0）。
4. SQLite 时间列编码在 PalORM 栈与 Dapper 栈互不兼容，同一物理库只能单选一个适配器族（双侧对称声明）。
5. `SqlServerOutboxDbContext` 已标 Obsolete（v5.0 移除预告）。
6. 运行时调小 Outbox MaxRetryCount 存在 Pending 消息滞留窗口（既不派发也不转 Dead，代码内声明）。
