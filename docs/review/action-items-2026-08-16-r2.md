# 任务清单 2026-08-16（第二轮）— 全量评审修复清单

> 来源：AI 质量系统第二轮全量运行（分析报告见 audit-2026-08-16-r2.md）。基线 d502b75。
> 定级：危害 × 复杂度（模板 docs/review/ACTION_ITEMS_TEMPLATE.md）。本轮分析产出：P1×4、P2×33、P3×96（分组列示）。
> **修复轮状态（2026-08-16 完成）**：全部 4 P1 + 33 P2 + 96 P3 已修复/处置（[x]）。P1×4 主线程亲修（126-129）；P2/P3 分 7 族并行子代理；验证轮发现 1 项 P1 返工（ITM-126 重查失败分支改 `throw;` 保留原始异常）+ 4 项 P3 返工（根 gate-check G3 恒过与退出码、check-all grep -c 中止、coverlet 残留），全部复查通过。验证：build 0 警告 0 错误、16 测试项目 **972 测试全绿**（新增 17 项回归）、方言探针 40/40、4 机制探针复跑、Base/Extension **pack 实测成功**、机械轴全绿（gate 22/22 待提交后 G22 复跑）。ITM-169/170 中 12 个"只列不改"子项按分析轮口径留痕跳过（见各条目标注）。

---

## 一、P1（近期必修，4 项）

### [x] ITM-126 · EventLogDbContext PG 唯一冲突重查踩 aborted 事务（25P02 替换原始异常）· 可信度 ✅（探针实锤）
- **维度**：并发/错误（PG 事务语义）
- **问题**：src/PalDDD.EventLog.EFCore/EventLogDbContext.cs:213-233——catch(23505) 后第 227 行在同一 aborted 事务重查 `GetActualStreamVersionAsync`；PG 显式事务内 23505 后任何后续命令 25P02（探针实锤），原始 DbUpdateException 被 PostgresException 替换，`EventStreamConcurrencyException` 重试契约失效。Dapper（128-142）/PalORM（119-134）姊妹均已守卫（重查失败保守上抛原始异常）。
- **建议**：重查包 `try/catch (DbException)`，失败 `throw;` 保留原始异常（与姊妹同型）。
- **风险**：低（只影响错误分类路径）。
- **验证**：修复后 PG 真库并发回归（或复用 23505→25P02 探针扩展：同事务重查断言原始 DbUpdateException 外传）。
- **涉及文件**：src/PalDDD.EventLog.EFCore/EventLogDbContext.cs

### [x] ITM-127 · PalOrmSagaStateStore PG 路径 saga_data 字符串直写 jsonb（42804）· 可信度 ✅（探针实锤）
- **维度**：方言 SQL（PD23 同族复发）
- **问题**：src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs:127/137/154——`_jsonTypeInfo` 非 null 时 STJ 产 string 参数直写 jsonb 列，PG 抛 42804（探针实锤）；Dapper 姊妹 SqlTemplates 用 `CAST(@data AS jsonb)`。既有方言探针 40/40 因 Saga 用例走 `_jsonTypeInfo=null` 未覆盖。
- **建议**：PG 分支 `CAST({jsonData} AS jsonb)` 或传原生 jsonb 参数；补 PG + jsonTypeInfo 探针用例进 dialect-probe。
- **风险**：低（无 PG CAST 时该路径必炸，修复纯增量）。
- **验证**：修复后 dialect-probe 扩展用例红转绿 + PalOrmSagaMultiDialect PG jsonTypeInfo 快照往返。
- **涉及文件**：src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs、.ai/scripts/dialect-probe.sh

