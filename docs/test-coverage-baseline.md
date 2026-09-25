# 测试覆盖率基线

> **基线日期**：2026-07-30
> **基线 commit**：`db47e22`（feature/palorm-adapter）
> **测试总数**：850（全绿）——**基线时点值**
> **当前规模**（2026-09-25，本机全量实测）：16 项目 1502 用例（1434 通过 + 68 跳过——60 项 Docker/Testcontainers 依赖：PalORM 多方言 46 项 + Integration 14 项，由 CI Testcontainers 执行；另 8 项 Messaging.Integration 的 RabbitMQ broker 本机 AMQP 预检不可达。较 1492 的净增 +10 来自两个竞态测试的 `[Repeat(5)]`——TUnit Repeat 语义为"额外再跑 5 次"共 6 实例）；本文余下覆盖率数字均为 2026-07-30 基线，未随测试增长重测（§基线重录记录的单模块重录值除外）
> **覆盖率工具**：dotnet-coverage 18.9.0 + ReportGenerator 5.5.11

## 总览

| 指标 | 值 |
|------|:--:|
| 行覆盖率 | **67.9%**（5895 / 8672） |
| 方法覆盖率 | 68%（749 / 1101） |
| 完全覆盖方法 | 58.2%（641 / 1101） |

## 按模块覆盖率

| 覆盖率 | 模块 |
|:--:|------|
| 90%+ | Projections 98.6% · MemoryPack 100% · EventLog 92.3% · Idempotency 93.7% · Serialization 94.6% · PalORM.Core 89.6% |
| 80-90% | Analyzers 87% · CQRS 87.3% · Core 86% · Messaging 86.5% · RabbitMQ 89.6% · Kafka 81.2% · Idempotency.EFCore 91.1% |
| 60-80% | Dapper 75.5% · DI 70% · Repository.EFCore 77.4% · EventLog.EFCore 78.8% · Projections.EFCore 88% |
| 40-60% | Transactions 59.1% · Transactions.EFCore 51.5% · Projections.EventLog 41.8% · Core.SourceGen 42.4% |
| < 30% | Hosting.AspNetCore 30% · Dapper.PostgreSql 11.2% · PalORM.MySql 4% · PalORM.PostgreSql 4% |

## 门禁阈值

- **全局行覆盖率不低于 70%**（阈值口径：2026-09-14 实测基线 72.98% − 3% 缓冲，
  取整 0.70；沿用项目原始设计原则「基线 − 3pp」）。
  实现脚本为 **`scripts/ci-coverage.cs`**（2026-09-11 由 `ci-coverage.sh` 迁移为
  C# file-based app）：build → 逐项目 `test --coverage` → ReportGenerator 合并 →
  解析合并 Cobertura 的 `line-rate` → 低于阈值退出 1（fail-closed）→ **单模块降幅
  门禁（见下）**。阈值可经 `COVERAGE_THRESHOLD` 环境变量覆盖；脚本自带 `--selftest`
  （18 例，含全局阈值路由与降幅判定）。

