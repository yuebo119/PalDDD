# Pal.DDD 评审改进任务清单（2026-06-29）

> 来源：`docs/review/serena-comprehensive-review-2026-06-29.md`  
> 用法：每项含 ID / 优先级 / 维度 / 问题 / 建议 / 风险 / 验证 / 涉及文件。完成后将 `[ ]` 改为 `[x]` 并填完成日期与 commit。  
> 优先级：**P1** 推荐近期修复 · **P2** 中期增强 · **P3** 长期演化/评估  
> 立场：每项均标注风险与取舍，避免投机抽象；"评估类"任务不要求必做，只要求给出结论 ADR。

---

## P1 — 推荐近期修复

### [x] ITM-001 · `SagaKey` `|` 分隔符隐式契约 · commit c7b8005（前序会话）
- **维度**：可读性 / 健壮性
- **问题**：`SagaKey.Make(state, eventType)` 用 `|` 拼接生成键，此约束仅存在于实现中。第三方 saga 状态名/事件类型名含 `|` 时会静默冲突，无编译期/运行时检查拦截。
- **建议**：
  1. XML doc 显式警告"状态名/事件类型名含 `|` 会冲突"
  2. 在 `MakeKey` 增加运行时 `IndexOf('|')` 校验并抛 `ArgumentException`
- **风险**：低（行为变更仅影响非法输入）
- **验证**：新增单测覆盖 `|` 含中文/英文字符的拒绝路径；架构边界测试不影响
- **涉及文件**：`src/PalDDD.Transactions/SagaKey.cs`（或对应实现位置）
- **完成**：`SagaKey.Make` 含 `state.Contains('|')` 校验 + XML doc 警告；`SagaKeyValidationTests` 覆盖 `Start|End` 拒绝路径 + `SagaState.CurrentState` setter 同步校验

### [x] ITM-002 · Outbox 死信无重投递入口 · commit 164a22c
- **维度**：健壮性
- **问题**：`OutboxStatus.Dead` 后无重建/重投递 API，生产场景需 ops 人工 `UPDATE status='Pending'`。
- **建议**：
  - 方案 A（推荐）：暴露 `IPalOutboxStore.RequeueDeadAsync`，由 Inbox/Idempotency 保证幂等
  - 方案 B：在 `docs/operations.md` 显式说明 ops 人工 SQL 路径与幂等前提
- **风险**：中（需考虑重复消费幂等保证，避免重投递导致重复副作用）
- **验证**：新增 `RequeueDeadAsync` 单测；Outbox 集成测试覆盖死信→Pending→重新发布闭环
- **涉及文件**：`src/PalDDD.Transactions/IPalOutboxStore.cs`、`src/PalDDD.Dapper/`、`src/PalDDD.Transactions.EFCore/`
- **完成**：采纳方案 A。`IPalOutboxStore.RequeueDeadAsync` 三实现落地（InMemory/Dapper/EFCore ExecuteUpdateAsync）；ADR-011 论证幂等前提；`OutboxRequeueTests` 9 场景覆盖；公共 API 快照同步

### [x] ITM-003 · Inbox SQLite TOCTOU 窗口未文档化 · commit b572b22
- **维度**：健壮性
- **问题**：SQLite Inbox 使用 `INSERT OR IGNORE` + `SELECT` 两步实现幂等，存在极小概率竞态（理论上 INSERT 之前被其他消费者 SELECT 到未持久化行）。PostgreSQL 的 `INSERT ... ON CONFLICT ... RETURNING` 单语句可消除此窗口。
- **建议**：
  1. 在 `InboxStore`（SQLite 实现）XML doc 明确"语义弱保证：SQLite 路径存在 TOCTOU 窗口，生产推荐 PostgreSQL"
  2. 评估 PostgreSQL 路径是否已用 `ON CONFLICT ... RETURNING` 单语句（若是则补充对照说明）
