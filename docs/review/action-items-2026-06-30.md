# Pal.DDD 评审改进任务清单 v3（2026-06-30）

> 来源：`docs/review/serena-comprehensive-review-2026-06-29-v4.md` Serena LSP 语义分析 + 源码扫描 + 架构测试分析 + Serena 诊断扫描（PostgreSql 适配器/Analyzer/Benchmark）  
> 前序：`docs/review/action-items-2026-06-29-v2.md`（ITM-025~030 已全部落地）  
> 用法：每项含 ID / 优先级 / 维度 / 问题 / 建议 / 风险 / 验证 / 涉及文件。完成后将 `[ ]` 改为 `[x]` 并填完成日期与 commit。  
> 优先级：**P1** 推荐近期修复 · **P2** 中期增强 · **P3** 长期演化/评估  
> 立场：每项均标注风险与取舍，避免投机抽象；"评估类"任务不要求必做，只要求给出结论 ADR。

---

## 评审基线快照（2026-06-30）

| 指标 | 实测值 | 证据 |
|------|--------|------|
| 源项目数 | 31 | `find src -name "*.csproj" \| wc -l` |
| 源文件数 | 168 | `find src -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" \| wc -l` |
| 测试项目数 | 15 | `find test -name "*.csproj" \| wc -l` |
| 测试文件数 | 64 | `find test -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" \| wc -l` |
| 架构边界测试用例 | 83 | `ArchitectureBoundaryTests.cs`（570 行） |
| 编译期诊断规则 | 15 条（PDDD001~015） | `StrategicDddAnalyzer.cs:18-32` |
| `IsAotCompatible=false` 项目 | 12 个业务项目 + 3 个工具项目（SourceGen/Analyzer/CodeFixes） | 各 `.csproj` |
| `catch(Exception)` 处 | 27（全部合规，详见下方 P2-4） | grep 核查 |
| `.editorconfig` 已配置 IDE 规则 | 9 条（IDE0002/0005/0008/0011/0022/0058/0060/0160/0161） | `.editorconfig` |
| `StrategicDddAnalyzer.cs` 行数 | 597 | Serena 确认 |
| Benchmark 基准类 | 10 个（已就绪，待 .NET 11 GA 运行） | `bench/PalDDD.Benchmarks/` |

---

## P1 — 推荐近期修复

### [x] ITM-031 · NoWarn 黑名单收窄：`CA1031` 改为精确 `[SuppressMessage]` 抑制 · 2026-06-30
- **维度**：健壮性 / 可维护性
- **问题**：`Directory.Build.props:38` 全局 `NoWarn` 含 `CA1031`（catch 全部异常）。禁用理由是后台处理器（Outbox/Inbox/Saga）需隔离任意异常以保护批处理循环——但这只适用于后台路径。全局禁用会**静默放行非后台代码的 catch-all**，削弱异常处理审查。
- **建议**：
  1. 从 `Directory.Build.props:38` 的 `NoWarn` 移除 `CA1031`
  2. 在 27 处 `catch(Exception)` 上方添加 `[SuppressMessage("Design", "CA1031:Do not catch general exception", Justification = "...")]`，按场景写明理由：
     - 后台处理器（Outbox/Inbox/Saga/Projection/PeriodicBackgroundProcessor）：`"后台批处理循环必须隔离任意异常以防止循环中断，已正确过滤 OperationCanceledException"`
     - `ExceptionMiddleware`：`"HTTP 中间件顶层异常屏障，映射为 500 响应"`
     - `IUnitOfWork` 回滚路径：`"事务回滚必须吞掉原始异常以保留 rollback 错误"`
     - `SagaCompensation`：`"补偿循环必须收集所有步骤失败，聚合抛出"`
     - `PalPlatformVerifier`：见 P2-4
  3. 保留 `CA1031` 在 `NoWarn` 中的唯一例外：无（全部改为精确抑制）
