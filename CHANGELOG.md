# Pal.DDD 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。
日志格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范（完整规则见 [`docs/release.md`](docs/release.md) §十一）：
**消费者可见变更在上**（Added/Changed/Deprecated/Removed/Fixed/Security + 本项目扩展 Dependencies/Documentation/Tests），**工程过程叙事入附录**；数字必须可验证；`[Unreleased]` 与发布段**同次提交转正、先于 tag**。

> **当前版本**：`VersionPrefix=4.0.0` / `VersionSuffix=`（空——见 `Directory.Build.props`；4.0.0 升位依据：ADR-026 契约窗口——③④ `IPalOutboxStore` 四同步方法删除改异步签名（含 `AddMessagesAsync` 补 ct 尾参）与既有 `Dispatcher.Register(Type,Type,Type,RequestExecutor)` 重载加 `needsScope` 尾参属**签名破坏**，② Outbox 终态写 fencing 契约跨栈统一属**行为破坏**，按 release.md 判据「破坏性 API 变更」定为 Major；公共 API 快照 +48/−27（排除程序集版本引用行，含 `VersionProbeRow` 属性 v/v2→V/V2 命名合规改名）；3.3.0 升位依据：零公共 API 变化（快照唯一差异为程序集版本引用行 3.2.0.0 → 3.3.0.0）、无破坏性行为变更，变更面为依赖消费者可见上移（PalORM 五包跨上游主版本线 5.6.0→6.3.1 / Confluent.Kafka 2.16.0 / NativeCompressions 跨 1.0 稳定线 1.0.1）与 Kafka broker 后台可观测增强，按 release.md 判据「向后兼容的功能增强」定为 Minor；3.2.0 升位依据：新增 3 项公共 API 均向后兼容（预热参数带默认值 / 新增重载 / 可选标注），公开 API 快照 653 行新增、1 行删除（唯一删除为程序集版本引用 3.1.0.0 → 3.2.0.0），唯一争点（压缩损坏帧异常类型统一为 `InvalidDataException`）经裁决按破坏面量级定为 Minor，非 Major；3.1.0 的升位依据是两处三栈行为对齐属**放宽/对齐**而非转严，故为 Minor；3.0.0 的升位依据是 M1-1 Dapper Saga 快照 fail-fast 与 ADR-023 UoW 嵌套事务的"静默错误转 fail-fast"，性质不同）
> **发布状态**：4.0.0 已发布（2026-10-11，tag `v4.0.0`，SemVer Major——ADR-026 契约窗口（③④ 接口异步化签名破坏 + ② fencing 行为统一 + ① Dispatcher 实例注册路径）+ ADR-025 MySQL SKIP LOCKED 租约 + EPSL 性能修复族 + PalORM 6.4.1 依赖上移）；3.3.0 已发布（2026-10-08，tag `v3.3.0` → 本提交，SemVer Minor——零公共 API 变更（快照唯一差异为版本引用行 3.2.0.0 → 3.3.0.0），变更面为依赖消费者可见上移与 Kafka 后台可观测增强，无破坏性变更）；3.2.0 已发布（2026-09-25，tag `v3.2.0` → 本提交，SemVer Minor——新增公共 API 向后兼容 + 压缩错误路径异常类型统一，快照 653+/1−（唯一删除为版本引用行）无签名破坏）；3.1.0 已发布（2026-09-23，tag `v3.1.0`，SemVer Minor——三栈行为对齐 + 版本承诺守卫类变更，无破坏性 API 变更）；3.0.0 已发布（2026-09-20 tag `v3.0.0`，SemVer Major——M1-1 Dapper Saga 快照 fail-fast 与 ADR-023 UoW 嵌套事务 fail-fast 两处行为破坏性变更）；2.2.0 已于 2026-09-15 发布（tag `v2.2.0`）；2.1.0 已于 2026-09-04 发布（tag `v2.1.0`→`0370c30`）；2.0.0 已于 2026-08-23 发布（tag `v2.0.0`→`a115c22`——发布时 CHANGELOG 的 `[Unreleased]` 未转正为 `[2.0.0]` 段，该段内容已并入 `[2.1.0]`，与 1.1.0 同款教训第二次，见 §九 教训 2）；1.1.0 已于 2026-07-31 发布（tag `v1.1.0`→`b4d532f`，事后回填）。tag `v4.0.0` 之后的变更见 `[Unreleased]`。
> **发布规范**：见 [`docs/release.md`](docs/release.md)

---

## [Unreleased]

### Added

- **2026-10-22 R13 F22/F23：ValueObject 实现 IFormattable、JSON Lines 错误带文档级行号**：
  ①**问题**：`ValueObject<T>` 的 `ToString(string?, IFormatProvider?)` 已存在但类型未声明 `IFormattable`——复合格式（插值 `$"{vo:X4}"` / `string.Format`）回退无参 `ToString`，格式串被静默忽略。`JsonLinesEventReader.DeserializeAll` 逐行反序列化，`JsonException.LineNumber` 恒为单行内位置（1），多行 `.jsonl` 无法定位坏行。
  ②**修法**：`ValueObject<T>` 声明 `IFormattable`（方法已在）；JSON Lines 捕获 `JsonException` 后以同类型重抛，消息前缀 `JSON Lines parse failed at line N`，保留原 `Path`/`LineNumber`/`BytePositionInLine`。
  ③**公共 API 无签名变化**（新增接口实现；快照未变）。
  验证：新增 2 个锁定测试（修前 `{vo:X4}` 得 "255"、错误消息无 "line 2"）；全量 1932 用例 0 失败。

- **2026-10-22 R13 F20/F21：基础库两处契约修复（NullPalLogger 结构化重载 + 演化启动校验哨兵）**：
  ①**问题**：`NullPalLogger<T>` 未覆写 `IPalLogger<T>` 的 EventId 结构化重载，接口默认实现走命名槽位→`string.Format`：参数少于槽位时抛 `FormatException`（本应最安全的空日志器转身成故障源），参数匹配时也白付正则替换与分配。`MessageEvolutionPipeline.ValidatePath` 缺 `Upgrade` 的 source/target ClrType 哨兵：同名同版本异 ClrType 的 descriptor 通过启动校验，首次消费才在 `Convert` 强转处炸（结构兼容时甚至静默错误转换）。
  ②**修法**：NullPalLogger 显式覆写四个结构化重载为空实现（不格式化、不分配、不抛）；ValidatePath 补 source 首步与 target 末步两处 ClrType 哨兵，与运行期同口径。
  ③**公共 API +4 方法**（NullPalLogger 的四个重载）；公共 API 快照同步，无签名破坏。
  验证：新增 4 个锁定测试（修前 FormatException / ToString 被调用 / 两处均不抛）；全量 1930 用例 0 失败。

- **2026-10-22 R13 F10：/health 授权/限流约定入口（新增公共 API `MapPalHealthChecksWithConventions`）**：
  ①**问题**：`MapPalHealthChecks` 返回 `IEndpointRouteBuilder`（历史契约），而类备注要求生产部署对 /health 链 `RequireAuthorization()` / `RequireRateLimiting()`——返回值无法链任何端点约定（CS1061），按备注配置只能绕过扩展自行 `MapHealthChecks`，丢失 PalDDD 的响应体格式。
  ②**修法**：新增 `MapPalHealthChecksWithConventions(...)`，返回 `IEndpointConventionBuilder`：`app.MapPalHealthChecksWithConventions().RequireAuthorization();`。原方法签名与行为逐字不变（未加可选参数：那会改变 CLR 签名并使 3/4 参重载调用二义）。
  ③**公共 API +1 方法**（快照同步）；无签名破坏、无行为变更。
  验证：新增 `MapPalHealthChecks_ConfigureCallback_AppliesConvention`（约定元数据落入端点）；Hosting 测试 13/13；公共 API 快照更新。

- **2026-10-22 R12 C-02：投影检查点租约续期（新增公共 API `IProjectionLeaseRenewalStore`）**：
  ①**问题**：`ProjectionProcessor` 只在 handler 执行前取一次检查点租约；单个 handler 超过 `processingTimeout` 后租约到期，另一 worker 可重租同一位置并发投影（重复副作用）。
  ②**修法**：新增公共接口 `IProjectionLeaseRenewalStore.RenewLeaseAsync(checkpoint, now, processingTimeout, ct)`；InMemory / Dapper / PalORM / EF 四栈实现均以 `Revision` + Processing 状态为守卫，只更新租约/更新时间，不推进 Revision。处理器在 handler 执行期间按 `processingTimeout / 3` 心跳续租；handler 完成、失败或取消时停止。自定义 store 未实现该接口时保持原语义（无续租）。
  ③**公共 API +6/−0**：接口与其方法 + 四个 Store 各一个 `RenewLeaseAsync` 方法；公共 API 快照同步更新，无签名破坏。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；新增 5 个测试（慢 handler 心跳保租；Dapper/EF SQLite/PalORM SQLite/EF InMemory 四栈续租后拒绝接管）；全量 1907 用例，0 失败。

- **2026-10-21 契约盘点第七轮修复（R7：组件级注册自带日志门面）**：
  ①**根因**：`OutboxProcessor` / `InboxProcessor` / `SagaProcessor<TState>` 构造函数强制依赖 `IPalLogger<T>`，而该门面的实现 `PalLogger<T>` 是 `PalDDD.DependencyInjection` 的 internal 类型、只在 `AddPalCoreStack()` / `AddPalPipelineBehaviors()` 中注册。按 usage.md 各章节单独使用 `AddPalOutbox()` / `AddPalInbox()` / `AddPalSaga<,>()`（不引入核心栈）时，宿主启动解析 `IHostedService` 即抛 `Unable to resolve service for type IPalLogger<T>`。
  ②**修法**：门面映射下移到 `PalDDD.Core` 的 `PalLoggerRegistration.EnsurePalLoggerFacade()`（单一定义点，TryAdd 幂等），`PalLogger<T>` 随迁至 Core（仍 internal，经 IVT 授 DependencyInjection / Transactions / 两个测试项目）；`AddPalOutbox` / `AddPalInbox` / `AddPalSaga<,>` 各调用一次，DI 包的 `EnsureLoggerFacade` 与 `AddPalLogging` 改用同一方法。宿主的 `ILogger<T>` 仍由标准宿主提供。
  ③**公共 API 无变化**；Core 的包依赖未变（`Microsoft.Extensions.Logging.Abstractions` 在 .NET 11 共享框架内）。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；新增 3 个 DI 用例（三个入口各自单独解析 hosted/processor），每个用例各做一次变异探针（移除对应入口的门面调用 → 该用例红，其余绿）；消费方最小复现（`AddLogging` + `AddPalOutbox` + 解析 `IHostedService`）由 exit 127 转为 exit 0。

- **2026-10-21 契约盘点第六轮修复（R6：批量 Inbox 1006 告警 + 批量回落可观测 + Auto 口径统一）**：
  ①**批量路径补齐 1006 告警**：`InboxProcessor.TryProcessBatchAsync` 此前不调用 `WarnIfLeaseExceeded`，而 usage.md 推荐的批量消费正是高吞吐路径，handler 超过 `ProcessingTimeout` 时没有任何信号；现按批聚合发出一条 `InboxHandlerLeaseExceeded`（含超限条数与最大耗时，计时起点与逐条路径一致取整批认领时刻），取消路径在抛出前冲刷，逐条路径行为不变。
  ②**批量回落可观测**：store 未实现 `IBatchInboxStore`（当前为 PalORM / EF）时，每个 processor 实例首次批量调用记录一次 Information（store 类型 + 消费者名），不再完全静默；usage.md 标注当前实现方与「每消息 2 次往返」的代价。
  ③**Auto 口径统一**：`DeliveryMode.Auto` 下 `BatchMarkThreshold` 有效值为 `min(BatchSize, 400)`（默认 100），`MaxInFlightPublishes` 至少 16；usage.md 与 `TransactionOptions` XML 的「默认 1 = 既有行为逐字不变」改为「选项默认 1，Auto 按能力提升」，并补「重复窗口上界由冲刷间隔加在途时长界定」。
  ④**注释纠偏**：EF `OutboxDbContext.MarkProcessedAsync` 回退路径原称「由调用方经 SaveChangesAsync 持久化」，与本类 `RequiresSaveAfterMark = false` 矛盾；改为说明 InMemory 的可见性来自存储按引用共享，并写明新增不支持 ExecuteUpdate 的关系型 provider 前必须先改开关或自行落库。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；新增 2 个测试（批量 1006 告警、回落单次提示）；公共 API 无变化（仅私有成员与文档）。

- **2026-10-21 契约盘点第五轮（R5：四组件 SQLite 四栈矩阵 + EF 租约陈旧跟踪修复）**：
  ①**四组件四栈矩阵**：Idempotency、Projections、Saga、EventLog 各新增 InMemory / Dapper SQLite / PalORM SQLite / EF SQLite 矩阵，同一组状态断言：幂等活跃租约拒绝与过期回收、投影检查点完成/失败/Reset、事件日志版本冲突与 Any 追加、Saga 创建/读回/租约/终态保存。
  ②**EF Saga 契约违背修复**：矩阵发现同一 DbContext 先 `GetByIdAsync` 再租约时，EF identity resolution 返回陈旧跟踪实例（`LeasedBy` 为空、`Version` 未换代），后续 `SaveChanges` 会因版本失配误判冲突；三个方言租约回读后仅对陈旧实体 `Reload`（`RefreshStaleLeaseStatesAsync`）。
  ③**矩阵覆盖**：清单的 24 个 verified-local 格子现在都具备同组件四栈统一断言（Inbox、Idempotency、Projections、Saga、EventLog 五个组件）。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；16 个测试项目 **1843 用例 / 1763 通过 / 80 跳过 / 0 失败**（+4 矩阵用例）。

- **2026-10-21 契约盘点第四轮修复（R4：文档计数纠偏 + 幂等方言补测 + 缺口集合冻结）**：
  ①**文档计数纠偏**：三处测试指引移除硬编码用例数（写入后当天套件从 1827 增至 1837，历史数字造成基线漂移），改为「以当次输出为准」。
  ②**Dapper 幂等 PG/MySQL 方言用例**（`DialectProbeTests`）：TryStart→并发拒绝→MarkCompleted→重放拒绝→GetAsync 终态，每方言 5 条状态断言；清单这两格由 accepted-gap 升为 verified-ci-only。
  ③**缺口集合冻结**：清单 AcceptedGap 从「总数 15」升级为精确集合（13 格），总数不变但缺口换位同样红。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；16 个测试项目 1839 用例，1759 通过、80 跳过、0 失败（新增 2 个方言用例，本地无 Docker 时跳过；CI 有 Docker 时转为通过）。

- **2026-10-21 契约盘点后续项实施（R3：格子清单、Inbox 矩阵、行为断言门禁、EF 方言守卫、本地真库提示）**：
  ①**契约格子清单**（`ContractCellManifestTests`）：61 格（组件 × 栈 × 方言）逐格登记实现类型、代表测试文件与三态状态（verified-local / verified-ci-only / accepted-gap），校验实现类型存在且实现对应抽象、新增未登记根实现即红、Local/CiOnly 格必须有含 `[Test]` 与 `Assert.That` 的测试文件；AcceptedGap 15 格（EF Outbox 3 + EF 其余组件 PG/MySQL 10 + Dapper 幂等 PG/MySQL 2）全部带原因并冻结（R4 补 Dapper 幂等 PG/MySQL 两格后为 13）。
  ②**Inbox 四栈 SQLite 矩阵**（`InboxCrossStackMatrixTests`）：InMemory / Dapper / PalORM / EF 跑同一组断言（首次认领、重复返回 null、完成后 null、失败后可重入且 Attempts=2），共 16 条状态断言。
  ③**行为断言门禁**（`AssertionStrengthGateTests`）：新增 `*StoreTests.cs` 规则，每个 [Test] 至少一条状态/返回值/异常断言，附负向自证；按规则补齐 4 个既有弱断言测试（Idempotency 回收、EventLog 接口赋值、PalORM 幂等回收、PalORM Inbox 独立消费者）。
  ④**EF 方言守卫**（`EfCoreDialectGuard`）：Sqlite/PG/MySQL 三个 Outbox 上下文租约入口校验 `Database.ProviderName`，provider 与方言错配抛可行动异常；新增 `EfDialectMismatchTests`（错配 + 正向两条）。
  ⑤**本地真库与提示**：`docs/development.md` 增「本地真库（wslc）」配方；pre-push 钩子在 store/方言 SQL 改动且未配置 `PALDDD_TEST_PG/MYSQL` 时打印提示（不阻断）。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；16 个测试项目 **1837 用例 / 1759 通过 / 78 跳过 / 0 失败**（+9：清单 3、Inbox 矩阵 1、EF 守卫 2、行为断言规则 2、豁免标记自证 1；另补齐 4 个既有弱断言）。

- **2026-10-21 契约盘点复验轮 R2（2 项缺陷 + 3 项债务收口；公共 API 快照 −1）**：
  ①**测试命令文档纠偏**——`dotnet test PalDDD.slnx` 实测 1828 例 / exit 0；修正 `conventions.md` §10.5、`development.md`、`testing.md` 的「MTP 禁用 slnx 批量（exit 5）」声明；CI 保持逐项目循环（并行、逐项目失败注解、覆盖率需要）。
  ②**runtime assembly scanning 守卫**——`SourceCodeGuardTests` 新增守卫 7：扫描 `GetAssemblies()` / `Assembly.Load*` / `AssemblyLoadContext.LoadFromAssembly*`，附 5 个正反样本矩阵（此前 architecture.md 稳定性约束零覆盖）。
  ③**移除 `AddPalFullStack`（Changed [Breaking]）**——v5.0 契约窗口内完成收敛：该方法从未注册基础设施、名字误导，统一入口 `AddPalCoreStack()`；usage/features/测试/快照同步。
  ④**Dapper 驱动传递足迹收口**——`Microsoft.Data.Sqlite`/`Npgsql`/`MySqlConnector` 改 `PrivateAssets="all"`：基础包仍编译 bulk copy，驱动不进 nuspec/传递依赖；`DapperConnectionGuard` 改类型全名判定、`DapperBulkCopy` 的 MySQL 缓存键改 `DbConnection`，消除构造期/类型初始化期强制加载未使用 provider 的回归（实测 SQLite-only 与 PG-only 消费方各自只加载所用驱动）。
  ⑤**`RetentionDialect` 公开面定案**——保留 public 作为方言选择器（`EfCoreRetentionStore` 公共构造参数），XML 文档声明其与 `DapperDbType` 同族、不携带 provider 类型；方言 SQL 文本维持 internal。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；16 个测试项目 1828 个用例，1750 通过、78 跳过、0 失败（−1 移除的 AddPalFullStack 测试，+2 守卫 7 测试）；后续优化方案见 `docs/review/contract-audit-followup-2026-10-21.md`。

- **2026-10-21 契约盘点修复轮（8 项发现闭环；公共 API 快照 +1/−8）**：
  ①**首用路径**——`AddPalPipelineBehaviors()`（开放与闭合两版）自带 `IPalLogger<T>` 最小注册（`AddLogging` + `TryAddSingleton`），修复文档推荐的 `AddPalCoreStack()` + handler 在首次 `SendAsync` 抛「Unable to resolve service for type IPalLogger<LoggingBehavior<...>>」；`AddPalLogging()` 仍只负责 ZLogger Provider 与级别策略。
  ②**构建基线**——修复 `Transactions.Tests/InMemoryStoreTests.cs` 两处 CS8602（补空值断言），干净重建 `dotnet build` 恢复 0 错误；同时修复被增量构建掩盖的 `Integration.Tests` DAP005（测试项目显式 `NoWarn`，测试不参与 AOT）。
  ③**抽象层收口（Changed [Breaking]）**——`PalDDD.Transactions` 的 `MySqlLeasePolicy` 与 `RetentionSql` 由 public 下沉为 internal（IVT 授 Dapper/PalORM/Transactions.EFCore/Integration.Tests），抽象程序集不再公开 MySQL 版本策略与方言 SQL 文本；`RetentionDialect` 保留 public（`EfCoreRetentionStore` 公共构造参数）。
  ④**Saga 方言面**——删除 `SagaStateDbContext` 内未文档化、零测试的 SQL Server 单语句租约分支（文档承诺 PG/MySQL/SQLite 三方言），SQL Server provider 回落通用分页路径。
  ⑤**配置错误可行动**——新增 `DapperConnectionGuard`：全部 Dapper store 构造期校验连接串非空与 `DapperDbType` ↔ 连接类型一致（SQLite 连接 + PostgreSql 方言原先在首次查询报 `near "FOR": syntax error`、缺连接串报 `no such table`，现均为指出配置项的 `ArgumentException`）。
  ⑥**Inbox 租约风险可见**——`InboxOptions.ProcessingTimeout` 的「必须大于最慢 handler」契约写入 XML 文档与 usage.md；`InboxProcessor` 在 handler 时长达到该值时记录 `InboxHandlerLeaseExceeded`（EventId 1006，公共 API +1）告警，把默认 5 分钟下的并发重复执行风险从静默变为可观测（审计 2026-09-20 登记的机械化缺口）。
  ⑦**Expression.Invoke 门禁**——`SourceCodeGuardTests` 守卫 2 增加 `Expression.Invoke` 检测与正反样本矩阵（conventions §10.2 的 EF Core 可翻译性禁令此前零测试）。
  ⑧**测试**——新增首次 dispatch、Inbox 超时告警、Dapper 构造守卫 3 件、Expression.Invoke 矩阵 2 样本。
  **Known（R2 已收口）**：`PalDDD.Dapper` 的三驱动引用改为 `PrivateAssets="all"` 的实现私有依赖——基础包仍编译 bulk copy，但驱动不再流向消费方；守卫与静态缓存已去掉 provider 类型引用，SQLite-only / PG-only 消费方只加载各自引用的驱动。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；16 个测试项目 1827 个用例，1749 通过、78 跳过、0 失败；`encoding-gate` 0 失败。

- **2026-10-21 基础库优化续轮（兼容过渡 + T1-T9；公共 API 快照 +35/−0）**：①**兼容过渡**——首轮的源破坏改为可 Minor 发布的形态：新增 `AddPalOutboxUnitOfWorkForContext<TContext>`（编译期要求业务上下文实现 `IPalOutboxStore`），旧 `AddPalOutboxUnitOfWork<TContext>` 保留并标 `[Obsolete]`，解析期校验同实例与拦截器；`PendingEvents` 与 `VersionProbeRow` 回归公共面并标 `[Obsolete]`，留到 v5.0.0 移除。②**结构化日志**——`IPalLogger<T>` 新增带 `EventId` 与模板参数的四个重载（DIM 默认实现保证旧实现者二进制兼容），适配器把字段原样交给 MEL/ZLogger；Outbox/Inbox/Saga 热点迁移，新增稳定 `PalLogEventIds`。③**聚焦跨栈契约矩阵**——批标记部分接受在 InMemory/Dapper/EF/PalORM 四栈同组断言；幂等 0 字节载荷在 Dapper/PalORM 共享 SQLite 文件上交叉读写。④**真库端到端**——`EfCoreRetentionStore` 在 wslc PG 18.6 / MySQL 8.4 的临时表上验证 PascalCase 标识符删除（`PALDDD_TEST_PG`/`PALDDD_TEST_MYSQL` opt-in）。⑤**穷尽性门禁**——DapperDbType、RetentionDialect、CompressionLevel 全枚举映射测试。⑥**注释棘轮**——`vN PM` 注释以 729 为基线，新增即红（`CommentRatchetTests`）。⑦**可选健康检查包**——新增 `PalDDD.Hosting.AspNetCore.OutboxHealth` 与 `AddPalOutboxHealthCheck()`，旧 Hosting 入口保持不变。⑧**桥事务测试**——`DomainEventOutboxBridge` 在真实 Dapper SQLite 上验证提交可见、回滚零行。验证：`dotnet build --nologo` 0 警告 0 错误；16 个测试项目 1822 个用例，1744 通过、78 跳过、0 失败；PG/MySQL 真库测试在 wslc 容器上通过；`encoding-gate`/`count-audit`/`doc-consistency`/`verify-conventions --quick`/`license-policy`/`xml-guard` 全部通过。未做：W3 移除弃用面与 Hosting 依赖（下一 Major）、W4 触发式项（方言策略表、AsyncLocal 统一、域事件链表基准、全仓轮次替换）。

- **2026-10-21 双评审合并修复轮（架构评审 + 98 条源码发现；首轮）**：合并 `docs/review/review-2026-10-21-architecture.md` 与只读源码评审，按「正确性 → 契约 → 注释 → 架构建议」实施。
  ①**EF Outbox 装配约束**——新增 `AddPalOutboxUnitOfWorkForContext<TContext>`（编译期要求 `DbContext, IPalOutboxStore`）；解析 `IUnitOfWork` 时校验 `IPalOutboxStore` 与业务上下文为同一实例且拦截器已挂接，错误装配立即抛错。教程改为「业务上下文继承方言 Outbox 基类 + `UsePalOutboxInterceptor`」；拦截器 store 改为延迟解析，消除同上下文形态的 DI 循环。新增真实 `SqliteOutboxDbContext` 端到端测试（事件落 outbox、缺拦截器/独立 store/旧入口非 store 上下文三种错误装配）。
  ②**投影回放续读生效**——`EventLogReplaySource<TMessage>` 补 `IResumableEventReplaySource<TMessage>`（原先 `is` 检测恒 false，ADR-028 的增量回放从未生效）。
  ③**投影批量检查点防跳段**——组首锚点改为处理器执行前取得；崩溃/取消/组首标记失败时续读最小位置为组首。每组多一次 TryStart + MarkCompleted（`--m2-probe` 阈值相应调整）。
  ④**EF SQLite 到期租约**——`NextCheckAt=NULL` 视为到期，与其余方言一致；等值回读补 `Take(batchSize)`。
  ⑤**EF Retention 物理名**——从 EF 模型解析表名/列名（PascalCase）并按方言加引号，PG/MySQL 不再报 relation/column 不存在；新增 `DbContext` 构造器与三表名构造器，默认列名按 EF 映射。
  ⑥**跨栈契约对齐**——Dapper 幂等 0 字节载荷按 Completed 回放、`MarkFailed`/`MarkCompleted` 时间统一 `ToTimeParam`；Dapper `saga_data` 空串与 PalORM 对齐；InMemory 批标记先校验后变更、部分接受零回写；Dapper/EF 批标记 null 元素校验前移；PalORM `GetAsync` 补空白守卫；`SagaKey.Make` 补 null 守卫。
  ⑦**Inbox 回落路径**——逐条 try/catch，单条失败不中止整批，最后聚合抛出。
  ⑧**资源与健壮性**——PG 数据源全部改工厂注册（容器接管释放）；RabbitMQ 声明失败释放 channel、清理顺序改为删队列先于释放；取消回调与宽限计时任务可观察；FTS DDL 单事务 + 单语句多命令；`SmartEnum.All`/`AllShards` 返回只读包装；Dapper 三处方言分派统一 `Enum.IsDefined`；断线续跑 400 响应收敛；`PendingEvents` 与 `VersionProbeRow` 标 `[Obsolete]`（v5.0 移除），删除 0 引用的 Dapper `EventLogRow`。
  ⑨**新增 `DomainEventOutboxBridge`**——栈无关的「收集 → 序列化 → 写 outbox → 成功后清事件」链路，EF 拦截器复用同一构造；Dapper/PalORM 用 `PersistAsync(aggregates, outboxStore, ct)`（须在同一 UoW 事务内）。文档补充方言不对称与「默认零反射、反射路径显式标注」口径。
  ⑩**注释纠偏**——Dapper 四条误导 AOT/性能评审的声明（真流式、无反射物化、UPDATE 先行、MySQL 两步）、Obsolete/软删除/读写路由示例等会让调用方写出必错代码的文案、计数类（`Start*`、Counter、Store 数）与 null-manager 语义。
  验证：`dotnet build --nologo` 0 警告 0 错误；全量 16 个测试项目 1806 个用例，1728 通过、78 跳过、0 失败（新增装配 3、领域事件桥 3、EF Retention 默认表名 1）；`encoding-gate`/`count-audit`/`doc-consistency`/`verify-conventions --quick`/`license-policy`/`xml-guard` 全部通过。未做与复活条件见 `docs/review/action-items-2026-10-21-merged.md`（AsyncLocal 收敛、四栈契约矩阵、结构化日志、大文件拆分、域事件链表基准、方言策略表）。

- **2026-10-21 性能实施轮（SSE 写合并、Saga 键、单语句幂等/投影、Inbox 批量、批量写快路径、Kafka 消费并发；公共 API 快照 +10）**：①**SSE 写合并**——流式端点不再每项 flush：首帧立即提交，连续同步产出的帧累计到 4KB 才提交，`MoveNext` 挂起前与流结束各刷一次；快生产者下 flush 次数降至 1/N，低速率与首帧延迟不变。②**Saga 运行期零字符串键**——步骤注册期固化 `(State, EventName, Key)`，事件查找改元组索引，超时扫描不再从 key 切分状态名；`ChildSagaInputEvent` 保留按 `Type.Name` 匹配的跨泛型实例语义。③**Dapper 幂等单语句**——PG/SQLite 的 `StartOrGetAsync` 改为 `INSERT ... ON CONFLICT DO UPDATE ... WHERE 过期/失败/租约过期 RETURNING`，冲突侧由 3 到 5 次往返降为 1 到 2 次；MySQL 保持原路径。④**投影检查点单语句**——PG/SQLite 的 `TryStartAsync` 改为 UPSERT + 条件 UPDATE + RETURNING，冲突侧由 3 次往返降为 1 次。⑤**MySQL 时间参数原生化**——租约/标记时间参数由 27 字符字符串改为无偏移 UTC `DateTime`，每参数少一次分配，数据库侧语义不变。⑥**消费上下文去 Lazy**——按需头解码不再分配 `Lazy` 对象，实例尺寸保持不变。⑦**Outbox 发布流水线**——直接消费 broker 的 `ValueTask`，同步成功复用 `Task.CompletedTask`，每消息少一个异步状态机与包装 Task。⑧**PalORM EventLog 单事件 SQL 常量化**——四种版本守卫改为编译期常量，每次追加少约 1.4KB 拼接分配。⑨**EF 领域事件拦截器按 DbContext 键控状态**——暂存事件/实体/注入 ID 移入 `ConditionalWeakTable`，拦截器不再持有跨请求可变字段，可与 `AddDbContextPool` 组合使用。⑩**删除三处写放大索引**——`idx_events_event_id_bin`（全方言）与 PG/SQLite 的 `idx_outbox_lease_holder`、`idx_saga_lease_holder`；MySQL 保留两张租约索引（两步回读仍需要），迁移见 `docs/sql/migration-2026-10-21-drop-unused-indexes.md`。⑪**Inbox 批量认领**——新增 `IBatchInboxStore` 与 `InboxProcessor.TryProcessBatchAsync`：Dapper PG/SQLite 每块 1 次往返（`INSERT ... ON CONFLICT DO UPDATE ... WHERE 可重入条件 RETURNING`），MySQL 每块 4 次（读既有行、插缺失行、更新可重入行、按令牌回读；单语句 `ON DUPLICATE KEY UPDATE` 在本机 MySQL 8.4 实测更慢，已记录在代码注释），InMemory 锁内复用逐条语义；未实现能力接口的 store 自动回落逐条路径。批量完成按 `(consumer_name, message_id, processing_started_at)` 定位，被抢占的旧持有者零命中。⑫**Outbox PG 强类型 COPY**——`DapperBulkCopy.BulkInsertOutboxAsync` 按列类型直接写 NpgsqlBinaryImporter，去掉通用路径每行 `object?[]` 与逐值装箱/类型分派。⑬**MySQL BulkCopy 去 DataTable**——轻量 `DbDataReader` 替代 DataTable，列类型仍按首个非空值采样，逐值方言转换在 GetValue 执行，不再构造 DataRow 与整表行集合。⑭**Kafka 消费并发**——`KafkaBroker.ConsumerDispatchConcurrency`（默认 1 = 原逐条保序形态）；大于 1 时 worker 维持 maxConcurrent 个在途 handler，关闭时先取消再排空在途 handler；真 broker 集成测试 `Kafka_ConsumerDispatchConcurrency_RunsHandlersInParallel` 通过（wslc kafka431，4 个 handler 同时在屏障上；并发度 1 时首个 handler 阻塞会使断言超时）。验证：`dotnet build PalDDD.slnx -c Release` 0 警告 0 错误；全量 1788 个测试，1710 通过、78 跳过、0 失败（Kafka 并发测试在无 broker 的默认配置下跳过，wslc kafka431 实跑通过）；公共 API 快照 +10（`IBatchInboxStore`/批量方法与 Kafka 并发属性）；本地 wslc 真库探针 PG/MySQL 各过 outbox 批量 1000 行（含载荷/追踪列/可用时刻抽查）与 inbox 批量 100 条（claimed=100、marked=100、重复=0）；单次墙钟 A/B（localhost wslc，非 BDN 统计口径）：outbox 1000 行单条→批量 PG 1838.9→13.37ms（137.5×）、MySQL 5944.6→29.53ms（201.3×），inbox 100 条逐条→批量 PG 418.9→9.07ms（46.2×）、MySQL 1153.1→20.73ms（55.6×）。未做：EF PG 原子追加（原生 ADO 会绕过 EF 命令拦截器，需先定审计契约与 PG 测试装置）、二进制 ULID 与表分区（迁移窗口）。Not-Tested：PG/MySQL 的批量 SQL 目前由本地 wslc 探针验证，CI dialect-probe 未覆盖 Inbox 批量与 AllowLoadLocalInfile 大批量路径。

- **2026-10-21 投影批量处理器（公共 API 快照 +2）**：新增 `IProjectionBatchHandler<TMessage>`（继承 `IProjectionHandler<TMessage>`）：`ProjectionProcessor.ProcessBatchAsync` 检测到该能力时把整组一次交给 `ProjectBatchAsync`（可批量 upsert，把 N 次写降为 1 次），未实现时保持逐条 `ProjectAsync`。检查点语义不变——只在组末位置 TryStart/MarkCompleted，失败时组末与组首均标 Failed 后整组重放（at-least-once，实现须整批幂等）。测试：10 条事件、批大小 4 → handler 收到 4/4/2 三批、逐条入口零调用、总项数 10。验证：全量 1799 个测试，1721 通过、78 跳过、0 失败；`dotnet build PalDDD.slnx -c Release` 0 警告 0 错误。

