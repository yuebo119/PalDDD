# Pal.DDD 评审改进任务清单 v2（2026-06-29）

> 来源：`docs/review/serena-comprehensive-review-2026-06-29-v4.md`（Serena LSP 符号分析 + 源码扫描）  
> 前序：`docs/review/action-items-2026-06-29.md`（24 项已全部落地）  
> 用法：每项含 ID / 优先级 / 维度 / 问题 / 建议 / 风险 / 验证 / 涉及文件。完成后将 `[ ]` 改为 `[x]` 并填完成日期与 commit。  
> 优先级：**P1** 推荐近期修复 · **P2** 中期增强 · **P3** 长期演化/评估  
> 立场：每项均标注风险与取舍，避免投机抽象；"评估类"任务不要求必做，只要求给出结论 ADR。

---

## 事实核查修正

> 评审报告 v4 中两项 P2 建议经源码核查后修正：

- **`InMemoryOutboxStore.RequeueDeadAsync` 并发语义**：经核查，完整状态转换（Status/ProcessedAt/Error/NextAttemptAt/LockedBy/LockedUntil）**已在 `lock (_lock)` 内完成**，评审报告 v4 的 P2 建议为误判，已移除。
- **`PeriodicBackgroundProcessor` 异常隔离**：经核查，已正确过滤关停场景的 `OperationCanceledException`（`when (stoppingToken.IsCancellationRequested)`）。但内层 `catch (Exception ex) { OnTickFailed(ex); }` 会捕获**非关停的 `OperationCanceledException`**（如下游 ct 取消但 host 未关停），降级为 P3。

---

## P1 — 推荐近期修复

### [x] ITM-025 · EventLog/Core 长尾英文 summary 残留统一中文 · commit 3a9bf9e
- **维度**：可读性 / 一致性
- **问题**：前序 ITM-010 已统一主要 Justification 与显眼公共 API 类头 summary，但 EventLog/Core 仍有 **48 处**英文 `<summary>` 残留（属性/方法级），如 `RecordedEvent.StreamName` 的 `/// <summary>Stream that owns the event.</summary>`。全局规范已收敛为中文 summary 为主。
- **建议**：分 2-3 个 commit 批量统一 EventLog/Core 的属性/方法级 summary 为中文（保留诊断码 `CA1031` 等本身、保留技术方法签名英文描述）
- **风险**：低（注释级，无行为变更）
- **验证**：`grep -rn '/// <summary>[A-Z][a-z]\+ [a-z]' src/PalDDD.EventLog/ src/PalDDD.Core/` 残留为 0；`dotnet build` 0 错误 0 警告
- **涉及文件**：
  - `src/PalDDD.EventLog/RecordedEvent.cs`（~12 处）
  - `src/PalDDD.EventLog/EventData.cs`（~8 处）
  - `src/PalDDD.EventLog/EventAuditMetadata.cs`（~4 处）
  - `src/PalDDD.EventLog/ExpectedStreamVersion.cs`（~6 处）
  - `src/PalDDD.EventLog/EventStreamConcurrencyException.cs`（~5 处）
  - `src/PalDDD.EventLog/IEventLog.cs`（~3 处）
  - `src/PalDDD.EventLog/InMemoryEventLog.cs`（~1 处）
  - `src/PalDDD.EventLog/AppendEventsResult.cs`（~1 处）
  - `src/PalDDD.Core/IUnitOfWork.cs`（~4 处）

---

## P2 — 中期增强

### [x] ITM-026 · `OutboxDomainEventInterceptor` 生命周期断言文档化 · commit 3a9bf9e
- **维度**：健壮性 / 可维护性
- **问题**：`OutboxDomainEventInterceptor` 持有实例字段 `_pending`（当前 SaveChanges 操作收集的领域事件列表），XML doc 已明确"必须注册为 Scoped"，但架构边界测试未显式断言此注册方式。若未来误改为 Singleton，`_pending` 会被并发请求交叉写入。
- **建议**：
  - 方案 A（推荐）：在 `ArchitectureBoundaryTests.cs` 新增断言，扫描 `ServiceCollectionExtensions.cs` 确认 `OutboxDomainEventInterceptor` 使用 `TryAddScoped` 注册
  - 方案 B：在 `OutboxDomainEventInterceptor` XML doc 增强"Singleton 会导致 `_pending` 并发污染"的警告（已有但可扩充）
- **风险**：低（方案 A 为新增测试断言；方案 B 为文档级）
- **验证**：架构边界测试新增断言通过；`grep "TryAddScoped<OutboxDomainEventInterceptor>"` 命中
- **涉及文件**：`test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`、`src/PalDDD.Repository.EFCore/ServiceCollectionExtensions.cs`

