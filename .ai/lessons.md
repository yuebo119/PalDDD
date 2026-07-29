# Pal.DDD AI 规范化系统 v1.0

> **一句话定义**：AI 在 Pal.DDD 上做任何代码变更前必读的入口文件。
> 本文件是 AI 规范真源之一，与 `docs/conventions.md` §10/§13/§14、`ArchitectureBoundaryTests.cs`、`PalDDD.Analyzers`（PDDD001-015）共同构成 AI 协作约束。
>
> **版本**：v1.0（DDD 项目起板，不沿用 ORM 项目的 22 案例史）
>
> **真源优先级**（冲突时从严）：代码（ArchitectureBoundaryTests + StrategicDddAnalyzer + Directory.Build.props） > `docs/conventions.md` > 本文件 > `.editorconfig`

---

## I. AI 协作 14 条铁律（不可违反）

| # | 铁律 | 违反后果 |
|---|------|---------|
| 1 | **同类改动批量完成后一次构建**（不是每次 Edit 后都跑 `--no-incremental`） | 增量缓存掩盖断裂或过度等待 |
| 2 | **批量 Edit 前必 Read** | 单行类 Edit 导致 CS1585 / 锚点漂移 |
| 3 | **SuppressMessage 必附 Justification**（英文说明具体原因） | CS1003 / CA1031 失去语义 |
| 4 | **新增 .cs 文件前必查 conventions §4.9 决策矩阵**（找不到匹配行=禁止创建） | 文件错位 / 引入被禁模式（Helpers/Utils/Manager/IRepository\<T\>） |
| 5 | **Core 层禁 DbContext/SqlConnection/HttpClient**（ArchitectureBoundaryTests 强制） | 编译期 FAIL |
| 6 | **`catch (Exception)` 必带 `when (ex is not OperationCanceledException)` 过滤**（conventions §10.3） | ITM-030 同型缺陷（3 处复发：PeriodicBackgroundProcessor / ExceptionMiddleware / HealthCheck） |
| 7 | **清空远程共享数据库前必须确认连接串指向测试库**（XII.2 #7） | 并行 DROP/CREATE 竞争或清空生产数据 |
| 8 | **永不删除 `.ai/` 目录**（XII.2 #8） | 失去全部项目约束（门禁/review/误判库/缺陷登记） |
| 9 | **不留占位符代码**（TODO/placeholder/NotImplementedException，XII.3 #7） | 半成品进测试掩盖真问题 |
| 10 | **引入新依赖前验证存在且版本有效**（XII.3 #8） | 运行时空对象 / 编译失败 |
| 11 | **长会话每完成里程碑自动 commit**（XII.3 #9） | 上下文丢失导致返工 |
| 12 | **复杂功能先输出签名让用户确认**（XII.3 #10） | 200 行错误方向 |
| 13 | **源生成器改动后清 obj/bin**（XII.3 #11） | 增量构建复用旧 emit |
| 14 | **跨方言测试串行化**（XII.3 #12） | 共享数据库并行 DROP/CREATE 竞争 |

### 构建验证时机

| 时机 | 命令 | 用途 |
|------|------|------|
| 同类改动内（如 D 批次 5 处删除）| 不构建 | 继续批量改动 |
| 跨类别切换（D→M 或 M→R）| `dotnet build PalDDD.slnx` | 确认上批无误 |
| 源生成器改动（GenerateId/GenerateEnum/MessageRegistry）| 先清 obj/bin + `PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1 dotnet build` | 避免增量构建复用旧生成物 |
| 最终提交前 | `dotnet build PalDDD.slnx --no-incremental` | 全量重建 |
| 规范扫描 | `bash scripts/verify-conventions.sh --quick` | grep 静态检查 |
| AI 触发 review 前 | `bash .ai/scripts/gate-check.sh` | PDDD-G1..G22 门禁 |

---

## II. AI 缺陷登记（DDD 项目自有）

> 来源：`docs/review/action-items-*.md`（ITM-001~NNN，详见各 action-items 文件）+ `docs/decisions/001-016`（ADR）+ conventions §14（TUnit/MTP 真实案例）。
> 本节不承诺具体 ITM 总数——按需查阅 `docs/review/` 历史归档。
> 每个 ITM 必须有 commit/文件路径可追溯。

### II.1 已核实的真实缺陷（部分列举，完整列表见 docs/review/）

