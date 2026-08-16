# 任务清单 2026-08-16 — 全量评审修复清单

> 来源：AI 质量系统全量运行（四系统 + 4 片地毯式评审，198 文件 / 23151 行，基线 commit 4e5437f（git log 可查））。
> 定级采用 **危害 × 复杂度** 双维度（模板见 docs/review/ACTION_ITEMS_TEMPLATE.md）。
> 本轮为分析轮产出：P2×6（5 项亲验定稿 + 1 项待探针）、P3 约 60 项。P0/P1 零。
> **修复轮状态（2026-08-16 完成）**：全部 54 项已修复/处置（[x]）。P2 六项主线程亲修 + ITM-076 双连接 MySQL 探针实证（5 轮未复现双处理，version 乐观锁兜底确认）；P3 四族并行子代理修复。验证：dotnet build 0 警告 0 错误、16 测试项目 955 测试全绿（新增回归测试 9 项：位置保留器 2 + SourceGen 1 + SagaState 2 + Saga 并发 1 + 分析器 3）、方言探针 40/40 复跑通过、gate 21/22（G22 为脏树检查，提交后即清除）、tech-debt 17/17、断言棘轮 173/173。验证轮与终验轮记录见文末（五、验证轮收口 + 六、终验轮收口；终验轮盲区发现 P1×1 + P2×2 + P3×3 已全修）。

---

## 一、P2 修复项（近期）

### [x] ITM-071 · EventLogPositionReserver catch(DbUpdateException) 过宽 · 可信度 ✅
- **维度**：健壮性（基础设施故障误判为并发冲突）
- **优先级**：P2 近期 · 危害: 中 · 复杂度: 易
- **问题**：`AllocateNewChunkAsync`（src/PalDDD.EventLog.EFCore/EventLogPositionReserver.cs 144-149 行）`catch (DbUpdateException)` 将**任意** DbUpdateException（连接中断/字段溢出/超时）当"主键冲突"detach 后重试 5 次，最终抛误导性 "Failed to allocate a global position chunk after 5 optimistic concurrency retries"。同族 4 处（EventLogDbContext/InboxDbContext/IdempotencyDbContext/ProjectionCheckpointDbContext）均已窄化为 IsUniqueConstraintViolation 鸭子判定（ITM-003/ITM-065 修复链），此处为第五处残留。✅ 已亲验代码。
- **建议**：复用 IsUniqueConstraintViolation 鸭子判定（含 SqliteException 类型限定，对齐 InboxDbContext 十七轮修复），仅唯一约束冲突 detach+continue，其余原样上抛。
- **风险**：低——窄化 catch 只影响错误分类路径。
- **验证**：构建 + 既有测试全绿 + 新增负向测试（非唯一约束异常不重试、直接上抛）。
- **涉及文件**：`src/PalDDD.EventLog.EFCore/EventLogPositionReserver.cs`

### [x] ITM-072 · SagaStateDbContext.SaveChangesAsync 返回语义违背接口契约 · 可信度 ✅
- **维度**：契约正确性（跨实现分叉）
- **优先级**：P2 近期 · 危害: 中 · 复杂度: 易
- **问题**：`ISagaStateStore<TState>.SaveChangesAsync` 契约（src/PalDDD.Transactions/ISagaStateStore.cs:34-36）定义返回 1=写入生效、0=目标行不存在或乐观锁冲突；EFCore 实现（src/PalDDD.Transactions.EFCore/SagaStateDbContext.cs:105-106）直接返回 `await SaveChangesAsync(ct)`（DbContext 实际写入实体数）——无变更保存返回 0 被调用方（SagaProcessor.cs:165/184）误判"乐观锁冲突"记 Warning + 快照作废；多实体一次保存返回 N>1。Dapper/PalORM/InMemory 三实现均返回受影响行数。✅ 已亲验两文件。
- **建议**：EFCore 实现改为：捕获 DbUpdateConcurrencyException → 分离实体返回 0；正常保存返回 1（无论写入几个实体，契约关注的是目标 Saga 的写入有效性）；或按 ChangeTracker 目标实体判定。
- **风险**：低——需同步 SagaProcessor 的调用方语义（0=冲突仍成立）。
- **验证**：构建 + 新增测试（无变更保存语义、冲突返回 0、多实体保存返回 1）。
- **涉及文件**：`src/PalDDD.Transactions.EFCore/SagaStateDbContext.cs`、`src/PalDDD.Transactions/ISagaStateStore.cs`（如补契约澄清）

