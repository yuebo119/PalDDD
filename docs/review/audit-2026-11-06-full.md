# 全量功能审计（2026-11-06）

> 触发：用户令「审计当前项目所有功能实现，你觉得还需要增加什么api吗？还有哪些欠缺，缺陷，不足，全找出来」。
> 方法：6 路并行只读审计（Messaging / Saga / 三栈持久化 / CQRS+宿主 / 序列化+投影 / Core+分析器横切）+ 主代理独立复验（构建基线、CI 实况、门禁可信度、全部 P1 机制链代码实证）。
> 基线：dev @ 764efbb；本机构建 0 警告 0 错误；`perf-registry-check` PASS（38 类/154 方法/173 case/24 车道）。
> 回避清单（已裁决不做 / 已闭合，不重复提案）：MySQL 三栈租约互斥统一（ITM-794）、AGPL 双轨、RequestHash 指纹、Kafka Produce 批量回调、ZLogger 门面、Dapper.AOT 启用、ORM 优化 11 项不做清单、EF Pooling（B1 实测撤回）、2026-11-04 A/B/C/D 批已闭合项、`[Obsolete]` 6 处移除排队项（B3）。

## 结论先行

- 无 P0（无即时数据丢失/安全/契约破坏证据）。
- **P1 共 5 条**：CI 连续 3 次红未处置；RabbitMQ 批量路径 pre-ack 使批次确认契约失效；Kafka 手动存储模式毒消息冻结分区水印；TimeoutBehavior 超时后 abandoned handler 持已释放 scope 运行且异常静默吞；Mutation 门禁假绿（100% 分建立在 12/35 个变异体上）。
- **P2 共 14 条**（欠缺 7 + 不足 7；含 1 条代理报告 P1 经复验降级：DapperInboxStore 事务破口不写错数据，降为 P2；1 条代理报告 P1 被证伪半降：XML 示例合法，仅文档缺口成立）。
- **P3 共 18 条**。
- 新 API 提案 5 项（其中 2 项属方向裁决，需用户拍板）。

## 一、缺陷（P1，5 条）

| # | 发现 | 位置 | 证据与机制 |
|---|------|------|-----------|
| B1 | CI 连续 3 次红未处置：`quality-analyzers` 门禁报分析器警告 8 条（基线 0），54feb95/70d19eb/764efbb 同因；state.md 仍记「待观察」，中间无修复提交 | run 41490304673/41803634620/42058483773；本机 `dotnet run scripts/quality-baseline.cs` 复现同口径 8 条 | [事实] 归因：44f4d59 引入 RetryExecutor MA0015×2（:49-50 guard 子句）+ S1994×2（:78/:111 `for(;;)` 误报形态但破基线）；9877796 引入 MessageBatchPump RCS1075（:198 空 catch，有 #pragma+注释属有意设计但未抑制）、KafkaBroker S1144（:894 `IsSettled` 死属性，全仓零读取方）、S1066（:583 嵌套 if）、MessageBrokerBase S4136（:93 重载不相邻）。门禁真红有效，但红灯无人看 = 门禁退化为装饰 |
| B2 | RabbitMQ 批量确认契约失效：批量泵内层 handler 只入队不结算，broker 逐条核心里 handler 返回后兜底 ACK 在入队时刻即结算，批次 handler 成败与结算脱钩；批次收尾 `AcknowledgeAsync` 落已结算确认器 → 每条刷 `Duplicate acknowledge ignored` Warning；handler 显式 `RejectAsync(requeue:true)` 在批量路径是静默无操作 | RabbitMqBroker.cs:519-523（兜底 ACK）、:479（逐条确认器）；MessageBatchPump.cs:89-98、:181、:194-195；接口承诺「一个 IMessageAck 对应整批交付」MessageBroker.cs:155-171 | [事实] 机制链代码实证；[推断] 崩溃窗口内已 ack 消息丢失（与 at-least-once 通路设计意图相反）。锁定测试：BrokerAckTests.cs:507-601 只锁逐条三态，MessageAckTests 用 fake broker 不经 ReceivedAsync 兜底，测不到 |
| B3 | Kafka 手动存储模式（`EnableAutoOffsetStore=false`，本契约存在的唯一理由模式）下 handler 抛异常/反序列化 null 不结算确认器；毒消息 offset 已在 handler 前播种进 OffsetTracker → 前缀推进卡死 → 分区消费永久停摆且重启全量重放 | KafkaBroker.cs:798-809（三 catch 只记日志）、:787-791（null 分支）、:532-533（播种在前）、:981-998（前缀推进） | [事实]；[推断] 与接口文档「失败消息丢弃」（MessageBroker.cs:130-132）矛盾。锁定测试：BrokerIntegrationTests.cs:580-623 毒消息用例走二参旧重载+默认配置，断言不到 offset 推进 |
| B4 | TimeoutBehavior 超时后 abandoned handler 持已释放 scope 继续运行：HTTP 路径端点把 RequestServices 传给 SendAsync，504 响应后 ASP.NET 释放请求 scope；handler 后续触碰 scoped 依赖抛 ODE，被 ObserveFault 以 `_ = t.Exception` 静默吞掉，零日志；叠加「CQRS 请求面没有幂等键」→ 客户端 504 重试可能双写 | TimeoutBehavior.cs:70-84（:77 WaitAsync 无 linked CTS）、:107-112（ObserveFault 零日志）；Dispatcher.cs:313-314；EndpointExtensions.cs:57,97 | [事实] 机制；[推断] 发生频率。usage.md:165 只声明「原工作可能仍在后台跑完」，未声明 scope 已释放后果。锁定测试：无 |
| B5 | Mutation 门禁假绿：最近一次 success（run 41442392906，2026-11-02）两段日志 152 created→12 tested（140 被 filter 吃掉）、2310 created→35 tested（1851 filter + 422 CompileError）；`thresholds.break=95` 在个位数基数上无区分度；「执行过」成立、「拒绝过吗」不成立 | mutation.yml:34,38；test/PalDDD.Transactions.Tests/stryker-config.json（mutate 仅 MySqlLeasePolicy.cs）；日志实测 | [事实]。memory 早有「Stryker 分从未产出」记录，本轮实测更确切：非零产出但被过滤至形同虚设 |

