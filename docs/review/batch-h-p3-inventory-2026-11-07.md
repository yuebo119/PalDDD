# 批 H · P3 卫生 18 项盘点（2026-11-07）

> 依据：`docs/review/fix-plan-2026-11-07-v4.md` §3 批 H；清单来源 `docs/review/audit-2026-11-06-full.md:70`。
> 口径：逐项标注「已闭合 / 未做」，已闭合项给源码锚点与闭合批次；未做项给理由与建议载体。
> **明确声明**：`verify-conventions --quick` 只查结构 / 链接 / TODO（V5/V8/V9/V10/V11/V12），**查不到语义失实**；本盘点不宣称它覆盖语义正确性。

## 一、已闭合（6 项）

| # | 条目 | 闭合方式 | 源码锚点 |
|---|------|---------|---------|
| ⑬ | 发布侧自定义头缺失 | 批 G A3：`MessagePublishContext.ExtraHeaders` + 两 broker `CreateHeaders` 追加（框架保留键名优先） | `src/PalDDD.Messaging/MessagePublishContext.cs:13-20`、`KafkaBroker.cs` 的 `AddExtraHeaders`、`RabbitMQ.cs` 的 `AddExtraHeaders` |
| ⑭ | broker 层 Activity 缺失 | 批 G A5：`StartBrokerPublish`/`StartBrokerConsume` 两工厂（12→14），两 broker 发布/消费入口各接一个 | `src/PalDDD.Core/PalDiagnostics.cs` 两新方法；`KafkaBroker.PublishSerializedAsync` / `HandleMessageAsync`；`RabbitMQ.cs` 两条发布路径与消费回调 |
| ⑮ | features.md §4 at-most-once 表述滞后 | 批 A + 批 D+E：`features.md:68` 补「含批次级 IMessageAck 的重载把整批结算权交给 handler」；`:213` 补批量订阅结算语义 | `docs/features.md:68`、`:213` |
| ⑯ | usage.md Kafka/Rabbit 节未提 ack 重载与批量订阅 | 批 A：新增「消费确认与批量订阅」子节（逐条 ack 三态 + 批量整批结算 + prefetch 截断披露） | `docs/usage.md` 的「### 消费确认与批量订阅」 |
| ⑰ | 跨栈矩阵测试面结构性缺口（部分） | 批 G A1：`GetStreamVersionAsync` 四栈矩阵各一用例（InMemory/EF Core/Dapper/PalORM 的 SQLite 格）；其余方言格由 CI `dialect-probe` 承担 | `EventLogTests.GetStreamVersionAsync_*`、`EventLogEfCoreTests.GetStreamVersionAsync_*`、`DapperStoreTests.EventLog_GetStreamVersionAsync_*`、`PalOrmEventLogMultiDialectTests.EventLog_Sqlite_GetStreamVersionAsync_*` |
| ③ | Saga 重试耗尽零日志 | 批 D+E（S4）：`SagaCompensated` 补到 `SagaCompensation` 成功路径——重试耗尽车道的补偿结果从「零信号」变为可计数（计数器可观测替代日志） | `src/PalDDD.Transactions/Saga/SagaCompensation.cs` 末尾 `PalMetrics.SagaCompensated.Add(1)` |

## 二、未做（12 项，附理由）