### [x] ITM-073 · Spec\<T\> 构造期无条件 Expression.Compile（Core AOT 违约 + 豁免漏洞）· 可信度 ✅
- **维度**：AOT 契约（Core 层 IsAotCompatible=true）
- **优先级**：P2 近期 · 危害: 中 · 复杂度: 易
- **问题**：`src/PalDDD.Core/ISpecification.cs`（207-224 行）——`Spec<T>.All/None/Where` 构造 `ExpressionSpecification` 时**无条件** `_expression.Compile()`（IL3050）；`[UnconditionalSuppressMessage("AOT","IL3050")]` 的 Justification 声称"AOT 场景应走 ToExpression()"，但编译发生在构造期而非 IsSatisfiedBy 调用期，AOT 应用触碰 Spec\<T\> 即 PlatformNotSupportedException。且 gate G8 按"文件含 RequiresDynamicCode 字符串"豁免（.ai/scripts/gate-check.sh:304），本文件因 SuppressMessage 消息文本含该字样而**误豁免**——豁免漏洞。✅ 已亲验代码 + 门禁脚本。
- **建议**：Compile 延迟到首次 IsSatisfiedBy（lazy `Func<T,bool>?`）；ToExpression 路径零编译。或诚实标注 [RequiresDynamicCode]。同时修 gate-check.sh G8 豁免逻辑（按 [RequiresDynamicCode]/[RequiresUnreferencedCode] 标注行判定，而非文件子串）。
- **风险**：中——lazy 化改变构造时序（静态 All/None 不再构造期编译），需确认无代码依赖构造期编译副作用。
- **验证**：构建 + AOT publish 探针（publish+run 触碰 Spec\<T\>.All 的最小程序）+ gate-check 复跑确认豁免逻辑修正。
- **涉及文件**：`src/PalDDD.Core/ISpecification.cs`、`.ai/scripts/gate-check.sh`、`.ai/gate/prompt.md`（G8 表述同步）

### [x] ITM-074 · IdentityGenerator [GenerateId(null)] NRE 生成器崩溃 · 可信度 ✅
- **维度**：生成器健壮性（一个坏输入毁全编译生成物）
- **优先级**：P2 近期 · 危害: 中 · 复杂度: 易
- **问题**：`src/PalDDD.Core.SourceGen/IdentityGenerator.cs`（70 行） attrData.ConstructorArguments[0].Value! ——`[GenerateId(null)]` 编译期合法（Attributes.cs:29 无 null 校验，对照 BoundedContextAttribute 等均有 blank 校验），null 传至 L136 `sourceType.ToDisplayString()` 抛 NRE → 整个增量生成器崩溃 → 该编译全部生成物丢失。✅ 已亲验两文件。
- **建议**：transform 开头判 `ConstructorArguments[0].Value is not INamedTypeSymbol sourceType` → 报 PALID 诊断（新编号）并 return；或属性构造加 `ArgumentNullException.ThrowIfNull(idType)`。
- **风险**：低。
- **验证**：构建 + SourceGen 负向测试（`[GenerateId(null)]` 得诊断不崩溃，其余生成物不受影响）。
- **涉及文件**：`src/PalDDD.Core.SourceGen/IdentityGenerator.cs`、`src/PalDDD.Core/Attributes.cs`

### [x] ITM-075 · PalOrmEventLog.AppendAsync 缺并发异常翻译 + 事务契约声明 · 可信度 ✅
- **维度**：契约正确性（跨实现分叉，ITM-003 同族）
- **优先级**：P2 近期 · 危害: 中 · 复杂度: 易
- **问题**：`src/PalDDD.PalORM/Stores/PalOrmEventLog.cs`（76-103 行） 循环 INSERT 无 try/catch——TOCTOU 窗口（SELECT MAX 预检后并发写入撞 `(stream_name, stream_version)` 唯一索引）抛**裸 provider 异常**；Dapper（DapperEventLog.cs:120-151）与 EFCore（EventLogDbContext.cs:197/233）均翻译为 EventStreamConcurrencyException。且 Dapper 版有事务契约声明（DapperEventLog.cs:68-71），PalORM 版无同款声明。✅ 已亲验三文件。
- **建议**：循环 INSERT 外包 try/catch，`when (IsUniqueConstraintViolation(ex))` → 重查实际版本 → 抛 EventStreamConcurrencyException（对齐 Dapper 的分类逻辑：批内 EventId 重复原样上抛、并发写入转统一异常）；补事务契约声明注释。
- **风险**：中——翻译逻辑需镜像 Dapper 的"重查版本再分类"（PD19：修前验证变量写入点）。
- **验证**：构建 + PalORM 测试（并发追加冲突得 EventStreamConcurrencyException）。
- **涉及文件**：`src/PalDDD.PalORM/Stores/PalOrmEventLog.cs`

### [x] ITM-076 · PalOrmSagaStateStore MySQL JOIN 租约跨 owner 覆盖竞态 · 可信度 ⚠
- **维度**：并发（多实例双处理风险）
- **优先级**：P2 近期 · 危害: 高 · 复杂度: 难
- **问题**：`src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs`（84-87 行） MySQL JOIN 路径——REPEATABLE READ 下 derived table 按语句开始快照物化，两个不同 owner 并发 UPDATE 均可基于同一候选集，后到者覆盖先到者的 `leased_by`，双方按 `(leased_by, leased_until)` 回读同一批 → 双 worker 处理同批。现有注释仅声明同 owner 同 tick 场景。⚠ 触发条件未经双连接实测验证，按引擎规则 P2 定稿但行动项标"修复前先补探针"。
- **建议**：① 先补 MySQL 双连接并发探针（dialect-probe 扩展：双 owner 并发租约断言）；② 修复方向：回读 SQL 加 `AND status = 0 AND leased_by = @owner` 二次过滤（覆盖后被排除），或声明与 Dapper SagaLeaseActiveMySql 对齐（version 乐观锁兜底 + 文档声明）。
- **风险**：中——MySQL 方言行为需实测确认。
- **验证**：方言探针新增跨 owner 并发用例 → 修复后复跑探针红转绿。
- **涉及文件**：`src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs`、`.ai/scripts/dialect-probe.sh`

---

## 二、P3 修复项（按族分组，可批量处置）

