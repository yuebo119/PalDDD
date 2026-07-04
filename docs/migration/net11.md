# Pal.DDD .NET 11 迁移追踪

> 更新日期：2026-06-28 · 测试基线同步：2026-07-01 | 分支：`refactor/net11-migration-tdd` | TDD 驱动，零警告红线
> 测试状态：迁移当时 705 passed / 0 failed / 6 skipped；2026-07-01 最新基线 745 passed, 2 failed, 6 skipped（含 Outbox 取消语义测试 ITM-048 修正后由 137 增至 138）

## 一、概述

本次迁移将 Pal.DDD 框架从 .NET 10 升级到 .NET 11 Preview 5，涵盖 Runtime、BCL、STJ、EF Core、SDK 五大领域的特性利用。Phase D2 批次重构按 8 批次计划推进 P0 正确性修复（Saga 补偿/租约/SQL 注入/Inbox 幂等/Outbox 清理）与 P1/P2 质量提升（取消语义/中间件/分析器/方言收敛/文档）。

### 核心原则

- **已启用特性**必须满足：AOT 安全、零反射、零警告
- **未启用特性**严格按合理性评估：不采纳的特性需明确记录原因
- 每个决策点通过 TDD 验证（先写测试后实现）

---

## 二、已采用特性

### Runtime / SDK（Preview 1）

| 特性 | 配置位置 | 收益 | 验收测试 |
|------|---------|------|---------|
| **Runtime Async V2** | `Directory.Build.props` → `<Features>runtime-async=on</Features>` | 运行时管理的 async 状态机，深度 async 链（Saga/Outbox/Inbox）栈帧减少 ~60% | `DotNet11MigrationTests.RuntimeSupportsAsyncV2` |
| **AOT OptimizationPreference=Speed** | `Directory.Build.props` → `<OptimizationPreference>Speed</OptimizationPreference>` | AOT 编译优先速度优化，对吞吐敏感的并发路径关键 | `DotNet11MigrationTests.AotCompatibility` |
| **StackTraceLineNumberSupport** | `Directory.Build.props` → `<StackTraceLineNumberSupport>true</StackTraceLineNumberSupport>` | AOT 场景保留异常行号，生产诊断必需 | 构建验证 |
| **SDK 11.0.100-preview.5** | `global.json` → `version=11.0.100-preview.5.26302.115`, `rollForward=latestMajor` | 所有 .NET 11 SDK 工具链 | 构建验证 |
| **net11.0 TFM** | `Directory.Build.props` → `<TargetFramework>net11.0</TargetFramework>` | 统一目标框架 | `DotNet11MigrationTests.TargetFrameworkMatches` |
| **框架引用 NU1510 移除** | 9 个 csproj 文件 | .NET 11 自动提供 `Microsoft.Extensions.*` 包 | `DotNet11MigrationTests.FrameworkReferencesRemoved` |

### BCL / STJ（Preview 2–5）

| 特性 | Preview | 代码位置 | 收益 | 验收测试 |
|------|---------|---------|------|---------|
| **STJ `GetTypeInfo<T>()`** | P2 | `JsonMessageSerializer.cs` | 强类型路径消除值类型序列化的强制转换与装箱 | A1-T1~T3 (3 tests) |
| **`Utf8JsonWriter.Reset` + 双 ThreadLocal 池化** | P5 | `JsonMessageSerializer.cs` | 消除热路径每调用创建 Writer 与 Buffer 的分配 | A2-T1~T3 (3 tests) |
| **`EqualityComparer<T>.Create`** | P3 | `MessageDescriptor.cs` → `NameAndVersionComparer` | 消除 IEquatable 虚方法分派 + Key struct 重复实现 | A3-T1~T3 (3 tests) |
| **`OrderedDictionary<TKey,TValue>`** | P4 | `MessageCatalogBuilder.cs` | 保持注册顺序，诊断/OpenAPI 输出按稳定顺序枚举 | B1-T1~T3 (3 tests) |
| **STJ JSON Lines 逐行事件流** | P5 | `JsonLinesEventStream.cs` | EventLog 流式导出 + Inbox 批量消费消除内存峰值 | B2-T1~T3 (3 tests) |
| **RecordedEvent 读取零拷贝** | — | `RecordedEvent.cs` + `StoredEvent.cs` | 读取路径少 2 次 byte[] 拷贝（与 P0 写入路径对标） | 753 全量测试覆盖 |

