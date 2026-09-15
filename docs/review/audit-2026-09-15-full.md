# Pal.DDD 全量审计报告（2026-09-15）

> 审计基线：`dev` @ `c908f40`（v2.2.0），工作树干净
> 审计范围：`src/` 214 文件 32,620 行 · `test/` 114 文件 33,237 行 · `docs/` 82 文件 14,826 行 · 37 源项目 · 17 测试项目 · 33 门禁脚本
> 执行方式：只读审计，零代码改动（唯一副作用为验证性 `dotnet build PalDDD.slnx -c Release`，写入 `obj/`）
> 可实施清单：[`docs/review/action-items-2026-09-15-full-audit.md`](action-items-2026-09-15-full-audit.md)
> 口径说明：本报告用**影响**分级（严重/高/中/低）；任务清单用本仓既有的**危害 × 复杂度**矩阵（P0-P3）。两套口径的映射见任务清单表头。

---

## 一、执行摘要

**整体健康等级：B+**（优秀工程纪律 + 一类系统性弱点的组合）

工程质量处于 .NET 开源库的前 5%：`dotnet build` 实跑零警告零错误（`TreatWarningsAsErrors=true` + `AnalysisLevel=latest-all` 条件下）；22 道门禁带隔离式变异探针（能证明仪器会产出错误答案）；AOT 声称有"真发布并运行二进制"的 CI 背书（`ci.yml:176-200`）；测试代码量（33,237 行）超过生产代码量（32,620 行）；安全面未发现可利用漏洞。

扣分不在纪律，在两处纪律覆盖不到的盲区：

1. **"三方一致"这条本仓最高红线在 Dapper AOT 状态上被同时违反三次**。代码（`DapperAotInitializer.cs:33` 已启用）、NuGet 包描述（`PalDDD.Dapper.csproj:7` 写"未启用"）、README 两张表（`:770` 已启用 vs `:790` 未启用）互相矛盾，同一文件的头注释（`:16-19`）还把"启用"写成未来动作。
2. **质量系统以门禁一致性为主轴，对性能画像与处理器内部测试缺口无感知**。其最近一轮自评"0 代码级缺陷"，而本次审计实测到 EF Core outbox 租约路径有可量化的 N+1 退化（5 倍于 Dapper），毒消息死信路径无任何测试覆盖。

**前三风险**

1. **EF Core outbox 租约 N+1 与 SQLite/MySQL 索引缺口**。逐行 `ExecuteUpdateAsync`（`SqliteOutboxDbContext.cs:111-128`）实测 24,043 μs vs Dapper 2,655 μs；租约回读查询 `WHERE locked_by=? AND locked_until=?`（`SqlTemplates.cs:189`）无可用索引，`EXPLAIN QUERY PLAN` 实测全表 SCAN。轮询间隔 500ms，代价随表增长线性上升。
2. **Dapper AOT 三方矛盾**。选型者依据 README AOT 表会排除 Dapper 栈，而该栈的三方言 NativeAOT 二进制实测是 13/13 通过的。消费者的判断依据是错的。
3. **毒消息死信路径零测试**。`OutboxBatchProcessor.cs:100-116` 与 `:154-184` 无测试引用；该路径回归后消息永久重投且套件保持全绿。

**前三机会**

1. **把"事实一致性"机械化**。`csproj` 的 `Description` 与文档中的 AOT 状态同属可 grep 的事实值，现有 `doc-consistency.cs` 不覆盖包描述。补一道检查即可把 #1 红线从人工纪律变成机械强制。
2. **下沉跨栈重复的存储语义**。唯一约束冲突分类器有 10 份私有副本（Dapper 5 + EFCore 5），而 PalORM 已抽出单一 `SqlErrorClassifier.cs`。
3. **outbox 索引与 EF 租约批量化**。两处小改动，把轮询路径从 O(全表) 拉回 O(批大小)。

---

## 二、仓库地图

### 目的与用户

面向 .NET 11 的 DDD/CQRS/Event Sourcing 基础设施框架，以 35 个独立 NuGet 包发布（另依赖 PalORM 引擎 5 个包），AGPL-3.0-or-later，公开仓库 `github.com/yuebo119/PalDDD`。目标用户是构建 DDD 应用的 .NET 团队。卖点：零运行时反射、Native AOT 完整链路、无过度抽象（不做 `IRepository<T>`、不做装配扫描）。

**成熟度：生产级库，不是原型。** 已发至 v2.2.0，版本三件套一致（`Directory.Build.props:60` = CHANGELOG = git tag = README badge），有 22 份 ADR、33 份历届评审、正式发布流程、以及独立版本的 AI 质量系统（`.ai/`）。

### 技术栈

| 维度 | 取值 |
|---|---|
| 语言/运行时 | C# `LangVersion=latest`，`net11.0` 单一目标，`Nullable` + `ImplicitUsings` |
| SDK | `11.0.100-rc.1.26425.128`（预发布；`global.json` 精确钉版 + `allowPrerelease:true` + `rollForward:latestMajor`） |
| 构建纪律 | `TreatWarningsAsErrors=true`、`AnalysisLevel=latest-all`、22 条逐条带理由的 `NoWarn`、`DebugType=embedded` |
| AOT | 三属性全局 true，非兼容项目显式翻 false 并有架构测试断言 |
| 测试 | TUnit 1.66.27 + Microsoft.Testing.Platform，FsCheck 属性测试，Verify.TUnit 快照，Testcontainers 4.15 |
| 持久化 | 三栈并行：PalORM（源生成，真 AOT）· Dapper（Dapper.AOT 调用点级）· EF Core（非 AOT） |
| 序列化/压缩 | System.Text.Json（源生成）+ MemoryPack；BCL 压缩 + NativeCompressions（LZ4/Zstd） |
| 可观测性 | `PalActivitySource` 11 个 Start 方法 + `PalMetrics` 21 个 Counter |

### 架构草图

```mermaid
graph TB
  Core["PalDDD.Core<br/>（零项目引用）"]
  Ser["Serialization"]
  Msg["Messaging"]
  CQRS["CQRS"]
  EL["EventLog"]
  Idem["Idempotency"]
  Proj["Projections"]
  Tx["Transactions<br/>Saga+Outbox+Inbox · 4593 行"]
  PalORM["PalORM + 3 方言"]
  Dapper["Dapper + 3 方言"]
  EFC["5 个 .EFCore 项目"]
  MQ["Messaging.Kafka / RabbitMQ"]
  DI["DependencyInjection"]
  AspNet["Hosting.AspNetCore"]
  Base["Base / Extension / Prompts"]

  Core --> Ser
  Core --> Msg
  Core --> CQRS
  Core --> EL
  Core --> Idem
  Core --> Proj
  Core --> Tx
  Ser --> Msg
  Ser --> Tx
  Tx --> PalORM
  Tx --> Dapper
  Tx --> EFC
  EL --> EFC
  Proj --> EFC
  Idem --> EFC
  Ser --> MQ
  CQRS --> DI
  Tx --> AspNet
  Core --> Base
```

依赖方向正确：`Core` 零项目引用（`ArchitectureBoundaryTests.cs:470-480` 断言），适配器一律向内，无环。

### 关键目录

