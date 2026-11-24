# 缺陷挖掘报告（2026-11-24，R17）

> 依据：`docs/design/defect-mining-prompt.md`（v2 刁钻缺陷挖掘提示词）。
> 基线：HEAD `e896ecd`，分支 dev；`dotnet build` 0 警告 0 错误；`dotnet test` 2132 总 / 2020 过 / 0 败 / 112 跳（环境缺失类：方言真库与 broker）。
> 全部发现为只读挖掘产物；本轮未改任何产品代码。

---

## 1. 结论摘要

基线全绿下挖出 **9 条真缺陷**：3 条 P2（关停时序、解压内存驻留、G23 门禁缺口）、6 条 P3（告警重复、文档失实、端点错误映射、可观测落点、健康位粘滞、Rabbit raw 发布缺 span）。另证伪 3 条、re-affirmed 7 项家族、列死角 10 条。

Top3：
1. Dapper-PG 通知器关停不等在飞批处理，优雅关停时最后一批终态可能未落库（P2，R16 F2 的同族漏格）。
2. 三个原生解压器漏调 `DecompressBufferPool.Release`，解压炸弹后长驻线程持 64MB 槽（P2，R12 C-17 的同族漏格）。
3. Kafka 消费线程故障恢复后健康位永久粘滞 Unhealthy，实例被摘流量直到重启（P3，Rabbit FIX-020 同族漏格）。

---

## 2. 覆盖表

### 代码分片（16 片）

| 片 | 范围 | 状态 |
|---|---|---|
| 1 | Core + Core.SourceGen（+Base/Extension/Prompts 无手写 .cs） | 读全（子代理） |
| 2 | CQRS + Messaging | 读全（子代理） |
| 3 | Messaging.Kafka | 读全（子代理） |
| 4 | Messaging.RabbitMQ | 读全（子代理） |
| 5 | Transactions（Outbox/Inbox/Saga/Retention） | 读全（子代理） |
| 6 | Transactions.EFCore + Repository.EFCore | 读全（子代理，R16 只读拦截器桥的优先片） |
| 7 | Dapper 四项目（三方言 SQL） | 读全（子代理） |
| 8 | PalORM 四项目 | 读全（子代理） |
| 9 | Serialization + Compression（含 Native） | 读全（子代理） |
| 10 | EventLog + Projections | 读全（子代理） |
| 11 | Idempotency（+EFCore） | 读全（子代理） |
| 12 | Hosting.AspNetCore 全族 | 读全（子代理） |
| 13 | DependencyInjection + 诊断/健康/遥测 | 读全（子代理） |
| 14 | Analyzers + Analyzers.CodeFixes | 读全（子代理，R16 完全未读的优先片） |
| 15 | 跨栈矩阵专项（PD17 姊妹对称） | 主代理：`sibling-map.cs` 全量枚举，27 个多实现接口族核对 |
| 16 | 热点复验（近 90 天改动 + 历史修复点） | 主代理：`git log -120` 修复/勘正提交 40 条逐条对账 |

### 新面（6 项）

| 面 | 检查手段 | 结论 |
|---|---|---|
| 测试基建自身 | `gate-audit --inventory` + 全量 33 探针 + mutation.yml 配置审计 | 45 脚本全分类（29 OK / 16 TOOL / 0 缺口），33 探针全过；mutation-replay 为唯一权威门禁属实；G23 接线缺口（见 D7） |
| CI 工作流语义 | `continue-on-error` / `if` / `schedule` / `uses:` SHA 钉固人工核对 | 无异常；Stryker 两条 `continue-on-error: true` 与 3f0fff8 裁决一致 |
| 公共 API 快照 | 快照 216KB 抽样 + `grep Obsolete`（0 处）与 R16 F4/F5 勘正对照 | 一致；`gate.cs` G23 实跑 FAIL（见 D7） |
| 文档承诺与 ADR 自洽 | ADR-034/029 全文 + 32 个遥测计数器发射点 + 14 个 Activity 发射点 | ADR-034 `EnableDueTimeFilter` 默认值 false/true 内部矛盾属历史决策演变（R16 已识别，不重报）；ADR-029 MemoryPack "暂不覆写"失实（见 D4） |
| 生成物与构建产物 | 基线全量 rebuild（0 警告 0 错误后测），排除陈旧 DLL | 无异常 |
| 上游依赖行为 | `ilspycmd` 反编译 `PgNotificationListener`（StopCoreAsync / OnConnectionNotification） | 上游 `StopAsync` 会 `await runTask`，回调分发同步完成 → 主代理证伪一条 PalORM 侧竞态候选（见第 5 章） |