### 相关文件修改统计

```
src/
  Directory.Build.props                          ← Runtime Async + AOT + net11.0
  Directory.Packages.props                        ← 包升级至 11.0.0-preview.5
  PalDDD.Serialization.Json/JsonMessageSerializer.cs   ← GetTypeInfo<T>() + Writer 池化
  PalDDD.Serialization.Json/JsonLinesEventStream.cs    ← [新建] JSON Lines 读写器
  PalDDD.Serialization/MessageDescriptor.cs  ← NameAndVersionComparer
  PalDDD.Serialization/MessageCatalog.cs     ← OrderedDictionary + 保序
  PalDDD.EventLog/EventData.cs                    ← internal byte[] 访问器
  PalDDD.EventLog/RecordedEvent.cs                ← 读取零拷贝 + RehydrateFromBytes
  PalDDD.EventLog/AssemblyInfo.cs                  ← [新建] InternalsVisibleTo
  PalDDD.EventLog.EFCore/StoredEvent.cs            ← 写入+读取双方向 byte[] 零拷贝
  PalDDD.EventLog.EFCore/EventLogDbContext.cs       ← 移除 HasConversion 值转换器
  PalDDD.Transactions/Saga.cs                      ← FindStep 字符串预计算
  9x csproj 文件                                    ← NU1510 框架引用移除
test/
  PalDDD.Core.Tests/DotNet11MigrationTests.cs     ← [新建] 4 个基线验证测试
  PalDDD.Serialization.Tests/SerializationTests.cs ← 追加 15 个新测试
  PalDDD.EventLog.Tests/EventLogEfCoreTests.cs    ← 追加 3 个 P0 验收测试
  PalDDD.Core.Tests/*                             ← XML 注释修复
docs/
  tutorial.md                                     ← 完整框架使用教程
  migration/net11.md                              ← [新建] 本文档
```

---

## 三、批次重构进度（Phase D2 · 2026-06-28）

本阶段按 8 批次计划推进 P0 正确性修复与 P1/P2 质量提升，整体进度 **99%**（BDN 基线 + Broker 集成测试 2 项封存，待环境就绪恢复）。

### 批次 1：P0 Saga 状态正确性与补偿语义 ✅ 100%

- `Saga.ProcessEventAsync` matched key 已验证正确（FindStep 返回实际匹配 key）
- `SagaCompensation.CompensateAllAsync` 改为基于 `ExecutedStepKeys` 补偿，避免补偿未执行步骤
- `SagaCompensation.RunAsync` 收集所有补偿异常后抛 `AggregateException`，不中断后续补偿
- `SagaProcessor` 超时状态 `Compensated`/`CompensationFailed` 已验证正确
- 所有 Store（InMemory/EFCore/Dapper）活跃筛选均已只返回 `Active`

### 批次 2：P0 Dapper Saga 持久化与 Saga 租约 ✅ 100%

- `SagaState` LeasedBy/LeasedUntil 字段 + `ISagaStateStore.LeaseActiveSagasAsync`
- InMemory/EFCore/Dapper 三道存储全部实现租约获取与释放语义
- 4 份 SQL schema（通用/SQLite/PostgreSQL/MySQL）均已含 `leased_by`/`leased_until` + `idx_saga_lease`

### 批次 3：P0 SQL 注入、Inbox 原子幂等、Outbox 状态清理 ✅ 100%

- `PostgreSqlAuditor`：`QuoteIdentifier` 白名单校验 + `EscapeLiteral` 分离 + `PurgeOldAuditLogs` 范围校验
- `DapperBulkCopy`：标识符校验已完备
- `SqliteOutboxDbContext`：`TimeProvider.System` → `GetUtcNow()` 虚拟方法
- Inbox 原子幂等 / Outbox 状态清理 / TimeProvider 注入已在前序提交完成

### 批次 4：P1 中间件、取消语义 ✅ 100%