### [x] ITM-128 · PalDDD.slnx 缺失两个公开发布元包（Base/Extension）· 可信度 ✅（dotnet sln list 实证）
- **维度**：发布完整性（公开包无任何构建覆盖）
- **问题**：src 36 个 csproj，slnx 仅 34 个 src（Base/Extension 0 命中）——build/test/pack/CI 永不触碰两个元包；元包依赖损坏照常发布。Base/Extension 内私有版本引用（Ulid 1.0.0、Extensions preview.5）未迁 CPM。
- **建议**：slnx 加两个 Project Path；两个 csproj 版本引用迁 Directory.Packages.props 并对齐当前版本（Ulid 1.4.0 / Extensions preview.7）；CI 增加 pack 冒烟。
- **风险**：低（增补构建覆盖）。
- **验证**：`dotnet sln list` 含 36 src；`dotnet build PalDDD.slnx` 全量编译 + `dotnet pack` 两元包成功。
- **涉及文件**：PalDDD.slnx、src/PalDDD.Base/PalDDD.Base.csproj、src/PalDDD.Extension/PalDDD.Extension.csproj、Directory.Packages.props

### [x] ITM-129 · scripts/publish-main.sh 强制覆盖 origin/main 违反仓库红线 · 可信度 ✅（静态亲验）
- **维度**：发布安全（破坏性脚本 vs 书面红线）
- **问题**：第 10 行 `git push origin "$branch":main --force`；branch-flow.md:35 禁止 force push main、release.md:226 禁止 AI/自动化自主合并 main；脚本无豁免声明、无人工确认闸门。
- **建议**：删除该脚本；或改 --force-with-lease + 显式环境变量确认（如 `CONFIRM_PUBLISH_MAIN=1` 缺省拒绝），并在 branch-flow/release.md 注明例外边界。
- **风险**：高（当前状态误执行即毁 main 历史）。
- **验证**：脚本在未确认时拒绝执行；文档同步。
- **涉及文件**：scripts/publish-main.sh、docs/branch-flow.md、docs/release.md

---

## 二、P2（33 项，按族）

### 多实现契约分叉族（5）

### [x] ITM-130 · DapperOutboxStore MarkProcessed/MarkDead/ReleaseForRetry 不清入参租约字段 · 可信度 ✅
- 接口契约明确"实现必须清空 LockedBy/LockedUntil"；EFCore/PalORM/InMemory 三姊妹均清对象字段，唯 Dapper 只清 DB 列——调用方读入参仍见陈旧持有者。建议：三个方法 SQL 后同步 `message.LockedBy=null; message.LockedUntil=null`（ReleaseForRetry 可选同步 Status/RetryCount 对齐 PalORM）。涉及：src/PalDDD.Dapper/DapperOutboxStore.cs

### [x] ITM-131 · DapperUnitOfWork Commit/Rollback 失败路径事务清理缺失 · 可信度 ✅
- CommitAsync/RollbackAsync 在底层抛异常时 `_transaction` 保持非 null 且不 Dispose；作用域释放时 DisposeAsync 对失效事务再 Rollback → 覆盖原始异常、`_disposed` 未置位可重入。PalOrmUnitOfWork 有 finally 清理。建议：try/finally 清理 + 释放异常过滤。涉及：src/PalDDD.Dapper/DapperUnitOfWork.cs

### [x] ITM-132 · PG 多主机端口编码：未编码 host 继承连接串 Port（ReadWriteRouter + PostgreSqlMultiHost）· 可信度 ✅（探针实锤）
- `sb.Port != 5432` 才编码端口；primary Port=5433 时，显式 5432 或不带端口的副本/备机被连接成 5433——读流量/故障转移落到错误实例（探针实锤：未编码 host 继承 Port=5433）。建议：primary Port≠5432 时全部 host 显式 `host:port` 编码（或按原串检测显式 Port）。涉及：src/PalDDD.Dapper.PostgreSql/PostgreSqlReadWriteRouter.cs、src/PalDDD.Dapper.PostgreSql/PostgreSqlMultiHost.cs

### [x] ITM-133 · PalOrmProjectionCheckpointStore 缺 processingTimeout 守卫（负值租约保护失效）· 可信度 ✅
- `TryStartAsync(..., TimeSpan.FromSeconds(-1))` → leaseUntil < startedAt → 抢占检查恒假，同一位置可被并行处理；Dapper 版已有守卫（ITM-107）。建议：补 `ThrowIfNegativeOrZero`/负值判空对齐 Dapper。涉及：src/PalDDD.PalORM/Stores/PalOrmProjectionCheckpointStore.cs

