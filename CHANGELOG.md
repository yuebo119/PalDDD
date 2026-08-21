# Pal.DDD 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。

> **当前版本**：`VersionPrefix=1.1.0` / `VersionSuffix=`（空——见 `Directory.Build.props`）
> **发布状态**：**1.1.0 已发布**（2026-07-31 推送 NuGet.org + tag `v1.1.0`→`b4d532f`；本节为事后回填——发布时 CHANGELOG 尚未建立）。tag 之后的所有变更见 `[Unreleased]`。
> **发布规范**：见 [`docs/release.md`](docs/release.md)

---

## [Unreleased]

### 修复（三十八轮：AI 质量系统全面运行 + 198 文件地毯式审计清偿）

- **🔴 P1 MySQL 幂等回归根治（四处姊妹）**：三十七轮 A1 的 `ON DUPLICATE KEY UPDATE` 模式存在幂等破口——`SELECT LAST_INSERT_ID()` 恒返回一行非 NULL（MySQL 官方语义）+ MySqlConnector 默认 `UseAffectedRows=false` 报告 found rows，冲突被误判为新插入伪造 Processing 记录。Dapper Inbox/Checkpoint + PalORM Inbox/Checkpoint 统一改为普通 INSERT + `IsUniqueConstraintViolation` 异常捕获（对齐 IdempotencyStore ITM-228 已验证模式）；tech-debt 门禁 #13 同步识别第三种合法守卫形态
- **Inbox Mark 系列抢占 token fencing（ITM-210 姊妹·四实现对齐）**：Dapper/PalORM 补 `processing_started_at` token 守卫——超时抢占后旧 worker 不再覆盖新 worker 的行；EFCore 已有 `IsConcurrencyToken`、InMemory 已有 successor 守卫（核查确认）
- **BulkCopy 事务贯通三方言**：`BulkInsertAsync` 新增可选 `transaction` 参数——MySQL 显式挂接（原未挂接在 UnitOfWork 内直接抛 InvalidOperationException）、SQLite 挂接外部事务不 Commit、PG COPY 自动入连接事务（契约显式化）
- **EventLog 冲突误分类修复**：唯一约束冲突判定对 `ExpectedStreamVersion.Any/StreamExists` 失效（Matches 恒真）——改为批内 EventId 重复 + 表中 EventId 存在性精确判定
- **ChangeTracker 异常路径清理**：EventLog/Idempotency EFCore 全部失败路径 Detach 涉事实体——长生命周期 DbContext 幽灵租约/幽灵批次不再污染后续 SaveChanges
- **RabbitMQ prefetch 上限**：`BasicQosAsync(prefetchCount)` 默认 10 可配——manual-ack 下防 broker 无界推送
- **IterativeDomainEventDispatcher 入口 fail-fast**：初始批量超 MaxIterations 时拒绝派发（原先派发 N 个再抛异常致部分副作用后整体报失败）
- **Kafka DisposeAsync Flush**：关停前排空 in-flight 消息（5 秒有限超时 + 剩余 Warning）；tombstone（null value）消息走专门分支不再进反序列化异常路径
- **ValidationBehavior default 防御**：用户验证器 `return default` 时 Errors 为 default(ImmutableArray) 不再 NRE（对齐 PalValidationException 同款防御）
- **Saga 观察者防护补全（ITM-212 四路对称）**：OnStepStarted/OnStepFailed 纳入 SafeObserve 隔离——Sink 异常不再使步骤未执行即失败或遮蔽原始异常
- **IdempotencyProcessor 毒载荷降级**：缓存命中反序列化失败降级 Skipped + Activity 留痕，不再永久阻塞该 key
- **SqliteOutboxDbContext leaseDuration 守卫补齐**（三姊妹漏网项）；类头"RetryCount 兜底"失真声明修正为如实描述
- **BackoffPolicy 抖动整型溢出 clamp**；EventLogReplaySource 计数移 yield 前（早退少计一条）
- **FTS 触发器名保留下划线**（outbox-messages/outbox_messages 清洗碰撞致第二张表索引停更）；PostgreSqlNotifier 未请求取消的 OCE 不再逃逸 StopHost
- **CheckpointRow.LeaseUntil nullable 物化**（NULL 行防御性容错）；ProjectionCheckpointDbContext 读路径 AsNoTracking + 写回分支 Detach（变更追踪无界增长）

### 文档与口径