### [x] ITM-027 · `Dispatcher.Register` 冻结后注册的线程安全文档化 · commit 3a9bf9e
- **维度**：健壮性 / 可读性
- **问题**：`Dispatcher.Register` 在冻结后（`_frozen is not null`）抛 `ObjectDisposedException`，但 XML doc 未显式说明"注册必须在 `Freeze()` 调用前完成，且 `Register` 非线程安全（启动期单线程调用）"。若未来在运行时并发注册会破坏 `_entries` 字典。
- **建议**：在 `Dispatcher.Register` XML doc 补充"启动期单线程调用约束"说明
- **风险**：低（文档级）
- **验证**：`Dispatcher.Register` XML doc 含"启动期单线程调用"约束说明
- **涉及文件**：`src/PalDDD.CQRS/Dispatcher.cs`

### [x] ITM-028 · `Saga.ProcessEventAsync` 补偿失败的可观测性增强 · commit 3a9bf9e
- **维度**：可维护性 / 健壮性
- **问题**：`Saga.ProcessEventAsync` 在所有重试耗尽后执行 `CompensateExecutedStepsAsync`，但补偿本身的失败仅通过 `AggregateException` 抛出，未记录到 `PalMetrics` 或 `PalActivitySource`。生产场景难以通过指标监控补偿失败率。
- **建议**：在 `CompensateExecutedStepsAsync` 调用前后增加 `PalMetrics.SagaCompensationFailed.Add(1)` 计数与 Activity tag（`pal.saga.compensation.failed`）
- **风险**：低（新增可观测性指标，无行为变更）
- **验证**：`SagaTests` 新增补偿失败指标计数断言；`PalMetrics` 含 `SagaCompensationFailed` 计数器
- **涉及文件**：`src/PalDDD.Transactions/Saga.cs`、`src/PalDDD.Core/PalMetrics.cs`（或对应位置）

### [x] ITM-029 · `DapperOutboxStore` 连接生命周期文档化 · commit 3a9bf9e
- **维度**：可维护性 / 健壮性
- **问题**：`DapperOutboxStore` 的 `EnsureOpen`/`EnsureOpenAsync` 注释已说明"连接生命周期由 DI 容器管理的 Scoped DbConnection 控制"，但未在 XML doc 显式声明"调用方不负责关闭连接"。第三方使用者可能误以为需在 using 块中释放。
- **建议**：在 `DapperOutboxStore` 类级 XML doc 补充"连接生命周期由 DI 容器管理，调用方不应关闭或释放连接"说明
- **风险**：低（文档级）
- **验证**：`DapperOutboxStore` 类级 XML doc 含连接生命周期说明
- **涉及文件**：`src/PalDDD.Dapper/DapperOutboxStore.cs`

---

## P3 — 长期演化 / 评估

### [x] ITM-030 · `PeriodicBackgroundProcessor` 非关停取消异常过滤 · commit da287f7
- **维度**：健壮性
- **问题**：`PeriodicBackgroundProcessor.ExecuteAsync` 内层 `catch (Exception ex) { OnTickFailed(ex); }` 会捕获**非关停的 `OperationCanceledException`**（如下游 handler 传递了已取消的 ct 但 `stoppingToken` 未取消），将其作为错误记录到 `OnTickFailed`。虽然不影响循环继续，但会产生误报错误日志。
- **建议**：将内层 catch 改为 `catch (Exception ex) when (ex is not OperationCanceledException) { OnTickFailed(ex); }`，或在 `OnTickFailed` 内部过滤
- **风险**：低（行为变更仅影响非关停取消异常的日志记录）
- **验证**：`OutboxProcessorTests` 新增非关停取消异常不触发 `OnTickFailed` 的断言
- **涉及文件**：`src/PalDDD.Transactions/PeriodicBackgroundProcessor.cs`
- **完成**：内层 catch 新增 `catch (OperationCanceledException) { /* 静默忽略 */ }` 分支，区分关停取消与下游取消

### [x] ITM-031 · `Saga` 独立拆分为 `PalDDD.Saga` 包评估 · ADR-010 结论不拆
- **维度**：可复用性
- **问题**：`PalDDD.Transactions` 依赖 `Core` + `Messaging` + `Serialization`，使用者即使只用 Saga 也要引入 Messaging。
- **建议**：评估将 Saga 拆分到独立包（`PalDDD.Saga`），但需权衡"细粒度 vs 项目数"（当前 31 项目已较多）。ADR-010 已论证不拆，若有明确需求重新评估。
- **风险**：中（项目结构变更，影响 NuGet 打包与 DI 注册）
- **验证**：ADR 论证；如采纳则 build/test + DI 注册测试
- **涉及文件**：`src/PalDDD.Transactions/`（评估）
- **完成**：ADR-010 结论「不拆」——31 项目已较多，Saga 与 Outbox/Inbox 事务语义强耦合，拆分增加 DI 注册复杂度，YAGNI

