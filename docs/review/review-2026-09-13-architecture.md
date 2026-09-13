# Pal.DDD 综合评审报告 — 十维度 × DDD/Clean Architecture 全景

> 评审编号：REVIEW-2026-09-13-ARCH（原 `REVIEW-COMPREHENSIVE-2026-09-13`；2026-09-13 文件名由 `comprehensive-review-2026-09-13.md` 改为 `review-2026-09-13-architecture.md`，编号同步更名——原名含主观褒义词 `comprehensive` 且类型词不在最前，违 NAMING.md 规则 5 与规则 1）
> 评审方法：五轮全仓循环（v81-v87）实证数据 + MIG-011/012 脚本化全量审计 + 本轮架构级深读
> 评审范围：36 src 项目 / 213 文件 / 32,300 行 | 17 test 项目 / 113 文件 / 33,015 行 | 28 C# 工具 | 58 文档 | .ai 质量系统
> 基线：`dcd2f57`（dev，含 ITM-671 hooks）

---

## 总评分卡

| 维度 | 评级 | 核心证据 |
|------|:----:|---------|
| 可维护性 | **A-** | ADR 22 份驱动 + 62+ 机械守卫 + 27 C# 工具；扣分：Saga 1002 行、Dapper/PalORM/EFCore 三栈同构维护 |
| 健壮性 | **A** | 全栈 fail-fast + OCE 三形态合规 + 参数化 SQL 零注入 + 62+ 守卫测试全带红测；扣分：少数窄窗口（EventLog 重查 OCE） |
| 可读性 | **A-** | Emoji 语义头标 + 中文设计注释 + XML doc 全覆盖 + 文件命名=主类型；扣分：多轮勘正注释堆叠 |
| 可扩展性 | **A-** | Clean Architecture 分层 + 适配器模式 + DIM 零反射 + 源生成器 + ADR 流程；扣分：Boundary 硬编码清单 |
| 灵活性 | **B+** | 三持久化栈可选 + Broker 抽象 + TimeProvider 注入 + Policy 模式；扣分：Dapper 栈与 Transactions 接口紧耦合 |
| 简洁性 | **B+** | AggregateRoot 薄基类 + DIM 消除反射复杂度 + 明确"不做"清单 + ValueObject 语言特性利用；扣分：PipelineStateMachine ref struct 复杂度 |
| 合理性 | **A** | AOT 三态分层理性 + 质量系统证据驱动 + "不做"清单明确 + 收敛轨迹数据支撑；扣分：三持久化栈维护负担 |
| 兼容性 | **B** | wire format .v1 版本化 + 多方言覆盖 + SourceGen 向后兼容；扣分：.NET 11 单 TFM + SqlServer Obsolete 无时间线 |
| 可复用性 | **B+** | Base/Extension 元包 + DIM 桥接 + 源生成器可移植 + StableNameValidation 共享；扣分：IPalLogger/IUnitOfWork 框架耦合 |
| 可测试性 | **A** | 1365 用例 + TimeProvider 注入 + InMemory 实现 + 守卫红测 57 样本 + S3 反向验证纪律；扣分：守卫分散 3 项目 + 真实时钟残留 |

**综合评级：A-**（S 级不需要完美——A- 反映的是"接近最优但仍有已知的设计取舍代价"）

---

## 第一部分：架构总评

### 1.1 Clean Architecture 合规度

**判定：高度合规（A），有一处已声明的偏离。**

```
依赖方向（实测 36 项目全图）：
  Core（零 ProjectReference + 仅 ByteAether.Ulid）← 领域层纯净 ✅
  CQRS/EventLog/Idempotency/Projections/Serialization → Core ✅
  Messaging → Core + Serialization ✅
  Transactions → Core + Messaging + Serialization ✅
  Dapper/PalORM → Core + Transactions + EventLog + Projections (+Idempotency) ✅
  Dapper.MySql/PostgreSql/Sqlite → Dapper ✅
  PalORM.MySql/PostgreSql/Sqlite → PalORM ✅
  Messaging.Kafka/RabbitMQ → Messaging + Serialization ✅
  Transactions.EFCore → Transactions ✅
  Hosting.AspNetCore → Core + CQRS + Messaging + Transactions ✅
  依赖方向：全部指向内层，零循环 ✅（DFS 全图验证）
```

