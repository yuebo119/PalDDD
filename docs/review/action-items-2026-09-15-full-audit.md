# Pal.DDD 可实施任务清单 — 全量审计（2026-09-15）

> 来源报告：[`docs/review/audit-2026-09-15-full.md`](audit-2026-09-15-full.md)
> 基线 commit：`c908f40`（dev 分支，v2.2.0），工作树干净
> 编号衔接：主仓既有最高为 ITM-684（`e2f4ffd` 提交；`docs/review/action-items-*` 最高为 ITM-683）。本清单自 **ITM-685** 起。
> ⚠️ 编号风险提示：`docs/review/query-observability-validation-2026-09-13.md:123` 存在前向引用 `ITM-773`（疑为 PalORM 上游台账号）。若 685 号段已被私有台账占用，本清单编号需整体后移后再落库。

---

## 口径说明（两套分级的关系）

来源报告用**影响**分级（严重/高/中/低）；本清单用本仓 `docs/review/ACTION_ITEMS_TEMPLATE.md` 既有的**危害 × 复杂度**矩阵（P0-P3），以保持跨轮清单可比。

映射规则（本次采用）：

| 报告影响级 | 映射到的危害级 | 依据（模板的危害定义） |
|---|---|---|
| 严重 / 高 → 安全面 | 高 | 安全漏洞 |
| 严重 / 高 → 投递/数据面 | 高 | 数据损失 / 运行时故障 |
| 高 → 仅文档/一致性问题 | 中 | 不一致 / 文档命令不可执行 |
| 中 | 中 | 不一致 / 潜在并发风险 / 架构退化 |
| 低 | 低 | 注释缺失 / 装饰性不一致 / 纯计数漂移 |

**这一映射会让部分"报告里看起来高"的项落到 P1/P2 而非 P0。** 例如 ITM-689（Dapper AOT 三方矛盾）在报告中标"严重"（因其违反本仓 #1 红线），但按模板危害定义属"不一致"→ 中危害 + 易修复 → **P1**。这是刻意的：模板的 P0 保留给"高危害 + 易修复"，避免优先级通胀。

---

## 总体进度

> 状态更新时间：2026-09-15（清单创建，0/41 闭环）

| 里程碑 | 条目数 | 待处理 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|
| M0 安全网 | 4 | 4 | 0 | 0% |
| M1 关键修复 | 8 | 8 | 0 | 0% |
| M2 高杠杆改进 | 8 | 8 | 0 | 0% |
| M3 质量与润色 | 14 | 14 | 0 | 0% |
| 评估（需 ADR 结论） | 7 | 7 | 0 | 0% |
| **合计** | **41** | **41** | **0** | **0%** |

**优先级分布**（按模板矩阵）：P1 = 13 · P2 = 15 · P3 = 6 · 评估 = 7

---

## 里程碑总览

### M0 安全网（动任何重构之前）

> 存在的理由：ITM-697 / 698 / 710 都改已发布业务逻辑，而 ITM-685 揭示的类（处理器内部失败分支）当前完全无测试。安全网先于重构。

| ID | 标题 | 优先级 | 危害/复杂度 | 工作量 | 依赖 |
|---|---|:--:|:--:|:--:|---|
| ITM-685 | `OutboxBatchProcessor` 毒消息死信路径测试 | P1 | 高 / 中 | M | — |
| ITM-686 | 三栈 store DI 注册集一致性测试 | P1 | 中 / 易 | S | — |
| ITM-687 | 覆盖率降幅基线补 `PalDDD.PalORM.Tests` | P1 | 中 / 易 | S | CI 可跑 |
| ITM-688 | 持久化基准纳入 CI 归档 | P2 | 中 / 中 | M | — |

### M1 关键修复（安全与正确性）

| ID | 标题 | 优先级 | 危害/复杂度 | 工作量 | 依赖 |
|---|---|:--:|:--:|:--:|---|
| ITM-689 | Dapper AOT 事实三方统一 | P1 | 中 / 易 | S | — |
| ITM-690 | csproj 包描述纳入事实一致性门禁 | P2 | 中 / 中 | M | ITM-689 |
| ITM-691 | README 快速开始样例不可编译 | P1 | 中 / 易 | S | — |
| ITM-692 | Dapper DI 缺口补齐或显式化 | P2 | 中 / 中 | M | ITM-686 |
| ITM-693 | outbox 索引补齐 + EF 模型/DDL 对齐 | P1 | 中 / 易 | M | — |
| ITM-694 | `secret-scan` 检测面扩充 | P1 | 高 / 中 | M | — |
| ITM-695 | 认证与授权文档 + 策略入口 | P1 | 高 / 中 | M | — |
| ITM-696 | `MapQuery` 调用方绑定异常归 400 | P1 | 中 / 易 | S | — |

### M2 高杠杆改进（让后续工作更省力）

| ID | 标题 | 优先级 | 危害/复杂度 | 工作量 | 依赖 |
|---|---|:--:|:--:|:--:|---|
| ITM-697 | 唯一约束分类器下沉（10 份 → 1 份） | P3 | 中 / 难 | L | ITM-686 |
| ITM-698 | EF Core outbox 查询与租约批量化 | P2 | 高 / 难 | L | ITM-685, 688 |
| ITM-699 | 事实单点化与计数守卫 | P3 | 低 / 中 | M | — |
| ITM-700 | 入门路径打通（CONTRIBUTING + SDK 前置） | P1 | 中 / 易 | S | — |
| ITM-701 | 文档 `.sh` 残留 / `.ai` 引用 / CI 状态表修正 | P1 | 中 / 易 | S | — |
| ITM-702 | 锁文件启用 | P2 | 中 / 中 | M | — |
| ITM-703 | `PalDDD.Transactions` 的 `M.E.*` 欠声明 | P1 | 中 / 易 | S | — |
| ITM-704 | `PalDDD.Base` 分析器交付缺口 | P2 | 中 / 中 | M | — |

### M3 质量与润色

| ID | 标题 | 优先级 | 危害/复杂度 | 工作量 | 依赖 |
|---|---|:--:|:--:|:--:|---|
| ITM-705 | 无断言测试清理（门禁加固前置） | P2 | 低 / 易 | S | — |
| ITM-706 | `AssertionStrengthGateTests` 加固 | P3 | 低 / 中 | M | ITM-705 |
| ITM-707 | 挂钟延迟测试改 `FakeTimeProvider` | P3 | 中 / 中 | M | — |
| ITM-708 | Kafka 错误路径测试补齐 | P2 | 中 / 中 | M | Docker |
| ITM-709 | 复杂度热点削减 | P3 | 低 / 中 | M | ITM-685 |
| ITM-710 | `Saga` 四条重试车道模板化 | P3 | 低 / 难 | XL | ITM-685, 709 |
| ITM-711 | 依赖与包卫生（SqlClient / 许可证 / nupkgs / 描述 / Platforms） | P2 | 中 / 易 | M | — |
| ITM-712 | 注释变更史考古清理 | P3 | 低 / 难 | L | ITM-710 |
| ITM-713 | `scripts/README.md` 索引 | P2 | 低 / 易 | S | — |
| ITM-714 | 文档计数与注释状态收尾 | P2 | 低 / 易 | S | — |
| ITM-715 | EventLog 批量插入 + Projection checkpoint 批量化 | P3 | 中 / 难 | L | ITM-698 |
| ITM-716 | 手动转义与原始 SQL 片段加固 | P2 | 低 / 易 | S | — |
| ITM-717 | 发布与本地凭据卫生 | P1 | 中 / 易 | S | — |
| ITM-718 | 公共 API 语义澄清（`SqlTemplates`） | P2 | 低 / 易 | S | — |

### 快速获胜（高影响 + S 工作量，可立即执行）

| 顺序 | ID | 标题 | 为什么值得先做 |
|:--:|---|---|---|
| 1 | ITM-689 | Dapper AOT 事实三方统一 | 消除本仓最严重的自相矛盾，改 4 处文本，当天可提交 |
| 2 | ITM-703 | `PalDDD.Transactions` 的 `M.E.*` 欠声明 | 一行 csproj，修掉一个真实的消费者解析失败 |
| 3 | ITM-701 | 文档 `.sh` 残留 / `.ai` 引用 / CI 状态表修正 | 纯文本，消除已删除脚本的失效引用 |
| 4 | ITM-700 | 入门路径打通 | 贡献者体验最低成本的改善 |
| 5 | ITM-696 | `MapQuery` 调用方绑定异常归 400 | 小改动，把错误分类错误收敛 |
| 6 | ITM-686 | 三栈 DI 注册集一致性测试 | 先红后绿的测试，把"注释里的自认"变成 CI 覆盖 |
| 7 | ITM-714 | 文档计数与注释状态收尾 | 机械可验证的收尾，防后续误引 |

---

## M0 详细条目

### [ ] ITM-685 · `OutboxBatchProcessor` 毒消息死信路径测试 · 可信度 ✅
- **维度**：测试覆盖（投递语义安全网）
- **优先级**：P1 · 危害: 高 · 复杂度: 中
- **问题**：`src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs` 四条关键失败分支无任何测试引用：① `:100-108` 消息类型未在 `MessageCatalog` 注册 → `MarkDead`；② `:110-116` 反序列化返回 null → `MarkDead`；③ `:160-168` / `:174-182` `MarkDead`/`ReleaseForRetry` 自身抛异常 → 挂 `Data["MarkError"]` 并继续批次；④ `:142-150` backoff 策略抛异常 → 1s 兜底。该路径回归后毒消息永久重投，而套件保持全绿。
- **证据**：
  - `grep -rn "Deserialization returned null" .` 全仓仅命中 `src/` 定义处（`:112`），`test/` 零命中。
  - `grep -rn "not registered in MessageCatalog" test/` 命中 2 处，但均在 `test/PalDDD.Core.Tests/AotContractTests.cs:397` 与 `test/PalDDD.Serialization.Tests/SerializationTests.cs:208`，断言的是 catalog 查询错误消息，与处理器死信路径无关（已读上下文排除假阳性）。
  - store 层 `MarkDead` 有测试（`test/PalDDD.Integration.Tests/DapperStoreTests.cs:423,461`、`test/PalDDD.PalORM.Tests/PalOrmOutboxStoreTests.cs:59`、`test/PalDDD.Transactions.Tests/InMemoryStoreTests.cs:91`）；处理器层无。
  - 现有处理器测试只覆盖"重试未达上限"（`test/PalDDD.Transactions.Tests/TransactionsTests.cs` 的 `ProcessBatchAsync_RecordsOutboxFailedMetricWhenMessageRetries` 附近）。
- **建议**：在 `test/PalDDD.Transactions.Tests/OutboxProcessorTests.cs` 补四条测试，用 `InMemoryOutboxStore` + 可控 `IMessageSerializer` 桩构造分支，不依赖真实数据库。分支 ③ 是关键项：它验证"标记失败不得中止整批"的设计承诺，必须断言批次继续处理后续消息。
- **风险**：低（纯新增测试，生产代码零改动）。注意 `TransactionsTests.cs` 中依赖 Meter 广播的测试标了 `[NotInParallel]`，新增测试若断言指标需照做。
- **验证**：
  - 新测试在四条分支上产生行覆盖（可用 `dotnet run scripts/ci-coverage.cs` 看项目级 line-rate 变化）。
  - 变异验证：临时把 `:101` 的 `if (descriptor is null)` 改为 `if (false)` → 跑测试确认**失败** → 恢复。对 `:111` 的 `@event is null` 做同样操作。这是本仓门禁规程第 3 步的强制要求。
  - ✅ 已交叉验证：`OutboxBatchProcessor.cs:100,110,142,154` 路径与 `MarkDead` 签名已 grep 存在。