- **2026-10-21 Saga 到期哨兵与到期过滤默认开启（公共 API 快照 +1）**：①**哨兵**——新增 `SagaState.NoPendingTimeout`（9999-12-31 00:00:00 UTC）：活动 Saga 的 `NextCheckAt` 恒有值，有带 Timeout 的已开始步骤时为真实到期时刻，否则写哨兵；不用 `DateTimeOffset.MaxValue`（7 位小数秒 + 偏移超出 MySQL `DATETIME(6)` 范围）。`Saga.UpdateNextCheckAt` 与 `SagaProcessor` 的到期后重算同口径写哨兵，终态仍写 null。②**到期谓词 NULL 包容**——四栈改为 `next_check_at IS NULL OR next_check_at <= now`（Dapper 五个模板、PalORM 两条、EF 三条原生 SQL 与 LINQ 回落、InMemory），NULL 只表示迁移前存量行，按到期处理。③**默认开启**——`EnableDueTimeFilter` 默认由 false 改为 true；惰性回填只在过滤生效且 store 实现 `IDueTimeSagaStore` 时发生（全量扫描路径零额外写），存量行首次扫描后回填真实时刻或哨兵，**不再需要独立回填阶段**。迁移文档与 ADR-034 实施附记已同步。④**测试**——租约用例改为「到期 + 遗留 NULL 被租，未来 + 哨兵不租」，新增「活动态无待检查超时写哨兵」；两个依赖回拨 `StepStartedAt` 的超时探针同步回拨 `NextCheckAt`（否则到期过滤下永不租到，属探针装置与生产形态不一致）；Outbox 完成序用例改用手动闸门而非 150ms/10ms 计时，消除满载线程池下的计时反转。验证：全量 1798 个测试，1720 通过、78 跳过、0 失败；`dotnet build PalDDD.slnx -c Release` 0 警告 0 错误。

- **2026-10-21 投递面三项（Kafka 幂等默认、PG 序列缓存、Outbox 完成序窗口；公共 API 无变化）**：①**Kafka 幂等默认**——`KafkaBroker` 构造时若调用方未设置 `EnableIdempotence`/`Acks`/`MaxInFlight`/`MessageSendMaxRetries` 任一项，则默认开启幂等生产端（acks=all、retries=INT_MAX、in-flight≤5），重试不产生重复也不乱序，同时使 `SupportsConcurrentPublish` 默认为 true（Outbox 自动并发窗口生效）；显式设置任一项即保持调用方取舍，不擅自改写。②**PG 序列缓存**——`inbox_messages_id_seq` 与 `events_global_position_seq` 改为 `CACHE 100`（`BIGSERIAL` 默认 `CACHE 1`，高写入下 nextval 的页推进/WAL 成为单点争用；两列只作代理键/全局排序，允许崩溃后最多 100 跳号）。新建库由 `docs/sql/postgresql/000_schema.sql` 带上；已有库按 `docs/sql/migration-2026-10-21-pg-sequence-cache.md` 执行，真库核对列为 `pg_sequences.cache_size`（本机 PG 18.6 实测 ALTER 后回读 100）。③**Outbox 完成序窗口**——`MaxInFlightPublishes > 1` 的出窗处理由 FIFO（等队头）改为完成序：每条发布完成经无界通道交给唯一读者顺序处理，同步完成直接入队不付续体分配；单条慢投递只占自己的窗口槽，不再阻塞已完成消息的标记与新发布启动。标记顺序不再与启动序一致（fencing 逐消息独立，消费方须 Inbox 幂等）。合成探针锁定：队头 150ms、其余 10ms、窗口 4 时后 7 条先标记、慢消息最后（FIFO 形态下队头必然首个标记）。验证：全量 1797 个测试，1719 通过、78 跳过、0 失败（Transactions 243、Messaging.Integration 新增 Kafka 配置 4 件）；PG 真库序列探针通过；`dotnet build PalDDD.slnx -c Release` 0 警告 0 错误。另：Dapper 单事件强类型参数 DTO 实验**失败并回滚**——Dapper.AOT 1.1.0 不拦截非匿名参数类型，调用静默回落经典反射路径（AOT 下会运行时炸），生成文件核对零拦截器零诊断，结论记入 `docs/persistence-aot-status.md`。

- **2026-10-21 批量消费默认可用（公共 API 快照 +17/−1）**：①**消息 ID 贯通**——`MessageConsumeContext.MessageId`（Kafka 消息 Key / RabbitMQ `BasicProperties.MessageId`）与 6 参构造、`FromHeaders(..., messageId)`；Kafka 发布侧本就写入 Key，消费侧此前丢弃，现作为 Inbox 幂等键的直接来源；仅带 Key 的消息不再返回 null 上下文。②**批量订阅**——`ConsumedMessage<TMessage>` 与 `IMessageBroker.SubscribeBatchAsync`（数量界默认 64、时间界默认 50ms，先到者 flush）：默认实现复用逐条订阅经有界 Channel 组批，单读者泵在时间界先到时保留挂起读作为下一批首条，避免丢消息；组批窗口用 `Stopwatch` 单调时间戳（不引入墙钟双轨）。`MessageBrokerBase` 覆写后经 `OnBatchHandlerFailed` 记日志并继续后续批次，Kafka/RabbitMQ 各自实现日志，`NullMessageBroker` 返回空订阅。③**`InboxBatchConsumer<TMessage>`**——把批量订阅、每批一个 DI scope、`InboxProcessor.TryProcessBatchAsync` 串成开箱即用消费者；消息 ID 默认取上下文，缺失时可显式提供 `messageIdSelector`，两者都无则快速失败。④**测试**——组批泵三件（数量界/时间界/消息 ID）与 `InboxBatchConsumer` 端到端一件（含重投被 Inbox 全部跳过、新消息照常处理）；`MessageConsumeContext` 零分配预算 70→76B/迭代（第 6 个引用字段）。验证：全量 1792 个测试，1714 通过、78 跳过、0 失败（Messaging 36、Transactions 242）；`dotnet build PalDDD.slnx -c Release` 0 警告 0 错误。附带：`DirectWrite_ZeroAllocation_WithReusedBuffer` 断言由「恰为 0」放宽为 ≤1KB（全量并行跑时一次性分层 JIT 记账偶发计入当前线程；DIM 回退形态是 1MB 量级，检测力不变）。

- **2026-10-21 增量审计完整修复（公共 API 快照无变化）**：①**EF Outbox** 去掉 `SavingChanges` 全量 `Entries<OutboxMessage>()` 扫描，改 `ChangeTracker.Tracked` 逐实体归一；②**StreamRun** 批次 List 循环复用，回放按 64 项/16KB 分段 flush，live 每 token 语义不变；③**PalORM EventLog** RETURNING 批量按 16 参/行分块（PG 4095、SQLite 2047），无 ambient 时本地事务全成全败，新增 SQLite 2500 事件测试；④**MySQL 批量**无 `AllowLoadLocalInfile` 时回退参数化多行 VALUES（60k 参数预算分块、本地事务），不再在 17 行处抛错，Dapper AOT 探针 20 行批量通过；⑤**Dapper/PalORM 二进制读**增加专用列清单/投影，不传/不解析文本 `event_id`，NULL bin 严格失败；⑥**InMemory** Outbox 批量标记改引用集合 + 单次 `RemoveAll`，Saga 租约改 `PriorityQueue` 保留前 N；⑦**Rabbit/Kafka 消息头**只立即解码四个追踪键，完整 Headers 按需物化；Kafka librdkafka 日志按 `IsEnabled` 门控；⑧**部署面**：`EventLogPositionReserver` 默认块 100→1000，PG Notifier 文档改 statement-level transition-table 触发器，EF Inbox/Saga 删除冗余索引。验证：全量 1780 个测试，1703 通过、77 跳过、0 失败；`check-all` 0/0/0；Dapper AOT 探针 PG/MySQL 各 33/33（含 20 行批量回退）；NativeAOT SQLite 33/33。未做项与风险见 `docs/review/perf-round-2026-10-21-audit-followup.md`。

- **2026-10-21 Outbox 可用时刻折叠与 EF EventLog 严格二进制投影（公共 API 快照 +9/−8）**：①**Outbox 单谓词到期**——`next_attempt_at` 成为唯一可用时刻：新建写显式值或存储层写入时刻，租约时写 `locked_until` 同值，重试/入队写退避时间；查询改为 `status=0 AND next_attempt_at <= @now ORDER BY next_attempt_at, created_at`，`locked_until` 只保留 fencing 与持租回读。四栈（Dapper/PalORM/EF/InMemory）同步，索引改为 `(status, next_attempt_at, created_at)`（PG/SQLite 部分索引）。迁移、回填、零违例校验与回滚见 `docs/sql/migration-2026-10-21-outbox-next-attempt-due.md`。②**EF EventLog 严格二进制投影**——`UseBinaryEventId=true` 时只 SELECT `EventIdBin`，文本模式只 SELECT `EventId`；NULL 行抛 `InvalidOperationException`，不再静默回退空 ID；新增 `CountMissingBinaryEventIdsAsync` 预检，共享 `StoredEvent.CreateRecordedEvent` 转换。验证：`check-all` IDE 0 / CA 0 / 编译 0；全量 1777 个测试，1700 通过、77 跳过、0 失败；Dapper AOT 探针 PG/MySQL 各 32/32（含新建可用时刻与租约折叠）；NativeAOT SQLite 32/32；100k Outbox 迁移探针回填零违例，PG 6.95ms→0.34ms（20.15×）、MySQL 196.91ms→0.61ms（324.82×），EXPLAIN 均命中新索引；EF SQLite 二进制投影与 NULL 预检测试通过。未做项与风险见 `docs/review/perf-round-2026-10-21-outbox-binary-read.md`。

- **2026-10-21 独立审计 P0/P1 修复（无公共 API 变化）**：①**Dapper PG 租约释放**——`ReleaseLeasesAsync` 的 `saga_id IN @ids` 在 Dapper.AOT + Npgsql 下被绑成单个数组参数并生成 `IN $n`，PG 报 `42601`；改为运行时构造显式 `@id0, @id1, ...` 列表，三方言同形。wslc 真库探针 PG/MySQL 各 30/30 通过（含 3 参/4 参释放、SaveMany 持久化）；PalORM 方言临时探针 PG/MySQL 各 9/9 通过；NativeAOT SQLite 探针 30/30 通过。②**Dapper/PalORM `SaveManyAsync` 单语句多行**——已有行按版本合批为一条 UPDATE（PG `FROM (VALUES ...)`、SQLite `FROM (SELECT ... UNION ALL ...)`、MySQL `JOIN (SELECT ... UNION ALL ...)`），每块 500 行、SQLite 90 行；整块命中才回写内存 Version/Error，部分冲突整块返回 0。新增 100 行跨块测试两件。③**自定义 Saga store 轮转告警**——未实现 `ISagaScanCursorStore<TState>` 时进程内首次 Warning，提示释放清 `leased_until` 可能导致超过批大小的活跃 Saga 饿死。④**PalORM 保留清理编码测试**——SQLite 真库写入 provider 原生时间戳，锁定同日 23:00 行不被当日 00:00 cutoff 删除。验证：`check-all` IDE 0 / CA 0 / 编译 0；全量 1775 个测试，1698 通过、77 跳过、0 失败。未做项与理由见 `docs/review/perf-round-2026-10-21-dapper-release-batch.md`（Outbox 可 sargable 到期列、EF EventLog binary 读投影）。

- **2026-10-21 第四轮独立审计修复（公共 API 快照 +13/−6）**：①**PalORM 保留清理 opt-in**——三个 `AddPalOrm*` 移除无条件 `AddPalRetention()`，默认只注册 `IRetentionStore`，清理后台服务需显式启用（与 `RetentionOptions`/`AddPalRetention` 文档一致）；新增 DI 锁定测试。②**SQLite 保留时间编码**——`RetentionSql.ToTimeParam` 增加 `sqliteProviderNative` 可选参数：EF/PalORM 的 SQLite 分支写原生 `DateTimeOffset`（空格格式），Dapper 保持 `"O"`；修复同一天行被提前最多 24 小时删除的缺陷，新增 EF SQLite 边界测试。③**Saga 扫描轮转**——新增 `ISagaScanCursorStore<TState>`，四栈批量释放时把 `leased_until` 写成本次扫描时刻；legacy 租约按 `leased_until NULLS FIRST, created_at` 排序，活跃 Saga 超过批大小时不再永远只扫前 N 条；无 schema 变更，新增四栈轮转测试。同时补 `saga_states(leased_by, leased_until)` 租约回读索引（PG/SQLite 部分索引、MySQL 复合索引、EF 模型），迁移见 `docs/sql/migration-2026-10-21-saga-lease-holder-index.md`。④**Dapper/PalORM 单语句批量推进 NextCheckAt**——按 300 行分块构造多行 UPDATE（PG `FROM (VALUES ...)`、SQLite `FROM (SELECT ... UNION ALL ...)`、MySQL `JOIN (SELECT ... UNION ALL ...)`），替代逐行往返；事务边界与 owner/version 守卫保持。⑤**EF EventLog append 清理**——按本批 `EntityEntry<StoredEvent>` 精确 Detach，allocator 由 `EventLogPositionReserver` 保存成功后自清，消除每次 append 两次全 ChangeTracker 扫描。⑥**Outbox 排空**——`Task.Delay(1ms)` 改 `Task.Yield()`（本机实测 1ms 延迟平均 11.46ms），Auto 标记阈值从固定 16 改为 `min(BatchSize, 400)`。⑦**StreamRunRegistry**——无心跳配置时无限等待信号，去掉空闲 500ms 轮询。⑧**NullMessageBroker**——声明 raw/并发能力并实现空 `PublishRawAsync`，单节点 Outbox 跳过反序列化后再丢弃。验证：`check-all` 0/0/0；全量 1772 个测试，1695 通过、77 跳过、0 失败；快照 +13/−6。未做项与原因见 `docs/review/perf-round-2026-10-21-audit-fixes.md`（Outbox 可 sargable 到期列、EventLog binary 读投影、Saga 元数据投影/回读索引、Dispatcher 类型索引、幂等冲突单读、Kafka 窗口、SSE 批 flush 等）。

- **2026-10-20 Saga/Kafka 后台性能轮（公共 API 快照 +14/−0）**：①**Saga 批量写入**——新增 `ISagaBatchStateStore<TState>`：`AdvanceNextCheckAtAsync(owner, sagas, ct)` 只写 `next_check_at + version`（owner 与逐行 version 双守卫），`SaveManyAsync(sagas, ct)` 批量保存完整状态并返回同序逐行结果。Dapper/PalORM 整批一个事务，EF 整批一次 `SaveChangesAsync`（`BumpVersionOnModifiedSagaStates` 只扫描一次 ChangeTracker，消除 256 批下的 O(n²) 扫描），InMemory 锁内批量更新；`SagaTimeoutProcessor` 的到期推进、NextCheckAt 回填、补偿终态改为环后统一冲刷，未实现能力接口的自定义 store 回落逐条路径，逐 Saga 告警与补偿指标按批量结果保留。②**Kafka 消费线程**——专用线程只执行阻塞 `Consume`，结果经无界 Channel + `SemaphoreSlim` 同步背压（最多 1 条处理中 + 1 条排队）交给异步 worker；原 `LongRunning` 只覆盖首个同步段、后续阻塞 `Consume` 占用 ThreadPool 的问题消除，关停时先取消并等待专用线程退出再 `Close/Dispose`。③**幂等单调用**——`DapperIdempotencyStore` 与 `PalOrmIdempotencyStore<TProvider>` 实现 `IIdempotencyStartOrGetStore`（Insert 优先，冲突后回读完整记录），新 key 与过期/失败回收路径省掉处理器侧前置 `GetAsync`。④**JsonLines UTF-8 直读直写**——导出改为单个池化 `Utf8JsonWriter` + 单块 `ArrayBufferWriter` 按 64KB 批量写流；导入按字节找换行并用 `Utf8JsonReader` 解析，去掉 `StreamReader`、`StringBuilder`、`string` 与 `JsonDocument.Parse(string)` 的整行转码链。⑤**EF SQL Server Saga 租约**——新增 `WITH candidates AS (SELECT TOP (@n) ... WITH (UPDLOCK, READPAST, ROWLOCK)) UPDATE ... INNER JOIN candidates` 原生路径与 `(owner, until)` 等值回读，替代 `Skip/Take` 深偏移回落。验证：`check-all` 0/0/0；全量测试 1765 个，1688 通过、77 跳过、0 失败（Integration 437 / Transactions 236 / PalORM 172 / Core 323 / DI 178 / Projections.EventLog 12）；新增锁定测试覆盖四栈 Saga 批量路径、Dapper `StartOrGetAsync` 两态与 JsonLines 大载荷跨 64KB 边界/CRLF/坏行。未执行：Kafka 真 broker 运行时验证、SQL Server 真库租约四形态、10k 活跃 Saga 真库复测；投影检查点单行高水位登记不做（回放已按 100 条批量提交，单行化不改变在线 2 写/事件，迁移成本高于收益），详见 `docs/review/perf-round-2026-10-20-saga-kafka.md`。

- **2026-10-20 数据面续轮（保留清理索引、三栈清理、原子批上限、到期排序）**：①**清理索引**：保留清理按 `processed_at` 删除，但旧 DDL 无该列索引。200k 行/100k 已处理实测：PG 21.5ms→0.379ms（57×，部分索引 `(processed_at) WHERE status <> 0`），MySQL 318ms→0.349ms（911×，`(status, processed_at)` 覆盖索引）；SQLite 改用 `(status, processed_at)` 复合索引，避免部分索引谓词推导失败。②**索引整理**：删除不再服务租约/清理的 `idx_outbox_created`，新增 `idx_inbox_processed`；EF 模型同步 Outbox/Inbox 的 `(Status, ProcessedAt)`。③**原子批上限**：PG 多条守卫追加限制在 `PostgreSqlBatchRowLimit=4000`，超出回落预检+分块，避免约 5,041 行时撞 65,535 参数上限；PG 真库 4,500 行追加探针 24/24。④**到期排序**：EF SQLite 到期租约改 `ORDER BY next_check_at, saga_id`，与 PG/MySQL 对齐。⑤**三栈清理**：SQL 与时间编码抽为共享 `RetentionSql`，ADO 执行下沉到 Dapper/PalORM/EF 三个适配包；新增 `RetentionDialect`、`PalOrmRetentionStore<TProvider>`、`EfCoreRetentionStore`、`AddPalRetention`、`AddPalEfCoreRetention`；PalORM 三个方言入口与 EF 注册均接入，`AddPalDapperRetention` 改为薄包装。⑥**导入零拷贝**：`EventStreamJsonLines` 改用 `EventData.FromOwned`，少一次整载荷复制。验证：Integration 420 通过 / 14 跳过、Transactions 234、PalORM 117（54 跳过）、EF 15、Projections.EventLog 9；PG 探针 24/24；`EXPLAIN` 清理子查询无 Seq Scan/Sort。未执行项：投影检查点单行高水位化、M4 文本唯一索引收缩、MySQL 二进制主键迁移、EF MySQL Testcontainers 车道。
- **2026-10-20 数据面性能实施轮（索引、往返、保留清理；公共 API 快照 +35 行）**：①**Outbox 租约索引对齐谓词**：PG/SQLite 部分索引 `(created_at) WHERE status=0`、MySQL `(status, created_at)`，EF 模型同步为 `(Status, CreatedAt)`；真库实测（20 万行、1000 待租）PG 0.279ms→0.061ms（4.6×）、MySQL 12.2ms→1.59ms（7.7×）；迁移脚本含 `CONCURRENTLY` 与回滚，见 `docs/sql/migration-2026-10-20-performance-indexes.md`。②**EventLog 索引**：移除 `idx_events_global`（`global_position` 主键已覆盖 ReadAll 的范围扫描与排序）；M4 `event_id` 收缩的前置校验、唯一索引与回滚写入同一迁移文档，未自动执行。③**EF Saga 租约**：PG/MySQL 从跟踪分页回环改为方言原生 UPDATE + 等值回读（PG 子查询 `FOR UPDATE SKIP LOCKED`，MySQL JOIN 派生表按服务端版本启用 SKIP LOCKED），语句数与批次大小无关；SQLite 原单语句路径保持不变。④**Dapper EventLog 多条守卫追加**：PG `INSERT ... SELECT ... FROM (VALUES ...) WHERE 版本守卫 RETURNING` 单语句完成判定与写入，Any 保持预检路径；真库探针新增四条断言，PG 22/22 通过。⑤**EF 幂等 INSERT 优先**：关系型 provider 新 key 从 SELECT+INSERT 两次往返降为一次，冲突回读现有记录；InMemory provider 保持 SELECT 优先（其重复键以 `ArgumentException` 暴露）。⑥**Dapper 幂等**：显式开连接避免逐命令进出连接池；精确数组响应载荷直接绑定，去掉持久化边界第二次复制。⑦**`EventData.FromOwned`**：所有权转移入口，事件溯源写入少一次整载荷复制；原构造仍防御性复制。⑧**流运行注册表**：容量判定由枚举全部 run 改为原子计数；域事件派发器处理器映射改为首次派发时构建；子 Saga 的 `GetMethod` 查找移入静态字段。⑨**可选保留清理**：`AddPalDapperRetention`（默认不注册、不运行）按 `RetentionOptions` 分批删除已处理 Outbox、已处理 Inbox 与已过期幂等记录，默认保留 Dead Outbox 给运维处置。验证：Integration 417 通过 / 14 跳过（含保留清理 SQLite 真库测试与 PG EventLog 探针 22/22）、EventLog 46、Transactions 234、DI 178、Messaging 33、Hosting 90、EF 15。未执行项与原因：投影检查点单行化（需数据迁移与在线泵改造）、MySQL 二进制主键迁移、PalORM 读路径 raw reader（会丢失读副本路由）。EF PG 租约四形态已用 Npgsql EF 11 preview 6 在 PG 18.6 真库通过 8/8；EF MySQL 全栈已用 Microting.EntityFrameworkCore.MySql 11.0.0-preview.1 在 EF Core 11 rc2 + MySQL 8.4.11 通过 8/8（含 `ExecutedStepKeys` 往返；provider 为 Pomelo 社区 fork，编译于 preview 7，接入 CI 需 test-only 依赖与 Testcontainers 车道）；探针暴露的 `PrimitiveCollection<Collection<string>>` 物化兼容问题已按 ADR-035 修复（属性改为 `List<string>`，PG 真库 8/8，含该列往返）。
- **PalORM 6.6.0 新 API 采用：会话池 DI 选项 + Outbox 整段 COPY（2026-10-20）**：①`AddPalOrm{Sqlite,PostgreSql,MySql}` 新增尾参 `useSessionPool`（默认 false）——true 时注册单例 `IDataSessionFactory<TProvider>` / `DataSessionFactory<TProvider>`，Scoped `DataSession<TProvider>` 经 `RentAsync` 租出、作用域释放归还；归还复位瞬态配置（WithTenant/WithRetry 等），复用槽、读连接与构造税跨租期保留，空闲上限 16（超出部分归还时真实释放）。默认关闭的理由：池化改变会话生命周期（SQLite `Data Source=:memory:` 的库随连接跨请求存活；归还门禁对未收口事务 fail-fast），需消费方显式选择。②`PalOrmOutboxStore.AddMessagesAsync` 改用 6.6.0 单参 `BulkInsertAsync`：PG 取整段行数单次 COPY（2 万行不再付 20 次 BeginBinaryImport/Complete），MySQL/SQLite 内部沿用 1000 批宽。验证：PalORM 面板 117 过 / 0 失败 / 54 跳过（新增池化两件 + 1001 行跨批一件）；公共 API 快照三行签名更新；PG/MySQL 的 `BulkInsertAsync` 单参行为由 CI `dialect-probe` 覆盖，池化 DI 的 PG/MySQL 运行期验证未做（与 SQLite 结构同构，Sqlite 代表性覆盖）。
- **2026-10-20 极致性能轮（回放默认批量、单调用幂等、Rabbit 并发、PG 单往返追加）**：①**回放检查点**——`ProjectionProcessor.CheckpointBatchSize` 默认由 1 改为 100（仅 `ProjectionRebuilder` 消费；在线 `ProcessAsync` 不变），新增 `ProjectionRebuilder.CheckpointFlushInterval`（默认 1 秒，慢回放按时间界提交，避免单组拖过租约），设 `CheckpointBatchSize=1` 回退逐事件。100k 探针检查点调用 200,000→2,000。②**PalORM PG 单事件追加**——PG + 单事件走 `INSERT ... SELECT MAX+1 ... RETURNING` 单往返，保留 Any/NoStream/StreamExists/Exact 四版本守卫与零行/唯一冲突分类；新增 `--palorm-append-pg` 真库探针（wslc PG 全过）。③**幂等单调用**——新增 `IIdempotencyStartOrGetStore`、`IdempotencyStartOutcome`、`IdempotencyStartResult`；InMemory/EF 实现一次返回 Started/Existing/Contended，处理器不再先 Get 再 TryStart；未实现该能力接口的自定义 store 自动回退旧路径。④**RabbitMQ 消费并发**——`RabbitMqBroker.ConsumerDispatchConcurrency`（默认 null=连接默认 1；建议 handler 线程安全/幂等时设 8，prefetch 不小于并发度），共享 channel 模式不生效；新增双 handler 同时在途的集成测试。⑤**Dapper 读取裁剪**——按 `UseBinaryEventId` 只解码实际使用的 ID 列，二进制模式回填行跳过 26 字符文本分配；NULL 行回退文本。⑥**Saga 到期扫描**——到期租约排序改为 `next_check_at, created_at`（Dapper/PalORM/EF），到期未超时行重算并推进 `NextCheckAt`；`EnableDueTimeFilter` 默认仍 false，生产开启仍须先回填。验证：Transactions 234、Integration 415（14 跳过）、Dapper 56、Idempotency 17/10、Rabbit 18/18；公共 API 快照 +27 行零删除。未落地项与原因见 `docs/review/perf-round-2026-10-20.md`。

- **M1/M4 真库演练（2026-10-19）**：新增 `--m1-probe-pg` / `--m1-probe-mysql` / `--m4-probe-pg` / `--m4-probe-mysql` 探针。M1 在旧 schema 上 ALTER 加列/索引后，两方言均验证 `due=1, active-non-due=2`；M4 两方言各播种 500 条旧行，分批回填、抽样 10 条、创建二进制唯一索引并用 `UseBinaryEventId=true` 读回 10 条全过（PG 1095ms、MySQL 2825ms）。命令与结果见 `docs/migration/saga-next-check-at.md` 与 `event-id-binary-column.md`。

- **M1/M4 规模演练（2026-10-20）**：新增 `--m1-load-pg` / `--m1-load-mysql` / `--m4-scale-pg` / `--m4-scale-mysql`。M1 在两方言各播 10,000 条 Active Saga（`NextCheckAt` 在 100s 内均匀到期、2s 轮询/256 批量），租约延迟 PG p99 1996ms/max 2101ms、MySQL p99 2003ms/max 2034ms，均不超过 2 个轮询周期（4000ms），单 tick 最多 202 条（≤256）。M4 两方言各回填 100,000 行：PG 41,031ms、MySQL 31,956ms（约 2.4k~3.1k 行/s）；二进制唯一索引体积 PG 10,010,624B→4,087,808B（−59.2%）、MySQL 4,734,976B→3,686,400B（−22.1%）。实测证伪统一宣称的「索引下降约 2/3」，按方言登记；生产回填、建唯一索引与切读仍未执行。命令与表见 `docs/migration/saga-next-check-at.md`、`event-id-binary-column.md`。

- **M2/M3 量具与正式结果（2026-10-19）**：新增 `--m2-probe` / `--m3-probe`。M2 回放 100,000 条事件统计检查点调用：`CheckpointBatchSize=1` 为 200,000 次、`=100` 为 2,000 次（100×；进程内耗时 120ms→31ms），探针硬断言 ≤2k。M3 在本机 wslc rabbit436 跑 1/4/8 订阅两场景：baseline 下每订阅独立 channel 相对单 channel 的 p99 为 1.05~1.19×（绝对差 ≤0.16ms）、吞吐 0.86~0.99×；isolation 场景（一个订阅每次延迟 2ms）单 channel 把其余订阅 p99 拖到 30.8s/30.4s，独立 channel 为 0.70ms/1.22ms（低约 2.5~4.4 万倍），发布确认两种拓扑均不受慢订阅阻塞（publish 0.7~1.6s）。默认维持每订阅 channel，`UseSharedConsumerChannel=true` 仅作兼容回退。命令与完整表见 `docs/migration/projection-batch-checkpoints.md`、`rabbitmq-channel-topology.md`。

- **M4 EventId 二进制列 expand（2026-10-19 性能迁移）**：`events` 增加 `event_id_bin`（16 字节）可空列与普通索引；EF `StoredEvent`、Dapper `DapperEventLog`、PalORM `EventLogRow` 追加时双写文本 `event_id` 与二进制列。读取默认文本；`UseBinaryEventId=true`（三栈）切二进制，NULL 行回退文本。文本唯一索引保留，冲突分类不变；应用侧分批回填后创建二进制唯一索引。DDL/回填配方/切读/回滚见 `docs/migration/event-id-binary-column.md`。验证：PalORM 114（54 跳过）、Integration 412（14 跳过）全绿，新增双写与二进制读回测试；快照更新。

- **M3 RabbitMQ 通道拓扑（2026-10-19 性能迁移）**：`RabbitMqBroker` 默认每订阅经 `IConnection.CreateChannelAsync` 创建独立 consumer channel，发布 channel 只承担发布与 publisher confirms；`UseSharedConsumerChannel=true` 复现单 channel 旧行为。订阅句柄释放顺序改为 cancel → dispose consumer channel → dispose linked CTS，失败路径同样释放。本地 RabbitMQ（wslc rabbit436）Messaging.Integration 18/18 全绿；回滚与验收见 `docs/migration/rabbitmq-channel-topology.md`。

- **M2 投影批量检查点（2026-10-19 性能迁移）**：`ProjectionProcessor<TMessage>.CheckpointBatchSize`（默认 1）>1 时，`ProjectionRebuilder` 按组调用 `ProcessBatchAsync`，组内只在最后位置做一次 TryStart/MarkCompleted；失败时同时把组首标 Failed，续读从组首重放而不是组末之后，崩溃最多重放 N 条。无 schema 变更，N=1 保持旧路径。语义/用法/验收见 `docs/migration/projection-batch-checkpoints.md`。验证：Integration 412（14 跳过）全绿，新增批量成功与组内失败续读两件测试。

- **M1 Saga 到期索引 expand（2026-10-19 性能迁移）**：`SagaState` 新增 `NextCheckAt`，由 `Saga<TState>` 在步骤开始/完成时计算（当前状态各 `StepStartedAt + Timeout` 的最小值，终态写 null）；`SagaProcessorOptions.EnableDueTimeFilter`（默认 false）开启后，实现 `IDueTimeSagaStore<TState>` 的 EF/Dapper/PalORM/InMemory 四栈只租约 `NextCheckAt <= now` 的活跃行，旧全量路径保留为回填与回退通道。`saga_states` 增加 `next_check_at` 列与 `(status, next_check_at)` 索引；DDL/回填/回滚见 `docs/migration/saga-next-check-at.md`。验证：Transactions 234、PalORM 114（54 跳过）、Integration 410（14 跳过）全绿；公共 API 快照更新。

- **ADR-033 Auto 组合真库 A/B（2026-10-19）**：`--delivery-auto-pg` / `--delivery-auto-mysql` 新增 Auto（窗口 16 + 批量标记 16 + raw）与 Serial 对照车道。wslc pg186/mysql84 真库、1000 条积压、3 迭代/2 预热：PG RTT 0/1/20 为 2,778→203ms（13.7×）、7,117→766ms（9.3×，中位 13.5×）、24,639→1,733ms（14.2×）；MySQL 为 6,448→588ms（11.0×）、9,551→803ms（11.9×）、28,061→1,858ms（15.1×）。组合收益 9.3~15.1×，不是 13× 与 6.4× 的乘积；分配下降 40%~58%。迁移类开放决策点的 expand/contract 清单见 `docs/review/perf-migration-plan-2026-10-19.md`。

- **2026-10-19 全链路性能实施轮（ADR-033；公共 API 增量 + 行为默认调整）**：①**Outbox 自动投递**——`OutboxOptions.DeliveryMode` 默认 `Auto`：broker 声明 `SupportsConcurrentPublish` 时窗口至少 16，store 实现 `IBatchMarkOutboxStore` 时标记阈值至少 16，并由 `BatchMarkFlushInterval`（默认 1 秒）加时间上界；`IPalOutboxStore.RequiresSaveAfterMark` 让内置四栈跳过逐消息空 `SaveChangesAsync`；`Serial` 可逐字复现旧行为。②**raw publish**——`IMessageBroker` 新增 `SupportsRawPublish`/`PublishRawAsync`，Outbox 在内容类型一致时直发持久化字节，跳过反序列化与再序列化；Kafka/RabbitMQ 已实现。③**序列化零拷贝契约**——`IMessageSerializer.SerializeToArray` 默认单次拷贝，JSON/MemoryPack 覆写为交还自身数组，消除 Outbox 第二次载荷复制。④**Dispatcher 作用域复用**——新增 `SendAsync`/`QueryAsync` 的 `IServiceProvider` 重载，HTTP 端点传 `RequestServices`，不再在请求作用域内建第二层 scope。⑤**SSE 断线续跑**——附着等待由 `Monitor.Wait` 改为 `SemaphoreSlim.WaitAsync`，空闲附着不再占用线程池线程；run scope 推迟到终态且无附着时释放（异步释放），Sweep 逐 run 异常隔离。⑥**EventLog**——位置预留器信号量唤醒后复查块缓存；PalORM 批量 `RETURNING` 只回传 `global_position` 且走 raw reader；Dapper 超过行上限按块处理（PG/MySQL），读取改 `DbDataReader` 真流式；EF 审计空值复用 `EventAuditMetadata.Empty`。⑦**Saga/EF/消息**——Saga 新建保存去掉 COUNT 探测（3→2 RTT，唯一冲突统一返回 0）；EF 拦截器按 `Core.Entity` 类型过滤并只清理本轮命中实体，同步钩子对立即写 store 走单次批量；Kafka 消费改 LongRunning 专用线程，消费端与 RabbitMQ 均走泛型反序列化。验证：Release 全量 0 警告 0 错误；17 个测试项目全绿（本机跳过 76 个 Docker/外部 broker 用例）；公共 API 快照已更新；文档 ADR-033、usage/features/performance 同步。