**一处已声明偏离**：`PalDDD.Core` 中的 `IUnitOfWork`（`PalDDD.Core.Repository` 命名空间）是持久化关注点在领域层的存在——通过注释显式声明了合并理由（原独立项目合并），且用命名空间区隔语义。这是**有理由的偏离**（避免项目爆炸），但严格 Clean Architecture 会将其放在 App-Abstractions 层。

**DDD 战术模式完整度**：

| 模式 | 实现 | 质量评估 |
|------|------|---------|
| Entity | `Entity` 基类：单链表事件存储 + `Entity<TId>` 身份 | ✅ 精确注释（线程安全边界/性能数字勘正史）|
| AggregateRoot | `AggregateRoot<TId>` 薄基类 | ✅ 符合"薄基类"原则，不变性由子类维护 |
| ValueObject | `readonly record struct` + `IValueObject` 标记 | ✅ 零分配 + 语言特性利用 + 通俗注释 |
| DomainEvent | `IDomainEvent`（`static abstract EventName`）+ AsyncLocal TimeProvider | ✅ AOT 安全 + 测试隔离 |
| Aggregate | 聚合内不变性由子类维护 | ✅ 不提供 IRepository —— 明确"不做" |
| UnitOfWork | `IUnitOfWork` 接口 + 扩展方法组合操作 | ✅ ISP 原则（扩展方法减实现者负担）|
| Process Manager / Saga | Saga 全状态机（1002 行但拆为 6 文件）| ✅ 补偿链基于 ExecutedStepKeys |

### 1.2 AOT 三态分层策略

```
显式 true (8):   Dapper×4 + PalORM×4 —— 源生成 SQL（ADR-020 推荐）
显式 false (14): Analyzers×2 + SourceGen + Compression.Native + EFCore×5 + Messaging×2 + Transactions×2
继承 true (14):  Core/CQRS/Messaging/EventLog/Projections/Idempotency/Serialization/Evolution 等
```

**判定：合理（A）**。三态分层是本项目的**独特设计贡献**——不是"全员 true"的盲目追求，而是按项目能力精确声明。`ArchitectureBoundaryTests` + `InfrastructureAdapters_AreExplicitlyNonAot` 机械守护。

---

## 第二部分：十维度逐维评审

### 2.1 可维护性（A-）

**正面证据**：
- **ADR 驱动**：22 份 ADR 覆盖全部关键决策（持久化选型/消息版本化/AOT 策略/Unit 简化等），每份含背景/方案/决策/后果/替代方案——可维护性的根基。
- **机械守卫 62+**：SourceCodeGuard（6 族 57 红绿样本）/ ArchitectureBoundary（37 方法）/ DiagnosticCoverage（38 条）/ DocConsistency（15 测试）/ AssertionStrength / TechDebtGuard / TestGateGuard——**每个守卫都带红测**（PD29 闭环），维护者可以信任它们的输出。
- **收敛轨迹**：五轮审计从 5 语义错误降至 0——维护质量在提升而非退化。
- **质量工具链 C# 化**：27 个 file-based app 替代 bash/python——可维护性从"bash 语法陷阱 + 正则转义"升级到"编译期检查 + 类型安全"。
- **一致的项目模板**：36 项目统一 csproj 极简结构 + Directory.Build.props 全局配置 + 中央包管理——新增项目零配置决策。

**负面证据**：
- **Saga.cs 1002 行**——虽拆为 6 文件，主文件仍是最大的单一维护面。状态机 + 补偿 + 超时 + FanOut + 中断恢复交织在同一个类中，虽然每个方法有清晰注释，但新维护者需要理解完整的 Saga 生命周期才能安全修改。
- **三持久化栈同构维护**——Dapper/PalORM/EFCore 三栈实现相同接口（IPalOutboxStore/IInboxStore/ISagaStateStore 等），每次修复需要三栈同步（v3 轮 ITM-653 的 OCE 过滤姊妹收口即为此形态的实证）。ADR-020 已声明 Dapper 栈 v3.0 退役，但当前仍需维护。
- **.ai 知识层的维护成本**——lessons 18 章 + 误判库 39 条 + metrics 87 行 + 5 份审计报告 + 多个行动项清单——知识传递链丰富但也意味着每轮修改需要同步多个文档（V5/V22/D12 校验器防止漂移，但维护负担仍存在）。

**评级依据**：可维护性的核心度量是"新维护者能否安全修改代码"——ADR + 守卫 + 测试 + 注释的四重保障使答案接近"是"。扣分项是结构性的（三栈/大文件/知识层体积），非执行能解决。

### 2.2 健壮性（A）