- **单模块覆盖率降幅 ≤5pp**（2026-09-14 增，账本 §四「规则 6」落地）：
  数据源 `coverage-baseline.json`（15 项目 line-rate 基线，由 `ci-coverage.cs --
  --update-baseline` 从当前产物生成——基线更新属校准步骤，需评审后提交）。
  判定：同项目「当前 line-rate vs 基线」降幅 > 5pp（绝对百分点，epsilon 1e-9 吸收
  浮点噪声；等于容差放行）→ 退出 1。
  口径边界：**同项目跨时间可比**（同一测试集插桩同一装配集）；**项目间不可比**
  （各次插桩 lines-valid 从 1578 到 10371 不等——不能横向比较各项目 line-rate）。
  **低值不是缺口（2026-09-22 T-19 加注）**：基线下端的极端值均由"度量单位"而非覆盖缺口解释，
  不应据此设模块阈值（对不可比的量设阈值会诱导为达标而写的测试）：
  - `PalDDD.Core.Abstractions.Tests` **1.32%**：其 src 项目**已不存在**（`PalDDD.Abstractions`
    早先拆分归还各层，见该测试项目头注）；该测试项目只做低层类型契约（ContentTypes 常量 /
    IUnitOfWork 扩展方法，8 个测试），却引用 `PalDDD.Core` + `PalDDD.Serialization` +
    `PalDDD.Transactions` 三个大装配集 ⇒ 插桩分母大、分子极小，1.32% 是必然结果。
  - `PalDDD.Repository.EFCore.Tests` 8.1% / `DependencyInjection.Tests` 15.11% /
    `Messaging.Tests` 21.79% 同理：测试面窄而装配集大。
  - 真正的覆盖信号是**同项目跨时间的降幅**（Step 6 的 5pp 棘轮），不是跨项目的绝对值。
  基线缺失/解析空 → fail-closed；基线有、当前无产物 → WARN 不阻断（CI 全项目有产物）。

  **状态（2026-09-14 已接线）**：`ci.yml` 新增独立 **`coverage` job**（与
  `aot-verify`/`dialect-probe` 同构，并行、失败域隔离；不放 `build-and-test` 内是
  因为覆盖率需完整再跑一遍测试，放进主 job 会把关键路径拉长近一倍）。job 内先
  `dotnet tool restore`（`reportgenerator` 由 `.config/dotnet-tools.json` 钉 5.5.11
  ——该工具此前 CI 从未还原过，是接线的隐藏前置）。

  **阈值校准依据（2026-09-14 实测）**：用 `dotnet test <proj> --coverage
  --coverage-output-format cobertura` 逐项目产出 16 份 cobertura（含 4 个此前从未
  跑到的项目：Projections.EventLog / Repository.EFCore / Serialization / Transactions
  ——经查它们均不依赖 Testcontainers，故本机可跑），然后按 **(文件, 行号) 取并集**
  计算（等价 ReportGenerator 的合并语义，不能简单相加各文件 line-rate——各次插桩的
  `lines-valid` 从 1578 到 10371 不等，只覆盖该测试项目加载到的装配集）：

  | 口径 | 值 |
  |------|-----|
  | 16/16 项目并集 | **72.98%**（13146 / 18013 行） |
  | 12/16 项目并集（未补 4 项时） | 66.85%（11881 / 17773） |
  | 2026-07-30 旧基线 | 67.9% |
  | 旧阈值 0.65 | 余量约 8pp → 失去早期预警意义 |

  该 72.98% 是**下界**：其中 `PalDDD.PalORM.Tests` 因本机无 Docker（Docker 未安装，
  非未启动）有 46 项多方言测试未跑（3.1.0 起 `MultiDialectFixture` 对未启用
  Testcontainers 的情况 `Skip.Unless` 跳过而非抛异常，见 T-17 裁决），CI 上这 46 项会跑，
  数字会更高。

  **⚠️ 复校准触发**：首次 `coverage` job 运行会给出含 Docker 的完整值（预期 ≥ 0.73）。
  届时按实测值重校准阈值并同步本表——不要在未读 CI 数字前继续上调。

  **本机限制（已知，非缺陷）**：无 Docker 的机器上 `dotnet run scripts/ci-coverage.cs`
  会在 `PalDDD.PalORM.Tests` 处少 46 项多方言覆盖（该项目的多方言测试要求 Testcontainers，
  未启用时整体跳过、不产出插桩数据）。本地想要完整数字：装 Docker 后重跑即可（Testcontainers 自动启动容器）；
  或按上表的分步方法只补跑缺失项目再取并集。
  **注意**：设 `PALDDD_TEST_PG` / `PALDDD_TEST_MYSQL` 环境变量**不会**启用外部库路径——
  那两个变量只被 `samples/PalDDD.DapperAotProbe` 这类显式探针读取；测试侧仅支持
  Testcontainers（2026-09-22 T-09 删除外部库隐式回退，见 `appsettings.test.json` 注释）。

- **基线值来源与复校准**：`coverage-baseline.json` 的 15 个值取自 2026-09-14 本机
  Debug 插桩产物，属**下界**——`PalDDD.PalORM.Tests` 因本机无 Docker 多方言测试跳过、
  无 cobertura 产物、**未纳入基线**（`UpdateBaseline` 只为有产物的项目写键），其降幅当前不受
  Step 6 检查。首次 `coverage` job 运行后应用 CI 完整产物重取基线
  （`-- --update-baseline`，按上文「基线更新属校准步骤，需评审后提交」），
  届时 PalORM 随产物齐全一并纳入。
  **例外（2026-09-25）**：`PalDDD.Integration.Tests` 一项已按 CI 实测重录，非 2026-09-14 本机产物，
  见下 §基线重录记录。

> 本文上文「按模块覆盖率」分档表与下文「覆盖率低的已知原因」表均为 **2026-07-30 历史
> 视角**——其中的百分比已与实际脱节（例如 `Core.SourceGen` 当时记 42.4%，2026-09-14
> 实测 91.7%），保留用于对照当时的判断，**不作为门禁输入**；门禁输入只有
> `coverage-baseline.json`（逐项目降幅）与合并 Cobertura 的全局 line-rate（全局阈值）。

## 基线重录记录

> 沿上文「基线更新属校准步骤，需评审后提交」：本节逐条记录 `coverage-baseline.json` 的重录，
> 只登记事实与依据，不改写上文的历史数字。旧值与当前值不可比（违反「同一测试集插桩同一装配集」）
> 才构成重录理由；可比且在 5pp 容差内的不重录。

| 重录日期 | 对象 | 旧值 → 新值 | 来源 | 理由 |
|:--:|------|:--:|------|------|
| 2026-09-25 | `PalDDD.Integration.Tests` | 0.5284 → **0.3815** | CI run `36128788289` coverage job（2026-09-25，379 项 0 失败 0 跳过） | 快照宿主迁入改变插桩装配集，旧基线与当前不可比（下详） |

### 2026-09-25 `PalDDD.Integration.Tests`：快照宿主迁入致插桩装配集变化