### [x] ITM-134 · SqlTemplates.SagaSelectByLease 缺 status=0（终态 Saga 混入回读）· 可信度 ✅
- PalORM 回读已有 `AND status=Active` 守卫（PalOrmSagaStateStore.cs:103），Dapper 模板未同步——租约 UPDATE 后行被置终态时 Dapper worker 仍领回终态 Saga。建议：模板补 `AND status=0`。涉及：src/PalDDD.Dapper/SqlTemplates.cs

### 配置缺陷族（1）

### [x] ITM-135 · AddPalSqlite("Data Source=:memory:") 默认 Production → WAL 确认启动即炸 · 可信度 ✅（探针实锤）
- 内存库识别后仍执行 `PRAGMA journal_mode=WAL`，:memory: 恒返回 "memory"（探针实锤）→ 抛 InvalidOperationException；文档宣称 :memory: 直接可用。建议：ApplyOptimization 接收 isMemory，内存源跳过 WAL 确认或直接按 InMemory 级；补启动探针测试。涉及：src/PalDDD.Dapper.Sqlite/SqliteServiceCollectionExtensions.cs

### 算法/配置守卫族（2）

### [x] ITM-136 · ConsistentHashSharding virtualNodes 未校验（0 → DivideByZero）· 可信度 ✅
- `new ConsistentHashSharding(2, virtualNodes: 0).GetShardId(...)` → 空环取模 0；ModSharding 姊妹已校验 shardCount。建议：构造器 `ThrowIfNegativeOrZero(virtualNodes)`。涉及：src/PalDDD.Dapper.PostgreSql/PostgreSqlSharding.cs

### [x] ITM-137 · RetryBackoffPolicy ExponentialBackoffPolicy maxDelay 负值未校验 · 可信度 ✅
- `new ExponentialBackoffPolicy(maxDelay: -1s)` → 负延迟 → 批量失败时 `nextAttemptAt` 为过去 → 退避失效紧循环；FixedBackoffPolicy 姊妹已校验。建议：构造器禁负（零合法）。涉及：src/PalDDD.Transactions/RetryBackoffPolicy.cs

### 测试自欺族（12）

### [x] ITM-138 · CqrsTests.SendAsync_VoidCommand_CompletesSuccessfully 零断言 · 可信度 ✅
- 只有 `await dispatcher.SendAsync(...)`；把实现改成 no-op 仍绿。建议：可观测 handler 计数/标志断言。涉及：test/PalDDD.CQRS.Tests/CqrsTests.cs:204-212

### [x] ITM-139 · EventLogTests.ReadStreamAsync_ReplaysEventsInStreamVersionOrder 不验顺序 · 可信度 ✅
- `IsEquivalentTo` 无序等价；逆序返回仍绿。建议：索引断言 `events[0].StreamVersion==0` 等。涉及：test/PalDDD.EventLog.Tests/EventLogTests.cs:120-121

### [x] ITM-140 · EventLogTests.ReadAllAsync_ReplaysEventsInGlobalAppendOrder 不验顺序 · 可信度 ✅
- 同上（GlobalPosition/StreamName）。涉及：test/PalDDD.EventLog.Tests/EventLogTests.cs:170-171

### [x] ITM-141 · EventLogPositionReserverTests.ReserveAsync_WithinChunk_DoesNotHitDatabase 无 DB 探针 · 可信度 ✅
- 名称宣称纯内存快路径，无调用计数/可计数 DbContext。建议：注入计数断言第二次零 DB 往返；否则改名。涉及：test/PalDDD.EventLog.Tests/EventLogPositionReserverTests.cs:18-35