**正面证据**：
- **Fail-fast 全栈覆盖**：参数守卫（ThrowIfNull/ThrowIfNegativeOrZero）在 36 项目的公共入口统一部署；构造器守卫（ITM-165/653/654 三栈 Commit/Rollback/Begin 的 ObjectDisposedException）姊妹同步。
- **OCE 三形态合规**：全仓 `catch(Exception)` 逐一核对——`when(ex is not OCE)`（合规过滤）/ 前置 `catch(OCE){throw;}`（不吞取消）/ `[SuppressMessage(CA1031)]`（后台处理器豁免带 Justification）——v3 轮修正了 Idempotency/Projection 两处姊妹遗漏。
- **SQL 零注入**：全部存储层参数化（PalORM FormattableString / Dapper 参数化 / EFCore LINQ）；标识符白名单（PostgreSqlAuditor.QuoteIdentifier / ShardedTableName.ValidateIdentifier / SoftDelete.Escape）；CSV 公式注入防护（ITM-201/215）。
- **AOT 安全**：零反射（DIM 桥接替代 MakeGenericType）+ STJ 源生成（JsonTypeInfo 强类型）+ `static abstract` 编译时常量 + SqlErrorClassifier 安全降级声明。
- **守卫红测纪律**：62+ 守卫每道带红绿矩阵（SourceCodeGuard 57 样本/DiagnosticCoverage 16 形态/AssertionStrength 注入负例）——**每台仪器都被证明能失败**。

**负面证据**：
- **EventLog 重查 OCE 窗口**：唯一约束冲突后重查段的过滤器只捕 `DbException`，重查自身抛 OCE 时原始异常被替换（v3 轮标注"窄窗口不修"——OCE 传播 = 取消优先合规）。
- **InMemory 栈限制**：`InMemorySagaStateStore` 固定 OrderBy(CreatedAt) 下 AWD 积压 ≥ batchSize 时饿死后续（v44 已声明测试/原型栈接受）。
- **`GetActualStreamVersionAsync` 的 `?? -1`**：与 `ConfigureAwait` 的优先级交互在 v3 轮已修复，但类似的 coalesce-await 形态在其他文件中**无守卫覆盖**（当前无实例但无守卫防止未来引入）。

**评级依据**：健壮性的核心度量是"异常路径是否安全"——fail-fast + 参数化 + AOT 三重保障覆盖了绝大多数异常路径。扣分项是已声明的窄窗口，非结构性缺陷。

### 2.3 可读性（A-）

**正面证据**：
- **Emoji 语义头标**：每个文件头用 👑 Entity / 📬 DomainEvent / 📤 OutboxProcessor / 🏛️ AggregateRoot 语义标记——扫一眼就知道文件的角色。
- **中文设计注释**：设计原则用 `💡` 段落式（"为什么不用 List<T>？因为…"），通俗解释用 `<para>` XML doc——注释回答"为什么"而非"做什么"。
- **XML doc 全覆盖**：D11 守卫（`CorePublicApi_HasXmlDocCoverage`）机械强制公共 API 文档——覆盖扫描器有自证测试（注入 gap 必被报出）。
- **文件命名 = 主类型名**：`Entity.cs → class Entity`，`Saga.cs → class Saga`——零搜索成本。
- **v62/v70 勘正文化**：性能数字从"88B/事件"勘正为定性描述、v33→v34→v65→v66 的归因链——可读性不是初始状态而是持续维护的结果。

**负面证据**：
- **多轮勘正注释堆叠**：Dispatcher.GetFrozenEntries 的注释从 v29→v33→v34→v65 逐轮追加，当前约 20 行注释描述同一特例——新维护者需要区分"当前有效的设计"和"历史勘正记录"。
- **Saga.cs 1002 行**：虽有清晰的段落分隔和设计注释，但状态机转换 + 补偿 + 超时 + FanOut + 中断恢复 + 观察者模式的交织使得线性阅读困难。
- **IdentityGenerator 863 行**：源生成器的模板字符串（`$$"""..."""`）中嵌套 C# 代码 + 诊断描述 + 性能注释——同一文件混合三层抽象。

**评级依据**：可读性的核心度量是"新维护者能否快速理解设计意图"——Emoji + 中文注释 + XML doc + 命名规范的四重保障覆盖了绝大多数场景。扣分项是**历史信息堆积**（勘正注释）和**大文件复杂性**，非风格问题。

### 2.4 可扩展性（A-）