- **风险**：中（涉及 27 处源码修改 + 可能触发新的编译警告需逐一处理）
- **验证**：
  - `grep "CA1031" Directory.Build.props` 不在 `NoWarn` 行
  - `dotnet build PalDDD.slnx` 零错误零警告
  - `dotnet test PalDDD.slnx --no-build` 零失败
- **涉及文件**：
  - `Directory.Build.props`（移除 NoWarn 中的 CA1031）
  - `src/PalDDD.Core/IUnitOfWork.cs`（2 处）
  - `src/PalDDD.CQRS/PipelineBehaviors.cs`（1 处）
  - `src/PalDDD.Hosting.AspNetCore/AspNetCore/ExceptionMiddleware.cs`（1 处）
  - `src/PalDDD.Hosting.AspNetCore/AspNetCore/HealthCheckExtensions.cs`（1 处）
  - `src/PalDDD.Messaging.Kafka/KafkaBroker.cs`（2 处）
  - `src/PalDDD.Messaging.RabbitMQ/RabbitMqBroker.cs`（1 处）
  - `src/PalDDD.Serialization.Evolution/PalPlatformVerifier.cs`（1 处，见 P2-4）
  - `src/PalDDD.Transactions/PeriodicBackgroundProcessor.cs`（1 处）
  - `src/PalDDD.Transactions/Saga.cs`（2 处）
  - `src/PalDDD.Transactions/SagaCompensation.cs`（1 处）
  - `src/PalDDD.Transactions/SagaProcessor.cs`（1 处）
  - `src/PalDDD.Dapper.PostgreSql/PostgreSqlOutboxNotifier.cs`（2 处）

---

### [x] ITM-032 · README 计数漂移修正 + `verify-doc-numbers.sh` 纳入强制校验 · 2026-06-30
- **维度**：可维护性 / 一致性（三方一致）
- **问题**：README 多处计数声明与实测值漂移，违反"三方一致"原则：
  - `README.md:83`："14 个测试项目" → 实测 **15**
  - `README.md:225`："30 源项目 / 14 测试项目 + 1 测试基础设施项目，160 源文件 / 60 测试文件" → 实测 **31 / 15 / 168 / 64**
  - `README.md:228`："30 项目 Clean Architecture 分层" → 实测 **31**
  - `README.md:83,215`："705 个测试" / "705 通过 0 失败 6 跳过" → 需运行 `dotnet test` 确认最新值（Serena 记忆载 716 通过，可能近期新增测试）
- **建议**：
  1. 运行 `dotnet test PalDDD.slnx --no-build` 获取最新测试总数（通过/失败/跳过）
  2. 修正 `README.md:83,225,228` 的项目数/文件数/测试数
  3. 检查 `scripts/verify-doc-numbers.sh` 是否已覆盖这些计数点；若未覆盖，扩展脚本断言
  4. 将 `verify-doc-numbers.sh` 纳入 `.githooks/pre-push`（当前 pre-push 含 build+test，需追加 doc 校验）
- **风险**：低（文档级 + 脚本增强）
- **验证**：
  - `bash scripts/verify-doc-numbers.sh` 退出码 0
  - `grep -E "30 源项目|14 个测试项目|160 源文件|60 测试文件" README.md` 零命中
  - pre-push hook 执行含 doc 校验
- **涉及文件**：
  - `README.md`（第 83、225、228、215 行）
  - `scripts/verify-doc-numbers.sh`（可能需扩展断言）
  - `.githooks/pre-push`（追加 doc 校验调用）

---

## P2 — 中期增强