| ITM/批次 | 缺陷 | 教训 | 沉淀 |
|---------|------|------|------|
| ITM-001 | SagaKey `\|` 分隔符隐式契约 | 第三方状态名含 `\|` 会静默冲突 | 运行时 `IndexOf('\|')` 校验 + SagaKeyValidationTests |
| ITM-002 / ADR-011 | Outbox 死信无重投递入口 | ops 直写库越权重置 | `RequeueDeadAsync` 三实现 + **幂等前提是调用方责任** |
| ITM-003 | Inbox SQLite TOCTOU 窗口 | `INSERT OR IGNORE`+`SELECT` 两步有竞态 | SQLite XML doc 弱保证 + PG `ON CONFLICT ... RETURNING` 单语句 |
| ITM-008 | PipelineStateMachine 单请求独占语义未标注 | `Reset` 跨请求复用导致并发污染 | XML doc 显式声明禁并发复用 |
| ITM-026 | OutboxDomainEventInterceptor 生命周期未断言 | 持有 `_pending` 实例字段，误改 Singleton 会并发交叉写入 | 架构测试 `OutboxDomainEventInterceptor_IsRegisteredAsScoped` |
| ITM-027 | Dispatcher.Register 冻结后非线程安全 | `Freeze()` 后 `Add` 抛 ObjectDisposedException | XML doc 约束启动期单线程 |
| ITM-030 | PeriodicBackgroundProcessor 非关停取消异常 | 内层 catch 捕获下游 ct 取消（非 host 关停）记为错误 | `catch (OperationCanceledException)` 静默分支 |
| 批次3 | PostgreSqlAuditor SQL 注入（QuoteIdentifier 无白名单） | 标识符未校验可注入 | 白名单 + `EscapeLiteral` 分离 |
| 批次3 | SqliteOutboxDbContext 硬编码 `DateTimeOffset.UtcNow` | 测试确定性 + 违反 §10.4 | 必须注入 TimeProvider |
| 批次4 | ExceptionMiddleware 把 OperationCanceledException 映射 500 | 取消异常应正常传播 | `when (ex is not OperationCanceledException)` 过滤 + RFC 9110 URL |
| §14 TUnit/MTP `-e` 误判 | 误判 `dotnet test` 需要 `-e TESTINGPLATFORM_COMMANDLINE_VERSION=2` | global.json runner 未生效 | 诊断三步骤 S1/S2/S3 反向验证 |

### II.2 高频复发模式（同一根因多处）

| 模式 | 复发处 | 沉淀 |
|------|-------|------|
| **OperationCanceledException 未过滤** | ITM-030 + ExceptionMiddleware + HealthCheck OBS-064 | conventions §10.3 + .ai review/engine 错误流卡 |
| **DIM 类型级契约误判**（S2326 IRequest/ICommand / S3246 IEventHandler\<TEvent\> 逆变） | 2 处 | `[SuppressMessage]` 带 Justification |
| **IDE0065 using 位置**（namespace 之前 vs 之后） | 2 处（1b360f1 / 962ab06） | conventions §1.2 ImplicitUsings |

---

## III. 编译期约束分层（替代 ORM Sonar S 规则表）

> ORM 项目的 lessons.md 第 III 章「SonarAnalyzer 规则层级（S2068/S6966/S3776）」在 DDD 项目**不适用**——DDD `.editorconfig` 采用 blanket `IDE.severity = none` 策略（框架库不强制编辑器样式偏好），无任何 Sonar S 规则显式 error 配置。

### III.1 DDD 真实策略（`Directory.Build.props` + `.editorconfig`）

| 层级 | 配置位置 | 规则来源 | 强制手段 |
|------|---------|---------|---------|
| **P0 编译期（不可抑制）** | `Directory.Build.props` 第 9 行 `TreatWarningsAsErrors=true` + 第 10 行 `AnalysisLevel=latest-all` | CS*/CA* 编译器警告 | 编译阻断 |
| **P0 AOT 硬约束** | `Directory.Build.props` 第 39-42 行 `IsAotCompatible/IsTrimmable/VerifyReferenceAotCompatibility/JsonSerializerIsReflectionEnabledByDefault` | AOT 分析器 IL2xxx/IL3xxx | 编译阻断 |
| **P1 框架特定** | `PalDDD.Analyzers/StrategicDddAnalyzer.cs` | PDDD001-015 战略 DDD 规则 | 编译期诊断 |
| **IDE 风格（全 none）** | `.editorconfig` 第 28-29 行 `dotnet_diagnostic.IDE.severity = none` | 框架不绑定样式 | 不强制 |

### III.2 NoWarn 21 条（Directory.Build.props 第 11-37 行逐条 Justification）