- **IMessageBroker 契约修正**：RabbitMQ 描述改为与实现一致的 at-most-once（原声明 at-least-once 与 durable queue 失真）；新增顺序性声明（分区键=每消息 Ulid 无序）与多订阅者语义分叉声明（RabbitMQ 广播 vs Kafka 负载均衡）
- **Hi/Lo 倒挂-跳过风险声明**：GlobalPosition 分配序与提交序可倒挂，检查点消费需追加方提交延迟相近或改用 ReadStreamAsync
- **OutboxDomainEventInterceptor 已知窗口声明**：SaveChanges 成功 + Commit 失败 + 同 scope 重试场景事件不重产（行为重构有双行风险，声明不修）
- **EndpointExtensions 滥用控制声明**：端点默认无限流/请求体约束，生产须宿主层配置
- **死常量标注**（OutboxLeaseUpdate/OutboxSelectById 无内部引用保留供外部消费方）；SagaInsert version 隐式对齐声明；Dapper Saga 两步租约同 tick 回读限制声明（对齐 ITM-109 格式）
- **EnumGenerator hint 拼接拉齐 IdentityGenerator "+" 方案**（消除病态命名 AddSource 碰撞）
- **DapperStoreTests 方言参数化基础**：`PALDDD_TEST_DAPPER_DB` 环境变量注入（缺省 Sqlite），CI 接入属后续任务
- **MySqlStores.cs 过时注释勘正**（INSERT IGNORE → 普通 INSERT + 异常捕获）

### 新增

- **统一质量体系 v2.0（三层一面）**：生成面/检测-修复面/元面 + 确定性>概率性统一原则，三源融合（实证数据 + 文献 + 控制论）
- **编码门禁 `encoding-gate.sh`（E1-E4）**：CRLF/BOM/mojibake 指纹（28 字符）/verified LF 全文件扫描；E1 增本地回退消除 .ai gitignore 盲区
- **姊妹防线 `sibling-map.sh`**：接口→实现族传递闭包枚举（16 族）+ 语义孪生轴，防"修一处漏姊妹"
- **Flaky 门禁 `flaky-gate.sh`**：重跑式检测（环境隔离 + skipped 分类 + 零报告=FAIL 守卫）
- **修复编排 `fix-orchestrator.sh`**：修复轮三步协议（姊妹联动 + 修复门 s≤p' + 回归清单）
- **方言探针 `dialect-probe.sh` CI 化**：PG17 + MySQL8.4 服务容器、路径触发、40 断言、红绿四态验证
- **CI 失败自诊断 `ci-failed-tests.py`**：三通道 `::error` 注解（失败测试名 + 日志尾 + Verify 快照首差异），公开 API 可读免认证
- **任务进件模板（第 9 个 AI 模板）**：中高复杂度任务强制验收断言 + 拒绝路径
- **`docs/testing.md`**：测试体系完整规范（金字塔/场景矩阵/BenchmarkDotNet 配置/统计判据/CI 触发规则）
- **`docs/release.md`**：NuGet 发布规范 SOP（版本管理/包范围/分支流程/回滚）
- **`docs/pitfalls.md`**：DDD 适用踩坑目录（66 条）
- **`CHANGELOG.md`**：本文件，从无到有建立变更日志规范

### 修复

- **Outbox 租约 token fencing**：`(LockedBy, LockedUntil)` 对完整匹配拒绝旧 worker（`LockedUntil` 单调变化免 DDL），消除租约释放后旧 worker 复活缺口
- **Saga 中断态超时兜底**：扫描集扩 `AwaitingHumanDecision`（HITL 中断态不再逃逸超时检测）；DynamicStep 路径补 `SafeObserveCompletedAsync` 四路对称
- **管道行为闭合泛型重载**：`AddPalPipelineBehaviors<TRequest,TResponse>()`——开放泛型在 AOT 下值类型响应抛异常，闭合版编译期实例化
- **INSERT IGNORE 四处姊妹收口**：PalORM Checkpoint/Inbox + Dapper SqlTemplates/DapperCheckpoint → `ON DUPLICATE KEY UPDATE`（MySQL 静默错误降级根治）
- **SQLite JSON 转义拆分**：`EscapeJsonPathSegment`（路径位 fail-fast `.`/`"`）与 `EscapeSqlLiteral`（值位引号倍增）分离——P1 回归根治
- **BulkCopy 11 类型显式映射**：bytea/int/long/string/bool/uuid/double/float/smallint/timestamp（byte[] 不再被 string 列 ToString）
- **MySQL DDL 列长对齐 EFCore**：Reason 2048 / StreamName 512 / TraceState 512；`event_id` 唯一索引四 DDL 补齐
- **PostgreSqlSharding.DisposeAsync 逐 shard 异常隔离**；PalOrmUnitOfWork.RollbackAsync try/finally 对齐 ITM-131
- **EventStreamJsonLines 分块行读**（8KB 缓冲）替代 ReadLineAsync 防 OOM；JSONL 单行 16MB 上限
- **IdempotencyStore 过期回收补 `status<>Completed` 守卫**；幂等策略倒挂 Processor 入口快速失败
- **PG JSONB 路径构建期守卫**（逗号/花括号 fail-fast）；Saga JsonTypeInfo fail-fast（无 jsonTypeInfo 抛异常防 saga_data 静默丢失）
- **RabbitMQ `mandatory:true`** 无路由消息抛异常不静默丢弃；CI 认证根因修复（Testcontainers 专用账号）
- **mojibake 全文法根治**（全仓 .cs 清零，28 字符指纹复检零残余）；`*.sh`/`*.py` 强制 LF（仓库级 eol=crlf 曾杀死 CI Linux bash）
- **库代码 179 处 `await` 全量补 `ConfigureAwait(false)`**；Native 解压 OOM 转 `InvalidDataException`
- **Entity.Id/EventId/OccurredOn get-only**（构造后身份不可覆盖）；Hi/Lo 游标事务感知（活动事务不发布内存缓存防回滚分叉）
- **PDDD009/010/011 解绑 BoundedContext** + CodeFix 版本后缀替换不叠加；EnumGenerator 过滤非 TSelf 字段

