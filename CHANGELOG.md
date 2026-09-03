# Pal.DDD 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。
日志格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范（完整规则见 [`docs/release.md`](docs/release.md) §十一）：
**消费者可见变更在上**（Added/Changed/Deprecated/Removed/Fixed/Security + 本项目扩展 Dependencies/Documentation/Tests），**工程过程叙事入附录**；数字必须可验证；`[Unreleased]` 与发布段**同次提交转正、先于 tag**。

> **当前版本**：`VersionPrefix=2.1.0` / `VersionSuffix=`（空——见 `Directory.Build.props`）
> **发布状态**：**2.1.0 已发布**（2026-09-04 tag `v2.1.0`）；2.0.0 已于 2026-08-23 发布（tag `v2.0.0`→`a115c22`——发布时 CHANGELOG 的 `[Unreleased]` 未转正为 `[2.0.0]` 段，该段内容已并入 `[2.1.0]`，与 1.1.0 同款教训第二次，见 §九 教训 2）；1.1.0 已于 2026-07-31 发布（tag `v1.1.0`→`b4d532f`，事后回填）。tag 之后的所有变更见 `[Unreleased]`。
> **发布规范**：见 [`docs/release.md`](docs/release.md)

---

## [Unreleased]

（暂无）

## [2.1.0] — 2026-09-04

> **范围**：`v2.0.0`（2026-08-23，commit a115c22）→ `v2.1.0`（commit 0370c30），106 个提交（main/dev 同点发布）。
> **兼容性**：SemVer Minor——向后兼容，无破坏性 API 变更；3 个零消费公共 API 预告废弃（v3.0 移除）。
> **组织方式**：分两层——上方按 Keep a Changelog 分类给出**消费者可见变更**；文末附录保留**逐轮工程过程记录**（内部评审叙事）。规范见 [`docs/release.md`](docs/release.md) §十一，逐轮明细真源 `.ai/review/metrics.md`。

### Added 新增

- **`Core.FailureReason` 共享类型**：`Normalize`（代理对完整防御——截断不落孤立高代理）+ `Truncate`（UTF-16 安全截断）——替换五栈各自为政的裸切片截断
- **租约 token fencing（守卫语义，三栈统一）**：Outbox `MarkProcessed`/`MarkDead`/`ReleaseForRetry` 持租路径补 `(locked_by, locked_until)` 双 token 守卫 + `retry_count` 乐观锁——租约被重租后旧 worker 终态写被拒（affected=0 零内存变异）；幂等存储补 `Revision` CAS 令牌（防 Completed 翻转后副作用重执行）
- **Saga API 扩展**：`Saga<TState>` 新增 `Input` 属性与 `ctor(object input)`（子类携带输入载荷）；`SagaState.InvalidateInterrupted(sagaId)`（HITL 中断失效集）
- **可观测性注入点**：`IdempotencyProcessor`/`ProjectionProcessor` 构造新增 `IPalLogger<T>` 可选重载；`AddPalLogging`（`clearProviders`/`minimumLevel` 参数化）
- **分析器与 CodeFix**：新增 PALID007（幂等 Revision 令牌）/PALENUM009 诊断；CodeFix 拆分为四个独立 provider（AddBoundedContextPrefix/AddProjectionContextPrefix/AddVersionSuffix/MatchEventName，一个诊断一文件惯例）
- **闭合泛型管线重载**：`AddPalPipelineBehaviors<TRequest,TResponse>()`——开放泛型在 AOT 下值类型响应抛异常的根治
- **质量工程脚本**（`.ai/scripts/`，挂 CI）：`template-gate.sh`（AI 模板编译探针机械门禁）/`encoding-gate.sh`（CRLF/BOM/mojibake 指纹）/`sibling-map.sh`（接口→实现族传递闭包）/`flaky-gate.sh`/`fix-orchestrator.sh`；`ci-failed-tests.py`（CI 失败三通道 `::error` 注解）

### Changed 变更

- **组织重组（零破坏，命名空间不变）**：PalDDD.Transactions 30 文件平铺 → Saga/（18）+ Outbox/（5）+ Inbox/（3）+ 根共享（5）；Analyzers 738 行单文件拆 5 文件（纯搬运，36 测试零回归）
- **异常口径统一**：解压超限（System 三算法）统一 `InvalidDataException`；畸形 JSON 400 补 ProblemDetails body（三处 400 形态收口）
- **BulkCopy 11 类型显式映射**（byte[] 不再被 string 列 ToString）；MySQL INSERT IGNORE → `ON DUPLICATE KEY UPDATE`（静默错误降级根治，四处姊妹收口）
- **默认质量**：库代码 179 处 `await` 全量补 `ConfigureAwait(false)`；mojibake 全仓清零；`IdentityGenerator.IsNumeric` 改 `SpecialType` 判定（extern alias 场景不再静默丢生成）

### Deprecated 废弃（`[Obsolete(error: false)]`，v3.0 移除预告）

- `AggregateNameAttribute` / `DomainCapabilityAttribute`——零消费（SourceGen/Analyzer 均不读取，原 doc"供 SourceGen 使用"失实）
- `SqlServerOutboxDbContext`——零测试覆盖的未验证方言基类

### Fixed 修复

- **P1 级（全部探针红→绿实证）**：PalOrmOutboxStore 租约 token 谓词被 SQL 行内注释吞（v72 引入 v74 闭环——同 owner 重租旧写放行）；Kafka 非关停 OCE 假修勘正（catch 锚定 while 外物理无法 continue，移入循环体）；interface 嵌套三元漏腿（生成物必炸）；模板区 P1×8（DDL 错位/prompt 破损/CI no-op/README 漏同步/探针假绿路径×3/template-gate 前存量）；PDDD015 显式接口实现假报（analyzer 生产缺陷）；NuGet env 作用域（CI）
- **并发/租约族**：EF 幽灵租约（瞬时异常 Detach 收口，Idempotency 样板推广五 DbContext）；Saga 终态吞决策（可见失败 + 失效集 + TOCTOU double-check 三层递进）；RabbitMQ ct 参与消费生命周期（linked-CTS + BasicCancelAsync 解绑）；`RabbitMqBroker.DisposeAsync` 幂等门
- **连接串族**：IPv6 四象限定稿（方括号纯 host/方括号+端口放行、裸 IPv6 fail-fast、`']'` 后三类畸形后缀三入口拦截）；MySQL 内嵌端口零容忍（MySqlConnector 2.6.2 源码四点实证）；LoadBalance 显式冲突 fail-fast；空条目/查重守卫五层
- **数据正确性**：SQLite JSON 转义拆分（路径位 fail-fast 与值位引号倍增分离）；PG JSONB 42804；Saga JsonTypeInfo fail-fast（防 saga_data 静默丢失）；PalOrmIdempotencyStore 过期 Completed 记录修正为可回收重建
- **API 语义**：`AddPalOutbox` 补 `RetryBackoffPolicy` 非空校验（原置 null 时逐 tick NRE、消息滞留）；`Saga` 补 `SafeObserveCompensationStartedAsync` 隔离（观察者异常不阻断补偿）；ValidationBehavior default 验证器静默放行修正