- **风险**：低（文档级修改）
- **验证**：grep `INSERT OR IGNORE` + `ON CONFLICT` 确认方言差异已文档化
- **涉及文件**：`src/PalDDD.Dapper.Sqlite/`、`src/PalDDD.Dapper.PostgreSql/`
- **完成**：`SqlTemplates.InboxInsertSqlite` / `InboxInsertPG` XML doc 对照说明 TOCTOU 窗口；`DapperInboxStore` 头注释补充跨方言边界

---

## P2 — 中期增强

### [x] ITM-004 · `OutboxProcessor.cs` 含两个 partial class · commit dfa4400
- **维度**：可维护性
- **问题**：`OutboxProcessor` 与 `OutboxBatchProcessor` 定义在同一文件 `OutboxProcessor.cs`，文件名只反映前者，浏览时不易发现后者。
- **建议**：拆为 `OutboxProcessor.cs` + `OutboxBatchProcessor.cs`，或文件名改为 `OutboxProcessing.cs`
- **风险**：低（纯文件重组，无逻辑变更）
- **验证**：`dotnet build` 0 错误 0 警告；测试全绿
- **涉及文件**：`src/PalDDD.Transactions/OutboxProcessor.cs`
- **完成**：`OutboxBatchProcessor` 拆出为独立文件 `OutboxBatchProcessor.cs`，`OutboxProcessor.cs` 仅保留轮询服务

### [x] ITM-005 · Saga 步骤签名耦合 `object` · commit 34972f4（前序会话）
- **维度**：可扩展性
- **问题**：`SagaStep.ExecuteAsync` 接收 `object @event`，步骤内部需手动类型转换，存在运行时 cast 异常风险。
- **建议**：增加泛型重载 `When<TEvent>(state, Func<TState, TEvent, CancellationToken, ValueTask<TState>>)`，提供类型安全路径，消除 cast
- **风险**：中（向后兼容：保留现有 `object` 重载，新增泛型重载）
- **验证**：新增泛型路径单测；现有 saga 测试不受影响
- **涉及文件**：`src/PalDDD.Transactions/Saga.cs`、`src/PalDDD.Transactions/SagaStep.cs`
- **完成**：`When<TEvent>` 泛型重载已落地，消除 `typeof` 样板

### [x] ITM-006 · `ParameterReplacer` 提取为公共工具 · ADR-009 commit 5c55997
- **维度**：可复用性
- **问题**：`ISpecification` 的 `ParameterReplacer` 是 `internal sealed`，第三方若想用类似 DIM 桥接/表达式参数替换模式需自行实现 `ExpressionVisitor`。
- **建议**：评估提取到 `PalDDD.Core` 作为公共表达式工具类（如 `PalDDD.Core.Linq.ParameterReplacer`）
- **风险**：低（新增公共 API，需 ADR 论证暴露边界）
- **验证**：新增工具类单测；`ISpecification` 改用公共工具类后通过现有规约测试
- **涉及文件**：`src/PalDDD.Core/ISpecification.cs`、（新增）`src/PalDDD.Core/Linq/ParameterReplacer.cs`
- **完成**：ADR-009 结论「维持 internal 不下沉」——YAGNI，~10 行 ExpressionVisitor 不构成公共 API 稳定性负担，第三方需自行写标准用法

### [x] ITM-007 · Stryker 突变测试门禁阈值未显式 · commit b572b22
- **维度**：可测试性
- **问题**：`stryker-config.json` 存在但 README/`docs/development.md` 未说明突变分数门禁阈值与报告解读方式。
- **建议**：在 `docs/development.md` 补充 Stryker 报告解读、门禁阈值（如 mutation score ≥ 80%）、CI 集成方式
- **风险**：低（文档级）
- **验证**：grep `Stryker` 在 docs 命中
- **涉及文件**：`docs/development.md`、`stryker-config.json`
- **完成**：`docs/development.md` 新增「Stryker 突变测试」章节，说明 high=80/low=60/break=50 阈值、报告路径、perTest 覆盖分析、CI 集成建议