**正面证据**：
- **适配器模式**：三持久化栈（Dapper/PalORM/EFCore）实现相同接口——新增第四栈只需实现 IPalOutboxStore/IInboxStore 等接口 + DI 注册，不改框架代码。
- **DIM 桥接**：`IPipelineBehavior` 非泛型接口通过 DIM 自动实现——新增管道行为只需实现泛型版本，零反射 AOT 安全。
- **源生成器**：`[GenerateId]` / `[GenerateEnum]` / `[GenerateMessage]` 三类生成器——新增身份类型/智能枚举/消息类型只需标注 attribute。
- **Broker 抽象**：`IMessageBroker` + `MessageBrokerBase`——Kafka/RabbitMQ/InMemory 可互换（对称测试保证）。
- **ADR 流程**：新决策产出 ADR 编号——文档化的决策过程保证扩展不偏离设计原则。
- **文件创建决策矩阵**：conventions §4.9 的 40+ 行矩阵——新增任何类型的文件，查找即得放置位置。

**负面证据**：
- **ArchitectureBoundaryTests 硬编码清单**：App 层项目列表（4 个 csproj）/ Domain+App 层关键字目录（8 个）/ Infra 项目（14 个）——新增 App 层项目需手动更新 3+ 处清单（I 类守卫的已知边界，v5 轮验证当前正确但无哨兵防止漂移）。**注**：ITM-667 的 guard.cs 已提供原子守卫命令，但清单本身仍需手动维护。
- **新增诊断的四步流程**：StrategicDddAnalyzer 定义 → SourceGen 或 Analyzers 实现 → DiagnosticCoverageGateTests 断言 → StrategicDddAnalyzerTests 负向测试——四步缺一不可但无自动脚手架。
- **三持久化栈的扩展**：新增方言（如 Oracle）需要三栈各自实现——Dapper 栈 v3.0 退役后降为双栈，但当前仍是三栈负担。

**评级依据**：可扩展性的核心度量是"新增能力需要改多少处"——适配器模式和 DIM 桥接使核心扩展改动集中，但架构测试的硬编码清单和三栈同构是扩展摩擦的主要来源。

### 2.5 灵活性（B+）

**正面证据**：
- **三持久化栈可选**：用户按 AOT 需求选 Dapper（真 AOT）/ PalORM（推荐 AOT）/ EFCore（生态兼容）——不是框架强制。
- **Broker 可换**：IMessageBroker 抽象 + MessageBrokerBase——Kafka/RabbitMQ/InMemory 对称实现。
- **TimeProvider 注入**：全域 TimeProvider（非 DateTimeOffset.UtcNow）——测试注入 FakeTimeProvider 确定性时间。
- **Policy 模式**：IdempotencyPolicy / CompensationPolicy / RetryBackoffPolicy——策略可配置。
- **PipelineBehavior 可插拔**：管道行为通过 DIM 接口注册——零反射构建管道链。

**负面证据**：
- **Dapper 栈接口耦合**：DapperOutboxStore 实现 `IPalOutboxStore`（PalDDD.Transactions 接口）+ 直接引用 `DapperAmbientTransaction`（PalDDD.Dapper 内部）——跨栈切换需要更换 DI 注册但不需要改业务代码，这是合格的灵活性。
- **Saga 的 store 接口约束**：`ISagaStateStore` 要求实现者提供 `SaveChangesAsync`/`LeaseActiveSagasAsync` 等完整接口——部分实现场景需要空方法（接口隔离不足），但这是 ISagaManager 内部使用的最小接口设计。
- **序列化器选择**：JsonMessageSerializer / MemoryPackMessageSerializer 可选——但 wire format 版本化（.v1 后缀）绑定 JSON 格式，MemoryPack 的二进制格式无版本化。
- **ZLogger 硬依赖**：DependencyInjection 项目直接引用 ZLogger——IPalLogger 抽象存在但 DI 入口默认绑定 ZLogger，换日志框架需要覆盖 DI 注册。

**评级依据**：灵活性的核心度量是"换组件需要改多少处"——持久化和 Broker 可换（不改业务代码）达标；序列化和日志部分可选（有抽象但默认绑定）；扣分项是 wire format 版本化的灵活性不足（MemoryPack 路径）。

### 2.6 简洁性（B+）

