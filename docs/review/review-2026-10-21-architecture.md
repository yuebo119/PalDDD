# Pal.DDD 架构评审报告

> 报告编号：REVIEW-2026-10-21-ARCHITECTURE
> 评审基准：commit `70887c0`（dev 分支）
> 评审方式：Serena LSP 符号级分析（get_symbols_overview / find_symbol / find_referencing_symbols / get_diagnostics_for_file）+ 静态扫描 + 全量测试基线
> 评审范围：分层与依赖方向、DDD/Clean 合规、C# 与 .NET 11 特性利用、十个质量维度
> 明确不检查：生产运行期行为、渗透测试、容量压测、许可证合规（后三项由既有门禁与专项流程覆盖）

---

## 执行摘要

工程治理达到少见的高度，架构判断整体成立；落地层存在两处会静默丢失领域事件的装配缺陷；重量来自三栈适配矩阵，与分层设计无关。

| 基线项 | 实测值 |
|---|---|
| 源码规模 | 46,800 行 / 248 个文件 / 36 个项目（33 个含 C# 代码） |
| 公共 API | 254 个公共类型 / 1,990 行快照 |
| 测试 | 1,799 个：1,721 通过、78 跳过、0 失败 |
| 注释密度 | 31%（14,942 / 46,800 行） |
| 持久化适配面 | 约 21,000 行，占 src 45% |
| 架构治理 | 35 份 ADR、103 条边界断言、12 项 pre-commit 守卫、38 条编译期诊断 |
| 性能门禁 | `--smoke` 分配预算 6/6 通过 |
| 构建 | Release 0 警告 0 错误 |

发现分级：P1 两项、P2 八项、P3 六项。P1 均集中在 EF Core Outbox 的装配前置条件，属于「文档写明了，推荐用法没有满足」的静默失败路径，优先级高于任何性能优化。

---

## 一、评审方法与基线

### 1.1 工具使用

- `get_symbols_overview` 与 `find_symbol`：抽取 `Dispatcher`、`Saga<TState>`、`StreamRunRegistry`、`IUnitOfWork`、`IPalOutboxStore`、`IInboxStore` 的成员结构与继承关系。
- `find_referencing_symbols` 与依赖图：验证适配器向内依赖、Core 零引用、无循环。
- `get_diagnostics_for_file`：对 `Saga.cs`、`StreamRunRegistry.cs` 等热点文件取 LSP 诊断，结果为零警告。
- 静态扫描补充：行数、注释比、能力接口数、运行期能力判定数、测试规模、TFM 与门禁计数。

### 1.2 范围声明

- 必须检查：分层依赖、DDD 战术模式落地、事务与一致性路径、AOT 边界、语言特性使用、十个质量维度。
- 明确不检查：真实生产流量下的行为、故障注入、渗透测试、第三方依赖许可证（由 `license-policy` 门禁独立覆盖）。
- 抽样策略：热点文件按行数前 15 名逐个读，其余按符号图与引用图抽查。静态评审无法证明运行时正确性，涉及运行期的结论均在文中标注证据来源。

---

## 二、分层与依赖方向

依赖图与 `docs/conventions.md` §8.1 的声明一致，未发现循环：

| 层 | 项目 | 引用 |
|---|---|---|
| 领域原语 | `PalDDD.Core` | 无任何 ProjectReference |
| 应用能力 | CQRS、EventLog、Projections、Idempotency、Messaging、Transactions | 指向 Core |
| 适配器 | Dapper/PalORM/EF 系、Kafka、RabbitMQ、Hosting | 指向内层能力包 |
| 元包 | Base、Extension | 只做聚合 |

`PalDDD.Core` 零依赖这一点做得彻底，`IUnitOfWork` 也放在 Core（`src/PalDDD.Core/IUnitOfWork.cs:8` 的 `PalDDD.Core.Repository` 命名空间），事务抽象没有泄漏 ORM 概念。这是 Clean Architecture 依赖倒置的正确落点。

