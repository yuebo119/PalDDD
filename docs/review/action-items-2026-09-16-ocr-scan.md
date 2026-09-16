# Pal.DDD 任务清单 — OpenCodeReview 全仓扫描（2026-09-16）

> 来源：`ocr scan` 全仓扫描（389/389 文件 · 1514 条评论），产物见
> `~/.opencodereview/reports/Pal.DDD-2026-09-16/`（`ocr-full-scan-389files.json` 全量、
> `findings-critical-high.md` 144 条 critical/high、`project-summary.md` 模型综合结论）。
> 基线 commit：`2ff9ef0`（dev 分支，v2.2.0）
> 编号衔接：主仓既有最高为 ITM-725（`docs/review/action-items-2026-09-15-full-audit.md`）。本清单自 **ITM-726** 起。
> 修复提交：`ea18a96` → `24e67d0` → `cf7caeb` → `4c8c1d9` → `cc8b72c` → `added89` → `8d0b5ff`（7 个提交，均在 dev）

---

## 口径说明

**分级**：沿用 `docs/review/ACTION_ITEMS_TEMPLATE.md` 的**危害 × 复杂度**矩阵（P0-P3），与 ITM-685~725 清单可比。

**可信度**：本清单的 43 条**全部经过人工读码核实**（不是模型原始输出）。模型输出共 1514 条，
本次只核实了其中的 critical 4 条 + 我去重后挑出的可疑项，核实结果四分：

| 判定 | 条数 | 处置 |
|------|:----:|------|
| 真实缺陷 | 26 | 已修（ITM-726~749 + 764~765，含全部 4 条 critical） |
| **已声明的设计决定（误报）** | 15 | 驳回（ITM-750~761 + 766~768）——**记录在此防下轮审计重复提出** |
| 已裁决·排队中 | 1 | ITM-762（本清单发现的问题，但**已有 ADR 覆盖**：ADR-020 决策 2 + 状态更新第 5 点，v3.0 窗口执行） |
| 需裁决 | 1 | 挂起（ITM-763），决策提案见 `docs/decisions/023-iunitofwork-nested-transaction-semantics.md`（提议） |

> **补批（同一扫描会话，2026-09-16）**：提交 `d500d31`（amend 后 `1ccdf86`）新增
> src/ 侧 2 处修复（ITM-764~765）与 3 处驳回（ITM-766~768）。本表与下方进度表为**补批后终值**。
> 计数勘正：该提交正文曾把驳回总数写成 14（12+3 应为 **15**），已 amend 更正为 15。

**误报规律（供后续使用参考）**：模型在「声明写在文件头/类型头，而非报错调用点」处误报率最高。
15 条驳回中有 10 条属此模式。使用者看到高危断言时，**先读被指文件的头注释再决定是否动代码**。

> 计数勘正：修复提交正文中曾写「合计 23 处」，按**独立缺陷**逐条拆分后为 24 处
> （`flaky-parse.cs` 的 run_N 崩溃与 groups 崩溃是两个独立调用路径）；补批再 +2 为 **26 处**。
> 本清单以 26 为准。

---

## 总体进度

> 状态更新时间：2026-09-16

| 里程碑 | 条目数 | 待处理 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|
| 已修（真实缺陷） | 26 | 0 | 26 | 100% |
| 驳回（记录依据） | 15 | 0 | 15 | 100% |
| 已裁决·排队中（ADR 已覆盖） | 1 | 0 | 1 | 100% |
| 需裁决（提案已就绪） | 1 | 1 | 0 | 0% |
| **合计** | **43** | **1** | **42** | **97.7%** |

---

## 一、已修条目（ITM-726~749、764~765）

> 每条的复现/变异/新旧对照证据写在对应提交正文中，此处只列结论与验证方式。