- **断线续跑能力补强：遥测 + POST 启动形态 + AOT 烟测（公共 API +3 行纯增量：`StreamAttached`/`StreamGap` 两个计数器与 `MapDetachedStreamQuery` POST 重载）**：①**遥测**——注册表泵开启 `Stream Query` Activity（tag 为查询/元素类型名），首个产出项记 `first-item` 事件（TTFT = 事件时间戳 − span 起点），终态复用五个 `paldd.stream.*` 计数器（取消独立于失败，与 MapStreamQuery 同口径）；新增 `paldd.stream.attached`（成功附着数，含首次/重连/接管）与 `paldd.stream.gap`（gap 帧写出次数）。run 未附着时仍计 started：产出由注册表泵驱动，与客户端是否在看无关。②**POST 启动形态**——`MapDetachedStreamQuery` 增 `JsonTypeInfo<TQuery>` 重载，反序列化段复用 `ReadJsonBodyOrWriteErrorAsync`（415/畸形 JSON/空 body 形态与 MapStreamQuery POST 逐字一致），GET/POST 共用 `StartAndAttachAsync` 单一编排，解决 prompt 类请求体超过 URL 长度的缺口。③**AOT 烟测**——AotSample §8 在 PublishAot 产物中真起 Kestrel，覆盖 POST 启动 → 终态回放附着 → 幂等取消 204，纳入 CI `aot-verify`。④**文档勘正**——ADR-031 API 草案的 `MapStreamRunAttach` 选择器参数与 `formatFrame` 说明按实现修正；features/usage/tutorial/README 同步七项流式计数器与 agent 端点，usage 增授权/限流/CORS 部署说明（框架不校验 run 所有权）。锁定测试 7 件（遥测 4：完成/失败/取消/附着与 gap；POST 3：happy path/415/畸形 400）；Hosting 面板 88/88、AotSample 全过。快照 +3 行零删除。