**正面证据**：
- **AggregateRoot 薄基类**：构造器一个参数、零方法——"薄基类"原则的教科书实现。
- **DIM 消除反射**：泛型接口 + 非泛型桥接 + `static abstract` 编译时常量——消除了 Reflection.Emit/Marshall 的运行时复杂度，代价是接口数量增多。
- **明确"不做"清单**：IRepository / IIntegrationEvent / [Transaction] / Assembly Scanning / Protobuf——五项明确不做，防止功能蔓延。
- **ValueObject 语言利用**：`readonly record struct` + `IUtf8SpanFormattable` + 泛型数学（`INumber<T>`）——用语言特性替代框架复杂度。
- **AOT 三态策略**：不是"全员 true"的盲目追求，而是按项目能力精确声明——简单且理性。

**负面证据**：
- **PipelineStateMachine ref struct**：~40B 可重用的状态机替代 N×72B 的闭包链——性能收益真实，但 ref struct 的使用限制（不能跨 await/不能存字段）增加了理解和维护复杂度。
- **单链表事件存储**：`_head`/`_tail` 指针替代 `List<T>`——O(1) 追加 + 零容器分配，但遍历顺序/插入中间/批量操作需要手写逻辑。
- **DIM 接口膨胀**：每个泛型接口需要一个非泛型桥接接口（ICommandHandler → ICommandHandler 桥接 + IRequest + IBaseRequest）——接口数量约为"无 AOT 约束"方案的 2 倍。
- **Saga 状态机的复杂度**：1002 行（即使拆 6 文件）的 Saga 全生命周期——状态转换 × 补偿 × 超时 × FanOut × 中断 × 观察者的组合复杂度是问题域固有的，但某些辅助方法（SafeObserve 系列）可以提取到更小的协作类。

**评级依据**：简洁性的核心度量是"实现是否比问题域固有的复杂度更复杂"——DIM/AOT/零分配的额外复杂度是 AOT 约束的必要代价（简化 = 牺牲 AOT）。扣分项是**个别地方可以从"精心设计"降级为"足够好"**（如 PipelineStateMachine 的收益是否值得 ref struct 的维护成本——v70 勘正表明部分性能数字已无法验证）。

### 2.7 合理性（A）

**正面证据**：
- **ADR 驱动**：22 份 ADR + 文件创建决策矩阵 + 明确"不做"清单——每个设计决策有文档化的理由和替代方案。
- **AOT 三态策略**：不是"全员 true"也不是"全员 false"——按项目能力精确声明，边界测试机械守护。
- **质量系统证据驱动**：红测纪律（每台仪器见过错误答案）/ S3 反向验证（移除修复确认退化）/ 收敛轨迹（v2→v5 错误载体降维）/ mutation 实证（PALENUM004/PALID003 曾零守护）——**决策基于数据而非直觉**。
- **三持久化栈的理性**：不是"一个最好的"，而是"AOT 主线（PalORM）+ 生态兼容（EFCore）+ Dapper 过渡（ADR-020 退役路线图）"——尊重用户选择而非框架强制。
- **TimeProvider 而非 static**：AsyncLocal 隔离 + 注入式测试——解决了全局时钟的并行测试干扰问题。

**负面证据**：
- **三持久化栈的维护成本**——三个栈实现相同的六 Store 接口，每轮修复需要三栈同步。ADR-020 已声明退役路线但**执行时间表不明确**。
- **Saga 状态机的领域复杂度**——问题域固有的复杂度（不是设计不当），但 Saga.cs 1002 行的维护面是否可以通过提取协作类（如 SagaCompensation 已独立）进一步降低？已做了 SagaCompensation/SagaTimeoutDetector 的提取，剩余部分是核心状态机。

**评级依据**：合理性的核心度量是"设计决策是否基于充分的理由和证据"——ADR 驱动 + 收敛轨迹 + 红测纪律 + 明确"不做"清单表明**决策过程是理性的**。扣分项是三栈维护负担的退役时间表不明确。

### 2.8 兼容性（B）

**正面证据**：
- **wire format 版本化**：消息名 .v1 后缀 + SchemaVersion + Serialization.Evolution 管道（MessageEvolutionPipeline 消息升级）——消息格式变更不破坏消费者。
- **多方言覆盖**：PostgreSQL/MySQL/SQLite 三方言 + 方言实测探针（DialectProbeTests 42 断言 CI 真跑）。
- **SourceGen 向后兼容**：生成器输出稳定（PublicApiSnapshot 守护）——升级 SDK 不破坏已生成的代码。
- **AOT 三态**：适配器层显式 false + VerifyReferenceAotCompatibility 守护——AOT 项目引用非 AOT 适配器时编译期即报错。
- **PalOrmSample CI 载体**：PalORM.Sqlite 的 AOT publish 每次验证——确保推荐栈的 AOT 兼容不退化。