### 跨实现契约分叉族（PD17 同步）

### [x] ITM-077 · PalOrmInboxStore.MarkFailedAsync 缺 failureReason 校验 · 可信度 ✅
- Dapper（DapperInboxStore.cs:120）/EFCore（InboxDbContext.cs:119）/InMemory（InMemoryInboxStore.cs:88）均有 `ArgumentException.ThrowIfNullOrWhiteSpace(failureReason)`，PalORM 版（PalOrmInboxStore.cs:144-153）缺失。✅ 已亲验四文件。
- 建议：补校验。涉及文件：`src/PalDDD.PalORM/Stores/PalOrmInboxStore.cs`

### [x] ITM-078 · PalORM Idempotency TryStartAsync Completed 返回分叉 · 可信度 ✅
- EFCore（IdempotencyDbContext.cs:56-63）/InMemory（InMemoryIdempotencyStore.cs:84-87）对 Completed 非过期记录返回 null（语义=他人已持有终态）；PalORM（PalOrmIdempotencyStore.cs:99-100）返回 existing——直接调用方会拿到 Completed 记录误以为获得租约。处理器路径安全（先 GetAsync 短路），但公共 API 契约分叉。✅ 已亲验三文件。
- 建议：对齐返回 null（或契约文档声明差异）。涉及文件：`src/PalDDD.PalORM/Stores/PalOrmIdempotencyStore.cs`

### [x] ITM-079 · PalOrmEventLog ReadStream/ReadAll 无 SQL LIMIT 下推 · 可信度 ✅
- Dapper（EventLogSql.cs:37-45 `LIMIT @max`）/EFCore（EventLogDbContext.cs:105 `.Take`）/InMemory（Take）均有服务端上限；PalORM（PalOrmEventLog.cs:123-129/142-148）靠客户端 `--maxCount` 截断，全量查询已下发。大流场景性能分叉。✅ 已亲验四文件。
- 建议：SQL 加 `LIMIT`（注意 maxCount=int.MaxValue 参数化边界，可条件拼接或注释声明）。涉及文件：`src/PalDDD.PalORM/Stores/PalOrmEventLog.cs`

### [x] ITM-080 · DapperEventLog ReadStream/ReadAll 缺 maxCount>=1 守卫 · 可信度 ✅
- EFCore/PalORM/InMemory 均有 `ThrowIfLessThan(maxCount, 1)`，Dapper（DapperEventLog.cs:161-189）只有 fromVersion/fromPosition 守卫。✅ 已亲验。
- 建议：补守卫。涉及文件：`src/PalDDD.Dapper/DapperEventLog.cs`

### [x] ITM-081 · PostgreSqlOutboxDbContext 缺 owner 校验 · 可信度 ✅
- SqlServer 姊妹（SqlServerOutboxDbContext.cs:40）有 `ThrowIfNullOrWhiteSpace(owner)`，PG 版（PostgreSqlOutboxDbContext.cs:40-46）缺失。✅ 已亲验两文件。
- 建议：补校验。涉及文件：`src/PalDDD.Transactions.EFCore/PostgreSqlOutboxDbContext.cs`

### [x] ITM-082 · OutboxDbContext.ReleaseForRetry 无存储层截断 · 可信度 ✅
- MarkDead（OutboxDbContext.cs:81）截断 2040，ReleaseForRetry（:100-118）不截断——Error 列 HasMaxLength(2048)，直连调用方超长失败原因使 ExecuteUpdate 抛错。处理器路径已截断（OutboxBatchProcessor.cs:117-119）不受影响，存储层兜底缺失。✅ 已亲验。
- 建议：ReleaseForRetry 补同款截断。涉及文件：`src/PalDDD.Transactions.EFCore/OutboxDbContext.cs`

### 资源流族

### [x] ITM-083 · DapperBulkCopy DataTable/MySqlBulkCopy 未 Dispose · 可信度 ✅
- DapperBulkCopy.cs:175,189-199——DataTable 与 MySqlBulkCopy（实现 IDisposable）未释放。
- 建议：using/Dispose。涉及文件：`src/PalDDD.Dapper/DapperBulkCopy.cs`

### [x] ITM-084 · KafkaBroker Subscribe 失败 consumer 泄漏 · 可信度 ✅
- KafkaBroker.cs:111-112——Subscribe 在登记 `_consumers`（:121-124）之前，抛异常则未入列未释放。
- 建议：try/catch 释放后重抛。涉及文件：`src/PalDDD.Messaging.Kafka/KafkaBroker.cs`

### [x] ITM-085 · PostgreSqlSharding 构造失败前序 data source 泄漏 · 可信度 ✅
- PostgreSqlSharding.cs:170-177——循环 Build 中途抛错，已构建的 0..i-1 个 NpgsqlDataSource 无人释放。
- 建议：try/catch 清理已建项后重抛。涉及文件：`src/PalDDD.Dapper.PostgreSql/PostgreSqlSharding.cs`

### [x] ITM-086 · MySqlServiceCollectionExtensions legacy 路径连接泄漏 · 可信度 ✅
- MySqlServiceCollectionExtensions.cs:124-131——Optimize(connection) 抛异常时 connection 未 Dispose。
- 建议：try/finally。涉及文件：`src/PalDDD.Dapper.MySql/MySqlServiceCollectionExtensions.cs`