- **SQLite FTS5/JSON1 双栈 API（ADR-032，公共 API 破坏式重写，随 v5.0.0 发布）**：`PalDDD.Dapper.Sqlite` 与 `PalDDD.PalORM.Sqlite` 各自暴露同语义的 FTS5/JSON1 定义与片段 API，共享 `src/PalDDD.Shared` 三个 internal 源码链接核心；PalORM 包不引用 Dapper/Npgsql/MySqlConnector。FTS5 新增 `SqliteFtsColumn` 与 `SqliteFtsIndexDefinition`（`ToSql`/`JoinClause`/`RebuildStatements`/`OptimizeStatements`/`DropStatements`），支持 external-content（INTEGER rowid）与 standalone（映射表 + 三触发器，TEXT/ULID 主键可用）两种形态；`CreateEventLogIndex`/`CreateOutboxIndex` 默认 standalone 且把 `payload` 以 `CAST(... AS TEXT)` 入索引；`ValidateAsync` 在 DDL 前校验源列与 external-content 主键类型；触发器名改为 `fts_{清洗名}_{FNV-1a64}_{ai|ad|au}`，消除清洗碰撞；新增 `MatchParameter`/`MatchExpression`/`OrderByRank(weights)`/`Snippet`/`DropLegacyStatements`。JSON1 新增引号路径 `Path`/`Extract(column, params path)`（支持 `.`、`[]`、空格，键内 `"`/`\` 转义）、`ExtractText`/`ExtractJson`、`ArrayLiteral`/`ArrayParameters`、`ObjectLiteral`/`ObjectParameters`、`Set/Insert/Replace/Patch/Remove` 参数化写入、`GroupArray`/`GroupObject`、`EqualsLiteral`/`EqualsParameter`；全部标识符、参数名、路径段入口 fail-fast。PalORM 侧新增 `SqliteFtsSessionExtensions`（`EnsureIndexAsync`/`RebuildIndexAsync`/`OptimizeIndexAsync`/`DropIndexAsync`/`IndexExistsAsync`/`EnsureEventLogIndexAsync`/`EnsureOutboxIndexAsync`），查询侧文档给出 `FormattableStringFactory.Create` 与原生 `DbCommand + @q` 两条配方。测试：Dapper FTS/JSON 29 件（含质量评审补充的路径单引号/注入形态与 JOIN 别名契约 3 件；外部内容与 standalone 的增删改同步、BLOB 转文本、TEXT rowid 拒绝、特殊键与 BLOB JSON、参数化 MATCH/bm25/snippet）+ PalORM 6 件（EventLog/Outbox 建索引后检索、生命周期、两种查询配方）+ 双栈门面同输入逐字对等 4 件（ADR-032 D2，防单侧演进分叉）；`--sqlgen` 基准新增 Path/ObjectParameters/CreateEventLogIndexDefinition/MatchParameter 四项（实测 `Sqlite_JsonExtract` 344B/op、`Sqlite_JsonPath` 248B/op、`Sqlite_FtsMatchParameter` 208B/op）。文档：ADR-032、`docs/migration/sqlite-fts-json-v5.md`、usage.md 新节、README/features/architecture 与 PalOrmSample AOT 运行时验证。快照随本批更新。

- **断线续跑（ADR-031 实施）：run 注册表 + 有界环形缓冲 + 附着/取消端点（公共 API +19 行纯增量）**：`MapStreamQuery`（ADR-030）的语义是连接断开即取消整轮，长 agent run 切页/弱网会白烧已产 token。新增 `AddDetachedStreamRuns` + 三个端点：`MapDetachedStreamQuery`（启动并附着，响应头 `X-Run-Id`）、`MapStreamRunAttach`（重连附着，遵守 `Last-Event-ID` 先回放缓冲再转 live）、`MapStreamRunCancel`（幂等取消）。**run 独立于 HTTP 请求**：断开只 detach，取消仅来自 cancel 端点/`DetachGrace` 宽限超时/`MaxRunDuration`/`MaxItems` 四条显式路径。**单写者 + 单附着 + 按项计数环形缓冲**（默认 1024），溢出丢最旧并在重连首帧发 `event: gap`（带 `oldestIndex`）。**run 自有 `IServiceScope`**：run 活过请求作用域，handler 从 run 自有 scope 解析、终态确定性释放（补上 ADR-030 被否「dispatcher 持有对象变体」缺失的释放责任；`RunScope_IsDisposedAfterCompletion` 用 scoped 探针锁定）。**接管语义**：新附着先给旧连接发 `event: detached` 再关闭，不返 409（`RejectNewAttach` 可切回）；`TryBeginAttach` 立即 `Pulse` 唤醒旧附着（否则接管帧要等 Wait 超时才送达）。**明确不是 EventBus**：单写者单附着、无订阅模型、无跨请求投递保证，EventBus 移除判决不受影响。**进程内状态**：重启丢进行中 run，多实例需会话亲和（已知约束）；完成态审计与回放仍走 EventLog（`Last-Event-ID` 即 `fromVersion`）。锁定测试 20 件（注册表 11：泵与缓冲/溢出丢弃与最旧可用前移/cancel 终止与幂等/未知 run/`MaxItems` 预算/并发上限/宽限取消/自定义 id 反查/滚出位置 gap/`RejectNewAttach`/接管代际；HTTP 9：`X-Run-Id` 与帧序/断线后按 `Last-Event-ID` 续读/gap 先于数据帧/取消幂等与终态回放/未知 run 404/终态回放缓冲后 done/接管 detached/`RejectNewAttach` 409/scope 释放）。AgentSample 增第 6 段（真 HTTP 启动→断线→重连，17 断言全过）。文档：ADR-031 状态转正含实施附记、conventions §1.11、usage.md §8、pitfalls PD11（SSE `Dispose` 挂起/连接池异常态/收尾帧无 id 三条实测）。快照 +19 行零删除。

- **agent 场景能力补齐轮：流式遥测 + SSE 测试助手 + agent 参考样本 + 回放续看参考（公共 API +6 行纯增量：`PalActivitySource.StartStreamQuery` + `PalMetrics` 五个 `Stream*` 计数器）**：①**流式遥测**——此前 `PalDiagnostics` 对 `Stream*` 零覆盖，agent SLI（首 token 延迟/项吞吐/错误率/取消率）无法量化。新增 `StartStreamQuery(queryType, itemType)` Activity（tag 为类型名，代码有界基数）覆盖全枚举期，首个元素写出时记 `first-item` 事件——**TTFT 由该事件时间戳与 span 起点之差给出**（绕开「零 tag 计数器无处放维度」的纪律约束，与 ITM-229 高基数列裁决一致）；新增五个零 tag 计数器 `paldd.stream.started/completed/failed/cancelled/items`（取消独立于失败：客户端断开是正常终止，混入 failed 污染错误率告警）；失败路径置 span 状态 `Error` 且描述为异常类型名（无消息/业务数据）。验证失败在 SSE 头写出前返回，不进枚举也不计 started。②**SSE 测试助手** `PalDDD.Testing.SseFrameReader`（此前流式测试每处手写解析）：纯 `Stream`/`TextReader` 输入，支持多行 data 拼接、event/id 行、注释帧（心跳）识别、断线截断半帧产出；零新依赖。③**agent 参考样本** `samples/PalDDD.AgentSample`（13 条断言实跑）：流式产出（event/id 分型 + 心跳）→ 工具调用经 `IdempotencyProcessor` 幂等（同键重试副作用只发生一次）→ 流结束追加一条 EventLog 审计记录（不逐 token 写，D6 分工）→ 回放端点以同帧形状 + `Last-Event-ID` 即 `fromVersion` 实现断点续看。④usage.md 增「回放与断点续看」与遥测两段 + tutorial 指标清单补五项 + README/features 计数同步（23→28 指标、11→12 Activity）+ 快照 +6 行零删除。

- **流式读 P1 性能轮：帧载荷序列化池化（默认/富化帧每项 56B→0B，零公共 API 变化）**：`--stream` 基准显示每帧 921ns 中框架可控 CPU 约 9%、每项分配里框架可控的仅剩序列化的 56B byte[]（首测结论，docs/performance.md）。将 `WriteItemFrame` 两分支的 `JsonSerializer.SerializeToUtf8Bytes` 换为 `internal static StreamFrameSerializer`：ThreadStatic 池化 `Utf8JsonWriter` + `ArrayBufferWriter<byte>`，每项 Clear + Reset 后 Serialize + Flush，取 `WrittenSpan` 交 framing span 直写（framing 路径零改动）。形态复用 `JsonMessageSerializer` 的 R8/ADR-029 池化路径（含 v27/v29 重入限制声明与 R9 的 256KB 弃槽纪律，Hosting 不引用 Serialization 包故 25 行小 helper 内联并注释指向先例）。**实测**（medium 档复测，同 run 对照）：`SerializeAndFrame_Pooled` 59.68ns/**0B**（P1 前 85.89ns/56B，且快于 byte[] 单独序列化 76.21ns）；`PerFrame_Default_WithFlush` 845.09ns/336B（前 920.80ns/390B）；端点 16 异步项请求 **12.62μs/5,952B**（前 15.87μs/6,848B，每项 −56B 与预测逐项吻合，−13.1% 分配）。至此默认帧路径 framing 与序列化双零分配，ADR-030 D4 声明获完整实证；心跳竞速开销重标为每异步项 +231ns/+209B（P1 后口径）。锁零分配测试三件（每项 0B/输出与 SerializeToUtf8Bytes 逐位一致/池内 span 连续两项不串内容）+ **变异红证明**（池化换回 byte[] 返回 → 零分配断言判红，恢复转绿）。ADR-030 D4 与实施附记、conventions §1.10、`.ai/perf-ledger.md` 表 2/表 4 同提交更新（含 P2 观察态：富化帧常量事件名 UTF-8 记忆 +23ns/帧，低于「<5% 默认不做」线；P3 四项不做附实测依据）。

- **流式读 Phase 2 收窄实施：心跳 keepalive + event/id 富化帧 + Last-Event-ID 重连入口（ADR-030 分期节实施附记，快照 +2 行零删除）**：按「对 AI agent 流式场景的用途」逐项评估六项 Phase 2 后收窄实施三项（评估与收窄论证全文在 ADR-030 分期节实施附记）。**心跳**（`MapStreamQuery` 新增可选参数 `keepAliveInterval`，默认 null 关闭）：agent 流的静默间隙属结构性常态（首 token 前可停几十秒、工具轮间隙达分钟级），中间设备空闲断连（nginx 默认 60s、部分云 LB 4min）会在工具轮之间掐断长 run；静默超过间隔即写注释帧 `:keepalive`（SSE 规范内客户端忽略、不计入 item 数、不影响审计）。实现为手动枚举器 + MoveNext 与心跳延时竞速：同步完成项走 `IsCompletedSuccessfully` 快路径，默认（无心跳）路径保持 Phase 1 await foreach 零额外开销；取消经延时取消态优先于心跳，挂起项落地（迭代器 finally 由状态机执行）后统一走 OCE 静默收尾，等待而非丢弃挂起任务避免未观测任务异常。**共享延时修正**（bench 设计评审期发现并修）：心跳延时由「每挂起项新建一个 `Task.Delay`、项胜出后丢弃」改为「每流一个在途延时、只在真正写出心跳时重建」——项胜出时旧延时留用（间隔自上次心跳起算，保活语义等价），消除高速流下被丢弃延时的计时器堆积（实测前每 op 级积累、修后每流一个且随 ct 取消整体释放）。**event/id 帧**（新增可选参数 `eventSelector`/`idSelector`）：复活触发器 2 提前满足（多家 LLM API 原生说分型 SSE，formatFrame 每消费方一份即 fork 前兆）——框架富化帧 `event: {名}\nid: {id}\ndata: {json}\n\n`（缺项行省略，event/id 字符串 UTF-8 直写 span 零分配，帧写出收口 `WriteItemFrame` 单一出处）；`formatFrame` 保持最高优先级（完整帧覆写）。**Last-Event-ID 入口**（新公开类型 `StreamResumeExtensions.GetLastEventId()`）：只做 EventSource 重连环 `Last-Event-ID` 请求头读取与 id 行写出，续传语义留应用层（框架不缓冲已产出项；实时生成走整轮重开，持久流回放的位置游标即天然事件 ID）。**收窄暂不建三项**（触发条件未满足，论证在 ADR 附记）：PipeWriter 零拷贝化（bench 实测证伪触发前提，见下段）、per-stream Channel 缓冲（基础形态纯开销，迟附/扇出需流注册表独立设计）、dispatcher 集成 + `IStreamPipelineBehavior`（无流级横切消费方）+ WebSocket（单向流无增益，ADR 明示另立议题）。AotSample §7 增 typed 端点（event/id + 40ms 心跳 + 120ms 静默间隙探针，喂 aot-verify）。锁定测试 10 件（Hosting：event 帧/id 帧/双选择器行序/formatFrame 优先/心跳静默间隙到达且早于慢帧/取消优先无 error 帧且 finally 执行/间隔零负映射期守卫/GetLastEventId 读取与空值/null 守卫/富化帧 framing 零分配）。**实现期一处实证 bug 修复**：`WriteDataFrame` ID 分支 `IdPrefix.CopyTo(span)` 漏 `[written..]` 偏移（id 前缀覆写缓冲区头、冲掉 event 行）——双选择器行序测试当场抓出（单选择器路径不经过该分支），已修。源兼容性：`MapStreamQuery` ×2 追加可选参数（源兼容；二进制层面已编译消费方需重编——Phase 1 同处 [Unreleased] 未随版本发布，无已编译消费方，二进制兼容不适用）。

- **流式读链路 bench 证据轮（2026-10-18，`--stream` 新入口 + `StreamReadBenchmarks` 11 lane，零公共 API 变化）**：为 ADR-030 Phase 2 收窄论证补实测数字（框架开销层口径：DefaultHttpContext 直调 RequestDelegate + 内存 PipeWriter/丢弃流，无网络与 DB；medium 档）。三条结论：①**PipeWriter 零拷贝化触发前提证伪**——默认帧 framing 5.19ns/0B 仅占「序列化 + flush」每帧 921ns 的 0.6%，零拷贝对照形态（Utf8JsonWriter 直写 PipeWriter）CPU 持平（77.9 vs 78.4ns）而分配 2.4×（136B vs 56B，writer 内部缓冲 churn 重于序列化器单次 byte[]）、带 flush 慢 5%，收窄获双向证据（论证回写 ADR 触发器 3）。②**心跳竞速每异步项 +417ns/+209B**（端点 16 异步项请求 15.87→22.54μs、6,848→10,186B；共享延时修正后 Task.Delay 不再每项新建），相对 LLM 流 10~50ms/token 延迟地板占 0.001~0.004%、1000 项/s 高速流也仅 ~0.04% CPU；同步完成项走快路径零开销。③**富化帧每帧 +23.3ns CPU、零额外分配**（UTF-8 直写 span），请求级 +8.4%。实测入账 docs/performance.md「流式读链路首测」节。量具排障三条实证：预分配 PipeWriter 替身须在 Advance 近容量时复位（否则微 lane 跑几千帧后 Destination too short）；BDN LaunchCount=2 两次启动不能绑同一固定端口（Launch 1 套接字释放时序致 AddressInUse，端点 setup 用 127.0.0.1:0）；`dotnet build ... | tail` 掩蔽构建退出码会让 `--no-build` 跑陈旧二进制（本轮因此废跑一轮，后续台命令改为显式校验退出码）。

- **流式读形态 `IStreamQuery<TItem>` / `IStreamQueryHandler<TQuery,TItem>` + SSE 端点原语 `MapStreamQuery` ×2（ADR-030，公共 API 新增 +6 行纯增量）**：agent 场景的 LLM token 流回传获得框架级入口。**D1 独立读形态**：标记接口刻意不继承 `IBaseRequest`——Dispatcher 的 per-request scope 随管道完成销毁（`Dispatcher.ExecutePipelineAsync` 的 using scope）而 `IAsyncEnumerable` 枚举晚于销毁，枚举期触碰 scoped 依赖（仓储、DbContext、流结束追加 EventLog）即 ObjectDisposedException（运行期地雷，故障形态取决于 handler 体内触碰什么）；`QueryAsync` 因此无法接受流式查询，「禁止经 dispatcher 路由流式查询」由编译器强制而非用户纪律。**D2 端点即作用域边界**：handler 从 `HttpContext.RequestServices` 解析、枚举在端点方法执行期内完成，请求作用域天然覆盖全枚举期。**D3 验证前置**：`ValidationBehavior` 泛型/非泛型两路径的验证核心抽为 `internal static PalValidatorRunner`（PalDDD.CQRS 内，IVT 链对 PalDDD.Hosting.AspNetCore 可见），与端点验证前置三处共用单一出处（含「IsValid=false 且 Errors 为空时构造占位条目」v25/v48 两轮勘正语义）；失败 400 ProblemDetails 与 MapCommand/MapQuery 同形态，且在 SSE 头写出前（状态码可回退）。**D4 帧最小化加可定制**：默认帧 data-only JSON，framing 三笔 `ReadOnlySpan` 拷贝经 `PipeWriter` span 直写（无字符串中转；序列化器输出的 JSON 载荷字节按项产出，「零分配」指 framing/write 段）；可选 `formatFrame` 委托返回完整帧文本（event/id 行）支持分型事件流。**D5 取消贯穿**：handler 契约响应 ct（`[EnumeratorCancellation]` 约定成文——接口层携带该特性被编译器拒绝 CS8424，故以 XML doc 约定 + 实现侧标注），端点传 `context.RequestAborted` 并经 `WithCancellation` 兜底；客户端断开静默收尾（无 error 帧、无 500），取消传播进 handler 的 finally（停计费、释放 provider 连接）。**D6 审计分工**：框架只记机械生命周期（流开始/结束/item 数/错误，`IPalLogger<T>` 内部标记类别 + `IsEnabled` 门控 + `TimeProvider` 可注入，与 LoggingBehavior 语义对齐）；响应内容审计由 handler 在流结束后追加 EventLog——框架不自动捕获载荷（隐私、体积、领域语义三样均属应用层）。**D7 注册**：`AddPalStreamQueryHandler<TQuery,TItem,THandler>` 闭合泛型 DI + 自动补 `AddPalDDD`；同一 `(TQuery, TItem)` 注册两个不同 THandler 注册期快速失败（ITM-220 同族裁决；检测面为 `IStreamQueryHandler<,>` 服务描述符扫描，覆盖手工 `AddScoped` 注册——宽于命令/查询的 Marker 哨兵）。**D8 端点双形态**：GET（`bindQuery` 绑定，浏览器原生 EventSource 兼容）+ POST（JSON body 承载 prompt 类请求体，415/畸形 JSON/空 body 错误形态与 MapCommand 逐字一致——`ReadJsonBodyOrWriteErrorAsync` 泛型化复用，消费方约束进 usage.md）。中途异常补 `event: error` 帧后结束（SSE 头已写出无法回退 500；帧内文案通用化，异常详情只进 Error 日志）。AotSample §7 真起 Kestrel、真发 HTTP、真读帧序列（GET/POST/验证 400 三断言，喂 CI aot-verify）。锁定测试 31 件：CQRS 10（取消传播进 finally/正常完成清理/`WithCancellation` 消费者令牌优先/IStreamQuery 不实现 IBaseRequest 类型系统防线/PalValidatorRunner 正负例五件含占位条目语义与泛型非泛型两路径一致）/ Hosting 14（帧序帧格式与 SSE 响应头/POST happy path/验证 400 且头未写出/客户端断开取消传播/中途异常 error 帧与帧序/POST 415/POST 畸形 400/formatFrame 定制帧/参数守卫/D6 三件含 IsEnabled 门控与 Error 记录/零分配断言/帧字节形态）/ DI 7（解析与具体类型/scoped 跨 scope 实例性/自动补核心注册/重复快速失败含消息双类型名/同 handler 幂等/手工注册检测/参数守卫）。**变异红证明**：`StreamFrameWriter.WriteDataFrame` 临时替换为字符串形态（Serialize → 插值帧串 → GetBytes）→ 零分配断言判红（1000 帧 ~150KB），恢复 span 形态转绿（量具为预分配固定缓冲的 PipeWriter 替身——实测真实 `StreamPipeWriter.FlushAsync` 每帧 ~131B 内部分配属管道缓冲段管理，已从断言目标隔离）。快照 +6 行零删除。

- **序列化直写重载 `IMessageSerializer.Serialize(object, descriptor, IBufferWriter<byte>)`（ADR-029，R8/P1-1，公共 API 新增 +2 行纯增量）**：消除每消息发布路径的**全载荷精确尺寸 `ToArray()` 分配 + 整载荷拷贝**（1KB 消息 1 万 msg/s ≈ 10MB/s Gen0 churn，审计确认的每消息最大单项分配）。接口 DIM 默认实现为内存形态 + 单次拷贝（**自定义序列化器零迁移成本**）；`JsonMessageSerializer` 覆写为真直写（池化 `Utf8JsonWriter` 经 `Reset(destination)` 直写调用方缓冲，输出与内存重载逐位一致）；`RabbitMqBroker` 发布路径改 TLS `ArrayBufferWriter` + `WrittenMemory` 直传（前提：RabbitMQ.Client 7 在发布完成前已消费 body——AMQP 帧组装即拷贝）。Kafka 不改（Confluent API body 为 `byte[]`，ToArray 不可避免，其发布处勘正注释在案）；MemoryPack 暂不覆写（1.21.4 IBufferWriter 重载仅泛型形态、T=object 动态语义未验证——P0 #4 纪律，走 DIM 回退，ADR-029 延后段）。调用形态约束：DIM 成员经具体类型不可见（红证明实证 CS1501），消费方须持接口引用（RabbitMqBroker 字段即接口类型 ✓）。锁定测试三件（字节等价/零分配/ClrType 守卫；**变异红证明**：覆写体替换为回退 → 零分配断言 56B/op 判红恢复绿）。快照 +2 行零删除。

- **`IResumableProjectionCheckpointStore` + `IResumableEventReplaySource` 投影增量回放续读（ADR-028，R6/P0-3，公共 API 新增 +9 行纯增量）**：双能力接口把 `ProjectionRebuilder.ReplayAsync` 的增量回放从 **O(全流长度) 降为 O(增量)**——原形态恒从 version 0 重读全流（每事件一次 payload 物化 + 一次 checkpoint TryStart 探查），现取检查点续读位置（**最小未完成位置**：Failed/僵尸 Processing 的最小数值位置——与「仅处理缺失或失败的检查点」契约逐字一致；全完成 → max+1；无行 → null 全量）下推回放源起始位置，已完成前缀零重读零探查。四栈实现：InMemory / Dapper（单语句方言 CAST 聚合）/ EF（服务端聚合优先；SQLite 不翻译 `Convert.ToInt64(string)` 实测——回退最小投影两列客户端聚合）/ PalORM（raw command）；`EventLogReplaySource` 加 `ReadAsync(sourceName, fromPosition)` 重载（全量收敛为 fromVersion=0 同源）。非破坏：store/源任一侧或自定义实现不具备能力时自动全量路径（`RebuildAsync` 先 Reset 天然全量）。Position 数值契约与脏数据回退语义见 ADR-028。锁定测试九件（InMemory 三态五件 + Rebuilder 续读观测两件含**变异红证明** + Dapper/EF SQLite 真库三态两件）。快照 +9 行零删除。

- **`IBatchMarkOutboxStore` 终态批量标记（ADR-027，R4/P0-2，公共 API 新增 +8 行纯增量，T2.1 收益锚立项）**：新能力接口 `MarkProcessedBatchAsync(messages, processedAt, ct)` → 受影响行数——成功路径的 `MarkProcessedAsync` 批量化（N 条已发布消息按 RetryCount 分组单语句，标记往返 N 次 → 组数次）。实现：`DapperOutboxStore` / `OutboxDbContext` 族（EF）/ `InMemoryOutboxStore` / `PalOrmOutboxStore`（PalORM 栈 2026-10-16 清算 ADR-027 延后段后补齐，until 编码三方言探针实证，见下条）。fencing 守卫与逐条路径逐字等价（`(Id, RetryCount)` 快照 + 租约 token + Status=Pending，分组形态四实现同构零窄化）；同租约批前提防御（混批/未租批 fail-fast）；全命中才内存六字段回写、部分拒绝零回写。配套 `OutboxOptions.BatchMarkThreshold`（默认 1 = 逐条路径逐字不变，opt-in 启用；语义边界与 `MaxInFlightPublishes` 同族：标记时点延迟到满阈值/批末，at-least-once 契约内重复窗口随阈值增大，消费方需 Inbox 幂等；失败路径恒逐条即时；冲刷失败镜像逐条路径退避重投；串行与 T3.1 流水线两路径同款接入）。动机与量具锚：T0.2 实测 RTT20ms 档逐条确认串行化占投递耗时 80%。锁定测试：处理器集成五件（`OutboxBatchMarkProcessorTests`）+ InMemory fencing 三件 + Dapper/EF/PalORM SQLite 真库十四件（`OutboxBatchMarkSqliteTests`，本地实跑；PalORM 腿另锁定 RetryCount 分组与 400 行分块边界）+ PalORM 多方言六件（`PalOrmOutboxMultiDialectTests`：三方言各「批量正腿 + until 前移负腿」，SQLite 本地、PG/MySQL 由 CI dialect-probe 车道执行）+ PalORM 能力/opt-in 门断言一件（`PalOrmOutboxStoreTests`）。快照 +8 行零删除。

- **PalORM 栈 `IBatchMarkOutboxStore` 补齐（ADR-027 延后段清算，2026-10-16，公共 API +1 行纯增量）**：原延后理由为批量 WHERE 的 `until` 参数编码须与上游 PalORM FormattableString 租约绑定逐字节一致（方言陷阱面）。清算方式 = 三方言 until 编码探针（SQLite 本地 + PG/MySQL wslc 外部实例）：租约 3 条 → 回读 until 走候选批量 UPDATE，正腿命中全批；until±1s / 换 owner / retry+1 / 已终态四负腿全拒（谓词选择性证明：非恒真非恒假）。实证库内形态：SQLite TEXT「…+00:00」（100ns 全精度）/ PG timestamptz（μs，第 7 位截断）/ MySQL DATETIME(6)（μs、无 offset 裸 UTC）——回读值重编码均精确命中；**同值以 DateTime(Utc) 形态传递三方言全部拒命**（编码形态敏感，实现以 DateTimeOffset 传 until，禁简化为 UtcDateTime）。实现：`PalOrmOutboxStore.MarkProcessedBatchAsync` 按 ADR-027 已定案分组单语句形态，`FormattableStringFactory.Create` + `{N}` 索引孔构造动态 IN 列表（PalORM 每插值孔只绑一个 DbParameter，PalOrmEventLog A11 先例），组内 400 行分块（SQLite 999 参数上限防御）。**变异红证明两轮**：①批量路径关停回落逐条 → fencing 零回写契约与方言 until 负腿判红 2 项；②until 编码破坏为 UtcDateTime → 全部正腿判红 5 项（负腿仍绿——正腿才是编码承重锁）；还原后两项目全绿。

### Changed 变更

- **[Breaking] `SagaState.ExecutedStepKeys` 由 `Collection<string>` 改为 `List<string>`（ADR-035，2026-10-20，随 v5.0.0 发布）**：Npgsql EF 11 preview 6 对 EF `PrimitiveCollection` 的物化类型是 `List<string>`，原 `Collection<string>` 属性在 EF PG/MySQL 读取 `SagaState` 时抛 `InvalidCastException`（PG 18.6 真库探针首次运行实证）。改为 `List<string>` 后属性类型与 provider 物化类型一致，JSON 线格式与 EF 列结构不变，无数据迁移；常规 `Add`/`Contains`/`foreach`/索引器用法不变，显式赋值 `Collection<string>` 的消费方需改为 `List<string>`。`CA1002` 在属性级精确抑制。验证：PG 18.6 + Npgsql EF 11 preview 6 租约四形态 8/8（含 `ExecutedStepKeys = ["Start","Step1"]` 往返）、MySQL 8.4.11 + Microting 11.0.0-preview.1 全栈 8/8（含该列往返）、SQLite EF 15/15、Integration 417 通过 / 14 跳过；公共 API 快照属性行同步。迁移与备选方案见 ADR-035。

- **变异测试调度由月度改为每周（2026-10-19）**：`.github/workflows/mutation.yml` 的 cron 从每月 1 日 04:00 UTC 改为每周一 04:00 UTC（Asia/Shanghai 周一 12:00）；两个 curated 基线（CompressionProvider、Transactions/MySqlLeasePolicy）、break=95、JSON/HTML artifact 与手动触发不变。动机：月度间隔过长，变异回归最坏可潜伏约一个月。

- **SQLite FTS5/JSON1 片段生成分配优化（2026-10-19，零公共 API 变化、行为逐字保持）**：`--sqlgen` 同机 A/B 实测分配 `JsonExtract` 344→128 B（−63%）、`JsonPath` 248→120 B（−52%）、`JsonObjectParameters` 440→176 B（−60%）、`MatchParameter` 208→64 B（−69%）、FTS 完整定义 14.5→9.9 KB（−32%）。实现：标识符/字面量/JSON 路径/函数调用/数组/对象/等值谓词改为先算精确长度、单次 `string.Create` 写出，去掉 `Replace` 中间串与 `StringBuilder`；`NormalizeParameterName` 对已带 `@` 的名称返回原实例；FTS 列名只转义一次、重建 SELECT 列表用预转义数组；`MatchLiteral`/`MatchParameter` 单次分配；`SqliteFtsIndexDefinition` 构造期缓存默认 `JoinClause`。测试 27+6 全过，质量分析器 0 警告。剩余 `JsonExtract` 128 B 含调用方 `params` 数组（约 32 B），再降需单段路径重载，属公开 API 扩张，本轮不做。

- **EPSL 地毯审计 2026-10-16 B 批十二项落地 + 两项证伪（2026-10-16 当晚，用户令「按报告最优方案优化实现」；零公共 API 变化，行为逐字保持）**：按 `docs/review/epsl-carpet-2026-10-16.md` 第六节处置表实施。①**Saga 租约 RETURNING 化**（Dapper+PalORM 双栈 PG/SQLite 单语句 `UPDATE ... RETURNING *`）：省回读往返并消除两步回读的同 tick 混批窗口（ITM-109 同族）；`CreatedAt` 客户端稳定排序保留原回读 ORDER BY 契约；MySQL 维持两步；wslc pg186 真库实证（RETURNING 行集=锁定批、同 tick 二次租约不混入）；公共 API 快照 +2 行零删除（SqlTemplates 两新常量）。②**DapperEventLog MySQL k>1 批化**（用户裁决注释级登记重开）：A11 延后的两条理由实证均不成立——wslc mysql84 8.4.11 mode 2 实测已知行数多行 VALUES 自增块连续（5 行 min/max=1/5 无间隙、LAST_INSERT_ID=首行值），且实现不依赖连续性（多行 INSERT + 版本区间回查 MIN/MAX 两往返，k≥3 净胜）；批插构造提共享 helper（PG RETURNING 与 MySQL 范围回查同源）。③**DapperEventLog PG 单事件三版本守卫化**：快路径从 Any 扩到 Exact/NoStream/StreamExists（INSERT..SELECT 守卫谓词，失匹零行插入无异常→重查实际版本转并发异常）；wslc pg186 store 级四形态验证并**抓修 Exact 守卫差一**（current=n 误写 next=n）。④**Idempotency 两项**（Dapper+PalORM 双栈）：回收路径 PG/SQLite 改 `UPDATE ... RETURNING revision` 单语句（MySQL 维持两步）；冲突路径轻量读四列（status/locked_until/expires_at/revision）免 response_payload 双传，过期/CAS 语义逐字保持。⑤**投影检查点直插优先**：关系型新位路径 3 语句→2（镜像 Inbox A9），冲突才回查分类；SQLite 语句计数测试旧码红→绿。⑥**InMemorySagaStateStore 活跃索引**：GetActive/Lease 扫描 O(活跃)（终态条目按 GetByIdAsync 行为契约保留），Add/SaveChanges 受理点自纠索引成员。⑦**生成器 O(N) churn 收口**：duplicateHints/Combine 的 hint/identity 上移 transform 预计算（工厂 + 10 处构造点改道），任一候选变化时对全部 N 候选的字符串重算消除。⑧**CQRS ValidationBehavior 验证器枚举零分配化**：JIT 探针实证非空 validator 集接口 foreach 32B/请求，构造期物化数组（Dispatcher MaterializeBehaviors 同款先例）；对照组增量分配测试旧码红→绿。⑨**DecompressBufferPool 驻留上限弃槽**：R9 四池同族漏格补齐（256KB 阈值），两契约测试锁定弃槽重建与小额复用。⑩**生成器 Equals 内 string[] LINQ 手写化**：SequenceEqual 枚举器分配改零分配循环（与 FieldsEqual 同构）。**两项证伪不实施**（实测数据入台账）：Saga 批量释放 IN 列表分块（捆绑 SQLite 3.53.4 变量上限实测 32766，配置可达 10k 上限三方言正常，审计引用的 999 是 SQLite 3.32 前旧知识；分块反使大批付 25 次往返）；DapperBulkCopy SQLite 多行 VALUES（本地 1000 行实测逐条复用 4ms vs 多行 14ms，慢 3.5×）。验证：Release 全量构建 0W0E + check-all 0/0/0 + 16 测试工程 1564 通过 0 失败（含本轮新增锁定测试；76 容器探针按 T-17 跳过）+ 文档门禁全过。踩坑记录：Dapper 行映射类须 internal（private 嵌套类型在动态 IL 发射下属性赋值走错路径致列错位，分配断言红后定位修复）。

- **EPSL 地毯审计 2026-10-16 A 批十项落地（2026-10-16 节点审计修复轮，零公共 API 变化、行为逐字保持）**：按 `docs/review/epsl-carpet-2026-10-16.md` A 批逐项「旧码必红测试 → 修 → 绿」动线实施。①**KafkaBroker 发布路径双 ToArray 收单**（头条，A13 假闭环清算）：133 行 TLS 直写后 ToArray 与遗留的 149 行 `value.ToArray()` 相抵致 A13 对该路径净效果为零（旧码分配断言实测 2264B/迭代），`Value` 改直传 byte[]；142-148 注释「唯一一次托管拷贝」失实一并勘误。红证明：`KafkaPublishAllocationTests`（IProducer 假件 + 内部构造 seam；分配断言旧码 2264B/迭代判红、新码绿，另锁 Key/Value/Headers 行为契约）。②**MessageConsumeContext correlation-only 空字典**：无头 + 兜底 correlation 形态（RabbitMQ 主流消费形态）免字典构造，空头视图换缓存只读单例；A-1 补的是「无头且无兜底」格，本项是其姊妹漏格。红证明：stash 旧码分配断言判红（~136B/迭代 vs 预算 70B）。③**SagaTimeoutDetector 常态扫描路径三笔分配全消**：无超时路径旧码 96B/次（try 外无条件空 List 32B + `[]` 集合表达式每次求值新建空数组 32B + 接口枚举器 32B，红证明分解探针实证），新码下标循环 + 惰性 List + Array.Empty 单例零堆分配；分配断言旧码红新码零。④**Saga Dynamic 车道 routeFailures 惰性建**：try 前无条件空 List（R3 四车道共享骨架惰性化的漏格姊妹）改首个失败才建；对照组增量设计（Dynamic 路径减普通路径分配，吸收 Debug/Release 配置基线）旧码 +32B/迭代判红。⑤**RecordedEvent payload/metadata 双拷收单**：InMemory 追加路径别名 EventData 构造期副本（`PayloadArray`/`MetadataArray`，v65 P3 别名共享声明在案，StoredEvent 同构先例），每事件省一次全载荷分配+拷贝；红证明 stash 旧码 2998B/事件判红、新码 1974B 绿。⑥**PDDD004 链检查形参重用**：调用方已算的 `chainBoundedContext` 形参直接使用，删除内联重算的基类链遍历（纯 CPU）；随带删除失去调用方的 `HasAttributeAlongBaseChain` 死代码。⑦**生成器 partial 检查闭包消除**（EnumGenerator/IdentityGenerator 各一处）：内联 `Any` lambda 捕获 ct 每次 transform 分配 display class + delegate，改调 `NamedTypeSymbolExtensions.IsPartial` 零分配参照（Modifiers 走 Roslyn `SyntaxTokenList.Any(SyntaxKind)` 重载）。⑧⑨**两处 CodeFix 消中间串/双扫**：`StartsWith(prefix + ".")` 拼接改 `CodeFixHelpers.StartsWithPrefix` 零分配判定（长度门控 + StartsWith + 分隔符位，与 BelongsToBoundedContext idiom 同构）；`AddVersionSuffixCodeFix` 剥离循环 IsMatch+Replace 双次正则全扫并为单次 Replace（无匹配时返回值与原串相等即停，双后缀 x.v1.v2 回归测试在案）。⑩**IMessageSerializer 注释勘误**：原称 MemoryPackMessageSerializer「覆写为真零拷贝」与实现的明确不覆写声明（ADR-029 延后段）直接矛盾，按实现事实重写。验证：Release 全量构建 0W0E + check-all（format/CA/编译）0/0/0 + 16 测试工程 1627 个测试 0 失败（76 个 PG/MySQL 方言探针按 T-17 无 Docker 自动跳过；含本轮新增 8 个锁定测试）+ 断言强度棘轮绿（两处新测试的 IsNotNull 弱断言按「新增测试必须行为断言」纪律改为直取属性，未抬基线）。

- **B5 GZip/Deflate Stream 管线 TLS 池化（2026-10-15 性能实施轮 S1，零公共 API 变化、行为逐字保持）**：先实测后实施（台账表 2 的 span decoder 否决前科针对解码器重写形态，本项为 Stream 对象池化的不同机制）。`TlsStreamPool` 三槽（压缩输出/解压输入/解压输出）TLS 复用 `MemoryStream`——原每次 `new MemoryStream` 从 0 倍增到输出尺寸（累计约 2× 输出的重分配拷贝），复用后稳态零倍增；v53 预设容量语义由 `Capacity` setter 单次扩容保留，驻留超 256KB 弃槽重建（R9 同族取舍）；压缩流以 `leaveOpen:true` 构造（否则 Dispose 连带销毁池化流）；同步方法内 Rent→使用，同线程同步段内完成无交错。实测（`GZipStreamAllocationProbeTests`，16KB 载荷 2000 次均值）：可压缩载荷 GZip ~1.6KB/op（输出近乎零，池化空间小）；不可压缩载荷（真实场景）旧形态 ~48KB/op（倍增链 ~32KB + ToArray 16KB），池化后收敛到 ToArray 量级。锁定测试两条（随机 16KB 压缩/解压 ≤20KB/op 预算；**变异红证明**：恒新建形态下两条判红）。既有往返正确性 33 件零回归。往返输出逐位一致（Stream 语义不变，仅对象复用）。

- **B4 探针：EF Outbox 零变更 SaveChanges 往返实测（2026-10-15 性能实施轮 S1，结论=不做，零公共 API 变化）**：`OutboxBatchProcessor.PersistSingleAsync` 每条消息处理后调 `SaveChangesAsync`——EF 栈的 Mark*/ReleaseForRetry 走 ExecuteUpdate 直写 DB（ChangeTracker 无待存实体），该调用的实际成本此前无实测。探针 `OutboxPersistSingleProbeTests.SaveChanges_AfterExecuteUpdateMark_IssuesNoDbCommands`（SQLite 真库 + DbCommandInterceptor 命令计数，租约+批量标记后 Reset 再 SaveChanges）：**零 DB 命令**——EF 对零变更 SaveChanges 不开连接、不发 BEGIN/COMMIT（`AutoTransactionBehavior.WhenNeeded` 的短路）。结论：PersistSingle 空转证实，「能力接口消空转」的收益仅剩每消息一次方法调用 + try/catch 框（ns 级），不构成新增公共 API 的理由，**B4 不做（实测零收益）**；探针测试留存锁定该行为（未来 EF 版本若改变零变更短路语义，本测试判红）。原计划中「若探针发现隐藏往返则升级 P0」的分支未触发。

- **S1 SourceGen 增量管线三项 + 两处零散消浪费（A14+A15，2026-10-15 性能实施轮，零公共 API 变化）**：①**A15 生成器 AddSource 移入 per-candidate Combine 节点**（IdentityGenerator/EnumGenerator 同构改造）——原 Collect 输出回调在任一候选变化时对全部候选重跑（每候选一次模板渲染 + AddSource，N 个带特性类型的解决方案每次编辑付 O(N) 渲染）；Combine 只对「该候选变化或重复集合变化」触发，未变候选零重渲染。重复 hint 的 P2 修复契约逐字保持（仅首个出现生成、其余报 PALID004/PALENUM005，「恰好一份生成物」由 SourceGeneratorDirectTests 断言锁定——首现判定用 (语法树路径, span 起点) 组合标识，netstandard2.0 兼容形态）；hint 计算提取为 IdentityHint/EnumHint 单一来源（去重节点与 AddSource 节点共用）。②**A15 MessageRegistry 同键双 GroupBy 收口单遍字典**——duplicateTypeNames 与同类型异名诊断原各做一次 GroupBy(TypeName)（同键相邻两趟），单遍 Dictionary 分组后同轮完成两件；Name 维度 GroupBy 保留（跑在 RemoveAll 后较小集合，键序保证优于字典枚举，诊断顺序零漂移）。③**A15 IsDomainEventType 跨方法双求值收口**——AnalyzeNamedType 与 AnalyzeMessageContractRules 原各算一次（基类链 + AllInterfaces 全名比对，每类型每轮固定开销），改由调用方算一次传参。④**A14 NullMessageBroker Debug 日志补 IsEnabled 门控**（四处）——IPalLogger.Debug 收预构造字符串，插值在调用点已发生；对齐两个真 broker 的 Z-1 模式。⑤**A14 InMemoryEventLog 读路径**——Where+Take+ToList 全表线性过滤改二分定位起始下标 + GetRange 只拷所需段（流按版本/位置单调追加，语义逐字等价）；每事件 Task.Yield 降频为每 64 事件一次（P3-SRC-106 防饿死动机由周期让出保留，大规模回放的纯调度税按 64× 收敛）。**A14 候选项「EF 拦截器 ChangeTracker 全量扫描收缩」经论证否决**：只枚举 Added/Modified 会漏掉「属性未变但 RaiseEvent 的 Unchanged 实体」（领域事件可在无跟踪属性变更时抛出），属静默事件丢失，不安全，不入库。验证：Release 全量构建 0W0E（生成器随全仓编译实证）+ format-verify 绿 + Core.Tests 323/323（含生成器直测 37 件，两个重复声明契约测试锁定行为保持）+ Analyzers.Tests 51/51 + EventLog.Tests 42/42 + Messaging.Tests 31/31。

- **S1 Kafka 发布消双 ToArray + PalORM/Dapper EventLog 批量化 + Dapper SQLite 租约 RETURNING 化（A11+A12+A13，2026-10-15 性能实施轮；A12 公共 API +1 行纯增量）**：①**A13 KafkaBroker**：发布路径改 TLS `ArrayBufferWriter` + IBufferWriter 直写重载，只留 Confluent `Message.Value` 的 byte[] 契约必需的一次 ToArray——内存重载返回前的 `WrittenSpan.ToArray`（第一次分配+整载荷拷贝）消除，每消息省一次全载荷分配+memcpy（TLS 数组在 await 前已定型，无跨 await 共享缓冲问题）。②**A11 PalOrmEventLog**：RETURNING 能力方言（PG/SQLite）改多行 VALUES INSERT ... RETURNING 单语句，k 事件往返 k+1 降为 2（MAX 预检 + INSERT）；RETURNING 全部列映射 EventLogRow 取 GlobalPosition min/max 作首末——PALORM005 告警注释声称的「批量无法保证单调有序 + RETURNING 每行」对 min/max 口径不成立（返回值只需首末），注释同提交勘正；MySQL 等无 RETURNING 方言维持逐行 InsertAsync（多行批化的位置回查需 2 次额外往返，仅 k≥4 净胜，且自增值连续性在 innodb_autoinc_lock_mode=2 下不保证、first+k-1 推导不成立——登记延后）。冲突分类（ITM-075）批粒度同款保持。③**A12 DapperEventLog**：批量化门控扩展 SQLite——多行 VALUES+RETURNING 是 SQL 标准形态（3.35+，Microsoft.Data.Sqlite 支持；仓内 InsertSqlite 单行 RETURNING 与 PalORM SQLite 租约同形态均在用），B-4 原注释「SQLite 自增无多行 RETURNING 语义」不成立（其引用的三方言实测是 INSERT..SELECT 子查询形态，SQLite +44% 劣化，与多行 VALUES 形态不同，不构成否决）；行上限 64（每行 13 参 + 2 共享参 = 834 < SQLite 变量数 999 防御）；MySQL 维持逐条（同 A11 理由）。④**A12 DapperOutboxStore**：SQLite 租约改 `OutboxLeaseUpdateSqliteReturning`（UPDATE ... RETURNING *）单语句——省 `OutboxSelectByLease` 回读往返，且按行锁语义返回刚锁定行，**消除 ITM-109 同 tick 混批窗口**（两步回读按 (locked_by, locked_until) 匹配会带回上一批遗留行致重复投递；与 PG 路径同语义）。新增公共 const 一条（快照 +1 行零删除）。锁定测试：`DapperStoreTests.EventLog_AppendAsync_BatchMultipleEvents`（k=3 行为锁定：版本连续/位置首末/读回全量）+ `DapperStoreTests.Outbox_LeasePendingMessages_SameTickSecondLease_NoStaleRows`（固定时钟同 owner 同 tick 二次租约只返回新行；**变异红证明**：关闭 RETURNING 分支回两步形态后二次租约回读到 2 条判红，ITM-109 窗口实证且消除）；PalORM EventLog 多方言 15 件（SQLite 5 本地绿、PG/MySQL 10 件 Testcontainers 车道）。

- **S1 Inbox EF 直插优先（A9，2026-10-15 性能实施轮，无公共 API 变化）**：`InboxDbContext.TryStartProcessingAsync` 删除预 SELECT，关系型 provider 新消息路径由 2 往返（SELECT miss + INSERT）降为 1（INSERT），与 Dapper/PalORM 两栈的 insert-first 形态对齐（EF 原为三栈唯一预查形态；回查即原预 SELECT 职责，仅在唯一约束冲突路径执行）。**权衡声明**：重复投递路径由 1 往返（预查命中即返回）变为 INSERT 失败 + 回查（2 起），净收益取决于首投/重投比——典型 at-least-once 重投率（<5%）下净胜，>50% 时净负；Id 为自增主键，重复投递的失败 INSERT 在身份列上烧号（间隙无害，与两栈既有成本一致）。仅关系型生效（唯一约束强制是关系型保证；InMemory 等无唯一约束 provider 保持预 SELECT，否则重复投递静默建第二行——活跃租约复投用例实证该边界）。冲突处理收口单一代码路径（瞬时异常 Detach 重抛 / 唯一冲突回查 / 回查失败保留原始异常语义均不变）。锁定测试 `InboxEfCoreTests.TryStartProcessingAsync_NewMessage_OnRelationalProvider_InsertsWithoutPreselect`（SQLite 真库 + DbCommandInterceptor 命令计数：0 SELECT + 1 INSERT；变异红证明：迫使预查形态后 SELECT=1 判红）；既有 14 件 Inbox 用例全绿（实施过程中两个既有用例先后捕获 InMemory 无唯一约束与冲突 try-catch 收口两处真实缺陷，测试网有效性获实证）。

- **S1 Saga 域三项内部优化（A6+A7+A8，2026-10-15 性能实施轮，零公共 API 变化、行为经既有表征网锁定）**：①`RecordExecutedStep` 同实例合轨守卫——六调用点中五处传 (current, current)（步骤原地改状态），旧形态对每个已执行 key 做一次线性 Contains（同实例下必然命中、零添加），k 次执行累计 O(k²) 次字符串比较；同实例时只剩 stepKey 存在性判定，跨实例合并（Normal/Dynamic 车道）改 HashSet 去重（O(k)，该路径每 saga 每步一次，一次分配换平方级比较下降）。②`FanOutStep` 去 `Task.Run`——每子项一次线程池跳转（工作项 + Task 对象 + 大捕获闭包）对 I/O 型执行体（`Func<TItem, CT, ValueTask<TResult>>` 典型形态）无并行收益，只增加分配与调度延迟；改本地 async 方法直接启动（并发度由信号量与执行体异步等待点决定，取消语义经 WaitAsync(ct) 等价 surfacing）；附带修复构造函数里 `_concurrencyGate` 重复赋值（复制粘贴残留，第一个实例即垃圾）。③ChildSaga 反射链查找缓存——每 attempt 原 3 次 GetMethod + MakeGenericMethod + Invoke + GetValue 中的查找构造部分按类型缓存（`ConcurrentDictionary<Type, MethodInfo/PropertyInfo>`），Invoke 语义不变。验证：Transactions.Tests 224/224（含 FanOutStepTests 并发敏感用例与 Saga 表征族）。**A10（Saga PG 单语句租约）未入码**：仓内无 EF PG provider 包（Directory.Packages.props 的 Npgsql.EF/Pomelo 已按死条目清理，dialect-probe 走 Dapper 栈），SQL 形态无法编译验证，按仓库纪律登记留待 EF 真库车道（R7 决策文档同款延期理由与形态预案在案）。

- **S1 EF EventLog append 往返再收敛 + 热点查询编译化（A3+A4+A5，2026-10-15 性能实施轮，无公共 API 变化）**：①删显式事务——`AppendCoreAsync` 内仅一次 SaveChanges，单语句自身原子；EF Core 7+ 默认 `AutoTransactionBehavior.WhenNeeded` 对单语句不开显式事务（微软官方文档实证省 BEGIN/COMMIT 两次往返），多事件批次下显式与隐式事务同为 BEGIN+COMMIT 呈中性；附带收益：失败 INSERT 不再使显式事务进入 aborted 状态，冲突分类的重查降级路径（ITM-126 的 PG 25P02 分支）失去触发条件。②NoStream 期望跳过 MAX 预查（**仅关系型 provider**——唯一索引强制是关系型保证；空流约定版本即 -1，插入首条版本 0 撞 (StreamName, StreamVersion) 唯一索引即证明流已存在，冲突分类路径与预查命中同一语义；InMemory 等无唯一约束的 provider 保持预查，否则陈旧 NoStream 期望会静默重复建流）。稳态往返：NoStream 建流 1（INSERT）、Exact/Any/StreamExists 2（MAX+INSERT），原 4。③五个热点固定形状查询（EventLog MAX / Idempotency 读与 TryStart / Checkpoint 读与 TryStart）改 `EF.CompileAsyncQuery` 编译委托，**按 IModel 引用分缓存**（编译委托与单一模型绑定是 EF 官方约束——同上下文类型跨 provider 模型实例不同，单静态委托跨模型执行抛 InvalidOperationException，该约束由对照腿测试执行实证后修复）。锁定测试 `EventLogStatementCountTests` 三件（SQLite 真库 + `DbTransactionInterceptor` 事务创建计数 + 命令级 MAX 计数；变异红证明：还原旧形态两条判红、对照腿 Exact 仍查 MAX 保持绿）。

- **S1 RabbitMQ 发布 body 缓冲按发布租借化（正确性加固，零公共 API 变化）**：发布路径 body 缓冲由 ThreadStatic 直传改为 ArrayPool 按发布租借（`PooledBodyWriter`：IBufferWriter 直写 + 超越容量倍增成长 + Dispose 归还）。依据 RabbitMQ.Client 7.2.2 官方源码实证（`Channel.BasicPublish.cs` / `SessionBase.cs`）：`BasicPublishCoreAsync` 在确认配额租约（outstanding confirms 超过 ThrottlingRateLimiter 上限即挂起，`CreateAsync` 默认上限 128）与流控检查两个可挂起 await 之后，才经 `TransmitAsync`→`SerializeToFrames` 同步拷贝 body；R8 原注释「BasicPublishAsync 完成前已消费 body」只覆盖拷贝发生的事实，不覆盖拷贝时点晚于两个可挂起点的事实。同线程流水线发布（Outbox `MaxInFlightPublishes>1`）的下一次入口 Clear 会抹掉尚待拷贝的共享缓冲，帧照常组装、confirm 照常成功，消息静默错内容。租借形态把缓冲生命周期收敛到单次发布调用内（rent/return 约 40ns）。锁定测试 `RabbitBodyBufferTests` 四件（并发实例隔离/成长保序/分段追加/Dispose 后池可用；红证明为结构性红——旧码无该类型，同 B-5 先例）。

- **R10 Analyzer 符号比对族零分配化（D-F5，2026-10-15 性能实施轮，零公共 API 变化）**：`StrategicDddAnalyzer` 的 IDE 增量分析热路径谓词去分配——每类型分析发生数十次符号比对（基类链 × 接口集 × attribute 集逐候选），原实现每次比对构造全名串/闭包/前缀串。①`MetadataNameEquals` 改零分配结构比对（命名空间链 + ContainingType 链逐段对照常量名，不构造中间串；改造中发现 Roslyn `MetadataName` 属性对泛型符号每次访问重建 arity 串——探针实测 40B/次，连结构比对自身最初也被该属性判红，改 `Name` + `Arity` 逐段匹配闭环）；②显式接口实现按名查找改手写循环（`Any(闭包)` 形态每次调用分配闭包对象）；③`BelongsToBoundedContext` 前缀拼接与 `HasVersionSuffix` 版本后缀拼接改字符级对照（int 全域等价含负号腿；前导零形态拒绝与拼接版一致）。**分配断言红绿双向在案**（旧形态等价体注入判红 → 新实现绿，B/op）：`MetadataNameEquals` 68.8 → 0.0012 · 显式实现查找 80.0 → 0.0024 · 上下文归属 40 → 0 · 版本后缀 32 → 0。语义矩阵（嵌套名/顶层碰撞/泛型元数/global 命名空间/前导零版本）由新增 `AnalyzerAllocationTests` 锁定；既有 47 用例零回归。配套：`PalDDD.Analyzers` 新开面向测试程序集的 `InternalsVisibleTo`（CodeFixes 侧复刻注释同步更正）。诊断上报路径的 `ImmutableDictionary`/`Diagnostic.Create` 按需分配属冷路径（仅违规时），不在本项范围。
- **R7 Saga EF 租约 SQLite 单语句下推（P0-4，2026-10-15 性能实施轮，零公共 API 变化、行为经回归网锁定）**：`SagaStateDbContext.LeaseActiveSagasAsync` 在 SQLite provider 下改走单语句原生 SQL 租约——集合式 UPDATE（资格谓词子查询 + ORDER BY + LIMIT + `version=version+1` 同句换代）+(owner, until) 等值守卫回读，**整批固定 2 条语句与批次大小无关**（原跟踪分页形态：一页全实体物化查询 + N 条逐行 UPDATE，头部被活跃租约占满时叠加 O(跳过页) 翻页往返，含 saga_data 大列的未选中行同样被物化）。语义逐项对齐（资格集/租约即换代/批量上限/SagaId 取批序）；回读保持跟踪（补偿路径 `ISagaStateStore.SaveChangesAsync(state)` 的 Detached→0 契约前提）；原子性上移单条 SQL（无部分写入；回退路径的 ITM-066/632 Detach 兜底保留不变）。锁定测试：语句计数判别器（**红证明**：旧码批 5 → 5 条 UPDATE 判红、新码 1 条绿）+ 语义三件（活跃跳过/过期接管换代/AWD 纳入）+ 跟踪契约端到端 + ITM-632 用例迁 InMemory（回退路径回归网）。⚠️ **PG/MySQL/SQL Server 等价下推留待 EF 真库车道**（rc 线无可用 provider 包——既有登记；形态已备：PG 为标识符引用差异、MySQL 需 JOIN 两步）。决策论证 `docs/review/decision-2026-10-15-saga-lease-sqlite-single-statement.md`。
- **R9 池驻留收敛（2026-10-14 性能实施轮，零公共 API 变化、行为逐字保持）**：① TLS 缓冲阈值弃槽四处（`JsonMessageSerializer` 两路径 / `JsonLinesEventWriter` / `RabbitMqBroker` body 缓冲）——历史峰值 >256KB 的缓冲在下次入口弃槽重建（256B 起步），稳态驻留从「按历史峰值无限保留」收敛为「≤ 阈值 × 线程数」（小载荷常态零额外成本）；② 解压增长缓冲首轮 `GetSpan` 提示上限 64KB（新 `DecompressionGuard.InitialBlockHintBytes`，Brotli + LZ4/ZStd/OpenZL 四处）——原 `max(4096, len*2)` 在 8MB 压缩输入下首轮提交 16MB 并随线程槽峰值固化，改后峰值容量收敛为「≥ 实际输出的最近倍增」。撤销三项留档：**D-F9（生成 ID 追加 ISpanFormattable）实测证伪后撤销**——net11-rc1 三形态对照（插值 111 / ToString 80 / 手动 TryFormat 80 B/op）：`DefaultInterpolatedStringHandler` 对结构体经该接口的装箱未消除，插值路径反向劣化 +31B，唯一受益面为负收益；勘正注释留 `IdentityGenerator.ToStringBody` 处防重开。RabbitMQ per-delivery CTS 池化撤销（linked CTS 无法 TryReset，语义保持需自定义子类，~72B/消息不值）；五处 `IsUniqueConstraintViolation` 收口撤销（跨 4 程序集 InternalsVisibleTo 链、冲突冷路径，收益不成立）。
- **R5 EF EventLog 追加：Hi/Lo 批量收益修复（P0-1，2026-10-14 性能实施轮，无公共 API 变化）**：`EventLogDbContext.AppendAsync`（关系型）的版本校验与位置预留**前移到事务外**（BeginTransaction 之前）——修复 v33 P3 勘正自认的「Hi/Lo 批量收益失效」：原形态预留恒在事务存活期内执行，`AllocateNewChunkAsync` 恒走降级分支（不发布内存游标）→ 每次追加都 SELECT+UPDATE 分配器行（恒 6 次 DB 往返）且 GlobalPosition 按 chunk 稀疏化。前移后预留自持提交、内存游标即时发布：**稳态追加 4 次往返（原 6，-33%），1/N 追加付分配器 2 次，位置连续无稀疏**。正确性：ITM-226 场景 (a) 化（事件事务回滚只留间隙，类头声明既定可接受）；场景 (b) 内存超前不可达；版本校验移出事务不改变并发语义（乐观检查 + 唯一索引兜底不变）；失败路径语义声明：版本不匹配抛并发异常前已消耗预留位置（间隙，`ReadAll >=` 过滤兼容）。锁定测试 SQLite 真库红绿双向（旧码 +100 稀疏判红、新码 +1 连续绿，stash 循环在案）。
- **R3 Saga 步骤执行分配族（2026-10-14 性能实施轮第三批，零公共 API 变化、行为逐字保持）**：① 四车道（Normal/FanOut/ChildSaga/Dynamic）步骤计时改 `Stopwatch.GetTimestamp`/`GetElapsedTime` 静态对——原 `Stopwatch.StartNew()` 每步骤执行分配一个 Stopwatch 对象（~40B）；② `RunRetryLaneAsync` 的 `failures` 列表惰性建——原成功路径（常态）每次调用无条件分配空 List（~32B），首个失败再建；③ `RecordExecutedStep` 的 `StepStartedAt` 改 `TryAdd` 单哈希查找（原 ContainsKey+索引器双查）。实测（--saga MediumRun）：Normal 车道 368→**296 B/op（-19.6%）**、FanOut 3,064→**2,991 B/op**（两笔预测值 72B 恰好兑现）；计时 192.2ns 在跨轮漂移带内（计时为趋势层）。撤销项留档：RunRetryLane 闭包重构与 MakeKey 缓存——Saga 步骤为 DB 主导业务操作，两项占比 <5%（PERF_MANAGED_DISCIPLINE），且 Saga 为行为最密集核心文件（补偿/观察者/重试契约），收益/风险比不成立。

- **R2 CQRS 管道分配族（2026-10-14 性能实施轮第二批，零公共 API 变化、行为逐字保持）**：① 内置 `ValidationBehavior`/`LoggingBehavior` 显式实现非泛型 `IPipelineBehavior.HandleAsync`——管道热路径免 DIM 桥的闭包+Func 两笔/请求分配（v25 勘正自认形态收口；Validation 非异步直转发零额外分配、Logging 落实例异步方法零闭包捕获；两路径与泛型体行为逐项对齐、验证核心共用单一来源）；② `ICommandHandler`/`IQueryHandler` DIM 桥的 Unit 响应装箱消除——`UnitBox.Shared` 单例箱 + `typeof` 特判经 JIT 值类型实例化折叠（Unit 实例化不再装箱、非 Unit 实例化分支整体消除，同步/异步两路径同款）。实测（--app MediumRun，同机对照基线 268.9ns/432B）：单行为路径 **225.5ns / 360B（计时 -16%、分配 -16.7%）**；零行为路径 160B 持平（该基准命令为引用类型响应，无装箱路径，符合预期）。

- **R1 性能快赢族（2026-10-14 全量重读性能实施轮第一批，九处内部优化，零公共 API 变化、行为逐字保持）**：① `PalActivitySource.StartOutboxProcess`/`StartEventLogAppend` 补 `HasListeners` 守卫前置——旧码 int tag 在实参元组构造处装箱（遥测全关时 24 B/op 纯浪费），守卫前置后 0（smoke 新增两条 ≤1 B/op 断言，旧码 24.000/24.002 判红实证；断言落 bench 进程——测试宿主订阅全局 ActivitySource 使测试侧零分配断言结构性不可行）；② `PipelineStateMachine` next 委托构造期缓存一次（每请求 1 笔替代每行为 1 笔 ~64B）；③ `Dispatcher` 行为物化改数组协变直取（免 `Select+ToImmutableArray` 迭代器/builder 链，1+ 行为场景每请求 ~100-150B）；④ `LoggingBehavior` 失败分支补 `IsEnabled(Error)` 门（对齐既有 Debug 门形态）；⑤ LZ4/ZStd/OpenZL 压缩 options 三档静态缓存（原 record `with` 每调用分配）；⑥ `JsonLinesEventReader` 单字节换行检测改 `IndexOf` 直检（原单元素 SearchValues 无 SIMD 优势）；⑦ `DapperOutboxStore` MySQL SKIP LOCKED 版本判定按连接 CWT 缓存（原每租约解析 ServerVersion 串，镜像 C-15 先例）；⑧ `InMemoryOutboxStore` Processed 终态按引用移除（原 `_messages` 只增不减使租约扫描随历史处理量线性劣化；Dead 保留——RequeueDead 运维入口依赖）；⑨ EF Inbox 首查询/冲突回查 `AsNoTracking`（全部写路径经 Add/ExecuteUpdate，无跟踪消费方）、EF Saga `StepStartedAt` 字典哈希去 OrderBy（DetectChanges 每快照省 O(n log n) 排序）、EF MySQL 租约回读补 `LIMIT`（同 tick 混入截断在本批内）。

- **远程库投递 A/B 车道（--delivery-pg / --delivery-mysql，2026-10-16，B1 复活前提实测，零产品代码变化）**：新增 `OutboxDeliveryRemoteDrainBenchmarks`——投递排空量具的远程库腿（store 走 `RemoteDialectBenchState` 注入的 PG/MySQL 网络真库连接，wslc 外部实例路径 `PALDDD_BENCH_PG_CONN`/`PALDDD_BENCH_MYSQL_CONN` + palddd_bench 库名守卫；MySQL 种子走 LOAD DATA LOCAL 协议，连接串显式 `AllowLoadLocalInfile=True` + 服务端 `local_infile=1`，库级安全门由调用方显式决策）。口径同 `--delivery`（排空 1000 条积压墙钟 + 发布计数守卫 + 假 broker RTT），矩阵 RttMs {0,1} × BatchMark {1,16}，InFlight 固定 1（B1 决策面向默认配置），专用采样档 5/3/1。**实测**：RttMs=0 标记段占排空 **84.3%（PG）/ 83.8%（MySQL）**，端到端 **6.37× / 6.19×**（PG 742.1→116.4ms、MySQL 2,221.7→359.0ms）；单标记往返成本 PG 0.67ms / MySQL 2.00ms（与账本单语句锚同带）；分配 −47% / −57%。RttMs=1 格两方言 A/B 均落在 Error 带内（本机 `Task.Delay(1)` 有效 ~9~10ms/条，标记段被发布延迟稀释），结论为「无可测差异」而非「差异小」。R4 补测段登记的远程库 Not-Tested 就此清算。**B1 复活前提第一项判定**：分形态成立（broker 确认延迟低于解析阈值 PG ~11.8ms/条、MySQL ~35.0ms/条时占比 >5%），另两项（Inbox 幂等可审计 + 时间界冲刷）未满足且重复窗口放大反驳与延迟形态无关，B1 维持不做；部署判据入账（broker 确认延迟与 DB 往返同量级或更低时 `BatchMarkThreshold` 16~64 的 opt-in 收益显著）。数字与判定入 docs/performance.md 远程投递 A/B 段 + B1 裁决文档证据锚点表。

### Removed 移除

- **SQLite FTS5/JSON1 v4 API（ADR-032，破坏式清理）**：删除对 TEXT/ULID 主键不可用的 external-content `CreateOutboxIndex` 旧语义、`Match(index, query)`、`ExtractPath`、`Array`、`BuildObject` 以及返回 `string` 的 `CreateEventLogIndex`/`CreateOutboxIndex`、`Rebuild`/`Optimize`/`Drop` 旧入口；旧触发器名不再生成。升级顺序与逐项替换见 [`docs/migration/sqlite-fts-json-v5.md`](docs/migration/sqlite-fts-json-v5.md)：先执行 `DropLegacyStatements`，再建新索引。PalORM 包首次获得同语义 API。

### Fixed 修复

- **批量 Inbox 告警口径与单条路径对齐（2026-10-22 契约审计 R9，运行探针实证）**：
  ①**批量标记失败失去 1004 且误报 1005**：`TryProcessBatchAsync` 在 `MarkProcessedManyAsync` 抛错时只记无 EventId 的 Error，部分未命中时只记无 EventId 的 Warning，随后仍输出 `InboxBatchProcessed`（1005）与 `pal.inbox.result=processed`；按 1004 配置的告警规则命中不到，1005 又把未落库的批计为已处理。改为与逐条路径同口径：两种情形都记 `InboxMessageProcessedMarkFailed`（1004），span 结果改 `processed-pending-confirmation`，1005 仅在全部成功项都标记落库时输出。
  ②**批量 handler 失败缺 1003**：批量路径逐条 `MarkFailed` 后直接聚合抛出，不发 `InboxMessageFailed`（1003），而 broker 侧只有无 EventId 的字符串。改为按批聚合一条 1003（含失败条数与消费者名），与 1006 的批量口径一致。
  ③**skipped 计数含批内重复 ID**：`skipped` 原为 `batch.Count - claimed.Count`，把认领前已去重的重复 MessageId 计为跳过，使 `paldd.inbox.skipped` 与 span 标签虚高。改为按去重后的 `ids.Count` 计算。
  ④**公共 API 无变化**；usage.md 补批量告警口径与 skipped 语义。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；全量 1855 用例 / 1775 通过 / 80 跳过 / 0 失败；新增 4 个锁定测试（批量标记失败发 1004 且不发 1005、部分未命中同口径、批量 handler 失败发 1003、重复 ID 不计 skipped），修复前四条均红、修复后全绿。

- **Dapper 批量 EventLog 与 Inbox 唯一冲突的 NativeAOT 回归（2026-10-21 R8，wslc 真库 + NativeAOT 探针实证）**：
  ①**批量 EventLog 行类型不可见**：PG `AppendSingleRow` 与 MySQL `PositionRange` 原为 private 嵌套类型，Dapper.AOT 生成代码无法引用，批量追加调用点静默回落经典 Reflection.Emit（NativeAOT 下 `PlatformNotSupportedException`，PG/MySQL 各 exit 127；SQLite 不受影响）。改 internal 后拦截器恢复接管。
  ②**唯一冲突分类器反射失效**：`DapperSqlErrorClassifier` 用 `GetType().GetProperty("Number"/"SqlState")` 鸭子类型判定，NativeAOT 裁剪后属性元数据不可见，MySQL 重复 Inbox 插入的 1062 被误判为非冲突并逃逸。Npgsql/MySqlConnector/Microsoft.Data.Sqlite 已是本包 `PrivateAssets=all` 实现依赖，改为强类型判定（SqlServer 分支保留鸭子类型，本包不引用 SqlClient）。
  验证：win-x64 NativeAOT 探针 SQLite / PG 18.6 / MySQL 8.4.11 各 33/33（JIT 各 33/33 同轮对照）；全量 1851 用例 / 1771 通过 / 80 跳过 / 0 失败；新增 CI `dapper-aot-probe` job（service containers + PublishAot + 三方言实跑）；公共 API 无变化（嵌套类型为 internal）。

- **PG LISTEN/NOTIFY 通知器缺日志门面 + Inbox span 文档契约失实（2026-10-21 契约盘点 R8，审计探针实证）**：
  ①**组件级注册漏网**：`AddPalPostgreSqlOutboxNotifier()` 的 hosted 工厂用 `GetRequiredService<IPalLogger<PostgreSqlOutboxNotifier>>()`，而门面只在 `AddPalOutbox` / `AddPalCoreStack` 注册；仅按该入口装配时宿主解析 `IHostedService` 抛 `No service for type IPalLogger<...>`（R7 家族漏网）。修法：入口调用同一 `PalLoggerRegistration.EnsurePalLoggerFacade()`（Core 授 IVT 给 `PalDDD.Dapper.PostgreSql`），并勘正 XML remarks 的依赖描述（实际解析 `OutboxBatchProcessor`，前置为 `AddPalOutbox`，原写 `OutboxProcessor`）。
  ②**Inbox span 契约纠偏**：usage.md 原称 `Inbox Process` span「仅包含 consumer/result 两标签、结果值三选一」；实际批量路径带 `pal.inbox.skipped`（跳过量），且 handler 成功而 `MarkProcessed` 失败时为 `processed-pending-confirmation`（ITM-180 既定设计）。文档补齐两处，行为不变。
  ③**注释勘正**：`InMemorySagaStateStore` 的 v44「固定排序会饿死」声明已与轮转游标实现矛盾，改为说明 `leased_until` 轮转；未实现 `ISagaScanCursorStore` 的自定义 store 仍有饿死风险（处理器构造期告警）。
  验证：`dotnet build PalDDD.slnx` 0 警告 0 错误；全量 1851 用例 / 1771 通过 / 80 跳过 / 0 失败；新增 3 个锁定测试（notifier 组件级解析、批量 skipped 标签、pending-confirmation 值），每个各做一次变异探针（移除门面调用 / 删除 skipped 标签 / 改 result 值 → 对应用例红，其余绿）。公共 API 无变化。

- **分离流心跳在等待期满分支被 continue 跳过（2026-10-20，S1854 死赋值暴露）**：`StreamRun.StreamToAsync` 原实现给 `heartbeat` 赋值后无条件 `continue`，写 `:keepalive` 的 `if (heartbeat)` 分支永远不可达——`MapDetachedStreamQuery` 的 `keepAliveInterval` 自 ADR-031 起实际未生效。修正为「唤醒直接 continue，等待期满才落下去写心跳并 flush」；新增 `StreamToAsync_IdleWithKeepAlive_WritesHeartbeat` 锁定（旧码红/新码绿）。
- **存量第三方分析器告警归零（2026-10-20）**：`StreamRunRegistry._gate` 的 `object` 锁改 `System.Threading.Lock`（MA0158）；`StreamRun.FinishAsync` 的 `Cancel()` 改 `await CancelAsync()`（S6966）；`DapperEventLog.ReadOrdinals` 与 `OutboxBatchProcessor.DeliveryPolicy` 补 `[StructLayout(LayoutKind.Auto)]`（MA0008）；`Dispatcher` 的 `SendAsync`/`QueryAsync`、两个序列化器的 `Deserialize` 重载归位（S4136）；`PalOrmEventLog` 两处参数化动态 SQL 以 `#pragma warning disable S2077` 定点抑制并注依据。`quality-analyzers` 从 11 条回到基线 0；全量 1749 用例 0 失败。
- **SQLite JSON1 路径段单引号未按 SQL 字面量翻倍（2026-10-19 质量系统评审，ADR-032 重构回归）**：v4 的 `EscapeJsonPathSegment` 对路径键做 `' → ''`；v5 改为引号路径后 `WritePathLiteral` 只处理 `\` 与 `"`，含单引号的键（如 `O'Brien`）会生成 `'$."a'b"'` 这类未终止字面量——执行期语法错误，且路径段若来自外部输入构成 SQL 注入面（实证 `Path("x' OR '1'='1")` 产出可解析的布尔表达式而非单一字符串）。修复：`PathLiteralLength` 与 `WritePathLiteral` 增加单引号翻倍；锁定测试 `Path_QuotesSpecialKeys`（含 `j'k`）、`Path_SingleQuote_InjectionAttempt_StaysInsideLiteral`、`Extract_SpecialKeys_ExecutesOnRealSqlite`（真实 SQLite 取值）。v4 的同名防线由 `SqliteDialectTests.Extract_KeyWithSingleQuote_IsEscaped` 覆盖，ADR-032 删除该文件时未迁移此用例，本轮补回。
- **rc.2 SDK PublishAot 文档文件路径缺陷致 aot-verify MSB3030（2026-10-17，CI run 39002067257 实证）**：IL2070 修复后发布推进到复制步骤再红——rc.2 SDK 在 `PublishAot` + RID 路径下把 `DocumentationFile` 落到非 RID 目录（`bin/Release/net11.0/` 而非 `bin/Release/net11.0/<rid>/`），publish 复制步骤 MSB3030「找不到文件」（普通 `build -r` 落点正常、仅 publish 路径异常；rc.1 时代无此行为，属 SDK 钉扎扶正 rc.2 后暴露的连带面；增量旧产物会掩盖为假绿——本地清洁复现存证）。修复：两个 AOT 样本项目（PalOrmSample/AotSample，均 `IsPackable=false` 非发布诊断件，XML 无消费方）设 `GenerateDocumentationFile=false`，CI 与本地 publish 双修。验证：本地按 CI 原序列（无 RID restore → 带 RID PublishAot → 实跑）双样本 0 错误 + 断言全过。
- **S 轮 A8 反射缓存 IL2070 压制 ID 错配（2026-10-17，CI run 39000562109 aot-verify 实证）**：`DefaultSagaManager.UnboxAndAwaitValueTaskAsync` 的 A8 缓存化把 `t.GetMethod("AsTask")`/`t.GetProperty("Result")`（this=缓存键 Type）纳入 `[UnconditionalSuppressMessage]`，但原压制对象是 IL2075，rc.2 SDK 修剪分析实际诊断族是 **IL2070**（'this' 实参不满足 DynamicallyAccessedMemberTypes），PublishAot 下 2 处 error 致 aot-verify 红（本地普通构建不跑修剪分析故 0W0E 未暴露，46 提交积压 CI 首跑抓出）。修复：同 `[RequiresDynamicCode]` 边界补 IL2070 压制（查找目标为框架稳定成员 ValueTask<T>.AsTask / Task<T>.Result）。验证：本地 win-x64 双样本 PublishAot 0 错误 + 二进制实跑断言全过；rc.1→rc.2 SDK 分析器诊断族差异一并入账。
- **S1 Outbox `MaxDegreeOfParallelism>1` 空租约 Chunk(0) 潜伏缺陷（2026-10-15 性能实施轮）**：空租约（队列排空后每 tick 常态）下 `Math.Ceiling(0/dop)=0` 使 `Enumerable.Chunk` 首行抛 `ArgumentOutOfRangeException`，被 `PeriodicBackgroundProcessor` 兜底 catch 吞掉只记 `OnTickFailed`，排空后持续噪音且每轮失败信号污染。修复 chunkSize 下限 1，空批经 Chunk 产生零分区、`ForEachAsync` 空转。锁定测试 `OutboxParallelPartitionTests` 两件（空租约完成 + 非空批分区正确性，DI 组装提供 per-worker scope 的 `IServiceScopeFactory` 与生产接线同构；变异探针双向：还原旧形态空租约用例判红、修复后 2/2 绿）。

