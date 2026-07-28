# 已知误判模式（Pal.DDD 审计系统知识库 v1.0）

> 来源：通用模式 1-8（跨项目适用）+ Pal.DDD 专项模式 PD1-PD9（项目自有）。
> 每次 `/review` 阶段 2 执行前自动加载。维护：发现新误判模式后追加到本文。
> 标注来源（commit / ITM / ADR / .codebuddy/memory / conventions 章节）。

---

## 速版（子代理提示词内嵌用——每模式一行；完整版在下方，主线程定稿门用完整版）

### 通用模式（1-8，跨项目适用）

1. lock 只读前半段→读完整 lock 块再下并发结论
2. grep 计数差异≠语义差异→逐行读每个命中点
3. catch(Exception) 前可能有 catch(OCE){throw;}→查前置 catch
4. 分析器行为必须 dotnet build 验证→不许猜（PDDD001-015 同）
5. "X 缺失"必须当前 commit ls/grep 验证→不采信记忆或旧报告
6. 评分/观感与实现质量脱节→先查覆盖度
7. grep 表面 ✅ 不算查过→Read 验证后才可 ✅
8. 外部任务的方法名/类名先 grep 存在性

### Pal.DDD 专项模式（PD1-PD9）

PD1. *.g.cs 生成文件不评（GenerateId/GenerateEnum/MessageRegistry emit）→只评手写代码
PD2. Broker 抽象（InMemory/Kafka/RabbitMQ）代码相似=刻意独立≠DRY 违规
PD3. IsAotCompatible=false 在 EF Core/Kafka/MemoryPack 适配器层=设计本意，非 AOT 违规
PD4. catch (Exception) when(ex is not OCE)=合规过滤，非异常吞噬（conventions §10.3）
PD5. [SuppressMessage("Design","CA1031",Justification=...)] 在 Outbox/Inbox/Saga 后台处理器合规
PD6. null! + [ModuleInitializer] 填充的 static 属性合规（MessageCatalog 同模式）
PD7. TryAdd* 优先≠必 Singleton，OutboxDomainEventInterceptor 必须 Scoped（持有 _pending）
PD8. 元包项目（PalDDD.Extension/PalDDD.Base）只含 .csproj 无 .cs=设计本意
PD9. 判断 NuGet 包存在性必须查 nupkgs/ + NuGet.org，不靠仓库 csproj
PD10. sync-over-async 在 ASP.NET Core 无 SyncContext 不会死锁 → 确认宿主再定级
PD11. 接口契约设计限制导致的 sync-over-async（void 返回）≠ 实现 bug → 降级 P1
PD12. 框架库 API 权限边界（projectionName 来自代码常量非用户输入）→ 降级 P3
PD13. GetRawConnection 事务隔离是 PalORM 设计限制 ≠ 适配层 bug → 降级 P1
PD14. 与既有实现（Dapper/EFCore）对齐的行为不是新 bug → 降级 P1

---

## 通用模式（跨项目适用 · 1-8）

### 模式 1：lock 块只读前半段 → "并发竞态"

**反例**：评审建议某方法存在并发竞态。实际完整状态转换已在 `lock (_lock)` 内完成，评审只读了 lock 块的前半段。

**如何避免**：读完整 lock 块体。确认被保护的所有修改是否都在同一 lock 内。

---

### 模式 2：grep 计数差异 → 直接当语义结论

**反例**：grep 显示 N 处 catch(Exception) 但只有 M 处 when(is not OCE)，初判"异常过滤不一致"。实际其余使用了"前置 catch(OCE){throw;}" 模式。

**如何避免**：grep 做定位不做计数判断。数字差异 ≠ 语义差异。逐行读每个 catch 的前一行。

---

### 模式 3：catch(Exception) 前已有 catch(OCE){throw;}

**反例**：评审建议某方法增加 OCE 过滤。实际已有 `catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)` 在前。