### [x] ITM-032 · SQL Server Dapper 适配器评估 · ADR-010 结论延后
- **维度**：兼容性
- **问题**：EFCore Outbox 有 `SqlServerOutboxDbContext`，但 Dapper 适配器缺少 SQL Server 方言。文档"三数据库支持"描述应明确 SQL Server 的覆盖范围（仅 EFCore Outbox）。ADR-010 已评估延后。
- **建议**：
  1. 短期：在 README/`docs/architecture.md` 明确 SQL Server 当前仅 EFCore Outbox 覆盖
  2. 长期：评估需求后补充 `PalDDD.Dapper.SqlServer`
- **风险**：中（新增项目，需 SQL Server 测试环境）
- **验证**：文档明确；若新增项目则三数据库对齐测试
- **涉及文件**：`docs/architecture.md`、（评估新增）`src/PalDDD.Dapper.SqlServer/`
- **完成**：ADR-010 结论「延后」——SQL Server 当前仅 EFCore Outbox 覆盖，Dapper SqlServer 适配器延后至明确需求出现

### [x] ITM-033 · `IMessageBroker.SubscribeAsync` 细粒度订阅评估 · ADR-010 结论不暴露
- **维度**：可扩展性
- **问题**：`SubscribeAsync<TMessage>` 只能按消息类型订阅，不支持按 topic/routing key 细粒度订阅（架构测试断言 `EventFilter.cs` 不存在，有意简化）。ADR-010 已评估不暴露。
- **建议**：评估是否需要补充细粒度订阅重载，或在 ADR 明确"按类型订阅"边界条件。若有明确需求重新评估。
- **风险**：中（新增 API 需跨 Kafka/RabbitMQ/Null 三实现）
- **验证**：ADR 论证；如采纳则三 Broker 实现对齐测试
- **涉及文件**：`src/PalDDD.Messaging/IMessageBroker.cs`
- **完成**：ADR-010 结论「不暴露细粒度订阅重载」——按类型订阅是跨三实现最小公共子集，高级路由由应用层 handler 分发，约定 > 配置

### [x] ITM-034 · `ValueObject<T>` 非数值场景评估 · ADR-010 结论不新增基类
- **维度**：灵活性 / 可复用性
- **问题**：`ValueObject<T>` 约束 `INumber<T>, IMinMaxValue<T>`，排除 string/GUID 等非数值载体。非数值值对象需自行实现 `IValueObject`。ADR-010 已评估不新增 `StringValueObject` 基类。
- **建议**：在 `ValueObject.cs` XML doc 增强非数值示例（已有但可扩充）。若有明确需求重新评估提供 `StringValueObject` 基类。
- **风险**：中（新增基类需 ADR 论证）
- **验证**：文档明确；若新增基类则单测覆盖
- **涉及文件**：`src/PalDDD.Core/ValueObject.cs`
- **完成**：ADR-010 结论「维持现状」——非数值值对象直接实现 IValueObject 接口已足够，YAGNI

### [x] ITM-035 · `RecordedEvent` 属性级 summary 中文化（与 ITM-025 合并）· commit 76b7c45
- **维度**：可读性
- **问题**：`RecordedEvent` 的 11 个属性 summary 均为英文（如 `StreamName`/`StreamVersion`/`GlobalPosition` 等），是 ITM-025 的子集。
- **建议**：与 ITM-025 合并处理，分批统一为中文
- **风险**：低（注释级）
- **验证**：`grep '/// <summary>[A-Z]' src/PalDDD.EventLog/RecordedEvent.cs` 残留为 0
- **涉及文件**：`src/PalDDD.EventLog/RecordedEvent.cs`
- **完成**：已在 ITM-025 第一批处理，RecordedEvent 的 12 处属性/方法/类级 summary 全部中文化

---

## 汇总

| 优先级 | 数量 | 类型分布 |
|--------|------|----------|
| P1 | 1 | 文档化 1 |
| P2 | 4 | 文档化 2 + 可观测性增强 1 + 测试断言 1 |
| P3 | 6 | 评估 4 + 增强修复 1 + 文档化 1（与 P1 合并） |
| **合计** | **11** | 文档化 4 / 评估 4 / 增强 2 / 测试 1 |

**推进建议**：
1. P1（ITM-025）可在 2-3 个 commit 内完成（低风险、批量注释统一）
2. P2 按"文档化 → 测试断言 → 可观测性增强"顺序推进，每项独立 commit
3. P3 评估类任务已有 ADR-010 结论，仅在明确需求出现时重新评估

---

## 与前序 action-items 的关系

| 前序（已全部落地） | 本批次 |
|--------------------|--------|
| `action-items-2026-06-29.md`（24 项） | `action-items-2026-06-29-v2.md`（11 项） |
| ITM-001 至 ITM-024 ✅ | ITM-025 至 ITM-035 |

本批次基于 Serena LSP 符号分析 + 源码扫描的深度评审，聚焦前序未覆盖的细节问题（长尾 summary 残留、生命周期断言、可观测性增强、非关停取消异常过滤）。