| ITM | 标题 | 危害/复杂度 | 提交 | 验证方式 |
|-----|------|:-----------:|------|---------|
| 726 | `pre-commit` 触发条件在 pipefail 下静默跳过守卫（6 处同形） | 高/易 | `ea18a96` | 复现旧写法退出码 141；新写法正负例 |
| 727 | `check-all.cs` 并发写同一 `List<string>`（丢行 → 门禁假绿 / 崩进程） | 高/易 | `cf7caeb` | `check-all.cs` 全量跑通 · 0 编译错误 |
| 728 | `ci-coverage.cs` NaN/Infinity 阈值使覆盖率门禁静默放行 | 高/易 | `cf7caeb` | 自测 25/25 · 变异探针 7 例转红（18/25） |
| 729 | `encoding-gate.cs` E4 恒空转（`Path.GetExtension` 只返回单段） | 高/易 | `cf7caeb` | 放含 CR 的 `.verified.txt` → 修复后能红 |
| 730 | `gate-lite.cs` 缺 `src/` 时三计数恒 0 → 三 ✅ + exit 0 | 高/易 | `24e67d0` | 非仓库根运行 → exit 1（修复前 exit 0） |
| 731 | `secret-scan.cs` git 失败返回空列表 → 渲染成 PASS（安全门禁 fail-open） | 高/易 | `24e67d0` | 空 git 仓库 → FAIL + exit 1 |
| 732 | `secret-scan.cs` stderr 重定向不排空（管道死锁） | 中/易 | `24e67d0` | 探针：旧写法 8000ms 未结束 · 新写法 143ms |
| 733 | `changelog-check.cs` stderr 不排空（死锁） | 中/易 | `4c8c1d9` | 同上探针（同形） |
| 734 | `fix-orchestrator.cs` stderr 不排空（死锁） | 中/易 | `4c8c1d9` | 同上探针（同形） |
| 735 | `review-gate.cs` stderr 不排空（死锁） | 中/易 | `4c8c1d9` | 同上探针（同形） |
| 736 | `review-snapshot.cs` stderr 不排空（死锁） | 中/易 | `4c8c1d9` | 同上探针（同形） |
| 737 | `doc-consistency.cs` D7 零条目假绿（README 在但提取 0 条） | 中/易 | `4c8c1d9` | 自测 +3 例 · 变异探针 14/15 |
| 738 | `vuln-scan.cs` 输入形状异常（缺 `projects`）被渲染成「0 漏洞」 | 中/易 | `4c8c1d9` | `{}` → exit 2 · 自测 +5 例 |
| 739 | `refine-scan.cs` 缺 `src/` → 全 0 报告 + exit 0 | 中/易 | `cc8b72c` | 假根探针 → FAIL + exit 1 |
| 740 | `test-gate.cs` ci.yml 缺失 → `0 < 0` 为假 → PASS「0 个 job 均有超时」 | 中/易 | `cc8b72c` | 本仓 4 job 不误伤 |
| 741 | `template-compile-probe` 零代码块 → PASS「全部可编译」 | 中/易 | `cc8b72c` | 本仓 9 模板 / 13 块 不误伤 |
| 742 | `template-compile-probe` `FindRoot` 走顶后抛裸 NRE | 低/易 | `cc8b72c` | 改为显式异常 + 可读信息 |
| 743 | `guard.cs` 失败详情取**前** 15 行（注释写的是尾部）→ RED 时看不到真失败 | 中/易 | `added89` | 取尾部 + 单次 Split |
| 744 | `dapper-param-guard.cs` 正则未锚定 `=` 左侧 → `==`/`!=`/`>=` 假阳性 | 中/易 | `added89` | 自测 +4 例 · 变异探针 3 例转红 |
| 745 | `xml-guard.cs` staged 模式校验工作树而非暂存 blob | 高/中 | `added89` | 暂存坏/工作树好探针转红 · 三模式复验 |
| 746 | `sister-axis.cs` 整行子串过滤丢真候选（15 行实测被丢） | 中/易 | `added89` | 新旧对照 15 行 → 30 行 |
| 747 | `flaky-parse.cs` `run_N` 缺失抛 `DirectoryNotFoundException`（裸堆栈 exit 127） | 中/易 | `8d0b5ff` | 新旧对照 127 → 告警 + exit 0 |
| 748 | `flaky-parse.cs` 报告缺 `groups` 抛 `KeyNotFoundException`（裸堆栈 exit 127） | 中/易 | `8d0b5ff` | 新旧对照 127 → 告警 + exit 0 |
| 749 | `review-gate.cs` 分类表漏判（`scripts/` 等改动判成 SKIP 且文案与实际相反） | 中/易 | `8d0b5ff` | 新旧对照：计数全 0 → 工具/样例 21 · CI/钩子 1 |
| 764 | `SagaStep.Timeout` 负值守卫被对象初始化器绕过（`public init` 可覆盖构造器校验） | 中/易 | `1ccdf86` | +1 测试 · 变异探针 2 例转红（两条赋值路径同时失守）· 公共 API 快照未变 |
| 765 | `PeriodicBackgroundProcessor` 失败回调自身抛出打死轮询循环（与本方法的 CA1031 抑制理由相反） | 中/中 | `1ccdf86` | +1 测试（会抛的 `IPalLogger` 桩）· 变异探针 1 例转红 · Transactions 全量 176/176 |

