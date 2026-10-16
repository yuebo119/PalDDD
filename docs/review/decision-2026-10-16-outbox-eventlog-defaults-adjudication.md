# 决策论证：Outbox/EventLog 三项默认值语义变更请求的裁决（S1 轮 B1/B2/B3，全部不做）

> 编号：DECISION-2026-10-16-outbox-eventlog-defaults-adjudication
> 基线：commit `6a5a2e3`（性能实施轮 S0~S5 七提交之后） · 日期：2026-10-16
> 性质：默认值语义变更裁决（用户主权项；S1 轮实施完成后余留的三个开放决策点）
> 结构约束：本模板三段（消费状态/证据锚点表/开放决策点）为 V11 门禁必填段——
> `dotnet run scripts/verify-conventions.cs -- --quick` 机械校验段存在与锚点格式。

---

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-10-16 |
| 评审状态 | **已裁决**（2026-10-16）：三项经两轮论证（首轮方案论证 + 二轮重辩含红队），用户拍板「都不做」；无代码变更，本文件即裁决记录 |
| 评审记录 | 重辩关键修正：①B1 的重复窗口不止崩溃路径——LeaseDuration 默认 2 分钟，一批 100 条发布耗时超租约即触发过期重投，放大倍数最高 99（慢 broker 场景重复最痛时放大最狠）；纯计数翻倍把窗口上界交给批次时长，不可控。②B2 首轮「乱序风险与 W=1 等价」的论断被自我推翻：Outbox 失败重试乱序确与 W 无关，但 W>1 新增 librdkafka 内部重试在 idempotence 关闭时的乱序面，W=1 下不存在；且 at-least-once 容忍重复不容忍乱序，B2 与 B1 不同性。③B1 收益面：裁决时点 PalORM 未实现 IBatchMarkOutboxStore（只覆盖 Dapper/EF/InMemory 三栈，ADR-027 延后段）；**2026-10-16 晚 PalORM 栈已补齐**（until 编码三方言探针实证后实施），收益面扩为四栈——不改变 B1 裁决（重复窗口性质与默认翻面的举证责任不变），仅覆盖面重估的前提已满足 |
| 裁决记录 | B1（BatchMarkThreshold 默认 1→BatchSize）= 不做；B2（MaxInFlightPublishes 默认 1→16）= 不做；B3（EventLogPositionReserver 构造必传化）= 不做。维持现有默认（逐条标记/串行确认/可选 reserver），选项能力对需要的用户保持可用（opt-in 路径与文档已在） |

## 证据锚点表

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| B1 目标默认值现状：`BatchMarkThreshold = 1`（逐条标记），XML doc 已声明崩溃/租约过期重复窗口与 Inbox 幂等前提 | TransactionOptions.cs:139 | ☒ |
| B1 放大路径的事实基础：`LeaseDuration` 默认 2 分钟——批处理时长超租约即过期重投，已发布未标记消息被另一 worker 重发 | TransactionOptions.cs:30 | ☒ |
| B1 收益覆盖面限制：PalORM 栈未实现 `IBatchMarkOutboxStore`（自动回落逐条），默认翻转只惠及 Dapper/EF/InMemory 三栈 | TransactionOptions.cs:132 | ☒（裁决时点事实；**2026-10-16 晚已补齐**，覆盖面扩为四栈，XML doc 同提交勘正） |
| B2 目标默认值现状：`MaxInFlightPublishes = 1`（串行逐条确认），XML doc 已声明 Rabbit 帧序天然/Kafka 重试乱序须 enable.idempotence | TransactionOptions.cs:120 | ☒ |
| B2 收益量级引用源：RTT 主导场景收益为窗口量级（T3.1 量具实测 RTT20ms 档 25.65s→1.97s），doc 引 performance.md 投递吞吐段 | TransactionOptions.cs:110 | ☒ |
| B3 目标现状：`positionReserver ?? new EventLogPositionReserver()`——漏传时每 Scoped 上下文各持一个预留器，Hi/Lo chunk 缓存不跨请求共享（每次 append 多 2 次分配器往返 + 位置按 chunkSize 稀疏化） | EventLogDbContext.cs:28 | ☒ |
| B3 失效模式的自我声明：类文档明示「单例是注入前提而非默认事实」 | EventLogPositionReserver.cs:19 | ☒ |
| B3 非破坏替代已存在：`AddPalEventLogEfCore` 一行注册 Singleton，缺的是可发现性而非能力 | ServiceCollectionExtensions.cs:63 | ☒ |
| 本轮实施上下文：S0~S5 七提交（正确性两项 + EF 往返收敛 + Saga/Inbox + EventLog 三栈批量化 + SourceGen + GZip 池化），16 项目面板 1539 过 0 失败 | CHANGELOG.md:13 | ☒ |

## 开放决策点

| # | 决策点 | 状态 | 复活触发条件 |
|---|--------|------|------|
| 1 | B1 批量标记默认化（含时间界冲刷变体） | **已裁决：不做**（2026-10-16） | 同时满足：远端 DB 标记往返实测占发布延迟 >5% + 消费侧 Inbox 幂等可审计 + 采用时间界冲刷（窗口 ≤ 数秒，恒小于租约）而非纯计数界。或 PalORM 栈补齐 IBatchMarkOutboxStore 后重估覆盖面（**PalORM 已于 2026-10-16 晚补齐，覆盖面已扩为四栈；B1 重开仍须满足前三项**） |
| 2 | B2 流水线默认化 | **已裁决：不做**（2026-10-16） | 机队标准化为 RabbitMQ（帧序天然保序），或 Kafka idempotence 成为可启动期校验的强制前提；当前维持默认 1 + 文档/模板推荐值推送 |
| 3 | B3 reserver 构造必传化 | **已裁决：不做（本轮）** | v5.0 大版本窗口开放时与其它破坏性变更打包，配迁移指南；此前以模板/示例默认接线补偿可发现性 |

---

## 附：三项的论证要点存档（供复活时省重推）

- **B1**：正方 = 远程 DB 每消息 1 次标记往返降为每批 1 次（除流水线外第二大杠杆）。反方 = ①租约过期放大 99 倍且窗口上界由批次时长决定（8/10）；②裸 broker 消费者重复突发放大（7/10）；③默认值教出「批规模重复」的消费者要求（5/10）。结论：收益真实但窗口性质不可控，opt-in 保留。
- **B2**：正方 = 远程 broker 13× 量级，本地零差。反方 = ①框架无法强制 Kafka idempotence 前提，翻默认等于替用户决定（7/10）；②逆转 T3.1 刻意保留的「默认 1 逐字不变」决策，举证责任在翻方（6/10）；③13× 为假 broker 固定 RTT 测得，真实量级受 broker 确认批处理约（4/10）。结论：乱序与重复不同性（at-least-once 不背乱序），默认不翻。
- **B3**：正方 = 失效静默（每次 append 多 2 往返 + 位置稀疏化），必传是唯一编译期强制。反方 = ①破坏性 API 太早（4.0.0 刚发布，逼用户迁移两轮）（8/10）；②一行 DI 扩展已提供正确接线，缺的是可发现性（7/10）；③失效代价是性能非正确性（3/10）。结论：留大版本。