| # | 条目 | 为什么本轮不做 | 建议载体 |
|---|------|---------------|---------|
| ① | Interrupt 车道观察者豁免 | 需先核实「豁免」具体指哪个观察点——`Saga.cs:825` 已有 `OnStatusChanged`（Interrupt 唯一生产发射点），其余观察点是否缺隔离未实证；G2 补的是**超时兜底补偿车道**，不是 Interrupt 车道，不能算闭合 | 只读实核 + 决策文档 |
| ② | DynamicStep 未知 key 零信号 | 需要新增诊断（诊断 ID 会触发 `count-audit` 的 diag 计数联动 + `DiagnosticCoverageGateTests` 注册），属「新诊断」而非纯卫生 | 独立 ITM + ADR（诊断分层见 ADR-022） |
| ④ | UoW 非泛型错误路径三测 | 属测试补强，需先读 `IUnitOfWork` 非泛型桥接的三条错误路径（回滚/嵌套/取消），单独一批 | `code-testing` 流程 |
| ⑤ | struct 领域事件零诊断盲区 | 需要判断「struct 事件是否应进诊断」的语义裁决（可能属刻意设计），先落决策文档 | `docs/decisions/` |
| ⑥ | TryFromValue null 契约零测试 | 单点补测，成本低但需先确认契约（null → 异常还是默认值） | `code-testing` |
| ⑦ | RecordedEvent.Rehydrate schemaVersion 校验 | 改的是水合校验行为（可能拒绝历史数据），属行为变更而非卫生 | ADR + 迁移说明 |
| ⑧ | EventLogReplaySource ContentType 构造期校验 | 同上，构造期 fail-fast 会改变既有调用方行为 | ADR |
| ⑨ | JSON Lines BOM 双栈不对称 | 需先实测两栈 BOM 行为再定修法（可能一侧是刻意容忍） | 探针 + 决策文档 |
| ⑩ | EventLogPositionReserver 分配停滞不可观测 | 需要新增计数器/事件（触发 count-audit 联动），且「停滞」判据需设计 | 独立性能/可观测项 |
| ⑪ | DetachedStream ProblemDetails 形态分叉 | 改 HTTP 错误响应形态，属对外契约变更 | ADR |
| ⑫ | Dispatcher.Register(instance) remarks 对 HTTP 路径失真 | 纯 remarks 修正——**本轮未做**：需先确认 remarks 原文与 HTTP 路径实际行为的精确差异，避免「改注释掩盖实现」（项目已知陷阱 M3-3） | 下一轮（先读 remarks + `Dispatcher.ExecutePipelineAsync` 三分支） |
| ⑱ | Outbox CorrelationId 检索入口缺口 | 新增公共 API（`IPalOutboxStore` 增检索成员），触发快照 + 四栈矩阵 | ADR + 独立批次 |

## 三、关于「删 v2 自拟 G8 编号」

审计附录 A 已无 G8（2026-11-06 修复轮已把 G8 映射回 P3 卫生）。v2 文档里的 G8 是**历史记录**（记录当时如何处置自拟编号），按「历史审计记录是时点产物」的既有口径不改写。本轮不再使用 G8 编号，视为已满足。

## 四、批 H 两条验收项的处置（2026-11-07 复核后判定：不做，理由如下）

v4 批 H 的验收列了四条，本盘点实际完成两条（逐项分类、口径声明），另两条的处置：

| 未做项 | 判定 | 理由 |
|--------|------|------|
| 行为类声明（at-most-once / scope 释放 / ack 语义）纳入 `doc-consistency` / `count-audit` 同族的「声明↔源码」机械对账 | **不做** | 这三类都是**语义声明**，不是数字或路径。可机械化的只有「关键词存在性」（如 remarks 是否含 `at-most-once`、`MessageBatchPump` 类是否存在），而关键词对语义漂移是**盲的**：把「handler 未结算时整批确认」改成「逐条确认」不会让任何关键词消失。按项目「验证验证者」纪律，门禁必须能拒绝已知坏输入；存在性检查做不到，做成门禁只会给假安全感。已有覆盖：数字类声明由 `count-audit` 管，路径类由 `verify-conventions` V9/V10 管 |
| `.cs` XML remarks 失真补「读取 remarks 文本对照行为」的锁定测试 | **不做（同上）** | 同上：remarks 与行为的对应关系无法从文本机械推导。替代载体：① 批 A/C/D/G 的**行为锁定测试**（`BatchSubscription_*` / `Health_*` / `ConsumeFailedCounter_*` / `ExtraHeaders_*`）已把关键语义钉在行为上；② 评审轮的四列可证伪判据（原文断言 → 改后断言 → 源码锚点 → 校验人）；③ `SourceCodeGuardTests` 已承担可机械化的部分（反射/async void/阻塞调用等形态） |

> 结论：这两项属「无法机械检查的判断类」，按全局规则留在文档与评审层，不建弱门禁。这是**有据的不做**，不是遗漏。

## 五、机械对账（本轮可跑的）

| 检查 | 结果 |
|------|------|
| `dotnet build PalDDD.slnx` | 0 警告 0 错误 |
| `dotnet run scripts/encoding-gate.cs` | E1–E5 全 PASS |
| `dotnet run scripts/count-audit.cs` | 16 项推导 · 84 处声明全一致 |
| `dotnet run scripts/gate-audit.cs -- --inventory` | 45 脚本（28 OK / 17 TOOL / 0 REVIEW） |
| `dotnet run scripts/verify-ai.cs` | V1–V30 全 PASS |

> **不宣称**：以上门禁不检查语义正确性；⑮⑯ 的文档修正是否与代码行为一致，由批 A/G 的锁定测试（`BatchSubscription_*`、`ExtraHeaders_*`）承载，不由 `--quick` 承载。