### Dependencies 依赖

- **全量核新（2026-10-20，用户令「当前项目引用的所有库和包、组件全都升级到最新版」）**：四通道实测——`dotnet list package --outdated --include-transitive --include-prerelease`、NuGet 搜索/索引逐包核对（既有 64 处中央钉扎 + 全部解析到的传递包）、.NET 11 官方 release metadata、GitHub Actions releases API。**包线两升**：PalORM ×5 6.5.0 → **6.6.0**（上游 CHANGELOG 明示无公共 API 破坏性变更；行为变化为版本化 `BulkUpdateAsync` 单语句路由、PG `BulkInsertAsync` 默认整段 COPY、MySQL `ToPageAsync` 事务内 DbBatch，本仓适配面由编译 + 三方言测试面板实证）、Meziantou.Analyzer 3.0.312 → **3.0.315**（MIT）。**传递钉扎补齐十一项** `.NET 11` 波次 `System.*`：`System.Composition`（×6）/ `System.IO.Pipelines` / `System.Threading.Channels` / `System.IO.Hashing` / `System.Memory.Data` / `System.Security.Cryptography.ProtectedData` → **11.0.0-rc.2.26504.105**——未声明时解析停在 4.5.0~10.0.3，与「微软 11 线全量同批」意图矛盾；各包均含 netstandard2.0 资产，分析器工程与 net11.0 项目共用。**按 2026-10-17 仓规不钉扎的链内部件**（归顶层包所有）：Verify 链 Argon 0.37.0 / DiffEngine 20.3.1 / EmptyFiles 8.18.2 / SimpleInfoName 3.2.0，FsCheck 链 FSharp.Core 5.0.2，MTP 链 ApplicationInsights 2.23.0 / DiaSymReader 2.2.10，BDN-Azure 链 Azure.Core 1.53.0 / Microsoft.Identity.Client 4.83.1 / System.ClientModel 1.10.0 / Microsoft.Diagnostics.*，及 ZLogger 链 Utf8StringInterpolation 1.3.1 / SQLitePCLRaw 链 SQLite 3.53.4；强制跨主版本会越过父包声明区间，保留父包支持面。**维持项及理由**：MediatR 12.5.0（最新 14.2.0，13+ Lucky Penny 商业许可）、Verify.TUnit 32.0.1（最新 33.3.4，SponsorCheck 商业门/包级禁令 ≥33）依 2026-09-25 开源许可裁决不升；`System.Runtime.CompilerServices.Unsafe` 6.1.2 不升 7.0.0-preview.2（2022 年废弃预览线）；`Microsoft.NETCore.Platforms` 8.0.0-preview.7 为停更前最后版本；CAP ×3 10.1.0-preview-308323494 与 BDN 0.16.0-preview.2 本身即各自最新预发布。**工具链**：SDK 11.0.100-rc.2.26504.105（11.0 channel 最新）、reportgenerator 5.5.11 / Stryker 5.0.0 均最新；GitHub Actions 七项经 releases API + commit SHA 双验均为最新（checkout v7.0.1 / setup-dotnet v6.0.0 / cache v6.1.0 / upload-artifact v7.0.2 / setup-python v7.0.0 / codeql-action v4.39.0 / gh-release v3.0.3），无需改动。**验证**：Release 全量构建 0 警告 0 错误；16 项目面板 **1745 用例 = 1668 通过 + 0 失败 + 77 跳过**（跳过为无 Docker/外部 broker 的结构性跳过）；两个 AOT 样本 `palorm-sample` / `aot-sample` win-x64 `PublishAot` 发布 0 错误且二进制实跑断言全过；smoke 分配预算 **6/6**。**偶发披露**：首轮面板出现两处时序/分配测量偶发（Serialization 分配断言 128B、SSE 心跳帧未达），其中分配断言在未升包的 HEAD 基线上同样复现，次轮全绿——判定为测量抖动非升级回归，未做代码改动。**Not-Tested**：PalORM 6.6.0 的 PG/MySQL 专属行为（COPY 批宽、版本化单语句批量更新）本机无 Testcontainers 未实跑，由 CI `dialect-probe` 覆盖；Actions 升位只能 CI 实跑验证。
- **全量核新与组件升位（2026-10-19，用户令「当前项目引用的所有库和包，组件全都升级到最新版」）**：四通道实测——`dotnet list package --outdated --include-prerelease`、NuGet flatcontainer 逐包索引（61 处中央钉扎全覆盖）、.NET 11 官方 release metadata、GitHub Actions releases API。**包线两升**：FsCheck 3.4.0 → **3.5.0**（属性测试，BSD-3-Clause）、Meziantou.Analyzer 3.0.307 → **3.0.312**（第三方分析器，MIT）；其余 59 处已在最新（含预发布口径）。**维持项及理由**：MediatR 12.5.0（最新 14.2.0，13.0.0 起 Lucky Penny 商业许可）与 Verify.TUnit 32.0.1（最新 33.3.4，SponsorCheck 商业门、包级禁令 ≥33）依 2026-09-25 开源许可裁决不升；`System.Runtime.CompilerServices.Unsafe` 6.1.2 不升 7.0.0-preview.2.22152.2（该预览线 2022 年后废弃）；`Microsoft.NETCore.Platforms` 8.0.0-preview.7 为停更前最后版本；CAP ×3 10.1.0-preview-308323494 与 BDN 0.16.0-preview.2 本身即各自最新预发布。**工具链不变**：SDK 11.0.100-rc.2.26504.105（11.0 channel 最新）与 reportgenerator 5.5.11 / Stryker 5.0.0 均为最新。**GitHub Actions 升位并重新钉 SHA**：checkout v4.4.0→**v7.0.1**、setup-dotnet v4.3.1→**v6.0.0**、cache v4.3.0→**v6.1.0**、upload-artifact v4.6.2→**v7.0.2**、setup-python v5.6.0→**v7.0.0**、codeql-action v3.39.0→**v4.39.0**、action-gh-release v2.6.2→**v3.0.3**；破坏性变更逐项比对：v5+ 系列统一要求 runner ≥ v2.327.1（GitHub 托管 runner 满足），setup-python 移除的 `pip-install`、checkout 对 fork PR 的 `pull_request_target`/`workflow_run` 限制均未在本仓使用，现有 `with:` 输入（fetch-depth / dotnet-version / path+key+restore-keys / python-version+cache / tag_name+files）全部保留。**验证**：Release 全量构建 0 警告 0 错误；16 项目面板 1724 用例 = 1648 通过 + 0 失败 + 76 跳过（跳过均为本机无 Docker 的结构性跳过）；config-policy / license-policy（判定集 162）/ encoding-gate / doc-consistency / count-audit（16 项·81 处）/ gate.cs（G23/G24）/ secret-scan / gate-audit（43 脚本·25 接线·0 UNVERIFIED/UNWIRED/REVIEW）/ quality-baseline（分析器 0 == 基线 0）全绿。**Not-Tested**：Actions 升位只能在 CI 实跑验证，本地仅完成 SHA 解析、版本注释与 config-policy 门禁校验。
- **同日二次核新复核（2026-10-17，用户令「更新所有库和包到最新版，并适配」；结论零可升项，零代码变更、零适配面）**：flatcontainer 索引全量复核 61 处中央钉扎（本轮 `dotnet list --outdated` 通道因本机代理对 registration 大页 100s 超时不可用，flatcontainer 单通道全覆盖、18 个失败项重试补查，与上轮双通道结论互证）+ SDK 官方 channel manifest + dotnet-tools.json 三面实测。**61 包全部在位**：PalORM ×5 6.5.0 / Ulid 1.4.2 / TUnit 1.75.0 / NativeCompressions 1.2.0 / Testcontainers ×4 4.16.0 / Npgsql 10.0.3 / MySqlConnector 2.6.2 / RabbitMQ.Client 7.2.2 / Confluent.Kafka 2.16.0 / Dapper 2.1.89 / Dapper.AOT 1.1.0 / MemoryPack 1.21.4 / ZLogger 2.5.10 / SqlClient 7.1.1 / SQLitePCLRaw ×2 3.0.5 / FsCheck 3.4.0 / Humanizer.Core 3.0.10 / 微软 11 线 rc.2 批 18 处 / System.* 传递钉扎（4.6.x 与 rc.2 各自最新）；CAP ×3 10.1.0-preview-308323494 与 BDN 0.16.0-preview.2 为各自最新预发布（稳定线 10.0.2 / 0.15.8 在其后，非升级方向）；Microsoft.NETCore.Platforms 8.0.0-preview.7.23375.6 仍为最新（8.0 后停更，勿再升）。**工具链两钉同验**：SDK 11.0 channel 最新 = 11.0.100-rc.2.26504.105（与 global.json 钉扎一致；GitHub `v11.0.0` tag 经查指向 release/11.0.1xx-preview2 分支 2026-03-03 提交，系分支版本 tag 非 GA 发布，GA 未发仍随 2026-11 窗口）；reportgenerator（dotnet-tools.json）5.5.11 = 最新。元包 5 处字面版本（Base/Extension 的 Ulid/DI/Logging/ZLogger）与中央钉扎逐字一致。**license 禁令两包维持**：MediatR 14.2.0（13.0.0 起 Lucky Penny 商业许可）与 Verify.TUnit 33.3.4（SponsorCheck 商业门，禁令 ≥33）为稳定版商业件，2026-09-25 裁决「所有引用的库必须是开源的许可，不使用商业许可」不因「更新到最新」的泛化指令而失效，继续维持 12.5.0 / 32.0.1（升位即 license-policy 门禁红）。GitHub Actions（checkout/setup-dotnet/cache/upload-artifact v4、codeql v3、action-gh-release v2）均在当前主版本线，由 dependabot github-actions ecosystem 自动跟踪，不属本轮包线范围。**勘误**：上条「核新补跑闭环」在位清单中 CAP 10.0.2 系预发布入线轮之前旧值，更正为 10.1.0-preview-308323494。
- **预发布入线轮（2026-10-17，用户裁决「库/包/组件升级可用预览版」，全面升级适配）**：① **CAP ×3 10.0.2 → 10.1.0-preview-308323494**（bench 对比车道专用消费；nuspec 许可 MIT 随 license-policy gate 复验通过；编译面零改动，对比车道数字需容器复测换锚，登记 Not-Tested）；② **BenchmarkDotNet 0.15.8 → 0.16.0-preview.2**（.NET 11 适配版）——适配实录：反射探针实证 `InProcessEmitToolchain.Instance` 已更名 `Default` 字段（同语义），Program.cs 8 处替换；**默认 Switcher 入口恢复**（0.15.8 时代 net11-rc moniker 不被 DotNetSdkValidator 识别致入口崩溃，`--list` 实测 exit 0 全量列出），Program.cs 头注与 testing.md §3.2/§4.1 两处记载同步勘正；smoke 分配预算 6/6 在 0.16 引擎下复测通过；③ 传递钉扎补齐 **Microsoft.Data.Sqlite.Core** 与 **Microsoft.Extensions.DependencyModel** 随 11 线 rc.2（核新实证二者未声明时解析分别停在 rc.1/10.0.10，与「微软钉扎全量随 rc.2」意图矛盾）。**license 禁令不受预发布政策影响继续维持**：MediatR 14.2.0（13+ Lucky Penny 商业许可）与 Verify.TUnit 33.3.4（SponsorCheck 商业门）为稳定版商业件，非预发布范畴。BDN 0.16 带入两个 licenseUrl 形态传递依赖（deniszykov.WebSocketListener / System.Security.Cryptography.ProtectedData，均经在线取证为 MIT），按 license-policy gap 台账机制登记观察态（2026-12-31 到期，附取证证据）。验证：Release 全量构建 0W0E + 16 项目面板 0 失败（1556 用例 = 1480 过 + 76 跳）+ format-verify 绿。
- **SDK 钉扎扶正 11.0.100-rc.1 → **11.0.100-rc.2.26504.105**（2026-10-17，工具链组件升位）**：包线 10-11 已随 rc.2（18 处中央钉扎），本机 `rollForward=latestMajor` 实际解析即 rc.2（近两日 EPSL/性能轮均在 rc.2 SDK 上构建测试），global.json 钉扎与事实扶正对齐；CI `setup-dotnet` 按本文件安装同版本（此前 CI 恒装 rc.1 SDK + rc.2 包的错配组合消除）。同步：docs/testing.md §2.2 示例、docs/migration/net11-ga-upgrade-plan.md 清单表（rc.1 列 → 当前 rc 版本列）；GA SDK 升位仍随 2026-11 GA 轮。验证：rc.2 精确钉扎下 Release 全量构建 0W0E + 16 项目面板 0 失败（1556 用例 = 1480 过 + 76 跳）+ smoke 分配预算 6/6。
- **核新受阻披露（2026-10-17）**：本轮 NuGet 在线核新（`dotnet list --outdated` 双通道）因**全网中断未能执行**（nuget.org 与 github.com 直连及本机代理 127.0.0.1:50001 均不可达，curl 000 复测两次）。离线替代核查：本地 NuGet 缓存逐包比对 props 钉扎，除已知 BDN 0.16.0-preview.2（仅预发布不升）外无任何缓存新版；包线结论沿用 2026-10-15 在线核新（全部最新，MediatR 14.2.0/Verify.TUnit 33.3.3 依禁令维持）。**网络恢复后需补跑在线核新**（含 dependabot 分支同步状态）。
- **核新补跑闭环（2026-10-17 当日，网络恢复后）**：双通道 `--outdated`（含 `--include-transitive`）+ flatcontainer 索引双源实测，**零可升项**——MediatR 14.2.0（13+ Lucky Penny 商业许可）与 Verify.TUnit 33.3.4（禁令 ≥33，较两日前 33.3.3 小版本递补）依裁决维持；CAP 10.1.0-preview / BDN 0.16.0-preview.2 仅预发布不升；其余全部在位（PalORM 6.5.0 / Ulid 1.4.2 / TUnit 1.75.0 / NativeCompressions 1.2.0 / Testcontainers ×4 4.16.0 / CAP 10.1.0-preview-308323494 / Npgsql 10.0.3 / MySqlConnector 2.6.2 / RabbitMQ.Client 7.2.2 / Confluent.Kafka 2.16.0 / Dapper 2.1.89 / Dapper.AOT 1.1.0 / MemoryPack 1.21.4 / ZLogger 2.5.10 / SqlClient 7.1.1 / SQLitePCLRaw 3.0.5）。传递依赖浮动物（Argon/DiffEngine/EmptyFiles/FSharp.Core/ApplicationInsights/DiaSymReader 等 Verify/BDN/FsCheck 链内部件）按仓规不钉扎，归顶层包所有。SDK 线：11.0 最新仍为 rc.2.26504.105（GA 未发，2026-11 窗口），10-17 的 SDK 扶正即最新态。
- **NativeCompressions 1.1.0 → 1.2.0（2026-10-15 核新，性能实施轮 R1~R10 后窗口，`dotnet list --outdated` 双通道实测）**：1.x 线内次版本，本轮唯一可升项；包内容 diff 逐项一致（8 文件同布局，仅 nupkg 自引用不同，LZ4/ZStd 消费面零变化）；破坏面按 PD9 由编译 + 往返测试实证（`NativeCompressionTests`）。同轮核新维持：MediatR 14.2.0（license 禁商业）、Verify.TUnit 33.3.3（禁令 ≥33）、CAP 10.1.0-preview / BDN 0.16.0-preview.2（仅预发布）不升。
- **全量升级（2026-10-11 三升，v4.0.0 发布后核新，`dotnet list --outdated` 双通道实测）**：PalORM 五包 6.4.1 → **6.5.0**（次版本线；XML diff 1090 行实证变更面为上游 internal 基础设施族——新增 `DataSessionPool<T>` 会话池化、`EntityInfoCache` 实体元数据缓存、`QueryBuilderShared`/`PooledCts` 等，三契约文本逐字未动复核（PreWarmAsync 灌暖/会话级熔断作用域/readFromReplica 回落主库与 READ-002 教义），适配层对全部变更类型零消费；PD9 纪律：Release 0W0E + 16 项目面板 0 失败 + 6 处适配层注释与 deployment-performance.md 契约引用同提交升号）；ByteAether.Ulid 1.4.1 → **1.4.2**（rc 转稳定，解除「仅预发布不升」钉扎；元包 PalDDD.Base/Extension 字面版本同步——元包走 `ManagePackageVersionsCentrally=false` 需双改）；TUnit / TUnit.FsCheck 1.73.19 → **1.75.0**（MTP 传递维持 2.5.1，conventions §10.6 与 testing.md 版本行同提交更新）；微软 11 线 rc.1 → **11.0.0-rc.2.26504.105**（rc.2 已发，18 处中央钉扎全量 + Extension 元包两处字面版本；flatcontainer 逐包验证九包同批在案；SDK 维持 rc.1——API go-live 冻结故 rc.2 包对 rc.1 运行时逐位兼容由面板实证，SDK/GA 统一升位随 2026-11 GA 轮评估；global.json 头注同步）。
- **钉扎维持（核新后复核）**：MediatR 维持 **12.5.0**（现最新 14.2.0，13.0.0 起 Lucky Penny 商业许可，license-policy 禁商业裁决；bench 对比锚）；Verify.TUnit 维持 **32.0.1**（现最新 33.3.3，license-policy 包级禁令 ≥33）；CAP 维持 **10.0.2**（10.1.0-preview 仅预发布不升）；BenchmarkDotNet 0.15.8（0.16.0-preview.2 仅预发布不升）。

### Fixed

- **DapperEventLog 真流式读取在 PG 上的列序缺陷（2026-10-19）**：M4 的 `event_id_bin` 由 `ALTER TABLE` 追加在物理列末，与逻辑列序不同；`CommandBehavior.SequentialAccess` 要求按物理列序单调读取，按列名取 ordinal 后跨列回读会抛 `Invalid attempt to read from column ordinal`。改为 `CommandBehavior.SingleResult`，仍逐行流式（不物化全部行），允许行内随机列访问。M4 真库探针在 PG/MySQL 均通过。

### Tests 测试

- **2026-10-22 R10 审计 D1：批量 Inbox pending span 回归锁**：`InboxProcessor_BatchMarkFailed_...` 与 `...PartiallyMissed...` 增加 `RecordingActivityListener` 断言（`pal.inbox.result = processed-pending-confirmation`、`pal.inbox.consumer`）；隔离变异把该值固定为 `processed` 时两条测试转红（修复前 259 条 Transactions 测试仍全绿）。同步 `docs/usage.md` 明确 1004 的级别口径：部分未命中 Warning（无异常可携带）、整批异常 Error，告警按 EventId 过滤。

## [4.0.0] — 2026-10-11

> **范围**：`v3.3.0`（2026-10-08，`c1c4886`）→ `v4.0.0`（本提交），39 个提交（`git log v3.3.0..HEAD`——v3.3.0 tag 打在 main 合并提交上，dev 侧等价窗口即此）。
> **兼容性**：SemVer Major。破坏面三处：① `IPalOutboxStore` 四个同步方法（`AddMessage`/`MarkProcessed`/`MarkDead`/`ReleaseForRetry`）删除、`AddMessagesAsync` 补 `ct` 尾参（ADR-026 ③④，自定义实现方须迁移，映射见 Changed [Breaking] 条）；② `Dispatcher.Register(Type,Type,Type,RequestExecutor)` 加 `needsScope` 尾参（直调该重载须补参，`AddPalCqrs` 等 DI 扩展路径不受影响）；③ Outbox 终态写 fencing 契约跨栈统一（Dapper 半回写→全套、EF 零回写→Detached 快照全套，ADR-026 ②）。公共 API 快照 +48/−27（排除程序集版本引用行，1613 → 1634 行；含 CI 首轮 format-verify 修复的 `VersionProbeRow` 属性 v/v2 → V/V2 命名合规改名——类型为本版新增未发布，零消费影响）。新增面全部向后兼容（实例注册重载 / `IImmediateWriteOutboxStore` / `ISagaLeaseBatchStore<TState>` / `MySqlLeasePolicy` / `ProcessBatchWithBacklogAsync` / `MaxInFlightPublishes`）。
> **组织方式**：按 Keep a Changelog 分类给出**消费者可见变更**（本版无独立工程过程附录——过程叙事已内联于各条目）；规范见 [`docs/release.md`](docs/release.md) §十一。

### Added
- **`Dispatcher` 实例注册路径（ADR-026 ①，公共 API 新增，向后兼容）**：新增 `Dispatcher(IServiceProvider rootProvider)` 构造重载与 `Register<TRequest, TResponse, THandler>(THandler handlerInstance)` 实例注册重载——实例路径**不经 per-request scope**（handler 即注册实例、行为从根容器解析），实测零行为分发 167.8ns/160B → **108.1ns/32B**（分配 -80%、计时 -36%）。契约约束（XML doc 声明）：实例注册隐含「handler 无 scoped 依赖、行为以单例注册于根容器」，scoped 依赖在该路径不可见；需要 scoped 依赖请用 DI 注册形态（原路径不变）。公共 API 快照随此再生（+3 行：构造重载 + Register 重载）。⚠️ 连带签名变更：既有 `Register(Type, Type, Type, RequestExecutor)` 重载加 `needsScope` 尾参（实例/DI 两类分派路由所需）——直调该重载的消费方须补参（`AddPalCqrs` 等 DI 扩展路径自动传递，不受影响）。
- **`PalDDD.Transactions.IImmediateWriteOutboxStore`（公共接口新增，向后兼容）**：立即写 Outbox store 的强化契约——声明 `AddMessagesAsync` 的全部写入在调用内立即完成（对 store 自身连接/事务），不依赖也不触发调用方的 SaveChanges。`DapperOutboxStore` / `InMemoryOutboxStore` / `PalOrmOutboxStore<TProvider>` 已实现（既有公共方法同时满足两接口，零新增成员）；EF 栈 `OutboxDbContext` 刻意不实现（其写入延迟到上下文 SaveChanges，且 `AddMessagesAsync` 内嵌自持 SaveChangesAsync）。公共 API 快照 +2 行。
- **`PalDDD.Transactions.ISagaLeaseBatchStore<TState>`（公共能力接口新增，向后兼容，EPSL 修复轮 B-1）**：Saga 租约批量释放能力——`ReleaseLeasesAsync(owner, sagas, ct)` 单语句批量清租约列并换代（version+1，`leased_by = owner` 防抢占守卫），返回实际释放行数。四栈实现：`InMemorySagaStateStore` / `DapperSagaStateStore` / `PalOrmSagaStateStore` / `SagaStateDbContext`（EF 栈 ExecuteUpdateAsync 单语句 + 全批 Detach 防跟踪器幽灵写）。`SagaTimeoutProcessor` 超时扫描的非超时分支（常态路径）据此从逐 saga `SaveChangesAsync`（每 saga 一次 DB 往返 + 全量快照序列化，Dapper 栈叠加存在性全行预查，EF 栈叠加全跟踪实体 DetectChanges 扫描）改为环后单语句批量释放；自定义存储未实现本接口时逐条路径逐字保持。⚠️ 已裁决取舍：冲突信号粒度由逐 saga Warning 收粗为批次计数 Warning（expected-affected 差值，无法定位具体 saga）。公共 API 快照 +8 行（接口 + 四栈方法 + 两 SQL 模板）。

### Changed
- **EPSL 修复轮（2026-10-11，行为等价的性能变更族，报告 `docs/review/epsl-carpet-2026-10-11.md`）**：
  - **Brotli 解压增长缓冲池化**：接入 `DecompressBufferPool` 线程槽（上移至 PalDDD.Compression 为两程序集单一事实源，Native 经 InternalsVisibleTo 消费）——16KB 负载解压分配 **45,185 → 16,440 B（-64%）**，与 LZ4/ZStd 池化形态（16,488 B）对齐，计时同带。
  - **EventLog k>1 批量追加在 PG 上改多行 VALUES+RETURNING 单往返（仅 PostgreSQL）**：`DapperEventLog.AppendAsync` 对 PG + k>1 改单语句多行插入（RETURNING 全部位置，first/last 取 Min/Max 序无关），k 次 INSERT..RETURNING 往返收为单往返——真库配对 A/B（postgres:18.6 同容器同日）：k=5 从 **3,726 → 1,345 μs（快 2.77×）**、分配 48.2 → 42.2 KB；k=1 与 MySQL/SQLite 维持原路径（对照项同带）。冲突分类等价（单语句原子 + 重查 MaxVersion 分类，批内 EventId 重复原样上抛、外部并发转 `EventStreamConcurrencyException`）。
  - **Saga Save 的存在性预查去全量反序列化（Dapper + PalORM）**：`SaveChangesAsync` 改 UPDATE 先行（乐观锁谓词），0 行时轻量 COUNT 探测区分「版本冲突」与「新行」——原「全行 SELECT + 全量 JSON 反序列化」把 O(快照大小) 成本花在只做 null 判断的结果上。常见路径 2 往返收 1；新行路径多一次探测往返（Saga 创建为冷路径）。返回值三态（0/1/并发插入异常）不变。
  - **Guid 强类型 ID 的 JSON 读路径三腿零分配（源生成器模板）**：镜像 Ulid 腿形态——非转义单段字符串走 `Utf8Parser` 'D' 格式直解（本仓写侧 `WriteStringValue(Guid)` 恒产 D 格式，框架往返全量覆盖快路径）；'D' 失配回退字符串路径**保持 N/B/P 格式接受面不变**（Utf8Parser 仅支持 'D'，直接替换会静默收窄）；转义/多段 sequence 同 Ulid 腿回退。旧形态每读 ~90B 字符串分配。
  - **消费路径头还原的分配清零（Messaging）**：`MessageConsumeContext.FromHeaders` 空入参快路径（无追踪消息为默认消费形态，原字典构造先于空判短路）；KafkaBroker 消费侧单趟直产已解码字典（免中间字典）；Kafka/RabbitMQ 发布侧空追踪上下文不再分配空 Headers/Dictionary 对象（客户端 null 语义等价，wire 不变）。
  - **领域事件派发器容器分配移除**：单事件派发（最常见形态）免 `Queue` 拷贝与去重 `HashSet`（Debug 测试口径 851 → 579 B/次）；多事件路径去重行为不变。
  - **InboxProcessor 消费热路径日志守卫**：成功/跳过两条路径的 `Information` 插值加 `IsEnabled` 守卫（日志关闭时免每消息 ~150-250B 字符串分配）；启用态日志行为不变。
  - **PalORM×MySQL Inbox 新消息路径单往返**：INSERT 尾随 `SELECT LAST_INSERT_ID()` 单命令（对齐 Dapper 姊妹 `SqlTemplates.InboxInsertMySql` 形态）——原 INSERT 后按 (consumer_name, message_id) 回查 id 多付一次往返；唯一冲突分类不变。
  - 其余内部变更：`DapperBulkCopy` MySQL 大批量路径的 `AllowLoadLocalInfile` 判定按连接实例缓存（ConditionalWeakTable，连接串不以字符串驻留静态表）；`StrategicDddAnalyzer` 消息契约规则的 `IsDomainEventType` 单轮单次求值（原双倍基类链 + AllInterfaces 全名比对，IDE 增量分析）。