### [x] ITM-033 · `ArchitectureBoundaryTests` AOT 断言扩展为全量 Theory · 2026-06-30
- **维度**：可测试性 / 健壮性
- **问题**：`InfrastructureAdapters_AreExplicitlyNonAot`（`ArchitectureBoundaryTests.cs:218`）的 Theory 仅断言 **5 个项目**（Hosting.AspNetCore / Messaging.Kafka / Messaging.RabbitMQ / Repository.EFCore / Transactions.EFCore）。但实际有 **12 个业务项目**显式 `IsAotCompatible=false`（含 EventLog.EFCore / Idempotency.EFCore / Projections.EFCore / Serialization.MemoryPack 等），新增非 AOT 项目不会被自动检测。
- **建议**：
  - 方案 A（推荐）：将 Theory 的 InlineData 扩展为全量 12 个业务项目列表（不含 SourceGen/Analyzers/CodeFixes，这三类设 false 是 Roslyn 工具链特性，非业务 AOT 豁免）
  - 方案 B：改为动态扫描——遍历 `src/` 下所有 `.csproj`，断言"凡含 `<IsAotCompatible>false</IsAotCompatible>` 的项目，必须同时含 `<IsTrimmable>false</IsTrimmable>` 和 `<VerifyReferenceAotCompatibility>false</VerifyReferenceAotCompatibility>`"，并维护一份"允许 false 的项目白名单"防漂移
  - 方案 B 更彻底但需维护白名单，方案 A 简单直接
- **风险**：低（纯测试增强，新增断言）
- **验证**：
  - 扩展后的 Theory 数据覆盖全部 12 个业务 false 项目
  - `dotnet test --filter "InfrastructureAdapters_AreExplicitlyNonAot"` 通过
  - 临时在某个未列入的项目删除 `IsAotCompatible=false`，测试应失败（验证覆盖度）
- **涉及文件**：
  - `test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`（第 212-225 行 Theory）

---

### [x] ITM-034 · `PalPlatformVerifier` 同步路径异常过滤加注释说明 · 2026-06-30
- **维度**：可读性 / 健壮性
- **问题**：`PalPlatformVerifier.cs:35` 的 `catch(Exception exception) when(exception is not OutOfMemoryException)` 是全项目**唯一**不按 `OperationCanceledException` 过滤的 catch。经核查，`ValidateMessageEvolutionPaths` 是**同步方法**，内部调 `pipeline.ValidatePath` 同步验证，不涉及 `CancellationToken`，故不会产生 `OperationCanceledException`——设计**合理**，但缺注释说明，易被误判为遗漏。
- **建议**：在 `catch` 上方添加行内注释，说明：
  - 此为同步验证路径，不涉及取消语义
  - `when(exception is not OutOfMemoryException)` 的意图是"聚合所有验证错误，OOM 直接向上传播"
  - 与异步路径的 `when(ex is not OperationCanceledException)` 模式不同的原因
- **风险**：极低（纯注释）
- **验证**：注释添加后 `dotnet build` 零警告
- **涉及文件**：
  - `src/PalDDD.Serialization.Evolution/PalPlatformVerifier.cs`（第 31-39 行）

---

### [x] ITM-037 · `.editorconfig` 补充 4 条 IDE 规则配置，消除诊断噪音 · 2026-06-30
- **维度**：可读性 / 可维护性
- **问题**：`.editorconfig` 已配置 9 条 IDE 规则为 `none`（IDE0002/0005/0008/0011/0022/0058/0060/0160/0161），但 Serena 诊断扫描发现仍有 4 条未配置的 IDE 规则产生噪音：`IDE0024`（运算符块主体）、`IDE0290`（主构造函数）、`IDE0032`（自动属性）、`IDE0370`（多余抑制）。框架库策略已声明"不强制编辑器样式偏好"，但这 4 条未纳入配置导致 IDE 持续报告视觉噪音。
- **建议**：在 `.editorconfig` 的 `[*.{cs,csx}]` 段追加：
  ```ini
  dotnet_diagnostic.IDE0024.severity = none
  dotnet_diagnostic.IDE0290.severity = none
  dotnet_diagnostic.IDE0032.severity = none
  dotnet_diagnostic.IDE0370.severity = none
  ```
- **风险**：极低（编辑器配置级，不影响编译行为；`TreatWarningsAsErrors=true` 下 IDE 规则非编译器警告，不阻塞编译）
- **验证**：Serena `get_diagnostics_for_file` 对 `Entity.cs` / `Dispatcher.cs` / `Saga.cs` / `KafkaBroker.cs` 扫描，确认 IDE0024/IDE0290/IDE0032/IDE0370 诊断消失
- **涉及文件**：
  - `.editorconfig`（`[*.{cs,csx}]` 段追加 4 行）