### [x] ITM-087 · PalOrmUnitOfWork.CommitAsync 异常路径不清事务引用 · 可信度 ✅
- PalOrmUnitOfWork.cs:50-60——Commit 抛异常时不清理 `_transaction`/`UseTransaction(null)`，后续 Rollback 二次抛错掩盖根因。
- 建议：异常路径清理（对齐 DapperUnitOfWork）。涉及文件：`src/PalDDD.PalORM/PalOrmUnitOfWork.cs`

### [x] ITM-088 · DapperUnitOfWork.BeginTransactionAsync 重复 Begin 覆盖旧引用 · 可信度 ✅
- DapperUnitOfWork.cs:37——事务已激活时再次 Begin 旧 `_transaction` 被覆盖未处置。
- 建议：前置判活抛 InvalidOperationException。涉及文件：`src/PalDDD.Dapper/DapperUnitOfWork.cs`

### [x] ITM-089 · MySqlPerformanceOptimizer 开连接不归还 · 可信度 ✅
- MySqlPerformanceOptimizer.cs:44,62——Optimize/SetUtf8mb4 内部 Open 不 Close（池化可接受、直连场景泄漏）。
- 建议：方法内 using 或文档声明生命周期归调用方。涉及文件：`src/PalDDD.Dapper.MySql/MySqlPerformanceOptimizer.cs`

### [x] ITM-090 · PalOutboxHealthCheck Scoped 依赖注入 Singleton IHealthCheck · 可信度 ⚠
- HealthCheckExtensions.cs:101-105——PalOutboxHealthCheck 构造可选注入 `IPalOutboxStore?`（Scoped），AddCheck\<T\> 注册的 IHealthCheck 生命周期需运行时验证（ValidateScopes 开启时可能根解析异常）。⚠ 待运行时验证。
- 建议：探针确认 ASP.NET Core 健康检查解析 scope 行为后处置（若按 scope 解析则关闭本条）。涉及文件：`src/PalDDD.Hosting.AspNetCore/AspNetCore/HealthCheckExtensions.cs`

### 错误流族

### [x] ITM-091 · EndpointExtensions PalValidationException catch 只包 JSON 读 · 可信度 ✅
- EndpointExtensions.cs:35-46/81-92——catch 只包 ReadFromJsonAsync；SendAsync（:55/101）在 try 外，验证失败异常走中间件（已注册时 400，未注册时 500）。注释宣称的"验证失败映射 400"与实现位置不符。
- 建议：catch 移至包住 SendAsync，或注释如实声明依赖 ExceptionMiddleware。涉及文件：`src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs`

### [x] ITM-092 · InboxProcessor/ProjectionProcessor catch 内 MarkFailed 抛错覆盖主异常 · 可信度 ✅
- InboxProcessor.cs:108-120、ProjectionProcessor.cs:60-64——`catch → await MarkFailedAsync → throw;` 中标记持久化失败时主异常被覆盖。
- 建议：标记失败时挂 Data 后重抛主异常（对齐 SagaProcessor 模式）。涉及文件：`src/PalDDD.Transactions/InboxProcessor.cs`、`src/PalDDD.Projections/ProjectionProcessor.cs`

### [x] ITM-093 · PalPlatformVerifier catch 无 OCE 过滤 · 可信度 ✅
- PalPlatformVerifier.cs:36——`catch(Exception) when(not OOM)` 无 OCE 过滤（同步路径无 ct 但 converter 可抛 OCE）。
- 建议：补 `when(ex is not OperationCanceledException)`。涉及文件：`src/PalDDD.Serialization.Evolution/PalPlatformVerifier.cs`

### [x] ITM-094 · PostgreSqlOutboxNotifier Task.Run OCE 逃逸 · 可信度 ✅
- PostgreSqlOutboxNotifier.cs:157——fire-and-forget 任务内 catch 过滤非 OCE，OCE 未观察。
- 建议：任务内捕获 OCE 记录或 SuppressMessage+理由。涉及文件：`src/PalDDD.Dapper.PostgreSql/PostgreSqlOutboxNotifier.cs`

### [x] ITM-095 · StrategicDddCodeFixProvider int.Parse 无保护 · 可信度 ✅
- StrategicDddCodeFixProvider.cs:73-74——`diagnostic.Properties["SchemaVersion"]` 直接 int.Parse，属性异常时 FormatException 破坏修复 UI。
- 建议：TryParse 失败跳过修复。涉及文件：`src/PalDDD.Analyzers.CodeFixes/StrategicDddCodeFixProvider.cs`

### [x] ITM-096 · PalOrmSagaStateStore reader.GetDateTime 无 IsDBNull · 可信度 ✅
- PalOrmSagaStateStore.cs:166-178——created_at/error_at/leased_until 等时间列无 IsDBNull（脏数据 InvalidCastException）。
- 建议：按列 IsDBNull 处理。涉及文件：`src/PalDDD.PalORM/Stores/PalOrmSagaStateStore.cs`

### [x] ITM-097 · OutboxBatchProcessor 取消时指标不记录 · 可信度 ✅
- OutboxBatchProcessor.cs:110-132——单条 OCE 直接传播中止整批，循环后指标段（:135-139）不执行。
- 建议：指标段移 finally 或 OCE 前先记录已处理数。涉及文件：`src/PalDDD.Transactions/OutboxBatchProcessor.cs`

