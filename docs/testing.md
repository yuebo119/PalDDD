# Pal.DDD 测试体系规范

> 从测试专家视角定义的分层测试体系。指导单元测试、性能基准、AOT 验证、并发安全和 CI 自动化的完整生命周期。
>
> **本文件是 conventions.md §5（测试规范）的展开版**——conventions §5 给出测试约定的精简要点，本文件提供完整 SOP、金字塔、场景矩阵、统计判据、CI 触发规则。
>
> **真源**：
> - [`conventions.md`](conventions.md) §5（测试规范要点）+ §10.6（TUnit+MTP 4 硬规则）+ §12（性能契约）
> - [`.ai/test/prompt.md`](../.ai/test/prompt.md)（T1-T14 + T-DDD-1..6 铁律）
> - [`test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`](../test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs)（38 测试方法机械守护——计数锚定见 doc-consistency D12a）

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

## 〇、InMemory 实现族定位（2026-08-26 精炼轮声明）

六个 InMemory 实现（Outbox/Inbox/SagaState/EventLog/Idempotency/Checkpoint）**刻意分散在各抽象包内**（`AddPalDDD` 引 Transactions 即得 InMemoryOutboxStore），不集中为独立 PalDDD.InMemory 包。理由：单元测试/原型零依赖直达（一跳引用）；集中包会形成对全部五个抽象包的反向汇聚依赖（版本耦合 + 测试项目两跳引用）。定位：**单元测试与原型的默认实现**，非生产实现——生产语义请用三栈之一（PalORM AOT 主线 / Dapper 调用点级 AOT / EFCore 生态线，见 ADR-020 2026-09-13 修订）。

> **三栈行为对齐（3.1.0）**：`InMemoryOutboxStore.MarkProcessed` 对**从未租约**的消息由静默忽略改为正常标记（此前比生产三栈更严格，会让测试与生产行为背离）；对已被其他 worker 重租的消息仍拒绝标记（僵尸标记保护由引用相等性检查承载，未受影响）。Dapper 侧同版对齐：`MarkProcessed` 被租约 token 拒阻时不再清空入参消息对象的租约字段。

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
                  │ 38 方法 103 断言 ││ │ AggregateRoot/Saga/Outbox│
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
| `PalDDD.Core.Tests` | 单元 | AggregateRoot/Entity/ValueObject/SmartEnum/Specification/DomainEvent/PublicApiSnapshot/AotContract/AllocationContract（性能契约由 ArchitectureBoundaryTests 的 PerformanceContract 存在性断言守护） |
| `PalDDD.Core.Abstractions.Tests` | 单元 | 核心抽象（IUnitOfWork/IMessageBroker 等）契约 |
| `PalDDD.CQRS.Tests` | 单元 | Dispatcher/PipelineStateMachine/CommandHandler/QueryHandler |
| `PalDDD.Transactions.Tests` | 单元 + 集成 | Saga/Outbox/Inbox + 租约锁 + 补偿链 + 死信重投递 |
| `PalDDD.EventLog.Tests` | 单元 | IEventLog 实现 + RecordedEvent 零拷贝 |
| `PalDDD.Projections.EventLog.Tests` | 单元 | EventLog 投影源 + 断点续传 |
| `PalDDD.Messaging.Tests` | 单元 | IMessageBroker InMemory 实现 + MessageCatalog |
| `PalDDD.Serialization.Tests` | 单元 | IMessageSerializer + MessageEvolutionPipeline + SchemaVersion |
| `PalDDD.DependencyInjection.Tests` | 单元 | **ArchitectureBoundaryTests 38 方法机械守护** + DI 注册规范 |
| `PalDDD.Repository.EFCore.Tests` | 集成 | UnitOfWork + OutboxDomainEventInterceptor Scoped |
| `PalDDD.Hosting.AspNetCore.Tests` | 集成 | ExceptionMiddleware + AspNetCore 中间件链 |
| `PalDDD.Integration.Tests` | 集成 | Testcontainers 真库（PG/MySQL/SQLite）+ OutboxDbContext 全链路 + Idempotency |
| `PalDDD.Messaging.Integration.Tests` | 集成 | Testcontainers 真库（RabbitMQ/Kafka）+ Broker 抽象对称 |
| `PalDDD.PalORM.Tests` | 集成 | PalORM 6 Store + 跨方言（SQLite/PG/MySQL）+ 并发竞争 |
| `PalDDD.Compression.Tests` | 单元 + 集成 | 系统压缩器往返（Brotli/GZip/Deflate）+ Native（LZ4/ZStandard） |
| `PalDDD.Analyzers.Tests` | 单元 | PDDD001-015 编译期诊断负向测试 |