- **[Breaking] IPalOutboxStore 接口异步化 + CancellationToken 传播（ADR-026 ③④，签名破坏性变更）**：四个同步方法删除，签名变更映射——`AddMessage` → `await AddMessageAsync(message, ct)`、`MarkProcessed` → `await MarkProcessedAsync(message, processedAt, ct)`、`MarkDead` → `await MarkDeadAsync(message, failureReason, deadAt, ct)`、`ReleaseForRetry` → `await ReleaseForRetryAsync(message, failureReason, nextAttemptAt, ct)`；`AddMessagesAsync` 补 `CancellationToken ct = default`（v8 声明的契约缺口收口）。实现方迁移：机械重命名 + await 化 + 真 I/O 路径改异步原语（Dapper `ExecuteAsync`/原生 ADO `ExecuteNonQueryAsync`、EF `ExecuteUpdateAsync`、PalORM 直连 session 异步并**删除全部 7 处同步阻塞桥**、InMemory 同步完成 ValueTask）。**EF 拦截器同步 `SaveChanges()` 钩子保留支持**：经单点文档化阻塞桥调用异步接口（无同步上下文宿主安全；classic sync-context 宿主改用 `SaveChangesAsync`）。消费方若自定义 `IPalOutboxStore` 实现或直调这四个方法，须同轮迁移。Dapper 内部 ct 口径不变（M1-3 勘正：直接重载，ct 仅收缩连接打开阶段）。
- **[Breaking] Outbox 终态写跨栈 fencing 契约统一（ADR-026 ②，消费者可见的行为变更）**：`MarkProcessed`/`MarkDead`/`ReleaseForRetry` 统一为「门控拒绝（租约 token 失配）→ 零 DB 写 + 零内存变异；成功 → DB 全套更新 + 入参对象全套六字段回写」。历史分叉同轮收口：Dapper 成功分支从仅清租约两字段升为全套回写、**`MarkDead` 补 affected 消费**（原 token 拒绝后仍无条件清入参租约，守卫缺口级分叉）；EF 栈主路径从零入参回写改为**仅 Detached 快照全套回写**（跟踪实体自动跳过——回写会置 Modified 态并在后续 SaveChangesAsync 撞 RetryCount 并发令牌假冲突，既有测试实证；主消费者 OutboxProcessor 传 AsNoTracking 快照，统一契约在主路径成立）。**迁移注意**：读终态写后入参对象 `Status`/`ProcessedAt`/`Error`/`NextAttemptAt` 字段的自定义处理器代码行为变化（此前 Dapper 半态/EF 零态，现全套终态）；PalORM/InMemory 栈行为不变（本即目标形态）。
- **Outbox 拦截器异步路径对立即写 store 批量化（消费者可见的执行形态变化）**：`OutboxDomainEventInterceptor.SavingChangesAsync` 对 `IImmediateWriteOutboxStore` 分派单次 `AddMessagesAsync` 批量写入——多事件保存从 k 次单条合并为 1 次批量（PG 单 COPY 往返，收益量级见 `docs/performance.md` AddBatch5 基准 3.5×/3.8×），并消除原逐条同步写在远程库上对异步调用线程的往返级阻塞（PG ~609μs/条/线程）。EF store 组合与同步 `SaveChanges()` 钩子维持逐条路径原语义；写入行内容与事务边界不变；失败清理镜像既有 v34 P2 自清理域。
- **EventLog 单事件 Any 版本追加在 PG 上改单语句原子形态（行为等价的性能变更，仅 PostgreSQL）**：`DapperEventLog.AppendAsync` 在单事件 + `ExpectedStreamVersion.Any` + PostgreSQL 时改走 `INSERT..SELECT` 单语句——服务端原子计算 `MAX(stream_version)+1` 并 `RETURNING` 取回位置与版本（原路径 MAX 预查 + INSERT 两次往返），同步消除该子路径的检查-写入竞态窗口。实测 PG 写路径 -15%（968→820μs）；MySQL/SQLite 维持原路径（实测无收益/反向劣化，方言门控依据见 `DapperEventLog` 快路径注释）。并发冲突的异常语义不变（版本位竞争转 `EventStreamConcurrencyException`、EventId 重复原样上抛）。
- **Outbox 单条插入执行层去 Dapper 化（内部变更，无公共 API 变化）**：`DapperOutboxStore.AddMessage` 改原生 ADO 命令直发（参数值与方言转换与原 DynamicParameters 形态一致，SQL 不变）——单条插入分配 -19.7%（10.53→8.46KB/op）、SQLite 计时 -12~17%。
- **MySQL 小批量批量写入改参数化多行 VALUES（部署要求放宽，消费者可见）**：`DapperOutboxStore.AddMessagesAsync`（经 `DapperBulkCopy`）在 ≤16 行时改走单语句参数化多行 VALUES 插入——此前小批量与大批量统一走 MySqlBulkCopy（LOAD DATA LOCAL INFILE），被迫要求连接串 `AllowLoadLocalInfile=True` + 服务端 `local_infile=1`；该要求现仅约束 >16 行的大批量路径。真库 MySQL 8.4.11 实测 k=5 批量写入从 5×单条耗时降为 3.8×（2,072μs vs 7,969μs）；行数守卫与既有 LOAD DATA 路径对齐（非全量写入显式抛错）。
- **三项热路径分配优化（无公共 API 变化，2026-10-09 优化轮）**：① CQRS 零行为分发快路径——空行为管道直呼 handler，`SendAsync` 零行为 216→160 B/op（-26%），分配已低于 MediatR 12.5.0 同口径的 224 B（单行为对照组逐字节不变）；② LZ4/ZStandard 压缩 scratch 改 `ArrayPool` 租借、解压增长缓冲改 ThreadStatic 复用——16KB 负载压缩 16,816/16,832→376/304 B/op（-98%）、解压 45,232→16,488 B/op（-64%，计时同步 -34%），Brotli 对照组逐字节不变；③ 上两项经 S3 反向验证（移除因子同晚复跑）各列逐字节退回基线，归因闭合。基准数字与验证记录见 [docs/performance.md](docs/performance.md)「优化轮」段。
- **MySQL 租约双模板——版本探测启用 SKIP LOCKED（ADR-025，消费者可见的行为变更，仅 MySQL ≥8.0.18 非 MariaDB）**：Dapper/PalORM 栈的 MySQL Outbox 租约在探测到 MySQL ≥8.0.18（与 EF 栈声明对齐）时改用 JOIN 派生表 `FOR UPDATE SKIP LOCKED` 模板——多实例并发租约从「InnoDB 死锁异常 + last-writer-wins 双租窗口」（ADR-024 接受的现状，2026-10-08 基准车道 8 处死锁实录）变为「零死锁 + 互斥」（`MySqlSkipLockedShapeBenchmarks` 同库实测：互斥不变量全波通过、串行→8 worker 加速 1.75×）。MySQL <8.0.18 / MariaDB / 探测失败回退原无锁模板，行为与既往逐字一致（fail-safe 判定见 `PalDDD.Transactions.MySqlLeasePolicy`，新增公共 API：该策略类 + `SqlTemplates.OutboxLeaseUpdateMySqlSkipLocked` 常量）。经 `OutboxProcessor`/`SagaProcessor` 消费的用户注意：处理器模板自带 tick 异常隔离，旧形态下死锁本就 tick 级自愈；新形态消除的是死锁与双租窗口本身。
- **Outbox 发布确认流水线化（T3.1 优化实施轮，消费者可见的吞吐行为变更，向后兼容，默认关闭）**：新增 `OutboxOptions.MaxInFlightPublishes`（默认 1 = 既有串行逐条确认行为逐字不变；>1 启用 FIFO 流水线）——分区内发布任务按启动序预发入窗（窗口满收队头），broker 确认往返不再逐条串行相乘，**标记顺序仍保启动序**（RabbitMQ 单 channel 帧序天然保序；Kafka producer 队列保序但重试乱序须 `enable.idempotence`），失败路径（退避重试/死信/标记错误挂 Data）与串行形态逐项一致。投递吞吐量具实测（窗口 16，SQLite 真库标记 + 假 broker RTT）：RTT 1ms 档 10.05s→776ms（**12.9×**）、RTT 20ms 档 25.65s→1.97s（**13.0×**）排空 1000 条；RTT 0 档无差异（分配 +2.6%）。推荐形态：远程 broker 部署开窗口 16~64（消费方具备 Inbox 幂等前提）；启动期 `ValidateOnStart` 校验 ≥1。锁定测试三件（满批保序 / in-flight 峰值有界且 >1 / 单条失败重试其余照常）三连绿。
- **Outbox 自适应排空节奏 + PollInterval 热读（T1.2 优化实施轮，消费者可见的吞吐行为变更，向后兼容）**：`OutboxProcessor` 后台循环从「固定间隔轮询」改为自适应——上一轮租到满批（`messages.Count ≥ BatchSize`）时下一轮仅让步 1ms 立即续批，未满批才按 `PollInterval` 轮询；积压排空吞吐上限从 `batchSize/(tick+interval)` 提升到 `batchSize/tick`（默认 500ms 间隔下满批场景约 4~6×，量具实测见 performance.md 优化实施轮段）。`PollInterval` 同时改为**每轮热读取**（经 `IOptionsMonitor`），原「构造期冷快照、热更新需重启」声明废止，运行时调整即时生效；其语义收窄为「空/浅队列检测节奏」——调大它不再压低积压排空速度。新增公共 API `OutboxBatchProcessor.ProcessBatchWithBacklogAsync(ct)`（满批信号，快照 +1 行）；既有 `ProcessBatchAsync` 与 `PostgreSqlOutboxNotifier` 路径不变。行为边界：多实例惊群不受影响（续批仍走 SKIP LOCKED 租约互斥）；`PeriodicBackgroundProcessor` 其他子类（SagaProcessor）默认行为逐位不变（信号默认 false）。锁定测试三件（满批 1ms 级续批 / 未满批守整间隔 / 热读生效）经改前红证明（旧循环下两件结构性红、不变量件绿）与三连绿零抖动验证。

### Deprecated 废弃

- **三处既有 `[Obsolete]` 移除预告改期 v5.0（v4.0 窗口未执行移除，零框架消费状态不变）**：`DomainCapabilityAttribute` / `AggregateNameAttribute`（精炼裁决 2026-08-26）与 `SqlServerOutboxDbContext`（零测试覆盖方言基类）的 Obsolete 文案由「v4.0 移除」改指「v5.0 移除」；另有 5 处内部注释级契约承诺（`AddPalFullStack` 更名、PalOrmEventLog 分类对齐、Saga 深隔离/双键轨迹/manager 租约 fencing）同轮改指 v5.0 窗口——循 2026-09-22「v3.0→4.0 改指」先例，`TechDebtGuardTests.VersionPromises_AreNotExpired` 守卫红绿联动（活账本保持零条目）。已兑现侧：`IPalOutboxStore` 接口 remarks 的 v4.0 预告块同轮转正为「已执行」表述（①② 均落地于本版）。

### Removed 移除

- **`IPalOutboxStore` 四个同步方法（ADR-026 ③，v4.0 major 窗口一次清偿、无 Obsolete 过渡）**：`AddMessage` / `MarkProcessed` / `MarkDead` / `ReleaseForRetry` 自公共接口与全部内置实现（Dapper / EF Core / PalORM / InMemory）删除——迁移映射（`AddMessage` → `await AddMessageAsync(message, ct)` 等）与各栈异步原语指引见上方 Changed [Breaking] 条目。
- **`Dispatcher.Register(Type, Type, Type, RequestExecutor)` 旧 4 参形态**：由加 `needsScope` 尾参的 5 参重载取代（ADR-026 ① 连带变更）——直调方补 `needsScope`（DI 注册扩展传 `true`，实例注册路径传 `false`）。

### Dependencies 依赖

- **全量升级（2026-10-11，`dotnet list --outdated` 实测盘点）**：PalORM 五包 6.3.1 → **6.4.0**（6.x 线内，PD9 纪律：Release 全量构建 0 警告 0 错误 + 16 项目测试面板 0 失败 68 环境跳过与 3.3.0 基线逐项一致 + 三契约 6.4.0 XML 复核——PreWarmAsync 逐条灌暖语义、会话级熔断作用域、readFromReplica 未配置回落主库（READ-002 opt-in 教义 6.4.0 XML 新增「不回归」明示），6 处适配层注释同提交升号）；NativeCompressions 1.0.1 → **1.1.0**（1.0 线内补丁位，编译+往返测试实证）；TUnit / TUnit.FsCheck 1.73.5 → **1.73.19**（测试期，MTP 传递解析 2.5.0 → 2.5.1，conventions §10.6 与 testing.md 版本行同提交更新）；Testcontainers 四包 4.15.0 → **4.16.0**（测试期，方言/消息面本机跳过由 CI 执行口径不变）；CAP 三包 8.4.1 → **10.0.2**（bench 对比车道专用——MIT nuspec 实证、net10 组依赖 M.E.* 10.0.0 低于本仓 rc.1 钉扎无冲突、编译零改动；**换锚复测已完成（2026-10-11）**：CAP 4.734ms vs PalDDD 1.854ms = 快 2.55×/分配少 60%，与 8.4.1 锚同带——结论跨主版本稳定，引用基准换为 10.0.2）。
- **补丁跟进（2026-10-11 同日二升）**：PalORM 五包 6.4.0 → **6.4.1**（上午轮 f57076b 后当日新发补丁位；XML diff 实证变更面全部在上游 internal 优化——新增 `CapabilityProbeCache<T>` 连接串键控能力探测缓存、`ConvertDeleteKey` 键转换委托替代 GetByKey 探针命令，公共 API 零增删改；三契约 6.4.1 XML 逐字未动复核在案——PreWarmAsync 逐条灌暖/会话级熔断作用域/readFromReplica 回落主库与 READ-002 不回归教义；PD9 纪律：Release 全量构建 0W0E + 16 项目面板 0 失败（1548 用例 = 1477 通过 + 71 环境跳过，跳过构成 = 方言 Testcontainers 63 + broker 容器 8，与 4e1af12 本机基线 68+3 一致）+ nuspec 许可 AGPL-3.0-only 与 6.4.0 同文；6 处适配层注释同提交升号，deployment-performance.md MinPoolSize 契约引用同步）。
- **钉扎维持（裁决不变）**：MediatR 维持 **12.5.0**（13.0.0 起 Lucky Penny 商业许可，license-policy 禁商业裁决；bench 对比锚）；Verify.TUnit 维持 **32.0.1**（license-policy 包级禁令 ≥33，SponsorCheck 商业门）；微软官方 11 线维持 **11.0.0-rc.1.26425.128**（2026-10-11 `--outdated` 实测 rc.2/GA 仍未发，EF Core ×4 + Extensions + System 钉扎线）；BenchmarkDotNet 0.15.8（0.16.0-preview.2 仅预发布不升）。

### Documentation 文档

- **v4.0 性能对比总表（2026-10-10 全量真跑实测，单表融合）**：环境与口径 = Win10 x64 / .NET 11.0.0-rc.1 / BenchmarkDotNet 0.15.8 InProcess / strict 严格档（40 迭代 × 3 launch；coverage 维度为 medium 档——该车道与 strict 存在 InProcess 单基准时长上限冲突）/ wslc 一次性真库容器 postgres:18.6、mysql:8.4.11、rabbitmq:4.3.6；12 条车道全部 exit 0。本表数字均为同日同机同 harness 读数（日内比值可直接引用）；分车道完整表、Error 区间与方法学注见 [docs/performance.md](docs/performance.md)「全量复跑轮」段。

  | 维度 | 场景（口径） | 本仓读数 | 对照 | 实测结论 |
  |---|---|---|---|---|
  | 分发层 | 零行为分发（DI scope 路径） | 173.1 ns / 160 B | MediatR 12.5.0：134.5 ns / 224 B | MediatR 计时快 22%；本仓分配少 29% |
  | 分发层 | 单直通行为 | 268.9 ns / 432 B | MediatR：224.3 ns / 528 B | MediatR 快 17%；本仓分配少 18% |
  | 分发层 | 实例注册路径（v4.0 新增，免 scope） | 106.3 ns / 32 B | 本仓 DI 路径 154.6 ns / 160 B | 计时 -31%、分配 -80% |
  | 发布路径 | 同语义「序列化 + 本地消息表写入 + 后台送 broker 入队」（MySQL 真库 + RabbitMQ） | 2.035 ms / 10.38 KB | CAP 8.4.1：4.788 ms / 25.02 KB | **快 2.35×、分配少 58%** |
  | Outbox 读 | GetPending 100 条（Dapper / PalORM） | PG：481.4 μs / 493.1 μs | MySQL：658.3 / 797.9 μs；SQLite 三栈（含 EF）：437.6 / 476.8 / 950.1 μs | 网络库约为 SQLite 十倍量级（往返主导） |
  | Outbox 租约 | Lease 100 条（Dapper / PalORM） | PG：63.2 ms / 174.7 ms | MySQL：172.0 ms / 534.0 ms | PG 真并行形态占优（SKIP LOCKED 同款） |
  | Outbox 写 | 单条插入（Dapper） | PG：630.4 μs | MySQL：1,730.6 μs；SQLite：41.6 μs | 同上量级差 |
  | Outbox 写 | 5 条批量一往返（Dapper） | PG：846.4 μs | 5× 单条 3,152 μs | **批量化 3.7×**（MySQL 侧 4.2×） |
  | EventLog | 单事件追加（Dapper / PalORM） | PG：1.05 ms / 2.19 ms | MySQL：2.55 ms / 5.13 ms | PG 约为 MySQL 一半 |
  | 幂等 | TryStart→MarkCompleted 全周期（Dapper / PalORM） | PG：1.52 ms / 3.21 ms | MySQL：3.87 ms / 8.96 ms | PG 约为 MySQL 一半 |
  | 并发租约 | 800 条波 8 worker（Dapper，每波零重复断言） | PG：串行 23.2 ms → 8W 20.7 ms | MySQL（SKIP LOCKED）：207.6 → 174.1 ms；SQLite：5.6 → 160.0 ms | PG/MySQL **零死锁真并行**；SQLite busy 平台期 28×（多实例部署不可用） |
  | 并发租约 | InMemory 簿记层 | 391.5 μs → 602.0 μs（8W） | — | 锁竞争有界退化 1.5× |
  | 绝对 QPS | dispatch 纯分发（1/4/8 线程，3s×3 轮中位） | 5.4M / 20.3M / 34.8M ops/s | chain 场景：1.0M / 1.6M / 1.8M ops/s | dispatch 近线性至 4 线程；chain 4 线程平台期（Gen0 饱和） |
  | 压缩 | 16KB LZ4（池化输出缓冲） | 5.78 μs / 376 B | GZip 15.07 μs / 1,624 B | 快 2.6×；较池化前 16.8KB 分配 **-98%** |
  | PG 批处理 | Pipeline 100 条单往返批插 | 6.18 ms | 同 100 条逐条往返 139.7 ms | **22.7×** |

### Tests 测试

- **16 项目面板 0 失败（2026-10-11，v4.0.0 版本位 + 快照再生后 §4.1 实测）**：1550 用例 = 1478 通过 + 72 环境跳过（构成 = 方言 Testcontainers 64 + broker 容器 8；容器面由 CI dialect-probe 权威判定）；Release 全量构建 0 警告 0 错误（`--no-incremental -warnaserror`）；本地 pack 35 包 + §4.2 nuspec 元数据检查通过。版本窗口内新增锁定测试：OutboxPublishPipeliningTests（流水线三件）/ SagaLeaseBatchReleaseTests（批量释放）/ MySqlLeasePolicyTests（版本探测）/ GeneratedIdentityTests（Guid 三腿）/ OutboxInterceptorMidwayFailureProbeTests 扩展（批量化中途失败）/ EventLog 重复 EventId 分类测试（三方言）。
- **CI 首轮（run 38148094954，v4.0 窗口 42 提交首次 CI 化）暴露两处测试基建缺口并同轮修复**：① format-verify 5 处存量违规——`VersionProbeRow` 属性 v/v2 命名改 V/V2（IDE1006，[Column] 映射保持 SQL 别名小写）+ 三处多余 using（ComparisonBenchmarks 别名 / MySqlLeasePolicyTests / OutboxPublishPipeliningTests），本地 `dotnet format style --verify-no-changes` 复核绿；② PalORM 测试栈 MultiDialectSchema 缺 `event_id` 唯一索引（EventId 重复分类契约的物理前提，文件头注「对齐需双改」的漏改半边，Dapper 侧 DapperStoreTests 既有）——补齐三方言索引 + 补 SQLite/MySQL 重复测试变体，SQLite 变体本地红（无索引断言红）→绿（补索引后过）证明在案，PG 变体即 CI 首轮失败项由复跑验证。

---

## [3.3.0] — 2026-10-08

> **范围**：`v3.2.0`（2026-09-25，`c37376b`）→ `v3.3.0`（本提交），12 个提交。
> **兼容性**：SemVer Minor——零公共 API 变更（快照 1613 行唯一差异为程序集版本引用行 `Version=3.2.0.0` → `Version=3.3.0.0`，30 个程序集零类型/成员/签名增删改，无破坏性行为变更）；变更面为消费者可见依赖上移（PalORM 五包跨上游主版本线 5.6.0→6.3.1、`Confluent.Kafka` 2.16.0、`NativeCompressions` 跨 1.0 稳定线 1.0.1）与 Kafka broker 后台可观测增强、EF Outbox 查询参数化形态收窄（行为等价）。
> **组织方式**：分两层——上方按 Keep a Changelog 分类给出**消费者可见变更**；文末附录保留本版工程过程明细（转正前 `[Unreleased]` 原料原文）。规范见 [`docs/release.md`](docs/release.md) §十一。

### Changed 变更

- **包版本 `3.2.0` → `3.3.0`（SemVer Minor）**：公开 API 快照全量差异 1 行——为唯一的程序集版本引用行（`Version=3.2.0.0` → `Version=3.3.0.0`，位于 `Saga.IsTimedOut` 参数 `IReadOnlyList<SagaStep>` 的程序集限定名内），30 个程序集零类型/成员/签名增删改，本版无破坏性 API 变更。程序集标识随版本变化，强命名引用与绑定重定向需按 3.3.0 更新。

- **Kafka 后台事件可观测（消费者可见的日志行为增强，无公共 API 变化）**：`KafkaBroker` 的 producer/consumer 构造接线官方示例标配三 handler（2026-10-08 全量 API 审计 P2 对齐）——`SetErrorHandler`（致命错误升 Error 携带 `KafkaException`；librdkafka 自愈类错误走 Warning）/ `SetLogHandler`（`SyslogLevel` ≤ Warning 走 Warning，Notice/Info/Debug 走 Debug）/ `SetStatisticsHandler`（Debug 门控，仅在 config 显式设 `StatisticsIntervalMs` 时回调，默认关闭）。此前 librdkafka 后台线程的错误/日志/统计结构性不可见。
- **EF Outbox 两处 `FromSqlRaw` → `FromSql`（行为等价，注入面收窄）**：`MySqlOutboxDbContext.LeasePendingMessagesAsync` 回读与 `SqliteOutboxDbContext.GetPendingMessagesAsync`——`{N}` 位置占位符迁移为 FormattableString 插值孔（EF 逐孔生成 DbParameter），EF1002 豁免注记随迁移移除；`CrossStackPredicateParityTests` 的源码切片锚同步（归一化规则本就统一 `{N}`/`{name}` 两种形态，断言语义不变——迁移后 EF 与 PalORM 命名孔形态同构）。其余 5 处维持 `FromSqlRaw`：内联方言时钟表达式（`NOW()`/`SYSDATETIMEOFFSET()`/`UTC_TIMESTAMP()` 属 DB 时钟设计决策，FormattableString 形态无法参数化内联 SQL 文本），既有 EF1002 豁免注记在案。

### Dependencies 依赖

- **PalORM 五包 6.1.0 → 6.3.1（2026-10-08 升位，消费者可见）**：五包同步升位（6.x 线内，跨 6.2.0/6.3.0/6.3.1 九日三连发），下一版 nuspec 的 PalORM 声明随之上移。上游无 releaseNotes 渠道，按 PD9 纪律以编译+测试实证破坏面：Release 全量构建 0 警告 0 错误；全量测试面板 1502 项 0 失败 = 1434 通过 + 68 环境跳过（60 Docker/容器 + 8 broker），与 3.2.0 基线逐项一致；XML 契约复核——`PreWarmAsync` 预热语义、会话级熔断「默认阈值 5 形同虚设」叙述在 6.3.1 XML 原样在；`readFromReplica` 回落主库契约（READ-002）由 6.3.1 新读路由助手 `CreateReadRoutedCommandAsync`（上游 ARCH-001 收口，2026-10-06）继承并明示 opt-in 教义不回归，本仓纯读流用法（无事务无并行作用域）落「否则主连接」分支语义不变，6 处适配层注释版本注记同提交升号。
- **`TUnit` / `TUnit.FsCheck` 1.71.0 → 1.73.5**：测试期依赖，不进产物。MTP 传递解析 2.4.1 → 2.5.0（实测），`docs/conventions.md` §10.6 与 `docs/testing.md` 版本行同提交更新。
- **`Confluent.Kafka` 2.15.1 → 2.16.0（消费者可见）**：Kafka 适配器依赖声明随 nuspec 上移。`ISerializer<T>.Serialize` 返回 `byte[]` 的不可消除双重拷贝理由经 2.16.0 反射实证仍成立。
- **`NativeCompressions` 0.6.1 → 1.0.1（消费者可见）**：压缩原生绑定适配器依赖上移，0.x 线转 1.0 稳定线。`LZ4.GetMaxCompressedLength` 的 Int32/UIntPtr 双重载在 1.0.1 反射实证仍在，显式 UIntPtr 调用路径不变；往返与守卫测试全绿。
- **`Microsoft.Data.SqlClient` 7.1.0 → 7.1.1**：零引用中央声明（全仓无 `PackageReference` 消费），升位不影响产物。
- **全量核新结论（2026-10-08，NuGet flatcontainer/`dotnet list package --outdated` 双源实测）**：微软官方包 11 线 rc.2 仍未发，EF Core ×4 + Extensions ×5 + System 系钉扎维持 `11.0.0-rc.1.26425.128`（GA 预计 2026-11）；其余第三方均已在最新稳定版（Dapper 2.1.89 / Dapper.AOT 1.1.0 / Npgsql 10.0.3 / MySqlConnector 2.6.2 / RabbitMQ.Client 7.2.2 / MemoryPack 1.21.4 / ZLogger 2.5.10 / ByteAether.Ulid 1.4.1 / Testcontainers 4.15.0 / FsCheck 3.4.0 / SQLitePCLRaw 3.0.5 / Humanizer.Core 3.0.10 / System.* 4.x-6.x 钉扎线 / Roslyn 5.9.0）；`Verify.TUnit` 最新 33.3.2 依 2026-09-23 裁决**不升**（license-policy 包级禁令 ≥33，SponsorCheck 商业许可门）；`BenchmarkDotNet` 0.16.0-preview.2、`ByteAether.Ulid` 1.4.2-rc.0、`System.Runtime.CompilerServices.Unsafe` 7.0.0-preview 线仅预发布不升。
- **PalORM 五包 5.6.0 → 6.1.0（2026-09-29 升位，消费者可见）**：五包同步升位（主版本线，跨 5.7/5.8/5.9/6.0.0/6.0.1），下一版 nuspec 的 PalORM 声明随之上移。上游无 releaseNotes 渠道（NuGet registration API 仅固定描述），按 PD9 纪律以编译+测试实证破坏面：Release 全量构建 0 警告 0 错误（编译面零破坏——5.6.0 引入的 `readFromReplica` 第 2 位参数本仓已全部命名参数化，无新破坏面暴露）；全量测试面板 1502 项 0 失败 = 1434 通过 + 68 环境跳过（60 Docker + 8 broker），与 3.2.0 基线逐项一致；XML 契约复核——`PreWarmAsync` 预热语义、`readFromReplica` 回落主库契约（READ-002）、会话级熔断「默认阈值 5 形同虚设」叙述在 6.1.0 XML 原样在，5 处适配层注释的版本注记同提交升号。
- **`TUnit` / `TUnit.FsCheck` 1.69.0 → 1.71.0（2026-09-29）**：测试期依赖，不进产物。MTP 传递解析仍为 2.4.1，`docs/conventions.md` §10.6 与 `docs/testing.md` 版本行同提交更新。
- **全量核新结论（2026-09-29，NuGet flatcontainer/`dotnet list package --outdated` 双源实测）**：微软官方包 11 线 rc.2 未发，EF Core ×4 + Extensions ×5 + System 系钉扎维持 `11.0.0-rc.1.26425.128`（GA 预计 2026-11）；其余第三方均已在最新稳定版（SqlClient 7.1.0 / Dapper 2.1.89 / Dapper.AOT 1.1.0 / Npgsql 10.0.3 / MySqlConnector 2.6.2 / RabbitMQ.Client 7.2.2 / Confluent.Kafka 2.15.1 / MemoryPack 1.21.4 / ZLogger 2.5.10 / ByteAether.Ulid 1.4.1 / Testcontainers 4.15.0 / FsCheck 3.4.0 / BenchmarkDotNet 0.15.8 / NativeCompressions 0.6.1 / SQLitePCLRaw 3.0.5 / Humanizer.Core 3.0.10 / System.* 4.x-6.x 钉扎线 / Roslyn 5.9.0）；`Verify.TUnit` 最新 33.1.5 依 2026-09-23 裁决**不升**（license-policy 包级禁令 ≥33，SponsorCheck 商业许可门）。

### Tests 测试

- **测试面板 16 项目 Release 全量实测（2026-10-08，本机）**：总计 1502 = 1434 通过 + 0 失败 + 68 跳过；68 项跳过全部为 Testcontainers 方言/消息面（本机 Docker 不可达时按 T-17 跳过策略执行，`release.yml` 发布流水线全量复跑为权威口径）。上版基线 1502（2026-09-25 实测，构成逐项一致）。
- **FanOut 探针并发竞态修复（表征测试基础设施，非产品代码）**：`FanOutLaneSaga` 探针的 `ItemAttempts`/`ExecutedItems`/`CompensationLog` 为裸 `Dictionary`/`List`，而 `FanOutStep` 契约并行调用 executor（`Task.Run`+`WhenAll`）——面板负载下竞态丢写（`ItemAttempts` 丢 key → item2 误判首试再抛 → 多一轮整批重放 → `ExecutedItems` 4≠3），`PartialFailure_RetriesThenSucceeds` 于 2026-10-08 升位面板实证偶发红一次（隔离复跑 3 次不复现）。修复：三集合写入收敛到探针内锁，表征语义（整批重放 3 次执行断言）不变。产品代码 `FanOutStep` 自身经审查无同类问题（`errors` 已有锁、`results`/`completedFlags` 按索引隔离）。
- **`PalDDD.Core.Tests` 移除冗余 `TUnit.FsCheck` 引用**：该项目 `PropertyTests.cs` 用纯 FsCheck 核心 API（`Prop.ForAll`+`QuickCheckThrowOnFailure`），`FsCheck` 直接引用已在位；`[FsCheckProperty]` 属性用法仅存在于 `Transactions.Tests`——集成包在该项目零使用（2026-10-08 全量 API 审计清理）。

### 附录：工程过程明细（转正前 `[Unreleased]` 原料原文——内部叙事，非消费者变更摘要）

- **工程脚本层全 C# 收口（git hooks 判定层迁移 + .ai 侧最后两个 .sh 退役）**：① `.githooks/pre-commit`（182 行 bash）全部逻辑迁 `scripts/hook-pre-commit.cs`（暂存集快照一次性内存匹配——原版 SIGPIPE 教训的形态性消解 + 触发条件 selftest 红绿矩阵）；`.githooks/pre-push`（42 行）迁 `scripts/hook-pre-push.cs`；钩子文件退化为 3 行 shell 启动器（git hook 机制要求 shell 引导，属机制下限）。② `.ai/scripts/template-gate.sh` 迁主仓 `scripts/template-gate.cs`（判定真源本就是 template-compile-probe 工程）；`install-ai-system.sh`（250 行）迁 `.ai/scripts/install-ai-system.cs` v3（`--source` 参数化 + 非交互默认 N）——MIG-012 时代"宿主无 .NET"不迁裁决翻案（门禁真身全为 dotnet run 后该约束失效，见 docs/design/script-migration-tasks.md 翻案记录）。③ `.gitattributes` 补 `.githooks/* eol=lf`（主仓与 .ai 双侧）——无扩展名 hook 此前被 `* eol=crlf` 兜住，新 clone 会 CRLF 化 hook 致 bash 死（*.sh 同型死法，存量隐患顺手修复）。④ 登记面同步：gate-audit manualTools 增 template-gate、verify-ai V2 名单全 .cs 化、跨仓契约版本 1→2（D13 双侧）。受跟踪文件脚本语言面：主仓与 .ai 全部零 .sh/.py/.mjs，唯余 3 行 × 3 的 hook 启动器与 CI step 引导语法（机制不可消除面）。
- **bench 项目分析器开启**：移除 `RunAnalyzersDuringBuild=false`（原值使 BDN 0.15.7+ 的 Roslyn 用法分析器——基准类结构/`[Params]`/单一 baseline 校验——失效）；开启当场命中 `SagaLaneBenchmarks.cs` 5 个全局命名空间类型（CA1050），按仓库惯例迁入 `PalDDD.Benchmarks` 命名空间（`Program.cs` 既有 `using` 即解析，零引用改动），ITM-810 时代的全局命名空间勘正注记随形态废止一并更新。

---

## [3.2.0] — 2026-09-25

