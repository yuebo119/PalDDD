# Pal.DDD 首席审计报告 v2（2026-09-19）

> **审计基线**：commit `fcb5ed3`（分支 `dev`，工作树干净）。
> **与前序报告的关系**：`audit-2026-09-19-chief-audit.md`（基线 `c17bd74`）的**独立复证审计**——不以其结论为前提重新取证，并显式记录 `c17bd74..fcb5ed3` 之间的状态变化。
> **方法**：阶段 1 结构勘察 + 阶段 2 四维度并行深读；所有「高」级结论由审计者本人直接读取源码复证，不采信转述。审计期间零代码修改。
> **证据标注**：`[事实]` = 文件:行可查证；`[判断]` = 审计师意见。
> **取证命令口径**（可复现）：`find src -name '*.cs' -not -path '*/obj/*' -exec cat {} + | wc -l` = 32,555；`test/` 同法 = 34,924；`git ls-files | wc -l` = 574。

---

## 执行摘要

**健康等级：B+（工艺 A，正确性证据 B）。**

这是工程纪律罕见的 .NET 框架库：test:src 行数比 1.07:1、`TreatWarningsAsErrors` 全域生效且 22 条 NoWarn 逐条附理由（`Directory.Build.props:9-45`）、32 个自证式门禁脚本、`[事实]` src 内 **0 个待办标记**（`grep TODO/FIXME/HACK` 于 `src/**/*.cs` 非注释行 = 0 命中）、反序列化面在架构层即 fail-closed（`MessageCatalog.cs:54-98`）。弱点是结构性的，集中在**「声明即免责」文化的盲区**：取舍写进了代码注释，却没有等价地进入用户文档、机械门禁与测试断言。

**前 3 风险**

1. `[事实]` **Dapper 栈 Saga 快照静默丢数据**——默认 DI 路径下 `saga_data` 写 NULL 且无诊断（`DapperSagaStateStore.cs:239-240`），而同仓 PalORM 栈在完全相同的位置抛异常并给出修复指引（`PalOrmSagaStateStore.cs:205-214`）。
2. `[事实]` **FanOut 重试粒度与工作粒度错配**——部分失败时整批子任务重放（`Saga.cs:527-558` + `FanOutStep.cs:137-141`），已成功子任务的外部副作用重复执行；无契约声明、无测试锁定、观测面不可见。
3. `[事实]` **CI 测试步骤可整体空转仍报绿**（`ci.yml:64-74` 的 `for ... in $(find ...)` 无「≥1 项目 / ≥1 用例」断言），叠加覆盖率账本自认陈旧（`docs/test-coverage-baseline.md:3` 基线日期 2026-07-30、`:6` 声明未随测试增长重测；Dapper.PostgreSql 记录 11.2%、PalORM 方言包 4%）——**生产 SQL 栈的「绿」缺乏新鲜证据支撑**。

**前 3 机会**

1. 把 PalORM 已有的 fail-fast 复制到 Dapper/EF 的 saga 快照路径：**S 级工作量消灭唯一的数据丢失面**。
2. CI 测试步骤加 3 行断言（项目数 ≥ 17、用例总数 > 0）：**S 级工作量关闭整个「空转假绿」类别**，且本仓已有同型正解（`release.yml:107-116` 断言包数 == 35）。
3. 借 `ADR-020` 的三栈契约统一窗口把 Outbox 的 `void Mark*` 异步化——它是 sync-over-async（`PalOrmOutboxStore.cs:160,196,203,239,244,307,310`）与指标失真（`OutboxBatchProcessor.cs:188-197` 自认）的**共同根因**，一次改三处。

**状态变化（相对 `c17bd74` 报告）**：`[事实]` 其头号风险 O-1「本地 16+ 提交未推送、CI 未跑」**已清偿**——`git log --oneline origin/dev..dev | wc -l` = 0。其 C-1/C-2/C-3 亦已由 `e206dda`/`ec0ad59` 实施（Dapper 侧唯一约束分类器已收口至 `DapperSqlErrorClassifier.cs`；EF 侧 5 份副本仍在，见 §三 3.1 A-7）。本报告因此不重复已闭合项，聚焦 `c17bd74` 报告未覆盖的 3 个「高」级发现。

---

## 一、仓库地图

**用途**：面向 .NET 11 的 DDD/CQRS/Event-Sourcing 基础设施**框架库**，35 个 NuGet 包，AGPL-3.0-or-later（`Directory.Build.props:63`）。**成熟度**：v2.2.0（`:60`）——生产级工艺 + 预发布运行时依赖。

| 维度 | 实证 |
|---|---|
| 语言/运行时 | C# `LangVersion latest` / `net11.0`（分析器与源生成器 netstandard2.0），SDK 锁 `11.0.100-rc.1.26425.128`（`global.json:3`） |
| 入口点 | 无应用入口（纯库）；`bench/` 为基准宿主，`samples/` 4 个可运行示例 |
| 依赖治理 | CPM 单一版本源（`Directory.Packages.props`）+ `CentralPackageTransitivePinningEnabled=true` |
| 测试栈 | TUnit 1.66 + MTP + FsCheck 属性测试 + Verify 快照 + Testcontainers 四方言 |
| 门禁 | `.githooks/pre-commit` 七守卫（首次构建自动接线，`Directory.Build.targets`）+ CI 五 job + 32 个 `scripts/*.cs` |

**架构分层**（`PalDDD.slnx:4-73` 显式分组）：
`Core`（零依赖）→ 抽象层（`Serialization`/`Messaging`/`Compression`/`CQRS`）→ **三栈同构持久化**（Dapper×4 手写 SQL / PalORM×4 源生成 SQL / EFCore×6）→ 组合层（`DependencyInjection`/`Hosting.AspNetCore`）。竖切域：`Transactions`（Saga 893 行 + Outbox + Inbox）、`EventLog`、`Projections`、`Idempotency`。

**关键目录**

| 目录 | LOC | 一行描述 |
|---|---|---|
| `src/PalDDD.Transactions` | 4,593 | Saga 编排四车道 + Outbox 租约/死信 + Inbox |
| `src/PalDDD.Dapper`(+方言) | 8,101 | 手写 SQL 栈：`SqlTemplates` 常量 + 三方言分支 |
| `src/PalDDD.PalORM`(+方言) | 3,021 | 源生成 SQL 栈（AOT 主线） |
| `src/PalDDD.Core`(+SourceGen) | 3,729 | 实体/值对象/SmartEnum + 标识/枚举生成器 |
| `test/` | 34,924 | 17 项目 + `PalDDD.Testing` 共享基建 |
| `scripts/` | 34 文件 | file-based app 门禁（`--selftest` + 变异探针范式） |
| `docs/` | 17,856 行 md | 24 份 ADR + 40+ 份评审台账 |