**共享基建**：`PalDDD.Testing`（非测试项目，提供 FakeTimeProvider/RecordingActivityListener/RecordingMeterListener/FixedOptionsMonitor）

---

## 二、测试框架与约定

### 2.1 框架选型

| 维度 | DDD 约定 |
|------|---------|
| **测试框架** | TUnit 1.75.0 + MTP（Microsoft.Testing.Platform） |
| **断言库** | TUnit.Assertions（Fluent 链式） |
| **属性测试** | TUnit.FsCheck（属性驱动） |
| **快照测试** | Verify.TUnit 32.0.1（已用于 Hosting.AspNetCore.Tests 的 ExceptionMiddleware，3 个 `.verified.txt` 基线；公共 API 快照另由自实现 PublicApiSnapshot 承载） |
| **集成测试** | Testcontainers.*（PG/MySQL/SQLite/RabbitMQ/Kafka） |

> **禁用** `Microsoft.NET.Test.Sdk`（与 TUnit MTP 冲突，conventions §10.6 硬规则）

### 2.2 global.json 配置

```json
{
  "sdk": { "version": "11.0.100-rc.2.26504.105", "rollForward": "latestMajor", "allowPrerelease": true },
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
| **Inbox 幂等**（SQLite TOCTOU/PG ON CONFLICT） | ✅ | ✅ | — | InboxProcessorTests + InboxMessageTests（Transactions.Tests） |
| **Outbox 原子租约**（FOR UPDATE SKIP LOCKED） | — | ✅ | — | Integration.Tests |
| **CQRS Dispatcher Freeze** | ✅ | — | — | DispatcherTests |
| **Pipeline 派发预算**（每请求 &lt;500B） | ✅ | — | — | CqrsTests.Dispatcher_QueryAsync_NoHandlerOverhead_BaselineAllocation |
| **MessageCatalog 不可变** | ✅ | — | — | AotContractTests |
| **MessageEvolutionPipeline** | ✅ | — | — | SerializationTests |
| **Projection 断点续传** | ✅ | ⚠ | — | EventLogReplaySourceTests（Projections.EventLog.Tests） |
| **StrategicDdd PDDD001-015** | ✅ | — | — | StrategicDddAnalyzerTests |
| **Broker 对称**（InMemory/Kafka/RabbitMQ） | ✅ | ✅ | — | MessagingTests + MessagingIntegrationTests |

### 3.2 性能基准矩阵

| 操作类型 | Smoke 模式 | BDN 正式 | 入口模式 | 状态 |
|---------|:---:|:---:|------|:---:|
| Entity 领域事件追加 | ✅ | ✅ | `--framework`（MediumRun InProcess） | ✅ |
| Entity 创建/GetHashCode | — | ✅ | `--framework` | ✅ |
| ValueObject 创建 | ✅ | ✅ | `--framework` | ✅ |
| SmartEnum 查找 | ✅ | ✅ | `--framework` | ✅ |
| Validation 规约 | ✅ | ✅ | `--framework` | ✅ |
| CQRS 分发链（零行为 vs 单直通行为） | — | ✅ | `--app`（2026-10-08 缺口补齐） | ✅ |
| 消息序列化（源生成 JSON 序列化/反序列化/Catalog 查找） | — | ✅ | `--app`（2026-10-08 缺口补齐） | ✅ |
| Outbox 吞吐 | ✅ | ✅ | `--infra` | ✅ |
| Outbox 并发租约争用（N worker=1/2/4/8，InMemory 簿记层 + Dapper SQLite 真库层，每 wave 零重复不变量守卫） | — | ✅ | `--concurrency`（2026-10-08 缺口补齐） | ✅ |
| Inbox 编排层（InMemory：首投/重复拒绝/完整生命周期） | — | ✅ | `--infra`（2026-10-08 缺口补齐） | ✅ |
| PG/MySQL 网络真库 Dapper + PalORM Outbox/EventLog/Idempotency（吞吐 + 并发 wave；Testcontainers 或 wslc 外部实例双路径） | — | ✅ | `--persist-pg` / `--persist-mysql`（2026-10-08 双栈真跑完成 + 2026-10-09 EventLog/Idempotency 口袋补齐：wslc 路径 `PALDDD_BENCH_PG_CONN`/`PALDDD_BENCH_MYSQL_CONN` + palddd_bench 库名守卫；Testcontainers 路径留给 Docker 环境，容器不可达报错退出码 2） | ✅（数字、MySQL 双栈死锁实录与 PalORM 会话非线程安全实录见 performance.md；吞吐类覆盖三 Store 能力，类名沿用 Outbox 见账本注） |
| EF 栈租约 SQL 形态（JOIN 派生表 SKIP LOCKED 互斥验证，MySQL） | — | ✅ | `--persist-mysql`（2026-10-09；EF 栈级车道待上游 Provider 出 EF 11 rc 配套版，SQL 形态为其等价物） | ✅（零死锁+互斥不变量+1.75× 加速，已补记 ADR-024） |
| PG 方言特性（Pipeline 单往返批处理/读写分离路由/LISTEN-NOTIFY 往返） | — | ✅ | `--persist-pg`（2026-10-09；COPY 隐含在种子路径） | ✅ |
| Projection 吞吐（处理器全周期 + 断点存储直测，InMemory） | — | ✅ | `--coverage`（2026-10-08 缺口补齐） | ✅ |
| 消息演化管线（V1→V2 单跳 vs V1→V3 链式） | — | ✅ | `--coverage` | ✅ |
| Specification 内存路径（IsSatisfiedBy/ToExpression） | — | ✅ | `--coverage` | ✅ |
| Hosting 每请求开销（终端基线/异常中间件直通/MapCommand 端点全周期，DefaultHttpContext 无网络栈） | — | ✅ | `--coverage` | ✅ |
| 端到端单命令链路（分发→序列化→Outbox 落入，InMemory 框架开销层） | — | ✅ | `--coverage` | ✅ |
| MediatR 横向对比（12.5.0 同进程四点：零行为/单行为 × 双框架） | — | ✅ | `--compare`（2026-10-09 新维度轮；引用须带完整口径——MediatR 是纯分发器无基础设施层） | ✅ |
| CAP 横向对比（DotNetCore.CAP 10.0.2，RabbitMQ 传输 + MySQL cap.published，同语义路径对位；2026-10-11 升级换锚，结论与 8.4.1 时代同带跨主版本稳定） | — | ✅ | `--compare` + `PALDDD_BENCH_CAP_MYSQL`/`PALDDD_BENCH_CAP_RABBIT`（一次性容器，2026-10-09 新维度轮） | ✅ |
| 投递吞吐端到端（SQLite 真库标记 + 假 broker RTT 矩阵 0/1/20ms + T1.2 排空节奏对照；排空 1000 条积压墙钟口径） | — | ✅ | `--delivery`（2026-10-11 优化实施轮 T0.2；专用采样档 5 迭代/3 预热/1 launch——多秒级 op 撞 InProcess 单基准 5 分钟上限，medium 不可用，同 coverage×strict 车道级形态；T2.1/T3.1 的 A/B 基线） | ✅（数字见 performance.md 优化实施轮段） |
| 投递吞吐端到端·远程库（PG/MySQL 网络真库 + 假 broker RTT 0/1ms × BatchMark 1/16，InFlight 固定 1；排空 1000 条积压墙钟口径） | — | ✅ | `--delivery-pg` / `--delivery-mysql`（2026-10-16 B1 复活前提实测新增；同 --delivery 采样档，wslc 外部实例路径 `PALDDD_BENCH_PG_CONN`/`PALDDD_BENCH_MYSQL_CONN` + palddd_bench 库名守卫，MySQL 种子走 LOAD DATA LOCAL 须连接串显式 `AllowLoadLocalInfile=True` + 服务端 `local_infile=1`——库级安全门，调用方显式决策） | ✅（数字见 performance.md 远程投递 A/B 段） |
| 绝对 QPS（1/4/8 线程 × dispatch/chain 聚合吞吐，非 BDN 专门 harness） | ✅（--smoke 级趋势） | —（harness 形态） | `--qps`（2026-10-09 新维度轮；多线程聚合不可由单线程 ns/op 外推） | ✅ |
| EventLog 写入 | ✅ | ✅ | `--infra` | ✅ |
| SagaState 持久化 | ✅ | ✅ | `--infra` | ✅ |
| SQL 生成（Dapper） | ✅ | ✅ | `--infra` | ✅ |
| 配置初始化 | ✅ | ✅ | `--infra` | ✅ |
| 压缩编解码（Brotli/GZip/Deflate/LZ4/ZStandard，16KB 同负载） | — | ✅ | `--compression`（单车道 A/B）·`--infra`（五算法同 Provider 对照） | ✅ |
| 持久化三栈批量（Dapper/PalOrm/EfCore 真库） | — | ✅ | `--persist` / `--persist-medium`（Lease 锚） | ✅ |
| Saga 编排车道（Normal 基线 / FanOut） | — | ✅ | `--saga` | ✅ |

> **「BDN 正式」的口径（2026-10-08 起生效）**：指 InProcess 显式 job 模式产出的正式报告（上表入口列）。默认 Switcher 入口（`dotnet run --project bench/...` 不带参数）在 BDN 0.15.8 时代因 net11-rc moniker 不被 DotNetSdkValidator 识别于本工具链不可用（根因与绕行详见 `Program.cs` 头注）；**2026-10-17 升位 BDN 0.16.0-preview.2 后默认入口已恢复**（`--list` 实测 exit 0 全量列出），显式 InProcess 路径保留为正式报告口径（MediumRun/InProcess 作业形态是刻意配置）。

### 3.3 数据规模矩阵

> DDD 当前未做 Params 参数化扫描（与 ORM 不同，DDD 无 `Bulk*` 大规模操作）。性能契约关注**单次操作零分配**而非规模拐点：

| 契约 | 不可改为 | 真源 |
|------|---------|------|
| `ValueTask` + `IsCompletedSuccessfully` | `Task`（同步完成零分配） | conventions §12.1 |
| `PipelineStateMachine`（每请求 ~40B，Dispatcher 为 Singleton 故不可跨请求重用） | 闭包链（N×72B） | conventions §12.1 |
| `FrozenDictionary` | `Dictionary`/`ConcurrentDictionary` | conventions §1.7 |
| `ref struct` 枚举器（DomainEventEnumerable） | `IEnumerable<T>` | conventions §12.1 |
| 单链表事件存储 | `List<DomainEvent>` | conventions §12.1 |
| `RecordedEvent.RehydrateFromBytes` 零拷贝 | `ToArray()` | conventions §12.3 |
| ThreadStatic 池化（JsonMessageSerializer） | 实例字段/AsyncLocal | conventions §12.2 |
| `SqlTemplates` const | `string` 拼接/$"" 插值 | conventions §12.4 |

---

## 四、BenchmarkDotNet 科学配置

### 4.1 DDD 实际偏好（与 ORM 不同）

> **2026-10-08 更新**：类级 `[ShortRunJob]` 已全部移除——BDN 0.15.8 对 jobs 是 union 不去重，类级 job 与模式级 ManualConfig 叠加出双 job，CsProj 工具链 job 在 net11-rc moniker 上触发 DotNetSdkValidator 崩溃（`--persist` 五十四轮先例同款，详见 `bench/PalDDD.Benchmarks/Program.cs` 头注）。运行口径统一由模式级显式 job 提供，**30 个基准类**（Framework 5 + App 2 + Infra 7 + Persistence 3 + SagaLane 1 + Concurrency 2 + Remote 5 + Coverage 5）全量可出正式报告（2026-10-08/09 缺口补齐轮新增：CQRS 分发链、消息序列化、Outbox N worker 租约争用、Inbox 编排层、PG/MySQL 网络真库 Dapper+PalORM 双栈、EF 租约 SQL 形态、PG 方言特性、Projection 吞吐、消息演化、Specification 内存路径、Hosting 每请求开销、端到端链路）。

| 配置 | DDD 使用场景 | ORM 使用场景 |
|------|------------|------|
| 模式级 ManualConfig：`Job.MediumRun` + InProcessEmitToolchain + `[MemoryDiagnoser]` | `--framework` / `--app` / `--infra` / `--concurrency` / `--coverage` / `--persist-pg` / `--persist-mysql` / `--persist-medium`（正式报告，27 类全覆盖） | 不使用 |
| 模式级 ManualConfig：`Job.MediumRun` 加密采样（迭代 40/预热 20/launch 3） | `--strict`（2026-10-10 增、同日专项治理轮全局化：**可叠加到任意模式**，如 `--infra --strict` / `--persist-pg --strict`；无其他模式参数时默认跑 OutboxThroughput+SmartEnum 两类。Error 12~18%→0.5~5.4% 且均值回归真值；strict 数字入账须带档位标签。**时长预期**：采样量为 medium 4×，实测分段耗时约为 medium 的 3~4×——framework ~29min / infra ~39min / app ~16min / persist-×-strict ~30-39min（medium 分别 17/12/5/9min）——非卡死判据：日志 WorkloadActual 单调递增即健康，多段链串行叠加属预期） | 不使用 |
| 模式级 ManualConfig：`Job.InProcess` | `--persist` / `--compression`（快速与单车道 A/B） | 不使用 |
| 类级 `[InProcess]` attribute | SagaLaneBenchmarks（`--saga`，单类无叠加风险） | 不使用 |
| ~~类级 `[ShortRunJob]`~~ | **已移除**（2026-10-08，双 job 崩溃见上注） | 不使用 |
| `[SimpleJob]` 三档（快速 1/3/5 / 标准 3/5/10 / 严格 5/10/15） | **未使用** | 偏好（grep 实证） |
| `[BenchmarkCategory]` | **未使用**（按文件分类 Framework/App/Infra/Persistence/SagaLane/Concurrency/Remote/Coverage） | 使用（按操作分类） |
| `[Params]` | **未使用**（用 const 或字段） | 使用（参数化扫描） |
| `[Benchmark(Baseline = true)]` | 少数方法标注 | 使用 |

### 4.2 配置层级（参考）

> DDD 当前由模式级 ManualConfig 统一供 job（正式档 `Job.MediumRun` InProcess，快速档 `Job.InProcess`，见 §4.1）。如需调整统计可信度，可参考 ORM 三档：

| 层级 | 配置 | 适用场景 | 统计可信度 |
|------|------|---------|:---:|
| 快速验证 | `launchCount=1, warmupCount=3, iterationCount=5` | 开发迭代 | ⚠ 低 |
| 标准基准 | `launchCount=3, warmupCount=5, iterationCount=10` | 正式报告 | ✅ 中 |
| 严格基准 | `launchCount=5, warmupCount=10, iterationCount=15` | 发版基线、CI 门禁 | ✅✅ 高 |

### 4.3 Smoke 模式（DDD 特色，.NET 11 Preview 必备）

```bash
dotnet run --project bench/PalDDD.Benchmarks -- --smoke
```

100 万次迭代手动计时（Stopwatch + `GC.GetAllocatedBytesForCurrentThread`），不依赖 BenchmarkDotNet 引擎。**2026-10-08 起带分配预算断言**：Success/FromValue ≤1 B/op、Failed ≤50 B/op、RaiseEvent ≤140 B/op（rc.1 实测校准；预算语义对齐 AllocationContractTests——上界含余量只拦大幅回归），超限退出码 1，可直接用作人工 TOOL 门禁（conventions §10.6 口径，未接 CI）。

> **重要**：BDN 0.15.8 曾不识别 net11-rc runtime moniker 导致默认 Switcher 入口在本工具链崩溃（根因详见 `Program.cs` 头注）；2026-10-17 升位 **BDN 0.16.0-preview.2** 后该问题已修复（`--list` 实测 exit 0），正式报告仍以 InProcess 显式模式为口径（`--framework`/`--app`/`--infra`/`--concurrency`/`--persist*`/`--saga`/`--compression`，见 §4.1）；`--smoke` 定位是**秒级成本的趋势检查 + 分配预算判定**，计时为单轮无统计效力，不替代正式报告。

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
| **AOT 核心层** | 显式 8（PalORM×4+Dapper×4）+ 继承 true 14（无 csproj 覆盖即继承全局 true） | `IsAotCompatible=true` | CI aot-verify（PalOrmSample + AotSample 双 sample publish + 实跑二进制）全绿 |
| **非 AOT 适配器层** | 14 | 显式 `IsAotCompatible=false`（设计本意） | ArchitectureBoundaryTests `InfrastructureAdapters_AreExplicitlyNonAot` 强制 |

---

## 九、CI Pipeline 触发规则

> DDD 当前 CI workflow（如有）应遵循以下触发规则。具体 workflow 文件见 `.github/workflows/`。

| Workflow | 触发条件 | 作用 |
|----------|---------|------|
| `ci.yml` build-and-test | 每次 push（main/dev/tag）/PR + workflow_dispatch | vuln-scan → restore → build → format-verify → 逐项目 test（MTP 一次一项目）→ secret-scan/dapper-param-guard/doc-consistency → encoding-gate/tech-debt/test-gate 循环 → gate（G22/G23/G24）→ gate-audit --inventory → gate-lite |
| `ci.yml` aot-verify | 同上 | PalOrmSample + AotSample **双 sample** publish `-p:PublishAot=true` + 实跑 native 二进制 |
| `ci.yml` coverage | 同上 | 全局行覆盖率阈值 0.70 + 单模块降幅 ≤5pp（`coverage-baseline.json`） |
| `ci.yml` dialect-probe | push/PR，路径门控（仅 Store/SQL/DDL/映射面变更；PR 全跑） | DialectProbeTests（Testcontainers PG/MySQL 真库） |
| `codeql.yml` | push（main/dev）/PR + 每周一 schedule + workflow_dispatch | CodeQL 静态安全分析 |
| `perf-gate.yml`（待实施） | 每周日 + `[perf]` PR | Smoke 基线 + 回归检测 |
| `mutation.yml` | 每周一 schedule + workflow_dispatch | Stryker 两个 curated 基线（Compression / Transactions lease），break 95，JSON/HTML artifact |
| `AssertionStrengthGateTests`（test/PalDDD.DependencyInjection.Tests） | 每次 PR/本地（dotnet test） | 断言强度棘轮（与 Stryker 互补；Stryker 5.0.0 MTP runner 见 mutation.yml；MIG-003 由脚本下沉） |

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

# 3. 单元测试全绿——slnx 批量可直接运行（2026-10-21 实测 exit 0；用例数以当次输出为准）
dotnet test PalDDD.slnx --no-build
# 需要单项目定位 / treenode-filter / 逐项目失败归因时，再逐项目循环：
# for p in $(find test -name '*.Tests.csproj' ! -path '*/obj/*' ! -path '*/bin/*' | sort); do dotnet test "$p" --no-restore --no-build; done

# 4. 规范验证（grep 静态检查）
dotnet run scripts/verify-conventions.cs -- --quick
# 注（ITM-680 收尾，2026-09-14）：--quick 为提交前口径（秒级，仅静态检查）；full（默认）
# 含全量 build+test，无 Docker 机器上方言族测试（PalORM 多方言 46 项 + Integration 14 项）
# 自动跳过而非失败（3.1.0 起，T-17 统一 skip 策略），本地看到 full 红先查环境而非代码。

# 5. AI 系统门禁（如使用 .ai/）
dotnet run scripts/gate.cs -- --allow-dirty

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
| `.ai/test/prompt.md` | T1-T14 + T-DDD-1..6 测试铁律 |
| `test/PalDDD.Testing/TestHelpers.cs` | 共享测试工具（FakeTimeProvider 等） |
| `test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs` | 38 测试方法机械守护 |
| `test/PalDDD.Core.Tests/PublicApiSnapshotTests.cs` | 公共 API 快照 |
| `test/PalDDD.Core.Tests/Snapshots/*.txt` | 快照基线（评审后提交） |
| `bench/PalDDD.Benchmarks/Program.cs` | BenchmarkSwitcher + Smoke 模式入口 |
| `bench/PalDDD.Benchmarks/FrameworkBenchmarks.cs` | 领域核心基准 |
| `bench/PalDDD.Benchmarks/InfraBenchmarks.cs` | 基础设施基准 |
| `scripts/verify-conventions.cs` | 规范验证脚本（三模式） |
| `AssertionStrengthGateTests.cs` | 断言强度检查测试（与 Stryker 互补；原 bash 脚本 MIG-003 下沉） |

---

## 维护规则

1. **本文件与 conventions §5 同步**：测试约定变更需同时更新两处。
2. **场景矩阵随项目演化**：新增功能域必须扩矩阵行。
3. **BenchmarkDotNet 配置变更需评审**：模式级 job/迭代参数的调整（如 MediumRun 降档 ShortRun）必须在 PR 说明依据。
4. **DDD 与 ORM 的测试体系差异**：TUnit+MTP（非 xUnit）/ 测试桩内嵌（非 IClassFixture）/ 按行为主题切分 / ArchitectureBoundaryTests 38 方法机械守护。
5. **统计有效性是硬约束**：Error/Mean > 15% 的基准数据不得用于正式报告或决策。
