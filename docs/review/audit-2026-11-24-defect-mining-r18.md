# 缺陷挖掘报告（2026-11-24，R18）

> 基线：`dev` 分支 `b55a3c4`（v3 提示词落盘提交），工作树干净。
> 提示词：`docs/design/defect-mining-prompt.md`（v3，2026-11-24）。
> 性质：只读挖掘。本轮未修改任何代码、测试、文档；所有验证命令均为只读或隔离临时目录。

## 1. 结论摘要

基线全绿（build 0 警告 0 错误；test 2143 通过 / 0 失败 / 112 跳）下挖出 **4 条新缺陷**：1 条 P2（Rabbit 健康位漏接 channel shutdown 事件，空闲断连后 /health 恒绿）+ 3 条 P3（PalORM PG 通知器缺非关停 OCE 分类、Dapper PG 通知器 OCE 退避用 None token、断线续跑 run 生产者异常无 Error 出口），全部 E3（源码级不变式 + git 历史 + 上游 API 文档/同族对照）。另证伪 1 条（code-fix 边界 E4 候选经探针降级，见第 5 章）、re-affirmed 12 项、列死角 16 条。

**Top3**：

1. **P2 / E3 — Rabbit 健康位漏接 channel shutdown 事件**：`_consumeFaultReason` 只在五个结算 catch 置位，channel 空闲断连（无结算动作）时永不置位，`IsConsumingHealthy` 恒 true，实例不被摘流量——静默失效。上游 RabbitMQ.Client 7.3.0 的 `AsyncEventingBasicConsumer.ShutdownAsync` 事件可主动捕获，本仓零订阅。
2. **P3 / E3 — PalORM PG 通知器缺非关停 OCE 分类**：Dapper 栈 v18 B1（`6e105ed`）已补 `catch (OperationCanceledException oce)` 记 Error，PalORM 栈复制时漏抄；非关停 OCE 逃出 async Task 成未观察异常，零日志。
3. **P3 / E3 — Dapper PG 通知器 OCE 退避用 None token**：`PostgreSqlOutboxNotifier.cs:266` 用 `CancellationToken.None`，与 `8c9e3a6` 提交说明「关停取消可从退避等待中退出」矛盾，关停期最长多耗 30s。

## 2. 覆盖表

### 2.1 代码分片（16 片）

| 片 | 范围 | 覆盖 | 说明 |
|---|---|---|---|
| 1 | Core + Core.Abstractions + Core.SourceGen | 读全（代理） | V1/V2/V4/V18，零新缺陷 |
| 2 | CQRS + Messaging（通用层） | 读全（代理） | 同上 |
| 3 | Messaging.Kafka | 读全（代理） | V7/V16/V19 + Kafka↔Rabbit 轴，3 条待验 |
| 4 | Messaging.RabbitMQ | 读全（代理）+ 主代理抽查 | 命中 P2（见缺陷 1） |
| 5 | Transactions（Outbox/Inbox/Saga/后台循环/Retention） | 读全（代理） | V3/V10/V13/V14，零新缺陷 |
| 6 | Transactions.EFCore + Repository.EFCore | 读全（代理） | 同上 |
| 7 | Dapper + 三方言 | 读全（代理）+ 主代理抽查 | Dapper↔PalORM 轴，命中 P3（缺陷 3）；MySQL 1022 死角 |
| 8 | PalORM + 三方言 | 读全（代理）+ 主代理抽查 | 命中 P3（缺陷 2） |
| 9 | Serialization + Compression.Native | 读全（代理） | System↔Native 轴，零新缺陷；OOM 不对称待验 |
| 10 | EventLog + Projections | 读全（代理） | V11/V15/V17，零新缺陷 |
| 11 | Idempotency.EFCore | 读全（代理） | 同上 |
| 12 | Hosting.AspNetCore 全族 | 读全（代理）+ 主代理抽查 | V5/V6/V8/V9/V20，命中 P3（缺陷 4） |
| 13 | DependencyInjection + 诊断/健康/遥测 | 读全（代理） | 同上 |
| 14 | Analyzers + Analyzers.CodeFixes | 读全（代理）+ 主代理探针 | code-fix 边界 E4 已证伪降级 |
| 15 | 跨栈矩阵专项 | 覆盖（代理） | 三条对称轴逐项对照 |
| 16 | 热点复验（近 90 天 + 历史修复点） | 覆盖（代理） | git log -S 修复簇枚举 |