两处可以收紧：

1. `PalDDD.Hosting.AspNetCore` 的 csproj 引用整个 `PalDDD.Transactions`，实际用途只有健康检查里的 `IPalOutboxStore`（`HealthCheckExtensions.cs:108`）。传输层为一个探针拉入整个事务包，依赖面与收益不匹配。
2. `PalDDD.Transactions` 同时装 Outbox、Inbox、Saga、Retention 与全部选项（6,827 行）。只想用 Outbox 的消费方也会解析到 Saga 的公共类型，包级 ISP 偏宽。

---

## 三、DDD 与 Clean 合规

| 构建块 | 实现 | 评价 |
|---|---|---|
| Entity / AggregateRoot | 泛型身份、Id 只读、瞬态判等自反性修复 | 正确，`Entity.cs:110-136` 的相等性契约考虑周全 |
| ValueObject | `readonly record struct` + `INumber<T>` + `IUtf8SpanFormattable` | 现代且零分配，是 .NET 8+ 值对象的范本 |
| DomainEvent | `static abstract EventName`、单链表节点、AsyncLocal 时钟 | 设计用力最深，语义代价也最大 |
| Specification | `And/Or/Not` DIM + `ParameterReplacer` 表达式组合 | 组合正确，EF 可翻译；内存求值需 `Expression.Compile` |
| Repository | 明确不提供通用 `IRepository<T>` | 理由成立，查询侧留白见下 |
| UnitOfWork | 只封装事务边界，嵌套 fail-fast（ADR-023） | 三栈一致，边界清晰 |

三个关键判断。

### 3.1 不提供通用仓储成立，查询侧留有空白

EF 的 `DbContext` 本身就是 UoW 加仓储，通用 `IRepository<T>` 要么暴露 `IQueryable`（AOT 不友好），要么退化成贫血 CRUD。这个取舍站得住。代价是应用层查询要么直接引用 `DbContext`，要么自己写 SQL；Dapper/PalORM 使用者没有跨栈的读模型抽象。Clean Architecture 的内层独立性在写侧成立，在读侧靠使用方自律。建议保留「不提供通用仓储」的结论，补一个按聚合声明查询接口的推荐模式与示例，比新增通用抽象更有效。

### 3.2 域事件到 Outbox 的桥只有 EF 一条

`DomainEvents()` 与 `ClearDomainEvents()` 的唯一生产消费点是 `OutboxDomainEventInterceptor.cs:460` 与 `:472`。Dapper/PalORM 没有 SaveChanges 钩子，框架也没有提供从聚合事件构造 `OutboxMessage` 的公开助手（`BuildOutboxMessage` 是 EF 拦截器私有方法）。三栈「平等共存」在 DDD 事件集成这一层不成立：Dapper/PalORM 用户需要自己完成目录查找、序列化、关联 ID 提取与事件清理，漏一步就是静默丢事件。建议补一个栈无关的 `DomainEventOutboxBridge`，EF 拦截器改为它的适配。

### 3.3 域事件单链表是分配优先压过语义简单的案例

`Entity.cs:43-58` 把事件对象本身当链表节点，换来零容器分配，代价写在 `Entity.cs:51-53`：事件实例不能跨聚合共享，否则 A 的链被截断；`ClearDomainEvents` 是 public，清理时机靠调用方纪律。一次聚合持久化的成本是数据库往返，链表省下的几十字节在端到端上不可见。按本仓自己的「小于 5% 默认不做」纪律，这里需要一次端到端基准证明链表值得它的语义约束；没有这次量测时，`List<DomainEvent>` 或 `ImmutableArray` 构建器的可读性与安全性更高。此项属复核项，不直接判定为缺陷。

---

## 四、事务与一致性

`IUnitOfWork` 的嵌套语义在三栈统一为 fail-fast（`src/PalDDD.Repository.EFCore/UnitOfWork.cs:44-55`、`src/PalDDD.Dapper/DapperUnitOfWork.cs:41-48`），防住了「内层 Commit 提交外层事务」的静默原子性破坏。

