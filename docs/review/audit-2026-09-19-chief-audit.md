# Pal.DDD 首席审计报告（2026-09-19）

> 审计基线：commit `c17bd74`（HEAD）· 方法：阶段 1 结构勘察（本日）+ 本会话第五十二/五十三轮全量真实读取（214 源文件、118 测试文件、23 份 ADR 全部 Read，两轮报告 review-2026-09-19-full{,-v2}.md 在案）作为阶段 2 证据基础。审计期间零代码修改。
> 证据标注：[事实] = 文件:行可查证；[判断] = 审计师意见。

---

## 执行摘要

**健康等级：A-。** 这是一个工程纪律罕见的框架库：测试与源码行数比 1.06:1、TreatWarningsAsErrors 全局生效且 22 条 NoWarn 逐条理由（Directory.Build.props:9-45）、门禁体系带变异探针自证（gate-audit 矩阵 17 接线 0 缺口）、断言强度棘轮含负向自证样本、两轮全量评审 0 P1。弱点是结构性的而非工艺性的。**前 3 风险**：① 全家桶锁定 .NET 11 rc.1 预发布依赖（NU5104 抑制自认 GA 债，Directory.Build.props:42）；② MySQL 方言三栈租约互斥分叉——Dapper/PalORM 无行锁存在双执行窗口（PalOrmOutboxStore.cs:129-133 注释自认）；③ 单维护者 + AI 并行流的协调脆弱——本地 16+ 提交未推送、会话外并行提交无任何协调机制。**前 3 机会**：① EF Pooling 解锁（B1，Lease 批量化后成为 EF 栈剩余差距的唯一主杠杆，方案与基准佐证齐备）；② 推送触发 CI 首跑（连带解锁 B4 覆盖率重校准——两个积压验证信号一次清偿）；③ 三栈谓词对照门禁化（姊妹一致性从纪律转为机械）。

---

## 一、仓库地图

**用途**：面向 .NET 11 的 DDD/CQRS/Event Sourcing 基础设施框架，35 个 NuGet 包分发（README.md:17-19），AGPL-3.0-or-later。**预期用户**：在框架上构建业务应用的 .NET 团队；**成熟度**：v2.2.0（Directory.Build.props:60），处于预发布依赖上的生产前成熟期（rc.1 锁定待 GA）。

**技术栈**：C#（LangVersion latest）/ net11.0 运行时 + netstandard2.0（分析器与源生成器）；EF Core 11 rc.1 / Dapper 2.1.79 / PalORM 5.5.1 三栈持久化；Confluent.Kafka + RabbitMQ.Client 消息；TUnit 1.66 + MTP 2.3.3 测试；BDN 0.15.8 基准；CPM 中央包管理（Directory.Packages.props）。

**架构草图**：Core（零依赖，实证：nuspec 仅 ByteAether.Ulid 1.4.0）→ 抽象层（Serialization/Compression/Messaging/CQRS）→ 三栈持久化适配（Dapper ×4 / PalORM ×4 / EFCore ×6，同契约姊妹）→ Hosting/extension 组合层。无应用入口（纯库；bench/ 为基准宿主）。竖切域：Transactions（Saga+Outbox+Inbox）、EventLog、Projections、Idempotency。

**关键目录**：

| 目录 | 一行描述 |
|---|---|
| src/PalDDD.Core + Core.SourceGen | 实体/聚合/事件/SmartEnum + 源生成（netstandard2.0，863+623 行两个生成器） |
| src/PalDDD.Transactions{,.EFCore} | Saga 编排（954 行核心）+ Outbox 租约/死信 + 三栈 EF 实现 |
| src/PalDDD.Dapper*（4 项目） | 手写 SQL 栈（SqlTemplates 常量模板 + 方言子包） |
| src/PalDDD.PalORM*（4 项目） | 源生成 SQL 栈（AOT 主线） |
| src/PalDDD.Messaging{,.Kafka,.RabbitMQ} | Broker 抽象 + 两独立栈（各约 500 行） |
| src/PalDDD.Analyzers{,.CodeFixes} | PDDD001-015 编译期 DDD 合规诊断 + CodeFix |
| test/（16 项目 + PalDDD.Testing） | 34.7k 行测试 + 共享基建（FakeTimeProvider/探针） |
| scripts/（32 个 .cs） | file-based app 门禁（自证+变异探针范式） |
| .ai/（独立 git 仓） | AI 质量系统（评审引擎/lessons/PD 库/metrics） |