### [x] ITM-142 · StrategicMetadataAttributeTests.GenerateEnumAttribute_IsMarkerAttribute 恒真 · 可信度 ✅
- 仅 `new` 后 IsNotNull（new 恒非 null），未验证 AttributeUsage/无状态。建议：反射断言 AttributeTargets.Class/AllowMultiple/无字段。涉及：test/PalDDD.Core.Tests/StrategicMetadataAttributeTests.cs:116-121

### [x] ITM-143 · MessagingTests.HandlerCancellation_DoesNotRecordEventHandlerFailedMetric 名不副实 · 可信度 ✅
- 只断言 OCE 抛出，从不查 Measurements。建议：[NotInParallel] 隔离下断言指标不增（或改名）。涉及：test/PalDDD.Messaging.Tests/MessagingTests.cs:202-210

### [x] ITM-144 · SerializationTests.RoundTrip_WithGenericDeserialize_DoesNotBoxValueType 假名 · 可信度 ✅
- TestMessage 是引用类型 record，且无装箱/分配计量。建议：真实值类型 + 分配环差，或改名。涉及：test/PalDDD.Serialization.Tests/SerializationTests.cs:120-133

### [x] ITM-145 · MemoryPackSerializerTests.AddPalMemoryPackSerialization_RegistersSingleton 不验生命周期 · 可信度 ✅
- 只解析一次 IsTypeOf；改 AddTransient 仍绿。建议：两次解析 SameReferenceAs 或 ServiceLifetime 断言。涉及：test/PalDDD.Serialization.Tests/MemoryPackSerializerTests.cs:148-163

### [x] ITM-146 · TransactionsTests.ProcessEventAsync_CompensatesAllExecutedSteps 无补偿断言 · 可信度 ✅
- saga/state 死变量；只断言 AggregateException 类型，未断言 failingState 被补偿。建议：断言 CurrentState 含 Compensated_*。涉及：test/PalDDD.Transactions.Tests/TransactionsTests.cs:1207-1235

### [x] ITM-147 · EventLogReplaySourceTests 顺序断言 IsEquivalentTo · 可信度 ✅
- 建议索引断言 events[0].Position=="0"。涉及：test/PalDDD.Projections.EventLog.Tests/EventLogReplaySourceTests.cs:35-42

### [x] ITM-148 · PalOrmEventLogMultiDialectTests.GlobalOrder 只断言 Count==2 · 可信度 ✅
- 三方言全局顺序零覆盖。建议：断言 all[0]/all[1] 或 GlobalPosition 升序。涉及：test/PalDDD.PalORM.Tests/PalOrmEventLogMultiDialectTests.cs:95-106

### [x] ITM-149 · BrokerIntegrationTests broker 未 await using（连接/句柄泄漏）· 可信度 ✅
- 4 处 `var (broker, _)` 未释放（Kafka producer / RabbitMQ channel+connection）。建议：对齐同文件 handler-cancellation 测试的 `await using var broker`。涉及：test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs:181,249,278,339

### 样本与基准自欺族（8）

### [x] ITM-150 · AotSample 校验失败仅打印 FAILED、exit 0、恒打印 all checks passed · 可信度 ✅
- 任一校验失败进程仍 0 退出。建议：失败即 throw 或累计并 Environment.Exit(1)。涉及：samples/PalDDD.AotSample/Program.cs

### [x] ITM-151 · OrderStatusBench 从未注册 SmartEnum 值 · 可信度 ✅（静态；运行被 BDN/.NET11 已知兼容问题前置阻断）
- FromValue/TryFromValue/All 触基类"未注册任何值"异常，4 个基准必败。建议：静态构造器 RegisterValues 或 [GenerateEnum]。涉及：bench/PalDDD.Benchmarks/FrameworkBenchmarks.cs:128-139

### [x] ITM-152 · OutboxThroughputBench 首迭代耗尽后空转测量 · 可信度 ✅
- GlobalSetup 一次性 100 条，迭代 1 全部租走/处理，后续迭代 0 条。建议：[IterationSetup]/自建 store 重置。涉及：bench/PalDDD.Benchmarks/InfraBenchmarks.cs:31-67