| 目录 | 一行描述 |
|---|---|
| `src/PalDDD.Core/` | 领域基元：Entity/AggregateRoot/DomainEvent/ValueObject/SmartEnum/ULID/诊断 |
| `src/PalDDD.Transactions/` | 最大项目：Saga 编排 16 文件 + Outbox + Inbox + 后台处理器 |
| `src/PalDDD.Dapper/` | 手写 SQL 三方言适配器，含 46 个 SQL 常量模板 |
| `src/PalDDD.PalORM/` | 源生成零反射适配器，AOT 主线 |
| `src/PalDDD.Core.SourceGen/` | 23 条 PALID/PALMSG/PALENUM 生成器诊断 |
| `src/PalDDD.Analyzers/` | 15 条 PDDD 战略 DDD 分析器（Roslyn） |
| `test/` | 17 项目 / 1256 个 `[Test]`，测试 LOC 超源码 LOC |
| `scripts/` | 33 个 C# file-based app 门禁与工具（0 Python、0 bash 判定体） |
| `docs/decisions/` | 22 份 ADR（含被否决方案） |
| `.githooks/` | 7 道 pre-commit + pre-push，由首次构建自动配置 `core.hooksPath` |
| `.ai/` | **独立嵌套 git 仓库**（主仓 0 跟踪文件），AI 质量系统本体 |

### 意外之处

1. **构建完全干净**：37 项目、`AnalysisLevel=latest-all`、警告即错误，实跑 0 警告 0 错误。
2. **`NoWarn` 有界且有理由**：22 条抑制逐条写明理由，并区分"全局抑制"与"逐点 `[SuppressMessage]`"（CA1031 属后者）。抑制被当作有成本的决策而非消音。
3. **AOT 声称有实跑背书**：CI 对两个 sample 做 `PublishAot` 并运行产物，自带断言与非零退出码。多数宣称 AOT 兼容的库只做编译期检查。
4. **门禁会验证自己**：`gate-audit.cs` 对 32 个脚本做隔离式变异探针；`ci.yml:88-93` 记录了之前 `set -o pipefail` 位置错误导致 secret-scan 实为 no-op 的事故与修复。在一人项目中罕见。
5. **`.ai/` 是独立仓库**：主仓 `git ls-files .ai/` 返回 0，但它承载质量系统本体（含 57KB `lessons.md`）。CI 有降级分支，而 `docs/architecture.md:5` 把它当作仓内目录引用。

---

## 三、审计发现

标注：`[事实]` = 代码/实跑直接验证；`[判断]` = 基于事实的推论。

### 3.1 架构与设计

**A-1　`PalDDD.Transactions` 是三个无关子系统的打包，Saga 的反射决定整个程序集的 AOT 状态　严重　[事实]**

`PalDDD.Transactions.csproj:9-11` 把 `IsAotCompatible`/`IsTrimmable`/`VerifyReferenceAotCompatibility` 全设为 `false`，唯一理由是 Saga 子编排解析的反射（`Saga.cs:650-668` `MakeGenericMethod`、`:670-678` `Activator.CreateInstance`）。Outbox/Inbox 是零反射的（`PeriodicBackgroundProcessor.cs:9` 自述），却被迫继承同一状态。矛盾已显形：`samples/PalDDD.AotSample/PalDDD.AotSample.csproj:8-9` 声明 `IsAotCompatible=true` 且在 `:18` 引用 Transactions。后果：只用 Outbox/Inbox 的消费者拿不到可裁剪程序集；Inbox 消费者还会被拖入 `PalDDD.Messaging`（`OutboxBatchProcessor.cs:24` 用 `IMessageBroker`）。

补充：`Saga.cs` 的反射方法**已带正确的 `RequiresUnreferencedCode`/`RequiresDynamicCode` 标注**，所以程序集级翻 false 更多是保守策略而非技术必需。[判断]

**A-2　`Saga<TState>` 是 1002 行神类，重试/补偿控制流复制四遍　高　[事实]**

`Saga.cs` 1002 行，四个近乎相同的重试车道：`ExecuteNormalStepAsync`（`:396-463`）、`ExecuteFanOutStepAsync`（`:469-547`）、`ExecuteChildSagaStepAsync`（`:555-648`）、`ExecuteDynamicStepAsync`（`:784-902`）。差异只在被执行的主体。注释自承复制：`:528-529`、`:629-630`、`:883-884` 三处写"P3 修复（十七轮）：补偿失败嵌套（见 `ExecuteNormalStepAsync` 同名修复注释）"。另有四个同构 `SafeObserve*` 包装（`:342-394`）。后果：每次修复落四遍，第五种 step 类型即第五份克隆。

**A-3　PalORM 与 Dapper 是两套平行全栈实现，不变量靠人工同步且已漂移　高　[事实 + 判断]**

同一组 6 个 SPI 每栈各实现一遍（`DapperOutboxStore.cs:62` / `PalOrmOutboxStore.cs:30`，依此类推六对）。重复不变量有据可查：`leaseDuration must be greater than zero` 出现 9 次、`ThrowIfNegativeOrZero(batchSize)` 22 次、`FailureReason.Truncate(...,2040)` 6 次。最尖锐的证据是唯一约束冲突分类器有 **10 份私有副本**（Dapper 5 + EFCore 5），而 PalORM 已抽成单一 `SqlErrorClassifier.cs`。`PalOrmOutboxStore.cs:75,100-104` 与 `DapperOutboxStore.cs:124-130` 的注释互写"对齐另一方"；`Repository.EFCore/UnitOfWork.cs:36` 直白记账"ITM-284：三栈 disposed 守卫对齐（PalOrm 3/3、Dapper 1/3、EFCore 原 0/3）"。

判据：这是**有意的多态持久化策略**（ADR-020 已裁决 Dapper 转能力平等栈），代价可接受的前提是两栈保持功能与修复等价。目前不等价（见 A-4），代价在支付。

**A-4　同一组 SPI 在三栈的 DI 完整度与功能面严重不对称　高　[事实]**

Dapper 只注册四个：`DapperServiceCollectionExtensions.cs:106`（Outbox）、`:107`（Inbox）、`:108`（Idempotency）、`:112`（`ISagaStateStore<>`）。`DapperEventLog`、`DapperProjectionCheckpointStore`、`DapperUnitOfWork` 三个类**确实存在却无任何 DI 注册路径**（全仓 grep 无对应 `AddScoped`）。PalORM 三个方言扩展全部注册 6 个 store + UoW（`SqlitePalOrmExtensions.cs:104-116`）。

仓库自己知道：`DapperAmbientTransaction.cs:10-13` 写"仅 Outbox/Inbox/SagaState 三者有 DI 注册；EventLog/ProjectionCheckpoint 需直连构造"。但代码注释知道不等于消费者知道，且无编译期信号。功能面同样不对称：分片、读写路由、多主机故障转移、LISTEN/NOTIFY、审计、软删除、JSONB、FTS 全部只在 Dapper 方言包；`PalDDD.PalORM.PostgreSql` 217 行 vs `PalDDD.Dapper.PostgreSql` 2866 行。

**A-5　栈中立的驱动层设施被锁在 Dapper 包内　中　[事实]**

`PostgreSqlMultiHost.cs` 625 行，`using` 只有 `Microsoft.Extensions.DependencyInjection` + `Npgsql`（`:31-32`），零 Dapper API；`PostgreSqlSharding.cs`、`PostgreSqlReadWriteRouter.cs`、`PostgreSqlAuditor.cs`、`PostgreSqlSoftDelete.cs`、`MySqlMultiHost.cs`、`SqliteFtsExtensions.cs` 同理。这些是驱动级而非 ORM 级能力，EF Core 与 PalORM 用户无法使用。

**A-6　Dapper 核心包耦合全部三个 ADO.NET 驱动　中　[事实]**