### 2.2 六条跨片对称轴

| 轴 | 覆盖 | 结果 |
|---|---|---|
| Dapper ↔ PalORM 同名组件 | 已对照生命周期/关停/健康位/异常分类 | 命中缺陷 2（异常分类漏格）、缺陷 3（同方法内 token 不对称） |
| 对象发布 ↔ raw 发布 | Kafka/Rabbit 两栈对照 span/计数/异常分类 | 无新缺陷；Kafka 序列化失败口径为待验 C |
| System 压缩 ↔ Native 压缩 | 逐操作对照池成对/上限/异常形态 | 对称完整；OOM 形态差异有正当理由，待验不进清单 |
| Kafka ↔ Rabbit 同语义钩子 | 健康位置位/清除、批量 ack、OCE、关停时序逐项 | 命中缺陷 1（Rabbit 置位路径漏格）；待验 A/B |
| 接口/ADR/实现/CHANGELOG 四方 | 公共承诺四方对账 | 无新失实（R17 D4 已修，复核一致） |
| 同方法内重复模式 | 抽取后同一告警/事件 ID 计数 | 无双发（R17 D3 已修，复核一致） |

### 2.3 新面（§3.2）

| 面 | 覆盖 | 结果 |
|---|---|---|
| 测试基建自身 | gate-audit 全量 47 探针 | 47/47 通过；矩阵 29 项 OK，0 缺口 |
| CI 工作流 | continue-on-error / if / schedule 语义 | Stryker 两处 continue-on-error 符合 3f0fff8 裁决；gate-audit --inventory 只跑矩阵不跑探针为已知取舍（见死角） |
| 公共 API 快照 | count-audit + doc-consistency | 16 项推导 / 30 文件 / 84 处声明全一致；无新增零消费者符号 |
| 文档承诺与 ADR 自洽 | ADR 默认值 grep 对账 | ADR-034 `EnableDueTimeFilter` false/true 分叉为上轮已识别（决策演变留痕），不重复 |
| 生成物与构建产物 | 干净树全量 build + test | 0 警告 0 错误；G23 历史 FAIL 见第 5 章 |
| 上游依赖行为 | RabbitMQ.Client 7.3.0 XML 文档查证 | 坐实 `AsyncEventingBasicConsumer.ShutdownAsync` 与 `ShutdownInitiator.Application/Library` 存在（修法骨架可行） |

### 2.4 20 视角覆盖声明（V1–V20）

| 视角 | 覆盖 | 命中 |
|---|---|---|
| V1 同族修复漏格 | 是 | 缺陷 2（Dapper 修了 PalORM 漏） |
| V2 注释与行为对账 | 是 | 缺陷 3（提交说明与实现矛盾） |
| V3 生命周期中途失败态 | 是 | 无 |
| V4 死代码死配置 | 是 | 无（全部有消费点） |
| V5 契约首个违反者 | 是 | 无（框架自身遵守） |
| V6 可观测三面皆空 | 是 | 缺陷 1（Rabbit 三面空） |
| V7 配对 API 完整性 | 是 | 无（两 broker 全成对） |
| V8 上游行为假设 | 是 | 无（ilspycmd 反编译 MySqlConnector 3.0.0 证实 DapperBulkCopy 注释） |
| V9 门控可信度三问 | 是 | 无新缺陷（mutation 基数已由 3f0fff8 裁决落地） |
| V10 排序与优先级分叉 | 是 | 无（EFCore 四分支口径一致） |
| V11 异常吞掉边界 | 是 | 无（空 catch 均有声明） |
| V12 时间源编码混用 | 是 | 无（Dapper/PalORM 两片 Stopwatch/GetTimestamp 零命中） |
| V13 批/单条路径对等 | 是 | 无（R9 修法无回归） |
| V14 状态机终态空洞 | 是 | 无（SagaStatus 4 有架构测试守护） |
| V15 版本约束快照边界 | 是 | 无（MaxLineChars Obsolete 别名有声明） |
| V16 语义克隆部分同步 | 是 | 缺陷 1/2/3/4 全部属此形态 |
| V17 承诺手段缺失 | 是 | 缺陷 4（异常对象无 Error 出口） |
| V18 测试基建假绿 | 是 | code-fix 边界候选经探针证伪降级（见第 5 章） |
| V19 跨抽象层状态归属 | 是 | 缺陷 1；待验 B（跨订阅竞态） |
| V20 配置能力声明不对称 | 是 | 无（六个默认值各有消费点） |