**让我惊讶的**[判断]：① 一个"框架库"拥有比多数生产应用更严的质量门禁（32 脚本 + 变异探针 + 断言棘轮）；② 注释密度与决策可追溯性（ITM/ADR/vNN 轮次编号体系贯穿 32k 行源码）；③ test:src 行数比 1.06:1 在框架库中极罕见；④ .ai 作为独立 git 仓管理 AI 评审系统——把"AI 协作流程"本身版本化是我未先例见过的方式。

---

## 二、审计报告（按维度，严重性排序）

### 架构与设计 — 健康（一句话 + 例外）

分层强制由 ArchitectureBoundaryTests（44 用例）机械守护，Core 零依赖实证成立，无循环依赖、无神文件（最大 954 行）。[事实]

| # | 发现 | 位置 | 后果 | 严重性 |
|---|------|------|------|--------|
| A-1 | [事实] MySQL 租约互斥三栈分叉：EF 有派生表 FOR UPDATE SKIP LOCKED，Dapper/PalORM 为 last-writer-wins | MySqlOutboxDbContext.cs:127 vs SqlTemplates.cs:127-131 vs PalOrmOutboxStore.cs:143 | PalORM 注释自认（:129-133）：双 worker 交错时序下"重复投递而非延迟"——多实例 Dapper/PalORM MySQL 部署的重复投递率结构性高于 EF；下游幂等兜底存在但属消费方成本 | **高**（架构级取舍已声明，修复需专项决策 ITM-794） |
| A-2 | [事实] SQLite 时间列 TEXT 编码跨栈互不兼容（Dapper "O" vs EF/PalORM 空格分隔） | DapperAotInitializer.cs:66 vs SqliteOutboxDbContext.cs:14-16 | 混栈共用同一物理库时文本序比较错乱（EF 侧 spike 实证 "O" 参数谓词恒真=错租）；DI 注释已补声明但无机械门禁 | 中（已声明，用户误用面） |
| A-3 | [判断] 三栈一致性靠纪律（姊妹同步评审）不靠机械（无谓词对照门禁） | 三栈 Store 域 | 每轮姊妹修复漂移史（PD17 系列数十条）证明该模式的维护税真实存在 | 中 |

### 代码质量 — 健康

 Saga 四车道 160 行复制已于本会话收敛为 RunRetryLaneAsync 骨架（f5947f5，净删 66 行，表征测试 12 用例锁定行为）；异常纪律全仓统一（无 throw ex、catch 均有理由注释）。

| # | 发现 | 位置 | 后果 | 严重性 |
|---|------|------|------|--------|
| C-1 | [事实] EndpointExtensions 两个 MapCommand 重载约 40 行反序列化段逐字重复 | EndpointExtensions.cs:39-85, 126-172 | 同 Logic 双份=未来修复落两遍（仓内已付过此类税） | 中（ITM-799） |
| C-2 | [事实] Dapper 侧 5 份唯一约束分类器副本且码集分叉（1062/1586 vs 1062/1022） | DapperInboxStore/EventLog/Saga/Checkpoint + DapperIdempotencyStore.cs:257-268 | PalORM 侧已收口 SqlErrorClassifier，Dapper 侧漂移面 | 低（ITM-796） |
| C-3 | [事实] EF FencedTarget 持租分支缺 Status 守卫（同文件三方法两种口径） | OutboxDbContext.cs:240-241 | 理论不可达（持租行恒 Pending），但口径不一致是未来回归温床 | 低（ITM-797） |

