# Pal.DDD 踩坑目录

> DDD/CQRS/Event Sourcing 框架在事件驱动、最终一致性、Saga 编排、Outbox/Inbox 幂等等场景的常见陷阱与对应设计。
>
> **本文件不是 ORM 302 项陷阱的复制**——ORM 项目（QueryBuilder/ChangeTracker/Schema 迁移/Provider 方言）的踩坑与 DDD 无关。本文件聚焦 DDD 项目实际涉及的领域：事件驱动、最终一致性、分布式事务、Async 异步、AOT 分层、消息序列化、Saga 补偿链。
>
> **真源**：`docs/architecture.md`（18 项架构决策）+ `docs/decisions/001-016`（16 ADR）+ `docs/review/action-items-*.md`（ITM 历史缺陷）+ `conventions.md` §10/§12/§14
>
> **状态标记**: ✅ 已修复/已实现 · ⚠️ 部分实现 · 🚫 架构层面避开

---

## 目录

1. [事件驱动 & 最终一致性](#一事件驱动--最终一致性-saga--outbox--inbox)
2. [Async & Task 陷阱](#二async--task-陷阱)
3. [AOT & 反射陷阱](#三aot--反射陷阱)
4. [消息序列化 & 演化](#四消息序列化--演化)
5. [并发 & 锁模式](#五并发--锁模式)
6. [安全 & 审计](#六安全--审计)
7. [DDD/Clean Architecture 陷阱](#七dddclean-architecture-陷阱)
8. [诊断 & 可观测性](#八诊断--可观测性)
9. [DDD 项目实战新增](#九ddd-项目实战新增)
10. [PalORM 适配层踩坑](#十palorm-适配层踩坑-2026-07-28)

---

## 一、事件驱动 & 最终一致性（Saga / Outbox / Inbox）

> DDD 项目核心场景。ORM 项目踩坑目录 Phase 19（10 条）+ Phase 22（10 条）的原版+DDD 实战新增。

### 1.1 Outbox 模式

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|---|---------------|------------|:----:|
| **E1** | **场景**：业务操作 → DB INSERT → 直接调 Broker 发送消息 → DB 提交成功 → Broker 不可达 → 消息永久丢失。**问题**：双写非原子，Broker 故障导致事件丢失。**后果**：下游系统永不知道业务发生 | `OutboxDomainEventInterceptor` 在 DbContext.SaveChangesAsync 时把领域事件写入 Outbox 表（同一事务）→ `OutboxProcessor` 后台异步发布。**ADR-001** 采纳方案 B（逐条独立）| ✅ |
| **E2** | **场景**：多实例后台 `OutboxProcessor` 并发扫描 → 两实例拿到同一行 → 重复发布。**问题**：无锁的 SELECT-then-UPDATE 有竞态窗口。**后果**：下游重复消费 | PG 用 `FOR UPDATE SKIP LOCKED` / SQL Server 用 `UPDLOCK + READPAST` 原子租约（`leased_by` + `leased_until` 字段，schema 含 `idx_outbox_lease` 索引） | ✅ |
| **E3** | **场景**：Outbox 消息发布失败 → 重试 N 次后置 Dead → 无重投递入口 → ops 直写 `UPDATE status='Pending'`。**问题**：ops 越权重置绕过校验，可能重复发布。**后果**：违反幂等前提 | **ADR-011** 提供框架统一入口 `IPalOutboxStore.RequeueDeadAsync`——**幂等前提是调用方责任**（必须接入 Inbox/Idempotency/天然幂等 handler） | ✅ |
| **E4** | **场景**：OutboxMessage 负载用 string JSON → 大消息占内存 + 序列化开销。**问题**：字符串负载限制序列化器选择，违反 byte[] 二进制抽象。**后果**：无法切换 MemoryPack 等高效序列化 | `OutboxMessage.Payload` 强制 `byte[]`（`ArchitectureBoundaryTests.OutboxMessage_UsesBinaryPayload` 守护，禁 `public string Content`） | ✅ |
| **E5** | **场景**：OutboxDomainEventInterceptor 注册为 Singleton → 持有 `_pending` 实例字段 → 多请求并发交叉写入。**问题**：Singleton 生命周期共享状态。**后果**：A 请求的事件被 B 请求提交 | 架构测试 `OutboxDomainEventInterceptor_IsRegisteredAsScoped` 强制 TryAddScoped（ITM-026） | ✅ |

### 1.2 Inbox 幂等

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|---|---------------|------------|:----:|
| **E6** | **场景**：消息消费 → 处理成功 → ACK → DB 提交成功 → ACK 丢失 → 消息重新投递 → 重复处理。**问题**：At-Least-Once 投递语义 + 重复消费。**后果**：重复扣费/重复发货 | `IPalInboxStore` 提供 `INSERT OR IGNORE`（SQLite）/`ON CONFLICT DO NOTHING`（PG）+ UNIQUE(message_id) 约束 | ✅ |
| **E7** | **场景**：SQLite Inbox 用 `INSERT OR IGNORE` + `SELECT` 两步实现幂等 → 极小概率竞态（A 消费者 INSERT 前 B 消费者 SELECT 到未持久化行）。**问题**：TOCTOU 窗口。**后果**：理论上可能重复消费 | SQLite 路径 XML doc 明确"语义弱保证，生产推荐 PostgreSQL"；PG 用 `ON CONFLICT ... RETURNING` 单语句消除窗口（ITM-003） | ⚠️ |

### 1.3 Saga 编排与补偿

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **E8** | **场景**：Saga 步骤 1 成功 → 步骤 2 失败 → 补偿步骤 1 → 补偿基于"全部已执行步骤"。**问题**：错误地补偿了未执行的步骤。**后果**：补偿链断裂 → 状态不一致 | `CompensateAllAsync` 基于 `ExecutedStepKeys` 集合，**只补偿已执行的步骤**；异常收集后抛 AggregateException 不中断后续补偿（ITM-批次1） | ✅ |
| **E9** | **场景**：多实例后台 SagaProcessor 并发扫描超时 → 两实例补偿同一 Saga。**问题**：重复补偿。**后果**：补偿操作副作用翻倍 | `ISagaStateStore.LeaseActiveSagasAsync` 租约锁（`leased_by` + `leased_until`），三实现（InMemory/Dapper/EFCore）全部对齐，4 份 SQL schema 含 `idx_saga_lease` 索引（ITM-批次2） | ✅ |
| **E10** | **场景**：SagaTimeout 无界扫描 → 数据库饥饿。**问题**：扫描全表 → 大 Saga 状态表拖垮 DB。**后果**：服务降级 | `SagaTimeoutStore_UsesBoundedActiveScan` 强制 `GetActiveSagasAsync(int batchSize)` + EFCore 实现用 `.Take(batchSize)`（架构测试守护） | ✅ |
| **E11** | **场景**：SagaKey 用 `|` 拼接 state + eventType → 第三方状态名含 `|` 字符。**问题**：分隔符冲突 → 静默错误。**后果**：Saga 状态错乱 | `SagaKey.Make` 运行时 `IndexOf('|')` 校验 + `SagaState.CurrentState` setter 同步校验 + `SagaKeyValidationTests`（ITM-001） | ✅ |

### 1.4 事件溯源（Event Sourcing）

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **E12** | **场景**：两个 writer 并发追加 version=5 → 一个成功 → 另一个失败 → 事件丢失。**问题**：乐观并发冲突。**后果**：状态不一致 | `ExpectedStreamVersion` 显式版本号 + `AppendEventsAsync` 用 `WHERE version=@expected` 实现乐观锁 → 冲突自动重试 → 幂等 | ✅ |
| **E13** | **场景**：事件重放 → 读取 RecordedEvent → `ToArray()` 拷贝 byte[] → 2 次分配/事件 → 10 万事件 = 60MB GC 压力。**问题**：读取路径不必要的拷贝。**后果**：重放慢 + GC 抖动 | **ADR-006** 双构造路径：写入 `ToArray()` 防御拷贝（公共 API），读取 `RehydrateFromBytes` 零拷贝（internal，受信任 Infrastructure 包） | ✅ |
| **E14** | **场景**：领域事件单链表存储 → 改用 List<T> → 无事件时仍分配 List 容器。**问题**：高频无事件场景每次分配。**后果**：GC 压力 | `DomainEventEnumerable` 单链表（`_head`/`_tail`）+ `ref struct` 枚举器 → 无事件时零分配 | ✅ |

### 1.5 跨进程消息契约

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **E15** | **场景**：跨进程消息 payload 实现 `IIntegrationEvent` 标记接口 → 消费方通过类型反射读取业务属性。**问题**：基础设施接口污染 + 反射不安全。**后果**：耦合 + AOT 风险 | 🚫 不暴露 `IIntegrationEvent`，跨进程 payload 是普通 CLR 类型（架构测试守护） | ✅ |
| **E16** | **场景**：消息演化用 `IUpcaster` marker 接口 + Assembly.GetTypes() 扫描实现。**问题**：反射扫描违反零反射红线。**后果**：AOT 不兼容 | 🚫 不暴露 `IUpcaster`，用 `MessageEvolutionPipeline` 显式注册演化步骤（ADR-007） | ✅ |
| **E17** | **场景**：消息 wire name 不稳定 → dispatcher/trace/EventLog/broker 各自命名 → 跨服务无法对齐。**问题**：两套名称。**后果**：消息丢失/误连 | 稳定 wire name 规则：小写+`.v{n}` 后缀+BC 前缀（PDDD008/009/010/011/015 + PALMSG001-005 编译期强制） | ✅ |
| **E18** | **场景**：DomainEvent 可继承 → 子类事件层级 → replay/serializer/handler 分派语义漂移。**问题**：继承层级导致序列化不闭合。**后果**：消息丢失 | PDDD012 强制 DomainEvent `sealed`（事件契约对回放/序列化关闭） | ✅ |

---

## 二、Async & Task 陷阱

> ORM 项目踩坑目录 Phase 17（15 条）的 DDD 适用部分。

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **A1** | **场景**：`async Task Foo()` → 调用 `OutboxProcessor.ExecuteAsync()` → 忘记 `await`。**问题**：返回 `Task` 而非值 → 后续代码 NullRef。**后果**：生产崩溃 | 所有 API 返回 `ValueTask`/`ValueTask<T>` + TreatWarningsAsErrors + CS4014 警告 | ✅ |
| **A2** | **场景**：ASP.NET Core → `async` Controller → `_outboxStore.AppendAsync()` → 不 ConfigureAwait(false)。**问题**：同步上下文捕获 → 线程池饥饿。**后果**：高并发慢 | 全层库代码 ConfigureAwait(false)（conventions §1.5，143+ 处）+ PDDD-G12 强制 | ⚠️ 部分违规（gate-check 发现 12 处） |
| **A3** | **场景**：`async void` → 异常逃逸 → 进程崩溃。**问题**：async void 异常无法捕获。**后果**：服务意外终止 | 🚫 禁止 async void（conventions §1.5 + PDDD-G9 + ArchitectureBoundaryTests） | ✅ |
| **A4** | **场景**：`PeriodicBackgroundProcessor` 内层 `catch (Exception)` → 捕获下游 CancellationToken 取消（非 host 关停）→ 记为错误日志。**问题**：取消异常被误报为错误。**后果**：日志噪声 + 误判服务健康 | `catch (OperationCanceledException)` 静默分支，仅过滤 host 关停取消（ITM-030，3 处同型：PeriodicBackgroundProcessor/ExceptionMiddleware/HealthCheck） | ✅ |
| **A5** | **场景**：`catch (Exception)` 不带 `when (ex is not OperationCanceledException)` 过滤 → 取消异常被吞掉 → 上层无法感知取消。**问题**：异常过滤缺失。**后果**：取消语义错误 | conventions §10.3 强制 `when (ex is not OperationCanceledException)`（PDDD-G7 + boundary 守护） | ✅ |
| **A6** | **场景**：`Task.Delay(100)` + `CancellationToken` → 模拟超时 → 实际查询不取消。**问题**：超时不传给 DbCommand → 假超时。**后果**：超时无效 + 资源泄漏 | 全链路 CancellationToken 传递（conventions §1.5） | ✅ |
| **A7** | **场景**：`ValueTask` 重复 await → "Already consumed"。**问题**：ValueTask 单次消费语义。**后果**：运行时崩溃 | 文档 + XML doc 警告，热点路径 ValueTask 同步完成零分配 | ✅ |
| **A8** | **场景**：事务回滚 → 裸 `catch (Exception)` 清理 → 清理异常覆盖主异常 → 失败原因丢失。**问题**：主异常保留 vs 清理异常的权衡。**后果**：日志只显示清理异常 | 清理异常挂 `Exception.Data` 而非 throw，保留主异常（conventions §10.3 唯一例外） | ✅ |

---

## 三、AOT & 反射陷阱

> DDD 项目 AOT 分层的关键陷阱。

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **T1** | **场景**：`IsAotCompatible=true` + NoWarn IL3058 → 编译 0 警告 → NativeAOT 发布后 QueryAsync\<T\> 抛 PlatformNotSupportedException。**问题**：编译假象。**后果**：AOT 运行时崩溃（PalDDD 教训：Dapper+SQLite 16 测试失败） | AOT 核心层 7 项目 `IsAotCompatible=true` + 非 AOT 适配器层 14 项目**显式 false**（设计本意，`InfrastructureAdapters_AreExplicitlyNonAot` 守护） | ✅ |
| **T2** | **场景**：`MakeGenericType` 在 DefaultSagaManager 动态拼接 Saga\<TState\>。**问题**：AOT 下 MakeGenericType 抛异常。**后果**：Saga 失败 | `PalDDD.Transactions` 项目**主动声明 IsAotCompatible=false** + `[RequiresDynamicCode]` 精确标注反射路径（csproj 注释说明） | ✅ |
| **T3** | **场景**：`Activator.CreateInstance` 动态创建 handler。**问题**：AOT 不兼容。**后果**：发布失败 | 显式 DI 注册 `AddPalCommandHandler<TCmd, TKey, THandler>()` 替代（conventions §1.4） | ✅ |
| **T4** | **场景**：`Assembly.GetTypes()` 扫描 handler 类型。**问题**：AOT 修剪后类型丢失。**后果**：handler 未注册 | `HandlerRegistrar`（IHostedService）启动期显式注册 → `Dispatcher.Freeze()` 转 FrozenDictionary（ADR-007） | ✅ |
| **T5** | **场景**：`System.Text.Json` 默认反射路径 → JsonSerializer.Serialize\<T\> 在 AOT 下抛异常。**问题**：反射序列化。**后果**：发布失败 | `[JsonSourceGenerationOptions]` + `[JsonSerializable]` 编译时生成 JsonTypeInfo + `JsonSerializerIsReflectionEnabledByDefault=false`（conventions §1.3） | ✅ |
| **T6** | **场景**：`Expression.Compile()` 在 ISpecification 编译表达式树。**问题**：AOT 不兼容。**后果**：规约评估失败 | **PDDD-G8 当前发现 ISpecification.cs:218 真实违规**（`_expression.Compile()`）—— **待修复** | ⚠️ |
| **T7** | **场景**：`Type.GetType(string)` 运行时反射查找类型。**问题**：AOT 修剪。**后果**：返回 null | 用 `typeof(T)` 编译时常量替代（conventions §1.4） | ✅ |

---

## 四、消息序列化 & 演化

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **S1** | **场景**：消息 wire name 不带版本后缀 → 演化时 schema 变更 → 旧消费者反序列化失败。**问题**：无版本 → 演化即破坏。**后果**：下游消费者崩溃 | PDDD010 强制消息名以 `.v{N}` 版本后缀结尾 | ✅ |
| **S2** | **场景**：消息演化（schema v1 → v2）→ 用 `IUpcaster` marker 接口 + Assembly.GetTypes() 扫描。**问题**：反射扫描。**后果**：AOT 不兼容 | `MessageEvolutionPipeline` 显式注册演化步骤（ADR-007），启动期 `PalPlatformVerifier` 校验演化路径完整 | ✅ |
| **S3** | **场景**：MessageCatalog 运行时可变（Register 在运行时调用）→ 并发修改竞态。**问题**：可变全局状态。**后果**：消息路由错乱 | `MessageCatalog` sealed class + `MessageCatalogBuilder` 构建期填充 + `Freeze()` 转 `FrozenDictionary`（架构测试 `MessageCatalog_IsImmutable` 守护） | ✅ |
| **S4** | **场景**：JsonMessageSerializer 每次调用创建 Utf8JsonWriter → GC 压力。**问题**：高频路径分配。**后果**：吞吐降低 | ThreadStatic 池化（`_tlsWriter` + `_tlsBufferWriter`），禁改实例字段（破坏线程安全）禁改 AsyncLocal（执行上下文开销）（conventions §12.2） | ✅ |
| **S5** | **场景**：SchemaVersion=0 或负数 → 消息契约无意义。**问题**：版本号语义错误。**后果**：演化逻辑混乱 | PDDD011 强制 `SchemaVersion >= 1`（编译期诊断） | ✅ |

---

## 五、并发 & 锁模式

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **C1** | **场景**：分布式锁 Redis TTL 太短 → 业务超时 → 锁被其他实例获取 → 双实例并发。**问题**：TTL 锁漏洞。**后果**：并发写 → 数据不一致 | PG 用 `FOR UPDATE SKIP LOCKED`（行级锁 + 连接断开自动释放，无 TTL 问题）；SQL Server 用 `UPDLOCK + READPAST` | ✅ |
| **C2** | **场景**：Dispatcher.Register 在启动后继续调用 → 与并发 Dispatch 请求竞态。**问题**：Freeze 后修改。**后果**：路由错乱或 ObjectDisposedException | `Dispatcher.Freeze()` 后转 `FrozenDictionary`，禁运行时 Add（ITM-027 XML doc 约束启动期单线程） | ✅ |
| **C3** | **场景**：InMemoryOutboxStore 用 `lock(object)` → .NET 9+ Lock 性能更好。**问题**：旧锁机制。**后果**：竞争激烈时性能差 | conventions §10.4 + §1.7 强制 `Lock`（.NET 9+） | ✅ |
| **C4** | **场景**：Dispatcher.Freeze() 与 IMessageBroker.Publish 并发 → 读 FrozenDictionary 与可能的写冲突。**问题**：读写竞态。**后果**：路由错乱 | Freeze() 后只读 FrozenDictionary，写入在启动期完成（启动期单线程约束） | ✅ |
| **C5** | **场景**：SagaState.CurrentState 含特殊字符 `|` → SagaKey.Make 用 `|` 拼接 → 解析错误。**问题**：分隔符冲突。**后果**：Saga 路由错乱 | 运行时 `IndexOf('|')` 校验 + setter 同步校验（ITM-001） | ✅ |

---

## 六、安全 & 审计

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **SE1** | **场景**：PostgreSqlAuditor 用 `QuoteIdentifier` 转义表名 → 无白名单校验 → 恶意标识符注入 SQL。**问题**：标识符注入。**后果**：SQL 注入 | 标识符白名单校验 + `EscapeLiteral` 分离 + `PurgeOldAuditLogs` 范围校验（ITM-批次3） | ✅ |
| **SE2** | **场景**：连接串硬编码（`Password=x;Host=y`）→ 入库 → 泄露。**问题**：凭据入库。**后果**：DB 被拖库 | PDDD-G19（原 ORM G9）扫描受跟踪文件零硬编码凭据 | ✅ |
| **SE3** | **场景**：SQL 注入 → `string.Format` 拼接 SQL。**问题**：字符串拼接。**后果**：注入风险 | 🚫 禁 `string.Format` 拼 SQL；用 SqlTemplates `public const string` + Dapper 参数化（conventions §12.4 + PDDD-G7） | ✅ |
| **SE4** | **场景**：异常消息/日志含 PII（连接串/患者数据/卡号）。**问题**：PII 泄露。**后果**：合规违规 | 异常消息仅技术描述，连接串脱敏（ITM-D04 已统一 Justification） | ✅ |

---

## 七、DDD/Clean Architecture 陷阱

> DDD 战术模式的核心陷阱。

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **D1** | **场景**：团队引入 `IRepository<TAggregate, TKey>` 包装 DbContext → 增加间接层。**问题**：DbContext 已是 UoW+Repository，再包一层违反 DDD 聚合根边界。**后果**：抽象泄漏 | 🚫 不提供 `IRepository<T>`（ArchitectureBoundaryTests 守护 `RepositoryBase.cs` 不存在 + IUnitOfWork 不含 IRepository\<T\>） | ✅ |
| **D2** | **场景**：领域层直接用 DbContext/SqlConnection → 领域层依赖基础设施。**问题**：依赖反向。**后果**：领域层不可测试/不可复用 | `DomainAndAppLayers_DoNotContainInfrastructureKeywords` 强制 Domain+App 层零 EF/ADO.NET/Dapper 关键字（PDDD-G2） | ✅ |
| **D3** | **场景**：CQRS 用 `[Transaction]` attribute 隐式声明事务 → 反射 + 隐藏策略。**问题**：事务策略通过 attribute 隐藏。**后果**：调试困难 + AOT 风险 | 🚫 CQRS 不含 TransactionAttribute（架构测试守护 + PDDD-G7），事务由应用层或外层持久化适配层显式处理 | ✅ |
| **D4** | **场景**：App 层引入 HttpClient/HttpContext → CQRS 耦合表现层。**问题**：层级混乱。**后果**：CQRS 不可复用于非 HTTP 场景 | `AppLayers_DoNotContainHttpInfrastructureKeywords` 强制 CQRS/Transactions/EventLog/Messaging 零 HttpClient（PDDD-G3） | ✅ |
| **D5** | **场景**：Core 层引入 CQRS/Messaging 引用 → 领域层依赖应用层。**问题**：依赖反向。**后果**：领域层耦合应用逻辑 | `CoreLayer_HasNoProjectReferences` 强制 Core 零项目引用（仅 ByteAether.Ulid 包） + `CoreLayer_Usings_DoNotImportAppOrInfrastructureNamespaces`（PDDD-G4/G5） | ✅ |
| **D6** | **场景**：进程内事件总线 EventBus → broker 不可达时事件永久丢失（AT-MOST-ONCE 语义）。**问题**：不可靠。**后果**：业务事件丢失 | 🚫 移除 EventBus（架构测试守护 EventFilter.cs 不存在），统一用 Outbox 模式（AT-LEAST-ONCE + Inbox 幂等） | ✅ |
| **D7** | **场景**：Bounded Context 概念缺失 → 跨上下文直接调用 → 边界模糊。**问题**：BC 治理缺失。**后果**：领域模型耦合 | PDDD001 强制领域模型声明 `[BoundedContext]` + PDDD013/014 校验 Projection/ProcessManager 属于 BC | ✅ |

---

## 八、诊断 & 可观测性

| # | 场景·问题·后果 | DDD 对应设计 | 状态 |
|:--:|---------------|------------|:----:|
| **O1** | **场景**：Saga 补偿链无统一追踪 → 补偿成功/失败不明。**问题**：补偿可观测性缺失。**后果**：资金挂起，手动修复 | 每步骤 emit 事件 + Tag("saga:{id}")（ITM-028 可观测性增强） | ✅ |
| **O2** | **场景**：消息消费不记录 messageId → 幂等性无法追溯。**问题**：消息血缘缺失。**后果**：重复扣款对账困难 | MessagePublishContext 传 correlation/causation/TraceParent/TraceState 给 broker | ✅ |
| **O3** | **场景**：OTel Activity 跨测试项目并行干扰。**问题**：测试隔离缺失。**后果**：测试不稳定 | `RecordingActivityListener` 构造时记录 `_createdAt` 时间戳，`ActivityStopped` 过滤早于此时间的残留 activity | ✅ |
| **O4** | **场景**：硬编码 `DateTimeOffset.UtcNow` → 测试无法控制时间。**问题**：时间确定性缺失。**后果**：超时/重试测试不可靠 | `TimeProvider` 注入（conventions §10.4 + PDDD-G17 强制）；`FakeTimeProvider` 测试工具 | ✅ |

---

## 九、DDD 项目实战新增

> 来自 ITM 缺陷历史 + ADR 决策记录的 DDD 项目独有踩坑。

| # | 教训 | 来源 |
|:--:|------|------|
| **PD1** | OperationCanceledException 过滤 3 处同型复发（PeriodicBackgroundProcessor + ExceptionMiddleware + HealthCheck） → 沉淀为 conventions §10.3 强制规则 | ITM-030 + 批次4 + OBS-064 |
| **PD2** | OutboxDomainEventInterceptor 生命周期未断言 → 持有 `_pending` 字段不能 Singleton → 架构测试强制 Scoped | ITM-026 |
| **PD3** | SagaKey `|` 分隔符隐式契约 → 第三方状态名含 `|` 静默冲突 → 运行时校验 | ITM-001 |
| **PD4** | Outbox 死信无重投递入口 → ops 直写库越权 → 统一入口 RequeueDeadAsync（幂等前提是调用方责任） | ITM-002 + ADR-011 |
| **PD5** | Inbox SQLite TOCTOU 窗口 → INSERT OR IGNORE + SELECT 两步竞态 → PG ON CONFLICT RETURNING 单语句消除 | ITM-003 |
| **PD6** | Dispatcher.Register 冻结后非线程安全 → Freeze() 转 FrozenDictionary 后禁运行时 Add | ITM-027 |
| **PD7** | TUnit/MTP `-e` 环境变量误判 → global.json runner 配置问题，`-e` 多余 → 诊断三步骤 S3 反向验证 | conventions §14 |
| **PD8** | DIM 类型级契约误判（S2326 IRequest/ICommand / S3246 IEventHandler\<TEvent\> 逆变）→ SuppressMessage 带 Justification | git 3b26afd / fa1cbf8 |

---

## 统计

| 章节 | 条数 | 来源 |
|------|:----:|------|
| 事件驱动 & 最终一致性 | 18 | ORM Phase 19/22 + DDD 实战 |
| Async & Task | 8 | ORM Phase 17（DDD 适用部分） |
| AOT & 反射 | 7 | DDD 项目独有 AOT 分层 |
| 消息序列化 & 演化 | 5 | DDD 项目独有 |
| 并发 & 锁 | 5 | ORM Phase 14/19（DDD 适用） |
| 安全 & 审计 | 4 | ORM Phase 6/21（DDD 适用） |
| DDD/Clean Architecture | 7 | DDD 项目独有（architecture.md 18 决策） |
| 诊断 & 可观测性 | 4 | ORM Phase 16/22（DDD 适用） |
| DDD 实战新增 | 8 | ITM 历史 + ADR |
| **合计** | **66** | — |

---

## 维护规则

1. **新坑追加**：新发现的 DDD 踩坑追加到对应章节，附 ITM 编号或 ADR 引用。
2. **不复制 ORM 经验**：ORM 302 项中与 DDD 无关的（QueryBuilder/ChangeTracker/Schema 迁移/PG/MySQL/SQLite 方言专有/批处理）不纳入。
3. **每条带状态**：✅ 已修复 / ⚠️ 部分实现 / 🚫 架构层面避开。
4. **真源单一**：架构决策见 `architecture.md`，决策依据见 `decisions/NNN-*.md`，缺陷历史见 `docs/review/action-items-*.md`。
5. **本文件与 conventions.md §10 同步**：API 约束变更需同时更新两处。

---

## 十、PalORM 适配层踩坑（2026-07-28）

> 17 次提交、96 测试（SQLite + PG + MySQL 三方言全绿）的实施过程中发现的真实问题。适用于任何源生成 ORM 适配 / 多方言 SQL / AOT 验证场景。

### 源生成器约束（编译期发现）

| # | 踩坑 | 根因 | 修复 | 状态 |
|---|------|------|------|:---:|
| PALORM-SG1 | PalORM.SourceGen analyzer 不触发 Row DTO 生成 | Provider 包用 `exclude="Build,Analyzers"` 引用 Core，不传递 SourceGen | 消费项目显式 `<PackageReference Include="PalORM.SourceGen">` | ✅ |
| PALORM-SG2 | `byte[]` 属性被 PALORM016 拒绝 | byte[] 不在白名单（IArrayTypeSymbol 拒绝） | `[Converter(typeof(ByteArrayBase64Converter))]` 转 Base64 string | ✅ |
| PALORM-SG3 | `[ConcurrencyCheck]` DateTimeOffset 属性被 PALORM012 拒绝 | 源生成器 emit `++` 自增，仅支持 int/long | Inbox 改用 `Attempts`（int 计数器）替代 `ProcessingStartedAt`（DateTimeOffset） | ✅ |
| PALORM-SG4 | 复合主键实体被 PALORM019 拒绝 | 源生成器 BindDelete 单 key 语义无法表达 | Projection/Idempotency 不注册实体，全程 `GetRawConnection()` + `DbDataReader` 手动映射 | ✅ |
| PALORM-SG5 | `[Key]` 非 int/long 属性 PALORM022 报错 | Ulid 主键不支持自增回填 | `[Key(AutoIncrement = false)]` 显式声明应用层赋值 | ✅ |

### 运行时约束（运行时发现）

| # | 踩坑 | 根因 | 修复 | 状态 |
|---|------|------|------|:---:|
| PALORM-RT1 | `SELECT *` 列序错位（error 混入 created_at 位） | ColumnOrderValidator 按 DTO 属性声明序映射，DDL 列序不同 | 显式列名 `SELECT id, type, ...` 按 DTO 声明序排列 | ✅ |
| PALORM-RT2 | 未注册实体 `QueryFirstAsync<T>` 返回空对象（不抛异常） | QueryFirstAsync 对未注册类型走默认构造 | 复合主键表改用 `GetRawConnection().CreateCommand()` + `DbDataReader` 手动映射 | ✅ |
| PALORM-RT3 | Commit/Rollback 后 `ObjectDisposedException` | DataSession 内部 OperationState 仍持有已释放事务引用 | `CommitAsync`/`RollbackAsync` 后显式 `session.UseTransaction(null)` | ✅ |
| PALORM-RT4 | 同一 DataSession 多 worker 并发抛"already has active operation" | AsyncLocal 门禁禁止重叠 await | 并发测试每 worker 创建独立 DataSession（共享文件型 SQLite） | ✅ |
| PALORM-RT5 | `QueryFirstAsync<long?>` 编译失败 | 泛型约束 `T : class`，不接受值类型 | 标量查询用 `ScalarAsync<T>` 替代 | ✅ |

### MySQL 方言特有约束

| # | 踩坑 | 根因 | 修复 | 状态 |
|---|------|------|------|:---:|
| MYSQL1 | `INSERT INTO t (key, ...) VALUES (...)` 语法错误 | `key` 是 MySQL 保留字 | 列名改为 `idempotency_key`（从根源消除，不用反引号转义） | ✅ |
| MYSQL2 | `UPDATE...WHERE id IN (SELECT...LIMIT n)` 报错 | MySQL 不支持 IN 谓词内嵌 LIMIT 子查询 | 改用 `UPDATE t JOIN (SELECT id FROM ... LIMIT n) AS sub ON t.id = sub.id SET ...` | ✅ |
| MYSQL3 | PG 的 `"Events"`（PascalCase 带引号）与手写 SQL 不匹配 | PG 折叠无引号标识符为小写；PalORM 手写 SQL 的 FormattableString 不加引号 | EventLog 列名从 PascalCase 统一改为 snake_case（三方言一致） | ✅ |

### C# 语言约束

| # | 踩坑 | 根因 | 修复 | 状态 |
|---|------|------|------|:---:|
| CSHARP1 | `$"SELECT " + const + $" FROM ..."` 编译失败 | C# 把 `$"" + $""` 推断为 string 而非 FormattableString | 所有 SQL 写在单一 `$"..."` 字面量内 | ✅ |
| CSHARP2 | `JsonTypeInfo<T>` 找不到命名空间 | 类型在 `System.Text.Json.Serialization.Metadata` 子命名空间 | 显式 `using System.Text.Json.Serialization.Metadata;` | ✅ |

### 安全教训

| # | 踩坑 | 根因 | 修复 | 状态 |
|---|------|------|------|:---:|
| SEC1 | 数据库连接串（含真实 IP + 用户名密码）硬编码到测试代码 | 快速验证时直接写入了远程数据库连接串默认值 | 改用 `Environment.GetEnvironmentVariable` + 缺失时 throw；git 历史 filter-branch 清除 + GC prune | ✅ |