**负面证据**：
- **.NET 11 单 TFM**：不支持 .NET Standard 2.0 / .NET 8 multi-target——非 .NET 11 消费者无法使用。这是 ADR-005 的刻意决策（依赖 .NET 11 特性），但限制了采用面。
- **SQL Server OutboxDbContext [Obsolete]**：零测试覆盖的未验证方言基类——v3.0 移除，但当前用户如果依赖它会中断。
- **Dapper AOT 假象**：Dapper 项目 IsAotCompatible=true + Dapper.AOT，但 DapperBulkCopy 等部分功能在 AOT 下有局限（IL2062 抑制声明）——声明了但消费者可能不理解"有 AOT 但有局限"的细微差别。
- **EFCore 版本耦合**：EFCore 适配器引用特定版本的 Microsoft.EntityFrameworkCore——EF Core 升级可能引入 breaking changes。

**评级依据**：兼容性的核心度量是"升级/变更时消费者受影响程度"——wire format 版本化和 AOT 三态是亮点；.NET 11 单 TFM 和 SQL Server 不确定状态是最大的兼容性风险。

### 2.9 可复用性（B+）

**正面证据**：
- **Base/Extension 元包**：PalDDD.Base（Analyzers+Core+SourceGen+Serialization+Compression 最小集）/ PalDDD.Extension（全部包聚合）——消费者按需选包。
- **DIM 桥接可移植**：DIM 模式（泛型接口+非泛型桥接+static abstract）是**通用的 AOT 模式**——不依赖 PalDDD 框架，可直接复制到其他项目。
- **源生成器可移植**：三个生成器（IdentityGenerator/EnumGenerator/MessageRegistryGenerator）独立于框架运行时——配合 [GenerateId] 等属性可在任何项目使用。
- **StableNameValidation 共享**：跨包链接编译（无 NuGet 依赖）——稳定名称谓词的两包一致由编译保证。
- **InMemory 实现可复用**：InMemoryOutboxStore/InMemorySagaStateStore/InMemoryEventLog 等——测试和原型可直接使用（非简化 mock，有真实并发语义）。

**负面证据**：
- **IPalLogger/IUnitOfWork 框架耦合**：这些接口虽在 Core 中，但设计时考虑了 PalDDD 的 Outbox/Saga 语义——脱离 PalDDD 使用需要理解框架上下文。
- **PalDDD.Transactions 接口绑定**：IPalOutboxStore 等接口的方法签名与 Outbox/Saga 的领域概念紧耦合——非事件溯源项目复用价值有限。
- **.ai 系统项目耦合**：误判库/lessons/协议深度特化 Pal.DDD——不可直接复制到其他项目（v1.0 迁移时已重写全部检查项）。
- **ZLogger 硬绑定**：PalDDD.DependencyInjection 默认注册 ZLogger——换日志框架需要覆盖 DI 注册（有抽象但默认不中性）。

**评级依据**：可复用性的核心度量是"脱离本项目后组件是否有独立价值"——源生成器/DIM 模式/InMemory 实现/ValueObject 基类**可独立复用**；IUnitOfWork/IPalOutboxStore 等接口**需要 PalDDD 上下文**——这是 DDD 框架的固有特征（领域接口绑定领域概念），非设计缺陷。

### 2.10 可测试性（A）

**正面证据**：
- **1365 用例 / 17 测试项目**：覆盖单元（Core.Tests 299）/ 集成（Integration.Tests 265）/ 事务（Transactions 174）/ 多方言（PalORM 144）/ 架构（DI.Tests 124）/ 序列化/事件日志/消息/压缩/分析器等全栈。
- **TimeProvider 注入**：全域 TimeProvider 抽象——FakeTimeProvider 提供确定性时间（测试不依赖真实时钟的精确断言）。
- **InMemory 实现**：InMemoryOutboxStore/InMemorySagaStateStore/InMemoryEventLog 等——测试用真实并发语义（非简化 mock），且放在 src 与接口共置（可被 samples/benchmarks 复用）。
- **守卫自测**：verify-ai --selftest 16 例 / SourceCodeGuard 57 红绿样本 / DiagnosticCoverage 16 形态 / ArchitectureBoundary 负向自证 / AssertionStrength 注入负例——**62+ 守卫每道可证明能失败**。
- **红测纪律**：S3 反向验证（移除修复确认退化）/ mutation 实证（PALENUM004/PALID003）——测试的有效性被持续验证。
- **Testcontainers CI**：PG/MySQL/Kafka/RabbitMQ 容器自动启动——集成测试在 CI 真实环境跑（DialectProbeTests 42 断言）。
- **Throwing* 注入探针**：ThrowingProjectionCheckpointDbContext / ThrowingIdempotencyDbContext / ThrowingSagaStateDbContext——保存失败/取消/OCE 场景的确定性注入。