### Dependencies 依赖

- PalORM 5.3.0 → 5.4.0（弹性层 DI 接入：三 Provider 扩展 `configureResilience` 回调）
- TUnit 1.65.38 → 1.65.68（TUnit.FsCheck 同步）

### Documentation 文档

- 新增 4 篇 ADR：018（DomainEvent Ambient TimeProvider）/ 019（IUnitOfWork 归属 Core）/ 020（三栈并行与 Dapper 退役路线）/ 021（四处理器管线不做基类提取）
- 新增 docs/testing.md（测试体系规范）/ docs/release.md（发布 SOP）/ docs/pitfalls.md（66 条踩坑）
- README/README.en 全面口径对齐（包表 2.1.0 / 测试计数 / 轮次计数）

### Tests 测试

- 16 项目面板：v2.0.0 基线约 1053 → **v2.1.0 实测 1202**（本机 1153 通过 + 49 环境依赖项由 CI Testcontainers 权威执行）；净增约 150 个回归/锁定测试，守卫类修复均带 mutation 红测锁定（移除修复套件必须变红）

### 附录：工程过程明细（评审逐轮记录——内部叙事，非消费者变更摘要）

> v24-v74 共 51 轮评审-修复循环（全仓地毯逐行 + 探针实证 + mutation 红测锁定），累计发现与处置约 490 项（其中 v53-v74 机械账本口径 P1×17 / P2×35 / P3×189，含证伪与留档项）。逐轮明细（含证伪数、姊妹联动、mutation 记录）见 `.ai/review/metrics.md`；以下保留历史轮次的发布面叙事。

### AI 质量系统评审循环 v24-v36（2026-08-23 起十三轮全仓地毯 + 修复，commit fc6447a..v36）

> 十三轮评审-修复循环（v24 起，每轮：六片独立子代理全量逐行 + 主线程亲验 + 机械门禁）。
> 完整轮次明细见 `.ai/review/metrics.md`；本段为发布面摘要。

- **P1 级（3 项）**：SagaStateDbContext SQLite 翻译缺口（ITM-261 姊妹，v23）；Kafka 非关停 OCE 假修勘正——v34 对齐仅改日志文本，catch 锚定 while 外层物理上无法继续循环（v36 真修：移入循环体 per-message continue）；审计系统能力实证轮注入缺陷全抓（R46）
- **P2 级（14 项）**：EF 幽灵租约族（瞬时异常 Detach 收口，Idempotency 样板推广五 DbContext）；ValidationBehavior default 验证器静默放行；PDDD003 abstract / PDDD004 struct 误报；Saga 终态吞决策（可见失败 + 失效集 + TOCTOU double-check 三层递进）；RabbitMQ ct 参与消费生命周期（对齐 Kafka linked-CTS 契约）；Rabbit OCE filter 互斥缝；IdentityGenerator FormatException 三腿 + 可访问性链检查；拦截器注入自清理（EF SavingChanges 派发点在 try 前，源码级实证）；ChildSagaInputEvent 通道封死（public 化）；Kafka/Rabbit 非关停 OCE 语义对齐
- **P3 级（约 110 项）**：守卫/截断/查重/ct 传导姊妹族全栈收口（FailureReason.Truncate 共享收敛 34 点）；可观测性（20 Counter 零 tag 设计声明、高基数 Activity tag 三实现清理、pending-confirmation logger 通道）；生成器族（PALMSG006/PALENUM006/007/008、PALID006、displayType 遮蔽、可访问性链检查）；多主机族（PG/MySQL 查重/归一化/异常统一）；文档三方一致勘正约 40 处（含 "7 Store" 计数、性能契约、日志跨栈口径）
- **测试**：净增约 60 个回归测试（全部探针红→绿或修复锁定），测试总数 1053 → 约 1180
- **已知架构裁决项（v3.0 窗口，留档 `.ai/review/action-items-p3-backlog.md`）**：HITL 恢复状态落库归属（Saga-store 分离，探针实证）；ChildSagaInputEvent 泛型嵌套顶层化；ReleaseForRetry Status 守卫三栈对齐的 InMemory 侧；Dynamic 路由 Timeout 语义

### 精炼（2026-08-26 第三轮：全源码价值判定 + 组织重组）

- **零消费公共 API 废弃预告**：`AggregateNameAttribute`/`DomainCapabilityAttribute`（SourceGen/Analyzer 均不读取，原 doc"供 SourceGen 使用"失实）与 `SqlServerOutboxDbContext`（零测试覆盖的未验证方言基类）标 `[Obsolete(error: false)]` 指向 v3.0 移除；samples/bench 的装饰性标注同步移除。废弃 API 保留反射式契约测试
- **死配置清除**：PalDDD.Prompts csproj 的无效 PackagePath 打包配置（IsPackable=false 下自认死配置）
- **重复收敛**：`LeaseOwnerFactory`（Outbox/Inbox 两 Options 逐字重复的"机器名:ULID"默认值）；`MessageVersionKey`（Evolution 的 Builder/Pipeline 嵌套 Key record 双拷）
- **组织重组（零破坏，命名空间不变）**：PalDDD.Transactions 30 文件平铺 → Saga/（18）+ Outbox/（5）+ Inbox/（3）+ 根共享（5）；InboxStore.cs 三类型拆分为 InboxStatus/InboxMessage/IInboxStore 独立文件（对齐 OutboxStore 单类型组织）
- **Analyzers/CodeFixes 单文件拆分（纯搬运，逻辑与修复史注释逐字保留）**：`StrategicDddAnalyzer.cs` 738 行单文件 → 5 文件（主文件 218 行：ID/descriptor 表 + Initialize + 调度骨架；`.BoundedContext.cs` PDDD001/002；`.MessageContracts.cs` PDDD005/008/009/010/011/012/015；`.Handlers.cs` PDDD003/004/006/007/013/014；`.SymbolHelpers.cs` 符号/语法辅助）——原 `AnalyzeNamedType` 230 行圈复杂度 ~50 的调度按规则族分发。`StrategicDddCodeFixProvider.cs` 343 行 5 类型 → 5 文件（对齐一个诊断一文件的 Roslyn 惯例）。36 个分析器测试零回归锁定
- **testing.md 新增"〇、InMemory 实现族定位"**：六个 InMemory 实现刻意分散各抽象包（单元测试/原型一跳引用零依赖），不集中独立包（避免反向汇聚依赖与版本耦合）；定位为测试默认实现非生产实现（生产见 ADR-020 三栈）
- **明确不做**（裁决记录，详见本轮报告）：EFCore 五处 SQL 错误分类器不收敛（五包无共同上层，新建共享包成本>收益）；PalORM 三方言 DI 注册不提取（形状相似≠语义等价，9 泛型参数+公共 API 扩张违背 ADR-021）；Outbox 列清单/跨栈截断常量不提取（PalORM FormattableString 约束/公共 API 面成本）；Dapper 栈四处分類器不动（ADR-020 只修缺陷不重构）