### 安全 — 健康（一句话）

注入面零发现（三栈全参数化实证）、无凭据进日志路径（两消息栈全读核验）、secret-scan pre-commit 门禁在位、AGPL 许可显式（Directory.Build.props:63）。**无严重/高级发现。** 唯一记录：CA2100（SQL 注入审查）在全局 NoWarn（Directory.Build.props:28）——[判断] 该抑制的依据（SqlTemplates 常量+参数化）在 Dapper/PalORM 成立，但 EF 栈新增的 FromSqlRaw/ExecuteSql 内插路径（SqliteOutboxDbContext.cs:63-73,116-126）不在原论证面内，属"抑制理由未随代码演化复核"的形态——实测全参数化（本会话 spike），无实际漏洞。

### 测试 — 优势项

test:src = 1.06:1；断言强度棘轮（IsNotNull 基线计数只许下调）含 4/3 引号嵌套的负向自证样本；AssertionStrengthGate/DialectProbe 探针有修复前红形态记录；本会话为三车道补齐 12 表征用例（此前 3/4 车道零覆盖）+ GetPending/守卫/同 tick/批中取消回归。缺口（均已登记 ITM-801/802）：SagaProcessor 一处断言无消息子串（SagaProcessorTests.cs:303-305）、RepositoryEfCore 三个零断言方法（:29-38,60-68,71-77）、EventLogReplaySource 并行注记缺（EventLogReplaySourceTests.cs:119-139）。**低**。

### 性能 — 已大幅清偿

Lease N+1（13.79ms，4.72×）与 GetPending 整表分页已于本会话修复（单语句谓词下推，SQL 次数=1 锁定）；(locked_by,locked_until) 索引在位（OutboxDbContext.cs:372）。剩余：

| # | 发现 | 位置 | 后果 | 严重性 |
|---|------|------|------|--------|
| P-1 | [事实] EF 栈基准残余差距主导项=耗尽型基准内重灌（EF SaveChanges 100 条 vs Dapper 裸 INSERT 固有 2-3×） | PersistenceBenchmarks.cs:348-357 | 消费方感知的 EF 栈吞吐天花板；解锁靠 B1 Pooling（方案齐备） | 中（已裁决 major 窗口） |
| P-2 | [事实] OR 谓词下 (Status,NextAttemptAt,CreatedAt) 索引不保序，下推后仍可能单次全扫+临时 B-tree | audit-2026-09-15-full.md:298 | 大表 GetPending 绝对耗时仍有 O(表·log表) 项 | 低 |

### 依赖 — 一个结构性风险

| # | 发现 | 位置 | 后果 | 严重性 |
|---|------|------|------|--------|
| D-1 | [事实] Microsoft 全家桶锁定 11.0.0-rc.1（EFCore/Extensions/Sqlite 等 9+ 包），NU5104 抑制在案并自认"GA 后统一升级" | Directory.Packages.props:5-15 + Directory.Build.props:42 | rc→GA 的破坏性变更是**硬迁移窗口**（API 变更风险 + 全量回归 + NuGet 重发布）；rc.1 与最终 RTM 间已有行为差异史（本会话 ExecuteSqlInterpolatedAsync 过时、ITM-261 翻译限制均 rc 特有） | **高**（时间窗口型） |
| D-2 | [事实] Dapper 2.1.79 → 2.1.86 可升级 | dotnet list package --outdated 实测 | 补丁级，低风险 | 低 |
| D-3 | [事实] AGPL-3.0-or-later | Directory.Build.props:63 | [判断] 对"被业务团队引用的框架"是采用阻力项——商业消费方需法务评估；属产品定位决策非缺陷 | 记录 |

CPM 卫生优秀（单一版本源）；锁文件不存在（库项目惯例，合理）。

