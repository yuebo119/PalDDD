# 任务清单 2026-08-15 — 全量评审 P1 修复

> 来源：AI 质量系统全量运行（四系统 + 4 片地毯式评审，199 文件 / 19888 行，基线 commit 95298c1（git log 可查））。
> 定级采用 **危害 × 复杂度** 双维度（模板见 docs/review/ACTION_ITEMS_TEMPLATE.md）。
> 本轮修复范围：ITM-061..070（全部 P0/P1 级 + 2 项降级后仍值得修的 P2）。P2/P3 其余项见文末"待排期"，不在本轮。

---

### [x] ITM-061 · MySQL Inbox 幂等守卫缺失 · 可信度 ✅
- **维度**：正确性（数据重复处理）
- **优先级**：P0 紧急 · 危害: 高 · 复杂度: 易
- **问题**：`SqlTemplates.InboxInsertMySql` 为 `INSERT IGNORE ...; SELECT LAST_INSERT_ID();`，被唯一约束忽略时 LAST_INSERT_ID() 返回 0 或同连接陈旧自增 ID，DapperInboxStore 的 TryStartProcessingAsync 方法的 `insertedId.HasValue` 恒真，伪造 Processing 记录，重复消息被再次处理。同文件 PG 版（ON CONFLICT RETURNING）与 SQLite 版（changes() 守卫）均有守卫。✅ 已 grep 验证存在（SqlTemplates.cs:148、DapperInboxStore.cs:55）
- **建议**：SELECT 追加 `WHERE ROW_COUNT() > 0`（MySQL 与 SQLite changes() 同型的守卫模式）。
- **验证**：构建 + 既有测试全绿；SQL 模板断言测试（PalDDD.Dapper.Tests 若有模板测试则更新）。
- **涉及文件**：`src/PalDDD.Dapper/SqlTemplates.cs`