### 六视角覆盖声明

V1 同族漏格（D1/D2/D5）、V2 注释对账（D4/D6）、V3 生命周期中途失败态（片11/12/8 复核无新缺陷）、V4 死代码死配置（片1/14 全计数器核对无零消费者）、V5 契约首个违反者（SQLite 内存守卫已落地，re-affirmed）、V6 可观测三面（D6 + Dapper PG 无日志）、V7 配对 API（PalORM PG OnError 订阅配对完整，re-affirmed）、V8 上游假设（反编译 1 次，证伪 1 条）、V9 门控可信度（D7）、V10 排序分叉（R16 Dapper Saga 排序键已修，re-affirmed）、V11 异常吞掉（PeriodicBackgroundProcessor 空 catch 有声明，re-affirmed）、V12 时间源编码（Retention 三栈编码分叉有声明，re-affirmed）、V13 批/单条对等（R9 修法复核一致）、V14 状态机终态（SagaStatus 4 空洞有架构测试守护）、V15 快照边界（D7 + F4/F5 勘正状态）。

### 预扫描各类命中基线

| 类 | 命中数 | 本轮处理 |
|---|---|---|
| 1 async void | 0 | 无 |
| 2 sync-over-async | 27 | 全为文档化桥（CQRS 完成态 + DI 工厂 + 拦截器 ADR-026），无新缺陷 |
| 3 反射 | 18 | 全为注释/RequiresDynamicCode 声明/已建 Saga AOT 豁免，无滥用 |
| 4 fire-and-forget | 35 | 命中 D1（Dapper PG 游离 Task.Run）与 PalORM 已修形态 |
| 5 SQL 字面量内嵌 `--` | 0 | 无 |
| 6 batch path | 181 | 与勿重开清单逐条对账，无新缺陷 |
| 7 事件订阅配对 | 15 | PalORM PG OnError 订阅/退订成对，re-affirmed |
| 8 CTS 生命周期 | 53 | 与证伪台账对账，无新缺陷 |
| 9 死配置定义 | 346 | 抽样核对 32 计数器全有消费者，无新增 |
| 10 无界增长 | 191 | 分片逐项确认有上限/移除点 |
| 11 异步形态 | 152 | finally 内 await 为 metrics 收尾（已声明），无新缺陷 |
| 12 条件编译 | 2 | 均为 Dapper.AOT 生成物，无分支不对称 |
| 13 完成态词 | 706 | 命中 D4/D6 与 R16 F2 勘正后的 PalORM 等待路径 |

---

## 3. 缺陷清单

### D1（P2 / E3）Dapper-PG 通知器关停不等在飞批处理（V1 同族漏格）