**负面证据**：
- **守卫分散 3 项目**：改 src 代码只跑受影响项目时，跨项目守卫不触发——guard.cs 原子命令已解决（ITM-667），但需要开发者知道用。
- **真实时钟残留**：SagaProcessor 轮询测试（Task.Delay 400ms/150ms）/ FanOutStep 快慢任务窗口 / EntityTests 时钟区间断言——CI 高载下有假红可能（已放宽但未根除）。
- **PalORM 46 环境性失败**：MultiDialectFixture 强制要求 Testcontainers——本机无 Docker 时 46 测试全部失败（fail-closed 正确但开发者体验差——skip 会更好但违反 fail-closed 设计）。
- **MTP --filter 限制**：`dotnet test --filter` 触发 VSTest 握手 exit 5——必须用 `-- --treenode-filter`，开发者容易踩坑（ci.yml 已注释但新维护者不知道）。

**评级依据**：可测试性的核心度量是"能否容易地写出有效测试并信任测试结果"——TimeProvider/InMemory/注入探针/守卫自测/red-test 纪律的五重保障使答案接近"是"。扣分项是**真实时钟残留**和**环境依赖的 fail-closed 体验**。

---

## 第三部分：DDD/Clean Architecture 原则逐条对照

| 原则 | 状态 | 证据 |
|------|:----:|------|
| 领域层零基础设施依赖 | ✅ | Core 零 ProjectReference + 仅 ByteAether.Ulid |
| 依赖方向外→内单向 | ✅ | 36 项目全图 DFS 零循环 |
| 聚合根保护不变量 | ✅ | 薄基类 + 不变性由子类维护 + 不提供 IRepository |
| 值对象不可变性 | ✅ | readonly record struct + 值相等性 |
| 领域事件不可变 | ✅ | IDomainEvent + EventName 编译时常量 |
| 显式接口优于反射 | ✅ | DIM 桥接 + static abstract + 零 MakeGenericType |
| 明确"不做"清单 | ✅ | IRepository/IIntegrationEvent/[Transaction]/Assembly Scanning/Protobuf |
| AOT 全链路 | ✅ | 三态分层 + STJ 源生成 + PalOrmSample CI publish 验证 |
| 消息版本化 | ✅ | wire name .v1 后缀 + SchemaVersion + Evolution 管道 |
| 事务边界显式 | ✅ | IUnitOfWork + ExecuteInTransactionAsync + Outbox 模式 |
| 限界上下文隔离 | ✅ | [BoundedContext] 标注 + wire name BC 前缀 |
| 事件溯源可选 | ✅ | EventLog + EventLogPositionReserver + EventLogReplaySource 独立包 |
| CQRS 分离 | ✅ | ICommandHandler/IQueryHandler 分离 + PipelineBehavior 可插拔 |
| Outbox 模式 | ✅ | OutboxDomainEventInterceptor + IPalOutboxStore + OutboxBatchProcessor |
| 幂等消费 | ✅ | IIdempotencyStore + IdempotencyProcessor + InboxProcessor |

---

## 第四部分：设计亮点（独特贡献）

| 亮点 | 说明 | 为什么值得 |
|------|------|-----------|
| AOT 三态分层 | 不是全员 true/false，按项目能力精确声明 + 机械守护 | 业界罕见的精细化 AOT 管理 |
| DIM 桥接模式 | 泛型接口 + 非泛型桥接 + static abstract 消除全部运行时反射 | AOT 约束下的 CQRS 管道通用解法 |
| 哨兵测试体系 | 62+ 守卫每道带红绿矩阵 + S3 反向验证 | "仪器见过错误答案"——PD29 闭环的行业级实践 |
| 质量工具链 C# 化 | 27 个 file-based app 全部替代 bash/python | 消除 bash 语言税 + 可编译 + 可测试 |
| 明确"不做"清单 | IRepository/IIntegrationEvent/[Transaction]/Scanning/Protobuf | 防止功能蔓延的架构纪律 |
| 文件创建决策矩阵 | 40+ 行"你要创建什么→放在哪→叫什么名" | 约定大于配置的极致实践 |
| 收敛轨迹追踪 | v2→v5 错误载体降维（语义→同步→注释→流程卫生） | 质量体系有效性的数据化证明 |
| 单链表事件存储 | _head/_tail 指针 O(1) 追加 + 零容器分配 | DDD 事件存储的性能优化教科书 |