### 修复（v8 全量评审清偿，2026-08-26）

- **P2-1** `PostgreSqlOutboxNotifier` 触发器示例 `NEW.status='Pending'` → `NEW.status=0`（int 化契约同步——旧示例照抄即建坏触发器，PG 无 integer=text 运算符）
- **P2-2** `PalOrmIdempotencyStore` 过期回收移除 `AND status<>Completed` 守卫——过期 Completed 记录从"该 key 永久拒绝"修正为可回收重建（三栈契约统一：Retention=可重新执行窗口）；新增回归测试 `TryStartAsync_ReclaimsExpiredCompletedRecord`（S3 双向验证：守卫恢复→红）
- **P2-3** `PalDDD.Analyzers.CodeFixes` 移除 `SuppressDependenciesWhenPacking`——nuspec 现声明 `PalDDD.Analyzers (>=2.0.0)` 依赖（重打包实证），独立安装时 5 个 fix provider 不再静默失效；analyzers/ 目录仍仅自身 dll（无双加载）
- **行为修正**：`FailureReason.Normalize` 代理对完整性防御（截断不落孤立高代理，+6 契约测试）；畸形 JSON 400 补 ProblemDetails body（对齐 ITM-283 验证 400 形态）；`IdentityGenerator.IsNumeric` 改 `SpecialType` 判定（extern alias 场景不再静默丢生成）；Outbox MarkDead 构造串过 Normalize 一致化
- **工具**：dialect-probe 单方言连接失败降级为记 fail（原 Unhandled 崩溃连带另一方言探针未跑，2026-08-26 PG 超时实证）
- **文档/注释修正 ×12**：int 化同步残留（SqlTemplates remarks/BulkCopy IL2062/AmbientTransaction DI 口径）、LeaseOwnerFactory 管线名勘正、"零 GC"过度声明收敛、Saga 重试整步重放语义成文、PALID002 补 readonly 提示、补偿告警双路径声明、Any 占位歧义、双时钟源/DapperEventLog auto-open/Kafka Dispose 超时声明
- **不修（裁决）**：MarkCompleted 无 status 守卫（姊妹对齐行为）、开放/闭合 behavior 互斥（设计权衡已声明）、Inbox 截断跨栈分叉（各有列契约）、EventLogPositionReserver 慢路径（正确性无影响）、AddMessagesAsync 无 ct（v3.0 接口窗口，已注释声明）

### 修复（v9 验证轮清偿，2026-08-26）

- **P1-1** `DapperEventLog` 恢复 `: IEventLog` 接口声明——上轮行内注释注入事故把接口声明吞进注释行尾（build/测试/快照三重拦截缺口均未拦，v9 敌对复核抓出）；注释移入 XML doc；**新增接口赋值契约测试**（`DapperEventLog_ImplementsIEventLog_InterfaceAssignmentCompiles`——声明丢失即 CS0029 编译失败，S3 双向验证：删→红/恢复→绿）
- **P2-1** `Saga` 补 `SafeObserveCompensationStartedAsync` 隔离（对齐 ITM-212 的 SafeObserve 族姊妹补全）——观察者 Sink 抛异常不再阻断真实补偿、不再误报"补偿失败"；回归测试 `ObserverSinkFailure_DoesNotBlockCompensation`（S3：直 await 版 10 红）
- **P2-2 + 注释×6**：SqlTemplates SagaActive int 化第四处残留（"字符串状态"勘正）、幻影 StatusPending 引用勘正、BulkCopy 不存在的 `[DapperAot(false)]` 标注措辞、SqliteExtensions `[module:DapperAot]` 失实勘正、GenerateIdAttribute doc 三处补 readonly（对齐 PALID002）、CodeFixes "5 个 provider"计数勘正（4 provider+1 helper）
- **行为×3**：`AddPalOutbox` 补 `RetryBackoffPolicy` 非空校验（原显式置 null 时 catch 块内 NRE 逐 tick 中止、已租约消息滞留）；`RabbitMqBroker.DisposeAsync` 补幂等门（对齐 Kafka ITM-217）；`MapCommand`/`MapQuery` 的空 body 裸 400 补 ProblemDetails（三处 400 形态统一收口，E1）
- **测试×1**：`CreateInvalidBody` 工厂契约测试（v8 引入时零覆盖，E6）
- **措辞×3**：SagaStep 重放语义 remarks 挪类头（原挂 FanOut/ChildSaga 恒 null 的 ExecuteAsync 上，挂靠错位）；Any 占位注意点改为面向框架维护者（Kind 为 internal 外部不可达）；FromHeaders 未知类型头行为澄清（部分上下文非全 null）

### 修复（v10 验证轮清偿，2026-08-26）

