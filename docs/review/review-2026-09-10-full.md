# Pal.DDD 评审报告 — 全仓 AI 质量系统运行

> 报告编号：REVIEW-2026-09-10
> 评审基准：commit `d8746a6`（dev 分支）· **全量/全仓档**（`review-scope.sh --all`）
> 评审系统：`.ai/review/prompt.md` v1.0（七流 + 三轴 + 误判库 PD1-PD37）
> 行动项清单：[`action-items-2026-09-10-full.md`](action-items-2026-09-10-full.md)（ITM-615 起）
> 执行方式：`.ai` 四系统全量实跑（**无缓存，全部实读**）+ 8 片并行子代理地毯式逐行（642 文件 / 107801 行）+ 跨片 6 轴全局不变式核对 + 主线程探针实证

---

## 执行摘要

**评审结论**：机械防线与测试基线**全绿**（gate 24/24 · verify-ai 22/22 · tech-debt 0 失败 · doc-consistency 11/11 · encoding 4/4 · template PASS · Release 0 警告 0 错误 · 无外部依赖测试 13 项目 841 用例全绿），但地毯式逐行 + 跨片核对暴露出 **4 项 P1 与 26 项 P2**。核心问题是**结构性的**：机械防线全绿恰恰掩盖了"机器构不着"的缺陷——发布面/文档失实（PD28）、门禁用 `git diff --cached` 在 CI 恒 no-op、凭据防线声明失实、生成物零接线、测试名实不符。**没有 P0**（无实际数据损失/安全漏洞/运行时崩溃已发生）。

| 指标 | 本轮 | 上轮 | 趋势 |
|------|:--:|:--:|:----:|
| P0 | 0 | 0 | → |
| P1 | 4 | — | ↑ |
| P2 / P3 | 26 / 34 | — | ↑ |
| 逃逸（累计）/ 复发 | — / — | — | 见 metrics.md |
| 证伪数（误报治理） | 24 | — | ↑（误判库生效） |
| P0/P1 修复时延 | 待修复 | — | — |

**一句话**：框架代码质量高（213 个手写源文件逐行读完仅 10 条 P2，且多为已声明边界），但**质量系统自身的"验证能力"存在系统性缺口**——4 项 P1 全部落在"声称有防线、实际无/假绿"这一类。

### 与上轮（第五轮实跑审计 `d8746a6`）对比

| 上轮发现 | 上轮 | 本轮 | 状态 |
|----------|:---:|:---:|:----:|
| 诊断覆盖门禁判据（Roslyn 语法树） | P2 | — | ✅ 上轮已修，本轮 V12/V7 复核通过 |
| PALENUM002 正向守护缺口 | P2 | — | ✅ 上轮已清偿，本轮 DiagnosticCoverageGate 复核通过 |
| — | — | P1×4 | 🔴 新发现（发布面/门禁面，此前评审聚焦代码层未覆盖） |
| — | — | P2×26 | 🟠 新发现（含测/文档面） |

> 本轮首次把**全仓档（src+test+docs+scripts+config 八片）**与**跨片全局不变式**同时纳入，故新增发现集中在 src 之外的发布/门禁/测试面——这正是 `.ai/review/engine.md`「全仓档」设计要抓的盲区（PD28：src/test 地毯看不到发布面失实）。

---

## 第一部分：评审基础

### 1.1 覆盖度账本

```
应读清单：642 文件 / 107801 行（review-scope.sh --all，含 .ai 独立仓 tracked 文件）
排除项：.g.cs 生成物 + docs/review/ 过程产物 + .ai/brain-data/

逐行覆盖（子代理段）：
  src  生产代码     213 文件 / 31611 行  → 7 片，30-31 文件/片，**全部逐行读完**
  test 测试代码     104 文件 / 27460 行  → 5 片，20-21 文件/片，**全部逐行读完**
  docs/scripts/config/模板  78 文件      → 1 片，**全部逐行读完**
  跨片全局不变式   AOT分层/消息键集/依赖方向/多实现契约/双管线/诊断分层 6 轴

七流覆盖度:
  架构流 ✅  安全流 ✅  资源流 ✅  并发流 ✅  错误流 ✅  AOT流 ✅  生成语义流 ✅
  （七流均在 src+test 双面覆盖；安全流另加负向声明验证）

三轴状态:
  机械轴: gate 24/24 · verify-ai-system 22/22 · tech-debt 19P/2A/1W/0F · doc-consistency 11/11
          · encoding 4/4 · template-gate PASS · test-gate 0F/1W · assertion 166/173 · 构建 0W/0E  ✅
  静态轴: 地毯轮新发现 P0=0 / P1=4 / P2=26（发布/门禁/测试面为主）                          ⚠️ 有发现
  实测轴: dialect-probe **SKIP**（PG TCP 可达但 Npgsql 握手超时，MySQL 同理）· 无外部依赖测试 841/841 全绿
```