**如何避免**：读 catch(Exception) 的上一个 catch 块。如果已有 `catch(OperationCanceledException){throw;}`，则当前 catch 不会捕获 OCE——这是合规模式，不是遗漏。

---

### 模式 4：带 when 过滤的 catch 不触发分析器

**反例**：预估 N 处 catch(Exception) 需添加 [SuppressMessage]，实际 `dotnet build` 后仅少数报错。带 `when(ex is not OCE)` 的 catch 不触发 CA1031 分析器。

**如何避免**：任何涉及分析器触发条件的判断，必须先 `dotnet build` 验证。禁止基于"分析器应该会"的假设。

---

### 模式 5：基于过期快照的缺失判断

**反例**：评审声称某文件缺失。实际已存在，评审基于旧版本快照。

**如何避免**：所有"X 不存在""X 缺失"的判断，必须在当前 commit 上 grep/`ls` 验证。

---

### 模式 6：评分与实现质量脱节

**反例**：评审评分高但连接泄漏未被发现。

**如何避免**：评分前先完成七流全量审计。若审计覆盖度未达 100%，评分需明确降级并注明"覆盖度为 X%"。

---

### 模式 7：grep 表面 ✅ → 跳过深度方法读取

**反例**：首次审计全部 ✅，零 P0/P1。补充深度审计后发现了真实问题。

**如何避免**：grep 定位 → Read 验证 → 才可 ✅。每条流的覆盖度必须达标。

---

### 模式 8：外部任务方法名/类名未交叉验证

**反例**：任务描述中方法名与实际源码不符。

**如何避免**：外部合并的任务在写入 action-items 前，逐项 grep 方法名/类名/路径在源码中是否存在。

---

## Pal.DDD 专项模式（PD1-PD9）

### 模式 PD1：源生成器生成的 `.g.cs` 文件被审计

**反例**：审计发现 `PalDDD.Core.SourceGen` 生成的 `.g.cs` 文件中存在代码异味。实际这些文件是编译时生成的（GenerateId/GenerateEnum/MessageRegistryGenerator emit），不应被审计。

**如何避免**：`find src -name "*.g.cs"` 识别生成文件 → 排除出审计范围。审计只覆盖手写 `.cs` 文件。

---

### 模式 PD2：Broker 抽象间代码重复 ≠ DRY 违规

**反例**：审计标记 InMemory/Kafka/RabbitMQ 三个 Broker 实现存在重复代码。实际各 Broker 因传输语义差异故意独立（如 Kafka offset commit vs RabbitMQ ack vs InMemory 即时返回），共享会引入不必要的抽象层。

**如何避免**：区分"偶然重复"和"刻意独立"。Broker 间相似代码是各消息中间件的独立实现，不是 DRY 违规（conventions §8 多 Broker 适配策略）。

---

### 模式 PD3：非 AOT 适配器层显式 `IsAotCompatible=false`

**反例**：审计标记 PalDDD.EntityFrameworkCore / PalDDD.Messaging.Kafka 等 14 个项目 `IsAotCompatible=false` 违反 AOT 红线。实际这些项目依赖反射或第三方库（EF Core/Kafka client/MemoryPack），**显式声明 false 是设计本意**（ArchitectureBoundaryTests `InfrastructureAdapters_AreExplicitlyNonAot` 强制）。

**如何避免**：检查项目所在分层。**AOT 核心层 7 项目**（Core/Serialization/CQRS/EventLog/Idempotency/Projections/Messaging）必须 true（继承 Directory.Build.props）；**非 AOT 适配器层 14 项目**（含 Transactions/EFCore.*/Kafka/RabbitMQ/MemoryPack/Analyzers.CodeFixes）必须显式 false。

**特例 PalDDD.Transactions**：因 Saga 子系统用 MakeGenericMethod/Activator（已带 [RequiresDynamicCode] 标注）主动声明 false。这是设计决策，见 csproj 注释。