- **P2-1 readonly 失实链改口**：`GenerateIdAttribute` doc 与 PALID002 诊断消息勘正——readonly **可省略**（C# 规则：readonly 出现在任一 partial 部分即整体 readonly，编译探针+主线程双实证）；真实约束是用户部分不得声明非 readonly 实例字段（CS8340）。v8 引入→v9 强化→v10 传播的未验证语言规则断言链终结
- **幽灵 Added 三处补 Detach**（姊妹对齐）：`InboxDbContext`/`EventLogPositionReserver`/`ProjectionCheckpointDbContext` 在瞬时 DbUpdateException（非唯一冲突）上抛前 Detach——对齐 EventLogDbContext 三十八轮形态，长驻 DbContext 重试不再 identity conflict
- **幻影/措辞勘正×8**：`[DapperAot(false)]` 幻影标注三处（测试文件）、契约测试错码 CS0311→CS0029、Outbox/Inbox Store 头部"Dapper.AOT 拦截器"措辞勘正（与诚实声明对齐）、两处 DapperAotInitializer 行号漂移、PostgreSqlPipeline 事务限制声明（NpgsqlBatch 未挂接事务，事务内批量走 DapperBulkCopy）
- **裁决不做**：双 Broker DisposeAsync 幂等门回归测试——无 mock 框架、fake IChannel 接口面成本超收益，Interlocked 门逻辑经两轮敌对确认（传感器欠账留档）；观察项×3（死 throw 防御保留/GetAsync 跟踪设计差异/Current 重读形态）维持既有裁决

### 修复（v11 验证轮清偿，2026-08-26）

- **P2×2**（d85183f 勘正链头部残留）：`DapperOutboxStore` 头部旧"零反射"AOT 块整体重写为诚实状态（经典路径含反射/编译无警告≠运行时承诺/栈级策略指 ADR-020）；`DapperSagaStateStore` 删除与 ADR-020 裁决冲突的旧块（"建议启用 Dapper.AOT SG"）
- **P3×3**：PostgreSqlPipeline"后者有挂接"精确化（Npgsql 自动加入 vs MySQL/SQLite 显式挂接）；InboxStore ADR-020 引用改为准确技术依据（csproj IL2062/IL3058）；StrategicDddAnalyzer 主文件 unused using 删除
- v11 敌对复核全部通过：readonly 改口三探针复证闭环、幽灵 Added 三处双代理交叉确认（when 互补穷尽含 Concurrency 子类归路论证）

### 能力实证轮（v12，2026-08-26 · engine 第五轴首触发）

- **审计能力验证：注入 1/1 全抓**——LeaseOwnerFactory 固定 owner 注入（测试盲区实证：154 全绿测不出），不知情子代理 P1 定级精确命中（触发路径推演 + git diff 佐证 + 三处文档失实连带抓出）
- **顺带存量清偿 P3×2**：`Saga.OnStatusChanged`（Interrupt 路径）补异常隔离——ITM-212 族第五个观察点漏网（原 fire-and-forget 只抑制 CA2012 警告，Sink 同步抛仍逃逸）；`FanOutStep` 构造器补 selector/executor null 守卫（ITM-166 漏网姊妹，对齐 DynamicStep/ChildSagaStep）
- **注入零残留确认**：git checkout 恢复 + ULID 回位 + 154 全绿
- v12 静态前置：8cc6ab7 亲核零 P2（纯注释级变更）——触发条件达成记录

### 修复（v13 清偿，2026-08-26 · P3×14 全清）

- **卫生**：unused using×9（StrategicDddAnalyzer 主文件 2——上轮拆分继承残留；CodeFixHelpers 7）；PipelineStateMachine remarks 对齐实现（每请求 new ~40B，原文"对象池"为畅想）；Dapper SqlServer 死分支口径统一注释
- **文档失实**：docs/aot.md 补"MySql/PG 方言 AOT publish 未经验证"真实声明（三方言包 csproj 引用的缺口落档）
- **姊妹守卫×3**：`OutboxDbContext.AddMessage`（expression-bodied 改块体）+ `InMemoryOutboxStore.AddMessagesAsync` 单条 ThrowIfNull；Rabbit `AsyncSubscription` 句柄级幂等门（对齐 KafkaSubscription ITM-217）
- **异步半面**：`Saga.OnStatusChanged` 补异步故障观测——`Preserve().AsTask().ContinueWith(OnlyOnFaulted)` 记 Activity（本地函数 RecordObserverFault 同步/异步两路共用）——CAP-2 完整闭环
- **租约分叉声明**：PalORM MySQL JOIN 的 last-writer-wins vs EFCore SKIP LOCKED 取舍落注释（版本兼容矩阵 + token 终态守卫兜底）
- **fix 覆盖**：`AddProjectionContextPrefixCodeFix` 补语义基类链查找（对齐 analyzer 链式——ProjectionName 在基类时 fix 可注册）
- **裁剪对齐**：Native LZ4/ZStd/OpenZL Compress 返回 `AsSpan(0,written).ToArray()`（对齐 BrotliCompressor——消除 maxSize 超分配数组的 LoH 驻留）

### 修复（v16 清偿，2026-08-26 · P2×5 + P3 重点族）

- **P2-1** 补 OnStatusChanged 真路径回归：`ObserverInterrupt_BothFaultModes_DoNotEscape`（同步抛=true 半面/[异步抛=行为面不崩]，TestSaga 新增 PublicWhenInterrupt 辅助）——**v15 测试走错路径勘正**（其覆盖 SafeObserve 族非 ContinueWith，注释已诚实声明覆盖边界与三轮 S3 不可达实证）
- **P2-2** PG MultiHost 三入口补 `DbDataSource` 抽象双注册（对齐 MySQL ITM-113 模式——缺失时 WithStores 连接工厂解析即抛）
- **P2-3** MySqlOutboxDbContext 注释勘正（Mark* 已是 ITM-210 token 化 ExecuteUpdate 直写，原"内存突变依赖 ChangeTracker"理由失效）
- **P2-4** PDDD013 fix 的 BC 来源改读 `diagnostic.Properties`（对齐 PDDD008——analyzer 已传属性但旧版未消费；字面量定位保留基类链）
- **P2-5** DependencyInjection 项目 AOT 归属落档：csproj 声明注释 + gate G14 三态表/aot.md 补列（核心层 7→8 项目）
- **P3 重点**：EFCore GetPending batchSize 非正守卫（姊妹对称）；NativeATO 错拼/ZStd 注释复制文案勘正

### 修复（v16 P3 尾批清偿，2026-08-26）

- **DI 扩展守卫补齐×8**：AddPalDDD/AddPalIdentity/AddPalCoreStack(块体化)/AddPalFullStack(同)/AddPalPipelineBehaviors/AddPalLogging×2/AddPalCommandHandler/AddPalQueryHandler 全部补 `ThrowIfNull(services)`——此前 8 方法仅泛型 behaviors 版有
- **声明与措辞**：Legacy Router Reader "any" 读到主库的缺口注释落位；MessageConsumeContext null key 框架边界文档化；DapperOutboxStore 补下游 nextAttemptAt 批次漂移取舍声明；PALMSG003 文案精确化（"registered more than once"）；MemoryPack "AOT-safe" csproj 措辞改精确表述
- **裁决维持**：SqliteFts 私有拼接种子（无触发路径）/ Sqlite 翻页全扫（Skip 保证终止）/ PalOrmInbox 捕 Exception（when 已收窄）/ 观察项×3 维持