| 规则 | 含义 | Justification |
|------|------|--------------|
| CA1062 | 公共 API null 验证 | 框架内部调用链已保证非 null，公共入口用 ThrowIfNull |
| CA2007 | ConfigureAwait(false) | 库代码不绑定 SynchronizationContext，全层已显式 |
| CA1307 | StringComparison | 关键路径已 Ordinal，剩余为显示/日志 |
| CA2100 | SQL 注入审查 | 所有 SQL 经 SqlTemplates 常量 + Dapper 参数化 |
| CA2016 | CancellationToken 传递 | 少数 fire-and-forget 后台路径有意不传 |
| CA1721 | 属性名与类型名冲突 | `OutboxMessage.Status` / `OutboxStatus`，领域语义优先 |
| CA1031 | catch general exception | **不全局禁用**；Outbox/Inbox/Saga 用 `[SuppressMessage]` 精确抑制（§10.3） |
| CS1591/CS1573 | XML 文档 | 公共 API 已有 summary，internal/private 不强制 |
| CS8620 | 泛型协变/逆变 | DIM 桥接的 `Func<ValueTask<T>>` 无法消除 |
| NU1900-1904 | NuGet 漏洞/版本 | 已手动审计并升级传递依赖 |
| IL3058 | AOT 动态代码 | 非 AOT 适配器层显式 false |
| CA1305 | 区域性指定 | — |

测试项目额外 NoWarn（`test/Directory.Build.props`）：CA1515 / CA1707 / CA1711 / CA1508 / CA1812 / CA2000 / CA2007 / CA1034 / CA2263

---

## IV. AI Agent 编码约束（conventions §10 全文要点）

> 完整版见 `docs/conventions.md` 第 719-808 行（§10）。本节是 AI 生成代码时的核心约束摘要。

### IV.1 禁止退化反射（5 类映射）

| 退化模式 | 合规替代 |
|---------|---------|
| `MakeGenericType` | DIM 桥接 + `typeof(T)` 编译时常量 |
| `Activator.CreateInstance` | 显式 DI 注册 |
| `Assembly.GetTypes()` | 源码生成器 `[ModuleInitializer]` |
| `Type.GetType(string)` | `typeof(T)` |
| 反射 JSON (`JsonSerializer.Serialize<T>`) | `[JsonSourceGenerationOptions]` + `[JsonSerializable]` |

### IV.2 表达式树禁 Expression.Invoke

EF Core LINQ 翻译无法处理 `Expression.Invoke`。规约 And/Or/Not 必须**参数替换**（`ParameterReplacer`，internal）。

### IV.3 异常过滤（conventions §10.3）

```csharp
// ✅ 合规：过滤取消异常
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // ...
}

// ✅ 合规：后台处理器带 SuppressMessage + 具体理由
[SuppressMessage("Design", "CA1031",
    Justification = "Outbox processor must isolate arbitrary exceptions to protect batch loop")]
public async Task ExecuteAsync(CancellationToken stoppingToken)
{
    try { /* ... */ }
    catch (Exception ex) when (ex is not OperationCanceledException) { /* log */ }
}

// ❌ 违规：裸 catch(Exception) 无过滤（ITM-030 同型）
catch (Exception) { /* ... */ }
```

**唯一例外**：事务回滚路径允许裸 `catch`（清理异常挂 `Exception.Data`，不覆盖主异常）。

### IV.4 并发安全（conventions §10.4）

- `Dispatcher.Freeze()` 后转 `FrozenDictionary`——**禁运行时 Add**
- `InMemory*Store` 用 `Lock`（.NET 9+，非 `lock(object)`）
- `TimeProvider` 注入——**禁硬编码 `DateTimeOffset.UtcNow`**（PDDD-G17 强制）
- `SagaState.CurrentState` **不能含 `|` 字符**（ITM-001 SagaKey 分隔符冲突）

### IV.5 提交前验证（AI 必跑，4 步）

```bash
dotnet build PalDDD.slnx                                # 0 警告 0 错误
dotnet test PalDDD.slnx --no-restore --no-build         # 零失败
PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1 dotnet build       # 如改公共 API（先确认 diff 评审后提交）
bash scripts/verify-conventions.sh --quick              # grep 静态检查
```

### IV.6 TUnit + MTP 测试框架（4 硬规则，conventions §10.6）

1. **禁引用 `Microsoft.NET.Test.Sdk`**（与 TUnit MTP 冲突）
2. **`global.json` 必配** `test.runner=Microsoft.Testing.Platform`
3. **MTP 参数在 `--` 之后**（如 `dotnet test -- --report-trx`）
4. **`test/Directory.Build.props` 已条件化设置** `IsTestingPlatformApplication` 等

---

## V. 诊断三步骤（conventions §14 全文引用）

> 排查配置/环境类问题时，原则不够，必须走完可执行的三步。跳过任一步 = 归因不可信。

### V.1 S1 基线快照

任何修改前先记录当前状态的可验证数据（不凭记忆）。