### 1.2 评审基线

```
基线 commit : d8746a6（dev）· 工作树清洁（git status --short 空）
构建        : dotnet build PalDDD.slnx -c Release → 0 警告 0 错误（11.14s）
SDK         : 11.0.100-rc.1.26425.128
无外部依赖测试（逐项目 MTP）:
  Core 296 · Core.Abstractions 8 · CQRS 29 · Serialization 51 · Compression 31 · Messaging 28
  · EventLog 34 · Transactions 174 · Analyzers 47 · DependencyInjection 97 · Repository.EFCore 12
  · Projections.EventLog 9 · Hosting.AspNetCore 25  = 841 用例，0 失败
机械防线实跑（全部实读输出，非引用历史）:
  gate-check        24 PASS / 0 WARN / 0 FAIL
  verify-ai-system  22 PASS / 0 FAIL
  tech-debt-scan    19 PASS / 2 ALLOW / 1 WARN / 0 FAIL（[Obsolete] 6 处为计划内）
  doc-consistency   11 PASS / 0 FAIL
  encoding-gate     E1-E4 全 PASS
  assertion-strength 弱断言 166（基线上限 173）· 零断言测试 22 个
  template-gate     PASS（9 模板 / 13 编译块）
  test-gate         0 失败 / 1 警告（T6 6 处 DROP TABLE 需人工审查）
  flaky-gate        需传 csproj（本轮未对单项目跑重跑式检测）
```

### 1.3 范围声明

- **必须检查**：`git ls-files` 全集 + `.ai` 独立仓 tracked 文件（除排除项）；七流视角 + 三评审轴 + 6 项跨片全局不变式。
- **明确不检查**：`*.g.cs` 生成物（PD1，仅验语义不评风格）；`docs/review/` 与 `.ai/review/history/`（历史过程产物，不可再生）；`.ai/brain-data/`（运行时记忆）；第三方 NuGet 包内部实现（仅核文档/程序集字符串）。
- **抽样策略**：`docs/design/palorm-architecture.md`（2024 行）仅读标题结构 + 关键节，其内部事实性声明未逐条对码（标 ❓）；`docs/design/v8-orm-*` 为 ORM 设计参考，仅扫读。抽样区结论均标 ⚠/❓。

---

## 第二部分：发现与证据

### 2.1 危害 × 复杂度分布

```
        高危害     中危害     低危害
易修复   [P0:0]     [P1:4]     [P2:8]
中等     [P1:0]     [P2:10]    [P3:12]
难修复   [P2:1]     [P3:3]     评估:1
         （+P2 其余 7 项跨格，+P3 汇总 34）
```

### 2.2 发现清单

#### 🔴 P0 — 无

本轮未发现 P0（无已发生的数据损失/安全漏洞/运行时崩溃/编译失败）。

#### 🟠 P1 — 近期修复（4 项，全部脚注探针实证）

| ID | 发现 | 危害×复杂度 | 证据 | 已对照模式 | 定稿门 |
|----|------|------|------|:--:|:--:|
| ITM-615 | CI 实际只跑 verify-ai + gate，六道 AI 质量防线未挂 CI | 高×易 | `cat .github/workflows/ci.yml:89-109` | PD28+PD29 | 三问✅ |
| ITM-616 | 全仓无凭据扫描器，pitfalls SE2 却称 PDDD-G19 做凭据扫描 | 高×易 | `grep -rniE "gitleaks\|trufflehog" scripts/ .ai/scripts/ .github/` → 空；`gate-check.sh:573` G19=命名 | PD28 | 三问✅ |
| ITM-617 | README/pitfalls 称 21 ADR，实测 22 | 中×易 | `ls docs/decisions/*.md \| wc -l` → 22；`grep "21 份 ADR" README.md` → 命中 | PD28 | 三问✅ |
| ITM-618 | dialect-probe 两份副本反向漂移（root 比 .ai 真源新） | 中×易 | `diff` 6 处差异；`grep -c RunDialectGuarded` root=3/.ai=0；git log 双向核实 | PD28 | 三问✅ |