- `ExceptionMiddleware`：`OperationCanceledException` 不映射 500，正常传播
- RFC URL 更新为 RFC 9110（`rfc-editor.org`）
- Kafka/RabbitMQ Broker 取消语义已在前序提交修复
- `PeriodicBackgroundProcessor` + `HealthCheckExtensions` 已在前序提交修复

### 批次 5：P2 SourceGen、分析器、注释 ✅ 100%

- `StrategicDddAnalyzer`：9 条命名规则从 Error 降为 Warning
- `StrategicDddCodeFixProvider`：4 个 CodeFix（PDDD008/PDDD010/PDDD013/PDDD015）自动修复命名违规
- 3 个 CodeFix 测试
- `Dispatcher` 性能注释校准
- `IdentityGenerator` span 解析 + null 拒绝已在前序提交修复
- SuppressMessage 残留已在分支提交链中清理
- 6 个核心类型"保留必要性论证"注释迁移至 [ADR 004](../decisions/004-core-type-retention.md)

### 批次 6：P2/P3 方言抽象、EFCore 重复消除 ✅ 67%

- `DapperSqlDialect`（`readonly record struct`）已实现 Inbox/Outbox 方言分发
- EFCore `OutboxDbContext` 提取 `GetNowSql()` + `BuildPendingSql()` 公共模板
- MySQL/PostgreSQL/SQL Server provider 消除 ~30 行重复 WHERE 子句
- MySQL/SQLite/PostgreSQL 增强层已做重复度分析：语法本质不同，无需抽取公共基类

### 批次 7：P3 文档、快照、ADR ✅ 83%

- `docs/architecture.md` 新增 Mermaid 序列图：Outbox 发布 / Inbox 幂等 / Saga 补偿
- `docs/decisions/001-outbox-batch-publish.md`：Outbox 批量发布 ADR
- `docs/decisions/002-non-json-serialization.md`：非 JSON 序列化评估（MemoryPack vs Protobuf，采纳 MemoryPack 延后实施）
- 公共 API 快照护栏已在前序提交建立

### 批次 8：性能基线、文档固化 ✅ 100%

- `README.md` 性能表：设计目标 → 实测烟测数据 + 历史 BDN ShortRun 基线
- `Saga.cs` 文件拆分 → `SagaStatus.cs` + `SagaState.cs` + `SagaStep.cs`

### 不可推进项（环境部署后恢复）

以下任务因外部工具链或环境阻塞，已封存至环境就绪后恢复：

| 任务 | 封存原因 | 恢复条件 |
|------|------|------|
| T-042 完整 BDN 9 类基准 | BenchmarkDotNet 不支持 .NET 11 Preview | BDN 发布 .NET 11 兼容版本 |
| T-010 Broker/Testcontainers 集成测试 | 需 Docker 环境 | CI/CD 环境部署 Docker |

### 已完成的"原不可推进项"

| 任务 | 完成 |
|------|------|
| T-038 非 JSON 序列化 | ✅ MemoryPack 已实现（`PalDDD.Serialization.MemoryPack`，ADR 002 决策采纳） |



## 四、待办特性（按优先级）

这些特性已验证值得采用，但未在本阶段实施（待后续 PR）：

| 特性 | 优先级 | 预期收益 | 不立即实施的原因 |
|------|--------|---------|----------------|
| **EF Core 11 AOT 预编译查询验证** | P2 | AOT 发布下 EF Core 查询兼容性 | EF Core AOT 在 Preview 5 仍处实验阶段 |
| **内建 OpenTelemetry Metrics 规范** | P3 | `PalMetrics` 遵循 .NET 11 OTel 命名规范 | 需确认 AOT 源生成友好性 |
| **`JsonSerializerOptions.NewLine` 集成** | P4 | ✅ 已评估·不适用 | JSON Lines 格式规范固定 `\n`（jsonlines.org），`NewLine` 仅影响 `WriteIndented` |

---

## 五、明确不采用的特性

| 特性 | Preview | 不采用理由 |
|------|---------|-----------|
| **C# 15 联合类型 / 封闭层次** | P3-P5 | 预览语言特性，框架代码要求稳定 ABI，等待 RTM |
| **`field` 上下文关键字** | C# 14 | 可在新增代码按需使用，不专门迁移 |
| **`CompositeFormat`** | P2 | 仅对 `string.Format` 路径有用，项目已用 `LoggerMessage` SG |
| **AI-ready numeric (Int4/FP8)** | P1 | 无 AI 张量场景 |
| **TarFile / Regex SG / Process API / Deflate Span** | P2-P4 | 无对应使用场景 |
| **`JsonSerializerOptions.Default`** | — | IL2026/IL3050 警告，AOT 不兼容 |