### [x] ITM-062 · JSONB Escape 回归（条件永假）· 可信度 ✅
- **维度**：正确性（静默空结果，公共 API）
- **优先级**：P1 近期 · 危害: 高 · 复杂度: 易
- **问题**：commit e892da8 将 `Escape` 改为带外层双引号（标识符语义），但 Include/HasKey 等模板未同步，key 位置变成双层引号，`@>`/`?` 条件永假。当前仓库零内部调用，爆炸半径为公共 API 消费者。
- **建议**：拆分两种转义——`Escape`（标识符，双引号包裹）与新增 `EscapeLiteral`（单引号字面量内文转义）；JSON key 位置改用 `EscapeJsonValue`。模板逐一修正。
- **验证**：构建 + SQL 生成字符串断言测试。
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlJsonbExtensions.cs`

### [x] ITM-063 · DapperEventLog maxCount 静默忽略 · 可信度 ✅
- **维度**：契约正确性
- **优先级**：P1 近期 · 危害: 中 · 复杂度: 易
- **问题**：ReadStream 与 ReadAll 两个方法接收 `maxCount` 但 EventLogSql 模板无 LIMIT 子句，参数被静默忽略。✅ 已 grep 验证（EventLogSql.cs 零 LIMIT 命中）
- **建议**：三方言模板加 `LIMIT @max`，调用方传 maxCount。
- **验证**：构建 + 测试；模板断言。
- **涉及文件**：`src/PalDDD.Dapper/EventLogSql.cs`、`src/PalDDD.Dapper/DapperEventLog.cs`

### [x] ITM-064 · PalORM 幂等过期记录永久卡死 · 可信度 ✅
- **维度**：正确性（请求被静默拒绝）
- **优先级**：P1 近期 · 危害: 高 · 复杂度: 中
- **问题**：TryStartAsync INSERT 冲突后走 GetAsync，GetAsync 对过期记录返回 null → TryStartAsync 返回 null（语义=他人持有），过期 key 永久拒绝。EFCore 版过期走复用、InMemory 版过期先删，三实现分叉。
- **建议**：INSERT 冲突且 GetAsync 返回 null（记录存在但已过期）时，执行条件 UPDATE 重新获取租约（乐观守卫 expires_at <= now），对齐 EFCore 复用语义。
- **验证**：构建 + PalORM 测试（SQLite 路径）；过期复用单元测试。
- **涉及文件**：`src/PalDDD.PalORM/Stores/PalOrmIdempotencyStore.cs`

### [x] ITM-065 · EFCore 幂等 catch(DbUpdateException) 过宽 · 可信度 ✅
- **维度**：健壮性（基础设施故障误判为冲突，请求丢失）
- **优先级**：P1 近期 · 危害: 中 · 复杂度: 易
- **问题**：`TryCreateRecordAsync` 把任何 DbUpdateException 当唯一键冲突返回 null；连接断开等故障被吞为"他人已持有"。
- **建议**：复用 EventLogDbContext/InboxDbContext 已有的 IsUniqueConstraintViolation 鸭子类型判定（ITM-003 同型对齐），仅冲突返回 null，其余上抛。
- **验证**：构建 + EFCore InMemory 测试。
- **涉及文件**：`src/PalDDD.Idempotency.EFCore/IdempotencyDbContext.cs`

### [x] ITM-066 · Saga 租约读改写非原子 · 可信度 ✅
- **维度**：并发（多实例整轮失败）
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：LeaseActiveSagasAsync 先查后改再 SaveChanges，多实例同批互撞抛 DbUpdateConcurrencyException 整轮失败（被 OnTickFailed 吞掉重试）。对照同库 Outbox 的 `FOR UPDATE SKIP LOCKED` 单语句原子租约。
- **建议**：最小修复——捕获 DbUpdateConcurrencyException 视为"本轮未获取任何租约"（SaveChanges 原子性保证无部分写入），记日志返回空，下轮重试。跨方言 SKIP LOCKED 改造（SQLite 不支持）留待后续 ADR。
- **验证**：构建 + 既有测试。
- **涉及文件**：`src/PalDDD.Transactions.EFCore/SagaStateDbContext.cs`

### [x] ITM-067 · PostgreSqlMultiHost 读写分离名实不符 · 可信度 ✅
- **维度**：契约正确性（写操作可能路由到只读副本）
- **优先级**：P1 近期 · 危害: 高 · 复杂度: 中
- **问题**：`AddPalNpgsqlDataSourceWithReadWriteSplit` 注释承诺"写走 primary，读走 replicas"，实现为单数据源且会话目标属性为 any + LoadBalanceHosts——写会被负载均衡到副本导致失败。真正的分离在 `AddPalPostgreSqlReadWriteRouter`。仓库内零调用。
- **建议**：数据源会话目标属性改为 primary（写安全），XML doc 如实描述为"主库亲和数据源"，并指向 ReadWriteRouter 获取真读写分离。
- **验证**：构建 + 连接串构建断言测试（若存在）。
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlMultiHost.cs`