Dapper 的事务传导用 `ConditionalWeakTable<DbConnection, DbTransaction>`（`src/PalDDD.Dapper/DapperAmbientTransaction.cs`），绕开 AsyncLocal 不向外流动的问题，并声明了「绕过 UoW 直接 Begin 不挂接」的边界。实现合理，代价是多了一个隐式上下文。

EF 路径有一处需要立即处理，证据链完整：

1. `AddPalOutboxUnitOfWork<TContext>` 只注册服务（`ServiceCollectionExtensions.cs:46-56`），拦截器需要显式挂到 DbContext options（同文件 `:28-37` 已写明）。
2. `docs/tutorial.md:467-478` 只调用了注册方法，没有 `AddInterceptors` 或 `UseApplicationServiceProvider`。
3. 教程把 `IPalOutboxStore` 注册为独立的 `AppOutboxDbContext`，而 `UnitOfWork<TContext>.SaveChangesAsync` 只保存业务上下文（`UnitOfWork.cs:68-76`）。
4. 拦截器的 `_outboxStore.AddMessageAsync` 对 EF 实现只是 ChangeTracker 跟踪添加（`OutboxDbContext.cs:37-44`），没有 I/O；业务上下文 SaveChanges 不会保存另一个上下文里的跟踪实体。

按教程接线运行，领域事件不会进入 outbox。API 文档写明了同事务前置条件（`ServiceCollectionExtensions.cs:20-25`），教程与示例没有满足，且没有端到端测试覆盖「真实拦截器 + 真实 outbox 表」这条链路（现有测试全部用 Recording 假 store，`RepositoryEfCoreTests.cs:148-171`）。

拦截器自身声明的两个已知窗口值得记录：SaveChanges 成功而外层 Commit 失败时的重复或缺失窗口（`OutboxDomainEventInterceptor.cs:46-51`），以及多拦截器链中断后的滞留（`:53-59`）。这两个窗口目前是文档约束，不是机制约束。

---

## 五、C# 与 .NET 11 特性利用

代码已站在语言前沿，不是停留在语法糖层面的「用了新特性」：

| 能力 | 使用量 | 评价 |
|---|---|---|
| `TimeProvider` | 164 处 | 测试确定性的基石，覆盖后台处理器与存储 |
| `FrozenDictionary` | 48 处 | 读多写少场景的正确选择 |
| `System.Threading.Lock` | 11 个字段 | 已采用 .NET 9 新原语 |
| `ref struct` 枚举器 | 7 处 | 热路径零分配，`DomainEventEnumerable` 用得干净 |
| `IUtf8SpanFormattable` + `INumber<T>` | 4 处 | 值对象格式化零堆分配 |
| `[OverloadResolutionPriority]` | 2 处 | C# 13 正确解决重载歧义 |
| `SearchValues` | 7 处 | 字符集扫描的正确工具 |
| 集合表达式 | 66 处 | 已普及 |
| 主构造器 | 16 处 | 偏少，但只在能保持校验清晰处使用是合理的 |

尚未采用、值得评估的：

| 候选 | 结论 |
|---|---|
| `[LoggerMessage]` 源生成日志 | 最有价值的缺口。`IPalLogger` 只收字符串（`IPalLogger.cs:14-30`），插值在 `IsEnabled` 门控后执行，生产路径半零分配，但结构化字段全部丢失。框架以可观测性为卖点，日志却无法按字段检索，建议在适配层提供结构化入口并保持门面兼容 |
| C# 14 `field` 关键字 | 可用于带校验的属性（`MaxRetries`、`RetryDelay`、`MaxInFlightPublishes`），消除显式 backing field。收益局部，属于可读性改良 |
| C# 14 扩展成员 | 可把 `UnitOfWorkExtensions`、`SagaExtensions`、`StreamResumeExtensions` 收成 `extension` 块，主要是组织方式改良 |
| `params ReadOnlySpan<T>` | 只有新增结构化日志或格式化 API 时才需要；当前 49 个 `params` 方法不在热路径 |
| `allows ref struct` | 当前泛型 API 没有跨 span 的泛型约束需求，引入没有收益 |
| `Task.WhenEach` | 已在 Outbox 完成序窗口评估过：滑动窗口需要反复重新枚举未完成任务，Channel 加续体的方案每消息 O(1)，`Task.WhenEach` 在这里更慢。属于正确的「评估后不用」 |