---

### 模式 PD4：`catch (Exception) when(ex is not OCE)` 合规过滤

**反例**：审计标记 `catch (Exception ex) when (ex is not OperationCanceledException)` 为异常吞噬。实际这是 conventions §10.3 规定的合规模式——过滤取消异常后捕获其他异常。

**如何避免**：检查 catch 是否带 `when(ex is not OperationCanceledException)` 过滤。如果有，是合规模式，不是异常吞噬。

**机械核验**（已下沉到 PDDD-G + boundary）：
```bash
# catch(Exception) 所在方法若带 when(ex is not OCE) 过滤 → 合规
grep -rn "catch (Exception" src/ --include='*.cs' | grep -v 'is not OperationCanceledException'
```

---

### 模式 PD5：后台处理器 `[SuppressMessage("Design","CA1031")]` 合规

**反例**：审计标记 OutboxProcessor/InboxProcessor/SagaProcessor 的 `[SuppressMessage("Design", "CA1031", Justification = "...")]` 为绕过分析器。实际这是 conventions §1.8 + §10.3 规定的合规模式——后台处理器必须隔离任意异常保护批处理循环。

**如何避免**：检查 SuppressMessage 是否带具体 Justification（英文说明原因）。如果有，是合规模式（conventions §1.8 NoWarn 21 条逐条 Justification 策略）。

---

### 模式 PD6：`null!` + `[ModuleInitializer]` 填充的 static 属性

**反例**：审计标记 MessageCatalog 某些 `static` 属性 `null!` 初始化为潜在 NRE。实际这些属性由 `[ModuleInitializer]` 在启动时一次性填充 `FrozenDictionary`，之后不可变。`null!` 是 C# 必需语法（static 属性必须有初始值）。

**如何避免**：检查是否有 `[ModuleInitializer]` 方法填充了这些属性。如果有，`null!` 是合规模式——不是缺陷。

---

### 模式 PD7：`TryAdd*` 优先 ≠ 必然 Singleton

**反例**：审计假设所有 `TryAdd*` 注册的都是 Singleton 生命周期。实际 DDD 项目中：

- **Dispatcher/IUnitOfWork/Handler** → Singleton（启动期 Freeze）
- **OutboxDomainEventInterceptor** → **必须 Scoped**（持有 `_pending` 实例字段，Singleton 会并发交叉写入，ITM-026）
- **DbContext** → Scoped（DI 容器管理生命周期）

**如何避免**：检查类型是否持有实例字段。持有的 → 必须 Scoped；无状态的 → Singleton。架构测试 `OutboxDomainEventInterceptor_IsRegisteredAsScoped` 强制断言。

---

### 模式 PD8：元包项目只含 .csproj 无 .cs 源码

**反例**：审计标记 PalDDD.Extension / PalDDD.Base 项目"无源码文件 = 死项目"。实际这些是**元包**（metapackage）——只含 PackageReference 集合，无 .cs 文件，设计本意就是按需引用一组包。

**如何避免**：检查项目 csproj 是否只含 `<PackageReference>` 无 `<Compile Include>`。如果是，是元包设计。

---

### 模式 PD9：判断 NuGet 包存在性必须查 nupkgs/ + NuGet.org

**反例（.codebuddy/memory/）**：评审 F-001 初版误判"3 个不存在的 NuGet 包"（评分 8.2 → 用户指正后撤销降级 OBS-068，评分 8.2→8.4）。实际这些包在 `nupkgs/` 目录或 NuGet.org 都存在，仅凭仓库 .csproj 判断不可信。

**如何避免**：判断 NuGet 包是否存在：
1. 先查 `nupkgs/` 目录（本地打包输出）
2. 再查 NuGet.org（远程仓库）
3. 不能仅凭仓库 .csproj 的 `<PackageReference>` 判断（可能是中央包管理 `Directory.Packages.props`）