### 修复（v17 验证轮清偿，2026-08-26）

- **P1** `InboxDbContext.TryStartProcessingAsync` 补 consumerName/messageId 空白守卫——ITM-163 四姊妹（Dapper/InMemory/PalORM 均有）中 EFCore 唯一漏网，空串键可创建幂等行且无法命中正常消息；契约对齐其余三实现抛 ArgumentException。新增回归测试 `TryStartProcessingAsync_BlankKeys_ThrowsArgumentException`（S3 双向：删→红/恢复→绿）
- **P3** SagaTests 注释勘正×2（引用不存在测试名/UTE 机理表述——OnlyOnFaulted 延续访问 t.Exception 即 observed 不触发 UTE，AsyncThrowingSink 改 Task.Yield 真异步）；MySqlOutboxDbContext 行号锚勘正；批 now 漂移取舍声明在源头 DapperOutboxStore 落档
- **v16 敌对复核全过**：ObserverInterrupt 双故障测试两段流/通配命中/无竞态三问确认；Legacy "any"/漂移声明与事实一致

### 修复（v17 片 E 迟到报告追偿，2026-08-26）

片 E 验证轮迟到返回后主线程核实的**三项假修**（上轮脚本 NameError 中断，部分项未落地但被误报成功）：
- **P2** `AddPalLogging` 双重载守卫缺失——8 处守卫中此二方法实际无 guard，本轮补齐至 9+1=10 处（含三参主签名）
- **P2** MemoryPack csproj "AOT-safe" 措辞**声称修了实际没修**（git log -S 实证自初始提交未变更）——本轮改为精确表述
- **P2** MessageConsumeContext null key 注释未落地——本轮落地
- **P3** OpenZL 行尾注释不成句修复；ServiceRegistration 格式核查（无损伤）
- **v17 F1（片 D）已先行清偿**：CodeFix 的 BC 本地早退门控删除（Properties 唯一来源）——v16 P2-4 第二次修一半的终结

### 修复（v17 P3 收尾，2026-08-26 · 对账追偿）

- **对账追偿×4**（上轮提交声称清偿但未落地的 SagaTests/MySql 注释项，本轮 git diff 对账发现后逐一真实落地）：SagaTests:185 引用不存在测试名勘正；:203 UTE 机理论证修正（OnlyOnFaulted 延续访问 t.Exception 即 observed——原"被吞为进程级 UTE"表述错误）；:988 AsyncThrowingSink summary 更新为 Task.Yield 真异步实现；MySqlOutboxDbContext "PalORM :78-84" 行号锚勘正（实为 :98-104）
- **声明级**：InMemory RequeueDeadAsync now 锁外取值微 TOCTOU 取舍声明；Legacy Router 引用处补全两处行为差异列示（Reader any→主库/Host 静默跳过）

### 修复（v17 P3 收官批，2026-08-27）

- **InboxDbContext Mark* 补 status==Processing 守卫**（v16 P3-2 姊妹对称收口——对齐 Dapper SqlTemplates/PalORM 的 AND status=Processing：重复标记/Completed 后误标不再静默 mutation；触发需调用方序列 bug，防御深度补齐）
- **Saga SafeObserve 族边界声明**（OCE 透传语义 + 派生 RetryBackoffPolicy 无守卫取舍成文）
- **Notifier OCE 日志措辞精确化**（区分真关停与非停机请求的内部超时转换）
- **批次 now 漂移取舍在 DapperOutboxStore 源头落档**（下游 OutboxBatchProcessor 行为的架构级说明）
- 观察项维持：ContinueWith ExecutionContext/Sqlite 翻页终止保证/跨包分类器收敛受架构约束

### 修复（v17 观察项收官，2026-08-27 · 第二批）

- **OCE 滞留语义成文**：InboxProcessor 补 handler 抛 OCE 时记录滞留 Processing 至 ProcessingTimeout 的设计取舍说明（不标 Failed 的理由：OCE 语义="不知道执行到哪一步"，标 Failed 会重放可能已完成的副作用；代价=最长 Timeout 重试延迟）
- **Dispatcher 尾部 throw 论证成文**：正常语义不可达的推导链（入口 fail-fast + 每迭代必 Dequeue + Handler 不入队）保留 throw 作为未来入队行为变更的哨兵

### 修复（v18 清偿首批，2026-08-27）

- **P1 E-1** `KafkaBroker.SubscribeAsync` 泄漏修复：Dispose 后并发 Subscribe 时 `_disposed` 守卫在锁内抛出，但 consumer 已 Subscribe 却未登记进 _consumers——无人释放。catch 内就地同步释放（cts.Cancel + consumer.Dispose；consumeTask 尚未创建无 unobserved 风险）。回归测试因需 Kafka 真实 Broker 无法本地化——探针方案已入档（file-based app + Dispose 后 Subscribe 断言）
- 其余批次继续进行中

### 修复（v18 清偿收口，2026-08-27）

- **P1 E-1** KafkaBroker SubscribeAsync：Dispose 后并发 Subscribe 时守卫抛出致 consumer 泄漏——catch 内就地同步释放（cts.Cancel + consumer.Dispose；consumeTask 未创建无 unobserved 风险）
- **P2 B1** PostgreSqlOutboxNotifier.FireBatchProcessAsync OCE 分类拆分（PD24 孪生对称）：真关停静默退出 / 非关停 OCE 按 Error 记录——原单分支把两类混记 "canceled during shutdown"
- **P3 批量**：EvolutionPipeline 运行期校验统一 MessageEvolutionException（对齐构造期单点 catch 语义）+ Upgrade 末步 ClrType 哨兵（防描述符互换静默错配）；DI AddPalEventHandler 补 ThrowIfNull；DapperServiceCollectionExtensions "全 AOT 安全"旧口径勘正；三方言 PalOrmExtensions jsonTypeInfo 注释随 ITM-228 fail-fast 勘正；Legacy Router 引用处两处行为差异补全

### 修复（v19 清偿收口，2026-08-27）