### [x] ITM-122 · StrategicDddAnalyzer PDDD003 shape 检查未沿基类链取 [BoundedContext] · 可信度 ⚠
- StrategicDddAnalyzer.cs:322-330——PDDD003 用直接声明取 `boundedContext`（:203 TryGetAttribute 仅本类型），同文件 PDDD004 已改 `HasAttributeAlongBaseChain`（:365）、PDDD013/014 已改链式（:343-344/383）——ProcessManager 继承基类 `[BoundedContext]`（Inherited=true）时 PDDD003 误报"未声明"。⚠ 分析器触发行为需 `dotnet build` 验证（误判库模式 4）。
- 建议：PDDD003 改用与 PDDD004 同款链式取参。涉及文件：`src/PalDDD.Analyzers/StrategicDddAnalyzer.cs`

### [x] ITM-123 · StrategicDddAnalyzer struct 领域事件不可消解 · 可信度 ⚠
- StrategicDddAnalyzer.cs:200,209-215,217-226——`TypeKind.Struct` 放行（仅 interface 短路），struct 实现 IDomainEvent 时命中 IsDomainEventType，而 `[GenerateMessage]`/`[BoundedContext]` 均 AttributeTargets.Class → PDDD001/PDDD005 对 struct 事件不可消解。⚠ 需 build 验证触发条件。
- 建议：TypeKind 检查补 Struct 排除或诊断文案声明仅支持 class。涉及文件：`src/PalDDD.Analyzers/StrategicDddAnalyzer.cs`

### [x] ITM-124 · StrategicDddAnalyzer PDDD015 EventName 非字面量误报 · 可信度 ⚠
- StrategicDddAnalyzer.cs:243-254/664——EventName 声明为非字面量（const 拼接）时返回 (null, location)，PDDD015 以空串比对报 mismatch 误报。⚠ 需 build 验证。
- 建议：非字面量时跳过该比对或降级提示。涉及文件：`src/PalDDD.Analyzers/StrategicDddAnalyzer.cs`

### [x] ITM-125 · OutboxDbContext.AddMessagesAsync SaveChanges 无 ct · 可信度 ✅
- OutboxDbContext.cs:30——`SaveChangesAsync()` 未传 CancellationToken，长事务不可取消。
- 建议：`SaveChangesAsync(ct)` 透传。涉及文件：`src/PalDDD.Transactions.EFCore/OutboxDbContext.cs`

### 生成语义族

### [x] ITM-098 · IdentityGenerator hint/转换器命名 Collision · 可信度 ✅
- IdentityGenerator.cs 217/262-267 行——命名空间级 Outer1_Foo 与嵌套 Outer1.Foo 同 hint（X.Outer1_Foo.g.cs）→ PALID004 误报 + CS0101 重复定义（示例名，非仓库现有标识符）。
- 建议：hint 用完整符号名（含命名空间）或嵌套层级编码。涉及文件：`src/PalDDD.Core.SourceGen/IdentityGenerator.cs`

### [x] ITM-099 · IdentityGenerator operator++/-- 溢出回绕 · 可信度 ✅
- IdentityGenerator.cs:409-410——long.MaxValue 自增回绕负数。
- 建议：checked 或文档声明。涉及文件：`src/PalDDD.Core.SourceGen/IdentityGenerator.cs`

### [x] ITM-100 · EnumGenerator record struct 分支落错诊断 · 可信度 ✅
- EnumGenerator.cs:92-104/172-173——record struct 声明落基类检查报 PALENUM002 而非 PALENUM003；PALENUM001 文案"public static fields"与 internal 收集条件不符。
- 建议：补 record struct 前置分支 + 文案修正。涉及文件：`src/PalDDD.Core.SourceGen/EnumGenerator.cs`

### [x] ITM-101 · SmartEnum Name null/重复 Value 无诊断 · 可信度 ✅
- SmartEnum.cs:36,74-77——自定义 TValue.ToString() 返回 null 时 Name 为 null；重复 Value 静默覆盖。
- 建议：构造/注册期校验。涉及文件：`src/PalDDD.Core/SmartEnum.cs`

### [x] ITM-102 · MessageCatalog Find(name,0) 与 builder 可注册 0 矛盾 · 可信度 ✅
- MessageCatalog.cs:90（ThrowIfLessThan 1）vs builder（:115-124 无下限校验）——schemaVersion=0 可注册不可查。
- 建议：builder 补下限校验或 Find 对齐。涉及文件：`src/PalDDD.Serialization/MessageCatalog.cs`

### [x] ITM-103 · OutboxDomainEventInterceptor CausationId 自指 · 可信度 ❓
- OutboxDomainEventInterceptor.cs:134——`CausationId = evt.EventId`（事件自指），对照 EventLog 路径从 EventAuditMetadata 映射。❓ 待设计定案（可能是有意简化，缺父事件追踪）。
- 建议：设计评审定案后处置（对齐或声明）。涉及文件：`src/PalDDD.Repository.EFCore/OutboxDomainEventInterceptor.cs`

### 并发/状态族

### [x] ITM-104 · SagaStateDbContext 租约冲突 detach 全部 TState · 可信度 ✅
- SagaStateDbContext.cs:92-94——detach 遍历全部 TState 跟踪实体，同 scope 其他已加载状态被脱轨。
- 建议：只 detach 本批 states。涉及文件：`src/PalDDD.Transactions.EFCore/SagaStateDbContext.cs`

