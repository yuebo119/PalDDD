# 优化台账

> 用途：记录审计发现的状态与证据指针。本文件是**指针，不是第二真源**——性能否决的权威记录在 `.ai/perf-ledger.md`，架构决策在 `docs/decisions/`。
> 规则：只追加，不删除；已修复项保留锁定测试名称供回归轮重跑；已否决项必须引用原文；延后项必须有可观测的复活条件。
> 状态：`fixed`、`rejected`、`deferred`、`accepted-risk`、`candidate`、`needs-decision`。
> 轮次口径：R11 = 2026-10-22 全量缺陷挖掘 + 修复轮，代码基线 `cda53b9`，修复提交范围 `6d9fc6a..0f2ce38`。

## 一、R11 已闭环（26 条）

| ID | 轴 | 位置 | 发现 | 状态 | 证据 | 决策来源 | 锁定测试 | 轮次 | 复活条件 |
|---|---|---|---|---|---|---|---|---|---|
| OPT-101 | CORRECTNESS | `ProjectionRebuilder.cs:194-201`；`ProjectionProcessor.cs:194-201` | 默认 `CheckpointBatchSize=100` + 非续读回放源 + 已完成前缀时，增量回放静默返回 0（handler 零调用）；组首 Failed 洞 + 组末 Completed 时洞永不重放 | fixed（`6d9fc6a`） | E1：探针 D1/D4；批=1 对照通过 | R11 F1 | `ProjectionTests.ReplayAsync_DefaultBatch_NonResumableSource_SkipsCompletedPrefix`、`..._LeadingFailedHoleWithCompletedSuffix_ReplaysHole`、`..._AllCompleted_ReturnsZeroWithoutHandlerCalls` | R11 | 非续读源被包装为可续读，或批语义改为不再按组首取租约 |
| OPT-102 | CORRECTNESS | `ExceptionMiddleware.cs:25-27`；`ServiceRegistration.cs:24-31`；`HealthCheckExtensions.cs:30-36` | `docs/usage.md` 独立 ASP.NET 片段只调 `AddPalHealthChecks` + `UsePalExceptionHandler`，Hosting 未装 `IPalLogger<ExceptionMiddleware>`，首个请求构造失败 | fixed（`bcea1e0`） | E1：DI 探针 M1/M2/F1/F2 | R11 F2 | `HealthCheckExtensionsTests.AddPalHealthChecks_SelfProvidesLoggerFacadeForExceptionMiddleware`、`AddDetachedStreamRuns_SelfProvides...`、`ServiceRegistrationTests.AddPalDDD_SelfProvidesLoggerFacade` | R11 | Hosting 改为完全不依赖 `IPalLogger`，或文档片段补全核心栈注册 |
| OPT-103 | ROBUSTNESS | `HealthCheckExtensions.cs:30-36`；`OutboxHealthServiceCollectionExtensions.cs:16-20` | 健康检查重名注册使 `HealthCheckService.CheckHealthAsync` 抛 `ArgumentException`（/health 500），文档只称“二选一” | fixed（`13eace0`） | E1：探针 B1/B2 | R11 F3 | `HealthCheckExtensionsTests.AddPalHealthChecks_CalledTwice_DeduplicatesRegistrations`、`AddPalHealthChecks_WithOptionalOutboxPackage_DeduplicatesOutbox` | R11 | .NET 健康检查改为 last-wins 或允许重名 |
| OPT-104 | CORRECTNESS | `DapperProjectionCheckpointStore.cs:299-325`；`PalOrmProjectionCheckpointStore.cs:189-218`；`ProjectionCheckpointDbContext.cs:327-358` | SQLite/MySQL `CAST('99abc' AS INTEGER)` 截断为 99，续读返回 100 并跳过中间事件；接口契约要求非数值回退 null | fixed（`1012548`） | E1：SQLite 探针 S1；`dotnet pack` 无关 | R11 F4 | `ProjectionResumeSqliteTests.Dapper_NonNumericPosition_ReturnsNullFallback`、`EfCore_NonNumericPosition_ReturnsNullFallback`、`PalOrmProjectionCheckpointStoreTests.ProjectionCheckpoint_GetResumePosition_NonNumericPosition_ReturnsNullFallback` | R11 | 检查点 Position 契约改为允许任意字符串并给出解析规则 |
| OPT-105 | CONTRACT | `JsonMessageSerializer.cs:140-187`；`MemoryPackMessageSerializer.cs:48-60` | JSON 序列化器接受声明为 MemoryPack 的 descriptor 并写出 JSON；MemoryPack 泛型路径缺 ClrType 守卫 | fixed（`ab81192`） | E1：探针 J1；J2 为 ClrType 对照 | R11 F5 | `JsonMessageSerializerTests.ContentType_MemoryPackDescriptor_AllEntryPoints_Throw`、`MemoryPackSerializerTests.GenericPaths_DescriptorForDifferentClrType_ThrowArgumentException` | R11 | descriptor 工厂统一强制 ContentType，或序列化器改为自动推断 |
| OPT-106 | CORRECTNESS | `InboxProcessor.cs:331-338,361-388` | 批量消费取消时成功项未 `MarkProcessedMany`，滞留 Processing 至租约到期并再次执行副作用；单条路径即时标记 | fixed（`19de869`） | E1：探针 I1；单条对照 I2 | R11 F6 | `InboxProcessorTests.TryProcessBatchAsync_CancellationMarksAlreadySucceededMessages` | R11 | 取消语义改为整批不认领，或存储提供事务性批量完成 |
| OPT-107 | CORRECTNESS | `SagaProcessor.cs:311-313,322-329` | 批内 OCE 直接 throw，跳过已收集的补偿终态与租约冲刷；已补偿 saga 重租后再补偿 | fixed（`6012e7c`） | E3：代码路径确定 | R11 F7 | `SagaTimeoutProcessorTests.CheckTimeoutsAsync_CancellationDuringBatch_FlushesEarlierCompensatedTerminal` | R11 | 取消时补偿终态改由独立后台任务落库 |
| OPT-108 | OBSERVABILITY | `OutboxBatchProcessor.cs:434-465` | 默认 Auto + 并发 broker 的流水线路径四条告警用字符串重载，无 EventId；按 EventId 过滤的告警零命中 | fixed（`6f82c0b`） | E3：串行/流水线对照 | R11 F8 | `OutboxPublishPipeliningTests.Pipelined_PublishFailure_EmitsStableEventId` | R11 | 日志 Provider 统一把字符串重载映射到 EventId |
| OPT-109 | AOT | `ServiceRegistration.cs:57-79`；`README.md:188-190`；`docs/aot.md:65` | `AddPalCoreStack` remarks/README 暗示 AOT 可用，但内部无条件调用开放泛型 `AddPalPipelineBehaviors`，NativeAOT 注册期抛 `NotSupportedException` | fixed（文档纠偏，`2f6b021`） | E3：代码路径确定；AOT 双样本运行通过 | R11 F9 | 无单测（文档/remarks）；`AotSample` 与 `PalOrmSample` 发布运行作为回归门禁 | R11 | 开放泛型管道在 AOT 下可用，或 `AddPalCoreStack` 改为 AOT 分支 |
| OPT-110 | ROBUSTNESS | `OutboxBatchProcessor.cs:389-396,499,510` | 流水线死信标记未包裹 try/catch，`MarkDead` 的 DB 瞬时故障逃出循环，整批其余消息不发布不释放 | fixed（`6f82c0b`） | E3：串行路径同场景有 catch 对照 | R11 F10 | `OutboxPublishPipeliningTests.Pipelined_MarkDeadFailure_DoesNotAbortBatch` | R11 | 死信标记改由独立事务/重试队列承担 |
| OPT-111 | GATE | `scripts/test-gate.cs:98` | timeout 计数按行 `Contains("timeout-minutes")`，注释补数可掩盖缺失 job 超时 | fixed（`2f6b021`） | E3 + 变异探针：删一个 timeout 并加注释，新实现 `FAIL 7 个 job 仅 6 个有`，ci.yml 已还原 | R11 F11 | 变异探针（非自动化单测）；`test-gate` 日常运行 | R11 | ci.yml 改用非 YAML 结构或 job 键正则失效 |
| OPT-112 | DOCS | `docs/development.md:91-95`；`docs/testing.md:371,374,489,510-511`；`docs/conventions.md:857` | 公共 API 快照命令指向 `Core.Tests`（测试实际在 `Integration.Tests`），`dotnet build` 不执行测试，`git diff` 目录不存在，按文档操作是静默 no-op | fixed（`2f6b021`） | E3：文件位置与测试体读取 env 的路径对照 | R11 F12 | 无单测（文档）；命令已改为 Integration.Tests + MTP filter | R11 | 快照测试迁回 Core.Tests，或 MTP 改变 test 运行语义 |
| OPT-113 | GATE | `.github/workflows/release.yml:88-100` | 发布前只校验 `build-and-test` 一个 job，coverage/aot-verify/semgrep/quality-analyzers/dialect-probe 红仍可发布 | fixed（`2f6b021`） | E3：ci.yml job 清单与 jq 过滤对照 | R11 F13 | 无单测；发布 workflow 的 jq 清单与 ci.yml 同步维护 | R11 | GitHub API 提供 check-suite→workflow 映射，可改为自动发现 |
| OPT-114 | GATE | `.github/workflows/release.yml:148` | 包数门禁 `EXPECTED=35`，实际 `dotnet pack` 产出 36（37 个 src 项目中 `PalDDD.Prompts` 显式 `IsPackable=false`），每次发布必然失败 | fixed（`2f6b021`） | E1：全量 pack 实测 36；`count-audit` packable 推导同为 36 | R11 F14（原审计误判为文档漂移，FIX 轮勘正） | 无单测；发布 workflow 的包数断言 | R11 | 新增/移除可打包项目时必须同步 EXPECTED（count-audit 可作对照） |
| OPT-115 | CONTRACT | `PalOrmOutboxStore.cs:416-434` | `MarkProcessedBatchAsync([null])` 先解引用 `messages[0]` 再校验元素，抛 NRE；Dapper/EF 抛 ArgumentNullException | fixed（`ee771a6`） | E3：三栈异常类型对照 | R11 F15 | `PalOrmOutboxMultiDialectTests.Outbox_Sqlite_BatchMark_NullElement_ThrowsArgumentNullException` | R11 | 批量标记改为接受 null 元素并跳过 |
| OPT-116 | CONTRACT | `DapperInboxStore.cs:150-184` | `MarkProcessed`/`MarkFailed` 丢弃 affected 且不回写入参对象，复用对象的调用方看到陈旧 Processing | fixed（`ee771a6`） | E1：探针 S4 | R11 F16 | `DapperStoreTests.Inbox_MarkProcessedAsync`、`Inbox_MarkFailedAsync`（新增状态回写断言） | R11 | 接口改为显式返回新状态对象、禁止复用入参 |
| OPT-117 | TEST | `scripts/gate-audit.cs:1226-1229` | 两条自测只对 corpus 调 `string.Contains`，未调用 `AppearsAsExecutableInvocation`；判定退化时自测仍绿 | fixed（`2f6b021`） | E3：代码对照；`--selftest` 17/17 通过 | R11 F17 | `gate-audit -- --selftest` | R11 | 判定函数签名变化需同步自测 corpus |
| OPT-118 | CONTRACT | `DapperRetentionStore.cs:44-55`；`PalOrmRetentionStore.cs:47-58`；`EfCoreRetentionStore.cs:146-157`；`IRetentionStore.cs:7-17` | 直调 Store 时 `batchSize=-1` 在 SQLite 变无上限删除（`LIMIT -1`），`0` 在 MySQL/PG 与“无更多数据”不可区分 | fixed（`0f2ce38`） | E3：三栈入口守卫；SQLite 方言语义确定 | R11 F18 | `RetentionProcessorTests.StoreDirectCall_InvalidBatchSize_ThrowsAtEntry`、`PalOrmRetentionRegistrationTests.PalOrmRetentionStore_InvalidBatchSize_ThrowsAtEntry` | R11 | 接口改为允许 0 表示 dry-run，并给出方言无关语义 |
| OPT-119 | ROBUSTNESS | `StreamRunRegistry.cs:784-791` | 容量判定 `Volatile.Read` 后再 `Interlocked.Increment` 是 check-then-act，并发 Register 可超出 `MaxConcurrentRuns` | fixed（`ee771a6`） | E3：并发语义 | R11 F19 | `StreamRunRegistryTests.Register_ConcurrentAtCapacity_NeverExceedsMax` | R11 | 改为信号量/令牌桶，或上限语义改为软限制 |
| OPT-120 | CORRECTNESS | `StreamRunRegistry.cs:442-465` | 数字型 Last-Event-ID 在缓冲未命中时被当作位置索引；自定义数字 id 滚出缓冲后静默跳过数据且不报 gap | fixed（`0f2ce38`） | E3：默认位置型 id 与自定义 id 语义对照 | R11 F20 | `StreamRunRegistryTests.Attach_CustomNumericIds_DoNotFallBackToPositionParsing` | R11 | idSelector 改为显式声明“位置型/标识型”能力 |
| OPT-121 | CONTRACT | `EndpointExtensions.cs:216,272` | `keepAliveInterval` 只拒绝 ≤0；超过 `Task.Delay` 上限（约 49.7 天）时每个流返回 200 + `event: error` | fixed（`ee771a6`） | E3：`Task.Delay` 上限确定；`DetachedStreamOptions` 已有同款校验 | R11 F21 | `StreamQueryEndpointTests.MapStreamQuery_KeepAliveBeyondTaskDelayLimit_ThrowsAtMapTime` | R11 | .NET 放宽 `Task.Delay` 上限 |
| OPT-122 | CONTRACT | `NativeCompressors.cs:61-66,140-145,230-235` | 原生压缩器未知 `CompressionLevel` 静默回落 Balanced；托管实现抛 `ArgumentOutOfRangeException` | fixed（`85d780b`） | E3：三原生实现与三托管实现对照 | R11 F22 | `NativeCompressionTests.Native_UnknownCompressionLevel_ThrowsArgumentOutOfRange` | R11 | `CompressionLevel` 新增枚举值且需要兼容映射 |
| OPT-123 | CODEGEN | `IdentityGenerator.cs:289,622`；`EnumGenerator.cs:336,591` | `ISymbol.Name` 去掉 `@` 前缀，模板直接拼接关键字标识符生成非法代码（`partial record struct class`），无指向性诊断 | fixed（`0f2ce38`） | E3：Roslyn harness 生成 + 编译断言 | R11 F23 | `SourceGeneratorDirectTests.IdentityGenerator_KeywordTypeName_EmitsEscapedIdentifierAndCompiles`、`..._KeywordContainingType_...`、`EnumGenerator_KeywordTypeName_...` | R11 | C# 语法规则改为允许裸关键字类型名（不会发生） |
| OPT-124 | OBSERVABILITY | `SagaProcessor.cs:315-319` | 定义了 `PalLogEventIds.SagaTimeoutCheckFailed=1205` 但全仓零引用，单条 Saga 检查失败无法按 EventId 告警 | fixed（`ee771a6`） | E3：常量引用对照 | R11 F24 | 无单测；日志重载人工核验 | R11 | 1205 语义并入批量释放 1206 |
| OPT-125 | OBSERVABILITY | `OutboxBatchProcessor.cs:675-694` | Auto 能力回落（broker 不支持并发 / store 无批量标记）静默保留窗口 1/逐条标记，无日志或指标 | fixed（`ee771a6`） | E3：能力判定路径对照；Inbox 已有同族一次性提示 | R11 F25 | 无单测；进程内一次性日志人工核验 | R11 | Auto 改为强制校验并在启动期拒绝不满足能力组合 |
| OPT-126 | TEST | `MessagingTests.cs:247-262`；`TransactionsTests.cs:2308-2309`；`EventLogCrossStackMatrixTests.cs:19` 等 | 零断言 no-op 测试、`Throws` 后 `IsNotNull` 恒真断言、Dapper 矩阵类未加入 `dapper-global` 串行组 | fixed（`ee771a6`） | E3：测试体对照 | R11 F26 | `MessagingTests` 同步完成断言；`TransactionsTests` 改断言异常消息；三个矩阵类加 `NotInParallel("dapper-global")` | R11 | 测试框架改变并行/断言 API |