---

## 待积累模式（PD10+）

### 模式 PD10：sync-over-async 在 ASP.NET Core 宿主下不会死锁

**反例**：评审标记 PalORM OutboxStore 的 4 处 `.GetAwaiter().GetResult()` 为 P0 死锁风险。实际 ASP.NET Core 默认无 SynchronizationContext，sync-over-async 不会死锁。`IPalOutboxStore.AddMessage` 等是同步接口（来自 PalDDD.Transactions），PalORM 全异步实现必须 sync-over-async。

**如何避免**：确认目标宿主。ASP.NET Core / Console Host 无 SynchronizationContext → 不会死锁。仅桌面/测试宿主（xUnit `[Theory]`）有 SyncContext → 风险。定级前确认宿主类型。

---

### 模式 PD11：接口契约设计限制导致的 sync-over-async（非实现 bug）

**反例**：评审标记 PalORM OutboxStore sync-over-async 为"整个适配包最严重的设计缺陷"。实际 `IPalOutboxStore.AddMessage` / `MarkProcessed` / `MarkDead` / `ReleaseForRetry` 接口本身定义为同步（void 返回）。Dapper 实现也是同步。PalORM 全异步——必须 sync-over-async。这是接口设计层面的约束，非实现 bug。

**如何避免**：区分"接口契约约束"和"实现选择"。接口约束导致的 sync-over-async 不是实现缺陷。建议改为 P1（推动接口异步化）而非 P0（实现 bug）。

---

### 模式 PD12：框架库 API 的权限边界（非 Web API）

**反例**：评审标记 `ProjectionCheckpointStore.ResetAsync` 的 `projectionName` 参数可被注入清空数据为 P0。实际 `projectionName` 来自代码常量（Projection 类名），不是用户输入。框架库不是 Web API——权限边界由应用层负责。

**如何避免**：确认代码所在分层。框架库（src/PalDDD.*）不直接接收用户输入。权限边界问题在框架库层降级为 P3（文档化）。

---

### 模式 PD13：GetRawConnection 只读路径的事务隔离（设计限制非 bug）

**反例**：评审标记复合主键 Store 的 `GetRawConnection().CreateCommand()` 绕过事务传播为 P0。实际 PalORM 的 `GetRawConnection()` 文档明确标注"逃生舱，原生操作不受会话并发门禁保护"。复合主键表因 PALORM019 无法注册实体，必须用 GetRawConnection。只读路径在大多数场景正确（读已提交）。

**如何避免**：区分"设计限制"和"实现 bug"。GetRawConnection 是 PalORM 的设计约束（不提供 CreateCommand 公开 API），适配层无法独立解决。降级为 P1（文档化 + 待上游提供 API）。

---

### 模式 PD14：与既有实现对齐的行为不是新 bug

**反例**：评审标记 EventLog 的 `SELECT MAX + 循环 INSERT` TOCTOU 竞态为 P0。实际 Dapper 实现完全相同的模式——这是 Event Sourcing 的已知设计（依赖 DDL UNIQUE 约束兜底）。新实现与旧实现对齐不是新引入的 bug。

**如何避免**：发现并发/竞态问题时，先检查既有实现（Dapper/EFCore）是否有同样模式。如果是对齐行为，降级为 P1（改进项）而非 P0（新 bug）。

---

## 维护规则

1. **模式只增不删**：发现新误判模式追加为 PD{N+1}；模式被推翻时标注勘误而非删除。
2. **每条附来源**：commit hash / ITM 编号 / ADR / .codebuddy/memory / conventions 章节。
3. **机械核验优先**：能用 grep/正则/架构测试机械验证的，下沉为 PDDD-G{N} 或 boundary test，不再逐个推理。
4. **与 ORM 项目迁移说明**：ORM 的 P1-P9 已删除（不适用 DDD）；DDD 重新设计 PD1-PD9 基于自身实践。