### 开发体验与运维 — 优势项 + 一个高优先缺口

32 个 file-based app 门禁（每个带 --selftest + gate-audit 变异探针 + 双向登记校验）、CI 五 job（build-and-test 含 format-verify 8e97e9c 增 / aot-verify 真发布运行 / coverage 0.70 阈值 / dialect-probe Testcontainers）、pre-commit 七守卫自动安装。

| # | 发现 | 位置 | 后果 | 严重性 |
|---|------|------|------|--------|
| O-1 | [事实] 本地 16+ 提交未推送 origin（工作树提交链 3c887c7..c17bd74，origin 停在 080871c） | git 状态 | 两个连锁积压：CI 全套（含 aot-verify 真发布）未在本轮 17 项源码改动上运行过；B4 覆盖率重校准的触发信号未产生。aot-verify 尤其关键——Saga/EFCore 改动后的 AOT 链路只有 CI 能实证（P0 #3：IsAotCompatible=true + 0 警告 ≠ 运行时安全） | **高**（验证债，非代码债） |
| O-2 | [事实] 会话外并行提交（6553c7a/3336aae/d973ead）与本会话无协调机制——同名文件域（AGENTS.md/台账）曾并行修改 | git log | 单人+多 AI 会话并行的写冲突风险；当前无 hook 强制"推送前 rebase" | 中 |

### 文档 — 优势项

三方一致是红线文化且有机械门禁（doc-consistency/V9/V10/V11）；23 份 ADR 全部有状态头注（本轮补齐 014/008 的演化注记）；两轮评审发现的全部失同步（DapperAot 翻案传导、ADR 计数、谓词描述）已清偿。残余低价值项已登记（ITM-803：ADR-005 版本快照、ADR-006/009 引用位置）。**低**。

### 优势部分（决定保留什么）

1. **确定性质量门禁体系**（scripts/ 32 个 + 变异探针 + 双向登记）——"没看过仪器故意产生错误答案，就不信它输出的任何数字"的哲学已被制度化 [事实]
2. **注释即契约文化**：ITM/ADR/vNN 编号贯穿，每个 catch 有理由，每个取舍有声明（两轮评审的"已声明项确认在位"共 20+ 处实证）
3. **三栈同构契约**：Outbox/Inbox/Saga 谓词族跨 Dapper/PalORM/EF 逐列一致（片3 对照表实证）
4. **测试质量基建**：断言棘轮 + 探针负向自证 + 表征测试模式（本会话两次实战）
5. **AOT 纪律**：8 项目零反射实证、边界声明完备（ChildSaga 反射面 RUC+RDC 全覆盖）

---

## 三、改进策略

### 主题 1：「验证债」——改动未过 CI 真发布链（解释 O-1，波及 A-1/P-1 的信心基础）
**目标态**：origin 与本地同步，CI 五 job 在最新 HEAD 全绿。**原则**：本地门禁再全也替代不了 aot-verify 的真实发布运行。**权衡**：不推荐本地复刻 aot-verify（约 20 分钟/次，收益低于推送一次 CI）。**完成定义**：推送后 CI run 全绿 + coverage job 产出含 Docker 完整值。

### 主题 2：「预发布窗口管理」（解释 D-1）
**目标态**：GA 发布日的迁移预案就绪（升级清单 + 已知 rc 特有行为清单 + 回归范围），而非临时应对。**原则**：rc→GA 是日历事件，准备工作可以前置。**权衡**：不推荐现在预升 rc.2（中间版本收益低、两次迁移成本）；不推荐为"稳定"降级 .NET 10（AOT/net11 特性是产品定位）。**完成定义**：迁移预案文档 + rc 特有行为清单（ITM-261 翻译限制、ExecuteSql API 演进、runtime-async 待验证项）落盘。