## 二、已否决与口径钉住（引原文，不重开）

| 项 | 原文 | 处置 |
|---|---|---|
| EF Saga Detached 返回 0 | `SagaStateDbContext.cs:551-557`：“对齐契约：Detached 状态直接返回 0（Dapper/PalORM 的自查 UPSERT 对新行真 INSERT，等价语义是「未见跟踪中的行 = 未写入」——正常管线 SagaProcessor 只保存已加载实体不受影响）” | accepted-risk，不重开；改变需先改 `ISagaStateStore` 契约与三栈调用方 |
| Dapper Saga INSERT 不写 version | `SqlTemplates.cs:424-426`：“三十八轮 P3 声明：不写 version 列——依赖 DB DEFAULT 0 与 state.Version 初始值隐式对齐；schema 默认值漂移将致首次 UPDATE（version CAS）必冲突” | accepted-risk，不重开；仅在新建行 Version≠0 时触发 |
| PalORM 唯一冲突分类的反射降级 | `SqlErrorClassifier.cs` IL2075 抑制理由：“裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃）” | accepted-risk（AOT 语义已声明）；H3 探针保留为下一轮候选 |
| `Entity.RaiseEvent` 同实例重入 | 注释把同实例重复发布定义为调用方误用并声明防御性截断 | rejected（调用方误用，非框架缺陷） |
| 消息契约验证空 catalog | catalog 为空即无 requirement，机制正例/正控均成立 | rejected（非契约违背） |
| 包数 36 | `docs/release.md:282`：“应等于 36”；`count-audit` packable 推导 36；全量 pack 实测 36 | 口径钉住：`release.yml` `EXPECTED=36`，新增/移除可打包项目必须同步 |