### 文档与口径

- 三轮全仓地毯评审（35-37 轮，513-516 文件 / 78K+ 行逐行）：修复缺陷率 31% → 8% 收敛
- README/README.en/架构/教程/性能/AOT 计数全面对齐（897+ 本地测试 + 41 Testcontainers CI）
- ADR-004/006/011/013 签名与年份勘正；SmartEnum 双注册口径勘正（行为不变）

---

## [1.1.0] — 2026-07-31

> 首个正式版（NuGet.org 已发布）。35 个 PalDDD 打包项目 + 5 个 PalORM 依赖包（5.1.0）。

### 核心能力（自 preview.1 起）

- **PalORM 适配层完整落地（步骤 1-10/10）**：核心骨架 → Row DTO + 转换器 → 4 Store（Outbox/Inbox/EventLog/Saga）→ Projection/Idempotency/UoW → SQLite/PostgreSQL/MySQL 三方言 → **PalOrmSample `PublishAot=true` 真实 AOT 发布验证通过**
- **PalORM 5.0.0 → 5.1.0**；跨方言集成测试（7 Store × 3 方言 + 9 Outbox 跨方言）+ 6 个真并发测试
- **发布链**：1.0.0 正式版 → 1.1.0 正式版发布到 NuGet.org（Base/Extension/Analyzers/CodeFixes/SourceGen）
- **许可证**：MIT → **AGPL-3.0-or-later**
- **安全**：移除硬编码数据库连接串（改环境变量）
- **修复**：LZ4 GetMaxCompressedLength .NET 11 栈溢出；Kafka 测试盲等待改 handler 反确认；RabbitMQ 凭证配置化
- **AI 质量系统**：.ai 目录分离为独立 git 仓库；lessons 沉淀 18 条实战规则
- **V8 ORM 设计文档系列**：106 API + 295 验证条（17 条 PalORM 实测踩坑入 pitfalls）
- **测试**：849 → 867 全绿（+17 测试）；四轮 test/ 审查修复（ITM-012..024）

---

## [1.0.0-preview.1] — 2026-07-08

> 首次预览版。面向 .NET 11 的 DDD/CQRS/Event Sourcing 基础设施框架。
> 35 个 PalDDD 打包项目（PalDDD.Prompts 非包，`IsPackable=false`）+ 5 个 PalORM 依赖包单列，覆盖 Entity/AggregateRoot/DomainEvent/Saga/Outbox/Inbox/Projection/EventLog 完整 DDD 战术模式。

### 核心能力

- **DDD 战术模式**：Entity\<TId\> / AggregateRoot\<TId\> / ValueObject\<T\> / DomainEvent / SmartEnum / Specification
- **CQRS**：CommandHandler / QueryHandler / PipelineStateMachine（零分配快速路径，~40B/请求）
- **Event Sourcing**：IEventLog / RecordedEvent（双构造路径，写入防御拷贝 + 读取零拷贝）
- **Saga 编排**：补偿链 + 租约锁（防多实例重复补偿）+ 超时检测（有界批量扫描）
- **Outbox 模式**：原子租约（PG FOR UPDATE SKIP LOCKED / SQL Server UPDLOCK+READPAST）+ 死信重投递（RequeueDeadAsync）
- **Inbox 幂等**：UNIQUE(message_id) 约束 + PG ON CONFLICT RETURNING 单语句
- **Projection**：断点续传 + EventLog 投影源
- **多 Broker**：IMessageBroker 抽象 + InMemory/Kafka/RabbitMQ 三实现（对称行为）
- **双持久化**：Dapper（声明层 AOT 兼容、运行时反射，见 aot.md）+ EF Core（功能完整）
- **序列化**：JsonMessageSerializer（ThreadStatic 池化）+ MemoryPack 适配 + MessageEvolutionPipeline
- **战略 DDD 编译期治理**：PDDD001-015（15 条 Roslyn 分析器规则）+ 4 个 CodeFix