```bash
dotnet test PalDDD.slnx 2>&1 | tail -5   # 记录：exit code、通过/失败数、耗时
```

用途：区分"本来就坏的"vs"我改坏的"。无基线则无法归因。

### V.2 S2 单变量隔离

一次只改一个变量，改完立即测并记录。**禁止 2+ 变量同时改动后一次测试**。

`✅ 改 A → 测 → 记 → 改 B → 测 → 记`　`❌ 改 A+B+C → 一次测 → 归因模糊`

### V.3 反向验证（最关键）

任何疑似修复因子在采纳前必须反转一次：移除后退化→必要→采纳；移除后仍通过→多余→丢弃。

`✅ 加 -e → 通过 → 去 -e → 仍通过 → -e 多余 → 丢弃`
`✅ 加 -e → 通过 → 去 -e → 退化到 exit 5 → -e 必要 → 采纳`
`❌ 加 -e → 通过 → 直接采纳（未反转）→ 把"恰好在工作"误判为"必要"`

**真实案例（§14）**：误判 `dotnet test` 需要 `-e "TESTINGPLATFORM_COMMANDLINE_VERSION=2"` 环境变量。S3 反向验证：移除后仍通过 → 实际根因是 global.json runner 未生效 → `-e` 多余 → 丢弃。

### V.4 结论标注

未做 S3 反向验证的修复因子，只能标 [推断]，不得标 [事实]，不得写入规范。跨项目移植结论须在 B 项目重跑三步骤，不得直接套用。

---

## VI. 战略 DDD 治理（PDDD001-015 编译期诊断）

> 真源：`src/PalDDD.Analyzers/StrategicDddAnalyzer.cs`。15 条规则在编译期强制战略 DDD 约束，AI 生成代码时必须遵守，否则编译失败。

| 规则 | 严重性 | 约束 |
|------|:------:|------|
| PDDD001 | Error | 领域模型类型必须声明 `[BoundedContext]` |
| PDDD002 | Warning | BC 名必须小写字母/数字/-/. |
| PDDD003 | Error | ProcessManager 必须 sealed + [BoundedContext] + IEventHandler\<TEvent\> |
| PDDD004 | Error | ProjectionHandler 必须 sealed + [BoundedContext] |
| PDDD005 | Error | DomainEvent 必须声明 `[GenerateMessage]` |
| PDDD006 | Warning | ProcessManager 名必须稳定小写 |
| PDDD007 | Warning | Projection 名必须稳定小写字符串字面量 |
| PDDD008 | Warning | 消息名必须以 BC 前缀开头 |
| PDDD009 | Warning | 消息名必须稳定小写 |
| PDDD010 | Warning | 消息名必须以 `.v{N}` 版本后缀结尾 |
| PDDD011 | Error | SchemaVersion 必须 ≥ 1 |
| PDDD012 | Error | DomainEvent 必须 sealed（事件契约对回放/序列化关闭） |
| PDDD013 | Warning | Projection 名必须属于 BC |
| PDDD014 | Warning | ProcessManager 名必须属于 BC |
| PDDD015 | Warning | DomainEvent.EventName 必须与生成消息名匹配 |

CodeFix 提供：PDDD008 / PDDD010 / PDDD013 / PDDD015。

---

## VII. 评审纪律 R0-R8（conventions §13）

> 历史误判率 **38%**、遗漏 **20%**——以下 9 条纪律是 DDD 项目从评审历史中沉淀的方法论。
> 2026-07-28 更新：全项目四系统审查中，review 子代理 P0 误判率 **36%-75%**（11 个 P0 中仅 7 个为真；PalORM 适配层 8 个 P0 中仅 2 个为真）。R0 三核实原则的必要性再次被实证。

### R0 可信度标注（核心）

每个发现必须标注：
- ✅ **完整审计**：覆盖度 100%，可下确定结论
- ⚠ **基于抽样**：部分区域未审，结论需声明局限
- ❓ **待验证**：未读源码或未运行命令，禁止下确定结论

**基于 ⚠/❓ 禁止下确定结论。**

### R1-R8 简表

| 编号 | 纪律 | 反面实例 |
|:--:|------|---------|
| R1 | 完整读取（引用代码块必须读完整方法体） | 读半截 lock 块下并发结论 |
| R2 | 语义场景区分 | 同一模式在不同场景合规/违规 |
| R3 | 当前 commit 锚定（`git log` + 实际代码核实） | 跨会话 summary 失真 |
| R4 | grep 语义核查（计数 ≠ 语义判断） | grep N 处 catch → 直接当"未过滤"结论 |
| R5 | 外部输入交叉验证（任务清单方法名 grep 存在性） | 任务描述方法名与源码不符 |
| R6 | 分析器行为必须 dotnet build 验证（不靠记忆） | 带过滤的 catch 不触发分析器 |
| R7 | 架构测试覆盖度审计（断言数据覆盖全部应覆盖项） | 硬编码 Theory 漏检 |
| R8 | 实测优先（数字声明必须运行命令获取） | 采信记忆中的 API 存在性 |