<details>
<summary><b>ITM-615 · CI 覆盖面缺口（P1 展开）</b></summary>

**证据**：`.github/workflows/ci.yml` 的 "AI system self-check + gate" 步骤（:89-109）分支逻辑：
- 有 `.ai/scripts` → `verify-ai-system.sh` + `gate-check.sh --allow-dirty`
- 无 `.ai/scripts` → `scripts/gate-check.sh`（根仓 G1-G3 快速门禁）

**未挂 CI 的防线**：`tech-debt-scan` / `doc-consistency-check` / `encoding-gate` / `test-gate` / `flaky-gate` / `template-gate` / `assertion-strength-check`。且 `.ai` 被 `.gitignore:67`（实为 95 行附近）忽略、为独立仓 → fresh checkout 恒走 else 分支 = 仅 G1-G3。

**定稿门三问**：① 误判库对照——非 PD1-PD37 误判模式，属 PD28/PD29 根因类（不豁免）；② 反证搜索——`git ls-files .github/workflows/ci.yml` 确认 CI 定义在案、`grep -n "tech-debt-scan" ci.yml` 零命中，无反证；③ 触发路径——任意 PR 只过 gate 即合并，文档口径/编码/命名/模板/弱断言无拦截。

**修复方案与验证**：CI 增挂秒级无依赖脚本（encoding/doc-consistency/tech-debt）；或文档明确覆盖范围。验证：`grep -nE "tech-debt-scan|doc-consistency|encoding-gate" ci.yml` 修复后应命中。

</details>

<details>
<summary><b>ITM-616 · 凭据防线声明失实（P1 展开）</b></summary>

**证据**：`docs/pitfalls.md:139`（SE2）真源列"PDDD-G19（原 ORM G9）扫描受跟踪文件零硬编码凭据"；实际 `.ai/scripts/gate-check.sh:573/598` 的 PDDD-G19 是**测试方法命名三段式**。全仓无 gitleaks/trufflehog/detect-secrets。

**反证搜索**：负向声明按 AGENTS 规则扩面——`grep -rniE` 覆盖 `scripts/`/`.ai/scripts/`/`.github/` 三目录、含同义词，零命中；第二独立方法：`git grep -lEi "Password=[A-Za-z0-9_]{6,}"` 全仓仅命中占位符文件（`appsettings.test.json`=`test`、`ci.yml`=`postgres`/`root`、测试=`probe-pass`），确认真实凭据仅在**未入库**的 `appsettings.test.local.json`（`.gitignore:93` 覆盖，`git log` 确认从未提交）。

**结论**：**当前无实际泄露**，但"机械守护凭据"的声明虚假——一旦真实凭据误提交无告警。触发路径见清单。

</details>

<details>
<summary><b>ITM-618 · 双副本反向漂移（P1 展开）</b></summary>

**证据**：`diff scripts/dialect-probe.sh .ai/scripts/dialect-probe.sh` → 6 处差异（除 ROOT 定位行外）：root 独有 `RunDialectGuarded`（v8 单方言连接失败降级，3 处）+ `AmbientTxDapperSmoke`（二轮 T5 ambient 事务挂接探针，3 处）；`.ai` 真源各 0 处。文件头（`scripts/dialect-probe.sh:2-4`）却声明"真源在 .ai……其余逐行一致，改后必须同步重新生成本副本"。

**方向核实**：`git log --oneline -- scripts/dialect-probe.sh` → root 由 `05dc469`/`955472a` 引入这两项；`.ai` 仓 `e39ba6d` 无。**CI 跑 root（新版），.ai 记录旧版**。

**机理**：`verify-ai-system.sh` V16 只做 `bash -n` 语法检查，不比对两副本内容 → 漂移不可见。