`PalDDD.Dapper.csproj:28-30` 在核心程序集引用 Npgsql、MySqlConnector、Microsoft.Data.Sqlite，方言靠运行时枚举分派。用 SQLite 的消费者被迫引入全部三个驱动。PalORM 做对了相反的事：核心零驱动引用（`PalDDD.PalORM.csproj:27-37`），provider 独立包 + 编译期特化。

**A-7　Inbox 与 Idempotency 是同一机制在两个项目里实现两遍　中　[事实 + 判断]**

`Inbox/InboxStore.cs:19` 的 `TryStartProcessingAsync/MarkProcessed/MarkFailed` 与 `Idempotency/IIdempotencyStore.cs:10` 的 `TryStartAsync/MarkCompleted/MarkFailed` 语义逐行对应；处理器同样镜像（`InboxProcessor.cs:150-153` 与 `IdempotencyProcessor.cs:96-99` 都把标记失败挂 `Data["MarkFailedError"]`）。每栈实现两份。保留两个公开 API 合理，但租约/状态/持久化机器可共享一个内部引擎。

**A-8　复杂度热点　中　[事实]**

| 方法体行数 | 最大嵌套 | 位置 |
|---:|---:|---|
| 151 | 6 | `Transactions/Outbox/OutboxBatchProcessor.cs:58` `ProcessBatchAsync` |
| 146 | 7 | `Transactions/Saga/SagaProcessor.cs:110` `CheckTimeoutsAsync`（5 层 try/foreach 嵌套，`:134-243`） |
| 365 | 4 | `Core.SourceGen/EnumGenerator.cs:137` 等生成器 `Initialize`（生成器代码，可接受） |

前两处是**已发布业务逻辑**中最差的可维护性位置。

**A-9　死公共面与陈旧元数据　低　[事实]**

`SqlTemplates.cs` 暴露 31 个 `public const` SQL 字符串，其中 `:115-119`、`:164-168` 注解自述"当前无内部引用"。框架库保留公共 API 合理（本仓"代码价值判定"规则要求不得仅凭无引用判定死代码），但注解措辞应改为面向外部使用者的契约说明。

### 3.2 代码质量

**C-1　Dapper AOT 状态三方矛盾，违反本仓最高红线　严重　[事实]**

| 事实源 | 声称 | 位置 |
|---|---|---|
| 代码 | **已启用** | `DapperAotInitializer.cs:33` `[module: DapperAot]`（`a1f3073` 引入，确认为 `dev` 祖先） |
| 权威文档 | **已启用**，34 调用点，三方言 13/13 | `docs/persistence-aot-status.md:10-14` |
| README 功能矩阵 | **已启用** | `README.md:770` |
| README AOT 表 | **未启用，"AOT 假象"** | `README.md:790` |
| NuGet 包描述 | **未启用，"AOT 假象"** | `PalDDD.Dapper.csproj:7` |
| 同文件头注释 | 启用是**未来动作** | `DapperAotInitializer.cs:16-19` |

最刺眼的是 `DapperAotInitializer.cs` 自身：头注释把"未启用"当作现状，第 33 行却已启用。而 `csproj:7` 是消费者在 nuget.org 读到的原文。这是本仓 `AGENTS.md` §1 明列"三方一致"红线的违反，且现有机械门禁不覆盖此形态：`doc-consistency.cs` 只看 DDL 与文档，不看 csproj `Description`。

**C-2　注释中大量变更史考古，且存在与代码矛盾的注释　中　[事实 + 判断]**

注释/代码比：`Saga.cs` 315/1002（31%）、`DapperOutboxStore.cs` 177/391（45%）、`PalOrmOutboxStore.cs` 151/349（43%）。相当比例是叙事型（"v43 P3（ITM-092 管线孪生）"）、同一解释在 3-4 个姊妹文件重复、以及与代码状态不符者（C-1 即一例）。后果：diff 噪声大，机制性重构（如合并 A-2 的四条车道）成本被显著抬高。[判断：这是风格选择，但它是 A-2 重构的主要阻力]

**C-3　少数测试无实质断言　低　[事实]**

`MessagingTests.cs:246-251`、`:253-262`、`:274-282`；`RepositoryEfCoreTests.cs:28-38`、`:40-48`、`:50-57`；`DapperUnitOfWorkTests.cs:106-112`；`MessageEvolutionTests.cs:140-165`。仓库自测口径为 1256 个测试中 30 个零断言（含 10 个方言探针委派壳与 9 个 FsCheck 属性测试），实际无断言者约 11 个，集中于 NullObject/幂等契约类。整体断言文化很强。

### 3.3 安全

**S-1　`secret-scan` 是唯一凭据控制，检测面偏窄　中　[事实]**

门禁本身设计负责（只扫已跟踪文件、只打印 `file:line:pattern` 不回显凭据、16 例自测经变异验证），但作为仓库唯一凭据防线，假阴性面偏大（`scripts/secret-scan.cs`）：

- 扩展名白名单（`:49`）不含 `.pem`/`.key`/`.pfx`/`.sql`/`.razor`/`.resx` 与无扩展名文件。提交 `deploy/signing.pem` 可完全绕过 mode-1 私钥检测器。
- 连接串正则要求 `Host|Server|Data Source` 与 `Password|Pwd` **同行**。多行 JSON/YAML、`.env` 形态、`postgres://user:pass@host/db`、`AccountKey=` 全部不可见。
- 白名单 3（`:167`）豁免"无数字且长度 < 10"的密码值；白名单 1（`:144`）对整值做 `example|sample|demo|...` 子串匹配，`S3cr3t-demo-key-9x` 这类真实凭据会被豁免。
- key 前缀覆盖窄：`sk-[A-Za-z0-9]{20,}` 漏 `sk-proj-`/`sk_live_`；GitHub 令牌只认 `ghp_`；无 Google `AIza`、无 AWS 40 字符 secret、无 JWT（`eyJ`）。
- 无熵启发式；仅扫已跟踪文件，故最可能存放真实凭据的本地文件按设计不在范围内。

**S-2　稳定版包依赖 RC 框架包，包审计警告全局抑制　中　[事实]**

`global.json:3` 钉 RC SDK；`Directory.Packages.props` 中 `Microsoft.EntityFrameworkCore*`、`Microsoft.Extensions.*`、`System.*`、`Microsoft.Data.Sqlite` 全部为 `11.0.0-rc.1.26425.128`；`Directory.Build.props:45` 把 `NU1900-NU1904` 与 `NU5104`（字面意思即"稳定包依赖预发布"）加入 `NoWarn`。后果：消费者无法通过正常构建获得这些包的 CVE 警告；从 RC 依赖线发布的包无法靠 GA 补丁服务而不升版本。

**补偿控制真实存在**：CI 跑 `dotnet list package --vulnerable --include-transitive` + `scripts/vuln-scan.cs`（`ci.yml:38-41`），该脚本经历过"exit-0 no-op 假绿"事故并修复 + 加探针；NuGet 审计配置为 `auditMode: all`、`auditLevel: low`。属"有多层补偿的有意权衡"，非疏漏。

**S-3　认证/授权完全无文档，样例是开放 API　中　[事实 + 判断]**

`src/` 无任何认证/授权原语（grep 仅命中一处文档注释 `HealthCheckExtensions.cs:24`）。委托宿主是正确的库设计。问题两点：`MapCommand`/`MapQuery` 返回 `IEndpointConventionBuilder`，调用方**可以**链 `.RequireAuthorization()`，但无策略参数/重载，也无测试证明该路径可用；且 `README.md` 与 `README.en.md` 对"认证/授权/鉴权"的提及数均为 **0**。默认 `/health` 返回组件拓扑与依赖状态，两个样例（`samples/PalDDD.MinimalApi/Program.cs:29-35`、`samples/PalDDD.ECommerce/Program.cs`）暴露无鉴权写端点。后果：照抄文档样例组装出的应用是完整的开放命令/查询 API。