### [x] ITM-105 · InMemoryInboxStore 超时接管无所有权守卫 · 可信度 ✅
- InMemoryInboxStore.cs:48-53/71-96——接管复用同一实例且 Mark 无守卫（对照 InMemoryIdempotencyStore 的 successor 隔离）。
- 建议：对齐 successor 隔离或守卫。涉及文件：`src/PalDDD.Transactions/InMemoryInboxStore.cs`

### [x] ITM-106 · InMemoryProjectionCheckpointStore 字典无限增长 · 可信度 ✅
- InMemoryProjectionCheckpointStore.cs:47-49——Completed/Processing 检查点永不移除，position 推进累积。
- 建议：Completed 记录清理或容量上限声明。涉及文件：`src/PalDDD.Projections/InMemoryProjectionCheckpointStore.cs`

### [x] ITM-107 · DapperProjectionCheckpointStore leaseUntil 无正数校验 · 可信度 ✅
- DapperProjectionCheckpointStore.cs:60——processingTimeout 非正时租约立即过期。
- 建议：ThrowIfLessThanOrEqual。涉及文件：`src/PalDDD.Dapper/DapperProjectionCheckpointStore.cs`

### [x] ITM-108 · TransactionOptions.LeaseOwner 默认值每实例漂移 · 可信度 ✅
- TransactionOptions.cs:35,51——属性初始化器内 new Ulid，直构（非 IOptions）场景 owner 每次不同 → 租约守卫失效。
- 建议：文档声明"须经 IOptions 单例"或静态默认。涉及文件：`src/PalDDD.Transactions/TransactionOptions.cs`

### [x] ITM-109 · DapperOutboxStore 同 tick 同 until 租约回读 · 可信度 ✅
- DapperOutboxStore.cs:148-153 + OutboxSelectByLease——冻结时钟下同 owner 同 until 二次调用回读上一批（PalORM 已声明同限制，Dapper 缺声明）。
- 建议：补声明或回读加守卫。涉及文件：`src/PalDDD.Dapper/DapperOutboxStore.cs`、`src/PalDDD.Dapper/SqlTemplates.cs`

### 其他

### [x] ITM-110 · PostgreSqlMultiHost 前导逗号/重复 Host · 可信度 ✅
- PostgreSqlMultiHost.cs:70,101-102——主串无 Host 时 `Host += ",pg2"` 前导逗号；零副本时 failover(primary,primary) 重复。
- 建议：拼接规范化。涉及文件：`src/PalDDD.Dapper.PostgreSql/PostgreSqlMultiHost.cs`

### [x] ITM-111 · SqliteServiceCollectionExtensions :memory: 子串误判 · 可信度 ✅
- SqliteServiceCollectionExtensions.cs:56——`Contains(":memory:")` 子串匹配，文件路径巧合含该子串被误分类 Singleton。
- 建议：规范化连接串解析。涉及文件：`src/PalDDD.Dapper.Sqlite/SqliteServiceCollectionExtensions.cs`

### [x] ITM-112 · PostgreSqlReadWriteRouter 缺 Host 静默跳过 · 可信度 ✅
- PostgreSqlReadWriteRouter.cs:100-108——副本串缺 Host 静默跳过不报错。
- 建议：配置错误显式报错。涉及文件：`src/PalDDD.Dapper.PostgreSql/PostgreSqlReadWriteRouter.cs`

### [x] ITM-113 · MySqlMultiHost 双注册双处置 · 可信度 ⚠
- MySqlMultiHost.cs:75-76,103-104,127-128——同一 MySqlDataSource 注册两种服务类型，容器 Dispose 双处置（PG 版有幂等探针声明，MySQL 缺）。
- 建议：探针验证 MySqlConnector Dispose 幂等性后声明或修复。涉及文件：`src/PalDDD.Dapper.MySql/MySqlMultiHost.cs`

### [x] ITM-114 · PostgreSqlReportHelper byte[]/float 导出降级 · 可信度 ✅
- PostgreSqlReportHelper.cs:69,198,201——bytea 导出 "System.Byte[]" 字符串；float/Guid? 同降级；CSV 时间丢时区。
- 建议：按类型映射（Base64/原生格式化）。涉及文件：`src/PalDDD.Dapper.PostgreSql/PostgreSqlReportHelper.cs`

### [x] ITM-115 · PostgreSqlAuditor ChangedAt 丢时区 · 可信度 ✅
- PostgreSqlAuditor.cs:186——DateTime 映射 timestamptz 丢偏移。
- 建议：DateTimeOffset。涉及文件：`src/PalDDD.Dapper.PostgreSql/PostgreSqlAuditor.cs`

### [x] ITM-116 · SqliteRowFactory ParseGuid 注释与代码矛盾 · 可信度 ✅
- SqliteRowFactory.cs:58,60——注释称"静默 Guid.Empty 改抛"，代码 DBNull 仍返回 Guid.Empty。
- 建议：注释或代码二选一修正。涉及文件：`src/PalDDD.Dapper/SqliteRowFactory.cs`

### [x] ITM-117 · EventLogGlobalPositionAllocator Revision 溢出 · 可信度 ✅
- EventLogGlobalPositionAllocator.cs:65——uint Revision++ 溢出回绕。
- 建议：checked 或 ulong。涉及文件：`src/PalDDD.EventLog.EFCore/EventLogGlobalPositionAllocator.cs`

