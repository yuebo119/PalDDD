# Pal.DDD 工程规范

> 本文档是 Pal.DDD 框架的唯一规范入口，系统化记录散落在 `.editorconfig`、`Directory.Build.props`、代码注释、ADR、AI 模板、架构测试中的隐式规范。
>
> 所有规范已在代码中强制执行（编译期诊断 + CI 门禁 + 架构边界测试），本文档是显式化记录，供新贡献者快速上手。

---

## 目录

1. [代码规范](#1-代码规范)
2. [注释规范](#2-注释规范)
3. [命名规范](#3-命名规范)
4. [工程规范](#4-工程规范)
5. [测试规范](#5-测试规范)
6. [文档规范](#6-文档规范)
7. [AI 提示模板规范](#7-ai-提示模板规范)
8. [DDD/Clean Architecture 约束](#8-dddclean-architecture-约束)
9. [Git 提交规范](#9-git-提交规范)
10. [AI Agent 编码约束](#10-ai-agent-编码约束)
11. [依赖注入规范](#11-依赖注入规范)
12. [性能契约](#12-性能契约)

---

## 1. 代码规范

### 1.1 目标框架

- **单目标 `net11.0`**（ADR-005），不多目标。原因：依赖 .NET 11 的 `static abstract`、`FrozenDictionary`、`ref struct` 枚举器等特性，多目标在技术上不可行。
- `global.json` 锁定 SDK 版本 + `rollForward: latestMajor` + `allowPrerelease: true`。

### 1.2 编译策略

| 配置项 | 值 | 含义 |
|--------|---|------|
| `Nullable` | `enable` | 全项目可空引用类型 |
| `ImplicitUsings` | `enable` | 隐式 using |
| `LangVersion` | `latest` | 最新 C# 语言版本 |
| `TreatWarningsAsErrors` | `true` | 警告即错误（零警告门禁） |
| `AnalysisLevel` | `latest-all` | 启用所有最新分析器规则 |
| `GenerateDocumentationFile` | `true` | 生成 XML 文档 |
| `EmbedAllSources` + `DebugType=embedded` | — | 源码嵌入 PDB |

### 1.3 AOT 硬约束

```xml
<IsAotCompatible>true</IsAotCompatible>
<IsTrimmable>true</IsTrimmable>
<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
```

EF Core / Kafka / RabbitMQ / MemoryPack 等不支持 AOT 的项目**显式覆盖为 `false`**，并在 ArchitectureBoundaryTests 中断言此覆盖。

### 1.4 零反射红线

以下 API 在生产代码中**零容忍**（ArchitectureBoundaryTests 通过源码扫描强制执行）：

- `MakeGenericType` — 用 DIM 桥接 + `typeof(T)` 编译时常量替代
- `Activator.CreateInstance` — 用显式 DI 注册替代
- `Assembly.GetTypes()` — 用源码生成器 `[ModuleInitializer]` 替代
- `Type.GetType(string)` — 用 `typeof(T)` 编译时常量替代

### 1.5 异步模式

- **`ValueTask` / `ValueTask<T>` 优先**于 `Task`，热路径零分配
- **`IsCompletedSuccessfully` 快速路径**：同步完成时直接 `.Result`，避免异步状态机分配
- **`ConfigureAwait(false)` 全层使用**（基础设施层 143+ 处，NoWarn CA2007）
- **禁止 `async void`**（ArchitectureBoundaryTests 零容忍）

### 1.6 null 校验

- 公共入口用 `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace`
- 内部调用链信任非 null，不重复检查
- NoWarn CA1062（公共 API null 验证），因为框架内部调用链已保证非 null

### 1.7 性能优先类型选择

| 场景 | 选择 | 原因 |
|------|------|------|
| 不可变字典 | `FrozenDictionary` | O(1) 查找，零 GC，AOT 安全 |
| 枚举器 | `ref struct` | 栈分配，零堆分配 |
| 编译时常量 | `static abstract` 接口成员 | AOT 安全，无反射 |
| 值对象 | `readonly record struct` | 栈分配，值相等性自动生成 |
| 后台轮询基类 | `PeriodicBackgroundProcessor` | 共享异常隔离 + 定时逻辑 |

### 1.8 NoWarn 抑制策略

每项 NoWarn 都有**逐条 Justification**（`Directory.Build.props` 第 11-38 行块注释）。不是「关掉警告」，是「已评估，有理由」。例如：

- `CA1031`（catch general exception）：后台处理器必须隔离任意异常保护批处理循环，每处带 `[SuppressMessage]` 具体理由
- `CA2007`（ConfigureAwait(false)）：库代码不绑定 SynchronizationContext，全层已显式 ConfigureAwait(false)
- `CS1591`（缺少 XML 文档）：仅针对 internal/private，公共 API 仍需文档

### 1.9 中央包管理

```xml
<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
```

所有 NuGet 版本集中在 `Directory.Packages.props`，csproj 中不写版本号。

---

## 2. 注释规范

### 2.1 文件头双分隔线模式

**强分隔（`═══` 双线）** — 用于核心/复杂文件：

```csharp
// ═══════════════════════════════════════════════════════════════
// 👑 Entity（实体）— DDD 核心抽象
// ═══════════════════════════════════════════════════════════════
//
// 💡 设计原则 / 怎么做 / 为什么：
//   ｜ <要点1>
//   ｜ <要点2>
// ═══════════════════════════════════════════════════════════════
```

**轻分隔（`───` 单线）** — 用于常规文件：

```csharp
// ─────────────────────────────────────────────────────────────
// 📤 OutboxPublisher — 租约模式 + 重试 + 死信的 Outbox 发布
// ─────────────────────────────────────────────────────────────
```

### 2.2 Emoji 语义化头标

| Emoji | 含义 | 示例 |
|:-----:|------|------|
| 👑 | 领域核心 | Entity, AggregateRoot, ValueObject |
| 📬 | 事件 | DomainEvent, EventLog |
| 📤 | 发布 | OutboxProcessor, MessageBroker |
| 📥 | 消费 | InboxProcessor |
| 🔄 | 编排 | Saga, MessageEvolutionPipeline |
| ⚙️ | 配置 | TransactionOptions |
| 🏷️ | 属性 | Attributes.cs |
| 🎯 | 路由 | Dispatcher |
| 🏗️ | DI | ServiceRegistration |
| ⏰ | 超时 | SagaTimeoutDetector |
| 📐 | 抽象 | IUnitOfWork, ISpecification |
| 🔬 | 测试 | *Tests.cs |
| 📦 | 序列化 | JsonMessageSerializer |
| 🧪 | 内存实现 | InMemoryEventLog |

### 2.3 💡 段落式注释三模式

**模式 1：设计原则（文件头 `｜` 缩进）**

```csharp
// 💡 Saga 是什么？
//   ｜ 一个跨多个步骤的业务流程编排器...
//   ｜
// 💡 步骤查找用 Dictionary 而非 List.Find：
//   ｜ List.Find 是 O(n)...Dictionary 是 O(1)...
```

**模式 2：通俗解释（XML doc `<para>`）**

```csharp
/// <summary>
/// 💡 通俗解释 —— 什么是值对象？
/// <para>值对象是没有独立身份的对象，它的"值"就是它的身份。</para>
/// </para>
/// </summary>
```

**模式 3：关键设计（单句 `<para>`）**

```csharp
/// <para>💡 性能优化：O(1) 追加，零堆分配。用 _tail 指针避免遍历。</para>
```

### 2.4 辅助标记

| 标记 | 用途 |
|------|------|
| 📐 设计决策 | 多点论证（如 DomainEvent.TimeProvider 为何 internal） |
| 📁 文件拆分 | 标注拆分关系（如 Saga 拆为 6 个文件） |
| ⚡ 性能提示 | 关键路径性能说明 |
| ⚠️ 约束/警告 | 运行时校验或限制说明 |

### 2.5 XML doc 语言策略

- **中文 summary 为主**：领域概念、设计决策、通俗解释
- **英文用于技术接口**：方法签名描述、参数说明（如 `IUnitOfWork.BeginTransactionAsync`）
- 公共 API 强制 `<summary>`（CS1591 仅抑制 internal/private）
- 使用 `<para>` + `<br/>` 多段、`<see cref="..."/>` 交叉引用、`<inheritdoc/>` 实现接口时

### 2.6 `[SuppressMessage]` 规范

一律带 `Justification` 英文说明抑制理由：

```csharp
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Outbox batch processor must isolate any exception to protect the polling loop.")]
```

---

## 3. 命名规范

### 3.1 项目命名

`PalDDD.<能力>.<技术适配>` 层级递进：

```
PalDDD.Core                    ← 领域能力
PalDDD.Transactions            ← 事务能力（抽象）
PalDDD.Dapper                  ← Dapper 适配（事务+事件日志+投影+仓储）
PalDDD.Dapper.PostgreSql       ← PostgreSQL 方言扩展
```

### 3.2 类型修饰符策略

| 类型 | 修饰符 | 用途 | 示例 |
|------|--------|------|------|
| 具体实现类 | `sealed class` | 默认，禁止继承 | `Dispatcher`, `OutboxProcessor` |
| 基类 | `abstract class` | 共享逻辑 | `Entity`, `Saga<TState>` |
| 值/数据载体 | `sealed record` | 不可变数据 | `EventAuditMetadata`, `IdempotencyPolicy` |
| 值对象 | `readonly record struct` | 栈分配值语义 | `ValueObject<T>`, `Deleted` |
| 工具/扩展 | `static class` | 无状态 | `PalActivitySource`, `Spec<T>` |
| 枚举 | `enum` | 有限状态 | `SagaStatus`, `CompensationPolicy` |

### 3.3 接口命名

- **`I*` 前缀**，按能力/角色命名（不按实现）
- **框架自有用 `IPal*` 前缀**（区分标准库抽象）：`IPalIdentity<TKey>`、`IPalValidator<T>`、`IPalOutboxStore`、`IPalLogger`、`IPalIdGenerator`
- 标准抽象用 `I*`：`IUnitOfWork`、`IMessageBroker`、`ISpecification<T>`

### 3.4 特定后缀

| 后缀 | 用途 | 示例 |
|------|------|------|
| `*Exception` | 异常（`sealed class`） | `HandlerNotFoundException`, `EventStreamConcurrencyException` |
| `*Options` | 配置选项（`sealed class`） | `OutboxOptions`, `SagaProcessorOptions` |
| `*Extensions` | 扩展方法类 | `ServiceCollectionExtensions`, `UnitOfWorkExtensions` |
| `*Tests` | 测试类 | `SagaTests`, `SpecificationTests` |
| `*Base` | 抽象基类 | `MessageBrokerBase`, `PeriodicBackgroundProcessor` |

### 3.5 DI 扩展方法命名

- 统一 `AddPal*` 前缀 + `this IServiceCollection`
- 入口聚合：`AddPalDDD()`、`AddPalCoreStack()`、`AddPalFullStack()`
- Handler 显式注册：`AddPalCommandHandler<TCmd,TResp,THandler>()`
- 能力注册：`AddPalOutbox()`、`AddPalInbox()`、`AddPalSaga<TState,TOrchestrator>()`
- 数据源：`AddPalNpgsqlDataSource()`、`AddPalMySqlDataSource()`、`AddPalSqlite()`
- 注册策略：`TryAddSingleton` / `TryAddScoped` 优先（避免覆盖）

### 3.6 测试方法命名

`Method_Scenario` 下划线格式（≥1 个下划线），推荐在有多场景/多结果时使用多段：

```
Method_Scenario                        → 简单场景
Method_Scenario_ExpectedResult         → 多场景/多结果
AppendAsync_WithStaleExpectedVersion_ThrowsConcurrencyException
Serialize_NullMessage_ThrowsArgumentNullException
```

此约定已由 `ArchitectureBoundaryTests.TestMethods_MustFollowTripleUnderscorePattern` 强制执行。

### 3.7 文档文件命名

| 文档类型 | 命名格式 | 示例 |
|----------|---------|------|
| **ADR** | `{nnn}-{slug}.md` | `001-outbox-batch-publish.md` |
| **核心文档** | 单英文词 kebab-case | `architecture.md`, `conventions.md`, `tutorial.md` |
| **Trellis spec** | kebab-case | `error-handling.md`, `logging-guidelines.md` |

规则：
- ADR：编号从 001 三位零填充，slug 用英文 kebab-case
- 核心文档：无日期（持续维护文档），全小写英文
- **审计产出**（`docs/review/`）的命名规范见 `docs/review/NAMING.md`——格式 `{type}-{date}[-v{n}].md`
- 禁止：主观形容词（final/definitive/ultimate）、工具名混入（serena-）、中文、大写、空格

---

## 4. 工程规范

### 4.1 目录结构

```
Pal.DDD/
├── .editorconfig              # IDE 样式策略
├── Directory.Build.props      # 全局编译配置
├── Directory.Packages.props   # 中央包管理（CPM）
├── global.json                # SDK 版本锁定
├── PalDDD.slnx                # 解决方案（按层分 Folder）
├── src/                       # 30 个源项目，Clean Architecture 分层
│   ├── PalDDD.Core/           # 领域层
│   ├── PalDDD.Serialization/  # 应用抽象层
│   ├── PalDDD.CQRS/           # 应用核心层
│   ├── PalDDD.Dapper/            # Dapper 基础设施层（事务+事件日志+投影+仓储）
│   ├── PalDDD.*.EFCore/       # EF Core 基础设施层
│   └── PalDDD.Prompts/        # AI 提示模板
├── test/                      # 14 个测试项目（1:1 映射 src，不含共享基础设施 PalDDD.Testing）
├── bench/                     # BenchmarkDotNet 基准
├── samples/                   # AOT / ECommerce / MinimalApi 示例
└── docs/                      # 架构 / 使用 / 教程 / ADR / 评审
```

### 4.2 解决方案分层

`PalDDD.slnx` 用 `<Folder>` 显式表达 Clean Architecture 分层：

```
/src/Domain/          ← Core + Analyzers + SourceGen
/src/App-Abstractions/← Serialization + Messaging
/src/App-Core/        ← CQRS + EventLog + Transactions + ...
/src/Infra-Dapper/    ← Dapper 适配器
/src/Infra-EFCore/    ← EF Core 适配器
/src/Infra-Serialization/ ← 序列化实现
/src/Infra-Messaging/ ← 消息代理适配器
/src/Hosting/         ← DI + AspNetCore
/src/Metapackages/    ← PalDDD.Prompts
```

### 4.3 文件命名

| 规则 | 示例 |
|------|------|
| **主类型或主抽象名 = 文件名** | `Entity.cs` → `class Entity`；`Dispatcher.cs` → `class Dispatcher` |
| **关系紧密的小类型可聚合** | `TransactionOptions.cs` 含 `OutboxOptions` + `InboxOptions` + `SagaProcessorOptions` |
| **接口 + DIM 桥接可同文件** | `CommandHandler.cs` 含 `ICommandHandler<T,R>` + `ICommand<T>` + DIM 桥接 |
| **同类标记属性可聚合** | `Attributes.cs` 含 `GenerateIdAttribute` + `GenerateMessageAttribute` + `BoundedContextAttribute` |
| **DI 注册** | 每包一个 `ServiceCollectionExtensions.cs`（或带适配前缀，见 §4.5） |
| **扩展方法类** | `*Extensions` 后缀用于任何扩展方法类（不限 IServiceCollection） |
| **禁止** | 空文件、仅含单个 `using` 的文件、`Helpers`/`Utils`/`Common`/`Manager` 等模糊词 |

**合规状态**（2026-07-02 全量验证）：370 源文件·30 项目·0 违规。子目录仅 2 个例外（§4.7）。

### 4.4 csproj 极简

csproj 只写 `<Project Sdk>` + `<PackageReference Include="..." />`（无 Version）+ `<ProjectReference>`。所有公共属性由 `Directory.Build.props` 继承。

### 4.5 DI 注册文件命名（严格）

| 规则 | 示例 |
|------|------|
| 项目仅一个 DI 注册类 → `ServiceCollectionExtensions.cs` | `PalDDD.Transactions/ServiceCollectionExtensions.cs` |
| 技术适配前缀 → `<Adapter>ServiceCollectionExtensions.cs` | `PalDDD.Dapper.PostgreSql/PostgreSqlServiceCollectionExtensions.cs` |
| 数据库方言前缀 → `<Dialect>ServiceCollectionExtensions.cs` | `PalDDD.Dapper.MySql/MySqlServiceCollectionExtensions.cs` |
| 所有 DI 方法统一 `AddPal*` 前缀 | `AddPalOutbox()` |

**违反后果**：命名守护（ArchitectureBoundaryTests `DependencyInjectionMethods_MustStartWithAddPalPrefix`）已在 CI 中强制执行。

### 4.6 AssemblyInfo.cs 与 GlobalUsings.cs（已废弃）

`InternalsVisibleTo` 已移入 csproj `<InternalsVisibleTo Include="..."/>` 项。
`global using` 别名已移入 csproj `<Using Include="..." Alias="..."/>` 项。

**禁止在 `src/` 中新建 `AssemblyInfo.cs` 或 `GlobalUsings.cs`**。此项由 gate-check.sh G1/G2 守护。

### 4.7 项目内子目录规范

| 规则 | 详情 |
|------|------|
| **默认扁平** | 所有 `.cs` 文件在项目根目录。28/30 项目遵循。 |
| **例外 1** | `AspNetCore/` 子目录 — ASP.NET 中间件（`ExceptionMiddleware.cs`）和端点映射（`EndpointExtensions.cs`·`HealthCheckExtensions.cs`）因涉及 `RequestDelegate`/`IApplicationBuilder` 类型，与纯 DI 注册分离 |
| **例外 2** | `.pal/prompts/` — AI 提示模板（8 个 `.prompt.md` 文件），非源码文件 |
| **禁止** | `Properties/`·`Models/`·`Services/`·`Helpers/`·`Utils/` 等子目录。新增子目录须在本表注册 |

**合规状态**：30 项目仅 2 个例外目录（均为已注册）。0 违规。

### 4.8 项目结构自检清单

每次新增文件时，自动回答以下问题：

```
□ 文件名是否等于主类型名？（§4.3）
□ DI 注册是否在 ServiceCollectionExtensions.cs 中？（§4.5）
□ 是否放在了正确的项目目录中？（§4.1·Clean Architecture 分层）
□ 是否避免了创建不必要的子目录？（§4.7）
□ 是否避免了新建 AssemblyInfo.cs / GlobalUsings.cs？（§4.6）
□ csproj 是否极简（仅 PackageReference/ProjectReference）？（§4.4）
```

此清单由 gate-check.sh G2（文件头格式）+ G3（审计文档命名）部分守护。

### 4.9 逐类型文件创建决策矩阵（强制·约定大于配置）

**原则**：开发者不需要决定文件放在哪、叫什么名字。约定已经替你做完了所有决策。本表是唯一允许的文件创建方式。

**新增任何 `.cs` 文件前，必须在本表找到匹配行。找不到 = 框架设计不支持该类型 = 禁止创建。**

| 你要新建什么 | 放在哪个项目 | 叫什么文件名 | 在哪个目录 |
|------------|------------|------------|-----------|
| **领域实体** | `PalDDD.Core` | `{EntityName}.cs` | 项目根目录 |
| **聚合根** | `PalDDD.Core` | `{AggregateName}.cs` | 项目根目录 |
| **值对象** | `PalDDD.Core` | `{ValueName}.cs` | 项目根目录 |
| **领域事件** | `PalDDD.Core` | `{EventName}.cs` | 项目根目录 |
| **领域接口**(IDomainService等) | `PalDDD.Core` | `I{Name}.cs` | 项目根目录 |
| **规约(Specification)** | `PalDDD.Core` | `{Name}Spec.cs` | 项目根目录 |
| **框架标记属性** | `PalDDD.Core` | `Attributes.cs`（追加到已有文件） | 项目根目录 |
| **命令/查询接口** | `PalDDD.CQRS` | `IRequest.cs`（追加）或 `{Name}.cs` | 项目根目录 |
| **命令处理器** | `PalDDD.CQRS` | `{Name}Handler.cs` | 项目根目录 |
| **查询处理器** | `PalDDD.CQRS` | `{Name}Handler.cs` | 项目根目录 |
| **管道行为** | `PalDDD.CQRS` | `PipelineBehaviors.cs`（追加） | 项目根目录 |
| **CQRS 异常** | `PalDDD.CQRS` | `{Name}Exception.cs` | 项目根目录 |
| **序列化接口/契约** | `PalDDD.Serialization` | `I{Name}.cs` 或 `{Name}.cs` | 项目根目录 |
| **序列化实现(JSON)** | `PalDDD.Serialization` | `Json{Name}.cs` | 项目根目录 |
| **序列化实现(MemoryPack)** | `PalDDD.Serialization.MemoryPack` | `MemoryPack{Name}.cs` | 项目根目录 |
| **消息演化/升级** | `PalDDD.Serialization.Evolution` | `Message{Name}.cs` 或 `{Name}.cs` | 项目根目录 |
| **消息 Broker 抽象** | `PalDDD.Messaging` | `Message{Name}.cs` 或 `I{Name}.cs` | 项目根目录 |
| **事件处理器接口** | `PalDDD.Messaging` | `EventHandler.cs`（追加） | 项目根目录 |
| **Kafka 实现** | `PalDDD.Messaging.Kafka` | `KafkaBroker.cs`（追加） | 项目根目录 |
| **RabbitMQ 实现** | `PalDDD.Messaging.RabbitMQ` | `RabbitMqBroker.cs`（追加） | 项目根目录 |
| **Outbox 抽象** | `PalDDD.Transactions` | `Outbox{Name}.cs` | 项目根目录 |
| **Inbox 抽象** | `PalDDD.Transactions` | `Inbox{Name}.cs` | 项目根目录 |
| **Saga 组件** | `PalDDD.Transactions` | `Saga{Name}.cs` | 项目根目录 |
| **后台处理器** | `PalDDD.Transactions` | `{Name}Processor.cs` 或 `PeriodicBackgroundProcessor.cs`（追加） | 项目根目录 |
| **重试/退避策略** | `PalDDD.Transactions` | `RetryBackoffPolicy.cs`（追加）或 `{Name}Policy.cs` | 项目根目录 |
| **事务配置** | `PalDDD.Transactions` | `TransactionOptions.cs`（追加） | 项目根目录 |
| **Dapper 存储实现** | `PalDDD.Dapper` | `Dapper{Name}Store.cs` | 项目根目录 |
| **Dapper SQL 模板** | `PalDDD.Dapper` | `SqlTemplates.cs`（追加） | 项目根目录 |
| **EF Core 存储实现** | `PalDDD.Transactions.EFCore` | `{Name}DbContext.cs` | 项目根目录 |
| **PostgreSQL 方言** | `PalDDD.Dapper.PostgreSql` | `PostgreSql{Name}.cs` | 项目根目录 |
| **MySQL 方言** | `PalDDD.Dapper.MySql` | `MySql{Name}.cs` | 项目根目录 |
| **SQLite 方言** | `PalDDD.Dapper.Sqlite` | `Sqlite{Name}.cs` | 项目根目录 |
| **投影抽象** | `PalDDD.Projections` | `Projection{Name}.cs` 或 `I{Name}.cs` | 项目根目录 |
| **投影存储实现** | `PalDDD.Projections.{EFCore/Dapper}` | `{Name}DbContext.cs` 或 `Dapper{Name}.cs` | 项目根目录 |
| **幂等抽象** | `PalDDD.Idempotency` | `Idempotency{Name}.cs` | 项目根目录 |
| **幂等存储实现** | `PalDDD.Idempotency.EFCore` | `IdempotencyDbContext.cs`（追加） | 项目根目录 |
| **事件日志抽象** | `PalDDD.EventLog` | `Event{Name}.cs` 或 `IEventLog.cs`（追加） | 项目根目录 |
| **事件日志存储(Dapper)** | `PalDDD.Dapper` | `DapperEventLog.cs` 等 | 项目根目录 |
| **事件日志存储(EFCore)** | `PalDDD.EventLog.EFCore` | `EventLogDbContext.cs`（追加） | 项目根目录 |
| **ASP.NET 中间件** | `PalDDD.Hosting.AspNetCore` | `ExceptionMiddleware.cs`（追加） | `AspNetCore/` 子目录 |
| **ASP.NET 端点** | `PalDDD.Hosting.AspNetCore` | `EndpointExtensions.cs`（追加） | `AspNetCore/` 子目录 |
| **DI 注册入口** | 对应项目的 `ServiceCollectionExtensions.cs` | `ServiceCollectionExtensions.cs` 或 `{Adapter}ServiceCollectionExtensions.cs` | 项目根目录 |
| **Roslyn 分析器** | `PalDDD.Analyzers` | `StrategicDddAnalyzer.cs`（追加） | 项目根目录 |
| **源码生成器** | `PalDDD.Core.SourceGen` | `{Name}Generator.cs` | 项目根目录 |
| **日志接口** | `PalDDD.Core` | `IPalLogger.cs` | 项目根目录 |
| **ID 生成器接口** | `PalDDD.Core` | `IPalIdGenerator.cs` | `Identity/` 子目录 |
| **压缩器接口** | `PalDDD.Compression` | `ICompressor.cs` | 项目根目录 |
| **压缩提供器接口** | `PalDDD.Compression` | `ICompressionProvider.cs` | 项目根目录 |
| **压缩算法/级别枚举** | `PalDDD.Compression` | `CompressionAlgorithm.cs` / `CompressionLevel.cs` | 项目根目录 |
| **压缩器实现（托管）** | `PalDDD.Compression` | `SystemCompressor.cs`（追加） | 项目根目录 |
| **压缩器实现（原生）** | `PalDDD.Compression.Native` | `NativeCompressors.cs`（追加） | 项目根目录 |
| **压缩 DI 注册** | `PalDDD.Compression` | `CompressionServiceCollectionExtensions.cs` | 项目根目录 |
| **原生压缩 DI 注册** | `PalDDD.Compression.Native` | `NativeCompressionServiceCollectionExtensions.cs` | 项目根目录 |

**若表中找不到匹配行**：说明该类型不在框架设计范围内。在 `docs/decisions/` 新建 ADR 论证必要性后，方可追加新行。

### 4.10 禁止创建的文件类型（约定大于配置·零例外）

以下文件/目录类型在框架中**不存在**，也**不允许被创建**。不是"不推荐"——是"不存在于框架设计中"：

| 禁止创建 | 原因 | 如果确实需要 |
|---------|------|------------|
| `IRepository<T>.cs` | 通用仓储抽象——`DbContext` 就是 UoW+Repository | 不需要。架构测试会阻断 |
| `IUpcaster.cs` | 消息升级标记接口——框架使用执行链而非标记 | 不需要。架构测试会阻断 |
| `EventBus.cs` | 进程内事件总线——框架使用 Outbox 模式 | 不需要。架构测试会阻断 |
| `[Transaction].cs` | 事务 Attribute——事务由 `IUnitOfWork` 显式管理 | 不需要 |
| `Helpers/` 或 `Utils/` 目录 | 模糊词——所有代码必须有明确的 DDD 归属 | 把代码放入正确的业务项目 |
| `Models/` 目录 | MVC 思维——DDD 中模型分实体/值对象/聚合根 | 按 DDD 类型放入对应项目 |
| `Services/` 目录 | 模糊词——应用服务 = CQRS Handler | 放入 CQRS 项目 |
| `*Manager.cs` | 模糊词——DDD 中无 Manager 概念 | 改为聚合根或领域服务 |
| `AssemblyInfo.cs` | 已废弃——内容已移入 csproj | 直接编辑 csproj |
| `GlobalUsings.cs` | 已废弃——内容已移入 csproj | 直接编辑 csproj |
| `Properties/` 子目录 | 已废弃 | 文件放项目根目录 |

### 5.1 项目命名

- 单元测试：`PalDDD.<能力>.Tests`（与 src 1:1 映射）
- 集成测试：`PalDDD.<能力>.Integration.Tests`（需 Docker/Testcontainers）
- 共享基础设施：`PalDDD.Testing`（非测试项目，提供 `FakeTimeProvider` 等）

### 5.2 测试类组织

**按行为主题切分**，非严格一被测类一测试类。一个文件可含多个测试类：

```csharp
// SagaTests.cs 含 8 个测试类
public sealed class SagaNormalTransitionTests { ... }
public sealed class SagaRetryAndCompensationTests { ... }
public sealed class SagaTimeoutTests { ... }
public sealed class SagaKeyValidationTests { ... }
```

### 5.3 测试方法命名

`Method_Scenario[_ExpectedResult]` 下划线分段（见 3.6），至少 1 个下划线分隔，推荐三段。

### 5.4 测试设置模式

**不使用 xUnit 构造函数或 `IClassFixture`**，采用轻量模式：

- **静态工厂方法**：`private static MemoryPackMessageSerializer CreateSerializer()`
- **直接 new**：`var saga = new TestSaga();`
- **派生类突破 protected**：`TestSaga : Saga<TestSagaState>`，封装 `PublicWhen()`

### 5.5 InMemory 实现规范

- **生产级实现**：带真实并发语义（`Lock` + 租约模式），非简化 mock
- **放在 src 不放 test**：与接口共置，可被 samples/benchmarks 复用
- **TimeProvider 注入**：构造函数接受可选 `TimeProvider?`，测试可注入 `FakeTimeProvider`

### 5.6 PalDDD.Testing 共享基础设施

| 工具 | 用途 |
|------|------|
| `FakeTimeProvider` | 可控时间 + 计时器快进 |
| `RecordingActivityListener` | OTel Activity 录制（并行隔离） |
| `RecordingMeterListener` | OTel Meter 录制 |
| `FixedOptionsMonitor<T>` | 固定值 IOptionsMonitor |

### 5.7 架构边界测试

`ArchitectureBoundaryTests.cs`（25+ 测试方法）将 ADR 和 Clean Architecture 落地为可执行断言：

- 项目引用禁令矩阵（`[Theory]` + InlineData）
- 源码内容关键字禁令（扫描 `.cs`，过滤注释行）
- 设计决策守护（如断言 `IRepository.cs` 不存在）
- 配置守护（断言 AOT 属性值）
- 测试自身质量守护（断言每个 BackgroundService 有对应 *Tests.cs）

---

## 6. 文档规范

### 6.1 README.md 结构

1. 标题 + badge（.NET/AOT/Tests/Warnings/DDD）
2. 核心理念表（7 条原则）
3. 项目结构（ASCII 树 + 分层编号）
4. 快速开始（5 步代码示例）
5. 性能数据（表格 + 1M 迭代）
6. 质量指标（emoji 块）
7. 文档索引表

### 6.2 ADR 模板

文件名 `NNN-kebab-case-topic.md`，统一结构：

```markdown
# ADR NNN：<中文标题>

> 状态：提案 | 已采纳
> 日期：YYYY-MM-DD

### 背景
### 方案       ← 方案 A/B/C 各列优缺点
### 决策       ← 粗体一句话结论 + 编号理由
### 后果       ← 正面 / 负面 / 风险 三段
### 替代方案（已评估并拒绝）
```

### 6.3 文档更新流程

代码变更时，以下文档必须同步更新：

| 变更类型 | 必须同步的文档 |
|---------|--------------|
| 公共 API 签名变更 | `README.md`·`docs/usage.md`·`docs/tutorial.md` |
| 新增/删除项目 | `README.md`·`docs/architecture.md`·`PalDDD.slnx` 分层描述 |
| AOT 配置变更 | `docs/aot.md` |
| 性能契约变更 | `docs/performance.md`·`docs/conventions.md` §12 |
| 验证命令变更 | `docs/development.md` |

**同步原则**：代码、文档、注释三方一致。变更后 `grep` 旧事实值确认零残留。

### 6.4 文档文件命名

- **审计产出**（`docs/review/`）：`{type}-{date}[-v{n}].md`，详见 `docs/review/NAMING.md`
- **ADR**：`{nnn}-{slug}.md`，编号三位零填充
- **核心文档**（`docs/`根目录）：单英文词 kebab-case，无日期（持续维护文档）
- **Trellis spec**：kebab-case
- 全局命名规范见 §3.7

- **中文为主** + 表格 + Mermaid/ASCII 图
- **先代码后解释**：先给可编译代码块，再散文解释设计取舍
- **显式说明"不做什么"**：如"不提供 `IRepository<T>`"及理由
- **诚实陈述 trade-off**：ADR 后果段分正面/负面/风险

---

## 7. AI 提示模板规范

### 7.1 位置

`src/PalDDD.Prompts/.pal/prompts/`，随 `PalDDD.Prompts` NuGet 包分发。

### 7.2 统一六段结构

```markdown
# <场景名称>

### 角色
你是 Pal.DDD 框架专家...

### 框架约束（编译期强制执行）
| 规则 | 说明 |      ← 映射 PDDD001-015

### 必须遵守
- <硬约束>

### 禁止
- ❌ <反模式 + 原因>

### 输出格式
<可填充代码骨架>

### 示例（来自 samples/）
<真实可编译代码>
```

### 7.3 约束映射

模板的「框架约束」段落**直接映射 `StrategicDddAnalyzer` 的 PDDD001-015 规则**，形成「分析器规则—AI 模板—架构测试」三位一体。

### 7.4 五条跨模板核心原则

1. 零反射（禁 `MakeGenericType` / `Activator` / `Assembly.GetTypes()`）
2. AOT 兼容（`JsonSerializerIsReflectionEnabledByDefault=false`）
3. 不做 `IRepository<T>`
4. 显式注册（零 Assembly Scanning）
5. 基础设施层 `ConfigureAwait(false)`

---

## 8. DDD/Clean Architecture 约束

### 8.1 分层依赖方向

```
Infrastructure / Adapters → App-Core → App-Abstractions → Domain
```

- 零循环依赖（ArchitectureBoundaryTests 强制）
- 领域层零基础设施依赖（Core 无任何 ProjectReference）
- 依赖方向从外到内（Serena `find_referencing_symbols` 验证）

### 8.2 战术 DDD 模式

| 构建块 | 实现 | 约束 |
|--------|------|------|
| Entity | `Entity<TId>` | 标识相等性，`GetType()` 精确匹配 |
| AggregateRoot | `AggregateRoot<TId>` | 极薄标记基类 |
| ValueObject | `ValueObject<T>` / `IValueObject` | `readonly record struct` |
| DomainEvent | `DomainEvent` + `IDomainEvent` | `static abstract EventName`，`sealed` |
| Repository | 不提供 `IRepository<T>` | DbContext 就是 UoW+Repository |
| UnitOfWork | `IUnitOfWork` | 纯事务抽象，零 DDD 概念泄漏 |

### 8.3 战略 DDD 治理

`StrategicDddAnalyzer` 15 条编译期规则（PDDD001-015）：

- 领域模型必须声明 `[BoundedContext]`
- 领域事件必须 `sealed` + `[GenerateMessage]`
- 消息名必须含 BC 前缀 + `.v{N}` 后缀
- ProcessManager / ProjectionHandler 必须 `sealed` + 归属 BC

### 8.4 「不做」清单

| 不做 | 原因 |
|------|------|
| `IRepository<T>` | DbContext 已实现 UoW+Repository |
| `IIntegrationEvent` | DomainEvent 就是事件，序列化由 MessageDescriptor 管理 |
| `[Transaction]` Attribute | 事务由 `IUnitOfWork.ExecuteInTransactionAsync` 显式管理 |
| Assembly Scanning | 零反射，Handler 显式注册 |
| Protobuf 工具链 | 代码优先 vs Schema 优先范式冲突（ADR-002） |

---

## 9. Git 提交规范

### 9.1 提交信息格式

```
类型：描述
```

### 9.2 类型清单

| 类型 | 用途 |
|------|------|
| 功能 | 新增功能 |
| 修复 | 修复 Bug |
| 重构 | 代码重构（无行为变更） |
| 文档 | 文档更新 |
| 测试 | 新增/修改测试 |
| 性能 | 性能优化 |
| 构建 | 构建/CI 配置 |

### 9.3 长期任务

多 commit 任务（≥2 次提交）的最后一次提交在正文附累计进度：

```
重构：架构精炼 + P2/P3 改进

进度：P2(4/4) + P3(5/5) + 架构精炼(5/5)
```

### 9.4 暂存规则

- 提交前检查 `git status` 和 `git diff`，只暂存本次任务相关文件
- 提交前三方一致自检：公共 API/签名/行为变更时 `grep` 旧事实值确认零残留

### 9.5 提交粒度

| 场景 | 粒度规则 | 示例 |
|------|---------|------|
| 独立修复 | 一个 commit | `修复：OutboxProcessor 空指针异常` |
| 关联修复（同一文件·同一主题） | 合并为一个 commit | `修复：Saga 补偿顺序 + Outbox 提示 + 注释修正` |
| 关联修复（不同文件·不同主题） | 拆分为独立 commit | 先 `修复：Saga.When() 线程安全文档`，再 `修复：tutorial Outbox 类定义` |
| 新功能 | 一个 commit（含代码+测试+文档） | `功能：Outbox 批处理背压控制` |

**回滚策略**：每个 commit 必须可独立回滚。不将无关变更混入同一 commit。非破坏性变更优先 `git revert`，破坏性变更评估影响范围后决定。

---

## 10. AI Agent 编码约束

> 本章专为 AI 编码助手设计，归纳自本项目评审历史中 AI 最容易犯的错误。这些约束是对第 1-9 章的补充，聚焦「AI 生成代码时的常见退化模式」。

### 10.1 禁止退化反射

Pal.DDD 的 AOT 兼容性建立在 DIM 桥接 + 源码生成器之上。AI 生成代码时禁止以下退化：

| 禁止 | 应使用 | 原因 |
|------|--------|------|
| `MakeGenericType` | DIM 桥接（`IHandler.HandleAsync` 默认实现） | AOT 编译失败 |
| `Activator.CreateInstance` | DI 显式注册 `AddPalCommandHandler<T>()` | AOT 编译失败 |
| `Assembly.GetTypes()` | 源码生成器 `[ModuleInitializer]` | AOT 编译失败 |
| `Type.GetType(string)` | `typeof(T)` 编译时常量 | AOT 编译失败 |
| 反射式 JSON 序列化 | `[JsonSerializable]` 源生成 `JsonTypeInfo` | `JsonSerializerIsReflectionEnabledByDefault=false` |

### 10.2 表达式树组合

组合规约（`ISpecification<T>.And/Or/Not`）的表达式树生成**禁止用 `Expression.Invoke`**——EF Core 的 LINQ 提供器无法将其翻译为 SQL。

**正确**（参数替换）：

```csharp
var leftBody = new ParameterReplacer(leftExpr.Parameters[0], parameter).Visit(leftExpr.Body);
var rightBody = new ParameterReplacer(rightExpr.Parameters[0], parameter).Visit(rightExpr.Body);
return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(leftBody, rightBody), parameter);
```

**错误**（EF Core 不兼容）：

```csharp
// ❌ Expression.Invoke 无法被 EF Core 翻译为 SQL
var body = Expression.AndAlso(
    Expression.Invoke(leftExpr, parameter),
    Expression.Invoke(rightExpr, parameter));
```

### 10.3 异常过滤

- `catch (Exception)` 必须带 `when (ex is not OperationCanceledException)` 过滤取消异常
- 后台处理器（Outbox/Inbox/Saga）的 `catch (Exception)` 必须带 `[SuppressMessage("Design", "CA1031", Justification = "...")]` + 具体理由
- 事务回滚的 `catch (Exception rollbackEx)` 是唯一允许的裸 catch（回滚后重抛原始异常）

### 10.4 并发安全

- `Dispatcher` 的 `Dictionary` 在 `Freeze()` 后转为 `FrozenDictionary`，**禁止运行时 `Add`**
- `InMemory*Store` 实现必须用 `Lock` 保护共享状态（`InMemoryOutboxStore` 已遵循）
- `TimeProvider` 注入是测试确定性契约，**禁止硬编码 `DateTimeOffset.UtcNow`**（用构造函数注入的 `TimeProvider`）
- `SagaState.CurrentState` 不能包含 `|` 字符（key 分隔符，运行时校验已强制）

### 10.5 提交前验证（AI Agent 必跑）

```bash
# 1. 构建（零错误零警告）
dotnet build PalDDD.slnx

# 2. 测试（零失败）
dotnet test PalDDD.slnx --no-restore -e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"

# 3. 公共 API 变更时更新快照
PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1 dotnet test test/PalDDD.Core.Tests --filter "CorePackagePublicApi_MatchesSnapshot"

# 4. 规范验证脚本（秒级）
bash scripts/verify-conventions.sh
```

---

## 11. 依赖注入规范

### 11.1 注册模式

- `TryAddSingleton` / `TryAddScoped` 优先（避免覆盖用户已有注册）
- Handler 通过 `HandlerMarker` + `HandlerRegistrar`（`IHostedService`）在启动时注册到 `Dispatcher`，然后 `Freeze()`
- **禁止 Assembly Scanning**（零反射，AOT 兼容）

### 11.2 生命周期

| 组件 | 生命周期 | 原因 |
|------|:--------:|------|
| `Dispatcher` | Singleton | `FrozenDictionary` 不可变 |
| `ICommandHandler` / `IQueryHandler` | Scoped | 通过 `IServiceScopeFactory` 创建 scope |
| `IUnitOfWork` | Scoped | `DbContext` 生命周期 |
| `OutboxDomainEventInterceptor` | Scoped | `_pending` 是实例状态 |
| `OutboxProcessor` / `InboxProcessor` / `SagaProcessor` | Singleton / HostedService | 后台轮询 |
| `IMessageSerializer` | Singleton | 无状态 |
| `IMessageCatalog` | Singleton | `FrozenDictionary` 不可变 |

### 11.3 命名约定

- 入口聚合：`AddPalDDD()` / `AddPalCoreStack()` / `AddPalFullStack()`
- Handler 显式注册：`AddPalCommandHandler<TCmd,TResp,THandler>()`
- 能力注册：`AddPalOutbox()` / `AddPalInbox()` / `AddPalSaga<TState,TOrchestrator>()`
- 数据源：`AddPalNpgsqlDataSource()` / `AddPalMySqlDataSource()` / `AddPalSqlite()`

### 11.4 违反后果

| 违规 | 后果 | 守护方式 |
|------|------|---------|
| Singleton 持有 Scoped 依赖 | 容器 Dispose 时 Scoped 已释放 → `ObjectDisposedException` | ArchitectureBoundaryTests 扫描 |
| Scoped 注册为 Singleton（如 `OutboxDomainEventInterceptor`） | 实例字段 `_pending` 被并发请求交叉写入 → 事件丢失或重复 | ArchitectureBoundaryTests 守护 |
| 使用 `IServiceScopeFactory.CreateScope` 后未释放 scope | scope 内 Scoped 服务泄漏 | 代码审查 |
| Assembly Scanning 注册 Handler | 反射破坏 AOT 兼容性 | ArchitectureBoundaryTests + verify-conventions 双重守护 |

---

## 12. 性能契约

> 以下设计决策是性能契约，修改前必须评估对热路径分配的影响。BenchmarkDotNet 基线见 `docs/performance.md`。

### 12.1 零分配快速路径

| 契约 | 不可改为 | 原因 |
|------|---------|------|
| `ValueTask` + `IsCompletedSuccessfully` | `Task` | 同步完成零分配 |
| `PipelineStateMachine`（~40B 可重用） | 闭包链（N×72B） | 每请求消除闭包分配 |
| `FrozenDictionary` | `Dictionary` / `ConcurrentDictionary` | O(1) 查找 + 零 GC |
| `ref struct` 枚举器（`DomainEventEnumerable`） | `IEnumerable<T>` | 栈分配，零装箱 |
| 单链表事件存储（`_head`/`_tail`） | `List<DomainEvent>` | 无事件时零分配 |

### 12.2 ThreadStatic 池化

`JsonMessageSerializer` 的 `_tlsWriter` / `_tlsBufferWriter` 是零分配热路径契约：

- **禁止改为实例字段**（会破坏线程安全）
- **禁止改为 `AsyncLocal<T>`**（会有执行上下文开销）
- 使用 `ThreadStatic` + `Reset()` 复用 `Utf8JsonWriter` + `ArrayBufferWriter<byte>`

### 12.3 零拷贝读取路径

`RecordedEvent.RehydrateFromBytes`（`internal`）是零拷贝契约：

- 写入路径（`EventData` 构造）：`ToArray()` 防御性拷贝（安全优先）
- 读取路径（`RehydrateFromBytes`）：引用赋值（性能优先）
- **禁止在读取路径调用 `ToArray()`**（会破坏零拷贝契约）

### 12.4 SQL 模板编译时常量

`SqlTemplates.cs` 的所有 SQL 是 `public const string`：

- **禁止改为 `string` + 运行时拼接**（会从编译时常量退化为运行时分配）
- **禁止用 `$"..."` 插值拼接 SQL**（SQL 注入风险 + 运行时分配）
- 数据库方言切换通过 `DapperSqlDialect` record struct，不通过字符串拼接

---

## 13. 评审纪律（基于专业审计的最优实践）

> 来源：`docs/review/` 审计报告。
> 目标：消除评审中的误判（历史误判率 38%）、遗漏（20%）、优先级错配。

### 13.1 评审基线强制规则

评审报告首行必须粘贴 `bash scripts/review-snapshot.sh` 的输出。所有断言锚定该快照。禁止采信工具记忆或过期数据。未粘贴 snapshot 的评审视为草稿。

### 13.2 九条评审纪律（R1~R8 + 新增 R0）

| 编号 | 纪律 | 针对根因 | 执行方式 |
|------|------|----------|----------|
| **R0** | **可信度标注原则** | 片段信息→确定性结论 | 每个发现标注 ✅完整审计 / ⚠基于抽样 / ❓待验证；基于 ⚠/❓ 禁止给出确定结论 |
| R1 | 完整读取原则 | 不完整读取 | 引用代码块时必须读完整方法/块体 |
| R2 | 语义场景区分原则 | 未区分语义场景 | 涉及异常/取消/并发时必须列举所有语义子场景 |
| R3 | 当前 commit 锚定原则 | 过期快照 | 评审基线标注 commit hash |
| R4 | grep 语义核查原则 | grep 计数→语义判断 | grep 差异必须逐项读上下文，数字差异不能直接当语义结论 |
| R5 | 分析器行为核实原则 | 错误心智模型 | 声称分析器触发时必须先 `dotnet build` 验证 |
| R6 | 外部输入交叉验证原则 | 未交叉验证 | 外部任务的方法名/类名/路径必须 grep/`ls` 验证 |
| R7 | 架构测试覆盖度审计原则 | 覆盖度未审计 | 评审架构测试时必须核查断言数据覆盖全部应覆盖项 |
| R8 | 实测优先原则 | 采信记忆 | 数字声明必须运行命令获取 |

### 13.3 优先级体系（危害 × 复杂度）

替代旧 P1/P2/P3（仅基于时间紧迫度），采用双维度判定：

| | 高危害 | 中危害 | 低危害 |
|------|:------:|:------:|:------:|
| **易修复**（< 1h） | P0 紧急 | P1 近期 | P2 |
| **中等**（1-4h） | P1 近期 | P2 | P3 |
| **难修复**（> 4h） | P2 | P3 | 评估（产出 ADR） |

**危害定义**：
- **高危害**：数据损失 / 安全漏洞 / 资源泄漏 / 运行时崩溃 / 编译失败
- **中危害**：不一致 / 潜在并发风险 / 架构退化 / 文档命令不可执行
- **低危害**：注释缺失 / IDE 噪音 / 装饰性不一致 / 纯计数漂移

### 13.4 任务清单验证

任务清单生成后：标识符存在性验证 → `bash scripts/verify-action-items.sh <file>`；涉及分析器 → 须附 `dotnet build` 命令；外部合并任务 → 逐项 grep 方法名/类名/路径（见 `ACTION_ITEMS_TEMPLATE.md`）。

### 13.5 评审模板与报告格式

评审报告必须使用 `docs/review/REVIEW_TEMPLATE.md`（v2 8 段结构）。任务清单必须使用 `docs/review/ACTION_ITEMS_TEMPLATE.md`（危害 × 复杂度双维度格式）。

---

## 附录：规范执行矩阵

| 规范 | 执行手段 | 强制级别 |
|------|---------|:--------:|
| 零反射 | ArchitectureBoundaryTests 源码扫描 | CI |
| AOT 兼容 | `Directory.Build.props` + `dotnet publish /p:PublishAot=true` | 编译期 + CI |
| 依赖方向 | ArchitectureBoundaryTests 项目引用矩阵 | CI |
| DDD 命名 | StrategicDddAnalyzer PDDD001-015 | 编译期 |
| 零警告 | TreatWarningsAsErrors | 编译期 |
| 测试覆盖 | coverlet ≥80% 行覆盖 | CI |
| 突变测试 | Stryker ≥50% 突变分数 | CI（PR 时） |
| 公共 API 快照 | PublicApiSnapshotTests | CI |
| AI 模板约束 | `.pal/prompts/` 六段结构 | 人工 |
| AI 编码约束 | Trellis spec 注入 + `scripts/verify-conventions.sh` | 会话 + pre-commit |
| 性能契约 | BenchmarkDotNet `--smoke` 烟测 | CI |
| DI 生命周期 | ArchitectureBoundaryTests 配置守护 | CI |
| 评审纪律 | `scripts/review-snapshot.sh` + `REVIEW_TEMPLATE.md` R0 可信度标注 | 评审时 |
| 任务清单验证 | `scripts/verify-action-items.sh` 标识符 + build 命令 + 外部合并 grep | 任务清单生成后 |
| AOT 断言动态扫描 | `ArchitectureBoundaryTests.InfrastructureAdapters_AreExplicitlyNonAot` 动态扫描 | CI |