---

## 第五部分：设计缺陷（结构性，非执行可解决）

| 缺陷 | 影响 | 可缓解性 |
|------|------|---------|
| **审计资源与防线规模线性矛盾** | 防线越多→审计面越大→全量审计不可行 | ITM-670 已策略化（变更面抽样为常态）；长期需按防线命中率分级审计 |
| **.ai 独立仓 → CI 防线是子集** | 评审引擎/知识层/账本不在 CI 上 | 本质限制（评审是会话级活动）；ITM-669 提醒行已补强制回路 |
| **三持久化栈维护负担** | 同一接口三栈实现，每轮修复需三栈同步 | ADR-020 已声明 Dapper v3.0 退役——退役后降为双栈 |
| **守卫测试分散 3 项目** | 本地"跑受影响项目"可能漏守卫 | guard.cs 已解决（ITM-667）；长期可考虑合并 QualityGate.Tests |
| **.NET 11 单 TFM** | 非 .NET 11 消费者无法使用 | ADR-005 刻意决策——依赖 .NET 11 特性（static abstract/ref struct/FrozenDictionary）|
| **真实时钟残留测试** | CI 高载下有假红可能 | 已放宽（九轮 P4）但未根除——根治需 FakeTimeProvider 全覆盖 |
| **文档同步无全量锚** | v4/v5 发现集中在文档面 | 全量锚定成本>收益；靠收敛趋势（问题密度在降）|

---

## 第六部分：与"行数少=更好"陷阱的对照

本项目**不满足**这个陷阱的特征——恰恰相反：

| 维度 | "行数少=更好"陷阱 | Pal.DDD 实际 |
|---|---|---|
| 注释密度 | "注释少=代码好" | 大量"为什么"注释（设计原则/性能数字/勘正史）——行数多但信息密度高 |
| 文件数 | "文件少=简单" | 36 项目/213 文件——但每个文件职责单一（文件命名=主类型名） |
| 抽象层 | "层数少=简单" | DIM 桥接增加接口数量——但消除了运行时反射的更高复杂度 |
| 测试数 | "测试少=维护少" | 1365 用例——但每道守卫的红测防止假绿（5 周死亡窗口的教训） |

**Pal.DDD 的行数服务于信息密度和可维护性**——如果为了减少行数而删除设计注释/勘正记录/防御性守卫，可读性和健壮性会立刻退化。

---

## 第七部分：改进建议（按 ROI 排序）

| 优先级 | 建议 | 维度 | 成本 | 预期收益 |
|:--:|------|------|------|---------|
| 1 | 合并守卫测试到 QualityGate.Tests 项目 | 可测试性 | 中 | 本地一条 `dotnet test` 触发全部守卫 |
| 2 | Saga.cs 提取 SagaStateTransition 协作类 | 可读性/可维护性 | 中 | 1002→~600 行核心 + 独立可测的状态转换 |
| 3 | Dapper 栈 v3.0 退役执行 | 可维护性/合理性 | 高（破坏性） | 三栈→双栈，消除同构维护 |
| 4 | IdentityGenerator 模板拆分 | 可读性 | 低 | 863 行 → 模板+逻辑分离 |
| 5 | PipelineStateMachine 收益验证 | 简洁性 | 低 | 确认 ref struct 的维护成本是否仍被性能收益覆盖 |
| 6 | 统一工具入口 quality.cs | 简洁性 | 低 | `dotnet run scripts/quality.cs gate|guard|verify-ai|...` 统一入口 |
| 7 | 误判库按命中率分级 | 可维护性 | 低 | PD 40+ 时防止速版上下文膨胀 |

---

## 附录：评审方法声明

- 本报告基于五轮全仓循环（v81-v87）的**实证数据**——不是走马观花的主观印象，而是 27 工具全量实跑 + 62+ 守卫红测 + 8 次抽样深审 + 4 次证伪裁决的累积结论。
- "避免行数少=更好"的陷阱：本报告**从未因为一个文件行数多而扣分**——Saga.cs 1002 行的扣分理由是"状态机+补偿+超时+FanOut+中断+观察者的交织使线性阅读困难"，不是"行数多"。
- 所有评级有 **A/B+/B 的区分**而非全部 A——反映的是真实的设计取舍代价，不是"完美"的自评。