### [x] ITM-118 · IdempotencyPolicy 无参数校验 · 可信度 ✅
- IdempotencyPolicy.cs:7-9——ProcessingTimeout/Retention 负值/倒挂不报错。
- 建议：补校验。涉及文件：`src/PalDDD.Idempotency/IdempotencyPolicy.cs`

### [x] ITM-119 · DapperOutboxStore 同 tick 租约回读（见 ITM-109，合并处置）

### [x] ITM-120 · InMemoryEventLog read 计数在 yield 后 · 可信度 ✅
- InMemoryEventLog.cs:113-118/152-157——`read++` 在 yield 之后，消费者早退（break）时最后一个事件不计入指标（finally 修复未消除该偏差）。
- 建议：yield 前计数或记录已产出数。涉及文件：`src/PalDDD.EventLog/InMemoryEventLog.cs`

---

## 三、证伪记录（本轮误判库候选）

| 候选 | 子代理初判 | 主线程反证 | 处置 |
|------|:--:|------|------|
| SqlTemplates `@ca` 参数语义冲突（SagaInsert/SagaUpdate 同参数名绑不同列） | **P1** | 亲验 DapperSagaStateStore：INSERT 分支传 `ca=CreatedAt`+`completedAt=CompletedAt`，UPDATE 分支传 `ca=CompletedAt`——各语句与其调用方绑定一致，无缺陷（命名易混淆但非 bug） | 证伪 → 建议：参数改名消歧（P3） |
| PalOrmProjectionCheckpointStore 接管后本地 Revision 不同步 | P3 | 亲验 ProjectionCheckpoint.MarkProcessing 内含 `Revision++`（ProjectionCheckpoint.cs:84），本地同步存在 | 证伪 |
| InMemorySagaStateStore 无同 owner 续租 | P3 | 亲验 Dapper/PalORM/EFCore 三版 SQL 均无 `leased_by=@owner` 续租分支（`leased_until IS NULL OR <= now` 为准），InMemory 与 DB 对齐 | 证伪 |
| DapperEventLog 头部 AOT 注释"IsAotCompatible=true 但反射物化" | P2 | PD3 勘正已记录（Dapper 四项目 true 为分层语义非运行时保证，csproj Description 自声明 AOT 假象） | 证伪（已有知识覆盖） |

**误判库候选模式（PD24 候选）**：SQL 模板参数名跨语句复用（同参数名在不同语句绑不同列）≠ 语义冲突——须核对调用方各分支实参后再定级；子代理跨文件推断必须移交主线程。

---

## 三·五、系统自身缺陷（本轮发现并已修复）

### [x] ITM-121 · verify-action-items.sh 语法损坏（`; then` 被注释吞掉）· 可信度 ✅
- **维度**：AI 质量系统自身缺陷（事后验证回路失效）
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`.ai/scripts/verify-action-items.sh` 与根 `scripts/verify-action-items.sh`（双源同步两份）第 63/73 行——2026-08-15 "元审计脚本#30" 补 `.ai` 目录时把 `2>/dev/null; then` 写进了行尾 `#` 注释内，`if/elif` 语句缺 `; then` 终止符，脚本从该 commit 起**每次运行必报 "syntax error near unexpected token `else'"**（exit 1）——事后验证回路静默失效，且 verify-ai-system.sh V1-V15 均不覆盖脚本语法（全部 PASS 造成"系统健康"假象）。
- **修复**：`2>/dev/null; then` 移出注释（两份同步），已修复并复跑通过。
- **验证**：`bash .ai/scripts/verify-action-items.sh docs/review/action-items-2026-08-16.md` → 63 found / 0 missing ✅（修复前 syntax error exit 1）
- **涉及文件**：`.ai/scripts/verify-action-items.sh`、`scripts/verify-action-items.sh`
- **下沉建议**：verify-ai-system.sh 增 V16：所有 .ai 脚本 `bash -n` 语法检查（防"存在但不可运行"）。

---

## 四、下沉审查（P2 收口）

| 发现 | 可机械检测？ | 下沉动作 |
|------|------|------|
| ITM-071（catch 过宽） | 部分（grep catch(DbUpdateException) 后人工） | 建议下沉：gate G 项或 boundary 测试——"所有 catch(DbUpdateException) 必须带 IsUniqueConstraintViolation 过滤"（同类 5 处已 4 处修复，防第六处） |
| ITM-073（AOT 豁免漏洞） | ✅ | gate-check.sh G8 豁免逻辑修正（按标注行判定非文件子串）——立即下沉 |
| ITM-074（生成器 null 崩溃） | ✅ | SourceGen 负向测试固化——随修复下沉 |
| ITM-072/075/076 | 否（需语义判断） | 保留提示词检查项 + 误判库模式（PD17 已有） |

---

## 五、验证轮收口（敌对审查）

> 修复轮完成后按「评审-修复循环协议」执行验证轮（独立上下文敌对审查），发现及处置如下。
> 每项返工均经修复后复查（构建 + 受影响测试项目全绿 + 全量 955 测试复跑）。