### 主题 3：「姊妹一致性的机械化」（解释 A-1/A-2/A-3/C-2/C-3）
**目标态**：三栈谓词/守卫对照由测试或脚本承载（改一栈不改他栈时红灯），MySQL 互斥分叉有显式决策文档收口。**原则**：该仓的历史缺陷流主体是"姊妹漂移"（PD17 系列），纪律已到极限，边际收益在机械层。**权衡**：不推荐全栈对照矩阵一步到位（维护成本高）——从 Lease/GetPending 谓词对照测试起步。**完成定义**：谓词对照测试在位 + ITM-794 有 ADR 裁决（统一 SKIP LOCKED 或显式接受现状并记录理由）。

### 主题 4：「并行流协调」（解释 O-2）
**目标态**：多会话并行的写冲突有机械防线（推送前 rebase 强制或文件锁约定）。**原则**：单人仓库的协作对象是"多个 AI 会话 + 一个人"，这是新型团队形态，需要新型流程。**权衡**：不推荐分支工作流（单人+AI 的主仓直推效率更高）——推荐"会话结束即推送"纪律 + hook 化。**完成定义**：pre-push hook 校验 origin 落后即提示。

### 明确不修的（努力 vs 回报）
- **P-2 索引不保序**：需要方言特化索引设计，等真实大表负载证据再做（过早优化）
- **EndpointExtensions 40 行重复之外的其余低频重复**：Wrong Abstraction 的反面教训（两次以内不抽）
- **README 全面重写/英文化扩充**：当前 README 准确性高（本轮抽核），投入应去 GA 迁移预案
- **测试零断言方法基线的全量清偿**：棘轮在管，等触碰时逐个清

---

## 四、任务计划

### 里程碑 0 — 安全网（先行，全部 S）
| 任务 | 区域 | 验收 | 工作量 | 风险 | 依赖 |
|------|------|------|--------|------|------|
| M0-1 推送 + CI 全套验证 | git/CI | CI 五 job 全绿（重点 aot-verify 在 Saga/EFCore 改动后通过） | S | 低（只读验证） | 无 |
| M0-2 B4 覆盖率重校准 | scripts/ci-coverage.cs | coverage job 产出含 Docker 完整值后重校阈值并更新基线 | S | 低 | M0-1 |

### 里程碑 1 — 关键修复
**无严重级发现在册**（两轮 0 P1）。唯一高优先项 ITM-794 属决策而非修复：见里程碑 2。

### 里程碑 2 — 高杠杆
| 任务 | 区域 | 验收 | 工作量 | 风险 | 依赖 |
|------|------|------|--------|------|------|
| M2-1 ITM-794 MySQL 互斥分叉裁决 | 三栈 Store | ADR 落盘（统一 SKIP LOCKED 带三栈测试 / 或显式接受 + 消费方指引），消除"已声明但无决策"状态 | M（决策+文档）或 L（实施） | 中（行为变更需专项回归） | M0-1 |
| M2-2 GA 迁移预案 | Directory.Packages.props + docs | 预案文档：升级清单 + rc 特有行为清单（ITM-261/ExecuteSql 演进/runtime-async 待验证）+ 回归范围 | M | 低（纯文档） | 无 |
| M2-3 三栈谓词对照测试 | test/ | Lease/GetPending 谓词逐列对照测试在位（改一栈漂移即红） | M | 低 | 无 |
| M2-4 pre-push 同步提示 | .githooks | pre-push hook：origin 落后 N 提交时警告 | S | 低 | 无 |