---

### [x] ITM-038 · `PostgreSqlReadWriteRouter` 实现 `IAsyncDisposable`，修复连接池泄漏 · 2026-06-30
- **维度**：健壮性 / 可维护性（资源安全）
- **问题**：`PostgreSqlReadWriteRouter` 持有 `NpgsqlDataSource Writer` 和 `NpgsqlDataSource? Reader` 两个连接池对象，但未实现 `IAsyncDisposable`。DI 注册为 Singleton（`AddSingleton(router)`），DI 容器**仅对实现 `IDisposable`/`IAsyncDisposable` 的 Singleton 执行 Dispose**——`NpgsqlDataSource` 持有连接池，未释放会导致连接泄漏。同项目 `ShardedDataSourceManager` 已正确实现 `IAsyncDisposable` 作为参照模式。
- **建议**：
  1. `PostgreSqlReadWriteRouter` 实现 `IAsyncDisposable`
  2. `DisposeAsync` 中释放 `Writer` 和 `Reader`：
     ```csharp
     public async ValueTask DisposeAsync()
     {
         await Writer.DisposeAsync();
         if (Reader is not null)
             await Reader.DisposeAsync();
     }
     ```
  3. 确认 DI 注册为 Singleton 时容器自动调用 `DisposeAsync`（`AddSingleton` 已满足）
  4. 新增单元测试验证 Dispose 后 `DataSource` 不可用
- **风险**：低（行为正确性增强；需确认现有测试是否依赖未释放状态）
- **验证**：
  - Serena `search_for_pattern` 确认 `IAsyncDisposable` 出现在 `PostgreSqlReadWriteRouter.cs`
  - 新增单元测试：Dispose 后调用方法应抛 `ObjectDisposedException`
  - `dotnet test PalDDD.slnx` 零失败
- **涉及文件**：
  - `src/PalDDD.Dapper.PostgreSql/PostgreSqlReadWriteRouter.cs`（类声明 + 新增 DisposeAsync）
  - `test/PalDDD.Integration.Tests/`（新增 Dispose 验证测试）

---

### [x] ITM-039 · `PostgreSqlSoftDelete` 的 `whereClause` 参数补 SQL 注入安全文档 · 2026-06-30
- **维度**：可读性 / 健壮性（安全责任标注）
- **问题**：`PostgreSqlSoftDelete` 的 `SoftDelete` / `Restore` / `PurgeOld` 方法接受 `string whereClause` 参数，直接嵌入生成的 SQL（`$"UPDATE {Escape(table)} SET ... WHERE {whereClause}"`）。这是 API 设计意图（调用方构造 WHERE 子句），但 XML doc 未标注安全责任。如果调用方将用户输入拼入 `whereClause`，存在 SQL 注入风险。
- **建议**：在 `SoftDelete` / `Restore` / `PurgeOld` 方法的 XML doc `<summary>` 中追加 `<remarks>`：
  ```xml
  <remarks>⚠️ <c>whereClause</c> 直接嵌入 SQL——调用方必须确保不含用户输入或已充分转义标识符。</remarks>
  ```
- **风险**：极低（纯文档标注，无行为变更）
- **验证**：Serena `find_symbol` 提取三个方法体，确认 XML doc 含安全警告
- **涉及文件**：
  - `src/PalDDD.Dapper.PostgreSql/PostgreSqlSoftDelete.cs`（3 个方法的 XML doc）

---

## P3 — 长期演化/评估

### [x] ITM-035 · 项目粒度评估：方言增强项目合并可行性 · ADR-012 结论：维持现状 · 2026-06-30
- **维度**：简洁性 / 可维护性
- **问题**：31 个源项目中，部分方言增强项目仅 1-2 个文件：
  - `PalDDD.Dapper.PostgreSql` / `MySql` / `Sqlite`（各 ~1-2 文件，主要是 `*OutboxNotifier` 或方言 SQL）
  - 评估这些项目是否可合并入 `PalDDD.Dapper` 或按方言分组