> **范围**：`v3.1.0`（2026-09-23，`f4acdd5`）→ `v3.2.0`（本提交），25 个提交。
> **兼容性**：SemVer Minor——新增 3 项公共 API 全部向后兼容（参数带默认值 / 新增重载 / 可选标注）；公开 API 快照 653 行新增、1 行删除（被删行即唯一的程序集版本引用 3.1.0.0 → 3.2.0.0；原 12 程序集零签名变更，652 行内容新增主要来自快照覆盖扩展的 19 个数据面程序集）。唯一行为不兼容点为压缩损坏帧异常类型统一（见 Changed 第二条），经裁决（2026-09-25）按破坏面量级定为 Minor 而非 Major。
> **组织方式**：分两层——上方按 Keep a Changelog 分类给出**消费者可见变更**；文末附录保留本版工程过程明细（转正前 `[Unreleased]` 原料原文）。规范见 [`docs/release.md`](docs/release.md) §十一。

### Added 新增

- **PalORM 连接池启动期预热入口（三方言，消费者可见）**：`AddPalOrmSqlite/PostgreSql/MySql` 新增可选参数 `prewarmConnectionCount`（默认 0 = 行为不变）——大于 0 时注册 `PalOrmPreWarmHostedService<TProvider>`（新增公共类型），宿主启动期执行上游 `DataSession<TProvider>.PreWarmAsync` 逐条灌暖连接（远程建连实测 ~8.5 ms/条，上游 XML）。物理连接进 ADO.NET 进程级共享池（按连接串键控），预热 options 与 DI 工厂共用同一实例保证同池；**失败降级启动**（记 Warning 不炸 host，fail-fast 需求由调用方自调 PreWarmAsync）；SQLite 上游契约为空操作（传值无害，保持三方言对称）。生产场景须与 `DbOptions.MinPoolSize` 配合（只预热不设下限会被空闲修剪清空，上游 XML 明示）。该类型落入扩展后的公共 API 快照覆盖。
- **MySQL 数据源新增 builder 配置重载**（`AddPalMySqlDataSource(cs, applyOptimization, Action<MySqlDataSourceBuilder>?)`）：在 `Build()` 前注入自定义配置，使 MySqlConnector 2.6 的 `ConfigureTracing`（OTel 语义约定、traceparent 下发）等 builder 级能力对调用方可达——此前 MySQL 侧无任何入口可摸到 builder，该能力结构性不可达。**新增重载而非改签名**，既有调用方二进制与源码均不受影响；PG 姊妹入口本就有 `Action<NpgsqlDataSourceBuilder>`，此为方言间对齐。
- **PalORM 读副本路由标注（消费者可用，行为可选）**：`PalOrmEventLog` 两个纯读流（ReadStreamAsync/ReadAllAsync）与 `PalOrmOutboxStore.GetPendingMessagesAsync`（租约前置候选读）三处读点标注 `readFromReplica: true`（PalORM 5.6.0 读副本路由能力）。**未配置 `DbOptions.ReadConnectionString` 时行为逐位不变**（上游契约回落主库）；配置后纯读点可路由副本，4 个 read-after-write 敏感点（版本预检 ×2、回查、租约回读）保持主库。正确性边界已声明于代码注释：候选读走副本最多产生无害空批，租约原子裁决恒在主库。

### Changed 变更

- **包版本 `3.1.0` → `3.2.0`（SemVer Minor）**：公开 API 快照全量差异 653 行新增、1 行删除——删除/新增对为唯一的程序集版本引用行（`Version=3.1.0.0` → `Version=3.2.0.0`），652 行内容新增来自快照覆盖扩展的 19 个数据面适配层程序集与本版新增成员，原 12 程序集无任何类型/成员/签名增删改，本版无破坏性 API 变更。程序集标识随版本变化，强命名引用与绑定重定向需按 3.2.0 更新。
- **原生压缩解压路径改为流式 decoder（解压炸弹防护时序改善）**：`LZ4Compressor`/`ZStandardCompressor`/`OpenZLCompressor` 的 `Decompress` 从「一次性全量分配 → 返回后检查上限」改为 `LZ4Decoder`/`ZstandardDecoder` span 循环（形态对齐本仓既有 `BrotliCompressor`），输出累计每轮即校验 `MaxOutputBytes`，**超限的下一轮立即中止**——上限检查先于后续分配生效，而非等全量解压完成。**消费者可见的行为差异**：① 超限输入（正常载荷超 64MB 上限）仍在解压过程中途抛 `InvalidDataException`（类型不变，消息含算法名与上限值），不再先承受一次大额分配；② **损坏/截断压缩帧的失败路径异常类型变更**——原实现透传上游的 `InvalidOperationException`（LZ4 "Invalid LZ4 frame."）或 `ZstandardException`，现统一转为 `InvalidDataException`（`解压失败：{status}`）；捕获上述原异常类型处理损坏数据的下游需同步调整；③ 正常路径内存峰值为 2×输入（首轮 `GetSpan(2×input)`，8MB 输入上限下 ≤16MB）——原实现为渐进增长，此代价为本方案取舍。输入上限（8MB）与输出上限（64MB）数值与 round-trip 结果不变。原声明「此限制受外部库 API 设计约束，无法在适配层修复」经包内 API 实测**失实**，已一并修正。

### Dependencies 依赖

- **PalORM 五包 5.5.1 → 5.6.0（2026-09-25 升位，消费者可见）**：五包同步升位，下一版 nuspec 的 PalORM 声明随之上移。5.6.0 为性能轮（写路径 −53%、UPSERT 分配 −28%），上游 CHANGELOG 声明 SemVer Minor 无破坏性变更，但**实测其 `QueryAsync`/`QueryFirstAsync`/`QuerySingleAsync`/`ScalarAsync`/`QueryAsyncEnumerable` 五个读入口在第 2 位插入了 `bool readFromReplica = false`**（读副本路由）——对既有位置传参的调用是破坏性签名变更，上游未在 CHANGELOG 记载，编译期即报 CS1503。本仓 11 处调用点改用命名参数 `ct:` 消解（语义等价于 5.5.1：`readFromReplica` 取默认 false）。两处已声明的行为微变经核实不命中本仓（`InsertAsync` 显式主键路径的返回值全测试零断言；`CommandSqlSet.Insert` 全仓零引用）。**升级脚本/下游消费者注意**：若以位置参数传递 `CancellationToken`，升到 5.6.0 会编译失败。
- **`Microsoft.Data.SqlClient` 7.0.2 → 7.1.0**：7.1.0 已于 2026-09-18 转稳定发布，原"停 7.0.2"的依据（7.1.0-preview3 build 26238 早于 RC1 26425 属过时 preview 线）失效。该条为**零引用中央声明**（全仓无 `PackageReference` 消费），升位不影响产物。
- **`Verify.TUnit` 钉在 32.x（Dependabot 忽略 33+）**：Verify 33 起在构建期强制检查许可属性（SponsorCheck，未声明即中断构建），属带商业许可门的依赖。**已裁决（2026-09-23）：本项目只使用开源依赖，不接此类许可门**，故停在最后一个无该门的 32.x 线（其补丁仍自动跟进）。影响面仅测试基础设施，包内容与运行时行为不变。
- **依赖升位（Dependabot #3）**：`ByteAether.Ulid` 1.4.0 → 1.4.1、`Dapper` 2.1.86 → 2.1.89——这两项随包发布，**消费者可见**（3.1.0 的 nuspec 里 ULID 声明为 `1.4.0`、Dapper 声明为 `2.1.86`，下一版起随之上移）；`TUnit`/`TUnit.FsCheck` 1.66.27 → 1.69.0、`Verify.TUnit` 32.0.0 → 32.0.1 属测试期依赖，不进产物。

### Documentation 文档

- **公共 API 快照覆盖扩展至数据面适配层**（+19 程序集，`decision-2026-09-25-publicapi-snapshot-scope-expansion`）：原 11 程序集范围决策的两个前提经对抗复核实证失效（G23 只校验快照内程序集——数据面 public API 变更零机械防线；基线实测增量 652 行）。快照宿主自 `PalDDD.Core.Tests` 迁至 `PalDDD.Integration.Tests`（后者在架构守卫的 Infra 白名单内——Domain 测试项目被 `DomainTests_DoNotReferenceInfrastructureImplementations` 禁止引用 Infra 实现，实测拦截）。编译时组件与零代码 Metapackage 不纳入。
- **PalORM 三方言弹性回调的熔断作用域声明**：上游 5.6.0 XML 明示 Scoped 会话下会话级熔断「默认阈值 5 几乎不可能达到，熔断器形同虚设」——`AddPalOrm*` 的 `configureResilience` XML doc 补声明：启用 `WithCircuitBreaker` 必须同步在传入的 `DbOptions` 上设 `CircuitBreakerScope = Process`（`DataSession` 不暴露 Options，回调内不可达）。
- **引用库最新特性使用审计**（`docs/review/feature-usage-audit-2026-09-25.md`）：55 个中央包按库族分 5 组审计，逐条特性附官方来源与 grep 证据；记录结构性结论（本仓 EF 走 raw SQL、PalORM 走 FormattableString 窄路径，故多数新特性零接触面）、5 项已实施、5 项待裁决（读副本路由 / 熔断作用域 / span 压缩编码 / broker tracing / 连接池预热）、以及含历史否决项在内的不建议清单。
- **RabbitMQ 内建 tracing 共存语义文档化**（源码级结论，反编译 7.2.2）：客户端经 W3C `DistributedContextPropagator` 注入/提取的 header 键与本仓 `traceparent` 同键；**宿主未注册 ActivityListener 时客户端 tracing 完全不激活**（`HasListeners()` 短路），本仓手工头行为不变；注册 listener 后消费侧先读本仓头建链（链条衔接正确），发布侧 Activity.Current 链会覆盖 outbox 还原头——需保留原始上下文者可自定义 `RabbitMQActivitySource.ContextInjector`。结论落 `RabbitMqBroker` XML doc。
- **全仓文档与真实实现对齐**（3.1.0 之后口径）：架构文档的质量体系版本、诊断数 21→23、prompt 模板数 9→10、Outbox 指标补 `paldd.outbox.dead`/`persist_failed`、TUnit/MTP 版本、GitHub Actions 模板改为 ci.yml 实际形态、CI 触发表对齐 4 job 实态、测试计数口径 1379→1490（2026-09-23 全量实测 1430 通过 + 60 项无 Docker 跳过）；补写 3.1.0 消费者可见变更（Outbox 三栈对齐、EventLog 写路径零拷贝）与 3.0.0 的 UoW fail-fast / `MaxDegreeOfParallelism` 警示；`IPalOutboxStore` 的 v4.0 预告与 `PalDiagnostics` 的计数器勘正注释同步更新。

### Tests 测试

- **测试面板 16 项目 Release 全量实测（2026-09-25，本机）**：总计 1502 = 1434 通过 + 0 失败 + 68 跳过；68 项跳过全部为 Testcontainers 方言/消息面（本机 Docker 不可达时按 T-17 跳过策略执行，`release.yml` 发布流水线全量复跑为权威口径）。上版基线 1490（1430 通过 + 60 跳过，2026-09-23 实测）。
- **两个竞态测试加 `[Repeat(5)]`**（`SmartEnumTests.ConcurrentReads_All_*`、`OutboxSqliteConcurrencyTests.LeasePending_SequentialWorkers_*`）：竞态类测试单次通过可能只是采样侥幸，重复执行把偶发交错变必现信号。**TUnit Repeat 语义为"额外再跑 5 次"共 6 实例**，两测试净增 +10 测试实例（总计数 1492→1502，文档已同步）。
- **`VerifyChecks.Run()` 约定自检**（新增 `VerifyChecksTests`）：Verify 官方约定的机器检查——ModuleInitializer 位置、命名、received 产物处理一旦偏离官方形态即失败，而非等某次快照比对异常才暴露。首跑即抓出两处真实缺口并已修复：`.gitignore` 缺 `*.received.*`（快照失败产物可被误提交）、`.editorconfig` 缺 `[*.{received,verified}.{txt}]` 段（其段头内层花括号是官方文案原样，过不了 Verify 的切片解析，勿"简化"）。测试数 +1。
- **SQLite `DateTimeOffset` 翻译 canary**（`OutboxSqliteConcurrencyTests`）：EF11 RC1 探针实测确认 SQLite provider 仍不翻译 `DateTimeOffset` 范围比较与排序，而本仓 `SqliteOutboxDbContext` 的 raw SQL 谓词下推与 Lease 单语句正建立在该限制上。canary 锁住这两条——**若 EF 后续修复翻译则转红，即 GA 复核信号**（GA 计划 §三.1 提前完成，GA 日仅需复跑）。测试数 +1。

### 附录：工程过程明细（转正前 `[Unreleased]` 原料原文——内部叙事，非消费者变更摘要）

- **GZip/Deflate 解压 span decoder 改造经测量否决（已回滚，零产物变化）**：按 PERF 纪律量具先行——新增 `--compression` 基准入口（含 GZip/Deflate 解压档）测得改造后分配确定性劣化 +34%/+35%（`ArrayBufferWriter` 增长序列超省掉的 `MemoryStream`，两轮逐字节一致、真回滚后逐字节还原）、耗时在噪声带内无改善。改造不落地，**数据与结论留档 `docs/performance.md` 防未来重复提议**（.NET decoder 优化或大负载场景可凭已入库的基准复测）。包内容与运行时行为不变。
- **公开 API 快照与发布记录的同步判定收紧**：此前只在「最后一次提交」的范围内校验公开 API 快照变更是否同步记录了 CHANGELOG——那意味着只要在快照变更之后再补一个只改 CHANGELOG 的提交，这道校验就被绕过（本仓 3.1.0 首发时实际发生过这种洗白）。现改为按发布窗口（当前提交到上一个版本 tag 之间）**逐个提交**校验：哪个提交动了公开 API 快照却没同步记录，就报出哪个提交。影响仅限构建与评审纪律——包内容、运行时行为、公共 API 表面均不变。
- **新增配置文件策略门禁 `scripts/config-policy.cs`（pre-commit + CI 双接线）**：检查 A 对 `.github/dependabot.yml` 做行式校验——`groups.update-types` 词表 `{patch, minor, major}` 与 `ignore[].update-types` 词表 `version-update:semver-*` 混用即报（两处 schema 词表不同，错值只在配置落到默认分支时由 Dependabot 配置检查报出，dev 上长期不暴露），连同 `ignore[].versions` 非空、每条 entry 的 `labels`/`schedule.interval` 非空与 `commit-message.prefix` 存在时非空，违规一律带行号；检查 B 扫根 `Directory.Build.props`/`Directory.Build.targets` 的每处 `<Exec>`，须自带 `IgnoreExitCode="true"` 或所属 `<Target>` 的 Condition 含 `'$(GITHUB_ACTIONS)' != 'true'`，否则报位置与缺失项——锁住 v3.1.0 发布链被 `-warnaserror` 升级的 MSB3073 打断（`ConfigureGitHooks` 并发写 `.git/config`）这一事故形态不回潮。两检查合体依据（同属配置文件策略类、零判定交叉、共享一次接线成本）与行式校验边界（非完整 YAML 解析器，多行 flow/锚点不在覆盖面）见脚本头注释；`--selftest` 25 例覆盖每条规则的正负例（变异翻转 A/B 判定均致其红），gate-audit 登记 3 条隔离探针（A 词表拦截 / B 豁免缺失拦截 / 干净输入负向对照）。影响仅限构建与评审纪律，包内容与运行时行为不变。
- **新增依赖许可门禁 `scripts/license-policy.cs`（pre-commit + CI 双接线）**：2026-09-25 裁决「所有引用的库必须是开源的许可，不使用商业许可」的机械执行层——此前该裁决只写在 `dependabot.yml` 注释与本文件里，文档不会执行，升包不会被拦。三层判定：① **白名单 deny-by-default**（OSI/FSF 认可的 SPDX 表达式 21 项，含本仓实际需要的 `AGPL-3.0-only`=PalORM 五包与 `PostgreSQL`=Npgsql（OSI 2010-02-10 批准）；`OR`/`AND` 复合按逐操作数判，带 `WITH` 例外按整串命中）；② **包级禁令** `Verify`/`Verify.TUnit` 主版本 ≥ 33（SponsorCheck 商业维护费门，裁决 2026-09-23）——**禁令与许可字符串正交**：Verify 33 的 nuspec 许可仍是 MIT，纯白名单抓不到，只有按包+版本下限才能拦；③ **gap 台账**（观察态，对齐 `TechDebtGuardTests` T-24 纪律：包名+原因+owner+到期日，到期未处置即红）接住「判不了」的包，**台账不接「判了但不在白名单」**，因此不是绕过白名单的后门。判定输入全离线：直接依赖读 `Directory.Packages.props` 的 `PackageVersion` ∪ 各 `*.csproj` 的 `PackageReference`，传递依赖读 `dotnet list PalDDD.slnx package --include-transitive --format json`（落临时文件再解析），许可证据读本机 NuGet 缓存 nuspec 的 `license expression`/`file`/`licenseUrl`；取不到或推不出 SPDX 标识一律 fail-closed 不猜。判定集口径是**被引用的库**（csproj 引用 ∪ 已解析依赖图），只在 props 声明而全仓无消费的 5 条零消费声明不判定但逐条打印（禁令不受此口径约束）。退出码 0/1/2（2=自测失败）。`--selftest` 59 例含正负例（注入商业许可表达式必红、禁令 32/33 边界、台账到期边界、台账不放过非白名单）；变异翻转白名单判定后自测 49/59、exit 2，恢复后 59/59、exit 0；gate-audit 登记 2 条隔离探针（禁令命中必红 / 白名单放行负向对照）。首次全量实跑存量：判定集 147 包版本、0 失败、6 项登记为 gap 观察态（`CommandLineParser`/`Microsoft.DotNet.PlatformAbstractions`/`Microsoft.Testing.Extensions.CodeCoverage`/`SQLite` 为 `license type="file"`，`NETStandard.Library`/`librdkafka.redist` 只有指向 github 正文的 `licenseUrl`，均离线推不出标识）。影响仅限构建与评审纪律，包内容与运行时行为不变。
- **新增文档计数声明门禁 `scripts/count-audit.cs`（pre-commit + CI 双接线）**：2026-09-25 文档清扫实证计数静默漂移——诊断计数器 21→23、测试用例 1379→1490、断言点 89→99、prompt 模板 9→10、ADR 22→24——只能靠 165 次人工工具调用才找全，而这些数字大半能从源码/文件系统机械推导，不该靠人扫。**真源 = 推导，不建第二台账**：脚本内 16 项推导表（可打包包数 35 · src 项目数 36 · 测试项目数 16 · prompt 模板 9 · prompt 目录文件 10 · 遥测计数器 23 · Activity Start 方法 11 · 诊断 ID 38（PDDD 15 + PAL* 23）· ADR 24 · 踩坑 66+16=82 · 架构边界断言点 99 · src 源文件 220），全部现算现比——台账必然与真源漂移，这正是要治的病。扫描面 README.md、README.en.md、docs/**（排除 `review`/`decisions`/`migration`/`design` 带日期的历史目录），声明形态为数字+关键词，**写了必须与推导一致**、缺失不算错（PD34 去数字化口径），失实报 `文件:行号：声明 X vs 推导 Y`；推导 0 值/异常/依赖文件缺失、扫描面零声明一律 fail-closed（防「判定恒真」式假绿）。退出码 0/1/2（2=自测失败）。`--selftest` 41 例含正负例（注入假声明必红；段号 `6.2`、动词「断言 nupkg」、EFCore「（5 项目）」三类误伤防线），变异翻转声明判定后自测 26/41、exit 2，恢复后 41/41、exit 0；gate-audit 登记 2 条隔离探针（999 假声明必红 / 真值一致放行负向对照）。真实仓首跑存量失实 1 处并已改正：`docs/conventions.md:341`「219 源文件」vs 推导 220，改正后 16 项推导 · 19 文件 · 91 处声明全绿。不纳入：测试用例数（需 dotnet test 实测）、架构测试方法数 37（D12a 反射锚已守）。影响仅限构建与评审纪律，包内容与运行时行为不变。
- **CI test 步骤新增 skipped 计数聚合披露**（源自 zcode 会话历史全量审计 2026-09-25，`~/.zcode/references/AI_QUALITY_PALDDD_AUDIT_2026-09-25.md`）：测试面板排除与 Skip 数不披露曾把「Skip 16」报成「Skip 9」、45 个 Testcontainers 测试长期不在"12 项目面板全绿"的口径外——现汇总行强制披露 skipped 总数（尽力提取无命中记 0，不做硬断言）。影响仅限构建与评审纪律，包内容与运行时行为不变。
- **新增 `scripts/new-skip-guard.cs` 守卫（pre-commit 接线）**：把失败测试改成 Skip 修绿是「改测试修绿」的隐性形态，test-change-guard 抓不到（正常 TDD 节奏下新增 Skip 完全绕行）。判定抓**源码新增 Skip 标注**这个静态动作（`[Skip(` / `Skip = "` / `.Skip("` / `Skip.If(` 四形态）按**净增口径**（增删相抵放行，修改既有 Skip 的 reason 不误伤）；LINQ `.Skip(1)` 零误伤；数量棘轮不做（本地/CI 跳过构成波动大且需运行时基线）。豁免 `ALLOW_NEW_SKIP=1` + 提交信息写理由与到期日。CI 不补偿（暂存集语义在 CI 不存在，与 test-change-guard 同口径）。selftest 13 断言 + gate-audit 3 隔离探针（净增必红/干净放行/reason 修改放行）。影响仅限构建与评审纪律，包内容与运行时行为不变。
- **新增 `scripts/review-coverage-report.cs` 沉寂面报告（TOOL）**：差分评审对长期未变更文件结构性失明（审计实证：28 处门禁存量假绿全部集中在 MIG-012 后未动的文件，IUnitOfWork 缺陷引入侧最后改动 2026-07-05 才被无锚点普查暴露）。单次 `git log` 推导每文件沉寂时长，按目录聚合 + Top N 清单（`--top`/`--min-days` 可调）；评审覆盖软信号未纳入（grep 点名有双向误差，宁缺毋滥）。发布前验证清单 §4.1 增步骤 5.6：tag 前对 Top 沉寂文件定向抽查，不必全量普查（日历式全量普查已被 60+ 轮循环治理失灵实证否决）。影响仅限构建与评审纪律，包内容与运行时行为不变。
- **二轮审计：FsCheck 属性测试接入 `[FsCheckProperty]`（`BackoffPolicyPropertyTests` 3 处）**：`TUnit.FsCheck` 此前被 2 个 csproj 引用却零消费。迁移后失败 seed 直接进 TUnit 报告、`Replay`/`MaxTest` 声明式可配；生成域经自定义 Arbitrary 收敛到与原手写 `Gen.Choose` 完全相同的有界区间，迭代数沿用默认 100——语义与覆盖面不变，测试数不变。`PropertyTests` 的 7 处复杂生成器（SelectMany/元组组合）保留手写 `Prop.ForAll`（迁移成本大于收益）。
- **二轮审计勘正：首版 `HasCount` 建议撤销**——`HasCount(int)` 在 TUnit 1.69.0 已标 `[Obsolete]`（推荐 `Count().IsEqualTo`），按首版建议替换 80 处会在 `TreatWarningsAsErrors` 下编译失败；现有 `Assert.That(x.Count).IsEqualTo(n)` 是合法形态维持不变。教训已写入审计报告：API 面核验必须含 Obsolete 状态，"存在"≠"推荐"。

---

## [3.1.0] — 2026-09-23

> **组织方式**：分两层——上方按 Keep a Changelog 分类给出**消费者可见变更**；文末附录保留本版工程过程明细（转正前 `[Unreleased]` 原料原文）。规范见 [`docs/release.md`](docs/release.md) §十一。

### Changed 变更

- **包版本 `3.0.0` → `3.1.0`（SemVer Minor）**：公开 API 表面与 3.0.0 一致——公开 API 快照的全量差异只有程序集版本号一行（`Version=3.0.0.0` → `Version=3.1.0.0`），无类型/成员/签名增删改，本版无破坏性 API 变更。程序集标识随版本变化，强命名引用与绑定重定向需按 3.1.0 更新。
- **`InMemoryOutboxStore.MarkProcessed` 对未租约消息改为放行**（与其余三栈对齐）：此前对**从未租约**的消息直呼标记会被静默忽略（不标记、不报错），而 PalORM / Dapper / EF 三栈对该输入一律放行——InMemory 作为测试替身比生产更严格，会让测试与生产行为背离。**行为变更**：该输入现在正常标记；对已被其他 worker 重租的消息仍拒绝标记（原保护不变）。
- **`DapperOutboxStore.MarkProcessed` 在被拒时不再改写入参对象**：当标记因租约 token 不匹配被拒（未影响任何行）时，此前会把传入消息对象的租约字段清空，使调用方持有的对象与数据库实际状态不一致。**行为变更**：被拒路径下入参对象保持原样（与 PalORM 一致）。
- **`[Obsolete]` 标注的移除版本由 v3.0 改为 v4.0**：`DomainCapabilityAttribute`、`AggregateNameAttribute`、`SqlServerOutboxDbContext` 及若干接口备注此前标注"v3.0 移除"，但 3.0.0 已发布而未移除。现统一改指 **4.0**（与接口异步化合并为同一次破坏性变更，不再分两次）。**消费者可见**：编译器与编辑器显示的过时提示文案随之变化。
- **EventLog 写路径零拷贝**（性能）：`DapperEventLog`/`PalOrmEventLog` 的批量追加不再对每事件 payload/metadata 各做一次防御性 `ToArray()`，改用 `EventData` 构造期已拷贝的 internal 数组（v65 P3 已声明的零拷贝路径，与 `StoredEvent` 同机制；`EventData` 构造后不可变是公开 API 契约）。每事件省 2 次数组分配，1KB 级 payload 按万事件/秒计约 20MB/s GC 垃圾。行为不变（`InternalsVisibleTo` 已授权两包）。

### Documentation 文档

- **`docs/usage.md` 补三处调用方契约警示**：`ReadAllAsync` 全局位置的提交序倒挂约束（EF 栈因分配器行锁不可达，PalORM/Dapper 自增路径可达且已分别声明）；Dapper/PalORM 无事务时批量追加留前半批；EF 业务上下文混配 Dapper/PalORM outbox store 的孤儿消息误配场景。
- **`docs/testing.md` 与 `docs/conventions.md` 勘正 `PipelineStateMachine` 描述**：原称「~40B 可重用」与 `Dispatcher.cs` 实际「每请求新建（Dispatcher 为 Singleton 故不可跨请求重用）」矛盾；`testing.md` 的测试归属从 `AllocationContractTests`（实为 Core 分配契约）勘正为 `CqrsTests.Dispatcher_QueryAsync_NoHandlerOverhead_BaselineAllocation`。
- **`TransactionOptions.MaxDegreeOfParallelism` remarks 结掉 RabbitMQ 开放前置**：记录上述 7.x 语义核实结论与 `MaxOutstandingConfirms` 仅在 `CreateAsync` 路径生效的限制。

### Tests 测试

- **多方言测试在无 Testcontainers 时由硬失败改为跳过**：此前未启用 Testcontainers（或 Docker 不可达）时多方言夹具直接抛异常，使该测试项目在本机环境下整批红且覆盖率门禁本地无法闭环；现改为跳过并给出可读原因，与集成探针的既有语义一致。安全前提是外部数据库隐式回退路径已删除（相关连接串属性零消费者）。
- **新增未租约标记的契约测试**：锁住上述 `InMemoryOutboxStore` 的行为变更（经变异验证：改回原判定条件即精确红在该断言）。

### 附录：工程过程明细（转正前 `[Unreleased]` 原料原文——内部叙事，非消费者变更摘要）

- **版本承诺期限守卫**：src 内 `vX.Y` 出现在承诺语境（移除/窗口/预告/破坏性变更）时 `X.Y` 必须严格大于 `VersionPrefix`，否则门禁红；配套活账本（白名单 == 实测集双向断言）——首次运行即抓出 **13 处**过期承诺，人工审阅只找到 5 处。落地：`TechDebtGuardTests.VersionPromises_AreNotExpired`。
- **跨仓契约版本锚**：`.ai` 独立仓声明契约版本、主仓校验一致（`doc-consistency.cs` D13，tripwire）。引用层一致性原已由 `verify-ai` 的 V2/V19/V25 守护，本项补的是"契约版本本身无同步机制"这一残余缺口。
- **观察态到期守卫**：三处观察态（`tech-debt` Obsolete WARN / `gate` G24 WARN / `refine-scan` 高假阳性）登记 owner + 到期日 + 处置建议，到期未处置即红（`TechDebtGuardTests.ObservationStates_AreNotExpired`）。
- **三栈契约对齐的实现细节**（消费者可忽略）：`InMemoryOutboxStore.IsCurrentLeaseHolder` 去掉 `&& message.LockedBy is not null`，保留 `Status == Pending`（对齐 EFCore 的终态守卫）；ITM-174 的僵尸标记保护由 `_messages.Contains(..., ReferenceEqualityComparer.Instance)` 承载，未受影响（successor 替换后旧引用不在列表，仍被门控）。Dapper 侧改为消费 `affected`、仅 `> 0` 时清租约字段；SQL 侧 fencing 未变（仍传 owner/until/retryCount 三 token）；原 `P3-SRC-301` 声明保留并追加决策回应，决策文档 `docs/review/decision-2026-09-22-dapper-markprocessed-lease-clear.md`。
- **v3.0 → v4.0 改期的范围**：24 处 / 18 文件，含 3 处 `[Obsolete]`、10 处接口契约 Remarks、2 个 `.pal/prompts/*.prompt.md` 与若干实现注释。

---

## [3.0.0] — 2026-09-20

> **范围**：`v2.2.0`（2026-09-15）→ `v3.0.0`（本次转正，含 Outbox 并行发布、死信指标独立、Dapper Saga 快照 fail-fast、UoW 嵌套事务 fail-fast）。
> **兼容性**：SemVer **Major**——两处行为破坏性变更（均从静默错误转为 fail-fast；破坏面为"原本已出错但无异常"的路径，不破坏正确使用方）。
> **组织方式**：分两层——上方按 Keep a Changelog 分类给出**消费者可见变更**；文末附录保留本版工程过程明细（转正前 `[Unreleased]` 原料原文）。规范见 [`docs/release.md`](docs/release.md) §十一。

### Changed 变更

- **`OutboxOptions.MaxDegreeOfParallelism`（新增，默认 1）与 `OutboxBatchProcessor` 构造新增可选 `IServiceScopeFactory` 参数**（C1，perf-opt-sweep）：并行度 >1 时批内消息按分区交由 per-worker scope（独立 store/DbContext 实例）并行「反序列化 → 发布 → 标记」——吞吐随并行度提升；租约/fencing 互斥不受影响。**前提**：① broker 发布须线程安全（Kafka producer 天然支持；RabbitMQ 单 channel 并发发布已核实 RabbitMQ.Client 7.x 语义——publisher-confirms 仅护帧发送段、确认等待不串行，官方 `TestFloodPublishing` 8 并发同 channel 实证）；② 消费方幂等（并行加速下乱序提交概率上升）。直构造 `OutboxBatchProcessor` 的调用方不受影响（scopeFactory 为可选尾参；并行度 >1 且缺失时运行期 fail-fast 并给出指引）。
- **新增发件箱指标 `paldd.outbox.dead`（死信独立计数）与 `paldd.outbox.persist_failed`（状态持久化失败计数）**（R1/R2）：原死信合并在 `paldd.outbox.failed` 中积压不可见；原 `processed` 计数含持久化失败条目（与 DB 真相漂移）。现死信独立可见、`processed` 仅含落库成功条目。**迁移**：基于 `paldd.outbox.failed` 建立的告警规则无需变更（该计数语义不变）；建议新增基于 `dead` 的死信积压告警。
- **`DapperSagaStateStore<TState>.SaveChangesAsync` 在未注册 `JsonTypeInfo<TState>` 时改为抛 `InvalidOperationException`**（⚠️ 破坏性变更，决策见 `docs/review/decision-2026-09-19-saga-snapshot-failfast.md`）：原实现静默把 `saga_data` 写 NULL——Saga 的全部业务字段（CustomerId 等派生状态）在持久化中丢失且无异常/无日志/无启动诊断；重启恢复只还原元数据。现对齐 PalORM 栈同位置修复 fail-fast，异常消息含注册指引。**迁移**：DI 路径 `services.AddPalDapperSagaSnapshot(jsonTypeInfo)` 或构造函数第三参传入 source-generated `JsonTypeInfo<TState>`（见 `docs/usage.md` ⚠️ 段）。EF 栈用 source-generated converter，不受影响。
- **`IUnitOfWork.BeginTransactionAsync` 在事务已活动时改为抛 `InvalidOperationException`**（⚠️ 破坏性变更，决策见 ADR-023）：原 EF Core 与 PalORM 两栈为静默 no-op，而 `ExecuteInTransactionAsync` 是无条件「Begin → work → SaveChanges → Commit」——嵌套调用时内层 Commit 提交的是**外层**事务，导致静默原子性破坏（内层之后的外层工作失去事务保护，外层异常路径的回滚面对已提交事务）。现三栈统一为 fail-fast（对齐 Dapper 既有契约），接口与 `ExecuteInTransactionAsync` 文档同步声明「不支持嵌套」。**迁移**：需要在既有事务内执行工作的调用方，请直接执行工作委托或自行编排提交边界，不要嵌套调用本方法。

### 附录：工程过程明细（转正前 `[Unreleased]` 原料原文——内部叙事，非消费者变更摘要）

> 本段记录事后补正事实：`v3.0.0` tag 先于 CHANGELOG 转正打出（违反 §十一「同次提交转正、先于 tag」），本次为补正，涉及提交 `9377fdb`（版本升位）、`e41e312`（公共 API 快照转正，C1/R1/R2 有意 API 面）、`9184bd6`（快照版本号勘正）。tag 之后的本仓变更（`46d367d` 起）归 `[Unreleased]`，不属本版范围。
> 转正动机与发布卫生整改见 `docs/review/audit-2026-09-20.md` C1 项（CHANGELOG 未转正 + README 版本口径矛盾）。

---

## [2.2.0] — 2026-09-15

> **范围**：`v2.1.0`（2026-09-04，commit 0370c30）→ `v2.2.0`（转正提交），113 个提交。
> **兼容性**：SemVer Minor——向后兼容。包公共 API 无变化（快照 diff 经逐项核实仅为记录口径修正：delegate 类型标签、枚举成员字段化；无新增/移除诊断、无新增废弃）；依赖大范围升级（EF Core 11 rc.1、Dapper.AOT 1.1.0、PalORM 5.5.1、TUnit 1.66）。
> **组织方式**：分两层——上方按 Keep a Changelog 分类给出**消费者可见变更**；文末附录保留本版工程过程明细（转正前 `[Unreleased]` 原料原文）。规范见 [`docs/release.md`](docs/release.md) §十一。

### Added 新增

- **质量门禁脚本全量 C# 化**：`scripts/` 下 30+ 个 bash/Python 脚本迁移为 C# file-based app（零包依赖、`dotnet run` 直跑），删除全部 8 个 `.sh`/`.py`；迁移经后续三轮验证轮背书（全量实跑无功能失败）。动机：跨平台一致（Windows/Git Bash 语义对齐）、可 `--selftest` 自证、消除 Python 工具链依赖
- **门禁可信度审计 `scripts/gate-audit.cs`**：静态矩阵（接线/自证/已探三轴）+ 隔离式变异探针（临时独立仓库注入坏输入、断言非零退出，6 项覆盖）。动机：本仓两次「门禁假绿」实案（漏洞扫描 exit-0 空转、凭据扫描被管道掩码）此前只靠一次性人工探针，未沉淀为可重复验证
- **覆盖率双门禁接 CI**：全局阈值 job（阈值 0.70）+ 单模块降幅（`coverage-baseline.json` 15 项目基线 + 容差 5pp 判定 + `--update-baseline` 校准入口）——此前是「脚本就绪但未接线」的已知缺口
- **发布流程工具**：`scripts/changelog-facts.cs`（9 段机械事实收集：提交分布/公共 API diff/诊断/脚本/废弃/ADR/依赖/测试占位）/ `scripts/changelog-check.cs`（C1-C5 结构门禁：转正一致性/分类顺序/术语泄漏）
- **提交期守卫三新增**（pre-commit 达 7 道）：`test-change-guard`（拦截「只改测试不改 src」的改测试修绿签名）/ `xml-guard`（XML 良构性）/ `dapper-param-guard`（Dapper 匿名参数枚举直传拦截——PostgreSQL 拒绝该形态）
- **`--selftest` 覆盖 13 个门禁脚本**：判定抽纯函数 + 正负例 + 变异验证可红；`gate-audit` 矩阵 `UNVERIFIED` 由 9 清零
- **`verify-conventions` 新增 V8/V9/V10**：模板必填段齐全 / 文档命令引用的脚本路径存在 / 文档内部链接可解析
- **`encoding-gate` 新增 E5**：源文件行尾 CRLF 一致性（防编辑器/工具链默认 LF 污染工作树）

### Changed 变更

- **CI 验证矩阵扩展**：`aot-verify` 扩为双 sample（主干五项目引用图独立验证，此前仅 PalORM 单栈）；`coverage` 独立 job（`dotnet tool restore` 取 reportgenerator）；`build-and-test` 内门禁循环扩至 encoding/doc-consistency/tech-debt/test-gate + template-gate
- **git hooks 构建时自动配置**（新增根 `Directory.Build.targets`）：`core.hooksPath` 是 git 本地配置、不进版本库，新 clone 默认无钩子——`ConfigureGitHooks` 目标在首次构建时配置（不覆盖既有自定义配置、`.git` 缺失跳过、增量判定）
- **RabbitMQ 集成测试预检升级为 AMQP 协议级**：原 TCP 探活对「TCP 通、AMQP 死」半坏环境误判为可用——升级后完整握手探测，半坏环境快速跳过（实测从 5×19s 假失败降为秒级跳过）
- **覆盖率阈值 0.65 → 0.70**：原值锚定 2026-07-30 旧基线，实测新基线 72.98% 下余量约 8pp 失去早期预警意义；按项目「基线 − 3pp」原则校准

### Fixed 修复

- **构建完整性族**：bench 项目 XML 注释含连续双连字符致全仓构建失败（MSB4025，此前通过全部提交门禁——无 XML 良构校验，缺口由 `xml-guard` 关闭）；`encoding-gate` E2/E3 覆盖盲区（`scripts/samples/bench` 下 69 个 `.cs` 不受 BOM/mojibake 检查）
- **门禁可信度族**：翻转检测器把「跳过」误归「失败」致环境波动被误报为测试打摆（Kafka 预检跳过与恢复叠加成假翻转）；覆盖率合并 glob 路径缺陷（单层匹配漏 MTP 双层落点，门禁空转）
- **Dapper 枚举参数族**：Checkpoint 枚举参数直传驱动被 PostgreSQL 拒绝（Dapper.AOT 拦截器行为分叉，本地 SQLite 测试盲区致缺陷直达 CI）——修复 + 提交期守卫前移
- **文档引用族**：`docs/` 中指向已删除 `*.sh` 的可执行命令与索引引用（脚本迁移后未全量收口）——修正 + V9 机械防回归

### Dependencies 依赖

- **EF Core 11 `preview.7` → `rc.1`**（EntityFrameworkCore/InMemory/Relational/Sqlite）；`Microsoft.Extensions.*` 与 `System.*` 系同步 `11.0.0-rc.1`
- **Dapper.AOT 1.0.52 → 1.1.0**；**PalORM 5.4.0 → 5.5.1**（五包同步：Core/SourceGen/Sqlite/PostgreSql/MySql；消 SQLitePCLRaw NU1903 High 级漏洞）
- Confluent.Kafka 2.15.0 → 2.15.1（librdkafka.redist 传递依赖同步）
- 测试栈：TUnit 1.65.68 → 1.66.27 · Verify.TUnit 31.28.0 → 32.0.0 · Testcontainers 4.14.0 → 4.15.0
- 死条目清理：`Microsoft.EntityFrameworkCore.SqlServer` / `.Design` / `Npgsql.EntityFrameworkCore.PostgreSQL` / `Pomelo.EntityFrameworkCore.MySql`（均不在用）

### Documentation 文档

- 新增 ADR-022「分析器诊断与生成器诊断的分层关系 + 稳定名称谓词共享」（三对诊断定性为分层而非冗余）
- `docs/release.md` 新增 §十二「变更日志生成流程」SOP（Phase 0 常态累积 → P1 事实收集 → P2 核验 → P3 起草 → P4 校验 → P5 转正 → P6/P7 发布与事后同步）
- 新增项目级 `AGENTS.md`（agent 操作层索引：三条硬红线 + 门禁体系 + 已知陷阱 + 文档地图）
- 新增 `docs/review/` 全仓评审系列（2026-09-10 至 09-14 六轮运行报告与行动项清单）与 `docs/review/open-items-2026-09-14.md` 开放项账本
- `docs/usage.md` 补「与 HTTP 请求验证的分工」（请求形态 vs 领域规则两层职责）；新增 `docs/persistence-aot-status.md`、`docs/idempotency-rationale.md`
- 口径勘正族：`NAMING.md` 命名规则 6/7（禁会话相对表述）+ 7 份违规裁决清零；`conventions.md` 工程约束表四处按实测改写；`test-coverage-baseline.md` 按实测校准

### Tests 测试

- 16 项目面板：v2.1.0 基线 1202 → **v2.2.0 实测 1379**（本机 1311 通过 + 54 环境依赖项由 CI Testcontainers 权威执行〔PalORM 多方言 46 + Messaging 集成 8〕+ 14 设计内跳过〔外部连接串优雅降级〕）；净增 177
- 门禁自证：13 个脚本 `--selftest`（均含变异验证可红）；`gate-audit` 探针 6 项全 PASS

### 附录：工程过程明细（转正前 `[Unreleased]` 原料原文——内部叙事，非消费者变更摘要）
> 逐轮明细真源 `.ai/review/metrics.md`；本版质量系统轮次 v75-v87。

#### Added 新增（原料原文）

- **变更日志事实收集器 `scripts/changelog-facts.cs`**：`dotnet run scripts/changelog-facts.cs -- <from-tag> [to-ref]` 一次产出 9 段机械可验证事实（提交分布/公共 API 快照 diff/新增诊断/废弃扫描/ADR 与文档增删/依赖变更/测试实测占位/[Unreleased] 原料），每条附可复查命令（`docs/release.md` §十二 Phase 1）。注：2026-09-11 MIG-012 已由 `.sh` 迁移为 C# file-based app，本行同步为现行形态
- **变更日志结构门禁 `scripts/changelog-check.cs`**：C1 [Unreleased] 首位 / C2 tag 与转正段一致性（未转正禁止打 tag）/ C3 分类顺序 / C4 Tests 段预估口径 WARN / C5 分类层内部术语泄漏 WARN——挂入发布前验证清单与打 tag 前核对。注：同上，已迁移为 `.cs`
- **门禁可信度审计 `scripts/gate-audit.cs`**：静态矩阵（`WIRED` 接线 / `SELFTEST` 自证 / `PROBED` 已探）叠加隔离式变异探针——在系统临时目录建独立 git 仓库注入已知坏输入，断言门禁必须非零退出。覆盖 4 项探针（`secret-scan` 双向、`encoding-gate` 核心判定与 `scripts/` 范围回归）。自带 `--selftest`（5 例，含变异验证可红）。动机：本仓已有两次「门禁假绿」实案（vuln-scan v53 exit-0 no-op；secret-scan 被 tee 掩码），其修复均为一次性人工探针
- **测试文件改动守卫 `scripts/test-change-guard.cs`**：拦截「暂存集含 `test/**` 修改/删除且无 `src/**` 变更」（改测试修绿签名）。豁免 `ALLOW_TEST_ONLY_CHANGE=1`。已接入 `pre-commit`
- **XML 良构性守卫 `scripts/xml-guard.cs`**：校验 `.csproj/.props/.slnx/.targets/.xml` 可加载（`--all` 全仓 / 默认暂存集）。已接入 `pre-commit`
- **`scripts/verify-conventions.cs` 新增 V8/V9 两条静态检查 + `--selftest`**（此前无自证能力）：**V8** 断言 `.pal/prompts/` 9 个模板的必填段齐全（该约束原标注为「人工」，且 `conventions.md` 的「六段结构」表述与实测的 5/6/7 段分布不符）；**V9** 断言文档中「命令形态」引用的脚本路径必须存在（只查 `bash X.sh` / `dotnet run X.cs`，排除 `docs/review/` 历史记录与含「下沉自/已迁移」等词的历史提及，保持高精度）。`--selftest` 12 例含双向负例，并经变异验证可红。已接入 `pre-commit`（`.md` 入暂存集时触发）——该脚本此前是未接线的观察态门禁，本次同时完成接线
- **`scripts/vuln-scan.cs` 补 `--selftest`**（此前无自证能力）：判定逻辑抽为纯函数 `ScanVulnerabilities`，自测以合成 JSON 覆盖 14 例——含本门禁最易错的「存在 + 是数组 + 非空」三条件语义（空数组/缺属性/非数组均不算命中）、`transitivePackages` 与 `topLevelPackages` 双路径、`resolvedVersion`/`severity` 的 `?` 回退、以及两条 fail-closed（缺 `id` 抛 `KeyNotFoundException`、损坏 JSON 抛 `JsonException`，均不得静默当成「无漏洞」）。经变异验证可红（把非空判定改为恒假 → 13/14），真实路径复跑与重构前一致（`0 个已知漏洞`，exit 0）。**动机**：该门禁有已证实的空转史（v53 为 exit-0 no-op），而漏洞扫描器的失败形态天然静默——「扫不到」与「没扫」输出完全相同
- **`scripts/guard.cs` 补第 8 道守卫 + 注册完整性核查 + `--selftest`**（本地漏检窗口收口）：**实测缺口**——`CompressionGuardTests`（解压炸弹防护：压缩输入上限 / 损坏输入拒绝 / 解压输出上限，16 测试）自 v2.1.0 起存在，但 `guard.cs` 创建时（v88/ITM-667）未将其纳入清单，导致**本地 pre-commit 一直不跑该安全守卫**（CI 的全量 `dotnet test` 会跑，故仅本地漏检）——正是该命令立命要消除的窗口。修法二处：登记为第 8 道（实测 `CompressionGuard … GREEN`）；新增**注册完整性核查**——扫描 `test/` 下守卫命名类（`*GateTests` / `*GuardTests` / `ArchitectureBoundaryTests`）与清单比对，未登记即红（有意只在 CI 跑者须登记进 `exemptGuardClasses` 并写明理由，豁免显式化）；扫描跳过行注释以避免「注释提到旧类名」的误报。`--selftest` 14 例，其中「清单含 CompressionGuardTests」一条是本次修复自身的回归守卫；自测在开发中已实际红过一次（抓出 filter→类名提取用 `Split("/*/")` 会得到 `*/X/*` 的真 bug），对合成变异更有说服力。**该命令此前无自证能力（gate-audit 标 UNVERIFIED）**