- **重录理由**：提交 `6f8270b` 把 `PublicApiSnapshotTests.cs`（652 行金标快照）自
  `PalDDD.Core.Tests` 迁入本测试项目（快照扩展需引用数据面适配层，而 Core.Tests 作 Domain
  测试被 `DomainTests_DoNotReferenceInfrastructureImplementations` 拦截）。csproj 随迁一次新增
  **17 个直接 `ProjectReference`**（csproj +20 行 = 17 行引用 + 3 行说明注释）：CQRS、
  DependencyInjection、EventLog、EventLog.EFCore、Messaging、Messaging.Kafka、
  Messaging.RabbitMQ、Projections.EventLog、Compression、Compression.Native、
  Serialization.MemoryPack、Hosting.AspNetCore、PalORM 及其三方言、Repository.EFCore。
  其中 **15 个装配集此前未被本测试项目插桩**（EventLog、Messaging 因 Dapper→EventLog、
  Transactions→Messaging 早已传递在册），故插桩装配集由 **16 → 31**——31 为当前 Cobertura
  实测装配集数；16 为改动前直接引用 14 项的传递闭包（传递插桩行为由 `PalDDD.CQRS.Tests` 只
  直接引用 CQRS 却含 Core 行实证），并经分母对账印证。单项目 `lines-valid`
  **10463 → 16080（+53.7%）**。
  「同一测试集插桩同一装配集」的前提已不成立 ⇒ 旧值与当前值不可比，按降幅判红是误报，故重录。

- **降幅分摊**（本机前后两轮同口径实测，按装配集聚合 Cobertura；本机合计 17.46pp，
  扣除起点/终点口径差后对账到 CI 的 14.69pp）：

  | 分项 | 本机口径贡献 | 依据 |
  |------|:--:|------|
  | 插桩装配集变大 | **16.27pp（93.2%）** | 新增 15 个装配集 5468 行进分母（占增幅 5617 行的 97%），仅 265 行被覆盖 |
  | `6f8270b` 新增源码未覆盖 | **0.43pp（2.5%）** | 该提交 `src/` 净增 184 行（RabbitMQ/PalORM 七文件），全部落在新增装配集内、0 覆盖，占新分母 1.14% |
  | 既有 16 装配集内变化 | 0.76pp（4.3%） | 净增约 149 行（`src/` 净 +32，其余为生成代码随工具版本漂移[推断]），覆盖 5724 → 5725 |
  | 本机前后两轮合计 | 17.46pp | 54.71% → 37.25%（2026-09-23 与 2026-09-25 各一轮） |
  | − 旧基线起点差 | −1.87pp | 旧值 0.5284 低于改动前本机实测 0.5471 |
  | − CI 终点差 | −0.90pp | CI 跑了本机无 Docker 跳过的 14 项集成测试，故 0.3815 高于本机 0.3725 |
  | = CI 报告降幅 | **14.69pp** | 52.84% → 38.15% |

  结论：**约九成降幅由插桩装配集变大解释，新增源码本身只占约 2.5%**——新增引用把一批
  从未被本测试项目插桩的既有代码拉进分母，其覆盖本就由各自的测试项目承担；余量为基线起点
  与测试环境（Docker）口径差。

- **待办（新代码补测后基线应上调）**：新入的 15 个装配集当前仅 `PalDDD.PalORM` 有 15.72% 覆盖，
  其余 14 个为 0%（CQRS、Compression、Compression.Native、DependencyInjection、EventLog.EFCore、
  Hosting.AspNetCore、Messaging.Kafka、Messaging.RabbitMQ、PalORM 三方言、Projections.EventLog、
  Repository.EFCore、Serialization.MemoryPack）——本测试项目只把它们拉进插桩范围，未执行其代码
  路径。为这批装配集补测后应重录并**上调 0.3815**（上调不受 5pp 降幅门禁限制）。

## 覆盖率低的已知原因（非缺陷）

| 模块 | 低覆盖率原因 | 是否需补测试 |
|------|------------|:--:|
| Core.SourceGen (42.4%) | 源生成器编译时执行，运行时覆盖率工具测不到 | ✅ P1 Verify 快照 |
| 各模块 ServiceCollectionExtensions (0%) | DI 胶水代码，架构边界测试已覆盖注册正确性 | ❌ 不需要 |
| PalORM 方言包 (4-16%) | 跨方言测试走泛型基类，不命中具体方言固化类 | ⚠️ P3 评估 |
| Dapper.PostgreSql (11.2%) | 分片/路由/JSON 类为高级特性，尚未投入使用 | ⚠️ 待定 |
| Testing (53.4%) | 测试基础设施自己测自己意义有限 | ❌ 不需要 |

## 真正需要补测试的运行时热点

| 优先级 | 模块 | 类 | 当前覆盖率 |
|:--:|------|------|:--:|
| P1 | Core.SourceGen | EnumGenerator / IdentityGenerator | 0% |
| P2 | Transactions | DefaultSagaManager / ChildSagaStep / DynamicStep / InterruptStep | 0% |
| P2 | Projections.EventLog | EventStreamJsonLines | 0% |
| P3 | Hosting.AspNetCore | EndpointExtensions | 3.2% |