**审计者惊讶处** `[判断]`：① 门禁体系带**变异探针**（故意让门禁变红才算可信）；② 注释不只解释代码而是**承载契约**（`SagaState.cs:96-117` 一个方法带 22 行取舍史与回滚记录）；③ `.ai/` 作为独立 git 仓把 AI 评审流程本身版本化。

**审查较浅的区域**（诚实声明）：`Core.SourceGen` 两个生成器内部（`EnumGenerator.Initialize` 337 行、`IdentityGenerator.Initialize` 327 行）、`Analyzers`/`CodeFixes` 的诊断正确性、`samples/`、`bench/`、`.ai/` 子系统；`docs/` 17.8k 行仅抽样。上述区域的结论只到尺寸/接线层面。

---

## 二、审计维度总览

| 维度 | 判定 | 最高severity |
|---|---|---|
| 架构与设计 | 有 2 个高级契约裂缝 | 高 |
| 代码质量 | 健康（错误处理纪律强） | 中 |
| 安全 | 健康（无严重/高级） | 中 |
| 测试 | 工艺强，但**可信度机制有洞** | 高 |
| 性能 | 已大幅清偿，残余中等 | 中 |
| 依赖 | 单一结构性风险 | 高（日历窗口） |
| 开发体验与运维 | 门禁密但自证不接线 | 中 |
| 文档 | 优势项 + 一类成比例落差 | 中 |

---

## 三、审计报告

### 3.1 架构与设计

| # | 严重性 | 位置 | 发现 | 后果 |
|---|---|---|---|---|
| A-1 | **高** `[事实]` | 缺陷点 `DapperSagaStateStore.cs:239-240`（`_jsonTypeInfo is null ? null : Serialize`）→ 写入点 `:170,:185`；默认注册无传参通道 `DapperServiceCollectionExtensions.cs:120`，opt-in 在 `:152` | 未调 `AddPalDapperSagaSnapshot` 时 Saga 业务字段**静默不持久化**，重启只恢复元数据。**PalORM 同位置抛异常**（`PalOrmSagaStateStore.cs:205-214`：`"Register with AddPalSagaStore<TState>(jsonTypeInfo)..."`） | 不可逆业务数据丢失，无异常/无日志/无启动诊断。用户文档仅软性提示「需要完整快照时…传入 JsonTypeInfo」（`docs/usage.md:300`、`docs/architecture.md:331`），README 零提及。同族缺陷在 PalORM 已按 fail-fast 解决 → **这是跨栈契约分叉，不是已声明取舍** |
| A-2 | **高** `[事实]` | 契约根因：`IPalOutboxStore` 为 `void`（`OutboxStore.cs:57,61`）而 `IInboxStore` 为 `ValueTask …(ct)`（`InboxStore.cs:30,33`） | 同框架内两个姊妹接口一个同步一个异步。Dapper 实现被迫 `conn.Open()` + `c.Execute(...)` 同步（`DapperOutboxStore.cs:215,260,283,301`），PalORM 被迫 `.AsTask().GetAwaiter().GetResult()` **7 处**（`PalOrmOutboxStore.cs:160,196,203,239,244,307,310`），全部位于 async 批处理循环内 | 每条消息阻塞一个线程池线程做网络 I/O，高并发下线程池饥饿；并导致 `OutboxBatchProcessor.cs:188-197` 自认的指标失真。已声明并推给 v3.0/ADR-020——声明无误，但它是三处症状的**单一根因**，应作为 ADR-020 头号条目而非并列项 |
| A-3 | 中 `[事实]` | `Saga.cs:527-558` + `RunRetryLaneAsync`（`:439` `for attemptNo <= MaxRetries`）+ `FanOutStep.cs:137-141`（`completedFlags` 是**单次 attempt 的局部数组**） | FanOut 车道把「整批 N 个子任务」包进一个重试单元；任一子任务失败 → `AggregateException`（`Saga.cs:536-541`）→ 重试时 `_selector(state)` 重新取全量 items，**已成功子任务的 executor 再跑一遍** | 外部副作用重复（扣款/发货/通知类 executor 直接双写）。`FanOutStep` 的 XML 契约（`:28-40`）只声明「部分失败不阻断其他子任务」，**未声明重放语义**；`SagaLaneCharacterizationTests.cs:62` 记录「FanOut 无子项级观察事件（item 粒度不在观察面）」→ 重放在观测面不可见 |
| A-4 | 中 `[事实]` | `DapperInboxStore.cs:78,152,178`、`DapperOutboxStore.cs:328` 注释均写「改 CommandDefinition 传递 ct」；`grep -rn CommandDefinition src/ --include=*.cs` 命中**全部为注释行，0 个代码调用点**；现行裁决在 `DapperAotInitializer.cs:15-22`（34 处已改**直接重载**，ct 显式收缩） | 十七轮的历史注释与 2026-09-13 的反向裁决**并存且互斥** | 下一位审计者按注释「恢复修复」即引入 DAP057 + NativeAOT `PlatformNotSupportedException` 回归（`DapperAotInitializer.cs:12-13` 实证）。违反 `AGENTS.md §1` 三方一致红线 |
| A-5 | 中 `[事实]` | `DefaultSagaManager.cs:162-167`：`GetInterruptedSagasAsync` 恒 `return new([])`，注释「生产环境应替换为数据库查询实现」 | 公共 HITL 运营查询 API 在 shipped 包内是永久空数组，不抛异常 | 运营方误判「无人工待办」，静默假空 |
| A-6 | 低 `[事实]` | `Saga.cs:480` `return current;` 不可达（`:111` 已守卫 `MaxRetries` 非负 → 循环必 return 或 throw） | 死代码一行 | 误导性控制流、扫描噪声 |
| A-7 | 低 `[事实]` | EF 侧唯一约束分类器仍 5 份副本：`EventLogDbContext.cs:383`、`EventLogPositionReserver.cs:296`、`IdempotencyDbContext.cs:249`、`ProjectionCheckpointDbContext.cs:353`、`InboxDbContext.cs:264`（Dapper 侧 ITM-796 已收口至 `DapperSqlErrorClassifier.cs`，PalORM 侧 `SqlErrorClassifier.cs`） | 姊妹已收口、EF 未收口，且码集分叉（缺 MySQL 1022 / SQLite 19,2067） | 同一冲突在 EF 侧误分类 → 重试 5 次后抛「乐观并发重试耗尽」，误导排障方向 |