**S-4　本地凭据文件与 job 级长生命周期发布密钥　低　[事实]**

`appsettings.test.local.json`（未跟踪，`.gitignore:94` 覆盖，`git check-ignore -v` 确认）含内网主机与真实形态口令。当前卫生正确，但距泄漏只有一个 `.gitignore` 编辑，而 secret-scan 按设计不看未跟踪文件。`release.yml:28` 把 `NUGET_API_KEY` 注入 job 级 env，使 restore/build/test/运行已发布二进制等步骤都带着可发布任意版本的组织级凭据；无 `id-token: write`，未采用 NuGet OIDC 可信发布。`permissions` 已最小化（`contents: write`），dispatch 输入已用 env 间接化加固。

**S-5　手动转义与原始 SQL 片段 API（设计脚枪，已文档化）　低　[事实]**

`PostgreSqlAuditor.cs:112,127`、`PostgreSqlJsonbExtensions.cs:53,64`、`SqliteJsonExtensions.cs:120,140`、`SqliteFtsExtensions.cs:102,154` 用手写引号加倍转义。对当前方言正确（PG `standard_conforming_strings=on`；SQLite 无反斜杠转义），但复用路径一旦换到 MySQL（反斜杠语义）就会静默变可注入。`PostgreSqlSoftDelete.Delete/Restore`（`:36-59`）接受调用方 SQL 片段并逐字插值进 `WHERE (...)`，XML 文档已警告。无当前可利用点。

**S-6　`MapQuery` 的调用方绑定异常逃逸为 500　低　[事实]**

`EndpointExtensions.cs:200-206` 的文档自述：任何来自 `bindQuery` 且非 `PalValidationException` 的异常会以 500 逃逸。样例正落入此坑：`samples/PalDDD.MinimalApi/Program.cs:35` 调 `Guid.Parse((string)ctx.Request.RouteValues["id"]!)` 且路由无约束，故 `GET /orders/xyz` 返回 500。无数据泄漏（`ExceptionMiddleware` 返回固定 body），但错误分类错误。

**安全维度优势（本仓最强的一维）**

- SQL 注入面干净：全部 SQL 是 `SqlTemplates.cs` 编译期常量 + Dapper 参数对象；PalORM 侧强制 `FormattableString` 绑定（`PalOrmOutboxStore.cs:64` 注释说明字符串拼接会退化）；EF 侧 `FromSqlRaw` 用 `{0}` 占位符绑定（`PostgreSqlOutboxDbContext.cs:41`），`EF1002` 抑制写明理由；标识符输入有白名单（`DapperBulkCopy.cs:421-452`、`PostgreSqlSharding.cs:311-316`）。全仓无 `string.Format`-进-SQL、无 `sql +=` 累积。grep 插值 Dapper 调用（`QueryAsync($"` 等）零命中。
- 反序列化干净：全部 JSON 走源生成 `JsonTypeInfo`，`JsonSerializerIsReflectionEnabledByDefault=false` 全局关闭；`MessageDescriptor.cs:42` 校验 `JsonTypeInfo` 与 CLR 类型匹配；消息类型解析从不用线上传来的类型名（`MessageBrokerBase.cs:47` 用编译期 `typeof(TMessage)`），改用预建 `FrozenDictionary` 目录。唯一 `Activator.CreateInstance`（`Saga.cs:674`）限制在已注册子状态类型且带标注，不可由线输入到达。
- TLS/加密干净：全 `src/` 无 `TrustServerCertificate=true`、无 `SslMode=None/Disable`、无证书校验绕过回调。库内无密码哈希故无算法误用；唯一 `Random.Shared` 是重试抖动并带理由（`RetryBackoffPolicy.cs:75`）。
- 错误面不泄漏：`ExceptionMiddleware` 返回固定 ProblemDetails；`JsonException` 消息被刻意泛化；连接串被刻意排除在异常消息外，且有测试锁定（`PostgreSqlRouterTests.cs:14-48` 断言消息不含口令）。
- 供应链：`nuget.config` 只留 nuget.org；workflows 无 `pull_request_target`；`git ls-files` 确认无 `.env`/`.pem`/`.pfx`/`.key`/`.snk`/`.db` 被跟踪。

### 3.4 测试

**T-1　毒消息死信路径零覆盖　高　[事实]**

`OutboxBatchProcessor.cs` 四条关键失败分支无测试引用：

| 分支 | 位置 | 后果 |
|---|---|---|
| 类型未在 `MessageCatalog` 注册 → `MarkDead` | `:100-108` | 回归后消息永久重投 |
| 反序列化返回 null → `MarkDead` | `:110-116` | 同上 |
| `MarkDead`/`ReleaseForRetry` 自身抛异常 → 挂 `Data["MarkError"]` | `:160-168`、`:174-182` | 标记失败被静默吞掉 |
| backoff 策略抛异常 → 1s 兜底 | `:142-150` | 未验证 |

假阳性排除：grep `Deserialization returned null` 全仓只命中 `src/` 定义处，测试中零命中；grep `not registered in MessageCatalog` 在 `test/` 命中 2 处，但都在 `AotContractTests.cs:397` 与 `SerializationTests.cs:208`，断言的是 catalog 查询错误消息，与处理器死信路径无关。store 层 `MarkDead` 有测试（`DapperStoreTests.cs:423,461`、`PalOrmOutboxStoreTests.cs:59`、`InMemoryStoreTests.cs:91`），**处理器层没有**。

**T-2　`AssertionStrengthGateTests` 是合理的防退化棘轮，但不是断言强度验证器　中　[事实 + 判断]**

好的部分：带过期说明的棘轮常量（`:29`）、四种注入样本的负向自检（`:69-149`）、等长空白化保留偏移的注释清洗（`:240-306`）。

弱点：检测器把任何 `Assert\w*[.(]` 调用当断言（`:193`），故 `Assert.That(true).IsTrue()` 通过且什么都没验证；不分析断言操作数。门禁自己的测试有两处恒真断言（`:59-60` 的 `isNotNullCount >= 0`、`zeroAssertMethods.Count >= 0`）。预算余量：声明实际 185 对上限 200（`:29`），可再加约 15 个弱断言才触发。信号双向稀释：零断言桶约 2/3 其实行为很强。盲区：`s_testMethodStart` 要求参数表后跟 `{`（`:197-199`），表达式体 `[Test]` 方法永不被检查（当前 0 个，无实际漏检）。

**T-3　覆盖率降幅门禁缺 PalORM.Tests，恰好是最依赖 Docker 的项目　中　[事实]**

`coverage-baseline.json` 有 15 个键（实测内容），`PalDDD.PalORM.Tests` 不在其中（grep `PalORM` 返回 0）。这正是本机无 Docker 时 46 项跑不完的项目，降幅门禁最该保护的对象反而无保护。

**T-4　挂钟延迟测试未用仓内已有的 `FakeTimeProvider`　中　[事实 + 判断]**