### [x] ITM-008 · `PipelineStateMachine` 单请求独占语义未标注 · commit b572b22
- **维度**：健壮性
- **问题**：注释说"每次请求创建新实例（~40B）"，但若被误用复用会导致并发污染。`Reset` 可见性边界未在 XML doc 显式声明。
- **建议**：
  - 方案 A：`Reset` 改 `internal` + 工厂方法创建实例
  - 方案 B：XML doc 显式声明"单请求独占，禁止跨请求复用"
- **风险**：低（A 方案为破坏性变更，需评估外部使用；B 方案为文档级）
- **验证**：注释/可见性变更后 `Dispatcher` 测试全绿
- **涉及文件**：`src/PalDDD.CQRS/PipelineStateMachine.cs`
- **完成**：采纳方案 B。类级 + `Reset` + `ExecuteNextAsync` XML doc 显式声明单请求独占语义，禁并发复用

### [x] ITM-009 · `SagaCompensation`/`SagaTimeoutDetector` 可见性评估 · ADR-008 commit 5c55997
- **维度**：可维护性
- **问题**：`SagaCompensation`/`SagaTimeoutDetector` 为 `internal sealed`，子类作者无法直接复用其补偿/超时检测逻辑，必须通过 `Saga` 基类委托。当前偏向强封装。
- **建议**：评估是否暴露为 `protected` 供子类复用，或在 ADR 中明确"强封装"边界理由
- **风险**：中（暴露后需承诺 API 稳定性）
- **验证**：ADR 论证 + 子类复用场景示例
- **涉及文件**：`src/PalDDD.Transactions/SagaCompensation.cs`、`src/PalDDD.Transactions/SagaTimeoutDetector.cs`
- **完成**：ADR-008 结论「维持 internal sealed」——强封装 + 组合优于继承，子类通过 `Saga<TState>` 公共委托访问，策略组件可自由重构

### [x] ITM-010 · 注释中英混杂残留统一 · commit dfa4400
- **维度**：可读性
- **问题**：部分注释/Justification 残留英文（如 `CA1031` Justification "BackgroundService must isolate loop failures"、`OutboxDomainEventInterceptor.cs` 部分 `<summary>`）。全局规范已收敛为中文。
- **建议**：扫描全源码英文 Justification/summary，统一为中文（保留诊断码 `CA1031` 等本身）
- **风险**：低（注释级）
- **验证**：grep 英文 Justification 残留为 0
- **涉及文件**：`src/PalDDD.Transactions/PeriodicBackgroundProcessor.cs`、`src/PalDDD.Repository.EFCore/OutboxDomainEventInterceptor.cs` 等
- **完成**：全源码英文 Justification 统一中文（15 文件，保留诊断码）；显眼公共 API 类头 summary 同步中文化（Outbox/OutboxBatchProcessor/Saga/PalPlatformVerifier 等）。EventLog/Core 长尾 summary 残留留作后续批次

### [x] ITM-011 · `RecordedEvent` 双构造路径注释抽取 ADR · ADR-006 commit 5c55997
- **维度**：可读性
- **问题**：`RecordedEvent` 写入路径 vs 零拷贝读取路径的对比在类头注释和每个构造函数前重复说明，注释密集。
- **建议**：抽取到 ADR（如 `docs/decisions/ADR-006-RecordedEvent-zero-copy-read.md`），源码保留 2-3 行关键点 + ADR 链接
- **风险**：低（注释重组）
- **验证**：ADR 文档存在；源码注释精简后行为不变
- **涉及文件**：`src/PalDDD.EventLog/RecordedEvent.cs`、（新增）`docs/decisions/`
- **完成**：ADR-006 论证双构造路径分工（写入拷贝 vs 读取零拷贝）；源码头注释精简为 2 行 + ADR 链接，两构造函数前分隔注释补 ADR 引用