### 里程碑 3 — 质量与润色
| 任务 | 区域 | 验收 | 工作量 | 风险 | 依赖 |
|------|------|------|--------|------|------|
| M3-1 ITM-799 反序列化段收口 | EndpointExtensions.cs | 40 行重复收口为单方法，快照测试绿 | S | 低 | 无 |
| M3-2 ITM-800 ContentType 死代码验证 | HealthCheckExtensions.cs:61 | 实测 WriteAsJsonAsync 行为后删除或修正 | S | 低 | 无 |
| M3-3 ITM-796 Dapper 分类器收口 | Dapper 四文件 | 收口为单分类器，码集统一（1062/1586/1022/Sqlite 19/2067 全集） | M | 中（错误分类行为变更需测试） | 无 |
| M3-4 ITM-801/802 测试增强批 | 多文件 | 断言子串补强/零断言方法清偿/并行注记/路径范式统一 | M | 低 | 无 |
| M3-5 ITM-797 FencedTarget 守卫统一 | OutboxDbContext.cs | 三方法同口径（持租分支补 Status）+ fencing 测试绿 | S | 低 | 无 |
| M3-6 ITM-803 ADR 低价值勘误 | docs/decisions | ADR-005/006/009 状态注记 | S | 低 | 无 |
| M3-7 Dapper 2.1.86 升级 | CPM | 升级 + 全量测试 | S | 低 | M0-1 |

### 快速获胜（高影响 × S）
**M0-1（推送）**、**M0-2（B4）**、**M2-4（pre-push hook）**、**M3-1（收口）**、**M3-5（守卫）**、**M3-7（升级）**。

### 前 3 任务实现草图
1. **M0-1 推送**：先 `git push`（A1 在案：proxy 环境需 `git -c http.proxy= -c https.proxy= -c http.version=HTTP/1.1 push` 组合）；观察 CI 五 job，**重点盯 aot-verify**——Saga.cs（RunRetryLaneAsync 泛型骨架 + 委托闭包）与 EFCore（FromSqlRaw 物化）是本轮 AOT 面改动，若 NativeAOT 发布失败优先查闭包类型根引用。陷阱：CI 的 dialect-probe 有 Path gate（Store/SQL 面变更触发）——本轮必触发，Testcontainers 覆盖要过。
2. **M2-3 谓词对照测试**：形态=每栈一个 fixture 提取实际执行 SQL（EF 用 CommandInterceptor 抽 SQL 文本、Dapper 用 wrapper connection、PalORM 用 Session 钩子），断言归一化后（参数占位符替换）三栈 Lease/GetPending 谓词文本等价。关键步骤：归一化规则先定（大小写/空白/参数名）；已知差异白名单显式（PG 的 NOW() vs 应用时钟、MySQL 的 JOIN 形态）。陷阱：EF 生成的 SQL 含前缀修饰，正则剥离子句顺序可能不稳定——按子句集合比较而非字符串全等。
3. **M2-2 GA 迁移预案**：从 NU5104 注释（Directory.Build.props:42）出发列全 rc 包清单；grep 仓内 "rc"、"preview" 标注的行为依赖（ITM-261 的翻译限制、Program.cs 注释的 BDN moniker 崩溃、runtime-async OPP-B2 待验证）建"rc 特有行为表"；预案=升级顺序（Extensions 先、EFCore 次、测试矩阵后）+ 每步回归范围。陷阱：EF Core 11 rc→GA 历史上曾有过翻译行为变更（ITM-261 本身就是 preview 间引入的）——ITM-261 的内存过滤 workaround 要在 GA 后重测可否还原。

---

## 五、开放问题（需人类决定）

1. **ITM-794 方向**：MySQL 三栈互斥统一（Dapper/PalORM 补 SKIP LOCKED 等价物）还是显式接受现状（文档化消费方幂等要求）？这是性能/正确性/维护三角的产品级取舍。
2. **AGPL 许可定位**（D-3）：目标是社区框架（AGPL 抑制商业采用）还是个人/内部（无影响）？影响未来是否加商业授权双轨。
3. **B1 Pooling 的时点**：方案与捆绑裁决（与 B2 同批 major）在案——是否因"EF 栈是生态兼容线"的定位而提前单独做？
4. **多会话并行的协调机制**（O-2）：接受"会话结束即推送"的软纪律，还是要 hook 强制？