需要点明一个风险：项目唯一 TFM 是 `net11.0`（`Directory.Build.props:5`），分析器项目才用 `netstandard2.0`。这换来了最新特性面，代价是排除了 .NET 10 LTS 及更早的消费方。作为内部平台合理；作为对外框架，需要多目标或明确声明只支持 .NET 11。

不建议为了「用上 C# 14」做语法迁移。本仓纪律是量测优先，`field` 与扩展成员属于纯可读性改良，应随热点重构顺带。

---

## 六、十维评审

评级口径：强 = 机制、测试、文档、实测四者齐备；良 = 机制与测试齐备，存在已识别且边界清楚的缺口；中上 = 机制存在，缺口影响上手或边界场景；中 = 缺口触及正确性或常见路径成本。评级是评审判断，不是测量值。

| 维度 | 评级 | 主要依据 | 主要缺口 |
|---|---|---|---|
| 可维护性 | 良 | 103 条架构断言、12 项 pre-commit、35 份 ADR、API 快照、计数审计；适配器隔离彻底 | 一次语义变更改 17 个文件；注释 31% 且大量引用内部轮次编号；4 个文件超 800 行 |
| 健壮性 | 良 | fencing 租约、版本守卫、at-least-once + Inbox、取消语义分层、真库探针抓过真实缺陷 | 事务原子性靠装配纪律；EF 拦截器全链路无端到端测试；broker 默认 at-most-once |
| 可读性 | 中上 | 命名一致、XML 注释密度高、教程与 ADR 齐备 | 注释里的 `v70 P3`、`三十八轮` 无外部索引；抑制声明密集；`Saga.cs` 1,032 行 |
| 可扩展性 | 良 | 11 个可选能力接口、DIM 默认实现、源生成器、分析器 | 12 处运行期 `is` 能力判定；新增能力要同步四栈；能力组合矩阵无契约测试 |
| 灵活性 | 良 | 三持久化栈、三方言、两 broker、JSON/MemoryPack、流式与断线续跑 | 单 TFM net11；AOT 安全子集有边界；`AddPalFullStack` 名不副实 |
| 简洁性 | 中 | 薄聚合根、只保留 UoW、无程序集扫描 | 三栈乘七存储的矩阵；四套 AsyncLocal 环境态；Saga DSL 面很宽 |
| 合理性 | 良 | 决策留痕、被否方案齐全、量测优先、真库探针 | 少数微优化收益可疑（域事件链表）；文档与教程漂移；零反射表述过强 |
| 兼容性 | 中上 | SemVer、快照金标、DIM 演进、三方言 | net11 RC 单目标；可选参数追加对已编译消费方是二进制破坏；MySQL/PG 的 EF provider 依赖 preview 或社区 fork |
| 可复用性 | 良 | 包边界清楚、分析器可独立引用、`PalDDD.Shared` 源码链接 | 应用层查询无跨栈抽象；域事件桥只在 EF；包内 ISP 偏宽 |
| 可测试性 | 强 | TimeProvider 164 处、线程安全 FakeTimeProvider、InMemory 全端口替身、1:1 测试比、分配断言 | 静态环境时钟需手工保存/恢复；进程级指标测试需串行；78 个外部依赖用例本地跳过 |

---

## 七、发现清单

### 7.1 P1（立即处理）