### 2.5 预扫描 17 类命中基线

| 类 | 命中数 | 本轮处理 |
|---|---|---|
| 1 async void | 0 | 无 |
| 2 sync-over-async | 27 | 全为文档化桥，无新缺陷 |
| 3 反射 | 18 | 全为声明/豁免面，无滥用 |
| 4 fire-and-forget | 36 | 缺陷 2/3 所在通知器路径经复核；其余无新缺陷 |
| 5 SQL 字面量内嵌 `--` | 0 | 无（三方言复扫零命中） |
| 6 batch path | 181 | 与勿重开清单对账，无新缺陷 |
| 7 事件订阅配对 | 6 | **命中缺陷 1**：唯一订阅是 ReceivedAsync，ShutdownAsync 零订阅 |
| 8 CTS 生命周期 | 23 | 与证伪台账对账，无新缺陷 |
| 9 Options 定义 | 342 | 抽样核对有消费者，无新增零命中 |
| 10 无界集合 | 14 | 有上限/移除点 |
| 11 异步形态 | 1040 | finally 内 await 为 metrics 收尾（已声明） |
| 12 条件编译 | 2 | Dapper.AOT 生成物，无分支不对称 |
| 13 完成态词 | 1827 | 命中缺陷 3/4 与 R17 D4/D6 勘正后路径 |
| 14 游离 Task（v3） | 8 | 通知器 fire 任务路径已复核 |
| 15 池操作（v3） | 785 | D2 已修三器 Release 齐备 |
| 16 重复事件 ID（v3） | 264 | R17 D3 已修，无双发 |
| 17 可观测承诺（v3） | 65 | 命中缺陷 4；R17 D6 已修 |

## 3. 缺陷清单

### 缺陷 1（P2 / E3）Rabbit 健康位漏接 channel shutdown 事件，空闲断连后 /health 恒绿