**修复方案**：以 root 为源反向同步回 `.ai`（修正"真源"方向），并给 V16 增内容比对项。

</details>

#### 🟡 P2 — 计划修复（26 项，允许 [推断] 定稿）

| ID | 发现 | 证据 | 已对照模式 |
|----|------|------|:--:|
| ITM-619 | `fix-completeness-check.sh` guard/status 两分支实测假绿 | 实跑 exit 0（错 KEY 放行） | PD29 |
| ITM-620 | `gate-check` G23/G24 只查暂存 → CI 恒 PASS | `gate-check.sh:693/707` `git diff --cached` | PD29 |
| ITM-621 | `tech-debt #20` 恒 PASS（UseSqlite 存在即过） | `tech-debt-scan.sh:334` | PD29 |
| ITM-622 | `install-ai-system.sh:63` `read` 语法错误 | 实跑 exit 127 | — |
| ITM-623 | 文档推荐 `dotnet test PalDDD.slnx`（违 MTP 禁令且扫描器豁免文档） | `conventions.md:805` 等 6 处 | PD27 |
| ITM-624 | 文档计数批量漂移（AOT 22→14 / 212→213 / 33→41 / 40≠35 包 / ConfigureAwait 四方 / 18 决策） | 各条实测命令 | PD28 |
| ITM-625 | PublicApiSnapshot 不含 field/const → PalMetrics 零守护 | 快照 field 行=0；`PublicApiSnapshotTests.cs:60-91` | — |
| ITM-626 | `AddGeneratedMessages` 全仓零接线（生成物无端到端测试） | `git grep` 仅生成器自身 | — |
| ITM-627 | Command Dispatch/Saga Transition 无发射点但 README 声称 | `PalDiagnostics.cs:46/57` 仅定义 | PD28 |
| ITM-628 | KafkaBroker EOF `Message==null` → NRE | `KafkaBroker.cs:250` | — |
| ITM-629 | Ulid converter 直用 `ValueSpan`（多段序列抛 IOE） | `IdentityGenerator.cs:706` | — |
| ITM-630 | tutorial `AppOutboxDbContext` 缺抽象成员（编译失败） | `tutorial.md:466` vs `OutboxDbContext.cs:90` | — |
| ITM-631 | `SqlServerOutboxDbContext` Obsolete 但 docs 列一级方言 | `SqlServerOutboxDbContext.cs:11` vs 4 文档 | PD28 |
| ITM-632 | ProjectionCheckpoint 四写路径 OCE 逃逸不 Detach（幽灵租约） | `ProjectionCheckpointDbContext.cs:96-297` | — |
| ITM-633 | `DefaultSagaManager.ResumeAsync` 不落库（决策效果可能丢失） | `DefaultSagaManager.cs:62-119` | — |
| ITM-634 | `DapperOutboxStore.created_at` 用 Store 时钟（跨栈分叉） | `DapperOutboxStore.cs:197/219/230` | PD17 |
| ITM-635 | `PostgreSqlMultiHost` 零副本分支绕过 Host 校验 | `:210-228` vs `:343/359` | PD17 |
| ITM-636 | `PostgreSqlOutboxNotifier` LISTEN/NOTIFY 未加引号（大小写折叠） | `:138/248` + `:103-115` | — |
| ITM-637 | `PostgreSqlReadWriteRouter` 实例注册不释放（连接池泄漏） | `:263-267`；子代理探针实测 | — |
| ITM-638 | Inbox/Checkpoint `MarkFailed` 截断 2040 vs 2000 分叉 | Dapper vs 其余三栈 | PD24 |
| ITM-639 | `RabbitMqBroker` mandatory 依赖 confirms（消息静默丢失） | `:105-115`（注释自认） | — |
| ITM-640 | `OutboxDomainEventInterceptor` 在 `AddDbContextPool` 下作用域捕获失效 | `:56-72` + `ServiceCollectionExtensions.cs:54` | PD32 |
| ITM-641 | `PostgreSqlReportHelper` DateOnly/TimeOnly/数组 落 `Convert.ToString` | `:243-291` | — |
| ITM-642 | `OutboxRequeueTests` 只测 InMemory——三栈零行为测试 | `OutboxRequeueTests.cs:4-12` | PD17 |
| ITM-643 | `ArchitectureBoundaryTests:891` 守卫正则恒不匹配（死代码） | Python re 实测 0 命中 | PD29 |
| ITM-644 | `PalOrmSagaMultiDialectTests` JSON 案例缺 MySQL | `:161-186` | — |
| ITM-645 | 测试名实不符批量（≥8 处） | 各条完整方法体 | PD29 |
| ITM-646 | `BrokerIntegrationTests` `count==5` 与 at-least-once 冲突 | `:452/637` | — |
| ITM-647 | `EventLogTests` 无 `[NotInParallel]` 但用进程级监听器 | `:8/37-67` | PD29 |