- **涉及文件**：`test/PalDDD.Transactions.Tests/OutboxProcessorTests.cs`（新增测试）；被测 `src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs`
- **状态**：⬜ 待处理

### [ ] ITM-686 · 三栈 store DI 注册集一致性测试 · 可信度 ✅
- **维度**：架构一致性（三栈等价性可观测化）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：Dapper 栈只注册 4 个（`src/PalDDD.Dapper/DapperServiceCollectionExtensions.cs:106` Outbox · `:107` Inbox · `:108` Idempotency · `:112` `ISagaStateStore<>`），而 `DapperEventLog`、`DapperProjectionCheckpointStore`、`DapperUnitOfWork` 三个类**在项目中存在却无任何 DI 注册路径**（全仓 grep 无对应 `AddScoped<IEventLog` / `IProjectionCheckpointStore` / `IUnitOfWork`）。PalORM 三个方言扩展均注册 6 个 store + UoW（`src/PalDDD.PalORM.Sqlite/SqlitePalOrmExtensions.cs:104-116`）。缺口本身已被代码注释承认（`src/PalDDD.Dapper/DapperAmbientTransaction.cs:10-13`「仅 Outbox/Inbox/SagaState 三者有 DI 注册；EventLog/ProjectionCheckpoint 需直连构造」），但无编译期或测试信号，消费者只能靠读注释发现。
- **证据**：`grep -n "AddScoped<" src/PalDDD.Dapper/DapperServiceCollectionExtensions.cs` → 4 条；同命令对 `src/PalDDD.PalORM.Sqlite/SqlitePalOrmExtensions.cs` → 6 条 + `DbConnection`；`ls src/PalDDD.Dapper/` 确认三个未注册类文件均存在。
- **建议**：在 `test/PalDDD.DependencyInjection.Tests/` 增加测试，用反射枚举三栈注册方法写入的 `ServiceDescriptor` 集合，断言集合差异等于显式登记的豁免清单（初始豁免 = Dapper 的三项）。差异集变化即测试变红，迫使后续改动显式决策。
- **风险**：低（纯新增测试）。写法需注意：三栈注册入口签名不同（Dapper 为 `AddPalDapperTransactions(type, connStr)` 族，PalORM 为 `AddPalOrm*` 族），需各自构造最小 `IServiceCollection`。
- **验证**：
  - 测试首跑**必须红**（暴露 Dapper 4 vs PalORM 7 的差异），据此建立豁免清单后转绿。
  - 变异验证：临时给 PalORM 扩展注释掉一行 `AddScoped<IEventLog, ...>`，确认测试变红。
  - ✅ 已交叉验证：`AddScoped` 行号与三个未注册类文件名已 grep / ls 验证存在。
- **涉及文件**：`test/PalDDD.DependencyInjection.Tests/`（新增测试）；参考 `src/PalDDD.Dapper/DapperServiceCollectionExtensions.cs:106-112`、`src/PalDDD.PalORM.Sqlite/SqlitePalOrmExtensions.cs:104-116`
- **状态**：⬜ 待处理

### [ ] ITM-687 · 覆盖率降幅基线补 `PalDDD.PalORM.Tests` · 可信度 ✅
- **维度**：门禁覆盖面
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：`coverage-baseline.json` 有 15 个键，**不含 `PalDDD.PalORM.Tests`**。而该项目正是本机无 Docker 时 46 项测试跑不完的项目（`docs/review/open-items-2026-09-14.md` D1 在案），即降幅门禁最该保护的对象反而无保护。前一轮 `ITM-683` 已在 `docs/test-coverage-baseline.md` 登记该空窗，但基线本身仍未补。
- **证据**：`cat coverage-baseline.json` → 15 键（Analyzers/CQRS/Compression/Core.Abstractions/Core/DependencyInjection/EventLog/Hosting.AspNetCore/Integration/Messaging.Integration/Messaging/Projections.EventLog/Repository.EFCore/Serialization/Transactions）；`grep -c "PalORM" coverage-baseline.json` → 0。
- **建议**：在 CI（有 Docker）跑一次完整覆盖率后，用 `dotnet run scripts/ci-coverage.cs --update-baseline` 生成含 PalORM 的 16 键基线。
- **风险**：低。注意 CI 首次产出值可能显著不同于本机，需确认新基线是"含 Docker 的完整值"而非降级值，避免基线偏低导致门禁失效。
- **验证**：`grep -c "PalDDD.PalORM.Tests" coverage-baseline.json` → 1；变异验证：手动把该项目基线调高一档，确认降幅门禁**变红**。
- **涉及文件**：`coverage-baseline.json`、`scripts/ci-coverage.cs`
- **状态**：⬜ 待处理

### [ ] ITM-688 · 持久化基准纳入 CI 归档 · 可信度 ✅
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **维度**：性能可观测性（ITM-698 的验证基础）
- **问题**：`docs/review/bench-baseline-2026-09-13.md` 记录 15 项真库存基准（含 `Outbox_Lease_Batch100` 的 EF/Dapper/PalORM 三方对比），但 `BenchmarkDotNet.Artifacts/results/` 的三个报告**未被 git 跟踪**（`git ls-files BenchmarkDotNet.Artifacts` 为空），CI 不跑基准，`README.md:802` 仍称 BDN 与 .NET 11 不兼容。后果：性能退化只能靠事后 Read 归档文本发现，没有可对比的自动基线。
- **证据**：`git ls-files BenchmarkDotNet.Artifacts` → 空；`grep -rn "BenchmarkDotNet\|--smoke" .github/workflows/` → 零命中；`bench/PalDDD.Benchmarks/Program.cs:14-30` 已有 `--persist` 分支可复用。
- **建议**：新增独立 CI job（与 `aot-verify` / `dialect-probe` 同构的失败域隔离），跑持久化基准子集并把结果作为 artifact 归档；首轮只记录不设阈值，待数据积累后再加回归门槛。
- **风险**：低（新增 job，不影响现有流水线）。注意基准在共享 runner 上噪声大，首轮不要设阈值，否则会制造假红。
- **验证**：CI 能产出 `Outbox_Lease_Batch100` 等关键指标并归档为 artifact；本地 `dotnet run --project bench/PalDDD.Benchmarks` 可复现同形输出。
- **涉及文件**：`.github/workflows/ci.yml`、`bench/PalDDD.Benchmarks/Program.cs`
- **状态**：⬜ 待处理

---

## M1 详细条目

### [ ] ITM-689 · Dapper AOT 事实三方统一 · 可信度 ✅
- **维度**：文档准确性 / 事实一致性（本仓 #1 红线）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：同一事实（Dapper 栈 AOT 是否启用）在六个位置有两种相反声称：

  | 事实源 | 声称 | 位置 |
  |---|---|---|
  | 代码 | **已启用** | `src/PalDDD.Dapper/DapperAotInitializer.cs:33` `[module: DapperAot]` |
  | 权威文档 | **已启用**，34 调用点，三方言 13/13 | `docs/persistence-aot-status.md:10-14` |
  | README 功能矩阵 | **已启用** | `README.md:770` |
  | README AOT 表 | **未启用，"AOT 假象"** | `README.md:790` |
  | NuGet 包描述 | **未启用，"AOT 假象"** | `src/PalDDD.Dapper/PalDDD.Dapper.csproj:7` |
  | 同文件头注释 | 启用是**未来动作** | `src/PalDDD.Dapper/DapperAotInitializer.cs:16-19` |

  最刺眼的是 `DapperAotInitializer.cs` 自身：头注释把"未启用"当现状，第 33 行已启用。而 `csproj:7` 是消费者在 nuget.org 读到的原文。选型者依据 README AOT 表会排除该栈，而该栈三方言 NativeAOT 二进制实测 13/13 通过。
- **证据**：`git merge-base --is-ancestor a1f3073 dev` → 真（实验提交 `a1f3073`「实验:Dapper.AOT 全量替换经典 Dapper——SQLite 全链路 AOT 实测通过(13/13)」是 dev 祖先）；`grep -rn "module: DapperAot" src/` → `DapperAotInitializer.cs:33` 一处（非注释）；`README.md:790` 与 `:770` 同文件内互斥（已读原文）；`docs/decisions/020-persistence-stack-retirement-roadmap.md:3` 记录 2026-09-13 裁决退役延后转"能力平等栈"。
- **建议**：以 `docs/persistence-aot-status.md` 为唯一权威，把其余四处改为引用它。**不要重新裁决事实**。
- **风险**：低（纯文本）。唯一注意：`csproj:7` 是 nuget.org 对外文案，改写时必须保留消费者必读边界「绕过封装直用 Dapper 原生 API 不受 AOT 支持」，否则是过度承诺。
- **验证**：
  - `grep -rn "AOT 假象" src/ README.md README.en.md` → 零命中。
  - `docs/aot.md` 若含同款旧口径须一并处理，改后全仓 `grep -rn "未启用" src/PalDDD.Dapper/ README.md README.en.md docs/aot.md docs/persistence-aot-status.md` 逐条确认无语境矛盾。
  - `DapperAotInitializer.cs` 头注释状态与 `:33` 自洽（人工读一遍）。
  - ✅ 已交叉验证：`DapperAotInitializer.cs`、`PalDDD.Dapper.csproj`、`README.md:770/790`、`docs/persistence-aot-status.md` 均已实读原文。
- **涉及文件**：`src/PalDDD.Dapper/PalDDD.Dapper.csproj:7`、`src/PalDDD.Dapper/DapperAotInitializer.cs:16-19`、`README.md:790`、`README.en.md:791`、`docs/aot.md`（待查）
- **状态**：⬜ 待处理

### [ ] ITM-690 · csproj 包描述纳入事实一致性门禁 · 可信度 ✅
- **维度**：门禁机械化（把红线从人工纪律变成机械强制）
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：ITM-689 这类漂移之所以能存活，是因为现有机械门禁不覆盖 `csproj` 的 `Description`。`scripts/doc-consistency.cs` 头注释明示其只保留 D7（`.ai/README.md` 文件地图），D1-D6/D8-D12 已下沉 `DocConsistencyGateTests`，均不读 csproj `Description`。而 `Description` 与文档中的 AOT 状态同属"可 grep 的事实值"。
- **证据**：`scripts/doc-consistency.cs:1-14` 的能力声明；`grep -rn "Description" scripts/*.cs` 无任何脚本读取 csproj 描述。
- **建议**：扩展 `scripts/doc-consistency.cs`（或在 `test/PalDDD.DependencyInjection.Tests/DocConsistencyGateTests.cs` 内下沉）新增判定：对声明了 AOT 能力的包，断言其 csproj `Description` 与 `docs/persistence-aot-status.md` 的状态值一致。判定逻辑抽纯函数 + 正反例自测。
- **风险**：低。设计上要注意只覆盖"有权威文档来源的事实值"，不要泛化成"csproj 描述必须与代码完全一致"（那会制造大量假红）。
- **验证**：
  - `--selftest` 覆盖正例与负例（防「判定恒真」的假绿）。
  - 变异验证：注入"未启用"描述到已启用 AOT 的包 → 脚本**非零退出** → 恢复。
  - 向 `scripts/gate-audit.cs` 追加隔离式探针并登记 `probedGates`。
  - 接入 `.githooks/pre-commit`（触发条件限 csproj 入暂存集）与 `.github/workflows/ci.yml`。