### 优先级体系（危害 × 复杂度双维度）

替代旧 P1/P2/P3 时间紧迫度：

| | 高危害 | 中危害 | 低危害 |
|------|:------:|:------:|:------:|
| **易修复**（< 1h） | P0 紧急 | P1 近期 | P2 |
| **中等**（1-4h） | P1 近期 | P2 | P3 |
| **难修复**（> 4h） | P2 | P3 | 评估（产出 ADR） |

---

## VIII. AI 启动检查清单

> AI 会话开始时自动加载本节，确认当前项目状态。

```
1. git status——工作树清洁？（PDDD-G22）
2. dotnet build PalDDD.slnx——0 警告 0 错误？
3. StrategicDddAnalyzer（PDDD001-015）——编译期规则全绿？
4. .editorconfig——IDE 全 none 策略未变？
5. docs/conventions.md——14 章 + 附录已读最新版？
6. src/PalDDD.Prompts/.pal/prompts/——8 个 AI 提示模板未偏离 conventions §7？
7. Directory.Build.props——VersionPrefix=1.0.0 / VersionSuffix=preview.1？
8. PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS——公共 API 变更走快照评审？
9. bash scripts/verify-conventions.sh --quick——静态检查通过？
```

---

## IX. 真源交叉引用

| 文件 | 用途 | 与本文件的关系 |
|------|------|--------------|
| `docs/conventions.md` | 14 章规范 + 附录执行矩阵 | **规范真源**——本文件 IV/V/VII 引用 §10/§13/§14 |
| `docs/architecture.md` | 18 项架构决策 + 稳定性约束 7 条 | 架构流对照基准 |
| `docs/aot.md` | AOT 与性能约束 | PDDD-G14/G15/G16 真源 |
| `docs/performance.md` | 性能契约 | 精炼系统基准 |
| `docs/decisions/001-016` | 16 个 ADR | 决策依据（Outbox 批量/MemoryPack/IValueObject 等） |
| `test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs` | 33 测试方法机械守护 | **G 表真源**——核心不变式 |
| `src/PalDDD.Analyzers/StrategicDddAnalyzer.cs` | PDDD001-015 | **战略 DDD 编译期治理** |
| `src/PalDDD.Prompts/.pal/prompts/` | 8 个 AI 提示模板（六段结构） | AI 协作模板（aggregate-root/saga-orchestrator 等） |
| `.ai/gate/prompt.md` | 门禁系统 | PDDD-G1..G22 对应本文件 III/IV 章 |
| `.ai/refine/prompt.md` | 精炼系统 | 24 项操作矩阵 |
| `.ai/review/prompt.md` | review 系统 | 七流方法论 + 评审纪律 R0-R8 |
| `.ai/test/prompt.md` | 测试规范 | TUnit+MTP 铁律 |
| `Directory.Build.props` | AOT 四属性 + NoWarn 21 条 | 本文件 III.2 引用 |
| `.editorconfig` | IDE 全 none 策略 | 本文件 III.1 引用 |

---

## X. 案例引用（DDD 真实 commit）