`OutboxProcessorTests.cs:38,60,79,83,101,126` 与 `SagaProcessorTests.cs:52,73,93,97,118` 等待 100-400ms 并断言 `>= 2` 次租约调用，注释记录了过往 CI 假红（ITM-278）。`FanOutStepTests.cs:29` 依赖 500ms 任务对 200ms 超时（2.5 倍余量）。仓库自己提供 `FakeTimeProvider.AdvanceNowAndTriggerTimers` 并在 `InMemoryStoreTests.cs` 与 Core 测试中使用，但上述三处没用。属"已有正确工具却未采用"的摩擦。

**T-5　Kafka 错误路径远薄于 RabbitMQ；Docker 依赖导致本地半矩阵不跑　中　[事实]**

Kafka 仅 3 个测试（往返、取消、多消息），无投毒消息/死信/重试测试；RabbitMQ 有死信与不可路由发布失败测试（`BrokerIntegrationTests.cs:586,702`）。12 处 `Skip.Test`，broker 测试在 Docker 不可用时跳过（`:307-309`、`:320-331`）。

**测试维度优势**

- 元门禁是真的且自验证：`DiagnosticCoverageGateTests.cs:36-100` 用 Roslyn 语法树强制 38 条诊断都有断言级覆盖并含红/绿边界矩阵；`TechDebtGuardTests`、`TestGateGuardTests`、`DocConsistencyGateTests` 各自用注入的红/绿样本测试自己的检测器。
- `PublicApiSnapshotTests.cs:49-63` 在检测到 `CI`/`GITHUB_ACTIONS` 时**拒绝**重写 API 基线，是真门禁而非便利开关。
- 确定性并发验证而非 sleep：两 Context EF 乐观锁冲突（`SagaEfCoreConcurrencyTests.cs:21-50`）、outbox 租约 fencing（`OutboxSqliteConcurrencyTests.cs:104-166`）、10 worker 租约竞争含重复检测、SQLite `busy_timeout=5000`。
- 错误路径有密度且诚实：246 个 `Throws*` 断言分布在 63/114 文件；测试带 ITM 编号与回归引用；多处显式记录并验证前身盲区（`IdempotencyTests.cs:432-493`）。
- 契约级测试超出行为级：`SourceCodeGuardTests`（Roslyn 源码策略）、`AotContractTests`、`AllocationContractTests`、公共 API 快照。15 条分析器规则与 CodeFix 全覆盖。

### 3.5 性能

**P-1　EF Core SQLite outbox 租约是 N+1，实测 5 倍于 Dapper　高　[事实]**

`SqliteOutboxDbContext.cs:108` 读候选页，`:111-128` **对每一行**发一条 `ExecuteUpdateAsync`。批 100 即 100 次 UPDATE 往返。仓库存档基准：`Outbox_Lease_Batch100` = **24,043.8 μs / 3,681 KB**（EF）vs 2,655 μs / 860 KB（Dapper，Ratio 5.04）vs 3,546 μs（PalORM），是其自身 `Outbox_GetPending_Batch100`（499.6 μs）的 **48.8 倍**。代码注释解释了为何从"SELECT 跟踪 → 内存改 → SaveChanges"改成逐条 CAS（两实例可同时租约同一批的正确性修复），所以这是**正确性换性能的有意取舍**，但批量化未被尝试。

**P-2　EF SQLite pending 查询迭代分页并 materialize 整表　高　[事实]**

`SqliteOutboxDbContext.cs:51-69` 的 `while (result.Count < batchSize)` 配 `Skip(skip).Take(batchSize)`，时间过滤在**内存**中做（EF SQLite 无法翻译 `DateTimeOffset` 有序比较）。当表头是未来重试行或活跃租约行时，每次 tick 发 ceil(表大小/批大小) 条 SELECT 并把整表物化。代码注释 `:32-37` 自承"worst case full-table paged scan"。

**P-3　缺 `(locked_by, locked_until)` 索引，且排序无覆盖索引　高　[事实]**

`SqlTemplates.cs:189` 的租约回读是 `WHERE locked_by=@owner AND locked_until=@until`（用于 `DapperOutboxStore.cs:183-188`，PalORM 同款 `PalOrmOutboxStore.cs:145-147`）。DDL 只有 `idx_outbox_status (status,next_attempt_at,locked_until)` 与 `idx_outbox_created (created_at)`（`docs/sql/sqlite/000_schema.sql:24-25`，MySQL `:20-21`，PG `:21-22`）。该谓词无可用索引。同时 pending 查询的 `ORDER BY created_at` 不被 `idx_outbox_status` 覆盖，需临时 B-tree 排序。框架不提供清理机制，Processed 行留存，代价随运行时长线性增长。PG 路径用 `UPDATE ... RETURNING` 不受影响。

**P-4　EventLog 逐事件 INSERT　中　[事实]**

`DapperEventLog.cs:111-138` 每事件一条 `QuerySingleAsync<long>`（还有每事件一个 `new DynamicParameters()` 与 `Payload.ToArray()`/`Metadata.ToArray()` 两次拷贝）；`PalOrmEventLog.cs:109-136` 每事件一次 `Session.InsertAsync`。EF 姊妹实现是批量（`EventLogDbContext.cs:223-233`）。`PalOrmEventLog.cs:97` 注释说明批量不可行的原因是每行需返回 GlobalPosition，属设计约束，但代价真实。

**P-5　Projection 每事件 2 次 checkpoint DB 操作　中　[事实]**

`ProjectionProcessor.cs:73-79`（`TryStartAsync`）与 `:109`（`MarkCompletedAsync`）；replay 驱动 `ProjectionRebuilder.cs:103-115`。重建 100 万事件流的投影会发约 200 万条 checkpoint 语句。逐事件持久化是有意的 at-least-once 选择，但无批量检查点模式。

**P-6　PalORM outbox store 的 sync-over-async 阻塞线程池　中　[事实]**

`PalOrmOutboxStore.cs:160,196,203,239,244,307,310` 使用 `Session.ExecuteAsync(...).AsTask().GetAwaiter().GetResult()`；方言 DI 工厂同样（`SqlitePalOrmExtensions.cs:72,156`、`PostgreSqlExtensions.cs:65,148`、`MySqlExtensions.cs:64,147`）。根因是 `IPalOutboxStore.Mark*` 签名为同步。outbox 循环每批 100 条即 100 次线程池等待。已登记为 open-items B2，等 v3.0 窗口。

**P-7　EF 模型索引与手写 DDL 漂移　中　[事实]**

EF outbox 模型声明 `(Status, NextAttemptAt, CreatedAt)`（`OutboxDbContext.cs:370`），DDL 是 `(status, next_attempt_at, locked_until)`。EF Inbox 声明 `ProcessedAt`、`Status`、`(Status, ProcessingStartedAt)`（`InboxDbContext.cs:212-214`），DDL 中不存在。从 `docs/sql` 部署的 Dapper/PalORM 用户与 EF 用户拿到不同索引集。

**P-8　Dispatcher 每请求分配　低　[事实]**

`Dispatcher.cs:201-213` 每请求 `GetServices<...>().Select(...).ToImmutableArray()`（新数组）+ `new PipelineStateMachine()`（约 40B）；每 behavior 每请求一个 `Func` 委托（`PipelineStateMachine.cs:69-77`）；`ValueTask<object?>` 桥接使值类型响应装箱（`CommandHandler.cs:53-61`）。注释已自我修正早期"零分配"说法，属已知债务。

**P-9　其余低优先级　低　[事实]**

GZip/Deflate 压缩用无容量 `MemoryStream` + `ToArray()` 全量拷贝（`SystemCompressor.cs:131-139`、`:184-192`），Brotli 路径已避免；内存适配器无界增长（`InMemoryOutboxStore.cs:27` 不删除、`InMemoryInboxStore.cs:22`、`InMemoryEventLog.cs:17-18`），属 dev/test 面；`DefaultSagaManager.cs:36,148` 的 `_invalidated` 单例持续增长（注释自述"只增不减"，有意取舍）。