1. **位置**：`src/PalDDD.Messaging.RabbitMQ/RabbitMqBroker.cs:53-56`（`_consumeFaultReason` 字段及观测面声明）、`:556`（`consumer.ReceivedAsync +=`，唯一事件订阅）、`:898-906`（`MarkChannelFaultIfClosed`）、`:803/854/872/889/1077`（五个置位调用点，全在结算/订阅 catch）。
2. **视角/判据**：V19 跨抽象层状态归属 + V16 语义克隆部分同步 + V6 可观测三面——健康状态写在结算 catch（broker 层），读在 `/health`（宿主层 `HealthCheckExtensions.cs:195`）；写者不覆盖「channel 关闭但无结算动作」路径。
3. **触发条件**：broker 服务端重启、网络分区或服务端主动关 channel，且消费空闲（无在飞交付、无 ack/nack/cancel）。此时无安全包装被调用，`MarkChannelFaultIfClosed` 永不执行；新消息不会到达已关闭 channel 的 consumer，后续也无结算触发。
4. **后果**：消费链路已不可恢复终止，但 `_consumeFaultReason` 保持 null → `IsConsumingHealthy` 恒 true → `message_broker` 健康检查恒 Healthy；实例不被摘流量、不重启，日志/指标/健康三面皆空（发布失败只发 `BrokerPublishFailed`，不置健康位）。
5. **修法骨架**：在 `SubscribeAsync` 的 consumer 上订阅 `consumer.ShutdownAsync += ...`（或 `consumerChannel.ChannelShutdownAsync`），回调内按 `ShutdownEventArgs.Initiator` 区分——非 `Application`（即非正常 Dispose）时调 `MarkChannelFaultIfClosed` 置位；在句柄 unsubscribe（`:749-757`）里 `-=` 配对退订。上游 API 已查证存在：RabbitMQ.Client 7.3.0 `AsyncEventingBasicConsumer.ShutdownAsync`（XML: "Fires on channel shutdown, both client and server initiated"）与 `ShutdownInitiator.Application/Library` 枚举。锁定测试：源码级不变式「consumer 必须订阅 ShutdownAsync 且退订成对」+ 行为测试在关闭连接后不结算直接读 `IsConsumingHealthy.IsFalse()`（现有 `Health_HealthyOnLiveBroker_UnhealthyAfterChannelFault` 名称声称 Unhealthy，但 `:795/:817` 只有 `IsTrue()`、`:796` 只有 `IsNull()`，无任何 `IsFalse()` 断言——测试名与断言不符，需补）。
6. **影响面**：`IsConsumingHealthy` 的唯一生产实现之一（另一为 Kafka）；消费方为 `PalMessageBrokerHealthCheck`（1 处），但实际影响所有配置 real broker 的部署 readiness。
7. **证据等级**：E3（源码级 + 上游 XML 文档 + 零订阅 grep + 测试缺口）。未跑真容器（只读约束），不标 E1；按 §6，静默失效本可定 P1，因 E3 降 P2。

### 缺陷 2（P3 / E3）PalORM PG 通知器缺 v18 B1 的「非关停 OCE」异常分类

1. **位置**：`src/PalDDD.PalORM.PostgreSql/PalOrmPostgreSqlOutboxNotifier.cs:157-177`，符号 `FireBatchProcessAsync`。
2. **视角/判据**：V1 同族修复漏格 + V16 语义克隆——同一 `ProcessBatchAsync` 调用的两栈 catch 分类应逐项对齐。
3. **触发条件**：`ProcessBatchAsync` 在 `_shutdownCts` 未取消时抛 OCE（内部超时转换、`Parallel.ForEachAsync` 内部取消等）。`ProcessBatchCoreAsync` 多处 catch 用 `ex is not OperationCanceledException` 过滤，OCE 会穿透上抛。
4. **后果**：PalORM 版只有 `catch (OCE) when (ct.IsCancellationRequested)` 与 `catch (Exception) when (ex is not OCE)` 两分支，OCE 被后者过滤器排除后逃出 async Task；finally 仍清 `_fireTask` 并 Release gate（无资源泄漏），但 faulted Task 正常运行期无人 await，成未观察异常且零 Error 日志；关停期即便被 `StopAsync`/`DisposeAsync` 抢到引用也被 `catch (OCE)` 当预期路径吞掉，排障误定性为正常关停。对照 Dapper 版记 Error `"PostgreSQL NOTIFY batch process cancelled without stop request (internal timeout/conversion)"`。
5. **修法骨架**：在 `:169` 之后、`:174` 之前插入 `catch (OperationCanceledException oce) => logger.Error(oce, "...cancelled without stop request (internal timeout/conversion) (PalORM stack)")`，与 Dapper 版 `:303-308` 对齐；纯日志分支增补，无公共 API 变更。锁定判据为源码级不变式：两栈 `FireBatchProcessAsync` 的 catch 分支数相等（各 3）。
6. **影响面**：仅 PalORM PG 栈通知器（1 个类）；调用方为 PG 仓储宿主。
7. **证据等级**：E3（源码级 + git 历史）。`git show 6e105ed -- src/PalDDD.Dapper.PostgreSql/PostgreSqlOutboxNotifier.cs` 坐实 v18 B1 只改 Dapper 一侧；PalORM 版引入点 `950d191/ec38c60/654f4da/8f4a0a2/495ce76` 均未触碰 OCE 分类，属复制时漏抄。行为探针受上游 listener 调度影响（R15 已证伪过一例竞态），故选源码级。
8. **声明注释核对**：PalORM 版 remarks `:27-31` 声明「与 Dapper 栈语义对齐」，指向监听载体差异（上游 listener 代管重连），不覆盖异常分类，不构成取舍豁免——反而是「注释承诺对齐、实现漏一维」。与上轮已修零重叠。