- **P2-①** PG MultiHost 四处注册补 `NpgsqlDataSource` 具体型双注册（Notifier 工厂强依赖具体型——仅抽象型时 failover+notifier 组合启动解析即抛）
- **P2-②** MySqlMultiHost 补 standby 串缺 Server 的 fail-fast（原静默并入 localhost 使故障转移指向错误节点——对齐 PG 姊妹 EncodeHostEntry）
- **P2-③** PeriodicBackgroundProcessor 补 `_disposed` 标志 + ODE 终止循环守卫（不规范宿主直调 Dispose 后 WaitForNextTickAsync 持续抛 ODE 被吞形成无限异常循环烧 CPU）
- **P3 批量**：ChildSagaStep 泛型 ExtractInput 死代码删除（姊妹镜像 ExtractOutput 先例）；PalOrm Saga 并发注释"PG/SQLite 免疫"失实勘正；三方言 jsonTypeInfo 断裂注释缝合；批 now 漂移取舍补 DapperOutboxStore 源头声明；Legacy Router remark 差异列表补全；DapperServiceCollectionExtensions AOT 旧口径勘正；OutboxInsert doc UUID→TEXT/Ulid26 勘正；SafeObserve 族 OCE 边界成文；批次漂移/DI 归属等观察项维持

### 修复（v19 P3 收官，2026-08-27）

- **Saga 重入边界成文**：StepStartedAt 只写不清、重入不刷新——状态回流滞留超期触发兜底补偿为宣称语义延伸
- **Dynamic 路由未命中观测面声明**：匹配失败发生在 SafeObserveStarted 前对观察端零观测（P3-SRC-101 宽容语义的既有不对称，刻意保留）
- **IInboxStore 消费语义边界**：handler 抛 OCE 记录滞留 Processing 至 Timeout 的取舍补入接口 remarks
- **MaxRetryDelay 双默认值差异声明**（60s vs Exponential 封顶 64s——仅影响观测读数）
- **三方言 jsonTypeInfo 断裂注释缝合**：v18 勘正插入原句中间造成的句子错乱，三方言统一重写为通顺版本
- **SagaTests 注释勘正**：叠词"该路径该路径"与 :207 残缺悬句修正

### 修复（v19 B 批清偿，2026-08-27 · 第二批）

- **B3** PostgreSqlMultiHost `if (sb.Host is not null)` 恒真死分支修正——Npgsql 缺 Host 返回空串非 null（ITM-262 同包实证），改为显式 `IsNullOrWhiteSpace` 使守卫在调用点可见（真实防线原本藏在 EncodeHostEntry callee 内）
- **B4** ChildSagaStep 泛型 `ExtractInput` 死代码删除——grep 全仓零调用方（唯一分发走 IInternalChildSagaStep 接口 object 版），镜像 :75 ExtractOutput 先例（十七轮"删一半留一半"的姊妹补全）
- **B5** IsUniqueConstraintViolation 四份注释双口径统一——Inbox/Checkpoint 版原称"不含 2601/2627"与代码矛盾（代码含该分支），统一为 SagaStateStore 的"防御性保留"口径

### 修复（v20 清偿，2026-08-27）

- **C-1** EFCore Outbox 四方言 override 补 `ThrowIfNegativeOrZero(batchSize)` 守卫——基类 :46 守卫因 override 不调 base 而死码化（batchSize=0 → LIMIT 0 静默空返回）
- **B-F3** PG MultiHost Failover 路径 :63 补 `IsNullOrWhiteSpace`（v19 B3 只改了 2/3 调用点，漏 Failover）
- **B-F1** DapperInbox/Checkpoint 分类器补 SqlServer 2601/2627 分支——v19 B5 注释称含但代码无，代码侧补齐对齐 EventLog/Saga
- **B-F2** MySqlMultiHost standby fail-fast 注释机理勘正——MySqlConnector 2.6.2 缺 Server 返空串非 localhost（片 B 探针实证）
- **B-F4** MySqlPerformanceOptimizer 补 connection null 守卫×2（对齐 Sqlite ITM-165/195/220 家族）
- **E-P3-2** Kafka E-1 catch 内补 `cts.Dispose()`（linked 注册即刻回收）
- **A-P3-1** PeriodicBackgroundProcessor v19 注释机理勘正——WaitForNextTickAsync 位于 while 条件不在内层 try，其 ODE 直接终止循环（不可能无限循环）；ODE catch 分支实际守护 tick 内部 ODE
- **A-P3-2** `_disposed` 加 volatile（Dispose 线程写/循环线程读 stale 窗口收口）

### 修复（v21 B 批清偿，2026-08-27）