- **涉及文件**：`scripts/doc-consistency.cs` 或 `test/PalDDD.DependencyInjection.Tests/DocConsistencyGateTests.cs`、`scripts/gate-audit.cs`、`.githooks/pre-commit`、`.github/workflows/ci.yml`
- **状态**：⬜ 待处理

### [ ] ITM-691 · README 快速开始样例不可编译 · 可信度 ✅
- **维度**：文档准确性（代码样例可执行性）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：`README.md:464`（`README.en.md:463` 同）写 `builder.Services.AddSingleton<IMessageBroker>(new MessageBroker()); // InMemory Broker（无参构造）`；`README.md:726` 进一步声称"InMemory MessageBroker 用于测试"。实际 `PalDDD.Messaging` 只有 `public abstract class MessageBrokerBase`（`MessageBrokerBase.cs:21`）与 `public sealed class NullMessageBroker`（`MessageBroker.cs:72`），**无公开 `MessageBroker` 类，也无可用 InMemory broker**。`README.md:109` 的"InMemory 实现覆盖全部抽象接口"对 `IMessageBroker` 不成立。照抄快速开始无法编译，而这是新用户的第一段代码。
- **证据**：`grep -rn "^public.*class.*Broker" src/PalDDD.Messaging/` → 仅 `NullMessageBroker` 与 `MessageBrokerBase` 两行；抽样其余 11 处 README API 样例均与真实签名吻合（说明这是孤立缺陷，非系统性）。
- **建议**：改用 `NullMessageBroker` 并**在注释中明写其丢弃语义**（否则是把一个坑换成另一个坑）；同时修正 `README.md:109` 与 `:726` 的措辞。
- **风险**：低。语义替换需准确：`NullMessageBroker` 丢弃消息而非投递，快速开始若声称"能跑通消息流"会变成新的错误。
- **验证**：把快速开始段的 C# 代码块抽到 `samples/` 下的最小工程真实编译通过（或新增 README 代码块编译探针）；`grep -n "new MessageBroker()" README*.md` → 零命中。
- **涉及文件**：`README.md:109,464,726`、`README.en.md`
- **状态**：⬜ 待处理

### [ ] ITM-692 · Dapper DI 缺口补齐或显式化 · 可信度 ✅
- **维度**：架构一致性（三栈 DI 完整度）
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：同 ITM-686 的事实基础。`DapperEventLog`、`DapperProjectionCheckpointStore`、`DapperUnitOfWork` 三个类存在但无 DI 注册路径。
- **证据**：同 ITM-686。
- **建议**：两条路线择一（需先做 ITM-719 的裁决）：
  - **路线 A（补齐）**：为三个类增加注册路径，使三栈注册集对齐。
  - **路线 B（显式化）**：在 `DapperServiceCollectionExtensions` 的 XML 文档与 README 的 Dapper 段写明"EventLog/ProjectionCheckpoint/UoW 需直连构造"，并登记为 ITM-686 的豁免项。
- **风险**：**中**。路线 A 会改变 DI 解析行为（原本解析失败的地方变成成功），可能影响现有消费者的注册顺序或生命周期假设。若采用路线 A，须确认 `DbConnection` 与事务通道（`DapperAmbientTransaction`）对新注册的 store 仍然成立。
- **验证**：ITM-686 的测试从"红 + 豁免三项"变为"绿"（路线 A）或"红 + 文档化豁免"（路线 B）；若路线 A，需补一个端到端测试证明从 DI 解析出的 `IEventLog` 可用。
- **涉及文件**：`src/PalDDD.Dapper/DapperServiceCollectionExtensions.cs:97-114`、`README.md`（Dapper 段）
- **依赖**：ITM-686（测试先落地）
- **状态**：⬜ 待处理

### [ ] ITM-693 · outbox 索引补齐 + EF 模型/DDL 对齐 · 可信度 ✅
- **维度**：性能 / schema 一致性
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：两处独立缺陷合并处理。① 租约回读查询 `SELECT * FROM outbox_messages WHERE locked_by=@owner AND locked_until=@until`（`src/PalDDD.Dapper/SqlTemplates.cs:189`，用于 `DapperOutboxStore.cs:183-188`，PalORM 同款 `PalOrmOutboxStore.cs:145-147`）**无可用索引**：DDL 只有 `idx_outbox_status (status, next_attempt_at, locked_until)` 与 `idx_outbox_created (created_at)`。② pending 查询的 `ORDER BY created_at` 不被 `idx_outbox_status` 覆盖，需临时 B-tree 排序。③ EF 模型索引声明与手写 DDL 漂移：EF outbox 声明 `(Status, NextAttemptAt, CreatedAt)`（`src/PalDDD.Transactions.EFCore/OutboxDbContext.cs:370`），DDL 是 `(status, next_attempt_at, locked_until)`；EF Inbox 声明 `ProcessedAt` / `Status` / `(Status, ProcessingStartedAt)`（`InboxDbContext.cs:212-214`），DDL 中不存在。后果：SQLite/MySQL 每次 lease 回读全表扫描，且 Dapper/PalORM 用户与 EF 用户拿到不同索引集；框架不提供行清理，Processed 行留存使代价随运行时长线性增长。
- **证据**：内存 SQLite 对 `docs/sql/sqlite/000_schema.sql` 跑 `EXPLAIN QUERY PLAN`（租约回读谓词）→ `SCAN outbox_messages`（无索引）；对 `OutboxSelectPending` 谓词 → `SEARCH ... USING INDEX idx_outbox_status (status=?)` + `USE TEMP B-TREE FOR ORDER BY`。DDL 索引行：`docs/sql/sqlite/000_schema.sql:24-25`、`docs/sql/mysql/000_schema.sql:20-21`、`docs/sql/postgresql/000_schema.sql:21-22`。
- **建议**：三方言 DDL 增加覆盖 `(locked_by, locked_until)` 与 `(status, created_at)` 的索引；同步 EF 模型的索引声明，使 EF 与 DDL 一致；在迁移文档写明既有部署需手工补索引。
- **风险**：低（纯增索引）。注意点：新增索引会略微增加写入成本；`(status, created_at)` 与既有 `idx_outbox_status (status, next_attempt_at, locked_until)` 存在前缀重叠，可评估是否直接调整既有索引列序而非新增（需验证 pending 查询仍走索引）。
- **验证**：`EXPLAIN QUERY PLAN` 对租约回读查询输出含 `INDEX`、不含 `SCAN outbox_messages`；对 pending 查询不含 `USE TEMP B-TREE FOR ORDER BY`。
- **涉及文件**：`docs/sql/{sqlite,mysql,postgresql}/000_schema.sql`、`src/PalDDD.Transactions.EFCore/OutboxDbContext.cs:370`、`src/PalDDD.Transactions.EFCore/InboxDbContext.cs:212-214`、迁移文档
- **状态**：⬜ 待处理

### [ ] ITM-694 · `secret-scan` 检测面扩充 · 可信度 ✅
- **维度**：安全（凭据防线）
- **优先级**：P1 · 危害: 高 · 复杂度: 中
- **问题**：`scripts/secret-scan.cs` 是仓库唯一的凭据控制，但假阴性面偏大，六类绕过全部有据：
  1. **扩展名白名单**（`:49`）不含 `.pem`/`.key`/`.pfx`/`.sql`/`.razor`/`.resx` 与无扩展名文件。提交 `deploy/signing.pem` 可完全绕过 mode-1 私钥检测器（该检测器本身存在，但文件永不被读）。
  2. **连接串正则**要求 `Host|Server|Data Source` 与 `Password|Pwd` **同行**。多行 JSON/YAML、`.env` 形态 `DB_PASSWORD=...`、URI 形态 `postgres://user:pass@host/db`、Azure `AccountKey=` 全部不可见；`.cs` 中的 `const string adminPassword = "..."` 同样不可见。
  3. **白名单 3**（`:167`）豁免"无数字且长度 < 10"的密码值。
  4. **白名单 1**（`:144`）对整值做 `example|sample|demo|placeholder|dummy|fake|mock` 子串匹配，`S3cr3t-demo-key-9x` 这类真实凭据会被豁免。
  5. **key 前缀覆盖窄**：`sk-[A-Za-z0-9]{20,}` 漏 `sk-proj-`/`sk_live_`（连字符/下划线断字符类）；GitHub 令牌只认 `ghp_`（无 `gho_`/`ghu_`/`ghs_`/`ghr_`）；无 Google `AIza`、无 AWS 40 字符 secret、无 JWT（`eyJ`）、无 Slack `xapp-`。
  6. **无熵启发式**，且仅扫已跟踪文件（这是设计，但意味着最可能放真实凭据的本地文件不在范围）。
- **证据**：`scripts/secret-scan.cs:49,144,167` 与 `BuildPatterns` 的模式表已实读；`.gitignore:94` 覆盖的 `appsettings.test.local.json` 含真实形态口令（`git check-ignore -v` 确认忽略），该文件按设计不被扫描。
- **建议**：逐类扩充，每类配 `--selftest` 正例；白名单豁免改为需同时满足多个弱信号（而非单条件即放行）。
- **风险**：低（门禁比现状更严，可能出现新命中，需评估是否为本仓既有样本的误报）。
- **验证**：
  - 用上文六类各构造样本作为负例，全部被捕获。
  - 正例（合法占位符如 `Password=xxx`）仍放行，不制造假红。
  - `--selftest` 全绿；变异验证：关掉新检测器任一分支确认自测变红。
  - ✅ 已交叉验证：`scripts/secret-scan.cs` 存在且含 `--selftest`。
- **涉及文件**：`scripts/secret-scan.cs`
- **状态**：⬜ 待处理

### [ ] ITM-695 · 认证与授权文档 + 策略入口 · 可信度 ✅
- **维度**：安全（部署面）/ 文档
- **优先级**：P1 · 危害: 高 · 复杂度: 中
- **问题**：`src/` 不存在任何认证/授权原语（grep `AddAuthentication|AddAuthorization|RequireAuthorization|JwtBearer|UseAuthentication|UseAuthorization|[Authorize]` 仅命中一处文档注释 `src/PalDDD.Hosting.AspNetCore/AspNetCore/HealthCheckExtensions.cs:24`）。委托宿主是正确的库设计，问题在两点：① `MapCommand`/`MapQuery` 返回 `IEndpointConventionBuilder`，调用方**可以**链 `.RequireAuthorization()`，但没有策略参数/重载，也没有测试证明该路径可用；② `README.md` 与 `README.en.md` 对"认证/授权/鉴权"的提及数均为 **0**（已 grep 计数）。叠加默认 `/health` 返回组件拓扑与依赖状态、两个样例（`samples/PalDDD.MinimalApi/Program.cs:29-35`、`samples/PalDDD.ECommerce/Program.cs`）暴露无鉴权写端点，后果是：照抄文档样例组装出的应用是一个完整的开放命令/查询 API。
- **证据**：上述 grep 计数；`grep -cin "认证\|授权\|鉴权\|authentication\|authorization" README.md README.en.md` → `README.md:0`、`README.en.md:0`。
- **建议**：① README 增"认证与授权"段落，说明委托宿主的立场、`.RequireAuthorization()` 的可用形态、`/health` 的默认暴露面与收敛方式；② 为 `MapCommand`/`MapQuery` 增加可选策略参数或提供带策略的重载，并补一个未授权请求被拒的测试。
- **风险**：文档部分低。API 部分**中**：新增重载会改动公共 API 快照（`PublicApiSnapshotTests`），需同提交更新基线；若只加重载不改变现有签名，则属非破坏性。
- **验证**：文档段落存在且含可运行示例；`grep -c "RequireAuthorization" README.md` 大于 0；新增测试证明带策略时未授权请求返回 401/403。
- **涉及文件**：`README.md`、`README.en.md`、`src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs`、`test/PalDDD.Hosting.AspNetCore.Tests/`、公共 API 快照
- **状态**：⬜ 待处理