**规程第 4 步**：`gate-audit.cs` 新增 2 条隔离式探针（`secret-scan` 空输入、`gate-lite` 缺 `src/`）
并登记 `gate-lite` 到 `probedGates`；最终矩阵 16 接线门禁 OK · 0 UNVERIFIED · 0 缺口 · 0 未归类 · 探针 8/8。

---

## 二、驳回条目（ITM-750~761、766~768）— 各附判断依据

> **本节的用途**：这 12 条是模型的 critical/high 断言，读码后确认属**已声明的设计决定**。
> 记录依据是为了避免下轮审计/下一个审查者重新提出——即本仓 AGENTS.md 的
> 「反驳结果记录存档（被否定的发现是结果不是浪费）」。

| ITM | 模型断言 | 依据（声明位置与原文摘录） |
|-----|---------|--------------------------|
| 750 | `ICompressor` 契约允许 `Compress` 产出解压侧拒绝的数据（"破坏往返期望"） | `src/PalDDD.Compression/ICompressor.cs:18-21`「v53 P3 联动约束声明：本方法对输入/输出均无上限；而 Decompress 侧有 8MB 上限……消息负载量级有界的框架场景下为预期行为」 |
| 751 | `OpenZL` 标识与 Zstd 字节格式不匹配（持久化后会读不出） | `src/PalDDD.Compression.Native/NativeCompressors.cs:143-146`「P2 定案（前向兼容声明）：本实现输出/输入为 Zstandard 字节格式，算法标识 OpenZL 是历史命名……持久化侧应同时记录格式版本」 |
| 752 | `CompressionProvider` 重复算法注册导致「解析顺序与覆盖行为歧义」 | `src/PalDDD.Compression/CompressionProvider.cs:30-34` 构造期 `TryAdd` 失败即抛 `NotSupportedException` 带算法名——是**显式 fail-fast**，不是歧义（模型把显式拒绝描述成了歧义） |
| 753 | `check-all.cs` 第二次 build 退出码丢弃 → 非 `error CS` 的构建失败被吞 | `scripts/check-all.cs:53-54` 注释声明「退出码不参与判定（对齐 bash `grep -c … \|\| true` 兜底——非 error CS 形式的失败已由 2/3 段拦截）」 |
| 754 | `ci-failed-tests.cs` 注解只替换 `\n` 不处理 `\r`（可致注解损坏/注入） | `scripts/ci-failed-tests.cs:49-50` 注释声明「与 Python 版等价：先截断（`message[:250]`）再替换换行（`chr(10) -> " \| "`），`\r` 不替换」 |
| 755 | `changelog-facts.cs` 段 2-7 的 git 查询丢弃退出码 → 失败渲染成「（无变更）」 | `scripts/changelog-facts.cs:19-20` 头注释「段 2-7 均有 `\|\| true` 兜底不阻断」。**已按可见性补强**（失败打 WARN，退出码契约不变） |
| 756 | `guard.cs` 用 `--no-build -c Release` → 可能测旧产物 | `scripts/guard.cs:24-35` 头注释：本工具定位是关闭本地「改哪跑哪」窗口，CI 的 `dotnet test` 全量构建+全量跑；且注释专门为省 ~22s 拒绝挂 pre-push。加构建违背其成本纪律 |
| 757 | `xml-guard.cs` git 不可用时返回空列表 → 静默 PASS | `scripts/xml-guard.cs:196` 注释「git 不可用——让行，不阻塞提交」。且本 hook 由 git 自身调用，该分支近乎不可达 |
| 758 | `flaky-parse.cs` 的 `Worst` 只排 `F>E>P`，`skipped` 输给 `passed` | `scripts/flaky-parse.cs:15-19` 头注释「等价口径：S=skipped（三十七轮 P2-1：条件性 skip 非 fail）……同一测试跨跑 P/F 任意组合时该跑取最差态（F>E>P）」。S 由 `StatusOf` 单独产出，跨跑 S/P 差异归 `env_mixed`（WARN） |
| 759 | `Entity.cs` 重发链中（非尾）事件会截断其后事件（静默丢事件） | `src/PalDDD.Core/Entity.cs:49-53` 注释声明「中链实例复用会截断其后事件（**同实例重复发布属调用方误用**，防御性取新链位置）」+「事件不可跨聚合共享」 |
| 760 | `SagaState.CloneForLease` 浅拷贝致 `StepStartedAt`/`ExecutedStepKeys` 跨实例共享 | `src/PalDDD.Transactions/Saga/SagaState.cs:100-107`「v26 P3 勘正：……并发写安全未保障；**Version fencing 语义不受影响**；**深隔离需后续破坏性变更**（拷贝容器会改变既有共享语义，须随主版本演进）」 |
| 761 | `Repository.EFCore` 把 Scoped 有状态拦截器烘进更长生命周期注册（captive dependency） | `src/PalDDD.Repository.EFCore/ServiceCollectionExtensions.cs:40-54` 实为 `TryAddScoped<OutboxDomainEventInterceptor>()`，且注释「⛔ 禁止与 `AddDbContextPool` 组合（ITM-640）：Scoped 是硬约束——不得改为 Singleton」 |
| 766 | MySQL `transaction_isolation` 在 MySQL < 5.7.20 / MariaDB < 11.1 不存在（应为 `tx_isolation`，否则 errno 1193） | 本仓只声明对 **MySQL 8.4.11** 做过真库验证（`docs/aot.md:178`、`docs/persistence-aot-status.md:15`），无旧版本或 MariaDB 支持声明——属**未声明支持面**的推测，改之即扩大支持面承诺 |
| 767 | `SET SESSION sql_mode = 'STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION'` 替换整会话模式（丢弃 `ONLY_FULL_GROUP_BY`/`NO_ZERO_DATE` 等） | `src/PalDDD.Dapper.MySql/MySqlPerformanceOptimizer.cs:15` 头注释与 `MySqlServiceCollectionExtensions.cs:259` 的 XML doc 均声明同一取值，且把该取值推荐为 `init_connect`/`my.cnf` 服务器配置——属**声明的取值**（MySQL 语义下声明该取值即等于声明整会话模式）。附证：本仓 SQL 无 `GROUP BY`，宽松模式非承重 |
| 768 | `MySqlPerformanceOptimizer` 连接态 `State != Open` 的 check-then-act 竞态 | 无并发使用证据（`DbConnection` 本身非线程安全，同一连接跨线程使用属调用方错误）。按 OCR 自身线程安全正负面清单「无多线程调用证据不报」判定 |