- **建议**：**评估类任务，不要求必做**。产出 ADR 结论即可：
  - 方案 A（维持现状）：slnx 分层清晰，分离便于按需引用，合并收益有限
  - 方案 B（合并）：降低解决方案项目数，但需调整 csproj 引用和 slnx 分层
  - 决策依据：消费方实际引用模式（是否真的按方言单独引用）、构建时间影响
- **风险**：高（若选择合并，涉及 csproj/引用/slnx 多处调整）
- **验证**：产出 `docs/decisions/012-*.md` ADR，记录评估结论
- **涉及文件**（若选择评估）：
  - `src/PalDDD.Dapper.PostgreSql/`
  - `src/PalDDD.Dapper.MySql/`
  - `src/PalDDD.Dapper.Sqlite/`
  - `PalDDD.slnx`

---

### [x] ITM-036 · .NET 11 Preview→RTM 迁移计划 ADR · ADR-013 已产出 · 2026-06-30
- **维度**：兼容性 / 可维护性
- **问题**：`global.json` 锁定 `.NET 11 Preview 5` + `rollForward:latestMajor` + `allowPrerelease:true`。框架库依赖 Preview API（如 `JsonSerializerContext` 源生成增强、新 AOT 分析器、`runtime-async` 特性）。Preview API 可能在 RTM 前发生破坏性变更，需有迁移预案。
- **建议**：**评估类任务**。产出 ADR 记录：
  1. 当前依赖的 .NET 11 Preview 特性清单
  2. Preview→RTM 的预期时间线（参考 .NET 官方发布节奏）
  3. 迁移策略：RTM 发布后是否立即升级、是否保留 Preview 兼容窗口
  4. 风险缓解：CI 是否应在多个 .NET 11 Preview 版本上验证
- **风险**：低（纯文档，但影响未来兼容性决策）
- **验证**：产出 `docs/decisions/013-*.md` ADR
- **涉及文件**（若产出 ADR）：
  - `docs/decisions/`（新增 ADR）
  - `global.json`（参考，不改）

---

### [x] ITM-040 · `StrategicDddAnalyzer` 文件拆分评估（597 行 → 拆分辅助方法）· ADR-014 结论：维持现状 · 2026-06-30
- **维度**：可维护性 / 可读性
- **问题**：`src/PalDDD.Analyzers/StrategicDddAnalyzer.cs` 单文件 597 行，包含 15 条诊断规则 + 多个辅助方法（`TryGetProjectionName` / `TryGetStaticStringProperty` / `InheritsFrom` / `ImplementsInterface` 等）。语法树遍历方法覆盖 4 种属性写法，逻辑复杂。单文件过大影响代码导航和合并冲突率。
- **建议**：**评估类任务，不要求必做**。产出拆分方案 ADR：
  - 方案 A（推荐，若拆分）：将 `TryGetProjectionName` / `TryGetStaticStringProperty` 提取为 `SyntaxHelper` 静态类，将 `InheritsFrom` / `ImplementsInterface` / `MetadataNameEquals` 提取为 `SymbolHelper` 静态类。保持 `StrategicDddAnalyzer` 专注于规则定义
  - 方案 B（维持现状）：分析器代码有内聚性，拆分需谨慎避免破坏诊断规则注册；597 行在分析器领域可接受
  - 决策依据：规则数是否还会增长、团队合并冲突频率
- **风险**：中（若选择拆分，需确保 15 条诊断规则注册不受影响）
- **验证**：`dotnet test PalDDD.slnx --filter "Analyzers"` 全部通过；拆分后 `StrategicDddAnalyzer.cs` < 400 行
- **涉及文件**（若选择拆分）：
  - `src/PalDDD.Analyzers/StrategicDddAnalyzer.cs`（提取辅助方法）
  - `src/PalDDD.Analyzers/SyntaxHelper.cs`（新增）
  - `src/PalDDD.Analyzers/SymbolHelper.cs`（新增）

