# Pal.DDD 测试体系规范

> 从测试专家视角定义的分层测试体系。指导单元测试、性能基准、AOT 验证、并发安全和 CI 自动化的完整生命周期。
>
> **本文件是 conventions.md §5（测试规范）的展开版**——conventions §5 给出测试约定的精简要点，本文件提供完整 SOP、金字塔、场景矩阵、统计判据、CI 触发规则。
>
> **真源**：
> - [`conventions.md`](conventions.md) §5（测试规范要点）+ §10.6（TUnit+MTP 4 硬规则）+ §12（性能契约）
> - [`.ai/test/prompt.md`](../.ai/test/prompt.md)（T1-T14 + T-DDD-1..5 铁律）
> - [`test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`](../test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs)（33 测试方法机械守护）

---

## 目录

1. [测试金字塔](#一测试金字塔)
2. [测试框架与约定](#二测试框架与约定)
3. [场景覆盖矩阵](#三场景覆盖矩阵)
4. [BenchmarkDotNet 科学配置](#四benchmarkdotnet-科学配置)
5. [统计有效性判据](#五统计有效性判据)
6. [源生成器变更规则](#六源生成器变更规则)
7. [PR 性能影响评估](#七pr-性能影响评估)
8. [AOT 变更验证](#八aot-变更验证)
9. [CI Pipeline 触发规则](#九ci-pipeline-触发规则)
10. [提交前必检清单](#十提交前必检清单)
11. [测试体系文件索引](#十一测试体系文件索引)

---

## 一、测试金字塔

```
                          ┌──────────────┐
                          │  AOT publish  │  核心层 publish -p:PublishAot=true
                         ╱└──────────────┘╲
                        ╱                   ╲
                  ┌──────────┐         ┌──────────────┐
                  │ 压力测试  │ 100K/1M │ Testcontainers│ PG/MySQL/SQLite/RabbitMQ/Kafka
                  │ Outbox/Saga│  规模  │  集成测试     │ 真库冒烟 + 死信闭环
                  └──────────┘         └──────────────┘
                                     ╱                   ╲
                              ┌────────────┐      ┌──────────────┐
                              │ 性能基准    │      │ 并发安全      │ Saga 租约锁
                              │ Framework + │      │ Outbox       │ MessageCatalog
                              │ Infra 双文件│      │ 不可变测试    │
                              └──────┬─────┘      └──────────────┘
                                    ╱
                  ┌──────────────────┐│ ┌──────────────────────┐
                  │ Architecture     ││ │ Core/CQRS/Transactions│
                  │ BoundaryTests    ││ │  单元功能正确性        │
                  │ 33 方法 85 断言  ││ │ AggregateRoot/Saga/Outbox│
                  └──────────────────┘│ └──────────────────────┘
                                     ╱
                          ┌────────────────────┐
                          │ PDDD001-015         │ 编译期战略 DDD 诊断
                          │ StrategicDddAnalyzer│ 15 条规则负向测试
                          └────────────────────┘
```

### 测试项目清单（16 个）

| 项目 | 类型 | 职责 |
|------|------|------|
| `PalDDD.Core.Tests` | 单元 | AggregateRoot/Entity/ValueObject/SmartEnum/Specification/DomainEvent/PublicApiSnapshot/AotContract/AllocationContract/PerformanceContract |
| `PalDDD.Core.Abstractions.Tests` | 单元 | 核心抽象（IUnitOfWork/IMessageBroker 等）契约 |
| `PalDDD.CQRS.Tests` | 单元 | Dispatcher/PipelineStateMachine/CommandHandler/QueryHandler |
| `PalDDD.Transactions.Tests` | 单元 + 集成 | Saga/Outbox/Inbox + 租约锁 + 补偿链 + 死信重投递 |
| `PalDDD.EventLog.Tests` | 单元 | IEventLog 实现 + RecordedEvent 零拷贝 |
| `PalDDD.Projections.EventLog.Tests` | 单元 | EventLog 投影源 + 断点续传 |
| `PalDDD.Messaging.Tests` | 单元 | IMessageBroker InMemory 实现 + MessageCatalog |
| `PalDDD.Serialization.Tests` | 单元 | IMessageSerializer + MessageEvolutionPipeline + SchemaVersion |
| `PalDDD.DependencyInjection.Tests` | 单元 | **ArchitectureBoundaryTests 33 方法机械守护** + DI 注册规范 |
| `PalDDD.Repository.EFCore.Tests` | 集成 | UnitOfWork + OutboxDomainEventInterceptor Scoped |
| `PalDDD.Hosting.AspNetCore.Tests` | 集成 | ExceptionMiddleware + AspNetCore 中间件链 |
| `PalDDD.Integration.Tests` | 集成 | Testcontainers 真库（PG/MySQL/SQLite）+ OutboxDbContext 全链路 + Idempotency |
| `PalDDD.Messaging.Integration.Tests` | 集成 | Testcontainers 真库（RabbitMQ/Kafka）+ Broker 抽象对称 |
| `PalDDD.PalORM.Tests` | 集成 | PalORM 7 Store + 跨方言（SQLite/PG/MySQL）+ 并发竞争 |
| `PalDDD.Compression.Tests` | 单元 + 集成 | 系统压缩器往返（Brotli/GZip/Deflate）+ Native（LZ4/ZStandard） |
| `PalDDD.Analyzers.Tests` | 单元 | PDDD001-015 编译期诊断负向测试 |

**共享基建**：`PalDDD.Testing`（非测试项目，提供 FakeTimeProvider/RecordingActivityListener/RecordingMeterListener/FixedOptionsMonitor）

---

## 二、测试框架与约定

### 2.1 框架选型

| 维度 | DDD 约定 |
|------|---------|
| **测试框架** | TUnit 1.58.0 + MTP（Microsoft.Testing.Platform） |
| **断言库** | TUnit.Assertions（Fluent 链式） |
| **属性测试** | TUnit.FsCheck（属性驱动） |
| **快照测试** | Verify.TUnit 31.20.0（预留，目前 PublicApiSnapshot 自实现） |
| **集成测试** | Testcontainers.*（PG/MySQL/SQLite/RabbitMQ/Kafka） |

> **禁用** `Microsoft.NET.Test.Sdk`（与 TUnit MTP 冲突，conventions §10.6 硬规则）

### 2.2 global.json 配置

```json
{
  "sdk": { "version": "11.0.100-preview.5.26302.115", "rollForward": "latestMajor", "allowPrerelease": true },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

### 2.3 test/Directory.Build.props 关键设置

```xml
<IsTestingPlatformApplication Condition="'$(IsTestProject)' == 'true'">true</IsTestingPlatformApplication>
<TestingPlatformDotnetTestSupport Condition="'$(IsTestProject)' == 'true'">true</TestingPlatformDotnetTestSupport>
<UseTestingPlatformProtocol Condition="'$(IsTestProject)' == 'true'">true</UseTestingPlatformProtocol>
<NoWarn>$(NoWarn);CA1515;CA1707;CA1711;CA1508;CA1812;CA2000;CA2007;CA1034;CA2263</NoWarn>
```

### 2.4 测试属性

```csharp
// ✅ TUnit：[Test] + [Arguments(...)]
[Test]
[Arguments("order-created.v1", true)]
[Arguments("invalid name", false)]
public async Task ValidateMessageName_ReturnsExpected(string name, bool expected) { ... }

// ❌ 禁用：xUnit 风格
[Fact]
[Theory]
[InlineData("order-created.v1", true)]
public void ValidateMessageName(string name, bool expected) { ... }
```

### 2.5 共享 Fixture 策略

**不用 `IClassFixture` / `[Collection]`**——基于时间戳的 listener 过滤隔离：

```csharp
// ✅ 时间戳过滤实现并行隔离
public sealed class RecordingActivityListener : IDisposable
{
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    // ActivityStopped 回调过滤早于 _createdAt 的残留 activity
}
```

### 2.6 Setup 方式

**不用 `[SetUp]` / 构造注入**——方法内局部构造 + 手动 SUT：

```csharp
// ✅ 静态工厂方法
private static MemoryPackMessageSerializer CreateSerializer() => new();

// ✅ 直接 new
var saga = new TestSaga();

// ✅ 派生类突破 protected
public sealed class TestSaga : Saga<TestSagaState>
{
    public Task PublicWhenAsync<T>(T evt) where T : DomainEvent => WhenAsync(evt);
}

// ✅ 测试桩内嵌
private sealed class StubScopeFactory : IServiceScopeFactory { /* ... */ }
private sealed class CountingOutboxStore : IPalOutboxStore { /* ... */ }
```

### 2.7 测试组织

**按行为主题切分**，非 1:1 被测类：

```csharp
// ✅ 一个文件含多个测试类，按行为主题
// SagaTests.cs
public sealed class SagaNormalTransitionTests { ... }
public sealed class SagaRetryAndCompensationTests { ... }
public sealed class SagaTimeoutTests { ... }
public sealed class SagaKeyValidationTests { ... }
```

### 2.8 断言风格

```csharp
// ✅ TUnit Fluent 链式（await 必需）
await Assert.That(value).IsEqualTo(expected);
await Assert.That(() => action()).Throws<InvalidOperationException>();
await Assert.That(collection).Count().IsEqualTo(2);
await Assert.That(source).Contains("...");

// ❌ 禁用：同步 Assert.Throws / Assert.Fail
Assert.Throws<X>(() => Method());           // 用 await Assert.That(() => Method()).Throws<X>()
Assert.Fail("unexpected");                  // 用 await Assert.That(false).IsTrue()
```

### 2.9 清理模式

```csharp
// SQLite 内存库：using var 自动释放
await using var db = await TestDb.SqliteAsync();

// Testcontainers（PG/MySQL/RabbitMQ/Kafka）：必须 try/finally
try
{
    await db.ExecuteAsync($"CREATE TABLE test_tbl (...)");
    // ... 测试逻辑
}
finally
{
    await db.ExecuteAsync($"DROP TABLE IF EXISTS test_tbl");
}

// 静态全局（如 DomainEvent.TimeProvider）：try/finally 还原
try { DomainEvent.TimeProvider = fakeTimeProvider; /* ... */ }
finally { DomainEvent.TimeProvider = TimeProvider.System; }
```

---

## 三、场景覆盖矩阵

### 3.1 功能正确性矩阵

| 功能域 | 单元测试 | 集成测试 | AOT 验证 | 负责测试 |
|--------|:---:|:---:|:---:|---------|
| **Entity/AggregateRoot 不变量** | ✅ | — | — | AggregateRootInvariantTests |
| **ValueObject 值相等** | ✅ | — | — | ValueObjectTests |
| **SmartEnum** | ✅ | — | — | SmartEnumTests |
| **Specification** | ✅ | — | — | SpecificationTests |
| **DomainEvent 收集与单链表存储** | ✅ | — | — | DomainEventSemanticsTests |
| **Event Sourcing 重放** | ✅ | — | — | EventSourcingContractTests |
| **Saga 编排 + 补偿链** | ✅ | — | — | SagaTests（8 类） |
| **Saga 租约锁**（多实例并发） | ✅ | ⚠ | — | SagaTimeoutTests |
| **Outbox 死信重投递** | ✅ | ✅ | — | OutboxRequeueTests（9 场景） |
| **Inbox 幂等**（SQLite TOCTOU/PG ON CONFLICT） | ✅ | ✅ | — | InboxTests |
| **Outbox 原子租约**（FOR UPDATE SKIP LOCKED） | — | ✅ | — | Integration.Tests |
| **CQRS Dispatcher Freeze** | ✅ | — | — | DispatcherTests |
| **Pipeline 状态机**（零分配） | ✅ | — | — | AllocationContractTests |
| **MessageCatalog 不可变** | ✅ | — | — | AotContractTests |
| **MessageEvolutionPipeline** | ✅ | — | — | SerializationTests |
| **Projection 断点续传** | ✅ | ⚠ | — | ProjectionsEventLogTests |
| **StrategicDdd PDDD001-015** | ✅ | — | — | StrategicDddAnalyzerTests |
| **Broker 对称**（InMemory/Kafka/RabbitMQ） | ✅ | ✅ | — | MessagingTests + MessagingIntegrationTests |

### 3.2 性能基准矩阵

| 操作类型 | Smoke 模式 | BDN 正式 | 配置 | 状态 |
|---------|:---:|:---:|------|:---:|
| Entity 领域事件追加 | ✅ | ✅ | `[ShortRunJob]+[MemoryDiagnoser]` | ✅ |
| ValueObject 创建 | ✅ | ✅ | 同上 | ✅ |
| SmartEnum 查找 | ✅ | ✅ | 同上 | ✅ |
| Validation 规约 | ✅ | ✅ | 同上 | ✅ |
| Outbox 吞吐 | ✅ | ✅ | 同上 | ✅ |
| EventLog 写入 | ✅ | ✅ | 同上 | ✅ |
| SagaState 持久化 | ✅ | ✅ | 同上 | ✅ |
| SQL 生成（Dapper） | ✅ | ✅ | 同上 | ✅ |
| 配置初始化 | ✅ | ✅ | 同上 | ✅ |

### 3.3 数据规模矩阵

> DDD 当前未做 Params 参数化扫描（与 ORM 不同，DDD 无 `Bulk*` 大规模操作）。性能契约关注**单次操作零分配**而非规模拐点：

| 契约 | 不可改为 | 真源 |
|------|---------|------|
| `ValueTask` + `IsCompletedSuccessfully` | `Task`（同步完成零分配） | conventions §12.1 |
| `PipelineStateMachine`（~40B 可重用） | 闭包链（N×72B） | conventions §12.1 |
| `FrozenDictionary` | `Dictionary`/`ConcurrentDictionary` | conventions §1.7 |
| `ref struct` 枚举器（DomainEventEnumerable） | `IEnumerable<T>` | conventions §12.1 |
| 单链表事件存储 | `List<DomainEvent>` | conventions §12.1 |
| `RecordedEvent.RehydrateFromBytes` 零拷贝 | `ToArray()` | conventions §12.3 |
| ThreadStatic 池化（JsonMessageSerializer） | 实例字段/AsyncLocal | conventions §12.2 |
| `SqlTemplates` const | `string` 拼接/$"" 插值 | conventions §12.4 |

---

## 四、BenchmarkDotNet 科学配置

### 4.1 DDD 实际偏好（与 ORM 不同）

| 配置 | DDD 使用场景 | ORM 使用场景 |
|------|------------|------------|
| `[ShortRunJob]` + `[MemoryDiagnoser]` | **全部 10 个 benchmark 类**（统一双标注） | 不使用 |
| `[SimpleJob]` 三档（快速 1/3/5 / 标准 3/5/10 / 严格 5/10/15） | **未使用** | 偏好（grep 实证） |
| `[BenchmarkCategory]` | **未使用**（按文件分类 Framework/Infra） | 使用（按操作分类） |
| `[Params]` | **未使用**（用 const 或字段） | 使用（参数化扫描） |
| `[Benchmark(Baseline = true)]` | 少数方法标注 | 使用 |

### 4.2 配置层级（参考）

> DDD 当前统一用 `[ShortRunJob]`。如需提升统计可信度，可参考 ORM 三档：

| 层级 | 配置 | 适用场景 | 统计可信度 |
|------|------|---------|:---:|
| 快速验证 | `launchCount=1, warmupCount=3, iterationCount=5` | 开发迭代 | ⚠ 低 |
| 标准基准 | `launchCount=3, warmupCount=5, iterationCount=10` | 正式报告 | ✅ 中 |
| 严格基准 | `launchCount=5, warmupCount=10, iterationCount=15` | 发版基线、CI 门禁 | ✅✅ 高 |

### 4.3 Smoke 模式（DDD 特色，.NET 11 Preview 必备）

```bash
dotnet run --project bench/PalDDD.Benchmarks -- --smoke
```

100 万次迭代手动计时（Stopwatch + `GC.GetAllocatedBytesForCurrentThread`），不依赖 BenchmarkDotNet 引擎。

> **重要**：BenchmarkDotNet 0.15.8 不支持 .NET 11 Preview，本机 BDN 校验可能只输出 `Validating benchmarks:` 未生成正式报告。**`--smoke` 是 .NET 11 Preview 下唯一可信的快速回归手段**。待 .NET 11 GA 后切回标准 BDN 报告。

---

## 五、统计有效性判据

```
Error / Mean < 5%   → 统计有效，可用于正式报告
Error / Mean 5-15%  → 可接受，报告需标注「环境敏感」
Error / Mean > 15%  → 统计无效，必须：
  (1) 提升配置（launchCount / iterationCount）
  (2) 排查噪声源（后台进程 / 散热 / VMware）
  (3) 考虑改用 Smoke 模式（纯计时无 MemoryDiagnoser）
```

---

## 六、源生成器变更规则

> DDD 源生成器：**GenerateId**（强类型 ID）/ **GenerateEnum**（智能枚举）/ **MessageRegistryGenerator**（消息注册表）。
> emit 模板变更是高风险操作——旧 obj 缓存会导致增量构建复用旧 emit（B8 教训）。

### 6.1 变更后必跑

```bash
# 1. 清 obj/bin
rm -rf src/*/obj src/*/bin test/*/obj test/*/bin

# 2. 全量构建 + 重生成公共 API 快照
PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1 dotnet build PalDDD.slnx --no-incremental

# 3. 评审 git diff 的快照变更，确认 emit 差异符合预期
git diff test/PalDDD.Core.Tests/Snapshots/

# 4. 如 diff 不符合预期，回滚 Generator 改动，重新分析
```

### 6.2 触发条件

| 改动 | 必跑快照评审 |
|------|:---:|
| `src/PalDDD.Core.SourceGen/*.cs` emit 模板 | ✅ |
| `src/PalDDD.Core.SourceGen/*.cs` Generator 注册逻辑 | ✅ |
| 新增 `[GenerateMessage]` 标注的消息类型 | ✅（MessageCatalog 键集变更） |
| 新增 `[GenerateId]` / `[GenerateEnum]` 使用方 | ⚠（看是否影响公共 API） |
| 公共 API 签名变更（任何 src/） | ✅（PublicApiSnapshot 强制） |

---

## 七、PR 性能影响评估

PR 涉及以下文件时必须做性能评估：

| 文件 | 评估方式 |
|------|---------|
| `src/PalDDD.CQRS/PipelineStateMachine.cs` | Smoke 模式前后对比 |
| `src/PalDDD.Core/DomainEvent*.cs` | Allocation 契约测试 |
| `src/PalDDD.Serialization/JsonMessageSerializer.cs` | Smoke + ThreadStatic 池化验证 |
| `src/PalDDD.Transactions/Outbox*.cs` 或 `Saga*.cs` | Outbox 吞吐基准 |
| `src/PalDDD.Core.SourceGen/*.cs` | PublicApiSnapshot diff + 编译所有消费者 |

### 评估 SOP

```bash
# 1. PR 分支跑 Smoke 基线
git checkout feature/xxx
dotnet run --project bench/PalDDD.Benchmarks -- --smoke | tee /tmp/before.txt

# 2. 切到 main 跑对比
git stash && git checkout main
dotnet run --project bench/PalDDD.Benchmarks -- --smoke | tee /tmp/after.txt

# 3. 人工对比两份输出
# 关键指标：单次操作 ns 回归 >10% 需在 PR 说明原因；内存分配回归必须为零（DDD 零分配契约）
```

---

## 八、AOT 变更验证

涉及以下文件时必须验证 AOT：

| 文件 | 风险 | 验证命令 |
|------|:---:|---------|
| `src/PalDDD.Core/*.cs` 反射用法 | 高 | `dotnet publish -p:PublishAot=true`（AOT 核心层项目） |
| `System.Text.Json` 相关 | 高 | 确认 `[JsonSerializable]` context 存在 + `JsonSerializerIsReflectionEnabledByDefault=false` |
| `src/PalDDD.Transactions/*.cs` Saga 反射 | 中 | 已带 `[RequiresDynamicCode]` 标注，项目显式 `IsAotCompatible=false` |
| 新增 `[GenerateMessage]` 类型 | 低 | SourceGen 自动 emit JsonTypeInfo |
| 新增 EF Core/Kafka 适配器 | 高 | 必须显式 `IsAotCompatible=false`（设计本意，ArchitectureBoundaryTests 强制） |

### AOT 分层验证矩阵（DDD 特色）

| 层 | 项目数 | AOT 策略 | 验证 |
|----|:------:|---------|------|
| **AOT 核心层** | 7 | `IsAotCompatible=true`（继承 Directory.Build.props） | `dotnet publish -p:PublishAot=true` 全绿 |
| **非 AOT 适配器层** | 14 | 显式 `IsAotCompatible=false`（设计本意） | ArchitectureBoundaryTests `InfrastructureAdapters_AreExplicitlyNonAot` 强制 |

---

## 九、CI Pipeline 触发规则

> DDD 当前 CI workflow（如有）应遵循以下触发规则。具体 workflow 文件见 `.github/workflows/`。

| Workflow | 触发条件 | 作用 |
|----------|---------|------|
| `ci.yml` Build & Test | 每次 push/PR | 构建 + 单元 + 集成（Testcontainers）+ 质量门禁 |
| `ci.yml` AOT | 每次 push/PR | AOT 核心层 publish -p:PublishAot=true |
| `perf-gate.yml`（待实施） | 每周日 + `[perf]` PR | Smoke 基线 + 回归检测 |
| `stryker.yml`（待实施） | 每月 + `[mutation]` PR | 突变测试 high=80/low=60/break=50 |

### Testcontainers CI 要求

- **必须 ubuntu-latest**（Windows runner 不支持 Testcontainers）
- **每个 job 显式 timeout-minutes**（默认 360 分钟过长，DB 挂起会占用 runner）
- **docker.sock 挂载**：Testcontainers 通过 Docker API 启动容器

---

## 十、提交前必检清单

```bash
# 1. 工作树清洁
git status --short

# 2. 全量构建（0 警告 0 错误）
dotnet build PalDDD.slnx --no-incremental

# 3. 单元测试全绿
dotnet test PalDDD.slnx --no-restore --no-build

# 4. 规范验证（grep 静态检查）
bash scripts/verify-conventions.sh --quick

# 5. AI 系统门禁（如使用 .ai/）
bash .ai/scripts/gate-check.sh --allow-dirty

# 6. 源生成器变更额外验证（如改了 SourceGen）
PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1 dotnet build PalDDD.slnx
git diff test/PalDDD.Core.Tests/Snapshots/   # 评审快照

# 7. 公共 API 变更三方一致（如改了签名）
# - 同步更新 docs/conventions.md / docs/architecture.md
# - 同步更新 docs/usage.md / docs/tutorial.md
# - 检查 src/PalDDD.Prompts/.pal/prompts/ 是否需更新
```

---

## 十一、测试体系文件索引

| 文件 | 用途 |
|------|------|
| `docs/testing.md` | ← 本文件，测试体系完整规范 |
| `docs/conventions.md` §5 | 测试规范要点（精简版） |
| `docs/conventions.md` §10.6 | TUnit+MTP 4 硬规则 |
| `docs/conventions.md` §12 | 性能契约（零分配快速路径） |
| `.ai/test/prompt.md` | T1-T14 + T-DDD-1..5 测试铁律 |
| `test/PalDDD.Testing/TestHelpers.cs` | 共享测试工具（FakeTimeProvider 等） |
| `test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs` | 33 测试方法机械守护 |
| `test/PalDDD.Core.Tests/PublicApiSnapshotTests.cs` | 公共 API 快照 |
| `test/PalDDD.Core.Tests/Snapshots/*.txt` | 快照基线（评审后提交） |
| `bench/PalDDD.Benchmarks/Program.cs` | BenchmarkSwitcher + Smoke 模式入口 |
| `bench/PalDDD.Benchmarks/FrameworkBenchmarks.cs` | 领域核心基准 |
| `bench/PalDDD.Benchmarks/InfraBenchmarks.cs` | 基础设施基准 |
| `scripts/verify-conventions.sh` | 规范验证脚本（三模式） |
| `test/PalDDD.Core.Tests/stryker-config.json` | 突变测试配置（如存在） |

---

## 维护规则

1. **本文件与 conventions §5 同步**：测试约定变更需同时更新两处。
2. **场景矩阵随项目演化**：新增功能域必须扩矩阵行。
3. **BenchmarkDotNet 配置变更需评审**：从 `[ShortRunJob]` 切换到其他配置必须在 PR 说明依据。
4. **DDD 与 ORM 的测试体系差异**：TUnit+MTP（非 xUnit）/ 测试桩内嵌（非 IClassFixture）/ 按行为主题切分 / ArchitectureBoundaryTests 33 方法机械守护。
5. **统计有效性是硬约束**：Error/Mean > 15% 的基准数据不得用于正式报告或决策。