**分层判定** `[事实]`：`ArchitectureBoundaryTests` 44 用例机械守护、无循环依赖、无神文件（最大类 `Saga` 893 行）。该维度总体健康，问题集中在**同构契约的裂缝**而非分层。

### 3.2 代码质量

* **错误处理纪律强** `[事实]`：无全域 `catch {}` 滥用；`Mark*` 失败挂 `ex.Data["MarkError"]` + Warning 且不遮蔽根因（`OutboxBatchProcessor.cs:161-188`、`InboxProcessor.cs:146-153`）；退避策略自身故障有降级（`OutboxBatchProcessor.cs:146-153`）。
* **时钟面统一** `[事实]`：src 无壁钟直取，`SagaState.cs:55-61` 走 `AsyncLocal<TimeProvider>`。这是可测性地基，须保留。
* **两处静默**（中）`[事实]`：`PostgreSqlOutboxNotifier.cs:265-268` 裸 `catch {}` 吞自唤醒失败零日志；`IdempotencyProcessor.cs:219-225` 毒载荷降级 `Skipped` + `default(TResult)`，**仅 Activity 事件无日志**——且因方法为 `private static`（`:205`）**结构上无法记日志**。
* `[判断]` 注释承载契约是双刃剑：`SagaState.cs:96-117` 的 22 行取舍史（含 2026-09-19 M3-3 回滚记录）说明**声明式注释确实阻止了一次错误「修复」**；同一机制在 A-4 处却留下互斥历史注释。差别在于**新决策是否向下清扫旧声明**——这是可制度化的（见 §四 主题 2）。

### 3.3 安全

**结论：该维度健康，无严重/高级发现。** 反序列化面是**结构性安全**而非「关掉了检查」：类型只从预注册 `FrozenDictionary` 解析、未知类型 fail-closed（`MessageCatalog.cs:54-98`、`MessageBrokerBase.cs:47-49`）、Kafka 类型编译期绑定（`KafkaBroker.cs:128`）；`grep` `TypeNameHandling` / `AssemblyQualified` / `BinaryFormatter` **零命中**；标识符注入走白名单而非转义（`DapperBulkCopy.cs:421-452`、`PostgreSqlSharding.cs:314`、`PostgreSqlAuditor.cs:160-165`）；src 内无 `unsafe`/`DllImport`/`Marshal`；无凭据入库（`appsettings.test.local.json` 未跟踪 + `.gitignore:97`，`git log --all -S` pickaxe 零命中）。

| # | 严重性 | 位置 | 发现 |
|---|---|---|---|
| S-1 | 中 `[事实]` | `grep MaxDepth\|MaxReceivedMessageSize src/` = **0 命中** | 反序列化无载荷尺寸/嵌套深度上限；恶意 broker/DB 对端 → `MemoryPackMessageSerializer.cs:99` / `JsonMessageSerializer.cs:185` OOM 或栈耗尽 |
| S-2 | 中 `[事实]` | `IEventLog.cs:24,30` `maxCount` 默认 `int.MaxValue` + `PalOrmEventLog.cs:195-199` 该分支**有意省略 LIMIT** | 任意可选流名的读接口可物化整条事件流 → 单调用 DoS |
| S-3 | 中 `[事实]` | `Directory.Build.props:45` 抑制 `NU1900;NU1901;NU1902;NU1903;NU1904` | NuGet 漏洞审计作为构建门禁被关闭；**NU1900 尤其**＝「漏洞数据下载失败」也静默通过。已复证 `dotnet list PalDDD.slnx package --vulnerable --include-transitive` 当前全绿（40 项目），故**无活漏洞被掩盖**——缺的是防线不是内容 |
| S-4 | 中 `[事实]` | `docs/review/dapper-aot-experiment-2026-09-13.md:23-24`、`review-2026-09-14-quality-run.md:5`、`open-items-2026-09-14.md:48` | 公开 AGPL 仓的跟踪文档泄露内网拓扑：`<内网 IP>`（2026-09-19 已全仓脱敏为 INTERNAL_TEST_HOST）、PG 18.4 / MySQL 8.4.11 / Kafka / RabbitMQ 端口版本，且注明 MySQL 以 **root** 访问 → 免预认证的目标清单 |
| S-5 | 低 `[事实]` | `PostgreSqlJsonbExtensions.cs:250-251`、`PostgreSqlAuditor.cs:127` | 手写转义只加倍单引号并显式拒绝处理反斜杠（`:240-249` 自认不修）→ 正确性依赖会话 `standard_conforming_strings=on`；pooler/per-role 置 `off` 时 `\'` 可逃逸 |
| S-6 | 低 `[事实]` | `Transactions/ServiceCollectionExtensions.cs:36,69` | `BatchSize`/`TimeoutScanBatchSize` 只校验 `> 0` 无上界 → 配置放大为单 tick 全表搬移（`PalOrmOutboxStore.cs:66,108` `LIMIT {batchSize}`） |
| S-7 | 低 `[判断]` | `Directory.Build.props:28` CA2100 抑制理由 | 理由（「全部走 SqlTemplates 常量」）**作为文字陈述已不实**：PalORM Stores 内插 SQL 34 处、Dapper.PostgreSql 12 处。逐点可辩护（PalORM 的 `FormattableString` 逐孔绑定 `@p{N}`，`PalOrmSagaStateStore.cs:114-117` 自证），但「永久抑制 + 已失真理由」＝下一处新增内插标识符不会被任何工具看到 |

### 3.4 测试

工艺优势是真实的（不是安慰奖）`[事实]`：tautology 0 个；`[Ignore]`/注释掉的测试 0 个，每个 skip 带显式理由；断言强度棘轮带**负向自证**样本（`AssertionStrengthGateTests.cs:27` `MaxWeak=200` + `:69+`）；公共 API 快照在 CI 拒绝自我更新（`PublicApiSnapshotTests.cs:49-57` + `ci.yml:77`）；测试内 `DateTime.Now`/`Stopwatch` 0 命中；flaky 面仅 6 处固定 `Task.Delay`。