## 二、欠缺（P2，含承诺未兑现与契约缺口，7 条）

| # | 发现 | 位置 | 证据 |
|---|------|------|------|
| G1 | `AddPalSaga` 不注册 `ISagaManager`/`DefaultSagaManager`（src 内零 DI 命中），InterruptStep 恢复链全靠手动赋值；遗漏时 `ExecuteInterruptStep` 的 `is DefaultSagaManager` 模式匹配整体跳过注册块 → Saga 挂 AwaitingHumanDecision 后无条目/无日志/无指标，仅靠中断步骤自身 Timeout 兜底或不超时 | ServiceCollectionExtensions.cs:112-147；Saga.cs:795,203-211；features.md:120 列为框架能力但 usage.md:740-770 无接线示例、samples/ 零命中 | [事实] 静默 AWD 滞留是范围内最坏运营结果 |
| G2 | features.md:122「五观察点贯穿全生命周期」不实：超时兜底补偿车道零观察者事件（补偿前无 OnCompensationStarted、终态迁移无 OnStatusChanged；全仓唯一生产发射点是 InterruptStep 挂起 Saga.cs:825） | SagaProcessor.cs:263-356；Saga.cs:1039-1040 对照 :1016 | [事实] |
| G3 | EventLog 导入导出零使用文档：usage.md 全文 grep `EventStreamJsonLines/ImportAsync/ExportAsync` 零命中；ADR-037 把它指定为合规归档路径、features.md:143 承诺「导入即重建」，接入细节（分组逐流 Append、期望版本 NoStream）无任何文档 | docs/usage.md；EventStreamJsonLines.cs:40；ADR-037:50-53 | [事实]。注：代理原报「XML 示例编译不过」经复验**证伪**——`log.ReadAllAsync(ct)` 解析到 IEventLog.cs:28 单 ct 重载，合法；仅文档缺口成立 |
| G4 | Inbox Failed 行生命周期无管理面：保留清理只含 Processed（`status = 2`），Failed（=3）行永不清理且无 reset/requeue 入口（Outbox 有 includeDead 先例）；2026-11-04 缺口分析自认「无手动重置」但 C 批只交付读侧；docs 全仓无此取舍声明 | RetentionSql.cs:106,115；IRetentionStore.cs:25-29；IInboxStore.cs:19-59；action-items-2026-11-04-api-gaps.md:194 | [事实]+[推断] handler 缺陷修复后 Failed 行只能连库裸 SQL 且无界增长 |
| G5 | CQRS 派发路径零遥测：StartCommandDispatch 全仓零调用点（ITM-627 家族），ITM-229 又删 CommandTotal/CommandDuration 死指标 → 框架最高频路径既无 Activity 也无 Counter；features.md:35,207「贯穿全部处理器」失实（README.md:67 已披露预留未接线，features.md 未同步） | PalDiagnostics.cs:46-51,165-168；Dispatcher.cs 零引用 PalMetrics/PalActivitySource | [事实] 三方一致红线现案 |
| G6 | 包/项目计数漂移 6 处：README.md:22「35 个」、README.en.md:22/:455、features.md:9「36 个项目（35 包+1 共享源码目录）」、README.md:518/README.en.md:522「36 个项目」；实测 src 37 csproj、36 可打包、1 非包（Prompts）；count-audit 门禁的「N 个」表格与「ships N packages」模式漏覆盖 | README.md:22,518；README.en.md:22,455,522；features.md:9 | [事实] 门禁模式盲区实锤 |
| G7 | features.md:207 横切能力节与 12 Activity 工厂口径对齐（二选一：接线 StartCommandDispatch/StartSagaTransition 或补披露） | features.md:207；PalDiagnostics.cs:48,61 | [事实] |