| # | 验证轮发现 | 处置 |
|---|-----------|------|
| V-1 | ITM-112 修复以 `is null` 判 `NpgsqlConnectionStringBuilder.Host`——Npgsql 空 host 语义为 String.Empty 而非 null，校验被绕过 | 返工：`string.IsNullOrWhiteSpace(sb.Host)`；同族 ITM-110 姊妹分支 `psb.Host` 前导逗号归一化同步修正（PostgreSqlReadWriteRouter.cs） |
| V-2 | ITM-098 生成器 hint 分隔符与确认表口径不一致 | 返工：统一为 `+`。最终格式 `{Namespace ?? "_"}+{ContainingNames joined "+"}.{TypeName}.g.cs`（嵌套类型） |
| V-3 | ITM-092 内层 catch 未过滤 OCE——取消时 MarkFailed 抛错覆盖主异常 | 返工：InboxProcessor / ProjectionProcessor 去掉内层 MarkFailed catch 的 OCE 过滤 |
| V-4 | ITM-091 仅主 MapCommand 写 ValidationProblemResponse，姊妹重载 catch 后空响应 | 返工：两个 MapCommand 方法 PalValidationException 分支均写响应体（EndpointExtensions.cs） |
| V-5 | ITM-071 回归测试 fake 类名 FakePostgresException 不匹配鸭子判定类型名 | 返工：改名 `PostgresException`（匹配 `Name == "PostgresException"` 判定） |
| V-6 | 初版修复未落实 ITM-122/123/124；ITM-090 待裁决 | ITM-122（PDDD003 链式 BoundedContext）/ ITM-123（struct 事件契约排除）/ ITM-124（PDDD015 非字面量跳过）补实现 + 3 项回归测试；ITM-090 官方源码验证关闭（AddCheck\<T\> 注册 HealthCheckRegistration，DefaultHealthCheckService 按次解析 scope） |
| V-7 | ITM-072（SaveChanges 返回 1/0 + 条件 detach）与 ITM-101 行为变更 | 复查确认符合契约语义（0=目标行不存在或乐观锁冲突）；受影响测试项目全绿 |

> 验证轮方法论结论：修复者视角存在系统性盲区（54 项修复中 6 项需返工）——「修复轮后必须验证轮」协议再次生效。

---

## 六、终验轮收口（修复后第二轮验证，2026-08-16）

> 按协议「验证轮有发现 → 再修复 → 再验证」，上轮 V-1..V-7 返工后执行终验轮。
> 终验子代理通道三连中断（无产出），按会话纪律主线程接管亲验。V-1..V-7 逐项确认全部落实
> （证据行号见上表处置列）；终验盲区（构建/CI/配置层）发现 6 项，全部修复并经机械轴复跑验证。

| # | 发现（主线程亲验，含触发路径） | 严重级 | 处置 |
|---|------|:--:|------|
| B-1 | `.github/workflows/ci.yml` 测试步骤 `dotnet test PalDDD.slnx` 批量运行 MTP 项目——实测（Compression+CQRS 双项目）exit 5 handshake 失败，CI 每次必红；同文件 AI 自检步骤引用 `.ai/scripts/*`，而 `.ai/` 被 .gitignore 第 95 行整体忽略（`git ls-files .ai` 为空），fresh checkout 该步骤必然 "No such file or directory" | **P1** | 测试步骤改逐项目循环（MTP 手写协议注释 + PalDDD.Testing 排除）；AI 步骤改条件执行——本地装有 .ai 跑全量自检，否则显式 SKIP 并退化为根 scripts/gate-check.sh（G1-G3 快速门禁） |
| B-2 | ci-coverage.sh（19 行）拼写 --nologe——实测 `dotnet build --nologe` 报 MSB1001 未知开关，`set -e` 下覆盖率管线第一步即死；且 `--collect:"XPlat Code Coverage"` 实测（单项目）同样触发 VSTest 握手 exit 5，与 MTP 不兼容 | **P2** | 改 `--nologo`；覆盖率改 MTP 原生 `--coverage --coverage-output-format cobertura` 逐项目收集（Compression.Tests 实测 30/30 通过 + cobertura 产物生成），reportgenerator glob 同步改 `TestResults/coverage.*.cobertura.xml` |
| B-3 | scripts/verify-conventions.sh（215 行）full 模式 `dotnet test PalDDD.slnx` 批量测试——同 B-1 机制必失败，"零失败"永远不可达；scripts/review-snapshot.sh（64 行）向评审员建议同一条必失败命令 | **P2** | verify-conventions 改逐项目循环（任一失败置 FAIL）；review-snapshot 建议命令改为逐项目表述 |
| B-4 | README.md（635 行）测试计数 "933 测试" 与实测 955（16 项目全绿）不符 | P3 | 更新为 955 |
| B-5 | CHANGELOG.md（5 行）"当前版本 VersionPrefix=1.0.0/preview.1" 与 Directory.Build.props 实际 `1.1.0` + 空 suffix 矛盾 | P3 | 更新为 1.1.0 / VersionSuffix 空 |
| B-6 | scripts/publish-main.sh（11 行）收尾 `git checkout master`——仓库无本地 master 分支（仅 main/master-archive），push 成功后脚本 set -e 退出 1 | P3 | 记录原分支（空则回退 main），发布后切回原分支再删孤儿分支 |

> 终验轮方法论结论：**验证轮必须换轴**——前两轮全盯 src/test 修复 diff，零发现的表象下
> 配置/CI 层仍藏着一个 P1（CI 全线红）。本轮把「盲区补扫」从形式改为固定新轴
> （构建/CI/配置层），机械轴无法覆盖"命令只在 CI 环境才执行"的缺陷类型。