**性能维度优势**

- PG 租约与 saga 路径用单语句 `UPDATE ... RETURNING` + `FOR UPDATE SKIP LOCKED`（`SqlTemplates.cs:147-152,174-180`），无锁竞争，常量 SQL 利于预备语句。
- 批量插入快路径齐备：PG COPY、MySQL BulkCopy、SQLite 多值插入（`DapperBulkCopy.cs:143-160,246-299`）。
- 序列化与压缩有池化：`JsonMessageSerializer.cs:225-254` 用 ThreadStatic + `ArrayBufferWriter`/`Utf8JsonWriter`；Brotli 与解压守卫用 `ArrayPool`（`SystemCompressor.cs:63-72`、`DecompressionGuard.CopyWithLimit`）；解压炸弹有上限。
- 批处理与轮询配置合理且有启动校验：`TransactionOptions.cs:29-30,42,54,80`（批 100、租约 2 分钟、轮询 500ms、重试上限 10），`ServiceCollectionExtensions.cs:19-30` 拒绝非正值；`PeriodicBackgroundProcessor.cs:45` 用 `PeriodicTimer`，无忙等。
- 无 `async void`、无 `Thread.Sleep`、无裸 `.Result`/`.Wait()`（唯一 `.Result` 在 `CommandHandler.cs:57` 与 `DefaultSagaManager.cs:222`，均在 `IsCompletedSuccessfully` 或 `await` 之后）。
- 幂等 store 用 `AsNoTracking` + `Revision` 并发令牌 + `(ExpiresAt)`/`(Status,LockedUntil)` 索引，ChangeTracker 增长已在各退出路径 `Detach`。

### 3.6 依赖

**D-1　无 lock file　中　[事实]**

全仓 `find -name packages.lock.json` 返回 0，`RestorePackagesWithLockFile`/`RestoreLockedMode` 未在任何 props/targets 设置。叠加 RC 线浮动 + `rollForward: latestMajor` + `allowPrerelease: true` 的弱钉版，还原不可复现。CI 不强制 locked mode。

**D-2　AGPL-3.0-or-later 依赖 AGPL-3.0-only　中　[事实 + 判断]**

项目 `PackageLicenseExpression` 为 `AGPL-3.0-or-later`（`Directory.Build.props:63`），而 PalORM.Core/SourceGen/Sqlite/PostgreSql/MySql 5.5.1 的 nuspec 声明的许可证是 `AGPL-3.0-only`（作者同为 "PalDDD"）。组合"only"组件意味着合并作品不能在 `or-later` 条款下传递，包元数据承诺过宽。两者都是 AGPLv3，无分发阻断，但表达式应统一。其余依赖全部宽松且 AGPL 兼容，未发现 GPLv2-only 陷阱。

**D-3　`Microsoft.Data.SqlClient 7.0.2` 是零引用的中央声明　中　[事实]**

`Directory.Packages.props:53` 声明，但全仓 csproj 无任何 `PackageReference` 使用它（EF SqlServer provider 已按文件自身注释移除）。

**D-4　`Microsoft.NETCore.Platforms 8.0.0-preview.7`　中　[事实]**

一个 2023 年的预览版包被留作传递依赖钉扎，跨越两个主版本，属不可审计依赖。

**D-5　`PalDDD.Transactions` 欠声明 `Microsoft.Extensions.*`　中　[事实]**

代码使用 `IOptionsMonitor`/`BackgroundService`（`OutboxBatchProcessor.cs:5,29`、`PeriodicBackgroundProcessor.cs:12-13`），但 csproj 无对应 `PackageReference`；打包后的 nuspec 也确认缺失。纯类库消费者需自行补 `Microsoft.Extensions.*`，本地被 SDK 包裁剪机制掩盖。

**D-6　`PalDDD.Base` 的分析器承诺未兑现　中　[事实]**

打包 nuspec 声明对 `PalDDD.Analyzers`/`.CodeFixes`/`Core.SourceGen` 的依赖，`include` 白名单为 `Runtime,Compile,Native,ContentFiles,BuildTransitive`，**不含 `Analyzers`**，而这三个包只含 `analyzers/dotnet/cs/*.dll`。因此安装 `PalDDD.Base` 的消费者拿不到分析器与源生成器，与包描述（"含编译时分析器"）矛盾。这是 README 主推卖点（"38 条编译期诊断"）的实际交付缺口。

**D-7　其余依赖卫生　低　[事实]**

`nupkgs/` 内容陈旧混杂（顶层 1.1.0 与 `preview-2.1.0/`，当前版本 2.2.0；含 5 个 5.1.0 的 vendored PalORM 包而构建用 5.5.1）；2.1.0 时代 18 个包的 nuspec `description` 为占位符 `Package Description`；`PalDDD.Compression.Native` 已发布但除自身测试外无引用且描述仍写已移除的 OpenZL；若干 `System.*` polyfill 在 net11 上为 in-box。

**依赖维度优势**：第三方版本全部为当前最新稳定版（Dapper 2.1.79 / Dapper.AOT 1.1.0 / PalORM 5.5.1 / MemoryPack 1.21.4 / Npgsql 10.0.3 / MySqlConnector 2.6.2 / Confluent.Kafka 2.15.1 / RabbitMQ.Client 7.2.2），CHANGELOG 记录了 SQLitePCLRaw 的 NU1903 高危通告通过升级 PalORM 5.5.1 清偿。`nuget.config` 清理继承源只用 nuget.org。

### 3.7 开发体验与运维

**X-1　新人入门路径断裂　高　[事实]**

无 `CONTRIBUTING.md`。`README.md` 全文无 clone/build/SDK 前置说明（只有 NuGet 消费段，`:59-119`）。唯一的开发环境文档是 `docs/development.md:9` 一行".NET SDK 11.0.x (RC1+)"，而 `global.json:3` 钉的是**精确预发布版本** `11.0.100-rc.1.26425.128`；README 的文档表（`:863-879`）**未收录** `docs/development.md`，也无 AGENTS.md 链接。后果：新贡献者按 README 找不到"如何本地构建/跑测试/装钩子"；装 .NET 10 或等 .NET 11 GA 的人会先撞 SDK 解析失败，而错误信息不会告诉他要装 RC SDK。

一致性风险：CI 与 release 的 `dotnet-version: '11.0.x'`（`ci.yml:28,161,214,268`、`release.yml:52`）均未带 `dotnet-quality: preview`，与预发布钉版口径不一致。[判断：无法离线验证 setup-dotnet 的实际解析行为，标记待核实]

**X-2　已删除的脚本仍被文档当作可执行命令引用　中　[事实]**

`docs/conventions.md:825` 引 `ci-coverage.sh`；`docs/release.md:436` 引 `gate-check.sh`、`:651` 引 `changelog-facts.sh`、`:693` 引 `changelog-check.sh`。实际已全部 C# 化（CHANGELOG 2.2.0 记录"删除全部 8 个 `.sh`/`.py`"），真实文件是 `ci-coverage.cs`、`changelog-facts.cs`、`changelog-check.cs`、`gate.cs`。`release.md:665` 自己写的是正确命令，同一文档内前后矛盾。V9 守卫只查 `bash X.sh` / `dotnet run X.cs` 形态，反引号/表格形态是它自述的已知盲区（`verify-conventions.cs:161-167`）。