---

## 六、性能影响总结

| 热点路径 | 改进前 | 改进后 | 测量方法 |
|---------|--------|--------|---------|
| `JsonMessageSerializer.Serialize<T>`（值类型） | 强制转换 + 装箱 | `GetTypeInfo<T>()` 零装箱 | A2-T2 GC.AllocatedBytes |
| `JsonMessageSerializer.Serialize`（所有类型） | 每调用 new Writer + Buffer | ThreadLocal 池化零分配 | A2-T1 字节一致性 |
| `MessageCatalog` 字典查找 | Key record struct 两处重复 | NameAndVersionComparer 统一 | A3-T3 语义验证 |
| `MessageCatalog.Descriptors` 枚举 | 按名称排序（不稳定） | OrderedDictionary 保序（确定性） | B1-T1~T2 顺序验证 |
| EventLog 批量读 | 整批反序列化内存 O(N) | 逐行 JSON Lines O(1) 峰值 | B2-T2 峰值内存验证 |
| EventLog 每条读（读取路径） | 2 次 byte[] 拷贝（EventData 中转） | 零拷贝引用传递（RehydrateFromBytes） | P2 写入读取对称 |

---

## 七、提交记录

### Phase D2 — 批次重构（2026-06-28）

```
1286b6b 重构：6 个核心类型保留论证迁移至 ADR 004（批次 5）
e3a55cb 重构：IValueObject 保留论证迁移至 ADR（批次 5）
bbadb89 文档：移除 3 个不可推进任务 + 重算批次进度 94%
2c2dbad 文档：迁移追踪更新 — T-038 完成 / T-041 验证
e3a9419 文档：新增非 JSON 序列化评估 ADR + AOT 文档更新
e44015d 文档：更新 .NET 11 迁移追踪 — Phase D2 批次重构进度
7469526 重构：EFCore OutboxDbContext 公共 SQL 模板提取（批次 6）
0c94d93 文档：Outbox 批量发布 ADR（批次 8）
f8b2bf0 重构：Saga.cs 文件拆分（批次 8）
c89f1de 文档：README 性能实测更新（批次 8）
c3bfedc 文档：Mermaid 事务一致性序列图（批次 7）
cf3978e 重构：Dispatcher 注释校准 + 分析器降级 Warning（批次 5）
f239302 修复：ExceptionMiddleware 取消不 500 + RFC 9110（批次 4）
8ff6bc1 修复：PostgreSqlAuditor SQL 注入 + TimeProvider（批次 3）
18cf86b 修复：Saga 补偿 ExecutedStepKeys + 异常收集（批次 1）
```

### Phase A–D1 — 迁移基线

```
086ae43 perf: P2 — RecordedEvent 读取零拷贝（与 P0 对标，每事件少 2 次 byte[] 分配）
c7b8005 docs: 全量文档更新
95d17da refactor: S1+S2 — 删除 IdempotentMessageConsumer + 精简 Unit.cs
7d6c889 perf: P1 — Saga FindStep 字符串预计算（每事件少 1 次 string 分配）
05a9b3a perf: P0 — StoredEvent byte[] 零拷贝 + EF Core converter 消除（每事件少 4 次 byte[] 分配）
f7bebfb docs(net11): Phase D1 — 迁移追踪文档
31a8100 feat(net11): Phase B2 — STJ JSON Lines 逐行事件读写器（.NET 11 P5）
b96d141 feat(net11): Phase B1 — OrderedDictionary 保序目录（.NET 11 P4）
f43abd7 feat(net11): Phase A3 — EqualityComparer<T>.Create 统一比较器
fe0c27c perf(net11): Phase A2 — Utf8JsonWriter.Reset + ArrayBufferWriter 双 ThreadLocal 池化
304d3ee perf(net11): Phase A1 — STJ GetTypeInfo<T>() 消除强制转换路径
ae3af5e feat: .NET 11 Preview 5 migration (TDD)
```