#### ⚪ P3 / 评估（34 条，汇总进 `action-items-p3-backlog.md`）

见清单 P3 段落：代码健壮性 32 条（含已声明边界 15 条、真实缺口 17 条）+ 测试/文档 2 批。全部为低危害（注释缺失/装饰性不一致/计数漂移/已声明取舍）。

### 2.3 观察项（非问题，但值得注意）

| ID | 观察 | 说明 |
|----|------|------|
| OBS-1 | 手写 src 码质量高 | 213 文件逐行读完仅 10 条 P2 落在代码层，且多为"已声明边界 + 姊妹未同步"；大量 v70+ 轮修复痕迹显示高成熟度 |
| OBS-2 | 误判库有效震慑 | 子代理明确剔除 ≥24 条命中误判库的候选（PD1/PD2/PD3/PD4/PD5/PD10/PD12/PD14/PD17/PD18/PD21/PD26/PD33），证伪数 24 |
| OBS-3 | 依赖方向零循环 | 36 项目全图 DFS 环检测 = NONE；Core 零 ProjectReference（轴 3 全绿） |
| OBS-4 | 诊断分层 ADR-022 落地 | 38 条诊断（PDDD 15 + PALMSG 7 + PALENUM 9 + PALID 7），三对分层与共享谓词链接编译核实一致 |
| OBS-5 | 测试基础设施规范 | 零 `Microsoft.NET.Test.Sdk`；MTP 三开关齐备；无 `[Ignore]`/`[Skip]` 长期挂起（skip 仅环境守卫） |
| OBS-6 | 方言实测轴不可达 | PG/MySQL TCP 可达但 Npgsql 握手超时（疑似防火墙/凭据过期）——本轮 `dialect-probe` 记 SKIP，须 CI 补跑 |

---

## 第三部分：架构合规与收束判定

### 3.1 DDD / Clean Architecture 合规

| 原则 | 状态 | 证据 |
|------|:----:|------|
| 领域层零基础设施依赖 | ✅ | `PalDDD.Core.csproj` 零 ProjectReference + 仅 ByteAether.Ulid（G2/G4/G5 PASS） |
| 依赖方向外→内单向 | ✅ | 全 36 项目 ProjectReference DFS 环检测 = NONE（轴 3） |
| 跨 BC 仅通过领域事件 | ✅ | G13 红线扫描 PASS |
| 无 IRepository\<T\> 等反模式 | ✅ | G13 PASS（文件不存在 + 源码零命中） |
| DIM 桥接替代反射 | ✅ | G7/G8 PASS；两个 AOT-true 项目反射点均带 `[RequiresDynamicCode]`/`[UnconditionalSuppressMessage]` |
| 聚合根保护不变量 | ✅ | Entity/AggregateRoot/ValueObject 逐行审读，构造+工厂校验完整 |
| AOT 三态分层 | ✅（实现）/ ⚠（文档） | 显式 true 8 / 显式 false 14 / 继承 14；`docs/testing.md:405` 计数失实（ITM-624） |

### 3.2 三轴收束判定

| 轴 | 状态 | 证据 |
|------|:--:|------|
| 机械轴 | ✅ | gate 24/24 + verify-ai 22/22 + tech-debt 0 失败 + doc/encoding/template/test-gate 全绿 + 构建 0W/0E |
| 静态轴 | ⚠️ | 地毯轮新发现 P1×4 + P2×26（非零新发现 → 不可收束） |
| 实测轴 | ⏸️ 待 CI | dialect-probe SKIP（PG/MySQL Npgsql 握手超时）；无外部依赖测试 841/841 全绿 |