## 三、R12 内存与生命周期优化（2026-10-22）

| ID | 轴 | 位置 | 发现 | 状态 | 证据 | 决策来源 | 锁定测试 | 轮次 | 复活条件 |
|---|---|---|---|---|---|---|---|---|---|
| OPT-201 | MEMORY | `StreamRunRegistry.cs:31,875` | 终态 run 只受 10 分钟保留期约束、不计入 `MaxConcurrentRuns`，内存随“完成速率 × 保留期”增长，文档上界失效 | fixed | 测试前 20 个短 run 后 `TrackedCount=20`（上限 2×2=4）判红；修复后 ≤4 | R12 PF-1 | `StreamRunRegistryTests.TerminalRuns_RetentionIsBoundedByMaxConcurrentRuns` | R12 | 保留期语义改为持久回看（应改用 EventLog，不占注册表） |
| OPT-202 | MEMORY | `StreamRunRegistry.cs:429,488,744` | 每次断线重连创建 linked 宽限 CTS，被替换/终态时只 Cancel 不 Dispose，以父 token 注册形式滞留到 run 释放 | fixed | 代码路径对照；所有权协议改为锁内取走、取到者 Dispose | R12 PF-2 | 无单测（`TryBeginAttach`/`ReleaseAttachmentAsync`/`FinishAsync`/`Dispose` 四路所有权人工核验） | R12 | 宽限计时改为单例 Timer 或 `CancellationTokenSource` 复用机制 |
| OPT-203 | MEMORY | `DecompressBufferPool.cs:28-43`；`SystemCompressor.cs:96-121,216-272` | 解压炸弹/超限异常后，64MB 级 Brotli 缓冲与 GZip/Deflate MemoryStream 驻留 TLS，直到同一线程下一次 Rent | fixed | 池行为单测：300KB 槽 Release 后下一次 Rent 容量回落 | R12 C-17 | `CompressionGuardTests.DecompressBufferPools_Release_DropsOversizedSlotsImmediately` | R12 | 池改为 ArrayPool/可注入上限，或线程池线程不再长驻 |
| OPT-204 | CORRECTNESS | `InboxDbContext.cs:82-196` | EF InMemory provider 不支持 `ExecuteUpdateAsync`：已有行的抢占与终态保存抛 `InvalidOperationException`，行永远停留 Processing | fixed | InMemory 回归：认领后 MarkProcessed 落库；过期租约抢占 Attempts=2 | R12 H2 | `InboxEfCoreTests.InMemory_TryStartThenMarkProcessed_PersistsTerminalState`、`InMemory_ExpiredLeaseTakeover_IncrementsAttempts` | R12 | EF InMemory 支持 ExecuteUpdate，或 InMemory 不再作为受支持测试栈 |
| OPT-205 | LEAK | `InboxDbContext.cs:104-157,280-315` | 认领 `SaveChanges` 抛非 `DbUpdateException`（OCE 最常见）时 Added 实体滞留 ChangeTracker；终态 `ExecuteUpdate` 失败也留下跟踪实体，后续保存可能插入幽灵行/写回陈旧终态 | fixed | InMemory + 注入 OCE 的 SaveChanges：异常后 ChangeTracker 0 行 | R12 C-05 | `InboxEfCoreTests.InMemory_TryStartCancelledMidSave_LeavesNoTrackedGhostRow` | R12 | EF 提供事务性认领原语，跟踪器清理策略改由框架统一承担 |

R12 验证：全量 16 项目 **1887 用例，1807 通过 / 80 跳过 / 0 失败**；`gate-audit` 42/42；`AotSample` 重新发布运行通过。

## 四、R11 验证汇总

- 代码基线：`cda53b9`；修复范围：`6d9fc6a..0f2ce38`（12 提交，dev，未推送）。
- 全量测试：16 个项目 **1882 用例，1802 通过 / 80 跳过 / 0 失败**（跳过均为 PG/MySQL Testcontainers 与真 broker）。
- 门禁：`gate-audit` 42/42、`count-audit` 16 项/80 处、`doc-consistency`、`secret-scan`、`verify-conventions --quick`、`test-gate` 全绿。
- AOT：`PalDDD.AotSample` 与 `PalDDD.PalOrmSample` 均 `PublishAot` 后运行通过。
- 未覆盖：PG/MySQL 真库探针（本机无 Docker，CI `dialect-probe` 承担）；NativeAOT 下的 PalORM 唯一冲突分类（H3，下一轮）。