### [x] ITM-153 · EventLogBench ReadStream 从未 seed 事件（空流枚举）· 可信度 ✅
- Setup 无 AppendAsync。建议：GlobalSetup 追加 N 条。涉及：bench/PalDDD.Benchmarks/InfraBenchmarks.cs:89-113

### [x] ITM-154 · IterateEvents_RefStructEnumerator void + 死计数（DCE 风险）· 可信度 ✅
- 建议：返回 count 供 BDN 消费或 GC.KeepAlive。涉及：bench/PalDDD.Benchmarks/FrameworkBenchmarks.cs:33-42

### [x] ITM-155 · MinimalApi /health 未注册 AddPalHealthChecks（500）· 可信度 ✅
- MapPalHealthChecks 前置要求先注册。建议：Build 前 `builder.Services.AddPalHealthChecks()`。涉及：samples/PalDDD.MinimalApi/Program.cs:22

### [x] ITM-156 · MinimalApi MapCommand 路由 {id} 从未读取（错误用法示例）· 可信度 ✅
- 建议：改用 /orders/items + body 传 OrderId，或手写 RouteValues 绑定。涉及：samples/PalDDD.MinimalApi/Program.cs:30

### [x] ITM-157 · PalOrmSample csproj 宣称 7 Store AOT 验证 vs 实际仅 Outbox · 可信度 ✅
- 建议：二选一——补全宣称冒烟或注释/头说明改为实际覆盖。涉及：samples/PalDDD.PalOrmSample/PalDDD.PalOrmSample.csproj、Program.cs

### 文档/发布面失实族（5）

### [x] ITM-158 · README AOT 表把 Transactions 标 ✅（实际 IsAotCompatible=false）· 可信度 ✅
- 建议：移入 ❌ 行并注明 Saga 反射特例（README 双语 590 行）。涉及：README.md、README.en.md

### [x] ITM-159 · README 示例多处编译失败（PalUlid/AddPalSaga 顺序/Configure/IProjection/FromValue）· 可信度 ✅
- ①PalUlid 非 public（应为 ByteAether.Ulid.Ulid）；②AddPalSaga<TState,TOrchestrator> 顺序反；③Configure 不存在（构造器 When）；④IProjection→IProjectionHandler.ProjectAsync；⑤FromValue 实参类型。建议：逐处改正并加"示例可编译"检查。涉及：README.md、README.en.md

### [x] ITM-160 · ci-coverage.sh reportgenerator 无 .config/dotnet-tools.json（合并步必败）· 可信度 ✅
- 建议：添加工具清单固定 ReportGenerator 版本（或脚本前置检测 + 安装提示）。涉及：.config/dotnet-tools.json、ci-coverage.sh

### [x] ITM-161 · 全局 NoWarn 含 IL3058 且 23 项无逐条 Justification（与 aot.md 项目级声明矛盾）· 可信度 ✅
- 建议：IL3058 下放 Dapper 四项目；CA1305/NU5104 移除或补 Justification；CHANGELOG 计数同步。涉及：Directory.Build.props、docs/aot.md、CHANGELOG.md

### [x] ITM-162 · CHANGELOG/release 发布状态三方矛盾（1.1.0 无段无 tag）· 可信度 ✅
- 建议：统一状态——若未发布：CHANGELOG 顶部改"未发布/VersionPrefix 1.1.0"、release.md:6 同步、补 [1.1.0] 发布流程；若已发布：补段 + tag + release.md:463 对齐。涉及：CHANGELOG.md、docs/release.md

---

## 三、P3（96 项，分组列示，批量处置）

### 守卫对称族（MT-1，1 项批量）
- [x] ITM-163 · 存储实现参数守卫不对称（PD17 复发，ITM-077 同族）——DapperInboxStore / PalOrmInboxStore（message null、consumer/messageId blank）、PalOrmIdempotencyStore（policy/record null、op/key/failureReason）、PalOrmProjectionCheckpointStore（checkpoint null、names/failureReason）、PalOrmOutboxStore（message(s) null、failureReason/retriedBy）、PalOrmSagaStateStore（owner/state）、DapperOutboxStore（message null、failureReason）、DapperEventLog/PalOrmEventLog（streamName）与 EFCore/InMemory 姊妹对齐；修后标注"已同步修的实现列表"。涉及：上述 9 文件