### [ ] ITM-696 · `MapQuery` 调用方绑定异常归 400 · 可信度 ✅
- **维度**：错误处理正确性
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：`src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs:200-206` 的自身文档写明：任何来自 `bindQuery` 且非 `PalValidationException` 的异常会以 500 逃逸。样例正落入此坑——`samples/PalDDD.MinimalApi/Program.cs:35` 调 `Guid.Parse((string)ctx.Request.RouteValues["id"]!)` 且路由无约束，故 `GET /orders/xyz` 返回 500。无数据泄漏（`ExceptionMiddleware` 返回固定 body），但错误分类错误：客户端输入问题被报成服务端故障。
- **证据**：`EndpointExtensions.cs` 的 `MapQuery` XML doc（`:200-206`）自述该行为；样例代码 `:35` 已读。
- **建议**：在 `MapQuery` 内捕获调用方绑定阶段的常见输入异常（`FormatException`/`ArgumentException`/`OverflowException`）并映射为 400 ProblemDetails；或在文档中明确"绑定失败由调用方负责转 400"并提供辅助方法。
- **风险**：低。注意不要过度捕获（把真正的服务端 bug 也吞成 400），建议按异常类型白名单而非 catch-all。
- **验证**：新增测试：以不可解析的路由值调用 `MapQuery` 端点，断言返回 400 而非 500；回归测试确认 `PalValidationException` 仍映射 400。
- **涉及文件**：`src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs`、`test/PalDDD.Hosting.AspNetCore.Tests/`、`samples/PalDDD.MinimalApi/Program.cs:35`
- **状态**：⬜ 待处理

---

## M2 详细条目

### [ ] ITM-697 · 唯一约束分类器下沉（10 份 → 1 份） · 可信度 ✅
- **维度**：可维护性（跨栈重复收敛）
- **优先级**：P3 · 危害: 中 · 复杂度: 难
- **问题**：唯一约束冲突分类器存在 **10 份私有副本**——Dapper 5 份（`src/PalDDD.Dapper/DapperInboxStore.cs:217`、`DapperSagaStateStore.cs:243`、`DapperEventLog.cs:283`、`DapperIdempotencyStore.cs:257`、`DapperProjectionCheckpointStore.cs:227`）+ EFCore 5 份；而 PalORM 栈已抽出单一 `src/PalDDD.PalORM/Stores/SqlErrorClassifier.cs:16`。同源的重复还包括：`leaseDuration must be greater than zero`（9 处）、`ThrowIfNegativeOrZero(batchSize)`（22 处）、`FailureReason.Truncate(...,2040)`（6 处）。仓库自述"姊妹修复在 40+ 轮提交中反复出现"，本项是削掉该税的最大单点。
- **证据**：上述文件:行号均已 grep 定位；单一实现范式见 `SqlErrorClassifier.cs:16`。
- **建议**：把分类器与守卫抽为共享内部助手（`internal` 而非 public，避免扩大公共 API 面），三栈各自消费。范式直接复用 PalORM 已完成的收敛。
- **风险**：**中**。三栈的方言错误码不同（PG SQLSTATE 23505 / MySQL 1062 / SQLite 19 + extended code），合并时必须验证每栈原有分类测试仍然通过；Dapper 与 EFCore 的异常包装层也不同（EF 有 `DbUpdateException` 包裹）。
- **验证**：私有副本数 = 1（grep 计数）；三栈原有分类测试全绿；新增跨栈等价性测试（同一冲突场景在三栈产生同一分类结果）。
- **涉及文件**：`src/PalDDD.Dapper/*Store.cs`（5 处）、`src/PalDDD.*.EFCore/*.cs`（5 处）、参照 `src/PalDDD.PalORM/Stores/SqlErrorClassifier.cs`
- **依赖**：ITM-686（先让三栈差异可观测，再动重构）
- **重组前置**：跨文件重构，须用 Serena `find_referencing_symbols` 确认引用点，禁止仅靠 grep 文本匹配（本仓 AGENTS.md 架构硬约束）
- **状态**：⬜ 待处理

### [ ] ITM-698 · EF Core outbox 查询与租约批量化 · 可信度 ✅
- **维度**：性能（已存档基准实测 5 倍差距）
- **优先级**：P2 · 危害: 高 · 复杂度: 难
- **问题**：两处合并。① **租约 N+1**：`src/PalDDD.Transactions.EFCore/SqliteOutboxDbContext.cs:108` 读候选页后，`:111-128` **对每一行**发一条 `ExecuteUpdateAsync`。批 100 即 100 次 UPDATE 往返。仓库存档基准实测 `Outbox_Lease_Batch100` = **24,043.8 μs / 3,681 KB**（EF）vs 2,655 μs / 860 KB（Dapper，Ratio 5.04）vs 3,546 μs（PalORM），是其自身 `Outbox_GetPending_Batch100`（499.6 μs）的 48.8 倍。② **整表分页**：`:51-69` 的 `while (result.Count < batchSize)` 配 `Skip(skip).Take(batchSize)`，时间过滤在**内存**中做（EF SQLite 无法翻译 `DateTimeOffset` 有序比较）；当表头是未来重试行或活跃租约行时，每次 tick 发 ceil(表大小/批大小) 条 SELECT 并把整表物化，代码注释 `:32-37` 自承"worst case full-table paged scan"。
  注意：逐条 CAS 是有意的**正确性**修复（两实例可同时租约同一批），不能为了性能回退该语义。
- **证据**：上述行号已实读；基准数值引自 `BenchmarkDotNet.Artifacts/results/PalDDD.Benchmarks.EfCorePersistenceBenchmarks-report-github.md`（存档，未重跑）。
- **建议**：分两步，且**第二步需先裁决**。
  - **步骤 1（可直接做）**：消除整表分页。把时间过滤下推 SQL。根因是 `DateTimeOffset` 在 EF SQLite 不可翻译，可行方向是存可比排序的列（ISO8601 文本或 `long` 时间戳）并在 SQL 层比较。
  - **步骤 2（需裁决）**：租约批量化。难点是 CAS 的版本守卫按行不同（每行 `LockedUntil` 原值可能不同），**不能用单一 `LockedUntil` 等值条件覆盖整批**。可选：按 `LockedUntil` 值分组（实践上同批多为 null 或同一过期时刻，分组数小），或引入单调递增的 `version`/`RowVersion` 列做批内统一谓词（属 schema 变更）。
- **风险**：**高**。这是并发正确性路径，本仓已有 40+ 轮针对该契约（lease token / retry_count fencing / status guard）的修复史。若步骤 2 必须改 schema，应停止并把本任务降级为"只做索引（ITM-693）与查询下推（步骤 1）"，批量化推迟到 v3.0 破坏性窗口。
- **验证**：
  - 先写并发安全网（若 ITM-685 未覆盖）：两实例同时 `LeasePendingMessagesAsync` 同一批，断言租约集合不相交（可参照 `test/PalDDD.Integration.Tests/OutboxSqliteConcurrencyTests.cs:104-166` 与 `test/PalDDD.PalORM.Tests/PalOrmConcurrencyTests.cs` 的 10-worker / 租约唯一性写法）。
  - `Outbox_Lease_Batch100` 比值进入 Dapper 的 2 倍以内（当前 5.04），由 ITM-688 的 CI 基准产出。
  - 全部并发测试保持绿。
- **涉及文件**：`src/PalDDD.Transactions.EFCore/SqliteOutboxDbContext.cs:51-128` 及 MySql/PG 姊妹实现
- **依赖**：ITM-685（死信路径安全网）、ITM-688（基准可对比）
- **状态**：⬜ 待处理

### [ ] ITM-699 · 事实单点化与计数守卫 · 可信度 ✅
- **维度**：文档内部一致性
- **优先级**：P3 · 危害: 低 · 复杂度: 中
- **问题**：同一事实（测试规模）在 5 处出现且不同步：`README.md:835` 写"1202 项实测"、`README.md:907` 写"1379 项实测用例"（同一文件自相矛盾）；`README.en.md:838` vs `:913` 同款；`docs/test-coverage-baseline.md:6`、`docs/performance.md:30`、`docs/tutorial.md:113` 停留 v2.1.0 的 1202。CHANGELOG 2.2.0 的权威口径是 1379。实测当前树 `[Test]` 方法为 1256（1379 应含参数化展开）。
- **证据**：`grep -n "1202\|1379" README.md` → 命中 `:835`、`:907`；`grep -rn "\[Test\]" test/ --include=*.cs | wc -l` → 1256。
- **建议**：确立每类事实的单一权威位置（计数类建议放 `docs/test-coverage-baseline.md`），其余位置改写为指针或标注"v2.1.0 时点值"；对关键计数加机械守卫（复用 `DocConsistencyGateTests.cs:426-450` 已有的计数断言模式）。
- **风险**：低。
- **验证**：同一事实在仓库中只有一处定义；注入错误计数时门禁变红；`grep -c "1202" README.md README.en.md docs/test-coverage-baseline.md docs/performance.md docs/tutorial.md` 全为 0 或全带时点前缀。
- **涉及文件**：`README.md:835,907`、`README.en.md:838,913`、`docs/test-coverage-baseline.md:6`、`docs/performance.md:30`、`docs/tutorial.md:113`
- **状态**：⬜ 待处理

### [ ] ITM-700 · 入门路径打通 · 可信度 ✅
- **维度**：开发体验（贡献者入口）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：仓库无 `CONTRIBUTING.md`；`README.md` 全文无 clone/build/SDK 前置说明（只有 NuGet 消费段 `:59-119`）；唯一的开发环境文档是 `docs/development.md:9` 一行".NET SDK 11.0.x (RC1+)"，而 `global.json:3` 钉的是**精确预发布版本** `11.0.100-rc.1.26425.128`；README 的文档表（`:863-879`）**未收录** `docs/development.md`，也无 AGENTS.md 链接。后果：新贡献者按 README 找不到"如何本地构建/跑测试/装钩子"；装 .NET 10 或等 .NET 11 GA 的人会先撞 SDK 解析失败，而错误信息不会告诉他要装 RC SDK。
- **证据**：仓库根 `ls CONTRIBUTING*` 无结果；`grep -n "development.md" README.md` 零命中；`global.json:3` 的精确版本号已读。
- **建议**：新建 `CONTRIBUTING.md`（内容可由 `docs/development.md` 前段 + 本仓 `AGENTS.md` §2 门禁表组合），或在 README 文档表首行收录 `docs/development.md` 并补 SDK 预发布说明。两条路线择一即可，关键是"README 一步可达"。
- **风险**：低。需注意 `docs/development.md` 本身质量高，不要复制其全文（复制必然漂移，与本仓"文档单点化"原则冲突），应链接。
- **验证**：新人按 README 一步可达构建/测试/钩子说明；SDK 预发布要求被明写；`docs/development.md` 出现在 README 文档表。
- **涉及文件**：新增 `CONTRIBUTING.md` 或 `README.md:863-879`、`README.en.md` 同款
- **状态**：⬜ 待处理