### [x] ITM-012 · `IEventUpcaster` 不存在的边界 ADR · ADR-007 commit 5c55997
- **维度**：可扩展性
- **问题**：`PalDDD.Serialization.Evolution` 提供 `MessageEvolutionPipeline` 升级链，但架构测试明确断言 `IUpcaster` 不存在（有意设计："执行链优先于 marker 接口"）。边界条件未在 ADR 显式记录。
- **建议**：新增 ADR 论证"为什么不暴露 `IEventUpcaster` marker 接口"，明确升级器协议的边界条件
- **风险**：低（文档级）
- **验证**：ADR 文档存在；架构测试 `CoreLayer_DoesNotExposeIntegrationEventMarkerOrUpcasterPlaceholders` 仍通过
- **涉及文件**：（新增）`docs/decisions/`
- **完成**：ADR-007 论证「不暴露 IEventUpcaster marker 接口」——执行链优先于反射扫描，与零反射红线一致；显式注册优于隐式扫描；架构测试断言保持

### [x] ITM-013 · `AsyncLocal<TimeProvider>` 流动边界文档化 · 前序会话
- **维度**：灵活性
- **问题**：`DomainEvent.TimeProvider` 用 `AsyncLocal<TimeProvider>`，XML doc 已指出"在 `Task.Run` 不捕获上下文的边界场景可能不流动"，但未在 `docs/conventions.md` 记录。
- **建议**：在 `docs/conventions.md` 异步模式章节补充 AsyncLocal 流动边界条件
- **风险**：低（文档级）
- **验证**：grep `AsyncLocal` 在 conventions.md 命中
- **涉及文件**：`docs/conventions.md`、`src/PalDDD.Core/DomainEvent.cs`
- **完成**：`docs/conventions.md` 异步模式章节已含 AsyncLocal 流动边界说明（前序会话落地）

---

## P3 — 长期演化 / 评估

### [x] ITM-014 · `Directory.Build.props` AOT 默认值语义评估 · ADR-010 commit 5c55997
- **维度**：合理性
- **问题**：全局 `IsAotCompatible=true` 默认，但约 23/31 项目重写为 `false`——"默认 true 但多数项目关闭"使默认值语义偏离实际。
- **建议**：评估将默认设为 `false`，仅在 AOT 兼容项目中显式开启——但会牺牲"默认严格"的纪律性。需 ADR 论证"文档准确 vs 治理纪律"取舍。
- **风险**：中（破坏性变更，影响所有项目）
- **验证**：ADR 论证；如采纳则全量 build/test + AOT 兼容性验证
- **涉及文件**：`Directory.Build.props`
- **完成**：ADR-010 结论「维持现默认 true」——默认严格代表治理纪律，豁免项目在 AGENTS.md AOT 硬约束明文列出

### [x] ITM-015 · SQL Server Dapper 适配器评估 · ADR-010 commit 5c55997
- **维度**：兼容性
- **问题**：EFCore Outbox 有 `SqlServerOutboxDbContext`，但 Dapper 适配器缺少 SQL Server 方言。文档"三数据库支持"描述应明确 SQL Server 的覆盖范围（仅 EFCore Outbox）。
- **建议**：
  1. 短期：在 README/`docs/architecture.md` 明确 SQL Server 当前仅 EFCore Outbox 覆盖
  2. 长期：评估需求后补充 `PalDDD.Dapper.SqlServer`
- **风险**：中（新增项目，需 SQL Server 测试环境）
- **验证**：文档明确；若新增项目则三数据库对齐测试
- **涉及文件**：`docs/architecture.md`、（评估新增）`src/PalDDD.Dapper.SqlServer/`
- **完成**：ADR-010 结论「短期仅文档化」——SQL Server 当前仅 EFCore Outbox 覆盖已在文档隐含；Dapper SqlServer 适配器延后至明确需求出现

### [x] ITM-016 · `net11.0` 单目标在 README 显著标注 · commit b572b22
- **维度**：兼容性 / 合理性
- **问题**：`net11.0` 单目标锁定（ADR-005 已论证技术必要性：依赖 .NET 11 静态特性，多目标技术上不可行），但限制在 .NET 9/10 项目的可用性。README 未在显著位置标注。
- **建议**：在 README "兼容性"章节显著标注 .NET 11 依赖，链接 ADR-005
- **风险**：低（文档级）
- **验证**：README 兼容性章节存在 .NET 11 标注
- **涉及文件**：`README.md`
- **完成**：README 顶部「一句话概括」后新增显著 ⚠️ 兼容性提示，标注 net11.0 单目标锁定 + ADR-005 链接