| # | 严重性 | 位置 | 发现 |
|---|---|---|---|
| T-1 | **高** `[事实]` | `ci.yml:64-74` | 测试步骤为 `for csproj in $(find test -name '*.csproj' ! -name 'PalDDD.Testing.csproj' \| sort)`，**无「项目数 ≥ N」或「用例数 > 0」断言**。glob 失效 / TUnit 发现失败 / 全量 skip → exit 0 报绿。`pipefail` 本身在 `:65` 设置正确（历史掩码问题现状干净）。同仓已有正解模式：`release.yml:107-116` |
| T-2 | 中 `[事实]` | `BrokerIntegrationTests.cs:307-331` 等 10 处 `Skip.Test`（Docker/broker 不可用）vs `test/PalDDD.PalORM.Tests/MultiDialectFixture.cs:92` `EnsureTestcontainersRequired` **抛异常** | 同一套件内两套相反策略。且 `src/PalDDD.Messaging.Kafka`(512 行)+`RabbitMQ`(496 行)**零单元测试**（`test/PalDDD.Messaging.Tests` 523 行只测抽象）→ Docker 一坏，约 1k 行 ack/去重/错误路径从「已测」变「静默绿」 |
| T-3 | 中 `[事实]` | `docs/test-coverage-baseline.md:3`（基线 2026-07-30）、`:6`（「本文余下覆盖率数字均为基线，未随测试增长重测」）、模块表 Dapper.PostgreSql **11.2%** / PalORM.MySql、PostgreSql **4%**；`coverage-baseline.json` 15 条目**不含 PalORM.Tests**（3,032 行测试的最大数据访问面无降幅地板） | 覆盖率地板（0.70 全局 + 单模块 ≤5pp）建立在自认过期的数据上；业务风险最高的生产 SQL 栈恰是最低覆盖区 |
| T-4 | 中 `[事实]` | `MessageEvolutionTests.cs:141-168` `ValidatePath_WithCompletePath_DoesNotThrow`：无断言、无 await、纯「没抛」 | 路径完整性逻辑的唯一正例测试可在回归时保持绿 |
| T-5 | 低 `[事实]` | 8 个测试文件各自重实现 `FindRepositoryRoot()`（`AssertionStrengthGateTests.cs:33` 等），`test/PalDDD.Testing` 无共享 helper | 复制漂移 |

### 3.5 性能

`[事实]` 已清偿项：Lease N+1（13.79ms，4.72×）、`GetPending` 谓词下推（`fcb5ed3` 台账记录）、`(locked_by,locked_until)` 索引在位。残余：

| # | 严重性 | 位置 | 发现 |
|---|---|---|---|
| P-1 | 中 `[事实]` | `OutboxBatchProcessor.cs:103,112,124,163,176` → `:211-215` `PersistSingleAsync` 每条消息调 `SaveChangesAsync` | Dapper/PalORM/InMemory 该方法是 no-op（`DapperOutboxStore.cs:337`、`PalOrmOutboxStore.cs:343-348`、`InMemoryOutboxStore.cs:292`），**EF 栈是真发**（`OutboxDbContext.cs:324-325`）→ 批大小 N = N 次 EF 往返 |
| P-2 | 中 `[事实]` | `DapperSagaStateStore.cs:16-17` 自述「先查询后决定」（代码 `:169-172`）+ `SagaProcessor.cs:215,238` 逐 saga 保存 | 每个非超时 saga 每 tick 2 次往返纯为清租约；`SqlTemplates` 已有 PG `RETURNING`/`SKIP LOCKED` 单语句形态 → 是**方言能力不对称**而非缺失 |
| P-3 | 中 `[事实]` | `DapperEventLog.cs:98` 注释「批量插入事件」vs `:112-175` 循环内 `QuerySingleAsync<long>`（`:147`） | N 事件 = N 往返 + 1 次版本预检。逐行 `pos` 是真实约束（`:178-179` 跟踪 first/lastGlobalPos），但 PG 可多行 `VALUES … RETURNING` 一趟取回 |
| P-4 | 中 `[事实]` | `InMemoryOutboxStore.cs:27`（`List`，全文 **0 个 Remove**）、`InMemoryEventLog.cs:17-18`（`Dictionary`+`List` 双份存储，0 Remove）、`InMemorySagaStateStore.cs:14`（0 Remove）；对照 `InMemoryIdempotencyStore`/`InMemoryProjectionCheckpointStore` **有**淘汰 | 无界增长 + `InMemoryOutboxStore.cs:70-72` 每次租约重扫全表 → O(n·batch)。**缓解事实** `[事实]`：这些类型在 src 中未被任何 DI 注册（`grep AddScoped.*InMemory` 零命中），属测试/文档 fixture（`docs/usage.md:310,390,438`）——但文档正例未标注「仅限测试」 |
| P-5 | 低 `[事实]` | `PostgreSqlOutboxNotifier.cs:180-189` 非停机 OCE 分支固定 1s 重连且 `_reconnectAttempts` 不增；`Task.Delay(..., CancellationToken.None)` 忽略关停 | 内部超时反复时 1 次/秒重连风暴（≤1s 的关停延迟可接受） |

### 3.6 依赖

| # | 严重性 | 位置 | 发现 |
|---|---|---|---|
| D-1 | **高**（日历窗口）`[事实]` | `Directory.Packages.props:18-27,54,84-95`（9+ 个 `11.0.0-rc.1.26425.128`）+ `:97`（`Microsoft.NETCore.Platforms 8.0.0-preview.7` 停更包，`:96` 自证无更新版）+ `:59`（`MySqlConnector 2.6.2` 落后）+ `Directory.Build.props:42`（`NU5104` 抑制 + 自认「GA 后统一升级」） | 以 `VersionPrefix 2.2.0`（`:60`，无 suffix）发布**稳定版本号但携带不可服务的预发布依赖**；rc→GA 是硬迁移窗口。仓内已有 rc 特有行为史（`Directory.Packages.props:14-17` 死条目误删案、ITM-261 翻译限制） |
| D-2 | 中 `[判断]` | `Directory.Packages.props:78-99` 传递钉扎 + `:79-83` 二十六轮误删复盘 | 钉扎面大且**人工判定**，本仓已为「仅 grep 直接引用」付过 7 项目回退 1-2 主版本的学费。缺一个「传递图 diff」门禁把该判定机械化 |
| D-3 | 记录 `[事实]` | 无锁文件 | 库项目惯例，合理（NuGet 不 ship lock 文件）。不判为缺陷，仅记：CI `restore` 结果不可字节复现，浮动窗口由 CPM 兜住 |

### 3.7 开发体验与运维