| 教训 | commit | 文件 |
|------|--------|------|
| ITM-001 SagaKey `\|` 校验 | `c7b8005` | `src/PalDDD.Transactions/SagaKey.cs` |
| ITM-002 / ADR-011 RequeueDeadAsync | `164a22c` | `IPalOutboxStore.cs` + 三实现 |
| ITM-003 Inbox SQLite TOCTOU | `b572b22` | InboxStore SQLite XML doc |
| ITM-008 PipelineStateMachine 单请求独占 | （XML doc） | `src/PalDDD.CQRS/PipelineStateMachine.cs` |
| ITM-026 OutboxDomainEventInterceptor Scoped | `5e85182` | Repository.EFCore/ServiceCollectionExtensions.cs |
| ITM-027 Dispatcher.Register 冻结约束 | `1c68e34` | Dispatcher XML doc |
| ITM-030 PeriodicBackgroundProcessor 取消异常 | `da287f7` | PeriodicBackgroundProcessor.cs |
| 批次3 PostgreSqlAuditor SQL 注入 | `8ff6bc1` | PostgreSqlAuditor.cs（QuoteIdentifier 白名单） |
| 批次3 SqliteOutboxDbContext TimeProvider | `8ff6bc1` | SqliteOutboxDbContext.cs |
| 批次4 ExceptionMiddleware 取消不映射 500 | `f239302` | ExceptionMiddleware.cs + RFC 9110 |
| §14 TUnit/MTP `-e` 误判 | （conventions §14） | global.json runner 配置 |
| SagaCompensation ExecutedStepKeys | `18cf86b` | SagaCompensation.cs |
| S2326 IRequest/ICommand SuppressMessage | `3b26afd` | CQRS DIM 类型级契约 |
| S3246 IEventHandler\<TEvent\> in 逆变 | `fa1cbf8` | Messaging IEventHandler |
| ITM-031 SagaProcessor 非超时 Saga 租约泄漏 | `e892da8` | SagaProcessor.cs（P0-FIX-3） |
| ITM-032 EventStreamJsonLines eventId 用 GetGuid 解析 ULID | `e892da8` | EventStreamJsonLines.cs（P0-FIX-1） |
| ITM-033 PostgreSqlJsonb JSON 反斜杠注入 | `e892da8` | PostgreSqlJsonbExtensions.cs（P0-FIX-5） |
| ITM-034 OutboxDomainEventInterceptor 生产路径未注册 | `e892da8` | ServiceCollectionExtensions.cs（P0-FIX-4） |
| ITM-035 EventLogPositionReserver DbContext 并发不安全 | `e892da8` | EventLogPositionReserver.cs（P1-FIX-2） |
| ITM-036 DbUpdateException catch 未区分唯一约束冲突 | `e892da8` | EventLogDbContext.cs（P1-FIX-3） |

---

## 维护规则

1. **缺陷只增不删**：新发现的 DDD 缺陷追加到 II 章。
2. **不套用 ORM 经验**：ORM 的 B1-B27（RowFactoryEmitter/QueryBuilder/MigrationEmitter/三方言/TUnit 0.19→1.61 升级等）**不是 DDD 项目踩过的坑**，不得作为 DDD 教训引用（Karpathy 准则 9）。
3. **每条 ITM 必须可追溯**：引用必带 commit hash 或文件路径。
4. **不编造数字**：ITM 总数、规则条数等以实际 grep/读文件为准，不承诺未经核实的数字。
5. **章节变更需评审**：新增/修改本文件章节必须在 PR 中说明依据。
6. **三方一致**：本文件与 conventions.md §10/§13/§14、ArchitectureBoundaryTests.cs、PalDDD.Analyzers 保持同步。改本文件必须同步检查这三处。

---

## XI. PalORM 适配层 + 全项目四系统审查教训（2026-07-28）

> 以下教训来自 21 次提交的 PalORM 适配层实施 + 全项目四系统审查（200 文件 / 19497 行），经实测验证。

### XI.1 源生成 ORM 约束（PalORM 适配层新增，6 条）

| 教训 | 详情 | 反模式 |
|------|------|------|
| **SourceGen analyzer 不随 Provider 包传递** | PalORM.SourceGen 是独立 analyzer 包，Provider 包用 `exclude="Build,Analyzers"` 引用 Core —— 消费项目必须显式引用 | ❌ 假设 Provider 包传递 SourceGen |
| **`SELECT *` 列序错位** | ColumnOrderValidator 按 DTO 属性声明序匹配 DB 列序。DDL 列序 ≠ DTO 序时静默错位 | ❌ 用 `SELECT *` + 假设列序与 DTO 一致 |
| **未注册实体 QueryFirstAsync 返回空对象** | 对未注册类型（无 [Table]）返回默认构造的空对象，不抛异常。复合主键表必须用 GetRawConnection + DbDataReader | ❌ 假设 QueryFirstAsync<T> 对未注册类型抛异常 |
| **FormattableString 拼接退化为 string** | `$"" + const + $""` 编译为 string 而非 FormattableString。ExecuteAsync/QueryAsync 只接受 FormattableString | ❌ 用 `$"" + 变量 + $""` 拼接 PalORM SQL |
| **PalORM Commit/Rollback 后必须 UseTransaction(null)** | DataSession 内部 OperationState 持有事务引用，Commit/Rollback 只释放 DbTransaction 不清除引用 | ❌ Commit 后直接用同一 session 执行新查询 |
| **DataSession 不支持同实例并发操作** | AsyncLocal 门禁禁止重叠 await。并发场景必须每 worker 独立 DataSession | ❌ 多 worker 共享同一 DataSession + Task.WhenAll |

### XI.2 多方言 SQL 陷阱（3 条）