---

## 三、已裁决·排队中（ITM-762）— 2026-09-16 复核 ADR 后勘正

> **勘正说明**：本条曾列「需裁决」。2026-09-16 复核 `docs/decisions/` 后发现**已有 ADR 覆盖**，
> 故改列本节（无需新决策）。以下保留分析以存档。

### [x] ITM-762 · `OutboxStore`/`IProjectionCheckpointStore` 契约无法表达 fencing · 可信度 ⚠

- **结论**：**已被 `docs/decisions/020-persistence-stack-retirement-roadmap.md` 裁决并排队**，非新增决策。
  依据三处：① 决策 2「**v3.0（破坏性变更窗口）**：……同窗口合并 `IPalOutboxStore` 异步化与
  **跨栈 fencing 契约统一**（两项均已排队的破坏性变更，一次 major 窗口清偿）」；
  ② 状态更新（2026-09-13 退役延后）第 5 点「原决策 2 中"v3.0 窗口合并 `IPalOutboxStore` 异步化
  与契约统一"**不受影响——仍按 major 窗口推进**，仅与退役解绑」；
  ③ 接口自身预告：`src/PalDDD.Transactions/Outbox/OutboxStore.cs` 类头 XML doc
  「📣 v3.0 破坏性变更预告（ADR-020，维护者裁决 2026-08-26）」。
- **模型断言的部分**：`MarkProcessed(record)` 不带 owner 令牌、`TryStartAsync` 返回 `null` 混合两义 ——
  事实成立，但**已在 ADR-020 的排队范围内**，且 `MarkProcessed` 的 Remarks（ITM-269/272）已登记
  三栈分歧细节（含「Dapper 内存侧 fencing 弱于 PalORM 的 affected 门控」）。
- **残留可议点（不构成新决策）**：收敛时的**目标语义**（对未租约消息放行 vs 拒绝）尚未指定 ——
  属 v3.0 窗口执行期的实现取舍；届时若需在候选语义间定案，再出 ADR。
- **涉及文件**：`docs/decisions/020-*.md`、`src/PalDDD.Transactions/Outbox/OutboxStore.cs`、
  `src/PalDDD.Projections/IProjectionCheckpointStore.cs`、`src/PalDDD.Transactions/Saga/ISagaStateStore.cs`

## 四、需裁决条目（ITM-763）— 决策提案见 ADR-023

### [ ] ITM-763 · `IUnitOfWork` 嵌套事务语义：三栈分歧（两栈静默提交外层事务）· 可信度 ✅