| # | 严重性 | 位置 | 发现 |
|---|---|---|---|
| O-1 | 中 `[事实]` | `scripts/gate-audit.cs:396-424`（判定在 `:420-421` `text.Contains(s, Ordinal)`） | 「验证者的验证者」用**名字出现即接线**的宽松口径：脚本名出现在 CI **注释**里也算 WIRED。`:393-395` 附了合理理由（CI 循环用变量插值无法字面匹配），代价是 `UNWIRED-GATE` 桶可能为空而实际未接线。另 `intendedWire["ci-coverage"]`（`:108`）已过期——`ci.yml:237-238` 现已接线 |
| O-2 | 中 `[事实]` | `scripts/gate-audit.cs:93` 将自身登记为 `manualTools` | 9 个变异探针（`:163-272`，含正负例，质量确实高）**从不在 CI 跑**；`AGENTS.md §3` 记录的两次历史假绿（vuln-scan exit-0、secret-scan tee 掩码）可无声回归 |
| O-3 | 中 `[事实]` | `ci.yml:19,162,215,248` 单一 `ubuntu-latest`，无 windows job；而 `:196` 注释提 `win-x64`、开发者主力平台为 Windows | Windows 侧回归（原生 zstd 绑定、路径/行尾、AOT 工具链）无人验证。`AGENTS.md §3` 的行尾三犯（`4a64fba` 修 1543 处）正是该平台的账 |
| O-4 | 低 `[事实]` | `release.yml:118-143`：缺 `NUGET_API_KEY` 时仅 `::warning::`，job 绿且 GitHub Release 照建 | 「发布成功」可等于「0 个包发布」 |
| O-5 | 低 `[事实]` | `ci.yml:79-85` `if-no-files-found: ignore`；`dialect-probe` 路径过滤含裸词 `Store`（`ci.yml:268`） | 测试报告丢失不可见；目录改名（Store→Repository）会**无声关闭方言探针**（PR 分支仍跑，`:260-261`） |
| O-6 | 低 `[事实]` | 无 `CONTRIBUTING.md`（`docs/development.md:22-28` 覆盖同内容）；`Directory.Build.targets:7-61` 首建自动接线 `core.hooksPath` | clone→build→钩子零手工步骤，缺口仅是文件名可发现性 |

### 3.8 文档

三方一致是红线且有机械门禁（`verify-conventions.cs` V5/V8/V9/V11 + `doc-consistency.cs` D7）；24 份 ADR 全带状态头注；`[事实]` README 抽 3 例 API 全部对得上源码（`AddPalOutbox` → `Transactions/ServiceCollectionExtensions.cs:14`、`AddPalSaga<TState,TSaga>` → `:50`、`AddPalOrmPostgreSql` → `PalORM.PostgreSql/PostgreSqlPalOrmExtensions.cs:52`）。该维度为优势项，一句结论。

**唯一成比例的缺口** `[事实]`：A-1 的静默丢数据只在代码 XML 注释里（`DapperServiceCollectionExtensions.cs:126-143`），README 零提及，`docs/usage.md:300` 以「需要完整快照时…传入」的可选口吻表述一个默认必踩的坑。按 `AGENTS.md §1` 红线口径（改公共 API 行为须同步 README/docs），这类「注释已声明但文档未升级」是可判定的落差点。

`[判断]` 文档体量本身中性：`docs/` 17,856 行 md + CHANGELOG 604 行/91KB + 40+ 份评审台账 ≈ src LOC 的 55%。收益是决策可追溯（`AGENTS.md §3` 的回滚记录确实救过一次），代价是每个改动付文档税。**不建议削减，建议加「文档预算」约束**（§四 不修清单）。

### 3.9 优势（决定保留什么）

1. `[事实]` **fail-closed 的多态解析设计**——线上字节里永不含类型名，经典 .NET 反序列化 gadget 类**在架构层不存在**（`MessageCatalog.cs:54-98`）。本仓最值钱的安全资产。
2. `[事实]` **租约/fencing 一致性**：所有写路径比较 `status`+`locked_by`+`locked_until`+`retry_count`/`version`（`SqlTemplates.cs:60-90,343`、`PalOrmInboxStore.cs:162,190`）→ 毒消息/重复消息无法双花副作用。
3. `[事实]` **at-least-once 不对称性判断正确**：副作用已发生后 `MarkProcessed` 失败返回成功而非降级为可重试（`InboxProcessor.cs:110-131`、`IdempotencyProcessor.cs:132-159`）——多数实现会写错这一处。
4. `[事实]` **时钟全注入 + 测试零壁钟**：租约/退避可用 `FakeTimeProvider` 确定性验证。
5. `[事实]` **门禁带负向自证**：`ci-coverage.cs:173-186,193-215,222-232` fail-closed 且 NaN 感知——覆盖率脚本通常会假绿。
6. `[事实]` 增量构建 10.5s / 0 警告 + `TreatWarningsAsErrors`，`dotnet format style --verify-no-changes` 进 CI（`ci.yml:57`）。
7. `[判断]` **`AGENTS.md §3` 的「先读声明注释」规程**是唯一见到能阻止**审计驱动的回退**的机制（M3-3 案）。它保护的是本仓最有价值的资产——已支付的学费。

---

## 四、改进策略

### 主题 1：把「三栈同构」从纪律变成机械
解释 A-1、A-2、A-7、S-7。
**目标态**：同一契约的三种实现，**失败模式一致**。原则：一致性只有被机械比对时才会保持——本仓已有 `dialect-probe`（Testcontainers 三方言）与三栈谓词对照测试（ITM-807），缺的是**行为族层**的对照（null / 缺失元数据 / 超界输入时是抛还是静默）。
**动作**：`saga_data` 快照在 Dapper/EF 侧对齐 PalORM 的 fail-fast；EF 5 份分类器副本并入共享分类器；建立「契约行为表」（每方法 × {null、缺失 JsonTypeInfo、超界、租约被抢} → 期望异常类型）驱动三栈参数化测试。

### 主题 2：让静默失败可见
解释 A-3、A-5、T-1、T-2、T-4、O-1、O-2、O-4、S-1..S-3。
**目标态**：**没有「返回空/返回默认但什么都没记」的公共路径**；CI 的绿必须是「跑过东西的绿」。原则：本仓已确立「指标尽力语义必须写声明」（`OutboxBatchProcessor.cs:188-197`），下一步把同等严肃性用在**测试与门禁自身的可信度**上。
**动作**：CI 断言项目数与用例数下界；无 Docker 时 `Skip.Test` 改为可配置 fail-loud（与 `MultiDialectFixture.cs:92` 对齐）；gate-audit 接线判定从「子串命中」升级为「上下文命中（命令行/step 名，排除注释行）」；9 个变异探针接入 CI；空实现公共 API 抛 `NotSupportedException`。