## 三、不足（P2 对称性/可观测，7 条；P3 18 条从略，详见附录 A）

| # | 发现 | 位置 | 严重度 |
|---|------|------|--------|
| S1 | 健康检查两栈分歧：Kafka 实现 IMessageBrokerHealth（故障置位），Rabbit 恒绿（类声明无该接口，HealthCheckExtensions.cs:169-176 注释自认「Kafka」） | KafkaBroker.cs:31,61-64,553-557；RabbitMqBroker.cs:41 | P2 |
| S2 | Rabbit 批量订阅有效批大小被 prefetch 默认 10 静默截断（批量泵全 unacked 至批次收尾，broker 推送上限 10 < maxBatchSize 64，Kafka 侧通道无界无此封顶）；两栈分歧且无文档披露 | RabbitMqBroker.cs:52-54,79,578；MessageBroker.cs:142-148 | P2 |
| S3 | broker 消费/发布失败路径零 PalMetrics 计数器（进程内派发有、跨进程没有），告警只能靠日志抓取 | KafkaBroker.cs:521,556,808；RabbitMqBroker.cs:559,243-246 | P2 |
| S4 | `paldd.saga.compensated` 只覆盖超时补偿车道（SagaProcessor.cs:535），重试耗尽补偿成功零计数、失败反计数（Saga.cs:524→SagaCompensation.cs:50-71,145）→ 计数器描述漂移 | PalDiagnostics.cs:267-269 | P2 |
| S5 | 在线投影路径零遥测（ProjectionProcessor 全文件无 PalActivitySource/PalMetrics，仅 3 处 Activity.Current?.AddEvent）；重建路径有而稳态路径无，与 features.md:35 宣称不对称 | ProjectionProcessor.cs 全文件 | P2 |
| S6 | EF Inbox/Idempotency 终态写在 DB 拒绝（affected==0）后仍变异入参对象（Dapper/PalORM/InMemory 均 affected>0 门控，EF 是唯一分叉；同族 OutboxDbContext 已门控化）→ 并发抢占下调用方持 Completed 快照而 DB 仍 Processing | InboxDbContext.cs:251-257,299-302,337-399；IdempotencyDbContext.cs:152-154,182-183,363-370 | P2 |
| S7 | DapperInboxStore.GetByStatusAsync 用 `_transaction` 而非 `Tx` 属性（同栈唯一例外，ambient 事务内此运维查询不挂事务） | DapperInboxStore.cs:82 对照 :52 属性与 DapperOutboxStore.cs:156 | P2（单字符修复；原报 P1 经复验降级：不写错数据，是事务语义破口） |

## 四、还需要增加的 API（5 项，3 项需方向裁决）