---

### [⏸] ITM-041 · BenchmarkDotNet .NET 11 GA 后运行全部基准，更新 README 实测数据 · 阻塞：待 .NET 11 GA（见 ADR-013）
- **维度**：可维护性 / 性能契约
- **问题**：当前 BenchmarkDotNet 0.15.8 不支持 .NET 11 Preview，`bench/PalDDD.Benchmarks/` 下 10 个基准类已就绪但无法运行。README 性能数据标注为"设计目标"而非"实测数据"。
- **建议**：与 ITM-036（.NET 11 Preview→RTM ADR）联动，GA 后执行：
  1. .NET 11 GA 后升级 BenchmarkDotNet 至支持版本
  2. `dotnet run -c Release --project bench/PalDDD.Benchmarks`
  3. 更新 README 性能表格，从"设计目标"标注为"实测数据"
  4. 将 `AllocationContractTests` 结果与 Benchmark 交叉验证
- **风险**：低（GA 后执行，无时间压力）
- **验证**：`BenchmarkDotNet.Artifacts/results/` 目录生成 10 个基准报告；README 性能表格标注"实测"
- **涉及文件**：
  - `bench/PalDDD.Benchmarks/`（升级 BenchmarkDotNet 版本）
  - `README.md`（性能表格更新）
  - `Directory.Packages.props`（BenchmarkDotNet 版本）

---

## 任务依赖与执行顺序

```
ITM-031 (NoWarn 收窄) ──┐
                        ├──> 可并行
ITM-032 (README 计数) ──┘

ITM-033 (AOT 断言扩展) ──> 独立
ITM-034 (PalPlatformVerifier 注释) ──> 独立，可与 ITM-031 同 commit
ITM-037 (editorconfig IDE) ──> 独立，可与 ITM-034 同 commit（纯配置/注释批次）
ITM-038 (PostgreSqlReadWriteRouter IAsyncDisposable) ──> 独立，资源安全修复
ITM-039 (PostgreSqlSoftDelete 安全文档) ──> 独立，可与 ITM-034/037 同 commit（文档批次）

ITM-035 (项目粒度评估) ──> 评估类，独立
ITM-036 (Preview→RTM ADR) ──┐
                             ├──> 联动：GA 时间线
ITM-041 (Benchmark GA 后运行) ──┘
ITM-040 (Analyzer 拆分评估) ──> 评估类，独立
```

**建议执行批次**：
1. **批次 1**（P1，2-3 个 commit）：ITM-031 + ITM-034（NoWarn 收窄时顺带给 PalPlatformVerifier 加注释）+ ITM-032
2. **批次 2**（P2 文档/配置批次，1 个 commit）：ITM-037 + ITM-039（editorconfig IDE 规则 + SoftDelete 安全文档，均为纯配置/注释）
3. **批次 3**（P2 代码修复，1 个 commit）：ITM-033（AOT 断言扩展）+ ITM-038（IAsyncDisposable 资源修复）
4. **批次 4**（P3 评估类，按需）：ITM-035 + ITM-036 + ITM-040 产出 ADR；ITM-041 待 .NET 11 GA

---

## 完成标准

- [x] 所有 P1 项（ITM-031、032）`[x]` 并附日期
- [x] 所有 P2 项（ITM-033、034、037、038、039）`[x]` 并附日期
- [x] P3 评估类项（ITM-035、036、040）产出 ADR 结论（ADR-012/013/014，均结论：维持现状）
- [⏸] P3 时间线项（ITM-041）待 .NET 11 GA 后执行（见 ADR-013）
- [x] 最终 `dotnet build PalDDD.slnx` 零错误零警告
- [x] 最终 `dotnet test PalDDD.slnx` 零回归（2 个既有失败已确认非本次引入）
- [ ] 最终 `bash scripts/verify-conventions.sh` 通过（待最终验证）