### 主题 3：重试单元 = 工作单元
解释 A-3、P-1、P-2。
**目标态**：FanOut 的重试发生在**子项粒度**，Outbox 的持久化发生在**批次粒度**。原则：幂等只能是消费方契约；框架不应主动制造重复副作用再要求下游兜住。
**权衡**：子项粒度需 per-item 完成记录进入 `SagaState`（当前 `ExecutedStepKeys` 是**步骤**粒度，`SagaState.cs:87-91`）→ 触碰 ADR-020 的 v3.0 契约面与快照兼容。故分两步：**先声明 + 加测试锁住现状语义**（S/M，零风险），粒度改造排进 v3.0 窗口。

### 主题 4：证据新鲜度即门禁
解释 T-3、D-1、O-1。
**目标态**：任何被引用的质量数字带日期与 commit，过期即红。原则：本仓哲学是「没让仪器故意变红过就不信它」，同样适用于**账本**——一份 7 周前的覆盖率表在 1,379 用例的套件上不构成证据。
**动作**：`coverage-baseline.json` 补 PalORM.Tests 条目；`docs/test-coverage-baseline.md` 的表改为脚本生成（禁手写）；GA 迁移预案（ITM-806 已起头）扩为可执行清单。

### 明确不修什么（权衡声明）

| 不修 | 理由 |
|---|---|
| 三栈合一 / 退役任一栈 | 三栈是产品定位（AOT 主线 + 生态兼容），`ADR-020` 已定退休路线；只做**行为对照**不做合并 |
| `SagaState.CloneForLease` 改深拷贝 | `[事实]` `SagaState.cs:100-116` 的 v26 声明 + 2026-09-19 M3-3 回滚已论证：深拷贝把「并发写可抛（可见→死信）」换成「僵尸步骤从补偿轨迹静默消失（漏补偿，无兜底）」。**本报告接受该取舍**。若要修，方向是**并发安全且语义等价**的（容器写入加锁 / 换 `ConcurrentDictionary`），且按 `AGENTS.md §3` 必须先落 `docs/decisions/` 再动代码——本报告不提议绕过该规程 |
| Dapper 侧重新引入 `CommandDefinition` 传 ct | `[事实]` `DapperAotInitializer.cs:12-13`：DAP057 + NativeAOT `PlatformNotSupportedException` 双向阻断，2026-09-13 用户裁决「铺垫不动」。要修的是注释（A-4），不是代码 |
| 预升 rc.2 / 降回 .NET 10 | 两次迁移成本换零收益；降级否定产品定位 |
| 文档削减工程 | 17.8k 行文档是已付学费。改为**加预算约束**：新审计台账须写明与前序报告的 diff 与对应 commit（本报告已按此执行），避免台账按轮次线性膨胀 |
| InMemory 存储加淘汰逻辑 | `[事实]` 未被 DI 注册，属 fixture；只补「仅限测试」文档标注（M3-12） |

**「完成」的可衡量定义**
① `saga_data` 路径三栈行为一致，且有一条跨栈参数化测试覆盖「未注册 JsonTypeInfo」；
② CI 在「0 个用例执行」时变红（以一次故意的 glob 破坏自证）；
③ FanOut 重放语义被测试锁定且写入 XML 契约 + `docs/usage.md`；
④ 覆盖率表由脚本生成、条目含 16 个测试项目、日期距上次运行 ≤ 7 天；
⑤ gate-audit 探针在 CI 有运行记录；
⑥ 严重级发现 0 个，「高」仅剩 D-1（日历事件型）。

---

## 五、任务计划

### 里程碑 0 —— 安全网（先于任何正确性修复）

| ID | 标题 | 影响区域 | 验收标准 | 工作量 | 变更风险 | 依赖 |
|---|---|---|---|---|---|---|
| M0-1 | **CI 断言「确实跑过测试」** | `.github/workflows/ci.yml:64-74` | 故意改坏 glob → 步骤红；恢复后绿；项目数 ≥ 17 且累计用例数 > 0 落进日志 | S | 低 | — |
| M0-2 | **FanOut 重放语义表征测试**（锁现状，不改行为） | `test/PalDDD.Transactions.Tests/SagaLaneCharacterizationTests.cs` | executor 计数器断言「部分失败 + MaxRetries=1 → 成功项被调用 2 次」变绿；若不符则暴露真实语义与本报告的差异 | M | 无（纯测试） | — |
| M0-3 | 覆盖率账本重测 + 补 PalORM.Tests 条目 | `docs/test-coverage-baseline.md`、`coverage-baseline.json` | 表内数字全来自同一次运行（记 commit + 时间戳）；条目数 == 16；0.70 门禁在新基线上重跑绿 | M | 低（阈值或需一次性调整） | — |
| M0-4 | gate-audit 接线判定去注释化 | `scripts/gate-audit.cs:396-424` | 新增负例：在 ci.yml **注释**里写脚本名 → 判定仍为 `UNWIRED-GATE`；现有正例不破 | M | 中（可能暴露历史未接线门禁） | — |

### 里程碑 1 —— 关键修复（数据完整性与静默失败）

| ID | 标题 | 影响区域 | 验收标准 | 工作量 | 风险 | 依赖 |
|---|---|---|---|---|---|---|
| M1-1 | **Dapper/EF Saga 快照 fail-fast（对齐 PalORM）** | `DapperSagaStateStore.cs:239-240`、EF 侧 `SagaStateDbContext.cs`；opt-in `DapperServiceCollectionExtensions.cs:120,152` | 未注册 JsonTypeInfo 时 `SaveChangesAsync` 抛含修复指引的异常（复用 `PalOrmSagaStateStore.cs:205-214` 文案与类型）；README + `docs/usage.md:300` 升级为警告段；三栈同测 | M | **中**（静默→异常对既有用户是破坏性变更，须 CHANGELOG，或归 v3.0） | M0-2 |
| M1-2 | FanOut 重放语义**先声明** | `FanOutStep.cs:28-40`（XML 契约）、`Saga.cs:527-558`、`docs/usage.md` | 契约明确「整批 attempt 级重试，executor 须自身幂等」；`SagaStep` 文档交叉引用；测试注释指向契约 | S | 低 | M0-2 |
| M1-3 | 清扫 A-4 历史注释（ct / CommandDefinition 口径） | `DapperInboxStore.cs:78,152,178`、`DapperOutboxStore.cs:328` | src 内不再有「CommandDefinition 传 ct」字样的**现行**声明；替换为指向 `DapperAotInitializer.cs:15-22` 裁决的指针 | S | 无 | — |
| M1-4 | 载荷/深度上界 | `Serialization/JsonMessageSerializer.cs:185`、`MemoryPackMessageSerializer.cs:99`、`Messaging*/` 选项 | 超限在**分配前**失败（测试断言异常类型）；默认上界写进 options 校验与文档 | M | 中（可能拒收既有合法大载荷，须可配置） | — |
| M1-5 | `GetInterruptedSagasAsync` 空实现表态 | `DefaultSagaManager.cs:162-167` | 抛 `NotSupportedException`（含替换指引）**或**在公共 API 快照 + 文档标注为宿主必须覆写；二者择一并有测试 | S | 低 | — |
| M1-6 | 跟踪文档内网拓扑脱敏 | `docs/review/dapper-aot-experiment-2026-09-13.md:23-24`、`review-2026-09-14-quality-run.md:5,57`、`open-items-2026-09-14.md:48` | `grep -r "192\.168" docs/` 命中 0（或全改 `<INTERNAL_HOST>` 形式）；历史 blob 无需改写（pickaxe 零命中已复证） | S | 无 | — |