| 教训 | 详情 | 反模式 |
|------|------|------|
| **MySQL 不支持 `UPDATE...WHERE id IN (SELECT...LIMIT)`** | `LIMIT & IN/ALL/ANY/SOME subquery` 语法限制。必须用 `UPDATE t JOIN (SELECT...LIMIT) AS sub ON ...` 替代 | ❌ 假设三方言 SQL 子查询语法一致 |
| **MySQL `key` 是保留字** | `` `key` `` 反引号可解但方言不统一（PG/SQLite 不用反引号）。正确做法：改列名（`key` → `idempotency_key`） | ❌ 用反引号转义保留字 + 假设三方言一致 |
| **PG PascalCase 折叠问题** | PG 折叠无引号标识符为小写。PalORM 手写 SQL 不加引号 —— PascalCase 列名与建表 DDL 不匹配。统一 snake_case 解决 | ❌ PalORM 手写 SQL 用 PascalCase 列名 |

### XI.3 Review 七流误判率实证（核心方法论更新）

> **数据来源**：本次全项目 review 子代理报告 11 个 P0，经 R0 三核实甄别后仅 7 个为真 P0（误判率 36%）。PalORM 适配层 review 报告 8 个 P0，甄别后仅 2 个真 P0（**误判率 75%**）。

| 教训 | 详情 | 反模式 |
|------|------|------|
| **Review 子代理 P0 误判率 36%-75%** | 子代理基于推理（未读完整调用链/未写复现测试）定级 P0。必须 R0 三核实后才能采纳 | ❌ 子代理 P0 定级直接采纳不经甄别 |
| **R0 三核实是降低误判的唯一手段** | 逐项核实：①与 Dapper/EFCore 行为对齐？②ASP.NET Core 宿主下真有风险？③是否框架库非 Web API？ | ❌ 跳过 R0 直接修复子代理报告的 P0 |
| **全项目 review 须分层并行** | 200 文件/19497 行无法单 Agent 逐行。按 Clean Architecture 分 4 层并行，每层 Agent 独立报告 | ❌ 单 Agent 逐文件审 200 个文件（超 token 限制） |

### XI.4 敏感信息教训

| 教训 | 详情 | 反模式 |
|------|------|------|
| **数据库连接串不可硬编码到测试代码** | 快速验证时直接写入远程数据库连接串默认值 → git 历史 filter-branch 清除 + GC prune。改用环境变量 + 缺失时 throw | ❌ 在代码中写 `?? "Host=192.168...;Password=..."` 默认值 |

---

## XII. AI 编码行为准则增强（从工程实践独立沉淀 · 2026-07-29）

> 以下规则来自 PalORM 适配层实施 + 全项目三轮四系统审查中 AI 的真实失误模式。
> 每条规则独立自含，不引用任何外部文件。与全局配置互不依赖、互不引用。

### XII.1 AI 结构性偏差 — PalDDD 场景化清单（6 条 · 写/审代码强制对照）

> AI 训练数据 85% 是 happy path，PalDDD 的事务/租约/并发场景全部依赖错误路径覆盖。
> 以下偏差在本次实施中全部真实发生过（附 ITM/commit 追溯）。

| 编号 | 偏差 | PalDDD 具体案例 | 强制要求 |
|------|------|----------------|---------|
| SPD-1 | **只测顺序不测真并发** | Outbox 租约原测试只顺序两次 Lease 验第二次为空——Task.WhenAll 多 worker 并发未覆盖 | 新增 Store 必须有至少 1 个 Task.WhenAll 并发测试 |
| SPD-2 | **返回值被静默丢弃** | `DapperEventLog.AppendAsync` 中 `expectedVersion.Matches()` 返回值未检查（ITM-P0-2, `57ed3fa`）——乐观并发控制完全失效 | 任何 bool/ValueTask 返回值必须检查或显式丢弃（`_ = expr`） |
| SPD-3 | **只覆盖部分路径** | `SagaProcessor.CheckTimeoutsAsync` 只在超时分支清空租约，非超时分支跳过——租约泄漏 2 分钟（ITM-031, `e892da8`） | if/else/switch 每个分支必须检查"资源是否在该路径释放" |
| SPD-4 | **三方一致遗漏** | EventLogSql 改 snake_case 后 DapperStoreTests DDL 未同步（`57ed3fa`）——编译过但运行时列名不匹配 | 改 SQL 列名 → 同步改 DDL + 测试 DDL + ORM Row DTO [Column] |
| SPD-5 | **注释与代码矛盾** | `[module:DapperAot]` 注释禁用但 SqliteRowFactory 声称"已启用"（`e892da8`）——误导后续维护者 | 修改代码行为时必须同步修改相关注释（文件头/类摘要/XML doc） |
| SPD-6 | **设计契约误判为 bug** | `OutboxMessage.Status` Dapper=string（`'Pending'`）/ EFCore=int（`0`）看似不一致——实际是双实现的历史契约，业务已围绕建立 | 改动前必须问"这个看似 bug 的行为是否是设计意图/生产契约？"不确定则问用户 |