### [ ] ITM-701 · 文档 `.sh` 残留 / `.ai` 引用 / CI 状态表修正 · 可信度 ✅
- **维度**：文档准确性（失效命令引用）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：三类合并。① **失效命令引用**：`docs/conventions.md:825` 引 `ci-coverage.sh`；`docs/release.md:436` 引 `gate-check.sh`、`:651` 引 `changelog-facts.sh`、`:693` 引 `changelog-check.sh`。实际已全部 C# 化（CHANGELOG 2.2.0 记录"删除全部 8 个 `.sh`/`.py`"），真实文件为 `ci-coverage.cs`、`changelog-facts.cs`、`changelog-check.cs`、`gate.cs`；`docs/release.md:665` 自己写的是正确命令，同一文档内前后矛盾。V9 守卫只查 `bash X.sh` / `dotnet run X.cs` 形态，反引号/表格形态是它自述的已知盲区（`scripts/verify-conventions.cs:161-167`）。② **`.ai` 引用**：`docs/architecture.md:5` 称".ai/ 目录内嵌统一质量体系 v2.1，详见 `.ai/README.md`"，实际 `.ai/.git` 存在（独立嵌套仓库），`git ls-files .ai/` 返回 0，fresh clone 不存在；且本工作树 `.ai/scripts/` 仅 2 个文件，而 `.ai/README.md:139-140` 描述 20 个脚本并给出不存在的入口。③ **CI 状态表两个方向都说反**：`docs/conventions.md:1030` 说覆盖率"脚本就绪，接线待阈值校准"（实际 `ci.yml:202-235` 已接线，阈值 0.70）；`:1035` 说性能契约 `--smoke` 烟测挂在 CI（实际 `.github/workflows/` 零命中）。
- **证据**：上述行号均已实读；`git ls-files .ai/ | wc -l` → 0；`ls .ai/scripts/` → 2 个文件。
- **建议**：逐处改写；`.ai` 引用改为条件式（"若存在 .ai（独立仓库，不在本仓分发）"）或删除；V9 的盲区（反引号/表格形态）纳入检测或至少在 `docs/conventions.md` 显式登记。
- **风险**：低（纯文本）。
- **验证**：`grep -rn "\.sh" docs/` 中命令类引用归零；`dotnet run scripts/verify-conventions.cs --quick` 全绿；`docs/conventions.md:1030,1035` 与 `ci.yml` 实况一致。
- **涉及文件**：`docs/conventions.md:825,1030,1035`、`docs/release.md:436,651,693`、`docs/architecture.md:5`
- **状态**：⬜ 待处理

### [ ] ITM-702 · 锁文件启用 · 可信度 ✅
- **维度**：依赖可复现性（供应链）
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：全仓 `find -name packages.lock.json` 返回 0，`RestorePackagesWithLockFile`/`RestoreLockedMode` 未在任何 props/targets 设置（grep 零命中）。叠加 `global.json:3` 的 RC 线浮动 + `rollForward: latestMajor` + `allowPrerelease: true` 的弱钉版，还原不可复现；CI 不强制 locked mode。
- **证据**：上述两条 grep 命令均为零结果。
- **建议**：`Directory.Build.props` 设 `RestorePackagesWithLockFile=true` 生成并跟踪 `packages.lock.json`；CI 用 `dotnet restore --locked-mode`。
- **风险**：**中**。RC 线浮动可能造成首次 lock 不稳定（同一 graph 在不同时间解出不同版本）；需确认 lock 文件生成后 CI 稳定通过。若在 .NET 11 GA 前不稳定，可推迟到 GA 清偿窗口与 ITM-724 一并处理。
- **验证**：全项目生成 `packages.lock.json` 并被跟踪；CI 以 `--locked-mode` 还原通过；有人为改版本时 CI 变红。
- **涉及文件**：`Directory.Build.props`、`.github/workflows/ci.yml`、各项目 `packages.lock.json`
- **状态**：⬜ 待处理

### [ ] ITM-703 · `PalDDD.Transactions` 的 `M.E.*` 欠声明 · 可信度 ✅
- **维度**：打包正确性（消费者可解析性）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：`src/PalDDD.Transactions/` 代码使用 `IOptionsMonitor` 与 `BackgroundService`（`src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs:5,29`、`PeriodicBackgroundProcessor.cs:12-13`），但 csproj 无对应 `PackageReference`；打包后的 nuspec 也确认缺失（`nupkgs/preview-2.1.0/PalDDD.Transactions.2.1.0.nupkg` 只列 PalDDD.* 与 Ulid）。后果：纯类库消费者需自行补 `Microsoft.Extensions.Options` / `Hosting.Abstractions`，否则解析失败；本地构建被 SDK 包裁剪机制掩盖，CI 也测不到。
- **证据**：csproj `PackageReference` 列表与源码 using 不一致；nuspec 内容已核。
- **建议**：在 `PalDDD.Transactions.csproj` 补 `Microsoft.Extensions.Options` 与 `Microsoft.Extensions.Hosting.Abstractions`（版本走 `Directory.Packages.props` 中央管理）。
- **风险**：低。可能改变传递依赖图，需确认不引入版本冲突（中央包管理已钉版）。
- **验证**：重新打包后 nuspec 含两个 `Microsoft.Extensions.*` 依赖；用纯类库消费者（不手动加 M.E.*）编译含 `AddPalTransactions` 的最小工程通过。
- **涉及文件**：`src/PalDDD.Transactions/PalDDD.Transactions.csproj`
- **状态**：⬜ 待处理

### [ ] ITM-704 · `PalDDD.Base` 分析器交付缺口 · 可信度 ⚠
- **维度**：打包正确性（卖点交付）
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：`PalDDD.Base` 的打包 nuspec 声明对 `PalDDD.Analyzers` / `PalDDD.Analyzers.CodeFixes` / `PalDDD.Core.SourceGen` 的依赖，`include` 白名单为 `Runtime,Compile,Native,ContentFiles,BuildTransitive`，**不含 `Analyzers`**；而这三个包只含 `analyzers/dotnet/cs/*.dll`（由 `unzip -l nupkgs/preview-2.1.0/PalDDD.Analyzers.2.1.0.nupkg` 得）。若属实，安装 `PalDDD.Base` 的消费者拿不到分析器与源生成器，与包描述"含编译时分析器"矛盾，且 README 主推卖点"38 条编译期诊断"不落地。
- **证据**：nuspec 的 `include` 白名单与三个包的内容清单（子 agent 从 `nupkgs/preview-2.1.0/` 读出）。
- **⚠ 可信度说明**：本项为**静态阅读 nuspec 得出的结论，未做端到端验证**（未实际安装 `PalDDD.Base` 到消费者工程观察分析器是否生效）。按本仓"负向声明不作数"规则，落地前必须先做正面验证。
- **建议**：先验证再修。验证方法：在 `samples/` 或临时工程中仅引用 `PalDDD.Base`，检查 `PDDD001` 等诊断是否触发；若不触发，则在 `PalDDD.Base.csproj` 的依赖 `include` 中补 `Analyzers`（或改用 `PrivateAssets` 的正确组合）。
- **风险**：中。`include` 白名单是 NuGet 打包语义的细节，改错会导致打包失败或产生非预期的传递依赖。需对照 NuGet 官方文档确认 `Analyzers` 资源类型的正确写法。
- **验证**：端到端验证（安装 `PalDDD.Base` 后故意写一个违反 PDDD 规则的类，确认编译报错）；变异验证：移除修复后诊断不再触发。
- **涉及文件**：`src/PalDDD.Base/PalDDD.Base.csproj`、`src/PalDDD.Analyzers/`、`src/PalDDD.Core.SourceGen/`
- **状态**：⬜ 待处理

---

## M3 详细条目

### [ ] ITM-705 · 无断言测试清理 · 可信度 ✅
- **维度**：测试质量
- **优先级**：P2 · 危害: 低 · 复杂度: 易
- **问题**：仓库自测口径为 1256 个测试中 30 个零断言方法；扣除 10 个方言探针委派壳与 9 个 FsCheck 属性测试后，实际无断言者约 11 个：`test/PalDDD.Messaging.Tests/MessagingTests.cs:246-251`（`PublishAsync_Generic_NoOp`）、`:253-262`（`PublishAsync_NonGeneric_NoOp`）、`:274-282`（`SubscribeAsync_Dispose_IsIdempotent`）；`test/PalDDD.Repository.EFCore.Tests/RepositoryEfCoreTests.cs:28-38`、`:40-48`、`:50-57`；`test/PalDDD.Integration.Tests/DapperUnitOfWorkTests.cs:106-112`；`test/PalDDD.Integration.Tests/MessageEvolutionTests.cs:140-165`。
- **证据**：上述文件:行号已读原文确认无断言。
- **建议**：逐个补有意义断言，或若确为契约性 no-op（如 NullObject），改名为显式标注并在测试内写明"本用例验证不抛出"的设计意图。本项是 ITM-706 收紧棘轮的前置。
- **风险**：低。
- **验证**：`AssertionStrengthGateTests` 的零断言计数下降；被改的测试仍全部通过。
- **涉及文件**：上述四个测试文件
- **状态**：⬜ 待处理

### [ ] ITM-706 · `AssertionStrengthGateTests` 加固 · 可信度 ✅
- **维度**：门禁可靠性（验证验证者）
- **优先级**：P3 · 危害: 低 · 复杂度: 中
- **问题**：`test/PalDDD.DependencyInjection.Tests/AssertionStrengthGateTests.cs` 是合理的防退化棘轮，但不是断言强度验证器：① 检测器把任何 `Assert\w*[.(]` 调用当断言（`:193`），故 `Assert.That(true).IsTrue()` 通过而什么都没验证，它不分析断言操作数；② 门禁自己的测试含两处恒真断言（`:59-60` 的 `isNotNullCount >= 0`、`zeroAssertMethods.Count >= 0`），恰是它无法拒绝的那类测试；③ 预算余量：声明实际 185 对上限 200（`:29`），可再加约 15 个弱断言才触发；④ 盲区：`s_testMethodStart` 要求参数表后跟 `{`（`:197-199`），表达式体 `[Test]` 方法永不被检查（当前 0 个，无实际漏检，属未来风险）。
- **证据**：上述行号已读；恒真断言可在 `:59-60` 直接看到。
- **建议**：自测移除恒真断言；`IsNotNull` 需配合其他断言或用例计数；收紧预算余量至接近实际值。注意第 ④ 项信号稀释：桶内约 2/3 其实行为很强，收紧前应先按 ITM-705 清理。
- **风险**：低，但收紧棘轮会让当前套件变红，须先完成 ITM-705。
- **验证**：门禁自测无恒真断言；构造 `Assert.That(true).IsTrue()` 类样本能被拒绝（若能实现操作数分析）；收紧后套件仍全绿。
- **涉及文件**：`test/PalDDD.DependencyInjection.Tests/AssertionStrengthGateTests.cs`
- **依赖**：ITM-705
- **状态**：⬜ 待处理