### 里程碑 2 —— 高杠杆改进

| ID | 标题 | 影响区域 | 验收标准 | 工作量 | 风险 | 依赖 |
|---|---|---|---|---|---|---|
| M2-1 | **三栈契约行为矩阵**（参数化同构测试） | `test/PalDDD.Transactions.Tests/` + `PalDDD.PalORM.Tests/`（新建跨栈 fixture）；契约源 `OutboxStore.cs:22-65`、`InboxStore.cs:30-33` | 一表覆盖 {Store 方法} × {null / 缺失元数据 / 超界 / 租约被抢} × {Dapper, PalORM, EF}，任一栈偏离即红；M1-1 修完由它守住 | XL（需先分解） | 中 | M1-1 |
| M2-2 | Outbox 接口异步化提案落地（v3.0 窗口） | `IPalOutboxStore`（`OutboxStore.cs:57,61`）→ `ValueTask + ct`；三实现 + `OutboxBatchProcessor.cs:99-176`；ADR-020 追加 | `PalOrmOutboxStore.cs` 内 `GetAwaiter().GetResult()` 命中 **0**；指标声明（`:188-197`）改写为精确计数（`Mark*` 返回 affected） | L | 高（公共 API 破坏）→ **必须 ADR 先行**，按 `AGENTS.md §3` | M2-1 |
| M2-3 | EF 分类器副本收口 + 码集统一 | 5 处（见 A-7）→ 共享分类器 | 私有实现数 5 → 1；MySQL 1022 / SQLite 19,2067 码集三栈一致 | M | 低 | M0-3 |
| M2-4 | 变异探针接入 CI | `.github/workflows/ci.yml` gate 段 + `scripts/gate-audit.cs:93,163-272` | CI 日志含 9 个探针的 CAUGHT 记录；任一 MASKED → job 红 | M | 中（可能暴露历史退化） | M0-4 |
| M2-5 | 覆盖率表脚本化（禁手写） | `scripts/ci-coverage.cs` + `docs/test-coverage-baseline.md` | 文档表由生成物注入；`doc-consistency` 拒绝与产物不符的数字 | M | 低 | M0-3 |
| M2-6 | 传递依赖图 diff 门禁 | `scripts/`（新）+ CI | 输入两份 `dotnet list --include-transitive` 快照，输出降级/漂移红；对 `Directory.Packages.props:79-83` 记载的误删案做变异自测 | M | 低 | — |

### 里程碑 3 —— 质量与润色

| ID | 标题 | 位置 | 工作量 | 备注 |
|---|---|---|---|---|
| M3-1 | `Saga.cs:480` 不可达 return 清除 | `Saga.cs:480` | S | 死代码一行 |
| M3-2 | `IdempotencyProcessor` 毒载荷降级补日志 | `IdempotencyProcessor.cs:205,219-225`（static → 实例或注入 logger） | S | 与 `ProjectionProcessor.cs:114-126` 同型 |
| M3-3 | 通知器裸 catch 补 Warning + OCE 分支纳入退避计数 | `PostgreSqlOutboxNotifier.cs:265-268`、`:180-189` | M | 须一并核查 `_processGate` 泄漏路径是否已被 `:176-178`（Task.Run 不传 token）完全覆盖 |
| M3-4 | EF 批末 flush（消除 N 次 SaveChanges） | `OutboxBatchProcessor.cs:211-215` | M | P-1；契约已允许（前三栈返回 0） |
| M3-5 | Dapper EventLog 多行 INSERT…RETURNING（PG） | `DapperEventLog.cs:112-175` | L | P-3；需 Testcontainers 实证 GlobalPosition 连续性 |
| M3-6 | `maxCount` 默认改有限值 + 显式全量 opt-in | `IEventLog.cs:24,30`、`PalOrmEventLog.cs:195-199` | M | 公共 API 默认值变更，须文档 + CHANGELOG；S-2 |
| M3-7 | 手写转义改参数化 | `PostgreSqlJsonbExtensions.cs:250-251`、`PostgreSqlAuditor.cs:127` | M | 消除 `standard_conforming_strings` 依赖；S-5 |
| M3-8 | CI 加 windows-latest（可先仅 build + 行尾/编码门禁） | `ci.yml:19` | M | 对准 `AGENTS.md §3` 实证过的行尾三犯；O-3 |
| M3-9 | release.yml 缺 key 时 fail + 断言发布包数 | `release.yml:118-143` | S | 复用 `:107-116` 现成模式；O-4 |
| M3-10 | `dialect-probe` 路径过滤去裸词 `Store` | `ci.yml:268` | S | 防改名无声关探针；O-5 |
| M3-11 | `BatchSize` 上界校验 | `Transactions/ServiceCollectionExtensions.cs:36,69` | S | S-6 |
| M3-12 | InMemory 存储标注「仅限测试」 | `docs/usage.md:310,390,438` | S | P-4 的可接受处置 |
| M3-13 | `test/PalDDD.Testing` 提供 `FindRepositoryRoot()` 共享 helper | 8 个测试文件 | S | T-5 |

### 快速获胜（高影响 / S）

`M0-1`（CI 用例数断言）、`M1-3`（陈旧注释清扫）、`M1-5`（空实现抛异常）、`M1-6`（文档脱敏）、`M3-1`（死代码）、`M3-9`（release 断言包数）、`M3-10`（探针路径过滤）、`M3-11`（BatchSize 上界）、`M3-12`（InMemory 标注）、`M3-13`（helper 收口）——**十项合计约 1.5 个工作日，覆盖「静默假绿」类的六成**。

### 前 3 个任务的实现草图

**① M0-1：CI 测试非空断言**