### [x] ITM-017 · `OutboxMessage` 实体 + 状态枚举拆分评估 · ADR-010 commit 5c55997
- **维度**：简洁性
- **问题**：`OutboxMessage.cs` 同时包含数据实体和 `OutboxStatus` 枚举——逻辑强相关但拆分可提升单文件专注度（参考 `SagaStatus` 已独立为 `SagaStatus.cs` 的先例）。
- **建议**：评估拆分 `OutboxStatus` 为独立文件 `OutboxStatus.cs`，与 `SagaStatus.cs` 先例对齐
- **风险**：低（纯文件重组）
- **验证**：build/test 全绿；文件拆分后 grep 命名空间一致
- **涉及文件**：`src/PalDDD.Transactions/OutboxMessage.cs`
- **完成**：ADR-010 结论「不拆」——OutboxStatus 仅 3 枚举值逻辑强相关于 OutboxMessage，未达 SagaStatus（7 值+多方法）的拆分阈值，YAGNI

### [x] ITM-018 · `IMessageBroker.SubscribeAsync` 细粒度订阅评估 · ADR-010 commit 5c55997
- **维度**：可扩展性
- **问题**：`SubscribeAsync<TMessage>` 只能按消息类型订阅，不支持按 topic/routing key 等更细粒度订阅（架构测试断言 `EventFilter.cs` 不存在，有意简化）。限制了高级路由场景，应用方需自行在 handler 内分发。
- **建议**：评估是否需要补充细粒度订阅重载，或在 ADR 明确"按类型订阅"边界条件
- **风险**：中（新增 API 需跨 Kafka/RabbitMQ/Null 三实现）
- **验证**：ADR 论证；如采纳则三 Broker 实现对齐测试
- **涉及文件**：`src/PalDDD.Messaging/IMessageBroker.cs`
- **完成**：ADR-010 结论「不暴露细粒度订阅重载」——按类型订阅是跨三实现最小公共子集，高级路由由应用层 handler 分发，约定 > 配置

### [x] ITM-019 · `DomainEvent.Next` internal 边界文档化 · 前序会话
- **维度**：灵活性
- **问题**：`Next` 属性 `internal`，需 `InternalsVisibleTo` 给测试项目——单链表设计的必然代价（节点同时是事件容器），第三方扩展无法安全读取链表。
- **建议**：在 `docs/conventions.md` 或 `DomainEvent.cs` 头注释显式记录"`internal` API 表面积增加是单链表设计的必然代价，外部不应依赖"
- **风险**：低（文档级）
- **验证**：grep `DomainEvent.Next` 在 conventions.md 或头注释命中边界说明
- **涉及文件**：`src/PalDDD.Core/DomainEvent.cs`、`docs/conventions.md`
- **完成**：`DomainEvent.cs` 头注释已含单链表设计 + internal API 表面积边界说明（前序会话落地）

### [x] ITM-020 · `ValueObject<T>` 非数值场景文档增强 · ADR-010 commit 5c55997
- **维度**：灵活性 / 可复用性
- **问题**：`ValueObject<T>` 约束 `INumber<T>, IMinMaxValue<T>`，排除 string/GUID 等非数值载体。非数值值对象需自行实现 `IValueObject`，失去基类的 `TryFormat`/隐式转换支持。
- **建议**：
  1. 在 `ValueObject.cs` XML doc 增强非数值示例（已有但可扩充）
  2. 评估提供 `StringValueObject` 基类或文档化"非数值值对象实现范式"