- **维度**：健壮性 / 契约 · **优先级**：P1 · 危害: 高 · 复杂度: 中（危害按「数据原子性」面定级）
- **问题（较本清单初版更精确）**：不是「文档与实现不一致」的单点问题，而是**三栈对「事务已活动时
  再次 Begin」的处置分歧，其中两栈造成原子性破坏**：

| 栈 | 事务已活动时 Begin | 位置 | 嵌套 `ExecuteInTransactionAsync` 的后果 |
|----|------------------|------|--------------------------------------|
| EF Core | 静默 no-op | `src/PalDDD.Repository.EFCore/UnitOfWork.cs:38` | 内层 Commit **提交外层事务** → 原子性破坏 |
| Dapper | 抛 `InvalidOperationException`（ITM-088） | `src/PalDDD.Dapper/DapperUnitOfWork.cs:41-43` | fail-fast，无破坏 |
| PalORM | 幂等 no-op（注释「幂等：已有活动事务」） | `src/PalDDD.PalORM/PalOrmUnitOfWork.cs:47` | 同 EF Core → 原子性破坏 |

  而 `UnitOfWorkExtensions.ExecuteInTransactionAsync`（`src/PalDDD.Core/IUnitOfWork.cs:43-69`）是
  无条件「Begin → work → SaveChanges → Commit」，其 XML doc 未提及嵌套语义。
- **决策提案**：`docs/decisions/023-iunitofwork-nested-transaction-semantics.md`（状态：**提议**）——
  建议**收敛到 fail-fast**（对齐 Dapper 的 ITM-088），并列出 3 个备选方案（统一 no-op / 仅文档化 /
  接口加 `HasActiveTransaction`）与各自未采纳理由。
- **需你决定**：采纳哪个方案（A 建议 / B / C / D）。采纳后由我实施：改实现 + 三栈对称测试 +
  文档三方一致同步 + Release note 行为变更登记。
- **涉及文件**：`src/PalDDD.Core/IUnitOfWork.cs`、`src/PalDDD.Repository.EFCore/UnitOfWork.cs`、
  `src/PalDDD.Dapper/DapperUnitOfWork.cs`、`src/PalDDD.PalORM/PalOrmUnitOfWork.cs`、`docs/decisions/023-*.md`

## 五、外部任务合并检查清单（本清单适用性）

按 `docs/review/ACTION_ITEMS_TEMPLATE.md` 的要求，合并外部（用户/其他 Agent）任务前须逐项 grep 验证。
本清单来源是 OpenCodeReview 工具产出（外部 Agent），**已验证项**：

- [x] 文件路径 → 43 条涉及的文件全部经 `Read`/`git show` 实读（含 `scripts/` 21 个、`src/PalDDD.Compression` 4 个、
  `src/PalDDD.Core` 2 个、`src/PalDDD.Transactions` 5 个、`src/PalDDD.Repository.EFCore` 1 个、
  `src/PalDDD.Dapper.MySql` 2 个）
- [x] 方法名/类名 → `CloneForLease`、`ExecuteInTransactionAsync`、`MarkProcessed`、`TryStartAsync`、
  `IsMapExtractionEmpty`、`HasProjectsArray`、`GitShowStaged`、`EnumerateByNameSuffix`、`Worst` 等均经实读确认存在
- [x] 配置属性 → `COVERAGE_THRESHOLD`、`Directory.Build.props` 的 `TreatWarningsAsErrors` 已 grep 验证
- [x] 例外登记：**ITM-762 标 ⚠**（fencing 契约的「无法表达」属能力判断，非事实错误；
  已给出 ADR 前的核对方式）；**ITM-763 标 ✅**（文档与实现的矛盾已读两侧代码确认）

---

## 六、修复后检查清单（沿用 ITM-685~725 清单，未改动）

- [ ] 修改是否引入新的编译期依赖？→ 检查 `.csproj` diff
- [ ] 修改是否改变现有行为的语义？→ 检查受影响路径
- [ ] 高危害修复是否有测试覆盖？（资源/安全/并发类必须有）
- [ ] 是否同步了文档与注释？（本仓 #1 三方一致红线）
- [ ] 是否做了变异验证？（新增或修改门禁/测试后，确认它能**红**）
- [ ] 新建或修改的文件行尾为 CRLF？（`dotnet run scripts/encoding-gate.cs` 的 E5 判定）
- [ ] `dotnet run scripts/verify-conventions.cs --quick` 全绿
- [ ] `dotnet run scripts/gate-audit.cs` 矩阵无新增 `REVIEW` 桶条目
- [ ] 若涉及跨文件重构：已用 Serena `find_referencing_symbols` 确认引用点