### [ ] ITM-707 · 挂钟延迟测试改 `FakeTimeProvider` · 可信度 ✅
- **维度**：测试稳定性（去 flaky）
- **优先级**：P3 · 危害: 中 · 复杂度: 中
- **问题**：三处以真实墙钟等待：`test/PalDDD.Transactions.Tests/OutboxProcessorTests.cs:38,60,79,83,101,126` 与 `SagaProcessorTests.cs:52,73,93,97,118` 等待 100-400ms 并断言 `>= 2` 次租约调用（注释记录了过往 CI 假红 ITM-278）；`FanOutStepTests.cs:29` 依赖 500ms 任务对 200ms 超时（2.5 倍余量）。仓库自己提供 `FakeTimeProvider.AdvanceNowAndTriggerTimers`（`test/PalDDD.Testing/`）并在 `InMemoryStoreTests.cs` 与 Core 测试中使用，但上述三处未采用。
- **证据**：上述行号；`test/PalDDD.Testing/TestHelpers.cs` 提供确定性时间原语。
- **建议**：改用 `FakeTimeProvider` 推进时间，消除真实等待。
- **风险**：低。需确认被测处理器确实接受注入的 `TimeProvider`（`PeriodicBackgroundProcessor` 已支持）。
- **验证**：三个文件内无真实 `Task.Delay` 等待；测试总耗时下降且仍全绿；连续多轮运行无翻转（可用 `dotnet run scripts/test-gate.cs` 观察）。
- **涉及文件**：`test/PalDDD.Transactions.Tests/OutboxProcessorTests.cs`、`SagaProcessorTests.cs`、`FanOutStepTests.cs`
- **状态**：⬜ 待处理

### [ ] ITM-708 · Kafka 错误路径测试补齐 · 可信度 ✅
- **维度**：测试覆盖（消息面）
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：Kafka 仅 3 个测试（往返、取消、多消息），无投毒消息 / 死信 / 重试测试；RabbitMQ 有死信与不可路由发布失败测试（`test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs:586,702`）。两个 broker 的错误路径覆盖密度不对称。
- **证据**：上述行号与测试数对比（子 agent 计数：Kafka 3 / RabbitMQ 含死信与不可路由失败）。
- **建议**：补 Kafka 投毒消息、消费失败重试、取消传播的集成测试，使错误路径密度接近 RabbitMQ。
- **风险**：低（纯新增测试）。依赖 Kafka 容器可用（Testcontainers）。
- **验证**：Kafka 测试数与错误路径密度接近 RabbitMQ；CI 中该 job 全绿；本地无 Docker 时按现有 `Skip.Test` 语义跳过而非失败。
- **涉及文件**：`test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs`
- **状态**：⬜ 待处理

### [ ] ITM-709 · 复杂度热点削减 · 可信度 ✅
- **维度**：可维护性
- **优先级**：P3 · 危害: 低 · 复杂度: 中
- **问题**：两个已发布业务逻辑方法嵌套过深：`src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs:58` `ProcessBatchAsync`（方法体 151 行，最大嵌套 6；try/catch 嵌在 foreach 嵌在 try 内）；`src/PalDDD.Transactions/Saga/SagaProcessor.cs:110` `CheckTimeoutsAsync`（方法体 146 行，最大嵌套 7；`:134-243` 有 5 层 try/foreach 交织）。
- **证据**：括号深度扫描与实读确认。
- **建议**：把逐项处理抽成私有方法，降低嵌套。
- **风险**：中（改已发布业务逻辑）。须先有 ITM-685 类的安全网。
- **验证**：两方法嵌套深度 ≤ 3；相关测试全绿（`test/PalDDD.Transactions.Tests/`）。
- **涉及文件**：`src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs:58`、`src/PalDDD.Transactions/Saga/SagaProcessor.cs:110-243`
- **依赖**：ITM-685
- **状态**：⬜ 待处理

### [ ] ITM-710 · `Saga` 四条重试车道模板化 · 可信度 ✅
- **维度**：可维护性（消除四份克隆）
- **优先级**：P3 · 危害: 低 · 复杂度: 难
- **问题**：`src/PalDDD.Transactions/Saga/Saga.cs`（1002 行）中四个近乎相同的重试/补偿车道：`ExecuteNormalStepAsync`（`:396-463`）、`ExecuteFanOutStepAsync`（`:469-547`）、`ExecuteChildSagaStepAsync`（`:555-648`）、`ExecuteDynamicStepAsync`（`:784-902`）；差异只在被执行的主体。注释自承复制：`:528-529`、`:629-630`、`:883-884` 三处写"P3 修复（十七轮）：补偿失败嵌套（见 `ExecuteNormalStepAsync` 同名修复注释）"。另有四个同构 `SafeObserve*` 包装（`:342-394`）。后果：每次修复落四遍，第五种 step 类型即第五份克隆。
- **证据**：行号与重复注释已实读。
- **建议**：抽出单一模板方法（如 `RunWithRetryAsync(Func<...> body)`），四个车道只提供各自主体。
- **风险**：**高**。1002 行 + 31% 注释 + 依赖 Saga 语义，是本清单风险最高的改动。建议先完成 ITM-685 / 709，并在独立提交中分步做（先抽 SafeObserve 包装四个，再抽车道）。
- **验证**：四条车道收敛为一处；`test/PalDDD.Transactions.Tests/SagaTests.cs`（约 1003 行）全绿；补偿路径行为逐条对齐（含 OCE 重抛、观察失败、聚合异常包装）。
- **涉及文件**：`src/PalDDD.Transactions/Saga/Saga.cs:342-394,396-463,469-547,555-648,784-902`
- **依赖**：ITM-685、ITM-709
- **工作量**：XL（需拆分提交）
- **状态**：⬜ 待处理

### [ ] ITM-711 · 依赖与包卫生 · 可信度 ✅
- **维度**：依赖治理
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：四项合并。
  1. **零引用中央声明**：`Directory.Packages.props:53` 声明 `Microsoft.Data.SqlClient` 7.0.2，但全仓 csproj 无任何 `PackageReference` 使用它（EF SqlServer provider 已按文件自身注释移除）。
  2. **许可证表达式过宽**：项目 `PackageLicenseExpression` 为 `AGPL-3.0-or-later`（`Directory.Build.props:63`），而 PalORM.Core/SourceGen/Sqlite/PostgreSql/MySql 5.5.1 的 nuspec 声明 `AGPL-3.0-only`（作者同为 "PalDDD"）。组合"only"组件意味着合并作品不能在 `or-later` 条款下传递，包元数据承诺过宽。
  3. **陈旧预览钉扎**：`Microsoft.NETCore.Platforms 8.0.0-preview.7.23375.6` 是 2023 年预览版包，跨两个主版本，属不可审计依赖。
  4. **包元数据与产物卫生**：`nupkgs/` 内容陈旧混杂（顶层 1.1.0 + `preview-2.1.0/`，当前版本 2.2.0；含 5 个 5.1.0 的 vendored PalORM 包而构建用 5.5.1）；2.1.0 时代 18 个包的 nuspec `description` 为占位符 `Package Description`。
- **证据**：`grep -rn "Microsoft.Data.SqlClient" --include=*.csproj --include=*.props .` 仅命中中央声明一处；`Directory.Build.props:63` 与 PalORM nuspec 许可证字段；`find . -name packages.lock.json` 为 0（与 ITM-702 呼应）。
- **建议**：移除零引用声明；许可证表达式统一为 `AGPL-3.0-only`（或为 PalORM 加例外说明）；`Microsoft.NETCore.Platforms` 评估能否随 GA 清理；`nupkgs/` 只留当前版本；补齐包 `description`。
- **风险**：低到中。**许可证变更需法律判断**，若采用 `only` 会影响下游使用者的合规假设，建议先确认意图（详见 ITM-724 与"需裁决"节）。`Microsoft.Data.SqlClient` 若计划恢复 SQL Server 支持则不应移除。
- **验证**：`grep -rn "Microsoft.Data.SqlClient" Directory.Packages.props` 零命中；许可证表达式与 PalORM 依赖条款自洽；`ls nupkgs/` 只剩当前版本；随机抽 3 个包检查 nuspec `description` 非占位符。
- **涉及文件**：`Directory.Packages.props:53`、`Directory.Build.props:63`、`nupkgs/`、各 `src/*/*.csproj`
- **状态**：⬜ 待处理

### [ ] ITM-712 · 注释变更史考古清理 · 可信度 ⚠
- **维度**：可读性
- **优先级**：P3 · 危害: 低 · 复杂度: 难
- **问题**：注释/代码比偏高且含大量变更史叙事：`src/PalDDD.Transactions/Saga/Saga.cs` 315/1002（31%）、`src/PalDDD.Dapper/DapperOutboxStore.cs` 177/391（45%）、`src/PalDDD.PalORM/Stores/PalOrmOutboxStore.cs` 151/349（43%）。相当比例是"v43 P3（ITM-092 管线孪生）"式叙事、同一解释在 3-4 个姊妹文件重复、以及与代码状态不符者（ITM-689 即一例）。
- **⚠ 可信度说明**：注释价值是**判断**而非事实，哪些该删需逐条评审。本项不宣称"注释过多是缺陷"，只指出它对 ITM-710 这类机制性重构构成阻力。
- **建议**：把重复在姊妹文件的解释改为指针；移除与代码矛盾的注释。**不建议大规模删注释**，只处理重复与矛盾两类。
- **风险**：中。删注释会丢失历史理由（本仓注释承载了 ITM 上下文），误删成本高于收益。建议只做"重复 → 指针"与"矛盾 → 修正"，不做"精简"。
- **验证**：姊妹文件的重复注释段归零；无与代码状态矛盾的注释（可用 ITM-689 的 grep 口径抽查）。
- **涉及文件**：`src/PalDDD.Transactions/Saga/Saga.cs`、`src/PalDDD.Dapper/DapperOutboxStore.cs`、`src/PalDDD.PalORM/Stores/PalOrmOutboxStore.cs`
- **依赖**：ITM-710（先重构再清注释，避免清理后重构又改一遍）
- **状态**：⬜ 待处理

### [ ] ITM-713 · `scripts/README.md` 索引 · 可信度 ✅
- **维度**：开发体验（可发现性）
- **优先级**：P2 · 危害: 低 · 复杂度: 易
- **问题**：`scripts/` 有 32 个 `.cs`（19 个带 `--selftest`），无 README、无索引（`ls scripts/README*` 无结果）。每个脚本头部自述用法与退出码（如 `scripts/gate.cs:1-12`），是有效缓解，但"有哪些脚本、各自职责、如何跑全量"需要逐文件读。本仓 `AGENTS.md` §2 的门禁表只列了 7 道 pre-commit 守卫，未覆盖 32 个脚本。
- **证据**：`ls scripts/` 计数 33 个条目（含 2 个子目录）；无 README。
- **建议**：新增 `scripts/README.md`，按用途分组（提交守卫 / CI 门禁 / 审计工具 / 迁移工具），每项一行职责 + 退出码语义 + 是否有 `--selftest`。注意与 `scripts/gate-audit.cs` 的矩阵口径一致（矩阵五态分类是该文件的权威来源）。
- **风险**：低。维护成本需注意：新增脚本时要同步，否则又是复制型文档。建议只写"分组 + 一句话"，把详细的用法留给脚本头部注释。
- **验证**：索引覆盖全部脚本；`docs/development.md` 链接该索引；新增脚本在 `gate-audit` 矩阵中仍须归入 TOOL 或 UNWIRED-GATE（现有硬约束）。
- **涉及文件**：新增 `scripts/README.md`、`docs/development.md`
- **状态**：⬜ 待处理