### 包清单（35 个 PalDDD 打包项目；PalDDD.Prompts 非包，另 5 个 PalORM 依赖包单列）

| 层 | 包 | 数量 |
|----|----|:----:|
| Domain | PalDDD.Core / Core.SourceGen / Analyzers / Analyzers.CodeFixes | 4 |
| App-Abstractions | PalDDD.Serialization / Messaging / Compression / Compression.Native | 4 |
| App-Core | PalDDD.CQRS / EventLog / Transactions / Idempotency / Projections | 5 |
| Infra-PalORM | PalDDD.PalORM / PalORM.Sqlite / PalORM.PostgreSql / PalORM.MySql | 4 |
| Infra-Dapper | PalDDD.Dapper / Dapper.PostgreSql / Dapper.MySql / Dapper.Sqlite | 4 |
| Infra-EFCore | PalDDD.EventLog.EFCore / Idempotency.EFCore / Projections.EFCore / Repository.EFCore / Transactions.EFCore | 5 |
| Infra-Serialization | PalDDD.Projections.EventLog / Serialization.Evolution / Serialization.MemoryPack | 3 |
| Infra-Messaging | PalDDD.Messaging.Kafka / Messaging.RabbitMQ | 2 |
| Hosting | PalDDD.Hosting.AspNetCore / DependencyInjection | 2 |
| Metapackages | PalDDD.Base / Extension（Prompts 非包，`IsPackable=false`） | 2 |

> PalORM 依赖包 5 个单列：PalORM.Core / PalORM.SourceGen / PalORM.PostgreSql / PalORM.MySql / PalORM.Sqlite（版本见 `Directory.Packages.props`）。

### 工程基线

- **AOT 分层**：核心层 7 项目 `IsAotCompatible=true`（Core/Serialization/CQRS/EventLog/Idempotency/Projections/Messaging），适配器层 14 项目显式 `false`（EF Core/Kafka/RabbitMQ/MemoryPack/Transactions 等）
- **零反射红线**：MakeGenericType/Activator.CreateInstance/Assembly.GetTypes/Type.GetType(string) 全禁（ArchitectureBoundaryTests 33 方法机械守护）
- **测试框架**：TUnit 1.65.0 + MTP（禁 Microsoft.NET.Test.Sdk）
- **质量保障**：TreatWarningsAsErrors + AnalysisLevel=latest-all + 21 条 NoWarn 逐条 Justification + MTP 原生 --coverage 覆盖率门禁 + assertion-strength-check 断言强度门禁（替代 Stryker：Stryker 不支持 TUnit/MTP）
- **规范文档**：conventions.md（1000 行 14 章）+ architecture.md（18 决策）+ 17 ADR + aot.md + performance.md + tutorial.md

### 已知限制

- **BenchmarkDotNet 0.15.8 不支持 .NET 11 Preview**：正式 BDN 报告不可生成，用 `--smoke` 模式（100 万次迭代手动计时）作为快速回归
- **`PalDDD.Transactions` 项目非 AOT 兼容**：Saga 子系统用 MakeGenericMethod/Activator（已带 [RequiresDynamicCode] 标注），主动声明 IsAotCompatible=false
- **Inbox SQLite TOCTOU 弱保证**：SQLite Inbox 用 INSERT OR IGNORE + SELECT 两步有极小竞态，生产推荐 PostgreSQL
- **`PalDDD.Core.SourceGen` 待修复**：`ISpecification.cs:218` 的 `_expression.Compile()` 违反 AOT 红线（gate-check PDDD-G8 已发现）
- **`Idempotency/Projections` 部分文件 ConfigureAwait 缺失**：12 处违规（gate-check PDDD-G12 已发现）

---

## 版本号约定

| 版本段 | 何时升级 | 示例 |
|--------|---------|------|
| Major（1.x.x） | 破坏性 API 变更 | 1.0.0 → 2.0.0 |
| Minor（x.1.x） | 新增功能、向后兼容 | 1.0.0 → 1.1.0 |
| Patch（x.x.1） | bug 修复 | 1.0.0 → 1.0.1 |
| Preview（VersionSuffix） | 预发布 | preview.1 → preview.2 |

详见 [`docs/release.md`](docs/release.md) §1.2 版本号语义。

---

## 维护规则

1. **未发布版本放 `[Unreleased]` 段**：所有未发布变更先追加到此处，发布时改为版本号。
2. **变更分类**：`### 新增` / `### 变更` / `### 修复` / `### 移除` / `### 破坏性变更` / `### 安全`（[Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范）。
3. **每条带 PR 或 commit 引用**：可追溯。
4. **发布时同步**：升版本同一次提交内同步 `Directory.Build.props` + README badge + tag + 本文件。
5. **GitHub Release body 来自本文件**：release.yml 自动读取对应版本段落作为 Release 说明。