**收束判定**：**不可收束**。静态轴有实质新发现（P1×4 属"验证能力"缺口），必须进入修复轮。按 `engine.md` 评审-修复循环协议，修复后须跟验证轮（逐 diff 验证 + 盲区补扫），三轴重新收敛后方可声明完成。

---

## 第四部分：附录

### 4.1 评审执行统计

| 指标 | 数值 |
|------|:----:|
| 逐行覆盖文件/行数 | 642 文件 / 107801 行（src 213 + test 104 + docs/scripts/config 78 + 其余为 .ai 与配置） |
| 并行子代理 | 14 个（src×7 + test×5 + env×1 + 跨片不变式×1） |
| 探针数（按形态） | 实跑命令 ≥30（grep/cat/diff/regex/dotnet test/dialect-probe）；子代理容器行为探针 2（MS.DI 释放、Kafka NRE） |
| 证伪数 | 24（命中误判库剔除项） |
| 下沉数 | 建议下沉 6（ITM-619/620/621 → 门禁修复；ITM-625/626/627 → 生成物断言；ITM-643 → boundary 负向自证） |

### 4.2 自我局限声明

```
本轮局限：
- 方言实测轴 SKIP——PG/MySQL TCP 可达但 Npgsql 握手超时，40 项方言断言未跑（待 CI dialect-probe job）。
- flaky-gate 未对单项目做重跑式检测（需传 csproj，本轮判据为子代理静态识别）。
- docs/design/palorm-architecture.md（2024 行）仅读结构 + 抽样，内部事实性声明未逐条对码（标 ❓）。
- docs/design/v8-orm-*.md 为 ORM 参考文档，仅扫读。
- P2 中标注 ⚠/❓ 者（ITM-629/633/639/640/646/647）为静态推理，修复前须补探针证实（engine.md 证据分级允许 P2 [推断] 定稿）。
- 第三方 NuGet 包内部实现未审（仅核 XML 文档与程序集字符串）。
- 未运行 AOT publish 实跑（CI aot-verify job 负责）；未跑全量集成测试（需 Testcontainers，环境无 Docker）。
```

### 4.3 元审计自检

```
□ Z0 覆盖度: 应读清单 642 文件全部覆盖；零发现流（七流）附覆盖度证据（子代理逐文件勾销）      ✅
□ S1 论证链: 4 项 P1 含 证据(实测命令) + 定稿门三问 + 触发路径                              ✅
□ S2 合规: 3.1 七项原则全部检查（六项 DDD + AOT 三态）                                      ✅
□ S3 指标: 4.1 统计与工作树实测一致（未评分——综合分已废止）                                ✅
□ S4 反模式: 无"行数少=更好"类判断；手工码"代码少"未被误判为质量高                          ✅
□ 可信度: ⚠/❓ 发现不含确定结论；P0/P1 无 [推断] 定稿（4 项 P1 全部实测）                    ✅
□ 一致性: 摘要(4P1/26P2/34P3) ↔ 发现清单 ↔ 收束判定 一致                                   ✅
□ 对比: 与上轮（d8746a6）无退化遗漏；新增面（全仓档）已声明原因                             ✅
违反项: 无
```

### 4.4 收束建议（下一步）

1. **立即**：修 ITM-616（安全声明）+ ITM-615（CI 覆盖）+ ITM-618（副本漂移）——三项 P1 均为"易修复"。
2. **本轮修复轮**：按清单"修复顺序建议"表推进 P1→P2；每项遵循修复门两问（先补传感器再修）。
3. **验证轮**：修复后逐 diff 验证 + 盲区补扫（发布面优先）；补跑方言探针（环境或 CI）。
4. **下沉**：把 ITM-643（守卫正则）的负向自证、ITM-619/620/621 的门禁修复下沉为机械防线；ITM-625/626/627 下沉为生成物断言。
5. **不纳入本轮**：P3 汇总（34 条）进 `action-items-p3-backlog.md`，30 天老化升 P2。

---

> 报告与证据链：`.ai` 系统运行输出留档于工作区临时目录；子代理逐文件覆盖度自报见各分片交付。本报告按 `.ai/review/prompt.md`「报告不可变」规则归档——后续只可追加「事后勘误」区块，不得改写正文结论。