### [ ] ITM-714 · 文档计数与注释状态收尾 · 可信度 ✅
- **维度**：文档准确性（机械可验证类）
- **优先级**：P2 · 危害: 低 · 复杂度: 易
- **问题**：六项机械可验证的小错。
  1. `docs/aot.md:3-7` 称 CI 的 aot-verify"仅覆盖 PalOrmSample"，实际已扩为双 sample（`.github/workflows/ci.yml:155-200` 含 `AotSample`）。
  2. `README.md:837` 的 samples 清单只列 4 个，实际 `samples/` 有 5 个（缺 `PalDDD.DapperAotProbe`）。
  3. `Directory.Build.targets:7` 注释称 pre-commit"5 道守卫"，实际 `.githooks/pre-commit` 为 7 道（`AGENTS.md` §2 与 CHANGELOG 2.2.0 均为 7）。
  4. `.github/workflows/ci.yml:84` 注释称 `.ai` 在 `.gitignore` 第 95 行，实际为 `:68`。
  5. `scripts/changelog-check.cs` 在 `docs/release.md:693` 被声明"必须 0 FAIL"，但 CI 与 release workflow 均未调用（grep 零命中），该门禁纯手工。
  6. `docs/performance.md:3` 环境写 `SDK 11.0.100-preview.5`，而 `global.json:3` 已是 rc.1；同文档 `README.md:802` 称 BDN 与 .NET 11 不兼容，实际 `docs/review/bench-baseline-2026-09-13.md` 已有 15 项 RC1 基准。
- **证据**：逐项均已 grep / 实读确认。
- **建议**：逐项修正；第 5 项二选一（在 `release.yml` 调用 `scripts/changelog-check.cs`，或从 `docs/release.md` 移除"必须 0 FAIL"的声明）；第 6 项同时把新基准的可达路径补进 README 或 `docs/performance.md`。
- **风险**：低。第 5 项若选择"接到 release.yml"，需确认该脚本能独立运行且退出码语义明确。
- **验证**：每项与实况一致；`grep -rn "preview.5" docs/performance.md` 零命中或标注为历史时点。
- **涉及文件**：`docs/aot.md:3-7`、`README.md:837,802`、`Directory.Build.targets:7`、`.github/workflows/ci.yml:84`、`docs/release.md:693`、`docs/performance.md:3,30`
- **状态**：⬜ 待处理

### [ ] ITM-715 · EventLog 批量插入 + Projection checkpoint 批量化 · 可信度 ✅
- **维度**：性能（写路径往返）
- **优先级**：P3 · 危害: 中 · 复杂度: 难
- **问题**：两处。① **EventLog 逐事件 INSERT**：`src/PalDDD.Dapper/DapperEventLog.cs:111-138` 每事件一条 `QuerySingleAsync<long>`（还有每事件一个 `new DynamicParameters()` 与 `Payload.ToArray()`/`Metadata.ToArray()` 两次拷贝）；`src/PalDDD.PalORM/Stores/PalOrmEventLog.cs:109-136` 每事件一次 `Session.InsertAsync`。EF 姊妹实现已是批量（`src/PalDDD.EventLog.EFCore/EventLogDbContext.cs:223-233` 用 `AddRange` + 一次 `SaveChangesAsync`）。② **Projection 每事件 2 次 checkpoint 操作**：`src/PalDDD.Projections/ProjectionProcessor.cs:73-79`（`TryStartAsync`）与 `:109`（`MarkCompletedAsync`），replay 驱动 `ProjectionRebuilder.cs:103-115`；重建 100 万事件流会发约 200 万条 checkpoint 语句。
- **证据**：上述行号已实读；`PalOrmEventLog.cs:97` 注释说明批量不可行的原因是每行需返回 GlobalPosition（属设计约束，代价真实）。
- **建议**：评估两条独立路线。EventLog：能否用 `RETURNING` 或多值 INSERT + 窗口函数一次性拿回 GlobalPosition（PG 可行，SQLite 受限）。Projection：增加批量检查点模式（每 N 事件或每 T 毫秒落一次），保留逐事件模式作为可选严格档。
- **风险**：**高**。Projection 的逐事件检查点是有意的 at-least-once 语义选择，改成批量会**扩大重放窗口**（崩溃后回退更多事件），属语义变化而非纯优化。必须先与维护者确认可接受的重复处理上界。EventLog 的 GlobalPosition 分配顺序在批量下更难保证单调。
- **验证**：若实施，需新增语义测试证明"崩溃后回退窗口 ≤ 配置的批量阈值"；基准显示写路径往返次数按批量因子下降。**若风险不可接受，本项应转为"评估不修"并记录理由。**
- **涉及文件**：`src/PalDDD.Dapper/DapperEventLog.cs:111-138`、`src/PalDDD.PalORM/Stores/PalOrmEventLog.cs:109-136`、`src/PalDDD.Projections/ProjectionProcessor.cs:73-109`、`ProjectionRebuilder.cs:103-115`
- **依赖**：ITM-698（同为写/读路径批量化，共用验证手段）
- **状态**：⬜ 待处理