### 覆盖率双轨族（1）
- [x] ITM-164 · 清理 16 个测试 csproj coverlet.collector 引用 + Directory.Build.props CollectCoverage/CoverletOutput（ci-coverage 已迁 MTP 原生 --coverage，--collect 实测 exit 5 弃用）

### src P3（片 1-4，共 41 项按文件分组）
- [x] ITM-165 · 片1：DapperUnitOfWork.BeginTransactionAsync 未查 _disposed；DapperConfiguration.Create 缺连接串守卫；SqlitePerformanceOptimizer Light/InMemory 缺 connection null 守卫
- [x] ITM-166 · 片2：PeriodicBackgroundProcessor OCE 吞弃边界注释；DapperBulkCopy MySQL 列全 string（decimal 区域性问题）；Saga/Outbox/Checkpoint 租约方法 leaseDuration 负值（Options 层集中校验）；InMemoryInboxStore ct/timeout；SagaExtensions/ChildSagaStep step/selector null；PostgreSqlPipeline 一处 ConfigureAwait；DomainEventDispatcher events null/MaxIterations；EventLogPositionReserver 溢出理论值；MessageEvolutionPipeline steps 枚举两次；MySqlPalOrmExtensions sync-over-async；FanOutStep init 路径延迟校验
- [x] ITM-167 · 片3：EventLogDbContext/PalOrmEventLog 异步迭代器 metrics 缺 finally（对齐 InMemory 二十一轮）；ProjectionProcessor MarkFailed 超长 ex.Message 无 2048 截断（对齐 Inbox/Outbox）；PostgreSqlReportHelper null 守卫/COPY 受信声明/StreamWriter 同步释放；KafkaBroker 登记窗口 cts 释放噪声；ServiceRegistration TryAddScoped vs TryAddEnumerable；AddPalMessageContractVerification 重复注册；MySqlOutbox/SqlServerOutbox leaseSeconds 边界；PG/SQLite JSON 扩展 null 守卫；PalOrmEventLog IsUniqueConstraintViolation 反射裁剪降级声明；MemoryPack 非泛型路径声明；PG DataSource 双注册 Dispose 幂等注释；InboxProcessor message null；TransactionOptions LeaseOwner 声明
- [x] ITM-168 · 片4：PostgreSqlSharding BitConverter 端序；PalOrmInboxStore Mark* 本地对象语义与 affected；DapperInboxStore 抢占后本地字段陈旧；MapQuery 未映射 PalValidationException→400；SagaState.CurrentState null→NRE；EventHandler DIM ValueTask 源语义（文档化）；EnumGenerator hint 下划线碰撞（病态命名，文档化）

### test P3（片 5a/5b，共 30 项）
- [x] ITM-169 · 片5a 18 项：CqrsTests 非泛型桥只解析不调用/自证接口/分配阈值；Abstractions CompositeKey；AllocationContract 上界宽；AotContract 反射开关侧证；DomainEventSemantics init-only 只查 setter；EntityTests NewEntity_IsTransient 名反/链表细节断言；ValueObjectTests 重复测试；SmartEnum ConcurrentReads 注释失实；MessageRegistry/SourceGenerator 硬编码 bin 路径；EventLogEfCore 顺序等价+首末兜底/分配阈值；VerificationHostedService/Verifier no-throw 成功路径；PositionReserver Max<200 可收紧
- [x] ITM-170 · 片5b 12 项：MessagingTests EmptyList/NullMessageBroker NoOp；BrokerIntegration RabbitMQ Task.Delay 盲等；RepositoryEfCore InMemory 回滚名实不符；PalOrmUnitOfWork BeginTransaction 只验不抛；PalOrmSagaMultiDialect WithJsonTypeInfo 仅 SQLite；MemoryPack 空 payload 泛化异常；OutboxProcessor 轮询阈值 flaky；MultiDialectFixture 注释顺序/吞异常；FakeTimeProvider CreateTimer 忽略 period；Saga StepWithoutCompensation 未证跳过；Serialization 值类型往返无计量