| ID | 发现 | 证据 | 后果 |
|---|---|---|---|
| F1 | 教程的 EF Outbox 接线不挂拦截器，且 outbox 使用独立上下文 | `docs/tutorial.md:467-478`；`UnitOfWork.cs:68-76`；`OutboxDbContext.cs:37-44` | 按教程实现会静默丢失领域事件 |
| F2 | 拦截器的同上下文前置条件没有编译期或启动期约束 | `ServiceCollectionExtensions.cs:20-25`、`:46-56` | 错误装配只能靠读文档发现 |

F1 展开：教程第 1 步只调用 `AddPalOutboxUnitOfWork<AppDbContext>()`，该 API 只注册服务，不修改 DbContext options；第 3 步把 `IPalOutboxStore` 指向独立的 `AppOutboxDbContext`，而 interceptors 的 `AddMessageAsync` 只在那个独立上下文里跟踪实体，业务上下文 SaveChanges 不会保存它。即使补上 `AddInterceptors`，两个上下文仍然无法共享一次事务。修复方向：教程改为同一上下文（业务上下文继承 `OutboxDbContext`），或明确声明双上下文需要显式共享连接与事务。

F2 展开：`AddPalOutboxUnitOfWork<TContext>` 的约束是 `where TContext : DbContext`，允许 `TContext` 不实现 `IPalOutboxStore`。把约束收紧为 `where TContext : DbContext, IPalOutboxStore`，可以让「同上下文」成为编译期事实，并在 DI 注册期校验 `IPalOutboxStore` 解析到同一个 scoped 实例。

### 7.2 P2（计划处理）

| ID | 发现 | 证据 | 建议 |
|---|---|---|---|
| F3 | 池化支持状态与注册文档矛盾 | `OutboxDomainEventInterceptor.cs:35-39` 对 `ServiceCollectionExtensions.cs:40-44`、`:62-65` | 按 CWT 状态模型重写注册文档，补池化并发测试 |
| F4 | 域事件到 Outbox 的桥只有 EF 实现 | `OutboxDomainEventInterceptor.cs:460-473` | 抽栈无关的 `DomainEventOutboxBridge`，三栈共用 |
| F5 | 零反射表述强于机制事实 | `Saga.cs:683-696`、`:716-725`；`ISpecification.cs:254-279` | 文档改为「默认零反射，反射路径必须标注」，门禁语义保持不变 |
| F6 | 四套 AsyncLocal 环境态各自独立 | `DomainEvent.cs:81`、`OutboxMessage.cs:32`、`SagaState.cs:61`、`SagaExecutionObserver.cs:56` | 收敛为单一 `PalAmbientContext` 作用域对象 |
| F7 | 能力接口矩阵没有跨栈契约测试 | 11 个能力接口、12 处运行期 `is` 判定 | 建参数化契约矩阵，四栈同组断言 |
| F8 | 78 个用例默认跳过，覆盖率双口径 | 测试运行日志；`docs/test-coverage-baseline.md` | 真 broker 车道纳入 CI，或写入支持矩阵；统一阈值口径 |
| F9 | 单 TFM net11 的兼容约束 | `Directory.Build.props:5` | 对外发布前评估 net10 多目标或首屏声明支持面 |
| F10 | 日志门面丢失结构化字段 | `IPalLogger.cs:14-30` | 适配层增加 `[LoggerMessage]` 支持的字段化入口 |

### 7.3 P3（观察与改良）

| ID | 发现 | 证据 | 建议 |
|---|---|---|---|
| F11 | Hosting 为一个健康检查硬引用 Transactions | `HealthCheckExtensions.cs:108` | 拆可选注册或独立健康检查包 |
| F12 | PG 包承载九个单方言特性 | `PalDDD.Dapper.PostgreSql`，2,919 行 | 明确单方言支持矩阵，避免「三方言对称」误读 |
| F13 | 注释携带内部评审轮次，缺少索引 | `Saga.cs`、`OutboxBatchProcessor.cs` 最集中 | 轮次编号换 ADR 链接或删除，保留不变量说明 |
| F14 | Entity 事件链表禁止跨聚合共享 | `Entity.cs:43-58` | 用端到端基准复核链表收益，低于阈值则简化 |
| F15 | 大文件职责集中 | `Saga.cs` 1,032 行、`DapperEventLog.cs` 1,020 行、`SagaStateDbContext.cs` 903 行、`KafkaBroker.cs` 866 行 | 按职责拆协作类或 partial，公共 API 快照保持零变化 |
| F16 | `AddPalFullStack` 不含基础设施 | `ServiceRegistration.cs:64-78` | 按既有计划在 v5.0 更名，或首屏文档消歧 |