### 缺陷 3（P3 / E3）Dapper PG 通知器 OCE 退避用 None token，关停取消无法从退避退出

1. **位置**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlOutboxNotifier.cs:266`，符号 `ExecuteAsync` 的 `catch (OperationCanceledException oce)` 分支。
2. **视角/判据**：V16 同方法内两处分支部分同步 + V2 注释与行为对账——同一方法内两条重连退避路径应使用同一取消源。
3. **触发条件**：非关停 OCE 触发重连退避（最长 30s）期间收到宿主 `StopAsync`。OCE 分支 `Task.Delay(oceDelay, CancellationToken.None)` 不响应 `stoppingToken`，外层 `catch (OCE) { break; }` 因 token 永不取消而永不触发。
4. **后果**：`ExecuteAsync` 要睡满当前退避时长才检查 `while (!stoppingToken.IsCancellationRequested)` 退出，`base.StopAsync` 等待它时最长多耗 30s。宿主 ShutdownTimeout 兜底返回，非无限挂起，但关停窗口内通知器仍在做重连尝试，与提交说明宣称行为相反。
5. **修法骨架**：`:266` 的 `CancellationToken.None` 改为 `stoppingToken`，保留内层 `catch (OCE) { break; }`，使退避期取消走与普通 Exception 分支（`:280`）相同的静默退出；或抽 `DelayWithBackoffAsync` 消除复制面。
6. **影响面**：仅 Dapper PG 通知器关停路径（1 个类）；影响宿主关停时延。
7. **证据等级**：E3（源码级 + git 历史 + 提交说明原文）。`git blame -L 264,267` 定位到 `8c9e3a6`，提交说明原文「R6：NOTIFY OCE 重连纳入指数退避计数……关停取消可从退避等待中退出」与 `None` 矛盾；普通 Exception 分支 `:280` 用 `stoppingToken` 且有 `:278-279` 专门注释解释，OCE 分支无对应说明。实际影响需关停期计时探针确认，故 P3。
8. **声明注释核对**：`:278-281` 与 `8c9e3a6` 均主张退避可被停机取消，OCE 分支未兑现；类级 R17 D1 关停勘正（`:91-105/:113-120`）声明的是在飞 fire 任务等待，属不同路径，不构成豁免。

### 缺陷 4（P3 / E3）断线续跑 run 的生产者异常只在 Debug 记类型名，异常对象无 Error 出口

1. **位置**：`src/PalDDD.Hosting.AspNetCore/AspNetCore/StreamRunRegistry.cs:396-401`（`StreamRun<TItem>.FinishAsync`）；对照同文件 `:744`（`StreamToAsync` 的 `_logger.Error(ex, ...)`）与 `EndpointExtensions.cs:567`（普通流式读 catch 的 Error + WriteErrorFrame）。
2. **视角/判据**：V16 语义克隆部分同步 + V17 承诺手段缺失（关联 V6）——三处同语义「流式故障」中，attach 失败与普通读都在 catch 记 `Error(ex)`，唯独 run 的生产者异常只走 `_logger.Debug(...{failure.GetType().Name})`，异常对象（消息 + 堆栈）无任何 Error 级落点。
3. **触发条件**：经 `MapDetachedStreamQuery`/`MapStreamRunAttach` 启动的 run，其 `IStreamQueryHandler.HandleAsync` 枚举中途抛非 OCE 异常（DB 断链、反序列化失败、业务 bug）。
4. **后果**：默认 Information 级别下 `IsEnabled(Debug)` 为 false，run 根因完全不可见。`StreamFailed` 计数器只给失败次数、`span.SetStatus(Error, 类型名)` 只给类型、`/health` 不含流状态，客户端仅收固定文案 `event: error`。排障只见红灯不知原因。三面非皆空（指标与 span 有信号），故 P3 非 P1。
5. **修法骨架**：`FinishAsync` 在 `state==Failed && failure!=null` 时补 `_logger.Error(failure, $"Stream run {_runId}: Failed after {produced} item(s)")`（对齐 `:744`）；补锁定测试——注入抛异常 handler，断言 `IPalLogger` 收到含异常对象的 Error 日志（现仓库 grep 零命中）。
6. **影响面**：所有使用断线续跑（ADR-031）的 SSE 端点（3 个映射入口）；调用方为流式读客户端。
7. **证据等级**：E3（源码级同文件 + 同族对照 + 测试缺口）。`PumpAsync` 的 catch（`:311-315`）只把异常存入 `failure` 变量，唯一出口是 `FinishAsync`；全文 `_logger.Error` 仅 `:564`（detach grace timer）与 `:744`（attach stream），均非生产者异常路径。
8. **声明注释核对**：无勘正/取舍/ADR 豁免生产者异常不记 Error；与上轮 D6、死角 #4/#7、已修八条零重叠。

## 4. 严重度汇总

| 级别 | 数量 | 条目 |
|---|---|---|
| P0 | 0 | — |
| P1 | 0 | — |
| P2 | 1 | 缺陷 1（Rabbit 健康位漏接 shutdown） |
| P3 | 3 | 缺陷 2/3/4 |

## 5. 与已知项对账

### 5.1 新发现

缺陷 1–4，均为本轮首次上报，与上轮已修八条（f6764bc）、证伪台账逐项核对无重叠。

### 5.2 re-affirmed（本轮实测复验）

1. **G23 历史 FAIL**：`dotnet run scripts/gate.cs` 干净树实跑仍报 `179b533` 快照变更未与 CHANGELOG 同提交，退出码 1。用户 2026-11-24 已裁决「不改历史，仅补文档说明」（见 `.ai/state.md`）；pre-push 只跑 gate-lite（不含 G23），不阻断常规 push。re-affirm，非新缺陷。
2. **mutation 权威门禁**：`mutation.yml` 中 `mutation-probe` 为唯一权威 step，Stryker 两条降趋势观察（continue-on-error），符合 3f0fff8 裁决；gate-audit 矩阵中 mutation-probe 为 OK（接线且有自证）。
3. **PalORM PG 通知器关停协作取消**：上游 `PgNotificationListener.StopAsync` 会 await runTask 构成 happens-before 屏障（R15 反编译结论），本轮未重跑。
4. **Kafka worker 健康位置位**：`:637/789/795` 三处置位齐备（R16 F1）；R17 D8 修的是恢复后清除，非置位漏格。
5. **Saga 租约常量与排序键**：R16 已勘正（快照收录只勘正不删除），EFCore 四分支口径一致，无新增可删项。
6. **共享 SQLite `:memory:` 守卫**：启动期守卫在场（R16 F6）。
7. **ADR-034 `EnableDueTimeFilter` 默认 false/true 分叉**：决策演变留痕（实施附记已勘正为 true，代码 = true），上轮已识别，不重复。
8. **streaming 性能否决**：perf-ledger 表 2/3/4 对账，未重提。
9. **ITM-627 两个零发射 Activity**：有声明，re-affirmed。
10. **Dapper/PalORM Saga 异常分类**：R15/R16 相关修复在场。
11. **D5 三条 bindQuery 路径**：锁定测试齐备（MapQuery/MapStreamQuery/MapDetachedStreamQuery 各一 FormatException→400）。
12. **D6 health logger**：锁定测试断言异常消息内容，复核一致。

### 5.3 与 ADR 冲突

无。缺陷 1–4 均不涉及已裁决 ADR 的前提失效；ADR-031（断线续跑）的缺陷 4 是日志缺口，不触及 ADR 的 run 生命周期/缓冲/取消设计。

### 5.4 本轮证伪（防止下轮重复）

1. **code-fix `orders.v` 畸形产物候选（上轮死角 #5）**：静态链完整（analyzer 报 PDDD010 → 正则 `\.v\d+$` 不匹配 → 产出 `orders.v.v1`），主代理跑 file-based 探针复刻机制确认产物为 `orders.v.v1`。但复核发现：`orders.v.v1` 仍是合法稳定名且 `HasVersionSuffix=true`（不再报诊断），而 code-fix 注释明确声明只剥离 `.v{N}` 数字后缀——`.v`（无数字）不匹配符合声明，产物虽双后缀但语义上仍是「版本 1」且诊断消失。**不构成「让使用方写出错误代码」**，降级回死角 #5，不进缺陷清单。这是本轮唯一证伪的 E4 候选。
2. **MySQL 1022 唯一冲突码跨栈不对称**：停在 E4（缺 1022 在 INSERT 路径可达性的真库探针），不重报为缺陷。
3. **Kafka 序列化失败计数口径**：停在待验 C（缺可达性证据与取舍判定），不重报。

## 6. 未评审与不确定项

- **真容器/真 broker 未跑**：缺陷 1 的空闲断连行为、待验 A 的批量 ack 丢消息风险，均需 wslc 真容器探针（本轮只读约束禁跑）。缺陷 1 已用上游 API 文档 + 源码级证据链补足，E3 成立；但 E1 运行复现仍缺。
- **关停期计时探针未跑**：缺陷 3 的「最长多耗 30s」为源码级推导（退避上限 30s + ShutdownTimeout 兜底），未实测。
- **行为探针选择**：缺陷 2/3 用源码级不变式而非行为探针，理由是上游 listener 线程调度不可控（R15 已证伪过一例竞态），符合 v3 §6「源码级是一等证据」。
- **其余分片未读细节**：各 `*DbContext` 的 OnModelCreating 尾部配置、RetryExecutor/RetryBackoffPolicy 完整实现未逐行（不在视角判据射程内）。

## 7. 死角清单

| # | 查了什么 | 缺什么证据能升级 |
|---|---|---|
| 1 | OpenZL 格式版本持久化（P2 定案注释在场） | 外部持久化层是否按此读取、切真 OpenZL 时旧数据识别；当前字节即 Zstd，未升级 |
| 2 | 原生解压 D2 运行态实证 | 原生 bomb → 同线程下次 Rent 容量回落探针（IVT/反射读 TLS 槽）升 E1 |
| 3 | 投影心跳续租失败窗口 | 双 worker 真并发探针（R13 F13 已裁决 handler 结果权威，未报） |
| 4 | Retention 后台服务静默失效（AddPalRetention 未调 EnsurePalLoggerFacade，optional NullPalLogger） | 运行探针证明 DI 解析到 NullPalLogger 且 OnTickFailed 零输出；「无 Provider 零输出」为文档化取舍 |
| 5 | code-fix `orders.v` 双后缀（本轮探针证伪降级） | 若未来判定双后缀语义不可接受，需业务侧裁决 + 整体字符串断言测试，否则维持现状 |
| 6 | code-fix 外部基类投影不注册（仓库内零用户面，v60 P3-8 已声明取舍） | 真实外部基类投影用户面存在性 |
| 7 | StreamRunRegistry.Cancel 默认 null-owner 恒 false（端点走 TryGetOwned 不受影响） | internal 调用方依赖此方法的存在性 |
| 8 | DapperEventLog / PalOrmEventLog 全量读 DateTime 编码一致性（载体不同但均有意设计） | 三方言回读探针 |
| 9 | Kafka 消费线程异常终止后重 SubscribeAsync 是否产生新健康线程 | 一次「触发异常→重订阅→读健康位」E1 探针 |
| 10 | Rabbit 批量聚合授权默认 1 vs 工厂并发 >1（本仓无工厂并发配置读取点，默认 1） | DI 是否暴露工厂并发配置读取点审计 + 真容器 4 并发丢消息探针 |
| 11 | Rabbit 待验 A：CanAggregateBatchAck 读 broker 属性而非工厂真实并发（同 #10） | 真容器 4 并发 + 慢 handler 丢消息探针 |
| 12 | Rabbit 待验 B：Kafka/Rabbit 跨订阅「旧订阅置位污染新订阅清除」竞态 | 可控竞态探针（窗口窄，上层通常先观察故障再重建） |
| 13 | Kafka 待验 C：对象发布路径序列化失败不计 BrokerPublishFailed、无 span | 注入抛异常序列化器 + MeterListener 断言 + 取舍判定 |
| 14 | System Brotli 解压缺 OOM→InvalidDataException 转译（Native 三器有） | 64MB 上限内让 System Brotli 实际抛 OOM 的可复现载荷；若无法构造则属上游差异 |
| 15 | MySQL 1022 唯一冲突码 PalORM/EFCore 栈未覆盖（多见于 DDL 加唯一索引） | 真库 1022 INSERT 路径可达性探针 |
| 16 | CI 只跑 gate-audit --inventory（跳过探针），矩阵 PROBED 列仍静态显示 yes | nightly 全量探针接线；「拒绝过吗」已由 mutation.yml 权威门禁承担，属设计取舍 |

## 进度追踪

- 实现进度：不适用（只读挖掘，本轮零代码改动）。
- 验证完成度：基线 100%（build + 全量 test 实跑）；门禁 100%（gate-audit 矩阵 29 OK + 47 探针全过、doc-consistency/count-audit/gate.cs 实跑）；分片覆盖 16/16 + 6 对称轴 + 6 新面；20 视角全覆盖；候选证据验证 5/5（4 真缺陷 + 1 证伪），死角 16 条登记。
- 未决：缺陷 1–4 本轮仅挖掘、未修复（按只读约束）；修复与真容器探针需单独授权。

## 后续修复落地（2026-11-24，用户授权「完整修复」）

缺陷 1–4 已全部修复，四条均走红→绿→变异红→恢复绿；公共 API 快照零 diff（均 private 实现 + 注释勘正）。

| 缺陷 | 落点 | 修法 | 锁定测试（变异红） |
|---|---|---|---|
| 1（P2） | `RabbitMqBroker.cs` | 订阅 `consumer.ShutdownAsync`，`Initiator` + `linkedCts` 双保险区分正常关停；两处退订成对；三处声明注释勘正 | `SubscribeAsync_WiresShutdownEventWithInitiatorGuard` / `SubscriptionDispose_UnsubscribesShutdownEventBeforeCtsDispose`；集成测试补 `IsFalse()` 断言 |
| 2（P3） | `PalOrmPostgreSqlOutboxNotifier.cs` | 补非关停 OCE 的 Error 分支（对齐 Dapper） | `BothStacks_FireBatchProcessAsync_HaveSameCatchBranches` |
| 3（P3） | `PostgreSqlOutboxNotifier.cs` | OCE 退避 `None` → `stoppingToken` | `DapperExecuteAsync_OceBackoff_UsesStoppingToken` |
| 4（P3） | `StreamRunRegistry.cs` | `FinishAsync` Failed 态补 `_logger.Error(failure, ...)` | `FinishAsync_FailedState_LogsExceptionObjectAtErrorLevel` |

**方法学教训**：缺陷 1 的初版源码级测试用全文 `Contains`，把「注释掉的订阅」也算命中，变异态未转红（假绿）。改为逐行扫描并跳过 `//` 注释行后变异红成立。这是 v3 §6「验证验证者」纪律的又一次实证，缺陷 2/3/4 的锁定测试均沿用该形态（跳过注释行）。

**未决**：真容器行为探针（Rabbit 空闲断连、Dapper 关停计时）仍未跑，本轮按 E3 源码级证据落地；如需 E1 运行复现另需真 broker 授权。1 E4 证伪与 16 死角维持原状。