### [x] ITM-068 · PostgreSqlPipeline 返回值语义错误 · 可信度 ✅
- **维度**：契约正确性
- **优先级**：P1 近期 · 危害: 中 · 复杂度: 易
- **问题**：`ExecuteAsync` 注释承诺"受影响总行数"，实现统计结果集行数——UPDATE/INSERT 类命令 PostgreSQL 返回命令标签而非结果行，计数恒 0。
- **建议**：改用 `reader.RecordsAffected` 累计（保留读循环排空结果集）。
- **验证**：构建；行为验证需 PG 实例（标注）。
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlPipeline.cs`

### [x] ITM-069 · DynamicStep 路由到特殊步骤 NRE · 可信度 ⚠
- **维度**：健壮性（NRE 崩溃且信息误导）
- **优先级**：P1 近期 · 危害: 高 · 复杂度: 易
- **问题**：src/PalDDD.Transactions/Saga.cs 的 DynamicStep 路由分发不检查 `matchedStep.DispatchKind`，直接调 `ExecuteAsync`；FanOut/Interrupt/Dynamic 步骤构造时 `execute: null!`，命中即 NRE。⚠ 触发条件（用户将特殊步骤注册进路由表）未验证，按引擎规则降 P2 后仍属廉价防御。
- **建议**：分发前检查 DispatchKind，特殊步骤抛明确 InvalidOperationException（说明不支持路由到该类型）。
- **验证**：构建 + Core 测试。
- **涉及文件**：`src/PalDDD.Transactions/Saga.cs`

### [x] ITM-070 · EnumGenerator partial 跨文件遗漏 · 可信度 ⚠
- **维度**：生成器正确性
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：transform 只扫描带 `[GenerateEnum]` 的那个 ClassDeclarationSyntax 声明；静态字段定义在另一 partial 文件时收集为空 → PALENUM001 且不生成注册代码。⚠ 依赖用户跨文件拆分用法，仓库内无此用法。
- **建议**：改用 INamedTypeSymbol 的 DeclaringSyntaxReferences 聚合全部 partial 声明收集成员。
- **验证**：构建 + SourceGen 测试（51 项全过）。
- **涉及文件**：`src/PalDDD.Core.SourceGen/EnumGenerator.cs`

---

## 第二轮（同日追加）— P2/P3 机械可修项

### [x] 已修复（第二轮，14 项）

| 修复 | 文件 |
|---|---|
| InMemoryOutboxStore 三个状态方法补锁 | src/PalDDD.Transactions/InMemoryOutboxStore.cs |
| SagaExecutionObserver 嵌套恢复外层注册 | src/PalDDD.Transactions/SagaExecutionObserver.cs |
| KafkaBroker _consumers 锁 + 订阅句柄兜底释放 consumer（Task.Run 未启动泄漏窗口） | src/PalDDD.Messaging.Kafka/KafkaBroker.cs |
| RabbitMQ Nack 安全包装（channel 已关不逃逸）+ Dispose 所有权契约（仅释放独占 Channel） | src/PalDDD.Messaging.RabbitMQ/RabbitMqBroker.cs |
| SQLite 文件模式改 Scoped（:memory: 保持 Singleton） | src/PalDDD.Dapper.Sqlite/SqliteServiceCollectionExtensions.cs |
| ProjectionCheckpointDbContext catch 收窄到唯一约束（第四处 ITM-003 同型） | src/PalDDD.Projections.EFCore/ProjectionCheckpointDbContext.cs |
| DapperProjectionCheckpointStore 乐观并发 rows=0 不再变更本地对象（两处） | src/PalDDD.Dapper/DapperProjectionCheckpointStore.cs |
| DapperEventLog 并发冲突转 EventStreamConcurrencyException（DbException 鸭子判定，IL2075 安全降级） | src/PalDDD.Dapper/DapperEventLog.cs |
| MessageEvolutionPipeline 构造期严格递增校验（防回环死循环） | src/PalDDD.Serialization.Evolution/MessageEvolutionPipeline.cs |
| DomainEventDispatcher 超限错误消息改为如实描述（批量上限而非"事件循环"） | src/PalDDD.Messaging/DomainEventDispatcher.cs |
| PostgreSqlSharding GetShard 越界显式报错 | src/PalDDD.Dapper.PostgreSql/PostgreSqlSharding.cs |
| SqlitePerformanceOptimizer source_id 切片越界守卫 | src/PalDDD.Dapper.Sqlite/SqlitePerformanceOptimizer.cs |
| IdentityGenerator 嵌套类型按 ContainingType 链包 partial（零嵌套输出不变） | src/PalDDD.Core.SourceGen/IdentityGenerator.cs |
| 死代码 ×2（GetNamedArgumentValue/同步 EnsureOpen）+ 注释漂移 ×1 + 入参校验 ×3（SagaStep name/InterruptStep/DynamicStep） | 各对应文件 |

**第二轮教训（误判库候选）**：SagaStep 基类构造对 execute 加 null 校验会误伤四类特殊步骤的既有 null! 契约（FanOut/Child/Dynamic/Interrupt），测试 4 失败后回退——特殊步骤防御应只在路由 DispatchKind 守卫（ITM-069 已做）。

### [ ] 剩余待排期（需设计决策或环境实证）

### [x] 第三轮（同日）— 设计决策 10 项 + 环境实证 4 项全部处置

| 决策/实证 | 定案 | 落点 |
|---|---|---|
| RabbitMQ 匿名队列 requeue 语义 | requeue:false + 如实日志（exclusive 队列 requeue:true 是自我热循环且"重连重投"不可能；与 Kafka ITM-008 对齐） | RabbitMqBroker.cs |
| MySqlMultiHost failover 凭据丢弃 | 构造期校验 Port/User/Password/Database 一致，不一致快速失败（MySQL 连接串对这些参数全节点统一生效，差异无法表达） | MySqlMultiHost.cs |
| outbox status 两适配器编码互斥 | 文档级声明：两侧 DI 入口标注"禁止同库同时注册" | DapperServiceCollectionExtensions.cs / SqlitePalOrmExtensions.cs |
| PalORM 审计字段持久化 | 实现：EventLogRow +4 列（correlation/causation/trace_parent/trace_state）+ INSERT/SELECT/ToRecorded 全链路 + 三方言 DDL + 000_schema.sql | EventLogRow.cs / PalOrmEventLog.cs / MultiDialectSchema.cs / 000_schema.sql |
| 导入流字段丢弃 | 设计意图声明：导入即重建（目标流边界/版本由 AppendAsync 重分配） | EventStreamJsonLines.cs 头注释 |
| 单类型流约定 | 设计约束声明：投影回放要求每流单契约，异构流需按 EventName 分组预过滤 | EventLogReplaySource.cs |
| FanOut 可空结果过滤 | 以完成标记收集替代非空过滤（TResult 可空时成功 null 结果不再误判丢弃） | FanOutStep.cs |
| OutboxMessage TimeProvider 双轨 | 镜像 DomainEvent 的 internal AsyncLocal TimeProvider 模式（测试可注入） | OutboxMessage.cs |
| 幂等读路径分叉 | 实现差异声明：InMemory 读时清理 vs EFCore 后台 GC（对外语义一致） | InMemoryIdempotencyStore.cs |
| Saga 跨方言 SKIP LOCKED | ADR-017：维持乐观并发+冲突重试，SQLite 不支持+复杂度收益比不成立；给出重评触发条件 | docs/decisions/017-saga-lease-concurrency.md |
| **实证** ValueObject 装箱 | benchmark 实锤 24B/次 → 修复为受约束 UTF16 调用+FromUtf16 转码 → 0B/次（S3 反向验证：改前 24B、改后 0B；契约测试守住失败路径） | ValueObject.cs |
| **实证** MultiHost Port=0 | Npgsql 10.0.3 实测 Port=0 赋值直接抛 ArgumentOutOfRange（旧注释错误）→ 修复为备机端口编码进 Host 条目（host:port 语法实测支持） | PostgreSqlMultiHost.cs |
| **实证** ToSqliteParameter PG 绑定 | 分析定性：依赖驱动 unknown-OID 推断，PG 实测待集成测试基建（与 Pipeline 同批） | 待 PG 集成测试 |
| **实证** Pipeline RecordsAffected | 修复已按 Npgsql 文档语义落地（ITM-068），实际值待 PG 实测 | 待 PG 集成测试 |

- **需设计决策（~10 项）**：RabbitMQ 匿名队列 requeue 语义（消息随队列消失 vs DLX）、MySqlMultiHost failover 凭据/端口丢弃、PalORM/Dapper outbox status 编码互斥（int vs string）、PalORM 审计字段持久化（已声明 gap）、EventStreamJsonLines 导入丢弃流字段（疑刻意）、EventLogReplaySource 单类型流约定、FanOutStep 可空结果过滤、OutboxMessage TimeProvider 双轨、InMemoryIdempotencyStore 读路径删除分叉、Saga 跨方言 SKIP LOCKED（需 ADR）。
- **需环境实证（~4 项）**：ValueObject 接口模式匹配装箱（benchmark 实证）、ToSqliteParameter 跨方言 PG 绑定（PG 实测）、PostgreSqlPipeline RecordsAffected 实际值（PG 实测）、MultiHost Port=0 语义（Npgsql 文档核实）。
- **P3 顺手修清单**：TimeProvider 硬编码 ×4、TryAdd 静默 ×4、DapperOutbox/Inbox ct 缺传（CommandDefinition）×2、其余注释漂移 ×5、CheckpointRow 死代码（须按框架库五步验证后处置）。


---

## 第四轮（同日晚）— 第二次全量复审修复（第二轮评审发现）

### [x] 已修复（第四轮，约 40 项）

**P1×3**：SagaCompleted 指标 !=→==（FanOut/ChildSaga 两处）；MemoryPack TryAddSingleton→AddSingleton（兑现"替换"文档语义）；幂等终态 MarkX×2 实现补 affected 守卫（PalORM Idempotency + PalORM ProjectionCheckpoint——PD17 姊妹同步）。

**修复覆盖残留×3（PD17 首案）**：HandleEventAsync 补 DispatchKind 守卫（ITM-069 只修了 ProcessEventAsync）；Dapper EventLog 审计 4 列补齐（INSERT 参数硬编码 null 的真实缺陷比初审更深——actor/reason 也从未持久化）；PalORM ProjectionCheckpoint MarkX affected 守卫。

**探针实证升级×1**：sharedCache 连接串裸形式 SQLite Error 14 打不开（file-based app 实证）→ file::memory:?cache=shared URI 形式。

**取消语义×6**：Idempotency/Inbox/Projection 三处理器 MarkFailed 改 CancellationToken.None；DapperInbox Mark×2 异步化+真传 ct；DapperOutbox 4 处 CommandDefinition ct；DapperBulkCopy 全链 ct。

**并发守卫×5**：PalORM Outbox ReleaseForRetry 租约守卫（先捕获 owner 再置空）；OutboxNotifier gate 泄漏+Dispose 竞态；DapperSaga INSERT TOCTOU 转语义化异常；RabbitMQ ACK 安全包装；MySql Obsolete 路径 Singleton→Scoped。

**安全×3**：解压炸弹守卫×6（System+Native 全部解压器 8MB 上限）；Sharding DDL 标识符白名单；SoftDelete Escape 补包裹（测试断言同步全引号形态）。

**时钟双轨清零×5**：LoggingBehavior/DapperSaga/InMemorySaga/SagaState/SagaStateDbContext 全部可注入；G17 附注正则精化（虚方法默认值豁免）→ 零残留。

**API/生成器×5**：PalORM 三方言 DbOptions 生产入口；EnumGenerator PALENUM002 隔层继承诊断（对称 PALENUM001）；Endpoint 畸形 JSON→400；MySqlPerformanceOptimizer 幂等开连接；SequentialAccess+GetValues 矛盾消除。

**文档契约×4**：OpenZL 前向兼容声明；DapperEventLog 事务契约声明；InMemory position 语义声明；LoggingBehavior/SqliteSCE 注释漂移修复。

**测试同步×3**：Integration 测试 schema 补审计列；SoftDelete 断言全引号；PublicApiSnapshot 刷新（PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1）。

### 第四轮教训

- PublicApiSnapshot 是 API 变更的第一道反馈（ctor 加参立即红）——刷新后必须检视 diff 确认变更是有意的。
- 测试 schema 与生产 DDL 的同步是审计列变更的必查项（DapperStoreTests 自带 CREATE TABLE）。

### [x] 第五轮（同日末）— 遗留清零（PD18 + 6 项）

| 修复 | 落点 |
|---|---|
| PD18 入库：PalORM FormattableString 字符串变量插值被参数化（AND (@p) 恒假）——SQL 片段必须字面量分支写死（ReleaseForRetry 探针三连定位的根因） | .ai/review/known-false-positives.md + V7 口径 18 |
| PalOrmSagaStateStore 乐观锁快照：expectedVersion 改用 state.Version（内存快照）而非 existing.Version（DB 最新）——修复"加载后被他人改过"窗口检测失效（与 EFCore concurrency token 对齐） | PalOrmSagaStateStore.cs |
| EnumGenerator 嵌套类型：镜像 IdentityGenerator 的 ContainingType 链包裹修复（零嵌套输出不变）+ PALENUM002 哨兵携带链参数 | EnumGenerator.cs |
| SagaProcessor 单条失败隔离：foreach 包 try-catch，失败 Saga 记录后继续（租约靠 LeaseDuration 兜底），OCE 仍上抛 | SagaProcessor.cs |
| IdentityGenerator 脆弱匹配：ToDisplayString 去 global:: 前缀后匹配（P3） | IdentityGenerator.cs |
| Dispatcher unbox null 守卫：handler 返回 null 且 TResponse 值类型时抛明确异常而非 NRE（2 处） | Dispatcher.cs |