### [ ] ITM-716 · 手动转义与原始 SQL 片段加固 · 可信度 ✅
- **维度**：安全（防御性）
- **优先级**：P2 · 危害: 低 · 复杂度: 易
- **问题**：两处设计脚枪（当前无可利用点）。① **手动引号加倍转义**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlAuditor.cs:112,127`、`PostgreSqlJsonbExtensions.cs:53,64`、`SqliteJsonExtensions.cs:120,140`、`SqliteFtsExtensions.cs:102,154`。对当前方言正确（PG `standard_conforming_strings=on`；SQLite 无反斜杠转义），但复用路径一旦换到 MySQL（反斜杠语义）会静默变可注入。② **原始 SQL 片段 API**：`PostgreSqlSoftDelete.Delete/Restore`（`:36-59`）接受调用方 SQL 片段并逐字插值进 `WHERE (...)`，XML 文档已警告不要拼接用户输入。
- **证据**：上述行号已实读；无 `string.Format`-进-SQL、无 `sql +=` 累积（全仓 grep 干净）。
- **建议**：把手动转义替换为参数绑定（若方言允许），或至少集中为单一 `QuoteLiteral` 助手并加方言断言（防跨方言误用）；原始片段 API 增加参数化重载（接受谓词 + 参数对象），保留原重载但标注为高级用法。
- **风险**：低。参数化改动可能影响 FTS/JSONB 表达式的构造方式（部分场景 SQL 结构本身是动态的），需逐个验证行为不变。
- **验证**：改动后相关测试全绿；新增测试证明含单引号/反斜杠的输入被正确转义或参数化。
- **涉及文件**：上述 PostgreSql / Sqlite 扩展文件
- **状态**：⬜ 待处理

### [ ] ITM-717 · 发布与本地凭据卫生 · 可信度 ✅
- **维度**：安全（凭据面收敛）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：两处。① **job 级长生命周期发布密钥**：`.github/workflows/release.yml:28` 把 `NUGET_API_KEY` 注入 job 级 env，使 restore/build/test/运行已发布二进制等步骤都带着一个可发布任意版本的组织级凭据；无 `id-token: write`，未采用 NuGet 的 OIDC 可信发布。② **本地真实形态凭据文件**：`appsettings.test.local.json`（未跟踪，`.gitignore:94` 覆盖，`git check-ignore -v` 确认）含内网主机与真实形态口令；当前卫生正确，但距泄漏只有一个 `.gitignore` 编辑，而 `secret-scan` 按设计不看未跟踪文件。
- **证据**：`release.yml:28` 的 env 块已读；`permissions` 为 `contents: write`（无 `id-token`）；`git check-ignore -v appsettings.test.local.json` 确认忽略。
- **建议**：① 评估迁移到 NuGet OIDC 可信发布（`id-token: write` + `NuGet/login`），消除静态密钥；若暂不迁移，至少把 `NUGET_API_KEY` 从 job 级收窄到仅发布步骤（注：`release.yml` 注释说明"step-level env does not cross steps"是既有的踩坑记录，收窄需按步骤边界显式注入）；② 本地文件保持忽略状态，并在 `docs/development.md` 写明该文件的用途与"绝不 `git add -f`"的要求。
- **风险**：低到中。OIDC 迁移需要 NuGet.org 侧配置可信发布策略，属外部依赖；收窄 env 作用域需确认所有实际用到的步骤都被覆盖（否则重演"密钥为空"的旧坑）。
- **验证**：迁移后发布流程端到端成功且 workflow 中无静态密钥引用；若仅收窄作用域，`grep -n "NUGET_API_KEY" release.yml` 的命中范围仅限发布步骤。
- **涉及文件**：`.github/workflows/release.yml:20-30`、`docs/development.md`
- **状态**：⬜ 待处理

### [ ] ITM-718 · 公共 API 语义澄清（`SqlTemplates`） · 可信度 ✅
- **维度**：可读性 / API 契约
- **优先级**：P2 · 危害: 低 · 复杂度: 易
- **问题**：`src/PalDDD.Dapper/SqlTemplates.cs` 暴露 31 个 `public const` SQL 字符串，其中 `:115-119`（`OutboxLeaseUpdate`）与 `:164-168`（`OutboxSelectById`）的注解自述"当前无内部引用"。
- **证据**：上述行号已实读。
- **建议**：按本仓"代码价值判定"规则（框架库 API 面向外部使用者，早期无内部消费方是常态），**不建议删除**。改为面向外部使用者的契约说明（该 SQL 的用途、方言差异、参数契约），去掉"无内部引用"这类面向维护者的自述。
- **风险**：低。
- **验证**：注释不再含"无内部引用"式表述；公共 API 快照无变化（本项不改签名）。
- **涉及文件**：`src/PalDDD.Dapper/SqlTemplates.cs:115-119,164-168`
- **状态**：⬜ 待处理

---

## 前三个任务的实现草图

### 草图 1：ITM-685 毒消息死信路径测试

**方法**：不改生产代码，只补测试。用 `InMemoryOutboxStore` + 可控 `IMessageSerializer` 桩构造分支，不依赖真实数据库。

**关键步骤**：

1. 先读 `OutboxBatchProcessor` 构造签名，确认如何注入 `MessageCatalog`、`IMessageSerializer`、`IPalOutboxStore`、`TimeProvider`、`ILogger`。优先复用 `test/PalDDD.Testing/` 的现成原语（`FakeTimeProvider`、`RecordingMeterListener`）。
2. **分支 A（类型未注册）**：构造 catalog 不含该 `Type` 的消息，入 store 为 Pending，跑 `ProcessBatchAsync`，断言消息 `Status == Dead`、`Error` 含 `not registered`、且持久化被调用（用 spy store 计数）。
3. **分支 B（反序列化 null）**：catalog 含该类型但注入返回 `null` 的序列化器桩，断言同上、`Error` 含 `Deserialization returned null`。
4. **分支 C（`MarkDead` 自身抛异常）**：用会抛的 store 桩，断言**不抛出**、`ex.Data["MarkError"]` 被设置、批次继续处理后续消息（2 条中 1 条成功）。这一条最关键——它验证"标记失败不得中止整批"的设计承诺。
5. **分支 D（backoff 抛异常）**：注入会抛的退避策略，断言走 1s 兜底而非崩批次。
6. **变异验证**：临时把 `:101` 的 `if (descriptor is null)` 改为 `if (false)` → 跑测试确认变红 → 恢复。对 `:111` 的 `@event is null` 同理。

**陷阱**：
- `ProcessBatchAsync` 用 `checked { dead++; }`。若只断言计数值，一个"计数对但状态错"的回归会漏过。**断言消息状态与 store 调用，不要只断言计数。**
- `test/PalDDD.Transactions.Tests/TransactionsTests.cs` 中依赖 Meter 广播的测试标了 `[NotInParallel]`。新增测试若断言指标需照做，否则并行污染会让计数不稳。
- `MarkDead` 在 store 层是同步签名，桩实现要与之匹配。

---

### 草图 2：ITM-689 Dapper AOT 事实三方统一

**方法**：以 `docs/persistence-aot-status.md` 为唯一权威，其余四处改为引用它。**不要重新裁决事实**，事实已由三方言 13/13 实测确定。

**关键步骤**：

1. 先读 `docs/persistence-aot-status.md` 全文，确认权威表述与边界。特别确认这条消费者必读边界不能丢：**只在六接口封装 API 面内的 34 个调用点有效；绕过封装直用 Dapper 原生 API 在 NativeAOT 下不受支持**。
2. 改 `src/PalDDD.Dapper/PalDDD.Dapper.csproj:7` 的 `<Description>`：删掉"⚠️ AOT 假象：[module:DapperAot] 未启用，走经典 Dapper 反射路径"，改为"调用点级 NativeAOT（`[module:DapperAot]` 已启用，34 调用点拦截器接管，三方言实测 13/13）；边界：绕过封装直用 Dapper 原生 API 不受 AOT 支持"。这段是 nuget.org 对外文案，边界必须保留。
3. 改 `README.md:790` 的表格行：从 `⚠️ 假象` 改为与 `:770` 一致的"✅ 调用点级 AOT（实测）"，并从 `⚠️` 语义中移出。
4. 改 `README.en.md:791` 英文同款。
5. 改 `src/PalDDD.Dapper/DapperAotInitializer.cs:16-19` 的文件头：现在的文字描述"铺垫不动"时的未启用状态，而 `:33` 已启用。改写为"当前状态：已启用（2026-09-13 实验合并）；以下清单保留为回滚参考"，或删掉"启用动作清单"中已完成的第 ① 项。

**陷阱**：
- **必须一并 grep 而不是只改已知四处**：`docs/aot.md` 可能也含旧口径。改前跑 `grep -rn "AOT 假象\|未启用" src/PalDDD.Dapper/ README.md README.en.md docs/aot.md docs/persistence-aot-status.md`。
- `Directory.Build.props` 的 `NoWarn` 注释里出现过"AOT 假象"式措辞的语境——改前确认那是抑制理由而非事实陈述，不要误改。
- 这是"三方一致"红线的修复，**必须同一个提交**完成代码注释 + 文档 + csproj 描述，不可分批。

---

### 草图 3：ITM-698 EF Core outbox 查询与租约批量化

**方法**：分两步，且第二步需先裁决。**保持不可协商的语义：两实例并发时同一消息不得被重复租约。**

**关键步骤**：

1. **先建安全网**（若 ITM-685 未覆盖）：两实例同时 `LeasePendingMessagesAsync` 同一批，断言租约集合不相交。可参照 `test/PalDDD.Integration.Tests/OutboxSqliteConcurrencyTests.cs:104-166` 与 `test/PalDDD.PalORM.Tests/PalOrmConcurrencyTests.cs`（10 worker / 100 消息 / 租约唯一性）。
2. **步骤 1，消除整表分页**：把时间过滤下推到 SQL，去掉 `:51-69` 的 `while (result.Count < batchSize)` + `Skip/Take` 循环。根因是 `DateTimeOffset` 在 EF SQLite 不可翻译，可行方向是存可比排序的列（ISO8601 文本或 `long` 时间戳）并在 SQL 层比较。
3. **步骤 2，租约批量化**（需裁决后才做）：把逐行 `ExecuteUpdateAsync` 改为按批 CAS。**核心难点**：CAS 的版本守卫按行不同（每行 `LockedUntil` 原值可能不同），**不能**用单一 `LockedUntil` 等值条件覆盖整批。两个方向：
   - 按 `LockedUntil` 值分组后分组更新。实践上同批多为 null 或同一过期时刻，分组数很小，代价可接受。
   - 引入单调递增的 `version`/`RowVersion` 列，使批内可用统一谓词。**属 schema 变更。**
4. **验证**：跑 ITM-688 的基准对比 `Outbox_Lease_Batch100` 前后值（目标：进入 Dapper 的 2 倍以内，当前 5.04）；跑全部并发测试确认无重复租约。

**陷阱**：
- 这项改动触及并发正确性，而本仓已有 40+ 轮针对该契约（lease token / `retry_count` fencing / status guard）的修复史。**如果步骤 2 必须改 schema，立即停止**，把本任务降级为"只做索引（ITM-693）与查询下推（步骤 1）"，批量化推迟到 v3.0 破坏性窗口（该窗口已排队 `IPalOutboxStore` 异步化与跨栈 fencing 契约统一，见 `docs/review/open-items-2026-09-14.md` B2）。
- EF Core 栈在本仓被声明为非 AOT（`docs/persistence-aot-status.md` 一页总表），不要为性能改动而破坏其与 PG 路径的行为等价性。
- 不要为了性能回退逐条 CAS 的正确性由来——原始逐条设计正是因为"SELECT 跟踪 → 内存改 → SaveChanges"三步分离会让两实例同时租约同一批。

---

## 需裁决项（评估类，产出 ADR 结论即可，不需立即改代码）

| ID | 事项 | 现状 | 需要你决定什么 |
|---|---|---|---|
| ITM-719 | `PalDDD.Transactions` 拆包 / AOT 标志对齐 | csproj `:9-11` 因 Saga 反射把整个程序集设为非 AOT，而 Outbox/Inbox 零反射（`PeriodicBackgroundProcessor.cs:9` 自述）；`samples/PalDDD.AotSample/PalDDD.AotSample.csproj:8-9` 声明 `IsAotCompatible=true` 却引用它。`Saga.cs` 的反射方法**已带正确 `RequiresUnreferencedCode`/`RequiresDynamicCode` 标注**，故程序集级翻 false 更多是保守策略而非技术必需 | 是否存在"只用 Outbox/Inbox 且需要 AOT"的真实用户？若有，拆出 `PalDDD.Sagas`（破坏性，属 major 窗口）或让标志与子系统对齐（需先确认 ILC 接受程序集内部分方法带 `RequiresDynamicCode` 而标志为 true）。若没有，本项降级为文档说明 |
| ITM-720 | 驱动层设施从 Dapper 包抽出 | `PostgreSqlMultiHost.cs`（625 行，`using` 只有 M.E.DI + Npgsql）、`PostgreSqlSharding.cs`、`PostgreSqlReadWriteRouter.cs`、`PostgreSqlAuditor.cs`、`PostgreSqlSoftDelete.cs`、`MySqlMultiHost.cs`、`SqliteFtsExtensions.cs` 均为驱动级能力，却只对 Dapper 用户可用 | 是否抽出 `PalDDD.Providers` 让三栈共享？这是新增公共面，需权衡"EFCore/PalORM 用户的实际需求"vs"多一个包的维护成本" |
| ITM-721 | Dapper 核心三驱动耦合 | `PalDDD.Dapper.csproj:28-30` 在核心程序集引用 Npgsql + MySqlConnector + Microsoft.Data.Sqlite，方言靠运行时枚举分派；PalORM 已做对（核心零驱动引用，provider 独立包） | 是否重构为按驱动分包？属破坏性变更（包边界变动），且与 ITM-719 的窗口相关 |
| ITM-722 | Inbox 与 Idempotency 引擎合并 | `Inbox/InboxStore.cs:19` 与 `Idempotency/IIdempotencyStore.cs:10` 语义逐行对应，处理器镜像（`InboxProcessor.cs:150-153` vs `IdempotencyProcessor.cs:96-99`），每栈实现两份 | 保留两个公开 API 合理，但**实现**能否共享一个内部引擎（键元组差异：`(consumer,messageId)` vs `(operation,key)`+缓存响应）？ |
| ITM-723 | 性能契约目标定义 | 当前无量化性能契约；`docs/performance.md` 只有 4 行烟测，新基准是 15 项一次性数值 | outbox 吞吐目标（msg/s）？轮询延迟上限？**outbox 表预期留存规模**（直接决定 ITM-698 与 ITM-693 的严重度：1 万行与 100 万行差三个数量级） |
| ITM-724 | RC 依赖 GA 清偿计划 | `global.json:3` 钉 RC SDK；`Directory.Build.props:45` 全局抑制 `NU1900-1904` + `NU5104`。补偿控制真实存在（CI 跑 `scripts/vuln-scan.cs`，`auditMode: all`、`auditLevel: low`） | .NET 11 GA 后如何清偿：哪些抑制可移除？`Microsoft.NETCore.Platforms 8.0.0-preview.7` 是否随 GA 清理？与 ITM-702（锁文件）、ITM-711（许可证）是否合并为一个 GA 窗口批次？ |
| ITM-725 | 文档归档策略 | 33 份评审 + 14,826 行文档；同一事实在 5 处复制；`docs/review/` 已被 `review-scope.cs:98` 排除在审查范围外 | 是否做一次归档整理（历史评审移出 `docs/`，只留权威文档）？还是维持现状靠 ITM-699 的单点化缓解？前者是较大的文档重构，需确认历史评审的留存价值 |

---

## 外部任务合并检查清单（本清单适用性）

按 `docs/review/ACTION_ITEMS_TEMPLATE.md` 的要求，外部任务合并前须逐项 grep 验证方法名/类名/文件路径/配置属性。本清单的来源是本次只读审计而非外部投喂，所有条目已在来源报告中逐处实读验证，标注如下：

- [x] 方法名 → 已 grep/实读（如 `MarkDead`、`ExecuteUpdateAsync`、`TryStartProcessingAsync`、`ResolveChildSagaByType`、`CreateChildState`）
- [x] 类名 → 已 grep/实读（如 `NullMessageBroker`、`MessageBrokerBase`、`SqlErrorClassifier`、`DapperAmbientTransaction`）
- [x] 文件路径 → 已 `ls`/Read 验证存在
- [x] 配置属性 → 已 grep 验证（如 `IsAotCompatible`、`RestorePackagesWithLockFile`、`PackageLicenseExpression`、`NUGET_API_KEY`）
- [x] 例外登记：**ITM-704 标 ⚠**（静态阅读 nuspec 得出，未做端到端验证）；**ITM-712 标 ⚠**（注释价值属判断而非事实）。两项的具体验证前置已写入各自条目。

---

## 修复后检查清单（每项任务提交前）

- [ ] 修改是否引入新的编译期依赖？（如新增 `using` 或项目引用）→ 检查 `.csproj` diff
- [ ] 修改是否改变现有行为的语义？→ 检查受影响路径
- [ ] 高危害修复是否有测试覆盖？（资源/安全/并发类必须有）
- [ ] 是否同步了文档与注释？（本仓 #1 三方一致红线：公共 API / 签名 / 行为 / 计数 / 配置 / 命令变更须同提交同步）
- [ ] 是否做了变异验证？（新增或修改门禁/测试后，确认它能**红**）
- [ ] 新建或修改的文件行尾为 CRLF？（`dotnet run scripts/encoding-gate.cs` 的 E5 判定）
- [ ] `dotnet run scripts/verify-conventions.cs --quick` 全绿（`.md` 入暂存集时）
- [ ] `dotnet run scripts/gate-audit.cs` 矩阵无新增 `REVIEW` 桶条目（新增脚本须归入 TOOL 或 UNWIRED-GATE）
- [ ] 若涉及跨文件重构：已用 Serena `find_referencing_symbols` 确认引用点，未仅靠 grep 文本匹配
- [ ] 若涉及迁移/DDL：已在迁移文档写明既有部署需执行的动作
