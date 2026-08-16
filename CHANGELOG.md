# Pal.DDD 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。

> **当前版本**：`VersionPrefix=1.1.0` / `VersionSuffix=`（空——见 `Directory.Build.props`）
> **发布规范**：见 [`docs/release.md`](docs/release.md)

---

## [Unreleased]

### 新增

- **`docs/testing.md`**：测试体系完整规范（金字塔/场景矩阵/BenchmarkDotNet 配置/统计判据/源生成器规则/CI 触发规则）
- **`docs/release.md`**：NuGet 发布规范 SOP（版本管理/包范围/分支流程/发布前验证/触发发布/回滚/版本模板）
- **`docs/pitfalls.md`**：DDD 适用踩坑目录（66 条，从 ORM 302 项筛选 + DDD 实战新增）
- **`CHANGELOG.md`**：本文件，从无到有建立变更日志规范

---

## [1.0.0-preview.1] — 2026-07-08

> 首次预览版。面向 .NET 11 的 DDD/CQRS/Event Sourcing 基础设施框架。
> 30 个独立 NuGet 包，覆盖 Entity/AggregateRoot/DomainEvent/Saga/Outbox/Inbox/Projection/EventLog 完整 DDD 战术模式。

### 核心能力

- **DDD 战术模式**：Entity\<TId\> / AggregateRoot\<TId\> / ValueObject\<T\> / DomainEvent / SmartEnum / Specification
- **CQRS**：CommandHandler / QueryHandler / PipelineStateMachine（零分配快速路径，~40B/请求）
- **Event Sourcing**：IEventLog / RecordedEvent（双构造路径，写入防御拷贝 + 读取零拷贝）
- **Saga 编排**：补偿链 + 租约锁（防多实例重复补偿）+ 超时检测（有界批量扫描）
- **Outbox 模式**：原子租约（PG FOR UPDATE SKIP LOCKED / SQL Server UPDLOCK+READPAST）+ 死信重投递（RequeueDeadAsync）
- **Inbox 幂等**：UNIQUE(message_id) 约束 + PG ON CONFLICT RETURNING 单语句
- **Projection**：断点续传 + EventLog 投影源
- **多 Broker**：IMessageBroker 抽象 + InMemory/Kafka/RabbitMQ 三实现（对称行为）
- **双持久化**：Dapper（AOT 兼容）+ EF Core（功能完整）
- **序列化**：JsonMessageSerializer（ThreadStatic 池化）+ MemoryPack 适配 + MessageEvolutionPipeline
- **战略 DDD 编译期治理**：PDDD001-015（15 条 Roslyn 分析器规则）+ 4 个 CodeFix

### 包清单（30 个公开发布包）

| 层 | 包 | 数量 |
|----|----|:----:|
| Domain | PalDDD.Core | 1 |
| App-Abstractions | PalDDD.Serialization / Serialization.Evolution | 2 |
| App-Core | PalDDD.CQRS / EventLog / Transactions / Idempotency / Projections / Messaging / Compression | 7 |
| Infra-EFCore | PalDDD.EntityFrameworkCore / Repository.EFCore / Transactions.EFCore / EventLog.EFCore / Idempotency.EFCore / Projections.EFCore | 6 |
| Infra-Dapper | PalDDD.Dapper / Dapper.PostgreSql / Dapper.Sqlite / Dapper.MySql | 4 |
| Infra-Messaging | PalDDD.Messaging.Kafka / Messaging.RabbitMQ | 2 |
| Infra-Other | PalDDD.Compression.Native / Serialization.MemoryPack / Projections.EventLog | 3 |
| Hosting | PalDDD.Hosting.AspNetCore / DependencyInjection | 2 |
| Analyzers | PalDDD.Analyzers / Analyzers.CodeFixes | 2 |
| Metapackages | PalDDD.Base / Extension / Prompts | 3 |

### 工程基线

- **AOT 分层**：核心层 7 项目 `IsAotCompatible=true`（Core/Serialization/CQRS/EventLog/Idempotency/Projections/Messaging），适配器层 14 项目显式 `false`（EF Core/Kafka/RabbitMQ/MemoryPack/Transactions 等）
- **零反射红线**：MakeGenericType/Activator.CreateInstance/Assembly.GetTypes/Type.GetType(string) 全禁（ArchitectureBoundaryTests 33 方法机械守护）
- **测试框架**：TUnit 1.58.0 + MTP（禁 Microsoft.NET.Test.Sdk）
- **质量保障**：TreatWarningsAsErrors + AnalysisLevel=latest-all + 21 条 NoWarn 逐条 Justification + coverlet Cobertura 覆盖率门禁 + Stryker 突变测试（high=80/low=60/break=50）
- **规范文档**：conventions.md（1000 行 14 章）+ architecture.md（18 决策）+ 16 ADR + aot.md + performance.md + tutorial.md

### 已知限制

- **BenchmarkDotNet 0.15.8 不支持 .NET 11 Preview**：正式 BDN 报告不可生成，用 `--smoke` 模式（100 万次迭代手动计时）作为快速回归
- **`PalDDD.Transactions` 项目非 AOT 兼容**：Saga 子系统用 MakeGenericMethod/Activator（已带 [RequiresDynamicCode] 标注），主动声明 IsAotCompatible=false
- **Inbox SQLite TOCTOU 弱保证**：SQLite Inbox 用 INSERT OR IGNORE + SELECT 两步有极小竞态，生产推荐 PostgreSQL
- **`PalDDD.Core.SourceGen` 待修复**：`ISpecification.cs:218` 的 `_expression.Compile()` 违反 AOT 红线（gate-check PDDD-G8 已发现）
- **`Idempotency/Projections` 部分文件 ConfigureAwait 缺失**：12 处违规（gate-check PDDD-G12 已发现）

---

## 版本号约定

| 版本段 | 何时升级 | 示例 |
|--------|---------|------|
| Major（1.x.x） | 破坏性 API 变更 | 1.0.0 → 2.0.0 |
| Minor（x.1.x） | 新增功能、向后兼容 | 1.0.0 → 1.1.0 |
| Patch（x.x.1） | bug 修复 | 1.0.0 → 1.0.1 |
| Preview（VersionSuffix） | 预发布 | preview.1 → preview.2 |

详见 [`docs/release.md`](docs/release.md) §1.2 版本号语义。

---

## 维护规则

1. **未发布版本放 `[Unreleased]` 段**：所有未发布变更先追加到此处，发布时改为版本号。
2. **变更分类**：`### 新增` / `### 变更` / `### 修复` / `### 移除` / `### 破坏性变更` / `### 安全`（[Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范）。
3. **每条带 PR 或 commit 引用**：可追溯。
4. **发布时同步**：升版本同一次提交内同步 `Directory.Build.props` + README badge + tag + 本文件。
5. **GitHub Release body 来自本文件**：release.yml 自动读取对应版本段落作为 Release 说明。