---

## 八、改进路线

按投入产出排序，每项给出可验证完成标准。

1. 修 EF Outbox 装配链（F1、F2）。收紧泛型约束，修正教程，补 SQLite 端到端测试：聚合 RaiseEvent、SaveChanges、断言 outbox 行存在且随业务事务回滚。完成标准：该测试在旧接线红、新接线绿。
2. 同步池化文档（F3）。删除或改写 `ServiceCollectionExtensions.cs` 中与 CWT 模型冲突的禁令，补 `AddDbContextPool` 下的拦截器并发测试。完成标准：文档、代码注释、测试三处口径一致。
3. 补栈无关的域事件桥（F4）。提供 `DomainEventOutboxBridge`，EF 拦截器复用；Dapper/PalORM 给出两行接法。完成标准：一条 Dapper/PalORM 集成测试证明聚合事件落 outbox 且事件被清理。
4. 统一环境时钟（F6）。把三处时钟收敛为一个 `PalAmbientContext` 作用域对象，提供 `using var scope = PalAmbientContext.Use(clock)`。完成标准：现有时间测试不改断言即可通过，新增一条「单入口设置、三处时间一致」的测试。
5. 建四栈行为契约矩阵（F7）。选一组语义（租约、可重入、批量标记、到期过滤、幂等冲突、清理）参数化跑四栈。完成标准：覆盖全部能力接口，漏改一栈时至少一条用例红。
6. 结构化日志（F10）。保留 `IPalLogger` 门面，适配层增加字段化入口，后台处理器与存储优先迁移。完成标准：日志可按 `message_id`、`consumer`、`retry_count` 检索，分配预算不回归。
7. 注释治理（F13）。内部轮次编号替换为 ADR 链接或删除，保留「为什么」与不变量。完成标准：新增注释检查，形如 `v\d+ P\d` 且无链接的注释计数归零或列入白名单。
8. 拆分热点类（F15）。Saga 按注册、执行、子 Saga、中断、动态路由、补偿拆协作类；SagaStateDbContext 按方言拆策略；DapperEventLog 按读写拆。完成标准：公共 API 快照零变化，行为测试全绿。
9. 复核域事件链表（F14）。用聚合级微基准加一次端到端写路径 A/B 比较 `List<DomainEvent>` 与单链表，低于 5% 阈值则简化。完成标准：量测记录进 `docs/performance.md`，结论二选一落地。
10. 明确兼容承诺（F9）。评估 net10 多目标，或在 README 首屏声明 net11-only。完成标准：NuGet 元数据与 README 口径一致。

---

## 九、结论

这套代码在工程治理上属于高水位：决策有 ADR，规则有门禁，性能有预算，缺陷有探针，三方言有真库验证。C# 与 .NET 11 特性的使用基本到了「按需取用」的水平，没有被语法糖牵着走。

架构上真正要处理的是两条静默失败路径：EF Outbox 的装配前置条件没有机制约束，Dapper/PalORM 的域事件桥缺失。这两条都属于「文档说对了，但推荐的用法会丢数据」，优先级高于任何性能优化。

重量问题不必回避：持久化矩阵占 45% 代码面，换来三栈自由；作为发布型框架这是产品特性，作为自用底座就是净成本。这个判断需要产品定位来裁决，技术层面只能把变更税压下去，契约测试矩阵是最直接的杠杆。

底座是专业的，边界是诚实的，装配层的确定性还没有跟上设计层的确定性。