* 做法：在 `ci.yml:64-74` 循环**前** `mapfile -t projects < <(find test -name '*.csproj' ! -name 'PalDDD.Testing.csproj' | sort)`，循环内累计 `ran`；循环后 `[[ ${#projects[@]} -ge 17 && $ran -ge ${#projects[@]} ]] || { echo "::error ::expected >=17 test projects, ran=$ran"; exit 1; }`。
* 陷阱 1：**不要**从日志 grep 用例数——TUnit JSON 报告已落 `**/TestResults/**`（`:79-85`），用 5 行 file-based app 解析 JSON 累计 `total` 才是可靠口径（并登记进 `gate-audit` 的 `probedGates`）。
* 陷阱 2：本仓红线「退出码 0 ≠ 任务成功」（`AGENTS.md §3`）——断言必须读输出内容；`pipefail` 保持在块首（`:65` 现状正确，勿移动）。
* 陷阱 3：硬下界 17 会随项目数漂移，写成 `ran == ${#projects[@]}`（禁静默跳过）更耐久。

**② M1-1：saga 快照 fail-fast**

* 做法：`DapperSagaStateStore.cs:239-240` 从 `is null ? null : Serialize` 改为 `_jsonTypeInfo ?? throw new InvalidOperationException(<复用 PalORM 文案>)`。抛点选**保存期**而非构造期：构造期抛会让 `GetByIdAsync` 等读路径不可用，且 DI 探测成本更高。
* 关键步骤：① 先读 `PalOrmSagaStateStore.cs:205-214` 逐字对齐异常类型与消息（同构契约优先于本地口味）；② 检查 `SerializeState` 全部调用点（`:170`，含 UPDATE/INSERT 两分支）；③ 三栈参数化测试（未注册 → 三栈同型异常）；④ README + `docs/usage.md:300` 升级为 ⚠️ 段；⑤ CHANGELOG 标 breaking。
* 陷阱 1：**行为变更须先落决策文档**——`AGENTS.md §3` 的 M3-3 教训直接适用：先追加 `docs/decisions/`（V11 门禁管其结构），同提交再改代码，否则 pre-commit 的 `verify-conventions` 会拦。
* 陷阱 2：EF 栈 `SagaStateDbContext` 的快照路径是否同源？2026-09-19 审计**未逐行核验 EF 侧 null 分支**——动手前先 grep `_jsonTypeInfo` / `Serialize(` 于 `SagaStateDbContext.cs`，别把「两栈一致」当假设。
* 陷阱 3：`PublicApiSnapshotTests`（`:49-57`）——若异常类型进入签名或 XML 注释，快照需人工确认，禁自更新。

**③ M0-2：FanOut 重放表征测试**

* 做法：`SagaLaneCharacterizationTests.cs` 的 `FanOutLaneSaga` 已有 `failItem` 注入点（`:52`）；扩为 `Interlocked` 计数的 executor + `MaxRetries = 1` + items = [0,1,2]、仅 item[1] 抛。
* 断言：`executedCounts == [2,2,2]`（现状整批重放）**且** `AggregateException.InnerExceptions.Count == 1`。
* 陷阱 1：`SagaState.ExecutedStepKeys` 是步骤粒度、`StepStartedAt` 同理 → **无法**用它断言子项进度（这正是 A-3 需 v3.0 才能真修的根因），别试图用状态字段表达子项。
* 陷阱 2：`FanOutStep.cs:156` 的 semaphore Wait/Release 配对依赖该行位于 try 块**之外**这一结构事实（`:150-155` 有精确声明）——测试若加并发压力，**不要**改动那三行的位置。
* 陷阱 3：外部 `ct` 取消与 PerItemTimeout 在 `:168-185` 分三支，测试须只走「executor 自身抛非 OCE」分支（`:186`），否则测的是取消语义而非重放语义。

---

## 六、开放问题（需人类裁决）

1. **FanOut 的幂等契约归谁？** (a) 文档化「executor 必须自身幂等」并保留整批重试（M1-2，零破坏）；(b) v3.0 引入子项进度记录，重试跳过已完成（真修，需 ADR + 快照格式演进）；(c) FanOut 车道禁用自动重试。建议 (a) 立即做 + (b) 进 ADR-020 清单——但取决于产品对「FanOut 用于支付类副作用」的支持意愿。
2. **`saga_data` 由静默改抛异常是否可接受为 2.x 破坏性变更？** 若不可：走 v3.0（M1-1 延后），还是先加 `LogError` + 一次性 opt-out 开关（折中，代价是临时分支代码）。
3. **InMemory 存储是「测试 fixture」还是「轻量生产选项」？** `[事实]` 当前无 DI 注册（倾向前者），但 `docs/usage.md:310,390,438` 以正常用法呈现。若是 fixture，只补标注（M3-12）；若支持生产，必须加淘汰上界（P-4 升为「高」）。
4. **.NET 11 GA 窗口承诺**：GA 迁移预案（ITM-806 已起头）需明确日期与回归范围，否则 D-1 的「高」会长期挂在台账上。谁在什么时点盯 rc.2/RTM？
5. **AGPL-3.0 定位**（`Directory.Build.props:63`）是否仍符合「被业务团队引用的框架」目标？不是缺陷而是商业约束——若目标用户含闭源商业方，`ADR-020` 的栈收敛决策会受它牵引，宜显式声明。
6. **性能目标量化**：`docs/performance.md` 与 `bench/` 存在，但未见「三栈吞吐差异可接受区间」的判定线（P-1/P-2 的优先级取决于此）。EF 栈比 Dapper 慢多少算 bug、多少算成本？
7. **CI 历史无法本地核验**：`gh` 未认证（`gh run list` 报需 `gh auth login`），故**覆盖率 job 与 aot-verify 在 `fcb5ed3` 上是否真绿、Docker 是否在 ubuntu runner 可用**无法判定。若提供一次 CI run 链接或日志，T-1/T-3 可重新定级。

---

## 附：取证范围与未验证项（诚实声明）

**已复证（审计者亲读源码/命令）**：A-1 至 A-7 全部、P-1 至 P-4、T-1（`ci.yml:64-74` 全文）、O-1（`gate-audit.cs:396-424`）、S-1/S-3（命令实测）、覆盖率账本状态（`docs/test-coverage-baseline.md:1-40`）、git 未推送计数。

**未验证（需后续实证，本报告不据此下结论）**：Dapper.AOT 拦截器是否真正接管全部 `QueryAsync<OutboxMessage>` 形状（未做 PublishAot 实跑）；`SagaProcessor` 租约释放成本的实测值；FanOut 整批重放在真实业务载荷下的影响面（M0-2 完成前只有代码级证据）；`Analyzers`/`CodeFixes` 4 个 provider 是否全部端到端被测（仅见 1 例）；`coverage` job 在近期 run 中是否真绿（见开放问题 7）。