| # | API 形状 | 理由 | 性质 |
|---|---------|------|------|
| A1 | `IEventLog.GetStreamVersionAsync(string streamName, Ct)` 只读流版本查询 | 跨进程/跨会话向已有流追加时，调用方只能 ReadAllAsync 全量物化才能算 LastStreamVersion 传给 ExpectedStreamVersion.Exact（10k 事件流单次追加付 10k 条物化）；EventStoreDB 的 backward read/GetStreamVersion 形态此无对应 | 新外部行为假设，需 ADR + 四栈同加，**用户裁决** |
| A2 | Inbox Failed 行生命周期：`DeleteExpiredInboxAsync` 增 includeFailed 参数（对齐 Outbox includeDead 先例）或 IInboxStore 增显式 reset/requeue 成员 | G4 的闭环；方向二选一 | **用户裁决** |
| A3 | `MessagePublishContext.ExtraHeaders`（IReadOnlyDictionary<string,string?>?，默认 null 零分配）+ 两 broker 写侧追加 | 使用者想传租户/版本等业务头无入口 | 纯增量，向后兼容 |
| A4 | Saga 只读步骤表 API（GetRegisteredSteps 返回 key/DispatchKind/Timeout） | DynamicStep.Route 未知 key 零信号，拼写错误与滚动发布窗口期未注册新 key 在观测上不可区分；Steps 为 protected，框架无公开枚举入口 | 便利/可观测 |
| A5 | 发布/消费各一 Activity 工厂（低基数 tag：topic/exchange + 事件名） | broker 跳本身无 span，traceparent 头虽传播但跳不可见（F8）；投影在线路径同源（S5） | 可观测接线 |

## 五、Top 行动建议（按价值排序）

1. **先灭 CI 红灯**（B1）：修 8 条分析器警告（S1994 循环显式化或定点抑制、RCS1075 定点抑制、S4136 重载归位、S1066 合并、S1144 删 Kafka IsSettled 死属性、MA0015 核对 guard 子句形态），同提交把 quality-analyzers 转绿；流程上 CI 红未处置不推新提交。
2. **修三个 P1 行为缺陷**（B2/B3/B4）：B2 把兜底结算从四参核心中移出（便捷重载包装层结算，四参核心结算权完全归 handler 与批量泵）；B3 catch 块与 null 分支收尾补 `ack.RejectAsync(requeue:false)`（自动存储模式下无操作，旧语义零变化）；B4 最低限 ObserveFault 加 Warning 日志 + 文档补 scope 释放后果，彻底方案需 Dispatcher 持有 abandoned 任务至完成或显式控制点（裁决）。
3. **修 G1**（AddPalSaga 注册 ISagaManager + 启动期校验）：一行 DI + 一条启动校验，消灭最坏运营静默面。
4. **恢复 mutation 门禁可信度**（B5）：收窄 mutate filter 口径、排除 CompileError 形态、或改自建 mutation-replay 为唯一信号源（与既有自建 replay 路线对齐），先裁决再动。
5. 方向裁决项 A1/A2 立案（ADR + 四栈同加 / includeFailed 参数）。

## 附录 A：P3 发现（18 条，从略）

代理报告原文含全部 P3 行式证据（Interrupt 车道观察者豁免、DynamicStep 未知 key 零信号、Saga 重试耗尽零日志、UoW 非泛型错误路径三测、struct 领域事件零诊断盲区、TryFromValue null 契约零测试、RecordedEvent.Rehydrate schemaVersion 校验、EventLogReplaySource ContentType 构造期校验、JSON Lines BOM 双栈不对称、EventLogPositionReserver 分配停滞不可观测、DetachedStream ProblemDetails 形态分叉、Dispatcher.Register(instance) remarks 对 HTTP 路径失真、发布侧自定义头缺失、broker 层 Activity 缺失、features.md §4 at-most-once 表述滞后、usage.md Kafka/Rabbit 节未提 ack 重载与批量订阅、跨栈矩阵测试面结构性缺口、Outbox CorrelationId 检索入口缺口）。共 18 项。

## 附录 B：已核验无缺陷维度（防负面遗漏）

三栈接口成员覆盖 100%（六端口 × 四内置栈逐成员核对，含全部可选能力接口）；排序/上限/空批/枚举守卫/maxCount 边界四栈一致；Flush 排空、订阅句柄幂等释放、mandatory 丢路由抛异常、Seek 退避有界、double-ack 防护、ct 贯穿、NullMessageBroker 批量门控均有实现且带锁定测试；AOT 零反射逃逸（全仓 grep 仅 Prompts 提示词文档命中）；30/30 计数器 ≥1 调用点、10/12 Activity 工厂有调用点；38 条诊断 ID 齐（DiagnosticCoverageGateTests 机械锁定）；快照覆盖 31 程序集，未覆盖 6 个均为编译期资产/元包/内容包，G23 盲区低风险；Prompts 模板无过期 API 引用。