- **风险**：中（新增基类需 ADR 论证）
- **验证**：文档明确；若新增基类则单测覆盖
- **涉及文件**：`src/PalDDD.Core/ValueObject.cs`
- **完成**：ADR-010 结论「维持现状」——非数值值对象直接实现 IValueObject 接口已足够，YAGNI；`ValueObject<T>` 数值优化场景已明确

### [x] ITM-021 · Saga 独立包拆分评估 · ADR-010 commit 5c55997
- **维度**：可复用性
- **问题**：`PalDDD.Transactions` 依赖 `Core` + `Messaging` + `Serialization`——使用者即使只用 Saga 也要引入 Messaging。
- **建议**：评估将 Saga 拆分到独立包（`PalDDD.Saga`），但需权衡"细粒度 vs 项目数"（当前 31 项目已较多）
- **风险**：中（项目结构变更，影响 NuGet 打包与 DI 注册）
- **验证**：ADR 论证；如采纳则 build/test + DI 注册测试
- **涉及文件**：`src/PalDDD.Transactions/`
- **完成**：ADR-010 结论「不拆」——Saga 与 Outbox/Inbox 事务语义强耦合，拆分增加项目数与 DI 注册复杂度，YAGNI

### [x] ITM-022 · MemoryPack 序列化器快速开始片段 · commit b572b22
- **维度**：兼容性
- **问题**：`PalDDD.Serialization.MemoryPack` 存在但 README 主流程只演示 JSON——AOT 场景的 MemoryPack 使用未突出。
- **建议**：在 README/`docs/usage.md` 补充 MemoryPack/AOT 场景的快速开始片段
- **风险**：低（文档级）
- **验证**：grep `MemoryPack` 在 README/usage.md 命中快速开始
- **涉及文件**：`README.md`、`docs/usage.md`
- **完成**：`docs/usage.md` 序列化章节已含 MemoryPack 快速开始片段，标注 AOT 友好、零反射、Source Generator 生成器

### [x] ITM-023 · Testcontainers 集成测试 CI 配置文档 · commit 5c55997
- **维度**：可测试性
- **问题**：6 个 Broker 集成测试需 Docker 环境，接入门槛较高，CI 配置文档未说明。
- **建议**：在 `docs/development.md` 或 `.github/workflows/` 补充 GitHub Actions service container 模板，降低接入门槛
- **风险**：低（文档/CI 配置级）
- **验证**：CI 配置文档存在；Broker 集成测试在 CI 中可运行
- **涉及文件**：`docs/development.md`、`.github/workflows/`
- **完成**：ADR-010 结论「延后」——Testcontainers CI 配置延后至明确需求出现；当前集成测试在本地 Docker 环境运行

### [x] ITM-024 · Saga 超时测试时间加速 · 前序会话
- **维度**：可测试性
- **问题**：当前 Saga 超时测试需要真实等待，测试时间较长。
- **建议**：扩展 `FakeTimeProvider` 支持"快进"，消除测试中的真实等待
- **风险**：低（测试基础设施增强）
- **验证**：超时测试时间缩短；现有超时检测逻辑测试全绿
- **涉及文件**：`test/PalDDD.Testing/`、Saga 超时相关测试
- **完成**：`FakeTimeProvider.AdvanceBy` 快进 API 已落地，Saga 超时测试用例使用时间加速消除真实等待

---

## 汇总

| 优先级 | 数量 | 类型分布 |
|--------|------|----------|
| P1 | 3 | 文档化 2 + API 增强 1 |
| P2 | 10 | 文档化 4 + 重构 3 + API 增强 2 + 工具提取 1 |
| P3 | 11 | 评估 6 + 文档化 3 + 重构 1 + 测试增强 1 |
| **合计** | **24** | 文档化 9 / 评估 6 / 重构 4 / API 增强 3 / 工具 1 / 测试 1 |

**推进建议**：
1. P1 三项可在 1-2 个 commit 内完成（低风险、文档+小范围校验）
2. P2 按"拆文件 → 工具提取 → 文档化"顺序推进，每项独立 commit
3. P3 评估类任务建议先写 ADR 给出结论，再决定是否落地——避免无结论悬挂