### XII.2 破坏性操作黑名单 — PalDDD 扩展（2 条 · 追加到 I 章铁律）

> 通用黑名单（`rm -rf` / `git push --force` / `DROP TABLE` / PR 合并等）此处不重复。
> 以下 2 条是 PalDDD 项目特有的破坏性场景。

| # | 操作 | 后果 | 执行规则 |
|---|------|------|---------|
| 7 | **清空远程共享数据库** | `CleanAllTablesAsync` 在 PG/MySQL `palorm_bench` 上 `DROP TABLE`——多测试并行时竞争，且连错库会清空生产数据 | 必须用户确认连接串指向测试库；跨方言测试必须 `[NotInParallel]` |
| 8 | **删除 `.ai/` 目录** | `.ai/` 是 AI 质量系统自包含目录——删除 = 失去全部项目约束（PDDD-G 门禁、七流 review 引擎、误判库 PD1-PD14、ITM 缺陷登记） | 永远不要删除；重构可改内容但不可删目录 |

### XII.3 Agentic 工程规则 — PalDDD 场景化（6 条 · 追加到 I/IV 章）

> 以下规则在 PalORM 适配层实施中全部被实践验证。

| # | 规则 | PalDDD 案例 | 违反后果 |
|---|------|------------|---------|
| 7 | **不留占位符代码** | `_Placeholder.cs` 在 Row DTO 就绪后立即删除（`22c950b`） | 占位符进测试会掩盖真问题 |
| 8 | **引入新依赖前必须验证存在且版本有效** | PalORM.SourceGen 是独立 analyzer 包，Provider 包不传递——不验证就运行时空对象（`22c950b`） | `dotnet list package` / 查 NuGet.org，不凭记忆声称 |
| 9 | **长会话每完成一个里程碑自动 commit** | PalORM 适配层 23 次提交，每完成一个 Store 就 commit | 防上下文丢失/窗口压缩导致返工 |
| 10 | **实现复杂功能前先输出接口签名让用户确认** | PalORM 先设计 6 个 Row DTO → 用户确认 → 再实现 7 Store | 阻断"200 行错误方向" |
| 11 | **源生成器改动后必须清 obj/bin** | 改 EventLogRow `[Column]` 后不清 obj/bin → 源生成器用旧 emit → 运行时空对象 | `rm -rf src/*/obj src/*/bin` + 全量 build |
| 12 | **跨方言测试串行化** | PG/MySQL 共享远程数据库 `palorm_bench`，并行 DROP/CREATE 竞争 | `[NotInParallel("palorm-multidialect")]` 或用唯一表名隔离 |

### XII.4 重构防护 — PalDDD 场景化（3 条 · 新增 IV.7 节）

> AI 在重构中有特定认知盲区。以下 3 条从 PalDDD 实施中验证。

| # | 规则 | PalDDD 案例 | 检查方式 |
|---|------|------------|---------|
| IV.7.1 | **bug-like 行为可能是生产契约** | `OutboxMessage.Status` Dapper=string / EFCore=int 不一致——看似 bug 但业务已围绕建立 | 改动前问用户"这是设计意图还是 bug？" |
| IV.7.2 | **跨文件依赖是崩溃边界** | EventLogRow 改 `[Column]` → EventLogStore SQL → DDL → 测试 DDL → appsettings.test.json 5 处联动 | 发现自己在级联修改 3+ 文件时立即停，拆分提交 |
| IV.7.3 | **Debug 时必须看最近改动** | EventLogSql snake_case 改动后测试失败 → 不看 `git diff HEAD~3` 就猜不到 DDL 未同步 | debug 第一步 `git log --since="2 hours ago"` + `git diff HEAD~3` |

### XII.5 代码价值判定 — 框架库特化（1 条 · 新增 V.5 节）

> PalDDD 是框架库（30+ NuGet 包），public API 零内部引用是**常态而非死代码信号**。

**删除前必须交叉验证 6 项**（全部无保留证据方可建议删除）：

1. **区分代码性质**：应用代码（无消费方≈死代码）vs 框架库（API 面向外部使用者，早期无内部消费方是常态）
2. **文档交叉验证**：grep `docs/` 是否将该 API 列为特性/能力
3. **Roadmap 演化**：查 docs/design/ 的"未来计划"章节
4. **Git 演进**：`git log --oneline -- <path>` 查 feat 提交链
5. **测试覆盖**：是否有专门测试文件
6. **NuGet 包验证**：查 `nupkgs/` 或 NuGet.org 是否已发布——已发布的 public API 不可随意删（消费者可能已在用）