**X-3　`docs/conventions.md` 的 CI 接线状态表两个方向都说反　中　[事实]**

`:1030` 说测试覆盖"脚本就绪，接线待阈值校准"，实际覆盖率 job 已于 2026-09-14 接入 CI（`ci.yml:202-235`，阈值 0.70）。`:1035` 说性能契约 `--smoke` 烟测挂在 CI，而 `--smoke` 在整个 `.github/workflows/` 中零命中，并未挂 CI。

**X-4　33 个脚本无可发现性入口　中　[判断]**

`scripts/` 有 32 个 `.cs`（19 个带 `--selftest`），无 README、无索引。每个脚本头部自述用法与退出码（如 `gate.cs:1-12`），是有效缓解，但"有哪些脚本、各自职责、如何跑全量"需要逐文件读。

**X-5　`.ai/` 是独立仓库却被架构文档当作仓内目录　中　[事实]**

`docs/architecture.md:5` 声称 ".ai/ 目录内嵌统一质量体系 v2.1，详见 `.ai/README.md`"。实际 `.ai/.git` 存在（独立嵌套仓库），`git ls-files .ai/` 返回 0，fresh clone 不存在。且本工作树中 `.ai/scripts/` 只有 2 个文件，而 `.ai/README.md:139-140` 描述 20 个脚本并给出 `bash .ai/scripts/verify-ai-system.sh` 入口，该文件不存在。CI 有降级分支（`ci.yml:114-142`）所以构建不坏，但文档引用在 fresh clone 中不可解析。

**X-6　性能文档停留旧的 Preview 时代，且新基准不可发现　中　[事实]**

`README.md:802` 称"BenchmarkDotNet 在当前 .NET 11 Preview 工具链下存在兼容问题，正式基准报告待 BDN 发布兼容版本后补充"，`docs/performance.md:3` 环境仍写 `SDK 11.0.100-preview.5`。而 BDN 0.15.8 已在 RC1 上跑出 15 项真库基准（`docs/review/bench-baseline-2026-09-13.md`，`bench/PalDDD.Benchmarks/Program.cs:14-30` 有 `--persist` 入口）。README 把完整数据指向 `docs/performance.md`，该文档既不含新基线也不链接它。

**X-7　低优先级运维项　低　[事实]**

pre-commit 最坏路径约 30-35 秒（`guard.cs` 8 个守卫约 22 秒，仅 `.cs` 入暂存时触发），纯文档提交约 4-5 秒；钩子由 `Directory.Build.targets:31-53` 首次构建自动配置，不覆盖既有配置、无 `.git` 时跳过，是明显加分项。注释类小错：`Directory.Build.targets:7` 称 pre-commit"5 道守卫"（实为 7 道）；`ci.yml:84` 称 `.gitignore` 第 95 行（实为 68 行）。`scripts/changelog-check.cs` 在 `docs/release.md:693` 被声明为"必须 0 FAIL"，但 CI 与 release workflow 均未调用（grep 零命中），该门禁纯手工。

**DX 维度优势**

- 钩子自动安装（构建即生效）解决了"新 clone 无钩子"的历史缺口，错误信息可行动（dapper-param-guard 提示改 `(int)` 强转，test-change-guard 给出豁免变量）。
- CI 对失败项目做自诊断，把 TUnit JSON 报告解析成 `::error` 注解，公开 API 可读，无需下载日志即可定位失败测试名。
- CI 在无 `.ai` 时降级为 `gate-lite` 而非必然失败，说明作者真实考虑过外部 clone 场景。
- `docs/development.md` 本身质量高（环境、钩子、MTP 测试协议、CI 匿名注解诊断、Dapper 参数敏感清单），问题只是没被 README 发现。

### 3.8 文档

**DOC-1　README 内部与代码三方矛盾（Dapper AOT）　严重　[事实]**

见 C-1。同一发现的两个视角：文档维度是准确性缺陷，治理维度是红线违反。

**DOC-2　快速开始样例引用不存在的类型，照抄不编译　高　[事实]**

`README.md:464`（及 `README.en.md:463`）：`builder.Services.AddSingleton<IMessageBroker>(new MessageBroker());` 注释写"InMemory Broker（无参构造）"；`README.md:726` 进一步声称"InMemory MessageBroker 用于测试"。实际 `PalDDD.Messaging` 只有 `public abstract class MessageBrokerBase`（`MessageBrokerBase.cs:21`）与 `public sealed class NullMessageBroker`（`MessageBroker.cs:72`），**没有公开的 `MessageBroker` 类，也没有可路由的 InMemory broker**（测试用内部 `RecordingMessageBroker`）。`README.md:109` 的"InMemory 实现覆盖全部抽象接口"对 `IMessageBroker` 不成立。

**DOC-3　同一 README 内测试规模自相矛盾，多处文档停留 v2.1.0　高　[事实]**

`README.md:835` 写"1202 项实测"，`README.md:907` 写"1379 项实测用例"，中英各一份（`README.en.md:838` vs `:913`）。CHANGELOG 2.2.0 权威口径是 1379。另有 `docs/test-coverage-baseline.md:6`、`docs/performance.md:30`、`docs/tutorial.md:113` 停留 1202。实测当前树 `[Test]` 方法为 1256（1379 应含参数化展开）。

**DOC-4　英文 README 与 ADR-020 状态更新冲突　高　[事实]**

ADR-020 的状态行（`:3`）与状态更新段（`:49-66`）明确 2026-09-13 裁决"退役延后，Dapper 转为**能力平等栈**"。但 `README.en.md:101,157,831,860,898,907` 仍称 Dapper "being deprecated / no AOT support"，mermaid 图写 `Dapper (deprecated)`；`README.md:857` 的 mermaid 同样写 `Dapper（弃用）`。ADR-020 正文 `:15`、`:30` 保留的旧结论是同一漂移源。

**DOC-5　其余文档缺陷　中/低　[事实]**

`docs/architecture.md:5` 引用 fresh clone 中不存在的 `.ai/README.md`（见 X-5）；`docs/aot.md:3-7` 称 CI 的 aot-verify"仅覆盖 PalOrmSample"，实际已扩为双 sample（`ci.yml:155-200`）；`README.md:837` 的 samples 清单只列 4 个而实际 5 个（缺 `PalDDD.DapperAotProbe`）。

**文档维度优势**

- 计数类主张多数经得起机械验证：38 条诊断 = 15 个 PDDD + 23 个生成器 ID；`PalActivitySource` 11 个 Start 方法与 `PalMetrics` 21 个 Counter 与实际一致；35 个可发布包与 `src/` 36 个 csproj（仅 `PalDDD.Prompts` 不打包）自洽。
- 抽样 12 处 API 样例，除 DOC-2 外全部与真实签名吻合。
- 相对链接健康：对全部 84 个 markdown 做链接存在性扫描，零断链。
- CHANGELOG 维护良好（90KB，`[Unreleased]` 显式置空并说明累积规则，版本三件套一致）。
- `docs/pitfalls.md` 的 82 条口径可验证（66 个唯一编号 + 统计表 66 + 十章 16 = 82）。
- 部分文档的计数有机械守卫（`docs/testing.md` 的"37 方法"由 `DocConsistencyGateTests.cs:426-450` 强制）。

---

## 四、改进策略

### 主题一：质量系统覆盖"门禁一致性"，但覆盖不到"事实一致性"与"运行时画像"

**解释的发现**：C-1、DOC-1 至 DOC-5、X-2、X-3、X-6、T-1、T-3、P-1 至 P-3。