- **位置**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlOutboxNotifier.cs:72`（`BackgroundService`）、`:197`（`_ = Task.Run(() => FireBatchProcessAsync(stoppingToken))`）、`:94`（`Dispose()` 内 `_processGate.Dispose()`）
- **触发条件**：graceful shutdown（SIGTERM）时 `stoppingToken` 取消令 `ExecuteAsync` 的 while 退出；`:197` 的 `Task.Run` 是脱离 `ExecuteAsync` 的游离任务。`BackgroundService.StopAsync` 只等 `ExecuteAsync`（微软 Learn 文档 `BackgroundService.StopAsync` 与 `ExecuteAsync` 条目实证），不等游离任务；宿主随后 Dispose 释放 `_processGate`，在飞 `FireBatchProcessAsync` 的 finally `Release()` 抛 `ObjectDisposedException` 被吞（`:310`）。
- **后果**：最后一批 Outbox 终态标记可能未落库即进程退出，租约悬挂至超时后重投（延迟投递）；ODE 被吞，日志/指标/健康三面无信号（V6 静默失效）。
- **修法骨架**：仿 PalORM 版 `PalOrmPostgreSqlOutboxNotifier`（`495ce76` 已修同缺陷）：增 `private Task? _fireTask`，`:197` 改为 `_fireTask = Task.Run(...)`；`FireBatchProcessAsync` 的 finally 清引用；重写 `StopAsync` 在停监听后 `await inFlight`（不用宿主 token），`Dispose()` 前亦等待；或整体切 `IHostedService + IAsyncDisposable`。
- **影响面**：公共 opt-in 注册 `AddPalPostgreSqlOutboxNotifier`（`PostgreSqlServiceCollectionExtensions.cs:166,187`，Singleton IHostedService）；所有启用该通知器并走优雅关停的 Dapper-PG 使用方命中。无公共 API 快照删除问题（只增字段/方法）。
- **证据等级**：E3（静态 + git 提交反证 + BCL 文档）。`git show --name-only 495ce76 | grep notif` 只返回 PalORM 两文件；Dapper 侧无 `StopAsync`/`_fireTask`。
- **前提失效核查**：该文件无「勘正/取舍/ADR-/有意为之」声明豁免关停等待；`:310` 注释只论证「release 抛 ODE 可吞」，未论证「无需等待批处理落库」。

### D2（P2 / E3）原生解压器三处漏调 `DecompressBufferPool.Release`（V1 同族漏格）

- **位置**：`src/PalDDD.Compression.Native/NativeCompressors.cs:94`（LZ4.Decompress）、`:175`（ZStandard.Decompress）、`:264`（OpenZL.Decompress），均为 `var buffer = DecompressBufferPool.Rent()` 后无对应 `Release`。
- **触发条件**：任一原生解压遇解压炸弹/损坏流，输出增长至近 `DecompressionGuard.MaxOutputBytes`（64MB，`SystemCompressor.cs:19`）后抛 `InvalidDataException`；无 `finally`/`Release`。
- **后果**：`DecompressBufferPool.Release`（R12 C-17 新增）的语义是「超 `MaxResidentBytes`（256KB）的历史峰值槽立即弃用，不必等同线程下次 Rent」。不调用即回到 R12 修前的「长驻线程持 64MB 直到下次 Rent」状态，稳态 RSS 上界被击穿。
- **修法骨架**：把 `var buffer = DecompressBufferPool.Rent()` 提到 try 外，包 `finally { DecompressBufferPool.Release(buffer); }`，镜像 `SystemCompressor.cs:97,128`（Brotli 路径已接）；三处对称。
- **影响面**：3 个 public `ICompressor`（经 `AddPalCompressionNative` 注册）；src 内零直接调用点，消费方经 `ICompressionProvider` 间接使用。无公共 API 变化。
- **证据等级**：E3（静态 + git 历史 + 现有池守卫测试 `CompressionGuardTests.DecompressBufferPools_Release_DropsOversizedSlotsImmediately`）。验证命令：`grep -c 'DecompressBufferPool.Rent' src/PalDDD.Compression.Native/NativeCompressors.cs`=3、`grep -c 'DecompressBufferPool.Release'`=0；`git show --name-only 864f90a` 只含 `SystemCompressor.cs` 与池文件，无 Native 文件。

### D3（P3 / E3）Saga 超时告警每超时步重复发射两次

- **位置**：`src/PalDDD.Transactions/Saga/SagaProcessor.cs:199`（`CheckTimeoutsAsync` 的调用方循环）与 `:273`（`CompensateTimedOutSagaAsync` 内循环）；同一 `PalLogEventIds.SagaTimedOut` 与消息文本「Saga {SagaId} timed out at state {State}, step {Step}. Compensating...」。
- **触发条件**：任一配置了 Step.Timeout 的 Saga 超时进入补偿路径；`CheckTimeoutsAsync` 先按 `timedOutSteps` 逐条告警，再把同一列表传入 `CompensateTimedOutSagaAsync` 二次告警。N 个超时步 → 2N 条告警。
- **后果**：按 EventId 过滤的告警规则计数翻倍、阈值误触发，超时面板虚高；非数据错误。
- **修法骨架**：删调用方 `:196-201` 循环，保留 `CompensateTimedOutSagaAsync` 内的一处（靠近真实补偿动作）；补一条「单步超时恰好发 1 条 SagaTimedOut」的锁定测试。
- **影响面**：所有 `AddPalSaga` + 步骤 Timeout 的消费者；直接调用方 1 处（`CheckTimeoutsAsync`）。
- **证据等级**：E3（静态 + git 痕迹 + 测试缺口）。`git show 06219fc^:...SagaProcessor.cs | grep -c 'timed out at state'`=1（抽取前仅调用方一处），HEAD=2；抽取提交 `06219fc`（ITM-709）自称「逐字迁移」实为复制。`grep -rln 'SagaTimedOut' test --include=*.cs` 无命中（零告警条数断言）。

### D4（P3 / E3）MemoryPack 直写落地后接口与 ADR/文档三处未同步（三方一致漏格）

- **位置**：`src/PalDDD.Serialization/IMessageSerializer.cs:33-38`（接口 XML doc）；`docs/decisions/029-serializer-direct-write.md:30`；`docs/performance.md:603`
- **声明原文**：接口 `:33-35`「`MemoryPackMessageSerializer` 当前**不覆写**（走 DIM 默认回退）：MemoryPack 1.21.4 的 IBufferWriter 重载仅有泛型形态，无 (writer, Type, object) 非 AOT 路径对应重载，T=object 动态序列化语义未验证——原生直写待验证后跟进（ADR-029 延后段）。」ADR `:30`、performance `:603` 同口径。
- **前提失效**：① 包版本 1.21.4→1.22.0（`Directory.Packages.props:178`）；② 提交 `a2b6434`（2026-11-23）已在 `MemoryPackMessageSerializer.cs:102` 落地真实覆写，类头 `:95-100` 与 CHANGELOG:24 均已勘正为「0.0 B/op、逐字节一致、1.22.0 存在该重载」；③ 该提交改 9 文件但未改接口 doc、ADR-029、performance.md（三处 git blame 分别为 26ae06f、旧 ADR 原文、旧报告原文）。
- **后果**：接口是自定义序列化器作者的契约源，三处「不覆写/待跟进/无重载」误导迁移决策，与 ADR-029 直写承诺及 CHANGELOG 分叉，违反三方一致红线。
- **修法骨架**：三处统一改写为「已覆写为真零拷贝（1.22.0 重载，实测 0 B/op）」，删 1.21.4/待验证措辞；仅文档，不动 API 与公共快照。`docs/review/package-api-audit-2026-10-20.md:47,110` 属带日期的历史审计快照，不在同步范围。
- **影响面**：接口消费者与自定义序列化器作者；XML doc 与 docs 不入快照，无需 Major。
- **证据等级**：E3（静态 + git 历史 + 包版本）。验证命令三条均非空：`grep -n 'MemoryPackMessageSerializer.*不覆写' src/PalDDD.Serialization/IMessageSerializer.cs`、`grep -n 'public void Serialize(object message, MessageDescriptor descriptor, System.Buffers.IBufferWriter' src/PalDDD.Serialization.MemoryPack/MemoryPackMessageSerializer.cs`、`grep -n 'Include="MemoryPack" Version' Directory.Packages.props`。

### D5（P3 / E3）两条流式端点的 bindQuery 解析异常未映射为 400（ITM-696 同族漏格）

- **位置**：`src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs:236`（`MapStreamQuery` GET）与 `src/PalDDD.Hosting.AspNetCore/AspNetCore/DetachedStreamEndpoints.cs:82`（`MapDetachedStreamQuery` GET）。
- **触发条件**：调用方 `bindQuery` 内 `Guid.Parse`/`int.Parse` 等抛 `FormatException`/`ArgumentException`/`OverflowException`（畸形 query string）。
- **后果**：同语义输入错误在 `MapQuery`（`EndpointExtensions.cs:152` 已修）返 400，在两条流式端点逃逸为 500；客户端把输入错误当服务端故障，污染错误率告警。
- **修法骨架**：两处各加 `:152` 同款 `catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)` → `ValidationProblemResponseFactory.CreateBadRequest`；或三处收口为共享 helper。
- **影响面**：两个 public 端点扩展的全部宿主；仓内 `MapStreamQuery` 调用点 20+。
- **证据等级**：E3（静态 + git + 测试缺口）。`grep -n 'bindQuery(context)'` 输出 142/152 区/236/82；仅 142 区有 `FormatException` catch（ITM-696，commit `10387e8` 只修 `MapQuery`），锁定测试 `MapQuery_BindQueryThrowsFormatException_Returns400Not500` 只覆盖一条路径。注释 `:240`「同 MapQuery v25 P3 行为族 B8：绑定异常 400」仅指 `PalValidationException`，非「已勘正取舍」。

### D6（P3 / E3）/health「故障详情只进日志」承诺无日志落点（V2 + V6）

- **位置**：`src/PalDDD.Hosting.AspNetCore/AspNetCore/HealthCheckExtensions.cs:139-140`（`WriteHealthResponseAsync`）。
- **声明原文**：FIX-019（2026-11-08）注释「故障详情只进日志」，响应体写固定文案 `"Unhealthy — details in logs"`。
- **事实**：该方法无 logger 入参、全文件零 `IPalLogger`/`.Error`/`.Warning` 调用（唯一 `IPalLogger` 命中是 :36 注释）。两个 `PalOutboxHealthCheck`（`PalOutboxHealthCheck.cs:34`）在 catch 中把 `ex` 放进 `HealthCheckResult.Unhealthy("发件箱存储不可用", ex)`，但 `ResponseWriter` 将 `Description` 与 `Exception` 全部丢弃，换成固定文案——异常对象无任何日志出口。
- **后果**：outbox 检查 Unhealthy 时，具体故障原因既不回传也不进日志，排障只见红灯；与 FIX-019「不泄露内部实现但保留日志可观测」的设计意图相反。
- **修法骨架**：`WriteHealthResponseAsync` 注入 `IPalLogger`（`EnsurePalLoggerFacade` 已就绪）遍历 Unhealthy entries 记 `e.Value.Exception`；或在两个 check 的 catch 内直接记 Error（须避免重复）。
- **影响面**：所有 `MapPalHealthChecks`/`WithConventions` 宿主；health 响应 shape 不变。
- **证据等级**：E3（静态 + 承诺/实现对账）。验证命令：`grep -n "details in logs" ...HealthCheckExtensions.cs` 非空且 `grep -nE "IPalLogger|\.Error\(|\.Warning\(" ...HealthCheckExtensions.cs` 仅 :36 注释命中。零测试覆盖（`grep -rn "details in logs\|PalHealthResponse" test/PalDDD.Hosting.AspNetCore.Tests` 无命中）。

### D7（P2 / E1）G23 门禁实跑 FAIL：快照变更与 CHANGELOG 未同提交（门禁接线缺口）

- **位置**：`scripts/gate.cs` G23 判定（逻辑正确）；违规提交 `179b533`（2026-11-09，`测试：FIX-014 PersistAsync 测试适配 + 公共 API 快照更新`）。
- **现象**：基线全绿工作树（G22 PASS）下 `dotnet run scripts/gate.cs` 输出 `FAIL PDDD-G23: 公共 API 快照变更未与 CHANGELOG.md 同提交记录（提交 179b533...）`；实测两次均一致。
- **事实**：`git show --numstat 179b533 -- .../core-packages-public-api.txt` = `1 0`（新增一行 `method System.Void ClearDomainEvents(...)`）；`git diff-tree --name-only -r 179b533 | grep -i changelog` 无命中；后续 `a2b6434`（2026-11-23）才在改快照的同时补了 CHANGELOG——正是 G23 逐提交口径设计为抓的「补记提交洗白」形态（脚本头注以 v3.1.0 窗口 `11a4f2d`→`b0feaa7` 为实证先例）。
- **后果**：公共 API 变更的「快照 + CHANGELOG + 二进制兼容评估」三真源在发布窗口内不同集；`gate.cs` 只在 CI gate job 与手动调用中执行，`hook-pre-push.cs:103` 只跑 `gate-lite.cs`（G1/G2/G3，不含 G23），故该违规随 dev 推送后 15 天才被本轮实跑发现。
- **修法骨架**：① 立即修历史违规需维护者裁决（重写/补记提交涉及历史改写，触发 P0 #5，不自主执行）；② 把 G23 的发布窗口判定接入 pre-push（或 CI 主路径已有则确认其执行与拒绝证据），避免只靠人工手动跑 gate.cs；③ 在 CHANGELOG 补记该 API 行（若裁决采用非改写路径）。
- **影响面**：发布门禁可信度；不产生运行时错误行为。
- **证据等级**：E1（运行复现且有 FAIL 状态）。`gate.cs --selftest` 33/33 通过，`gate-audit --inventory` 45 全分类、`mutation-probe` 2/2 转红——本项是「门禁自身正确但接线/历史状态有缺口」，非门禁误判。

### D8（P3 / E3）Kafka 消费健康位只置不清，故障恢复后 /health 永久 Unhealthy（V1 同族漏格）

- **位置**：`src/PalDDD.Messaging.Kafka/KafkaBroker.cs:40`（字段）、`:62`（`IsConsumingHealthy`）、`:637/789/795`（三处置位）。
- **触发条件**：消费线程或 worker 因非关停异常终止（置位）后，调用方 Dispose 旧订阅并重新 `SubscribeAsync` 恢复消费。Kafka 侧全文零 `_consumeFaultReason = null`（`grep -c` = 0）。
- **后果**：消费已恢复但 `/health` 永久 Unhealthy，实例被摘流量直到 broker 重启。同族漏格：Rabbit 的 FIX-020（`RabbitMqBroker.cs:696-700`）已实现「新订阅成功即清粘滞标志」并配恢复断言，Kafka 未同步。
- **修法骨架**：`SubscribeAsync` 消费线程启动成功后置 `_consumeFaultReason = null`，仿 Rabbit `:700`；注意 broker 级标志多订阅互相清除的取舍（Rabbit `:697-699` 已声明，须同步声明）。
- **影响面**：`IMessageBrokerHealth` 被 `HealthCheckExtensions.cs:178` 消费；所有 `AddPalHealthChecks` 的 Kafka 宿主。
- **证据等级**：E3（静态 + 同族对照）。验证命令：`grep -n "_consumeFaultReason" src/PalDDD.Messaging.Kafka/KafkaBroker.cs`（5 命中、零清除）；Rabbit 对照 `:700` 有清除。E1 需探针「触发消费线程异常→重订阅→读健康位」。

### D9（P3 / E3）Rabbit raw 发布路径 exchange 声明失败在 try 外，不计 `BrokerPublishFailed`、无 span

- **位置**：`src/PalDDD.Messaging.RabbitMQ/RabbitMqBroker.cs:308-349`（`PublishRawAsync`），`:320` 的 `EnsureExchangeAsync(exchange)` 在 try 之外；对比对象路径 `:229`（`StartBrokerPublish` span）与 `:238`（声明移入 try，FIX-011）。
- **触发条件**：broker 不可达使 exchange 声明失败，或任意 raw 发布（无 publish span）。
- **后果**：声明失败逃逸出计数与 tracing（`paldd.broker.publish_failed` 告警假阴性、链路追踪缺发布跳）；该路径是 Outbox 主发布口（`OutboxBatchProcessor.cs:258,588`），影响放大。注：`BrokerPublishFailed` 在 raw 的两个 catch 内已有计数（F2，2026-11-08），漏的是声明段与 span。
- **修法骨架**：`EnsureExchangeAsync` 移入 try；补 `using var publishActivity = StartBrokerPublish(...)` 并在两 catch 内 `SetStatus(Error)`，对齐对象路径。
- **影响面**：2 个生产调用方（Outbox 两处）。
- **证据等级**：E3（静态 + 同族对照）。`sed -n '305,352p'` 显示声明在 try 外、无 span；对象路径 `:229/:238` 两者齐备。FIX-011 计划原文仅列对象路径（`docs/review/fix-plan-2026-11-08-comprehensive-v97.md:401`）。

---

## 4. 严重度汇总

| 级别 | 条目 | 判据 |
|---|---|---|
| P0 | 无 | — |
| P1 | 无 | — |
| P2 | D1 / D2 / D7 | D1/D2 静默失效或资源上界击穿；D7 门禁接线缺口 |
| P3 | D3 / D4 / D5 / D6 / D8 / D9 | 告警/文档/错误映射/可观测一致性/健康位粘滞/发布追踪缺口，均非数据错误 |

---

## 5. 与已知项对账

### re-affirmed（本轮实测复验，引用旧结论不算）

1. **PalORM PG 通知器关停协作取消**：反编译上游 `PgNotificationListener.StopCoreAsync`（`StopAsync` 在 lock 内取 `_runTask`，`await runTask`；`OnConnectionNotification` 在 `_runTask` 上同步分发并在返回前写 `_fireTask`）→ `await _listener.StopAsync()` 构成 happens-before 屏障，R16 F2 的等待点顺序充分。**主代理证伪一条「在途回调竞态」候选**。
2. **Dapper/PalORM Saga 租约常量死配置与排序键反向**：R16 已勘正（公共 API 快照收录，只勘正不删除），本轮 grep 零新增可删项。
3. **Kafka worker 健康位**：`KafkaBroker.cs:789,795` 两 catch `??=` 置位在场（R16 F1）。
4. **共享 SQLite `:memory:` 守卫**：`SqliteServiceCollectionExtensions.cs` 启动期守卫在场（R16 F6）。
5. **streaming zero-copy / flush / 心跳等性能否决**：perf-ledger 表2/3/4 逐条对账，未重提。
6. **ADR-034 `EnableDueTimeFilter` 默认 false/true 内部矛盾**：R16 已识别，属决策演变留痕（实施附记 :57/:66 已勘正为 true，代码 = true），不重复上报。
7. **ITM-627 两个零发射 Activity**：`PalDiagnostics.cs:46-47,59-60` 有声明，re-affirmed。
8. **Kafka 健康位置位完整性**：`KafkaBroker.cs:637/789/795` 三处置位齐备（R16 F1 与 R16 门禁），本轮 D8 是**恢复后清除**漏格，非置位漏格，不重复。

### 本轮证伪（防止下轮重复）

- **PalORM PG 通知器「在途回调写 `_fireTask` 竞态」**：反编译证明上游 StopAsync 等待 runTask 完成，回调同步分发，无窗口（见上）。
- **CTS Cancel 后 Dispose 破坏协作取消**：台账已证伪，本轮未重跑。
- **原生 OOM 根因丢失**：`NativeCompressors.cs:116/197/286` 三处 `InvalidDataException(..., ex)` 保堆栈，v38 已修。
- **解压炸弹防护缺失**：`SystemCompressor.cs:18-19` 64MB 上限在案，D2 是池释放漏格不是防护缺失。
- **Dapper 唯一冲突分类器 JIT FileNotFound / MySQL 回调时序 / Rabbit dispatch 串行前提**：台账+分片反编译/静态核对，均证伪或未重开。
- **Kafka rebalance 回调线程安全**：怀疑 `OffsetTracker._partitions`（`Dictionary`）在消费线程与 rebalance 回调间并发；反编译 Confluent.Kafka XML（`SetPartitionsAssignedHandler` "Executes as a side-effect of Consumer.Consume call (on the same thread)"）确认单线程，证伪。
- **Rabbit bodyOwner 池化双重归还**：`PooledBodyWriter.Dispose` 的 `Interlocked.Exchange` 幂等门 + `PublishAsync` finally 的 `bodyWriter=null` 移交配对无缺口，证伪。

### 与 ADR 冲突

- D4 与 ADR-029 冲突：ADR 正文「MemoryPack 暂不覆写」与已落地的真实覆写 + CHANGELOG 勘正分叉；前提（1.21.4 无重载）已失效，需同提交修 ADR + 接口 doc + performance.md。

---

## 6. 未评审与不确定项

- **MessageBatchPump 组批中途取消丢批（片2/13 死角 C1）**：`MessageBatchPump.cs:164-175` 内层组批期取消时，`pendingRead` 已取消的读任务先于 delayTask 完成 WhenAny → `await pendingRead` 抛 OCE → 外层 catch `:231` 仅「正常关停」，finally `:239-273` 只排空 channel 与 pendingRead，**不结算停留在局部 batch/batchAcks 的条目**（已 MarkDeferred，broker 兜底跳过）。现有测试只覆盖「取消发生在 handler 内」与「pendingRead 已完成未被消费」。本轮未跑探针（只读约束），停 E4，未进缺陷清单。升级证据：可控竞态探针在组批期注入取消并观察 Kafka OffsetTracker 前缀冻结或 Rabbit unacked 泄漏。
- **EFCore 幂等冲突码集缺 MySQL 1022**：`IdempotencyDbContext.cs:325` 仅 1062/1586，Dapper 分类器含 1022；缺 MySQL 复合主键冲突实测证据，未升级。
- **幂等键大小写折叠跨方言**：三方言 DDL 主键未显式 collation，MySQL 默认大小写不敏感下 Key="A"/"a" 是否撞键缺实测；未升级。
- **CI 实跑**：本轮未触发 push，CI 绿状态沿用 .ai/state.md 记录（用户单独授权 push 后复核）。
- **5.0.0 tag / NuGet push**：需用户单独显式授权，本轮未涉及。

---

## 7. 死角清单

| # | 查了什么 | 缺什么证据能升级 |
|---|---|---|
| 1 | OpenZL 格式版本持久化：`NativeCompressors.cs:216-218` P2 定案要求「持久化侧同时记录格式版本」，但 payload 仅带 CompressionAlgorithm | 是否存在外部持久化层按此读取、切真 OpenZL 时旧数据能否识别；属 P定案且当前字节即 Zstd，未升级 |
| 2 | D2 运行态实证：仅静态 + 池单测 | 需原生 bomb → 同线程下次 Rent 容量回落的探针（IVT/反射读 TLS 槽）升 E1 |
| 3 | 投影心跳续租失败窗口：`ProjectionProcessor.cs:406-412` 续租失败仅 Warning 并 return | 双 worker 真并发探针判定是否存在短暂双写窗口（R13 F13 已裁决 handler 结果权威，未报） |
| 4 | Retention 后台服务静默失效：`AddPalRetention` 未调 `EnsurePalLoggerFacade`，独立 opt-in 宿主 `OnTickFailed` 可能零输出 | 运行探针证实 DI 解析到 NullPalLogger 且失败无信号（optional 参数 + 无 Provider 零输出为文档化取舍） |
| 5 | AddVersionSuffixCodeFix 对 `orders.v`（已以 .v 结尾无数字）产出 `orders.v.v1` | 可执行 code-fix 单测断言产物非畸形；当前 [推断] |
| 6 | AddProjectionContextPrefixCodeFix 对外部程序集基类投影不注册 fix | 是否存在真实外部基类投影用户面（仓库内零命中，v60 P3-8 已声明取舍） |
| 7 | StreamRunRegistry.Cancel(runId, owner) 默认 null-owner 恒 false | 是否有 internal 调用方依赖此方法（端点走 TryGetOwned 不受影响） |
| 8 | DapperEventLog / PalOrmEventLog 全量读路径 DateTime 编码一致性 | 全量读路径对照表 + 三方言探针 |
| 9 | D8 可达性：Kafka 消费线程异常终止后重 `SubscribeAsync` 是否真能产生新健康消费线程 | 一次「触发异常→重订阅→读健康位」E1 探针（本轮禁跑测试未执行） |
| 10 | Rabbit 批量聚合授权 `CanAggregateBatchAck(..., null)` 按「默认 1」放行（`RabbitMqBroker.cs:831`），但 `CreateAsync:176` 传 null 继承工厂 `ConsumerDispatchConcurrency`（反编译 RabbitMQ 7.3.0 `InternalConsumerDispatchConcurrency = ConsumerDispatchConcurrency ?? _connectionConfigConsumerDispatchConcurrency ?? 1` 坐实；工厂值可写、默认 1、本仓未显式暴露） | 若调用方自建工厂设 >1，单帧 `multiple:true` ack 可能确认未被批次处理的低 tag → 消息丢失。缺：本仓 DI 是否暴露工厂并发配置的读取点审计 + 真容器 4 并发探针。本仓默认 1，故不进缺陷清单 |

---

## 进度追踪

- 实现进度：不适用（只读挖掘，无产品代码改动）。
- 验证完成度：基线 100%（build + test + gate-audit + doc-consistency + count-audit + gate.cs 全实跑）；分片覆盖 16/16 + 6 新面；候选证据验证 12/12（9 真 3 证伪）、死角 10 条登记；Kafka/Rabbit 分片已全数纳入（D8/D9 + 死角 9/10）。
- 下一步：维护者裁决 D1/D2/D5/D8/D9 修复排期、D4 三方一致同步、D7 历史违规处置方式（不自主改写历史或外发）；无 E1 运行复现的候选按第 6/7 章探针清单升级。