- **`scripts/encoding-gate.cs` 补 `--selftest`**（此前无自证能力）：本门禁的判定全是字节级/指纹级，错一个字节即「静默放行」且输出与正常无异（E2 漏检 BOM、E3 漏检 mojibake 都不会有任何可观察差异）。自测 14 例覆盖：`ContainsByte` 的命中/不命中/空数组；`HasUtf8Bom` 的**三分支含边界**（有 BOM / 无 BOM 负向对照 / 仅 2 字节的短文件 / 空文件）；E1 与 E4 的 CR 字节双向；`ReadTextTolerant` 对非法 UTF-8 不抛（对齐 grep 字节语义）；指纹表的非空性、命中与不误报；以及**自指陷阱回归守卫**（断言本文件源码不含任何字面指纹——若指纹以字面字符写入，encoding-gate 会命中自己，该陷阱原先只写在注释里无机械守护）。另将 mojibake 指纹表抽为 `MojibakeFingerprints()` 单一来源供 E3 与自测共用。经变异验证可红（把 BOM 首字节 0xEF 改成 0xEE → 13/14）

- **`scripts/secret-scan.cs` 补 `--selftest`**（此前无自证能力）：本门禁有已证实的空转史（CI 注释记「ITM-648 凭据门禁实为 no-op」），且三层白名单写松一点即静默放过真实凭据——漏报与「确实没有凭据」输出完全一致。判定抽为纯函数 `JudgeLine`/`BuildPatterns`，自测 16 例覆盖：模式 1 的四类密钥前缀各自独立（AKIA / ghp_ / PEM 块头 / 不误报短前缀与普通文本）；模式 2 的命中与**三层白名单的每一条边界**，其中「短但有数字则命中」是关键边界（该条写松即漏报 `Password=a1` 这类真凭据）；主机键与密码键必须同行共存（仅其一不命中）；`Data Source` 亦识别；两模式同线可各报一次（不短路）；大小写不敏感。经变异验证可红（把白名单第三条的 `&&` 改成 `||` → 15/16，红的正是该边界）。另：所有权重样本改为**运行期拼装**以规避自指陷阱（见下）

- **`scripts/gate-lite.cs` 补 `--selftest`**（此前无自证能力）：G1-G3 均为「计数 == 0」判定，模式写细一点即静默漏报违规（与本会话已发现的 `encoding-gate` E2/E3 范围缺口同类）。三个计数函数都接收目录根，故自测对着**临时目录的真实文件**做判定，而非只测纯逻辑。6 例覆盖：G1 的整文件子串语义（sealed 出现在注释里也算通过）与路径过滤（Middleware/Extensions）、并显式钉住一处已知宽口径（正则 `public.*class.*Exception` 是子串匹配，类名含 Exception 的普通类也被计数，方向是多报而非漏报，属 MIG-012 要求保持的原 bash 语义）；G2 的四类合规首行（using/`//`/namespace/空行）与三类排除（obj、bin、SourceGen、Analyzers）各自独立；G3 与 G2 的**排除口径差异**（G3 仅排 obj/bin、不排 SourceGen）；全合规目录三项归零的负向对照；不存在目录退化为 0。开发中自测已真实红过一次（G1 期望值写错，暴露上述子串语义），非空转

- **`scripts/doc-consistency.cs` 补 `--selftest`**（此前无自证能力）：本壳仅保留 D7（`.ai/README.md` 文件地图指向的文件必须存在），判定分两步——正则路径提取与缺失过滤——两步都可能静默失效（提取正则写窄则一条都不匹配、写宽则误报），故抽为纯函数 `ExtractMapEntries`/`MissingMapEntries` 并覆盖 12 例：四类前缀提取（`gate/`/`refine/`/`review/`/`test/`，含数字与连字符）、同行情多次出现、字符集边界（不提取大写、不提取下划线、不提取非 `.md`）、空行、去重与 Ordinal 排序、存在性过滤、空输入。另钉住一处已知宽口径（正则无行首锚定，故 `docs/review/x.md` 中的 `review/x.md` 片段也会被提取——本仓实际写法是相对 `.ai/` 的短路径故不构成问题，但改锚定会无声改变提取集合，需显式改测试）。经变异验证可红（字符集加入 `A-Z` → 11/12）

- **`scripts/gate.cs` 补 `--selftest`**（此前无自证能力）：G23/G24 的判定都是「对 git 输出文本做计数后比对」，计数口径写错即静默放行（G23 漏判 = 公共 API 变更不记录；G24 漏判 = 跨平台守卫在 CI 平台 no-op），故把两处计数抽为纯函数 `G23Counts`/`G24Counts` 并对**合成 git 输出**覆盖 18 例，不需要真实 git fixture。覆盖：`ToLines` 的 CRLF 拆分与非空行计数；G23 的四种组合（快照有/无 × CHANGELOG 有/无）与两处口径差异（快照是**子串**匹配故更长路径亦命中；CHANGELOG 是**整行**匹配故 `docs/CHANGELOG.md` 不计入）与多行计数；G24 的 `WithoutExtension` 变体、归一化字面命中、非新增行不计（`-` 与空格起首）、`+++` 头行、空输出。经变异验证可红（CHANGELOG 整行匹配改子串匹配 → 17/18）。病态样本按自指陷阱一般化规则运行期拼装

- **`scripts/tech-debt.cs` 补 `--selftest`**（此前无自证能力）：判定分两层——`Check` 的三态（FAIL/ALLOW/PASS）与计数口径（路径段排除、行长边界、行级排除）——两层写错都表现为「计数为 0 于是 PASS」，即静默漏报。故把三态判定抽为纯函数 `Verdict`，并为 `LongLines`/`EnumerateCs` 加可选根参数以便对临时目录做真实判定。17 例覆盖：`Verdict` 五种组合（含两条易错边界——`allow` 无命中必须 PASS 而非 ALLOW、未知 `allow` 值与原 bash 一样与 count 无关地判 PASS）；`ContainsSegment` 的段边界（`myobj` 不是 `obj` 段、`objX` 后无斜杠不命中）；`EnumerateCs` 只收 `.cs` + Ordinal 稳定序 + Rel 为 posix + 目录不存在为空；`LongLines` 的 **180/181 行长边界**（含未多报）与路径段排除。经变异验证可红（边界改 181 → 15/17）

- **`scripts/ci-failed-tests.cs` 补 `--selftest`**（此前无自证能力，gate-audit 矩阵最后一项 UNVERIFIED）：三通道都是「从日志/报告里挑出该报的行」，失效形态是**该报的失败没报**——且因调用方一律 `|| true` 兜底，诊断静默丢失不会以非零退出暴露，属最难发现的失效类型。通道 ②的两个窗口判定抽为纯函数 `LogKeywordContexts`/`LogTailWindow` 并覆盖 22 例：关键字命中（最后 15 个窗口，数 `CTX\|` 行）、命中附下一行（下一行空/为末行两种边界）、大小写不敏感、`keys` 八项逐项独立（含 `timed out`/`exit code`/`error(s)` 等含空格与括号者）、普通行不误报；尾部两级窗口（末 120 行 → 去空白 → 最后 30 行）的**先后顺序**用 200 行构造钉住（起点必为第 170 行、早期行不出现）；`Truncate` 的 100 边界。经变异验证可红（15 命中窗口改 16 → 20/22）

- **`scripts/gate-audit.cs` 矩阵判定三处改进**：① **未接线脚本分类化**——此前对一切未接线者判「OBSERVE 未接线——永远不触发」，实测 17 个中 **16 个是按设计手工调用的工具**（`review-snapshot` 供评审粘贴输出、`fix-completeness` 修复提交前跑、`gate-audit` 自身按需跑等），一律报成问题属虚假告警；现改为五态 `OK`/`UNVERIFIED`/`TOOL`/`UNWIRED-GATE`/`REVIEW`，逐条登记工具理由，**17 个告警收敛为 1 个真缺口**（`ci-coverage`）。② **`REVIEW` 桶**：未接线且未登记者单列，使新增脚本必须被显式归类（不放过）。③ 判定抽为纯函数 `ClassifyVerdict` 并补 7 例自测（含「已接线时分类标记不改变结论」的两条边界）。自测 12/12

- **`scripts/verify-conventions.cs` 新增 V10「文档内部链接必须可解析」**（机械化一类此前无守护的缺陷）：实测来源——`docs/review/action-items-2026-09-13-v5.md` 引用 `review-2026-09-13-full-v5.md` 而该文件不存在。深查后**根因是文件名有误而非链接有误**：该文档标题为「第五轮全仓运行（2026-09-13）」、报告编号 `REVIEW-2026-09-13-V5`、基线 `4e53622`，均指 09-13，而同系列 v2/v3/v4 都满足「文件名日期 == 内容日期」，v5 是唯一破例。已 `git mv` 改名为 `review-2026-09-13-full-v5.md` 并同步引用处（全仓仅 NAMING 债务表一处），断链归零。V10 精度经首版教训修正：只查**以 `.md` 结尾**的目标（天然排除项目里 `[事实](代码可查)` 这类证据标注，无需维护排除清单），且按**所在文件目录**解析相对路径（首版按仓库根解析产生 12 条误报）。自测 20 例含 8 条 V10 用例，经变异验证可红（去掉 `.md` 限定 → 18/20）

#### Changed 变更（原料原文）

- **CI `aot-verify` job 扩为双 sample**：原仅 `PalOrmSample`（直接引用仅 `PalORM.Sqlite`，即只覆盖 PalORM 一条栈）。新增 `AotSample` 的 restore/publish/run 三步——其引用 Core/Serialization/Transactions/CQRS/DependencyInjection 五个主干项目，与前者引用图不重叠。此前主干项目的 AOT 运行时安全无 CI 守卫（`docs/release.md` 自述 AotSample 为「手动 AOT 验证示例」）。本地等价形式（win-x64）已实测：输出 `Generating native code`、产物仅 native exe（无托管 dll）、实跑 exit 0 且含 CQRS AOT 值类型管道检查通过

- **git hooks 改为构建时自动配置**（新增根 `Directory.Build.targets`）：`.githooks/` 下的守卫脚本**已被跟踪**（clone 即获得），但 `core.hooksPath` 是 **git 本地配置、不进版本库**——新 clone 默认无钩子，于是整套本地防线（5 道 pre-commit 守卫 + pre-push 门禁）形同不存在。全仓此前仅在 `AGENTS.md` 的括号注里提过一次，**无安装脚本、无 clone 安装步骤、无任何地方校验其是否生效**。新增 `ConfigureGitHooks` 目标在**首次构建时**为本 clone 配置 hooksPath，把「记得手动 git config」变成「构建即生效」。设计约束：仅在未设置时写入（**不覆盖**开发者既有自定义 hooksPath）、`.git` 缺失时整目标跳过、git 失败一律 `ContinueOnError` 不阻断构建、以 `.git/config` → `obj/*.stamp` 做增量判定避免每次构建都起 git 进程。**实测**：解除本地配置（模拟新 clone）→ 构建 → 自动装回，二次构建被增量判定跳过。已知无害副作用：首次构建时并行节点各读到「未设置」，提示行与幂等写入会按项目数重复若干次，不做跨进程加锁

- **覆盖率门禁接线**（此前是「脚本就绪但未接线」的已知缺口，阻塞理由为「阈值未校准」）：新增独立 **`coverage` job**——不放 `build-and-test` 内是因为覆盖率需完整再跑一遍测试，放进主 job 会把关键路径拉长近一倍；独立 job 与 `aot-verify`/`dialect-probe` 同构，并行且失败域隔离。job 内先 `dotnet tool restore`：`reportgenerator` 由 `.config/dotnet-tools.json` 钉 5.5.11，**该工具此前 CI 从未还原过，是接线的隐藏前置**。阈值由 **0.65 校准为 0.70**：原值锚定 2026-07-30 基线 67.9%（67.9 − 3 ≈ 65），实测新基线 72.98% 下余量已达约 8pp、失去早期预警意义；按项目原始原则「基线 − 3pp」取 0.70。`ci-coverage.cs` 自测同步更新（11/11）

#### Fixed 修复（原料原文）

- **bench 项目不可加载导致全仓构建失败**：`bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj` 注释内出现 `--`（`CA1031:--verify-persist`），XML 注释禁止连续双连字符 → MSB4025 项目加载失败。该缺陷于 21549d3 引入，并通过全部提交时门禁（`secret-scan`/`encoding-gate`/`guard` 均不校验 XML 良构性）——缺口由新增的 `xml-guard.cs` 关闭
- **`encoding-gate` E2/E3 覆盖盲区**：E1 覆盖 6 目录而 E2（.cs BOM）/E3（.cs mojibake）仅覆盖 `src test`，`scripts/ samples/ bench/` 下 69 个 `.cs` 不受检查（实测当前 0 违规，属潜在）。已抽 `CsScanRoots` 常量统一为 5 目录，并加范围回归探针
- **`docs/` 中指向已不存在 `*.sh` 的可执行命令**：MIG-011/012 迁移脚本后文档未全量收口。本次修正主干手册 `docs/conventions.md`、`docs/release.md`、`docs/testing.md`、`CHANGELOG.md` 中的现行命令（统一改为「dotnet run 脚本 .cs」形态）。**并机制化以防再次回归**：`scripts/verify-conventions.cs` 新增 V9「文档命令形态引用的脚本路径必须存在」（只查 `bash X.sh`/`dotnet run X.cs` 命令形态，排除 `docs/review/` 历史记录与含「下沉自/已迁移」等词的历史提及）

#### Dependencies 依赖（原料原文）

- PalORM 5.4.0 → 5.5.1（五包同步：Core/SourceGen/Sqlite/PostgreSql/MySql；5.5.1 纯依赖升级——Roslyn 对齐本仓 5.9.0 钉扎、Microsoft.Data.Sqlite.Core 传递依赖 11.0.0-preview.7 → 11.0.0-rc.1、消 SQLitePCLRaw NU1903 High 级漏洞；5.5.0 行为变更点（脱敏信道/外部事务语义/WhereJson 守卫）经 grep 核实本仓零使用）
- Confluent.Kafka 2.15.0 → 2.15.1（补丁版；librdkafka.redist 传递依赖同步 2.15.1）

#### Documentation 文档（原料原文）

- 新增 ADR-022「分析器诊断与生成器诊断的分层关系 + 稳定名称谓词共享」：三对诊断（PDDD009/PALMSG004、PDDD010/PALMSG005、PDDD011/PALMSG002）维持共存并定性为分层而非冗余；`IsStableName` 谓词收敛为 `src/PalDDD.Shared/StableNameValidation.cs` 链接共享源码（`docs/decisions/022-analyzer-generator-diagnostic-layering.md`）
- `docs/release.md` 新增 §十二「变更日志生成流程」SOP：Phase 0 常态累积 → P1 事实收集 → P2 事实核验三问 → P3 起草 → P4 校验（机械门禁 + 人工六问）→ P5 转正（先于 tag）→ P6/P7 发布与事后同步，附流程失效回溯条款
- 新增项目级 `AGENTS.md`（agent 操作层，≤150 行）：三条硬红线 + 门禁体系（含接线状态）+ 已知陷阱（4 条实证）+ 编号体系 + 文档地图。定位为**索引而非复制**——编码规范权威出处仍是 `docs/conventions.md`，避免复制漂移
- 新增 `docs/review/rule-placement-audit-2026-09-13.md`：按「可脚本化 / 类型化 / 人工判断」三分类审计规则归属，登记 8 项实测承载缺口与本次处置、6 项未完成项与阻塞、完整的验证记录
- `docs/review/NAMING.md`：新增规则 6/7（**禁止会话相对表述**：`本轮`/`上轮`/`下一轮` → 改绝对日期 + commit；版本号不承载会话语义）+ 写法对照表；§六 手工文件清单改为命令式（原清单所列文件已全部不存在）并登记 7 份违规命名债务；新增可选「主题槽位」`{type}-{date}-{topic}`
- `docs/test-coverage-baseline.md`：§门禁阈值按实测改写——原表述「已自动化」与实际不符（脚本可运行 ≠ 门禁在运行），补记接线未完成的两个具体阻塞（本机 Docker 缺失致 12/16 项目中断、阈值 0.65 锚定 2026-07-30 旧基线）与四步接线前置序列
- `docs/conventions.md` §工程约束表三处勘正：① `.pal/prompts/` 由「六段结构」改为按实测的段数分布（9 模板 5/6/7 段不等，`bounded-context` 与 `task-intake` 结构不同）；② AOT 发布命令 `/p:PublishAot=true` → `-p:PublishAot=true`（Git Bash 下 `/p:` 被 MSYS 路径转换致 MSB1008，实测踩中）；③ 补 `AOT 运行时验证` 行记录 CI 双 sample 覆盖；④ `review-snapshot.sh`/`verify-action-items.sh` → `.cs`
- `docs/review/NAMING.md`：**7 份命名冲突已裁决并执行，存量清零**。判据为规则 5 的原意（禁止**自我评价**，理由是「如果一份报告确实比前一份更完整——用版本号区分」）：`full` 在这些文件名里标示**范围**（全仓轮 vs 局部，如 `action-items-*-bench.md`），可核验且不表达优劣 → 6 份违反的是规则**文字**而非**意图**，故细化规则而非改名（§四 接纳 `review-` 为现行类型前缀并说明与 `audit-` 的历史 corpus 关系；规则 5 增设范围标记白名单 `full`；§七 那条「review≠audit」作废）。另 1 份 `comprehensive-review-2026-09-13.md` 的 `comprehensive` 属自我评价**且**类型词不在最前 → 改名为 `review-2026-09-13-architecture.md`，内部评审编号同步改为 `REVIEW-2026-09-13-ARCH`。另新增 §一「主题槽位」`{type}-{date}-{topic}`；`docs/development.md` 增「git hooks（首次构建自动配置）」小节
- `docs/usage.md`：`验证` 一节新增「与 HTTP 请求验证的分工（两层，别混）」——此前全 `docs/` 对平台内置的 Minimal API 验证零提及（`AddValidation` / `IEndpointFilter` / `Microsoft.Extensions.Validation` / `IValidatableObject` 全部零命中）。补记两层分工：**请求形态**（query/header/body 的必填长度范围）归宿主侧的 ASP.NET Core 平台（.NET 10 起 `builder.Services.AddValidation()` + `DataAnnotations`，源生成器驱动、逐端点自动挂验证过滤器；`SkipValidationAttribute` / `ValidatableTypeAttribute` 于 .NET 10 随包发布、.NET 11 起不再标记 experimental）；**领域业务规则**归本框架的 `IPalValidator<T>` + `ValidationBehavior`（在 CQRS 管线内，故覆盖 HTTP / 消息消费者 / 后台任务全部路径）。并写明两种误用：把业务规则写进端点 `IEndpointFilter` 会让非 HTTP 路径绕过验证；只打 `[Required]` 而不启用 `AddValidation()` 则那些特性不生效（平台不自动验证 Minimal API 参数——官方文档明确未正确注册时返回 200 而非 400）。框架**不引用** `Microsoft.Extensions.Validation`。`ASP.NET Core 集成` 一节同步补端点映射器指引与该分工的交叉引用

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
- **默认质量**：库代码 179 处 `await` 全量补 `ConfigureAwait(false)`（179 为 v2.1.0 当时补入数的历史快照；现行口径不再锚裸数字计数——该数字随提交漂移曾致文档振荡，机械保证为 PDDD-G12 零违规，见 doc-consistency D12b）；mojibake 全仓清零；`IdentityGenerator.IsNumeric` 改 `SpecialType` 判定（extern alias 场景不再静默丢生成）

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

- 16 项目面板：v2.0.0 基线约 1053 → **v2.1.0 实测 1202**（本机 1153 通过 + 49 环境依赖项由 CI Testcontainers 权威执行）；净增 149（1202−1053）个回归/锁定测试，守卫类修复均带 mutation 红测锁定（移除修复套件必须变红）

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
- **库代码 179 处 `await` 全量补 `ConfigureAwait(false)`**（179 为 v2.1.0 当时补入数的历史快照；现行口径不锚裸数字计数——随提交漂移，机械保证为 PDDD-G12 零违规）；Native 解压 OOM 转 `InvalidDataException`
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
- **零反射红线**：MakeGenericType/Activator.CreateInstance/Assembly.GetTypes/Type.GetType(string) 全禁（ArchitectureBoundaryTests 机械守护；本段为 1.0.0-preview.1 当时 33 方法，2026-09-10 实测 41 方法）
- **测试框架**：TUnit 1.65.0 + MTP（禁 Microsoft.NET.Test.Sdk）
- **质量保障**：TreatWarningsAsErrors + AnalysisLevel=latest-all + 21 条 NoWarn 逐条 Justification（本段为 1.0.0-preview.1 当时值；2026-09-10 实测 22 条）+ MTP 原生 --coverage 覆盖率门禁 + assertion-strength-check 断言强度门禁（替代 Stryker：Stryker 不支持 TUnit/MTP）
- **规范文档**：conventions.md（1000 行 14 章）+ architecture.md（本段为 1.0.0-preview.1 当时 18 决策）+ 17 ADR（历史值；2026-09-10 实测 22 ADR）+ aot.md + performance.md + tutorial.md

### 已知限制

- **BenchmarkDotNet 0.15.8 不支持 .NET 11 Preview**：正式 BDN 报告不可生成，用 `--smoke` 模式（100 万次迭代手动计时）作为快速回归
- **`PalDDD.Transactions` 项目非 AOT 兼容**：Saga 子系统用 MakeGenericMethod/Activator（已带 [RequiresDynamicCode] 标注），主动声明 IsAotCompatible=false
- **Inbox SQLite TOCTOU 弱保证**：SQLite Inbox 用 INSERT OR IGNORE + SELECT 两步有极小竞态，生产推荐 PostgreSQL
- **`PalDDD.Core.SourceGen` 待修复**（1.0.0-preview.1 当时状态；2026-09-10 已修复）：`ISpecification.cs:218` 的 `_expression.Compile()` 违反 AOT 红线（gate-check PDDD-G8 已发现）——现编译动作收敛在私有 helper 并带 `[RequiresDynamicCode]`，G8 PASS
- **`Idempotency/Projections` 部分文件 ConfigureAwait 缺失**（1.0.0-preview.1 当时状态；2026-09-10 已修复）：12 处违规（gate-check PDDD-G12 已发现）——现库代码 await 均带 `ConfigureAwait(false)`，G12 零违规

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
5. **GitHub Release body 来自本文件**：release.yml 以 `body_path: CHANGELOG.md` 读取**整份** CHANGELOG 作为 Release 说明（当前实现为全文，非仅对应版本段落）；如需只发版本段落，需在发布流程中另做裁剪。