### samples/bench P3（7）
- [x] ITM-171 · ECommerce/MinimalApi 手写 AddSingleton 注册；Get! NRE 无 404；Guid.Parse→500；FrameworkBench 标题缺 List 对照；void 基准 DCE；AllValues 测属性访问；Program.Measure 未除以迭代数

### docs/scripts/config P3（16）
- [x] ITM-172 · release.md Infra-Dapper AOT 口径；architecture.md Dapper 重复 4 行/计数口径/Repository 不依赖 Core；release.md IsPackable 标签未闭合；gate-check.sh G3 硬编码恒过（CI 回退门禁空转）；check-all.sh grep -c 管线中止风险；README 641 Prompts 非包声明；.gitignore 重复/.githooks 不可分发；README.en 措辞；CHANGELOG 30 vs 32 求和；testing/development stryker 路径矛盾；conventions 守护方法名；README 片段 someUlid 未定义/ProcessManager 示例不一致；TUnit 1.58→1.65（conventions/testing/CHANGELOG）；ADR 16→17 四处；测试计数 869→955 三处；stryker-config 不存在与命令；release.md ci.yml"待创建"；README 结构树 vs slnx；PalMetrics 24→27；development.md MTP --filter 位置

---

## 四、验证轮收口（2026-08-16 完成）

验证轮（独立上下文逐 diff 敌对审查）发现并处置：

| # | 验证轮发现 | 处置 |
|---|-----------|------|
| V-R2-1 | **ITM-126 初版修复不实（P1 返工）**：重查失败（25P02）分支仍抛 `EventStreamConcurrencyException(-1)`，未保留原始 DbUpdateException，与姊妹 `throw;` 同型要求不符 | 已修：`if (!requerySucceeded) throw;`（EventLogDbContext.cs:259-261）；全量 build 0/0 + 972 测试复跑 |
| V-R2-2 | 根 scripts/gate-check.sh G3 硬编码 `"0" "0"` 恒过（CI 回退门禁空转） | 已修：真实文件命名检查（[A-Za-z0-9._-]）；顺带修复 `[ FAIL -gt 0 ] && exit 1` 全绿时退出码为 1 的隐性 bug（显式 if/exit） |
| V-R2-3 | scripts/check-all.sh 两处 `grep -c` 零匹配 + `set -euo pipefail` 中止风险 | 已修：计数拆变量 + `|| true` 兜底 |
| V-R2-4 | coverlet 残留：conventions.md 仍写"可选 coverlet.collector / ≥80% 行覆盖"、test/coverlet.runsettings 死配置未删 | 已修：规范改 MTP 原生 --coverage 口径 + 删除 runsettings |
| V-R2-5 | 测试计数 972 vs 955 需机械轴确认；ITM-128 CPM 迁移与清单字面不符（P3 低）；ITM-127 方言探针未扩展（P3 低） | 972 由全量机械复跑确认并同步四处文档；ITM-128 采用 ProjectReference 化 + 外部版本对齐（两 csproj 注释声明，pack 实测通过）；ITM-127 由 PG 真库 TUnit 回归（PalOrmSagaMultiDialectTests）兜底，方言探针扩展留待后续 |
| V-R2-6 | ITM-169/170 部分子项未见改动 | 主线程复核：属分析轮"只列不改"口径（C 代理已在交付报告逐项注明），按约定留痕跳过 12 子项 |

三轴收束：机械 7 件套全绿（gate 22/22 待提交后 G22 复跑）· 方言探针 40/40 · 972 测试全绿（16 项目）· 4 机制探针复跑确认 · Base/Extension pack 实测成功。