- **B-1** PG MultiHost Failover + ReadWriteSplit 静默跳过 standby/replica 改 fail-fast（对齐 MySQL v20 F2 / ReadWriteRouter ITM-112 姊妹——原跳过语义使无备机数据源无声注册）
- **B-2** MySqlMultiHost primary 缺 Server 前导空条目修复（镜像 PG ITM-110 规范化：primary 空则直接赋 standby）
- **B-3 勘误放弃**：ReportHelper `
`u8.ToArray()` 优化尝试——`u8` 字面量是 `ReadOnlySpan<byte>`，`WriteAsync` 收 `ReadOnlyMemory<byte>` 无隐式转换，ToArray 是必需的。P3 撤销

### 修复（v22 清偿，2026-08-27 · 全量档）

- **P1 D1** EnumGenerator 跨文件 partial 崩溃修复——`context.SemanticModel` 绑定 attribute 所在树，`GetDeclaredSymbol` 对树外节点抛 ArgumentException → CS8785 生成器崩溃。改用 `SemanticModel.Compilation.GetSemanticModel(partialDecl.SyntaxTree)` per-tree 获取。**探针先行**：CrossFilePartial 测试红（source=""=崩溃确认）→ 修复 → 绿
- **P2 C-1** PalORM 六 Store 构造补 `ThrowIfNull(session)`（ITM-281 姊妹漏网——三栈 UnitOfWork 均有，Store 层 PalORM 独缺）
- **P2 C-2** PalORM + Dapper Outbox Lease 补 `ThrowIfNullOrWhiteSpace(owner)`（EFCore 四方言 ITM-081/216 均有，两栈漏网姊妹）
- **P3 批**：D2 CodeFix WithTriviaFrom 对齐×2（ITM-221 先例）/ C-4 方言包 Saga 注释 Sqlite 方法名笔误×2 / InMemoryOutbox SaveChangesAsync 补 ct 检查（ITM-204 漏网）

### 修复（v22 P3 全量清偿，2026-08-27 · 第三批）

- **A 批**：InMemoryOutbox QueryPending 补 batchSize 守卫（ITM-204 姊妹）/ SagaCompensation switch 编译期穷尽声明（原 `_ => throw` 因枚举穷尽不可达撤销）/ retriedBy 256 vs 2040 注释差异勘正
- **B 批**：DapperInbox + DapperCheckpoint MarkFailedAsync 补 2040 截断兜底（PD24 管线截断族对齐）
- **C 批**：EFCore MySql/PG/SqlServer Lease 路径补 batchSize 守卫×3（v20 C-1 只修了 GetPending，Lease 漏网）
- **E-1** KafkaBroker `cts.Token` ODE 窗口修复——Task.Run 第二实参在启动前求值，Dispose 并发完成时抛 ODE；改用 lock 后快照 `tokenSnapshot` 传入
- **E-2** AddPalEventHandler 补 `AddPalDDD()` 自动核心注册（对齐 Command/Query P2-1 一致性）

### 修复（v23 清偿，2026-08-27）

- **P1 C1** SagaStateDbContext SQLite 翻译缺口修复（探针先行：UseSqlite 测试红→NotSupportedException 实证）——ITM-261 对 Outbox 族修复的姊妹漏网（Saga 族无 SQLite 特化，InMemory 测试掩盖）。修复镜像 SqliteOutboxDbContext：OrderBy(CreatedAt)→OrderBy(SagaId)（ULID 字典序=创建序）+ Lease 的 LeasedUntil<=now 改物化后内存过滤。排序语义变化（CreatedAt 时间戳→ULID 生成序）在关联测试断言中同步声明
- **C1 探针测试**：`SQLiteProvider_DateTimeOffsetOrderByAndLeaseComparison_Translates` 通过 Store 方法验证修复（Integration 196=+1）
- **E-2** AddPalEventHandler 重复 ThrowIfNull 删除（v22 补注册时残留）
- **v22 E-2 修复对账**：确认 AddPalDDD() 调用在位

### 决策（维护者裁决 2026-08-26）

- **ADR-020 正式采纳**：Dapper 栈退役时点定为 v3.0 `[Obsolete]` / v4.0 移除五包；终态双栈（PalORM AOT 主线 + EF Core 生态线）。Dapper 栈即日起**功能冻结**（只修缺陷不加特性，conventions §8.5）。`IPalOutboxStore` 的跨栈 fencing 契约统一 + 异步化两项破坏性变更合并到 v3.0 窗口执行（接口 Remarks 已加预告，实现者关注迁移指引）

### ⚠️ 行为变更（AddPalLogging 追加语义，二轮评审 P2-2）

- **`AddPalLogging` 默认不再清除用户已配置的日志 Provider、不再覆盖最低级别**——v2.0.0 的旧行为（隐式 `ClearProviders()` + `SetMinimumLevel(Information)`）会静默丢弃调用方的全部日志配置，属隐式破坏性副作用。新主签名为 `AddPalLogging(IServiceCollection, bool clearProviders = false, LogLevel? minimumLevel = null)`：独占接管传 `clearProviders: true`，指定级别传 `minimumLevel`。**原 1 参重载保留**（委托至新签名默认参数，源码与二进制均兼容）——注意其行为已从"独占接管"变为"追加"：ASP.NET 默认宿主下宿主 Console Provider 与 ZLoggerConsole 并存，日志将双份输出（plain + JSON），需要旧行为请显式传 `clearProviders: true`
- ⚠️ 已知可观测变化：`WebApplication.CreateBuilder` 默认自带 Console/Debug/EventSource Provider（官方文档），追加语义下宿主 plain-text Console 与 ZLoggerConsole 并存、每条日志双份输出（此前被 ClearProviders 抑制）；需要旧行为请显式传 `clearProviders: true`。`CreateSlimBuilder` 场景经 MinimalApi 样例实测无宿主 Console 输出，无双份问题

### 行为变更（AddPalCommandHandler/AddPalQueryHandler 自动补齐核心注册，二轮评审 P2-1）

- 漏调 `AddPalDDD()` 时不再把错误延迟到首个请求（HandlerNotFound）：两个显式注册 API 现自动调用 `AddPalDDD()`（全 TryAdd 幂等，已显式调用者零影响）。纯 `BuildServiceProvider()`（非 IHost）场景 IHostedService 仍不执行——见 `HandlerRegistrar` Remarks 的手动注册指引

### 修复（SQLite Outbox 租约竞态，二轮评审 P1-3）

- `SqliteOutboxDbContext.LeasePendingMessagesAsync` 弃"SELECT 跟踪→内存改→SaveChanges"三步分离（两实例可同时租约同一批消息导致重复发布），改逐条 CAS 条件更新（`ExecuteUpdateAsync` 以 `Id + Status==Pending + LockedUntil==原值` 守卫，0 行即被抢占丢弃），多实例语义对齐 PG/MySQL。已知语义：批次内逐条 UPDATE 无显式事务——中途失败已租消息需等租约过期重试（延迟非丢失/重复）

### 内部（不涉公共 API）

- 架构边界测试改 csproj XML 解析 + 家族前缀匹配（消灭 `Confluent.Kafka`/`RabbitMQ.Client`/`MySqlConnector`/`Pomelo.*` 等文本子串匹配盲区）+ 项目引用路径分隔符归一化（修复 Linux CI 守卫空转）
- `ci-coverage.sh` 落地全局行覆盖率 ≥65% 门禁（fail-closed）
- Saga 补偿幂等契约升格（`SagaStep.CompensateAsync` Remarks 强制声明）
- PalORM 栈五处 SQL 错误分类器收敛为共享 `SqlErrorClassifier`（反射属性缓存）

## [2.0.0] - 2026-08-23

### ⚠️ 破坏性变更（三十八轮统一：状态列 int 化）

- **Dapper 栈 outbox/inbox status 列从字符串改为 INT**：`OutboxStatus` Pending=0/Processed=1/Dead=2、`InboxStatus` Pending=0/Processing=1/Processed=2/Failed=3——对齐 Saga/Checkpoint/Idempotency 三表既定 int 语义与 PalORM/EFCore 映射，五表状态列自此全部 INT，docs/sql DDL 一套三栈通用。**存量 ≤1.1.0 数据库需执行迁移脚本**（`docs/sql/migration-status-to-int.md`：先停写 → CASE 数据转换 → 改列类型 → 升级应用）；新部署直接用现行 DDL。技术可行性经 Dapper.AOT 1.0.52 探针实证（经典反射/AOT 拦截 × INT 列→枚举 读/写/参数化 7/7 断言 + 生成代码 `GetFieldValue<enum>` 直接证据）

### ⚠️ 破坏性变更（PalORM 栈 payload 列原生化，2026-08-22）

- **PalORM 适配器移除 `ByteArrayBase64Converter`，payload 系列列改原生二进制**（随 PalORM 5.3 ADR-G）：`outbox_messages.payload`、`events.payload`、`events.metadata`、`idempotency_records.response_payload` 四列从 Base64 TEXT 统一为原生二进制列（PG `BYTEA` / MySQL `LONGBLOB` / SQLite `BLOB`）——与 Dapper/EFCore 栈及 docs/sql DDL 完全一致，三栈 payload 列契约归一（省 33% 体积、免双向编解码、消除 85KB 档 LOH 分配；PalORM ADR-G 基准：64KB 档 2.3× 延迟/6.3× 分配差异）。**公共类型 `ByteArrayBase64Converter` 移除**（PalORM 适配器不在 12 核心程序集快照口径内，快照无变化）。**存量 PalORM 栈库需执行反向迁移**（`docs/sql/migration-payload-to-blob.md`：PG `decode` / MySQL `FROM_BASE64` 三步法 / SQLite 应用侧解码回填）；Dapper/EFCore 栈与新部署零动作。验证：真库探针 10/10（PG/MySQL × Outbox/EventLog payload+metadata/幂等 response_payload，含 0x00 字节全链路往返，临时库实测）+ 全量测试与基线逐项一致零回归 + PalOrmSample AOT 冒烟

### 依赖升级（2026-08-22 全量更新）

- **PalORM 全家 5.2.0→5.3.0**（Core/SourceGen/PostgreSql/MySql/Sqlite）：上游 byte[] 二进制列原生支持（ADR-G：白名单收窄放行 Byte 一维数组 + 三方言 BYTEA/BLOB/LONGBLOB DDL + DbType.Binary 显式绑定 + 等值谓词含 0x00 + Scaffold 反向工程闭环），**无破坏性变更**（纯放宽）——PalDDD 适配器的 `ByteArrayBase64Converter` 路径保留、存量 TEXT 列不受影响；真库验证 PalORM 5.3.0 Integration 181/181（PG COPY/MySQL 往返含 0x00 字节，专用临时库实测）
- **Microsoft.CodeAnalysis 5.6.0→5.9.0**（Analyzers/CodeFixes/SourceGen 三项目，编译期工具链）
- **TUnit 1.65.0→1.65.38 / TUnit.FsCheck 同步 / FsCheck 3.3.4→3.4.0**（测试框架与属性测试）
- 验证：严格构建 0 警告 0 错误；16 测试项目 956 例（915 通过 + 41 环境性 fail-closed 与升级前基线逐项一致，零回归）；README/docs 16 处 PalORM 版本引用三方同步

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

### AI 质量系统评审循环 v37-v74（2026-08-28 ~ 09-04 三十八轮稳态收敛 + 清偿）

> v37 起 P1/P2 趋零进入稳态（v70 起 P2 归零、v60 起产品运行时 P1 连续零）；修复主源转为"上轮修复残留 + 存量长尾 + 质量系统自检"。关键能力与缺陷修复按发布面摘录：

- **租约 token fencing 全面落地（v53 特性级）**：Dapper/PalORM Outbox `MarkProcessed/MarkDead/ReleaseForRetry` 持租路径补 `(locked_by, locked_until)` 双 token 守卫 + `retry_count` 乐观锁——租约被重租后旧 worker 终态写被拒（affected=0 零内存变异）；幂等存储补 `Revision` CAS 令牌（Completed 翻转重执行缺口）。EFCore 侧 `FencedTarget` 终态守卫姊妹收口（v43-v44）
- **IPv6 连接串四象限定稿（v37-v40 四轮闭环）**：方括号纯 host / 方括号+内嵌端口放行、裸 IPv6 fail-fast、`']'` 后三类畸形后缀拦截（v74 清偿批补 primary 侧三入口）+ MySQL 内嵌端口零容忍（MySqlConnector 2.6.2 源码四点实证 #762——内嵌语法 fail-fast 化，四入口共享 helper）
- **MySQL LoadBalance 显式冲突 fail-fast（v74）**：三入口原无条件覆盖串内显式策略值；规范关键字 `"Load Balance"`（带空格）经探针实证
- **模板编译探针机械化（v56 特性）**：`template-gate.sh` 确定性强制替代人工评审——模板区连续四轮 P1 根治（record 继承 CS8864/Unit.Value/saga 基类转形/MapCommand 签名等 8 项真实缺陷由探针抓出）
- **分析器生产缺陷修复（v57-v58）**：PDDD015 显式接口实现假报（analyzer 升级后探针照出）；PDDD013 ProjectionName 同型盲点姊妹收口
- **PalOrmOutboxStore 租约 token 谓词回归修复（v72 引入 v74 修复，P1）**：勘正说明误以 SQL 行内注释（`--`）嵌进单行 SQL 字符串，`AND locked_until` 谓词被数据库当注释吞掉——同 owner 重租后旧快照终态写放行；探针双红实证 + 修复后双腿回归锁定（换 owner 腿已有测试未覆盖 until 腿的盲区一并补齐）
- **守卫/口径族清偿（v53-v74 P3×189，含证伪与留档项）**：NuGet env 作用域 / fetch-depth 正交 / Detach no-op 二例 / README Build 后注册 / 计数三方同步 / `status<>1` 口径收窄（Failed 不可翻转）/ Saga Timeout 守卫锁定 / `null!` 锁定测试零保护力实证（MUTATION-4，防御性修复不可黑盒锁定的诚实声明先例）
- **mutation 红测纪律制度化（v68-v69）**：守卫修复必须带"移除后套件变红"的锁定测试——连环抓出 v66 回退不完整 + v62 口径偏宽两层潜伏缺陷
- **测试**：v2.0.0 基线约 1053 → v2.1.0 实测 1202（本机 16 项目 1153 通过 + 49 环境依赖项由 CI Testcontainers 执行）

### 依赖升级（2026-09-02）

- PalORM 5.3.0 → 5.4.0（弹性层 DI 接入：三 Provider 扩展 `configureResilience` 回调）；TUnit 1.65.38 → 1.65.68（FsCheck 同步）

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