仓库有 22 道门禁、33 个脚本、变异探针，能把 `set -o pipefail` 位置错误导致的假绿揪出来。但它的**证据模型是同质事实之间的比对**（DDL vs 文档、API 快照 vs 代码、诊断数 vs 断言数）。缺两类证据：

- **跨载体事实比对**：同一个 AOT 状态值活在代码、csproj `Description`、README 两张表、docs 权威文档、行内注释五处，`doc-consistency.cs` 只覆盖其中一部分。DOC-2 的"类型不存在"同理：没有东西把 README 代码块编译一遍。
- **运行时画像**：门禁不跑基准、不看查询计划，所以"0 代码级缺陷"的自评在性能与查询计划面上没有证据基础。P-1 是实测 5 倍差距，P-3 是查询计划实锤全表 SCAN，两者都不是推测。

**目标状态**：新增两类证据源。（1）事实值单点定义 + 引用检查，覆盖 csproj `Description` 与 README 代码块。（2）把基准与查询计划纳入可执行门禁的启动集，最初可只记录数值而非设阈值。

**原则**：门禁的证据种类决定它能发现的问题种类；只在同质事实上加门禁，永远发现不了异质事实之间的漂移。

### 主题二：三栈并行的成本以"人工姊妹同步"支付，且已出现不等价

**解释的发现**：A-3、A-4、A-5、A-7、P-7、D-5、DOC-4。

ADR-020 有意保留三栈，理由充分（EF Core 生态用户无法用 PalORM 覆盖）。但"能力平等栈"这个承诺目前靠人工维护，证据是那 10 份唯一约束冲突分类器副本，以及三栈 DI 注册数 4 vs 7 的不对称。承诺与实现之间的落差本身比任何单个 bug 更贵，因为它把成本摊到未来每一次修复。

**目标状态**：**收敛不变量，不收敛栈**。把重复语义（唯一冲突分类、租约与终态守卫、截断、UoW 守卫、方言索引契约）下沉到共享内部助手，让 PalORM 已做过的 `SqlErrorClassifier.cs` 式收敛成为规则；同时用一处声明驱动索引定义，消除 EF 模型与手写 DDL 的漂移。

**原则**：多栈是产品决策，重复是工程缺陷；把决策留在架构层，把重复消灭在实现层。

### 主题三：文档体积远超其可维护性，且缺少入口层

**解释的发现**：X-1、X-4、X-5、DOC-3、DOC-5、X-6、C-2。

14,826 行文档、33 份评审、22 份 ADR 是罕见的投入，但副作用明显：同一个数字（测试数）出现在 5 个文件且不同步；`docs/conventions.md` 的 CI 状态表两个方向都错；README 有 53KB 却在文档表里漏掉唯一的开发文档。文档没有"单一事实源 + 指针"结构，而是多处复制同一事实。C-2 的注释问题同源：解释被复制到 3-4 个姊妹文件而不是被引用。

**目标状态**：每类事实一个权威位置，其他位置写指针。新贡献者入口从 README 一步可达。计数类事实要么有机械守卫，要么标注时点。

**原则**：被复制的文档必然漂移；可验证的数字才值得写。

### 建议不修的东西及理由

| 不修 | 理由 |
|---|---|
| 合并三栈为双栈（ADR-020 的 v3/v4 退役计划） | 已裁决退役延后转能力平等栈，且 EF Core 生态兼容线有真实用户群体。主题二的"收敛不变量"能以低得多的风险拿到大部分收益。属产品决策。 |
| `Saga.cs` 四条重试车道的完整重构 | 1002 行 + 31% 注释 + 无测试覆盖的重构是风险最高的动作，回报是维护性而非正确性。建议等 T-1 类安全网建成后分步做。 |
| 覆盖率门禁从 0.70 提高 | 覆盖率是滞后指标。本仓真问题（T-1 死信路径、T-5 Kafka 错误路径）不是靠提高总覆盖率发现的，而是靠"关键路径清单"。提阈值只会制造为覆盖率而写的测试。 |
| `System.*` polyfill 与 in-box 包清理 | 纯噪声，无运行时风险，改动面广而收益低。 |
| `NoWarn` 减项（CA1062/CA2007 等） | 22 条抑制每条都写了针对框架库的正确理由。撤销会引入数百个噪声警告而零正确性收益。 |
| 为 `.ai/` 建仓内副本 | 它是独立演化的质量系统，强制同步会破坏其独立版本节奏。正确做法是修文档措辞。 |
| `PalDDD.Compression.Native` 移除 | 已发布包，可能有外部消费者。按本仓"代码价值判定"规则需先查文档定位、roadmap、git 演进、测试覆盖。当前证据不足，登记为待裁决。 |

### "完成"的可衡量信号

1. `grep -rn "AOT 假象" src/ README.md README.en.md` 返回 0，且 `DapperAotInitializer.cs` 头注释与 `:33` 状态自洽；新门禁在注入矛盾描述时**变红**。
2. `grep -c "1202" README.md README.en.md docs/test-coverage-baseline.md docs/performance.md docs/tutorial.md` 全部为 0，或全部带"v2.1.0 时点值"前缀。
3. 新增测试覆盖 `OutboxBatchProcessor` 三条死信分支；把 `descriptor is null` 翻转为恒假后套件**变红**。
4. `EXPLAIN QUERY PLAN` 对租约回读查询输出含 `INDEX`，不含 `SCAN outbox_messages`。
5. `Outbox_Lease_Batch100` 的 EF 比值进入 Dapper 的 2 倍以内（当前 5.04），且基准结果归档并可从 README 抵达。
6. 唯一约束冲突分类器的私有副本数从 10 降到 1。
7. `AddPalDapper*` 注册的 store 数从 4 升到 7（或缺口有显式测试登记的豁免），有测试断言三栈注册集差异。
8. `dotnet restore --locked-mode` 在 CI 通过。
9. 新贡献者按 README 一步找到构建/测试/装钩子说明；`docs/development.md` 出现在 README 文档表。
10. `scripts/changelog-check.cs` 在 `release.yml` 中被调用，或从 `docs/release.md` 移除"必须 0 FAIL"的声明。

---

## 五、审查深度声明

以下区域本次未深入，报告中相关结论基于抽样而非穷尽：

- **未执行完整测试套件**（仓库自述 1379 项用例）。测试相关结论基于静态阅读与 grep 计数。
- **未跑 Docker 依赖的方言探针**（本机无 Docker）。PalORM 多方言矩阵与 `DialectProbeTests` 的实际通过情况以 CI 记录与文档为据。
- **未逐条审计 15 条 Roslyn 分析器规则与 23 条生成器诊断的判定逻辑**，只确认规则数量与文档一致、规则级测试存在。
- **未逐行读 33 个门禁脚本**。深读的是 `secret-scan.cs`、`ci-coverage.cs`、`gate-audit.cs` 的机制，其余基于头部注释与 CI 接线点。
- **未重新运行 AOT 发布**。13/13 与 34 调用点数据引自 `docs/persistence-aot-status.md` 与 CI 配置，未独立复现。
- **P-1/P-2/P-4/P-5 的性能数值引自仓库存档的 BDN 报告**，未重跑基准。P-3 的索引结论是用内存 SQLite 对 DDL 跑 `EXPLAIN QUERY PLAN` 独立复现的。
- **未审计 `samples/` 与 `bench/` 的实现质量**，仅用作 AOT 与基准的证据来源。
- **未审计 `.ai/` 内部内容**（独立仓库），只确认了它与主仓的关系与文档引用一致性。
