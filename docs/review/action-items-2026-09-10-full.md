# Pal.DDD 行动项清单 — 全仓 AI 质量系统运行（2026-09-10）

> 来源报告：[`review-2026-09-10-full.md`](review-2026-09-10-full.md)
> 基线 commit：`d8746a6`（dev 分支）· 档位：全量/全仓（`review-scope.sh --all`，642 文件 / 107801 行应读）
> 生成方式：`.ai` 四系统全量实跑 + 8 片并行子代理地毯式逐行 + 跨片全局不变式核对 + 主线程探针实证
> 优先级判定：`docs/conventions.md` §13 危害 × 复杂度双维度

---

## 总体进度

| 优先级 | 条目数 | 待修复 | 修复中 | 已完成 | 完成率 |
|:------:|:------:|:------:|:------:|:------:|:------:|
| **P0** | 0 | 0 | 0 | 0 | — |
| **P1** | 4 | 4 | 0 | 0 | 0% |
| **P2** | 26 | 26 | 0 | 0 | 0% |
| **P3** | 34（汇总） | 34 | 0 | 0 | 0% |
| **合计** | 64 | 64 | 0 | 0 | **0%** |

**本轮结论**：机械防线全绿（gate 24/24 · verify-ai 22/22 · tech-debt 0 失败 · doc-consistency 11/11 · encoding 4/4 · template PASS · test-gate 0 失败）· Release 构建 0 警告 0 错误 · 无外部依赖测试 13 项目 841 用例全绿。但地毯式逐行 + 跨片核对暴露出 **4 项 P1（发布面/门禁面失实）与 26 项 P2（代码健壮性 + 测试防线 + 文档一致性）**——机械防线全绿恰好掩盖了这些"机器构不着"的缺陷（PD28/PD29 根因类）。

> 编号衔接：`.ai` 质量系统仓的最大既有行动项编号为 ITM-614（历史行动项归档于 `.ai/review/history/action-items/`；主仓内 ITM 引用最大为 599，散布于 CHANGELOG/README/bench 等）；本清单自 **ITM-615** 起编号。

---

## 🔴 P1 — 近期修复（4 条）

### [ ] ITM-615 · CI 实际覆盖面缺口——六道 AI 质量防线未挂 CI · 可信度 ✅
- **维度**：可维护性 / 质量门禁完整性
- **优先级**：P1 · 危害: 高 · 复杂度: 易
- **问题**：`.github/workflows/ci.yml:89-109` 的 "AI system self-check + gate" 步骤实际只调 `.ai/scripts/verify-ai-system.sh` + `gate-check.sh`（`.ai` 不存在时退化为根 `scripts/gate-check.sh` 的 G1-G3）。**`tech-debt-scan.sh` / `doc-consistency-check.sh` / `encoding-gate.sh` / `test-gate.sh` / `flaky-gate.sh` / `template-gate.sh` / `assertion-strength-check.sh` 全部不在 CI**。而 `docs/conventions.md` 附录、`docs/testing.md` §9、`CHANGELOG.md` v2.1.0 均声称这些防线"挂 CI（PR 时）"。
- **触发路径**：任意 PR → CI 只过 verify-ai + gate（或根仓仅 G1-G3）→ 文档口径校验、编码一致性、测试命名、模板编译、弱断言棘轮、技术债全部不拦截 → 引 README/文档计数漂移、CRLF、弱断言增量可直入主干。
- **建议**：CI 增加对上述脚本的调用（`encoding-gate`/`doc-consistency`/`tech-debt` 为秒级，无外部依赖，可直接挂 `build-and-test` job）。若某些脚本依赖 `.ai` 仓（fresh checkout 不存在），需明确"CI 覆盖范围仅 gate+verify-ai"并同步修正文档口径，消除"挂 CI"失实。
- **风险**：把依赖 `.ai` 的脚本直接挂 CI 会在无 `.ai` 的 fresh checkout 上必然失败——需先解决 `.ai` 分发问题（见 ITM-618）或做存在性降级。
- **验证**：`grep -nE "tech-debt-scan|doc-consistency|encoding-gate|test-gate" .github/workflows/ci.yml` → 当前 0 命中；修复后应命中。✅ 已 cat ci.yml 核实
- **涉及文件**：`.github/workflows/ci.yml`、`docs/conventions.md`（附录）、`docs/testing.md`、`CHANGELOG.md`

### [ ] ITM-616 · 凭据扫描器不存在，但 pitfalls SE2 声称 PDDD-G19 做凭据扫描 · 可信度 ✅
- **维度**：安全 / 文档一致性
- **优先级**：P1 · 危害: 高 · 复杂度: 易
- **问题**：`docs/pitfalls.md:139`（SE2）声称"PDDD-G19（原 ORM G9）扫描受跟踪文件零硬编码凭据"。实际 `.ai/scripts/gate-check.sh:573/598` 的 PDDD-G19 = **测试方法下划线三段式命名**（与凭据无关）；且全仓（`scripts/` / `.ai/scripts/` / `.github/`）**无任何凭据扫描器**（`grep -rniE "gitleaks|trufflehog|detect-secrets"` 零命中）。审阅者据 SE2 会误信"凭据已被机械守护"。
- **触发路径**：开发者/审计者读 pitfalls SE2 → 认为连接串/密码入库会被 G19 拦截 → 实际零防线 → 真实凭据（如本地 `appsettings.test.local.json` 的真实 DB 密码）一旦被误提交即泄露无告警。**注**：本次实查确认当前入库的 `appsettings.test.json`（占位符 `Password=test`）、`ci.yml`（`postgres/root`）、测试文件（`probe-pass`）均为占位符，真实凭据仅在未入库的 `.local.json`——**当前无实际泄露**，但防线声明失实。
- **建议**：① 修正 `docs/pitfalls.md:139` 的 SE2 真源指向（改为实际存在的防线，或标注"无机械防线，依赖人工 review"）；② 补一个确定性凭据扫描（CI 步骤或 pre-commit hook，如 gitleaks）；③ 若采用扫描器，同步 conventions 附录"规范执行矩阵"。
- **风险**：hooks/CI 扫描器可能对测试占位符误报，需白名单（`test`/`postgres`/`guest` 等已知占位符）。
- **验证**：`grep -rniE "gitleaks|trufflehog|detect-secrets" scripts/ .ai/scripts/ .github/` → 当前空；`grep -n "PDDD-G19" .ai/scripts/gate-check.sh` → 命名检查。✅ 已实测
- **涉及文件**：`docs/pitfalls.md`、`.ai/scripts/gate-check.sh`、`.github/workflows/ci.yml`、`docs/conventions.md`

### [ ] ITM-617 · ADR 计数三方一致失守（实测 22，README/pitfalls 声称 21） · 可信度 ✅
- **维度**：文档一致性（准则 8 三方一致）
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：`docs/decisions/` 实测 **22 份**（001-022 连续，`022-analyzer-generator-diagnostic-layering.md` 已于 `eee4c86` 落盘）。但 `README.md:875`「21 份 ADR」、`README.en.md:881`「21 ADRs」、`docs/pitfalls.md:7`「`docs/decisions/001-021`（21 ADR）」均未同步。`doc-consistency-check.sh` D3/D4 已知 ADR=22，却不反查 README 的 21 → 漏检。`.ai/review/prompt.md:11/109` 还写"现行 21"。
- **触发路径**：新读者按 README 索引 ADR → 漏掉 ADR-022（分析器/生成器诊断分层）；按 pitfalls 真源回溯 → 指向不存在的"001-021"范围。属"口径数字单一来源"维护规则（`.ai/README.md` 规则 1）违反。
- **建议**：全仓 `grep -rn "21 份 ADR\|21 ADR\|21 ADRs\|001-021\|ADR 001-021\|现行 21"` 逐处改为 22 / 001-022；优先在 `doc-consistency-check.sh` 增 D-项：README ADR 计数 == `ls docs/decisions/*.md | wc -l`（机械防空转）。同步 `CHANGELOG.md`（只记到 021）。
- **风险**：`docs/pitfalls.md:7` 还含"18 项架构决策"（architecture.md 全文无该表，见 ITM-624）——同一行两处失实，一并处理。
- **验证**：`ls docs/decisions/*.md | wc -l` → 22；`grep -rn "21 份 ADR\|001-021" README.md README.en.md docs/pitfalls.md` → 命中（应零残留）。✅ 已实测
- **涉及文件**：`README.md`、`README.en.md`、`docs/pitfalls.md`、`.ai/review/prompt.md`、`CHANGELOG.md`、`.ai/scripts/doc-consistency-check.sh`

### [ ] ITM-618 · dialect-probe 双副本反向漂移——root 分发副本比 .ai"真源"新 · 可信度 ✅
- **维度**：可维护性 / 防线完整性
- **优先级**：P1 · 危害: 中 · 复杂度: 易
- **问题**：`scripts/dialect-probe.sh:2-4` 文件头声明"本文件是 `.ai/scripts/dialect-probe.sh` 的 CI 分发副本……其余逐行一致"。实测 `diff` 显示 root 副本**多出 v8 单方言降级修复（`RunDialectGuarded`，3 处）与 `AmbientTxDapperSmoke` 事务挂接探针（3 处）**，`.ai` 真源各 0 处（`grep -c` root=6 / .ai=0）。方向已用 `git log` 双向核实：root 由 `05dc469`/`955472a` 引入，`.ai` `e39ba6d` 无。**CI 跑的是 root 新版，.ai 侧记录的是旧版**——`verify-ai-system V16` 只做语法检查不比对内容，故未发现。
- **触发路径**：维护者按文件头指引"从 .ai 真源重生成 root 副本"→ 静默删除这 2 项修复 → PG 握手超时使 MySQL 探针全跳过（v8 修复的正是此场景）、ambient 事务挂接契约探针丢失 → 方言防线退化。
- **建议**：① 以**内容更全的 root 版**为源反向同步回 `.ai/scripts/dialect-probe.sh`（修正"真源"方向）；② 在 `verify-ai-system.sh` 增 V-项：两副本除 `ROOT` 定位行外 `diff` 应为空（机械防空转，呼应"同名双源防分叉规则"）。注：`conventions.md` §同名双源规则点名同步 `review-snapshot.sh`/`refine-scan.sh`/`verify-action-items.sh`——`dialect-probe.sh` 同样适用但未纳入规则。
- **风险**：反向同步需先确认 root 的新增项在 `.ai` 环境无副作用（两者仅 ROOT 深度不同，逻辑一致）。
- **验证**：`diff <(sed 's/[[:space:]]*$//' scripts/dialect-probe.sh) <(sed 's/[[:space:]]*$//' .ai/scripts/dialect-probe.sh)` → 当前 6 处差异。✅ 已实测
- **涉及文件**：`scripts/dialect-probe.sh`、`.ai/scripts/dialect-probe.sh`、`.ai/scripts/verify-ai-system.sh`

---

## 🟠 P2 — 计划修复（26 条）

### A 组 · 质量门禁自欺（PD29 根因类，6 条）

### [ ] ITM-619 · `fix-completeness-check.sh` guard/status 两分支实测假绿 · 可信度 ✅
- **维度**：质量门禁 / 验证器自欺
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`guard` 分支（:36-46）当 `grep -rl "$KEY"` 命中 0 文件时循环体不执行、直落"✓ 全部含 KEY 的文件均有守卫覆盖"+exit 0；`status` 分支（:69-70）的 `HAS_STATUS_GUARD=$(grep -c ...)` 在 pipefail 下产出多行触发算术错误。**实测**：`bash fix-completeness-check.sh guard ZZNoSuchKeyZZZ` → 打印"完整性验证通过" exit 0（错 KEY/改名后静默放行）；`status` → `[[: 0\n0: arithmetic syntax error` 后仍"通过" exit 0。用 falsification question（"若它是无声 no-op，可观察输出会不同吗"）→ **否**。
- **建议**：guard 分支命中 0 文件时 `fail "KEY 未匹配任何文件——检查拼写或防误用"`；status 分支用 `grep -c ... || true` 取单行（或 `grep -q` 判存在性）。
- **验证**：修复后 `guard ZZNoSuchKeyZZZ` 应 exit 1；`status` 无 arithmetic error。✅ 已实测当前假绿
- **涉及文件**：`.ai/scripts/fix-completeness-check.sh`

### [ ] ITM-620 · `gate-check.sh` G23/G24 在 CI 恒 PASS（只查暂存集） · 可信度 ✅
- **维度**：质量门禁 / 跨平台守卫
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：G23（:693）/G24（:707）均以 `git diff --cached`（暂存集）为输入。CI `actions/checkout` 后零暂存 → 两门恒走 PASS 分支（no-op）。**G24 是 PLAT-1 跨平台路径守卫，CI（ubuntu）恰是它要保护的平台**——现形同虚设。
- **建议**：改为 `git diff HEAD~1 --cached` 或对 PR 用 `git diff origin/${{ base_ref }}...HEAD`；或接受"仅本地 pre-commit 生效"并在脚本头/文档标注 CI 不可用（观察态控制需 owner+过期时间，不得永久停留 no-op）。
- **验证**：本地无暂存时 G23/G24 输出 PASS（"或本次无快照变更"）——需构造暂存 diff 验证其真判定。⚠ 修复前先补探针
- **涉及文件**：`.ai/scripts/gate-check.sh`

### [ ] ITM-621 · `tech-debt-scan.sh` #20 恒 PASS（UseSqlite 存在即通过） · 可信度 ✅
- **维度**：质量门禁 / 关系型映射防线
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：`#20`（:334-345）PD26 关系型覆盖检查 `grep -rl "UseSqlite" test/ | head -1`——只要仓内任一测试用 `UseSqlite` 即非空 → `RELATIONAL_GAPS` 恒空 → 恒 PASS。PD26 防线（每个 DbContext 至少一个 SQLite 关系型测试）实际未逐 DbContext 校验。falsification → 否。
- **建议**：改为逐 DbContext 枚举（`find src -name "*DbContext.cs"` → 对每个在 test/ 找 `UseSqlite`/`EnsureCreated` 关系型用例，缺失则记 gap）。
- **验证**：删任一 DbContext 的关系型测试后 #20 应报 gap。⚠ 修复前先补探针
- **涉及文件**：`.ai/scripts/tech-debt-scan.sh`

### [ ] ITM-622 · `install-ai-system.sh:63` `read` 语法错误（安装静默跳过） · 可信度 ✅
- **维度**：可维护性 / 脚本正确性
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`read -r -t 5 || REPLY=p "是否启用…" response`——`REPLY=p "..." response` 被当命令调用。超时/EOF 时 `response` 恒空 → 静默跳过安装分支。
- **建议**：改为 `read -r -t 5 -p "是否启用…" -p 用法…… </dev/null` 或 `read -r -t 5 response </dev/null || response=n`（正确语法 + 明确默认）。注：`.ai` 已不需安装（DDD 项目），但脚本仍在册且 `verify-ai V16` 仅检语法不检运行期语义，故保留为 P2。
- **验证**：`bash -n` 通过但运行期报 `No such file or directory` exit 127。✅ 已实测
- **涉及文件**：`.ai/scripts/install-ai-system.sh`

### [ ] ITM-623 · 文档推荐 `dotnet test PalDDD.slnx` 违反 MTP 禁令（且扫描器豁免文档） · 可信度 ✅
- **维度**：文档一致性 / 命令可执行性
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`docs/conventions.md:805/809`、`docs/development.md:24/47`、`docs/testing.md:439`、`docs/performance.md:30` 推荐 `dotnet test PalDDD.slnx`；而项目自身规则（tech-debt #21 标题、`ci.yml:62` 注释、`docs/release.md:256`）明确禁止 slnx 批量（MTP 握手 → exit 5）。且 `tech-debt-scan.sh` #21 扫描器**显式排除文档**（不含 `docs/`）→ 该违规对机械防线不可见。
- **建议**：文档改为逐项目循环（对齐 `ci.yml` 写法）；`tech-debt #21` 扫描面扩展到 `docs/**/*.md` 的代码块。
- **验证**：`grep -rn "dotnet test PalDDD.slnx" docs/` → 命中（应改）。✅ 已核实
- **涉及文件**：`docs/conventions.md`、`docs/development.md`、`docs/testing.md`、`docs/performance.md`、`.ai/scripts/tech-debt-scan.sh`

### [ ] ITM-624 · 文档事实计数批量漂移（7 处口径失守） · 可信度 ✅
- **维度**：文档一致性（准则 8）
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**（逐项实测）：
  1. `docs/testing.md:405` AOT 矩阵写"显式 8 + 继承 true **22**"——实测继承 true=**14**（8+22=30≠36）。
  2. `docs/conventions.md:340` "212 源文件"——实测 **213**（`find src -name '*.cs' ! -path '*/obj/*' ...`）。
  3. `docs/testing.md:485` ArchitectureBoundaryTests "33 方法" vs 正文两处"41 方法"（实测 **41**）；`.ai/scripts/gate-check.sh:18` 文件头亦写 33。
  4. `README.en.md:15` "into 40 independent NuGet packages" vs `:122` "35 PalDDD packages"（中文版一致 35）——同文件自相矛盾。
  5. ConfigureAwait 口径四方不一：`docs/aot.md:182` "443 处"、`CHANGELOG.md:45` "179 处"、`docs/conventions.md:72` "143+ 处"、`docs/pitfalls.md:84` "143+ 处 + gate 发现 12 处违规"——实测 **444 处、零违规**。
  6. `docs/pitfalls.md:7` "architecture.md（18 项架构决策）"——architecture.md 全文无该表（grep "18" 零命中）。
  7. `docs/pitfalls.md:105`（T6）"PDDD-G8 当前发现 ISpecification.cs:218 真实违规待修复"——现状 `ISpecification.cs:269` 已带 `[RequiresDynamicCode]`、G8 PASS（行号已漂移）。
- **建议**：逐处刷新为实测值；在 `doc-consistency-check.sh` 增机械项：源文件数、boundary 方法数、AOT 三态计数、ConfigureAwait 计数——与 README/docs 声称值比对（防止再次漂移）。
- **验证**：各条已附实测命令（见上）。✅ 已实测
- **涉及文件**：`docs/testing.md`、`docs/conventions.md`、`docs/aot.md`、`docs/pitfalls.md`、`README.en.md`、`CHANGELOG.md`、`.ai/scripts/gate-check.sh`

### B 组 · 生产代码健壮性（src，10 条）

### [ ] ITM-625 · `PublicApiSnapshotTests` 不 dump 字段/常量——公共静态字段零守护 · 可信度 ✅
- **维度**：生成语义流 / 公共 API 契约
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`BuildSnapshot()`（`PublicApiSnapshotTests.cs:60-91`）只 dump ctor/property/method，**不含 field/const/static readonly**。实测快照 `core-packages-public-api.txt` 中 `^  (field|operator|event) ` 行数 = **0**，`PalDDD.Core.Diagnostics.PalMetrics`（约 20 个 `public static readonly Counter<long>`）与 `PalActivitySource.Name/Version/Source` 在快照里只剩空类名、零成员守护，全仓无其他测试覆盖。
- **触发路径**：重命名/删除任意公共 const 或 static readonly（如 `PalMetrics.EventHandlersFailed`）→ 快照测试仍绿 → 二进制兼容性破坏无告警（结合 G23 只查快照文件是否变更，字段变更连快照都不受影响）。
- **建议**：`BuildSnapshot` 补 `GetFields(BindingFlags.Public|Static|Instance|DeclaredOnly)`（含 `IsLiteral`/`IsInitOnly` 标记）；重新生成基线并人工评审 diff（`PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1`）。
- **验证**：探针——为 BuildSnapshot 增补 field 行后重生成快照，应出现 `PalMetrics` 字段行。✅ 已实测快照零 field
- **涉及文件**：`test/PalDDD.Core.Tests/PublicApiSnapshotTests.cs`、`test/PalDDD.Core.Tests/Snapshots/core-packages-public-api.txt`

### [ ] ITM-626 · `AddGeneratedMessages` 全仓零接线——生成物无端到端测试 · 可信度 ✅
- **维度**：生成语义流
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：`MessageRegistryGenerator` emit 的 `PalMessageCatalog.AddGeneratedMessages(builder, jsonContext)`（`MessageRegistryGenerator.cs:298`）在全仓（src/test/samples/docs）**零调用点**；所有样本/测试手写 `MessageCatalogBuilder`。生成物是"孤儿 API"——单测用 Roslyn driver 断言生成源码，但**无集成测试证明生成物能被 `AddPalJsonSerialization` 消费**。若用户 context 缺 `[JsonSerializable]`，运行时抛 `InvalidOperationException("Missing JsonTypeInfo")`，编译期无守卫。
- **建议**：补一个 samples/ 或测试用例走完整链（`[GenerateMessage]` → 生成器 → `AddGeneratedMessages` → 序列化 roundtrip）；或在文档明确"生成物需手动接线"契约。
- **验证**：`grep -rn "AddGeneratedMessages" -- .` → 仅生成器自身 1 处（定义）。✅ 已实测
- **涉及文件**：`src/PalDDD.Core.SourceGen/MessageRegistryGenerator.cs`、`samples/`、`test/PalDDD.Core.Tests/MessageRegistryGeneratorTests.cs`

### [ ] ITM-627 · `Command Dispatch`/`Saga Transition` 两个 Activity 无发射点，README 声称已发射 · 可信度 ✅
- **维度**：可观测性 / 文档一致性
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`PalActivitySource.StartCommandDispatch`（`PalDiagnostics.cs:46`）与 `StartSagaTransition`（:57）全仓**零调用点**（仅定义处）；但 `README.md:650/652`、`README.en.md`、`docs/architecture.md:289` 声称"Dispatcher.SendAsync → Activity 'Command Dispatch'"、"SagaProcessor → Activity 'Saga Transition'"。平台能力文档高估。
- **建议**：二选一——① 在 `Dispatcher.SendAsync`/`SagaProcessor` 接入 Activity（补 2 处埋点 + 测试）；② 修正 README/docs 移除未发射的 Activity 声明，并在 `PalDiagnostics.cs` 对两方法加"保留供外部消费/未接线"声明（对比 :69-70 的既有保留声明）。
- **验证**：`grep -rn "StartCommandDispatch\|StartSagaTransition" -- 'src/**/*.cs'` → 仅 2 处定义。✅ 已实测
- **涉及文件**：`src/PalDDD.Core/PalDiagnostics.cs`、`README.md`、`README.en.md`、`docs/architecture.md`

### [ ] ITM-628 · `KafkaBroker` 分区 EOF 结果 `Message == null` → NRE 日志洪水 · 可信度 ✅
- **维度**：错误流
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`KafkaBroker.cs:250` 只判 `result.Message.Value is null`（tombstone），未判 **`result.Message` 本身为 null**。Confluent.Kafka 官方 XML 明确 `ConsumeResult.Message` 在分区 EOF 事件（`EnablePartitionEof=true`）为 null → `:250` 解引用 NRE → 落 `:287 catch(Exception)` 记 "Failed to handle … message" 假错误日志并继续循环（日志洪水/语义误导）。
- **触发路径**：`new KafkaBroker(producerConfig, new ConsumerConfig{ EnablePartitionEof = true, ... }, ...)` → 消费到分区末尾 → `IsPartitionEOF=true, Message=null` → NRE。
- **建议**：`:250` 前加 `if (result.Message is null) { /* partition EOF, continue */ continue; }`；并核对 `EnablePartitionEof` 是否需在 `KafkaBroker` 构造校验/覆盖。
- **验证**：探针——构造含 `EnablePartitionEof=true` 的消费者（或单测注入 `ConsumeResult` mock）复现 NRE。⚠ 修复前先补探针
- **涉及文件**：`src/PalDDD.Messaging.Kafka/KafkaBroker.cs`

### [ ] ITM-629 · `IdentityGenerator` Ulid JsonConverter 直用 `ValueSpan`——多段序列抛 IOE · 可信度 ✅（可达性❓）
- **维度**：生成语义流 / 错误流
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：`IdentityGenerator.cs:706` Ulid 非转义快路径直接 `reader.ValueSpan`，无 `HasValueSequence` 守卫。`Utf8JsonReader.ValueSpan` 契约：token 落在单段内或 reader 由 `ReadOnlySpan<byte>` 构造时才有效；多段（`ReadOnlySequence`）下抛 **`InvalidOperationException`**（非 `JsonException`），绕过上层 `catch (JsonException)`。同方法其他分支（Guid/int/long 用 `GetString`/`TryGetInt32`；escaped 腿用 `GetString`）均不受影响——Ulid 非转义腿是唯一破约点。
- **触发路径**：多段 `ReadOnlySequence<byte>`（字符串 token 跨段）→ `JsonSerializer.DeserializeAsync` → Ulid 字段 → IOE 逃逸 → 上层（如 `EndpointExtensions.MapCommand` 的 `catch (JsonException)`）映射 400 失效 → 500。
- **建议**：非转义腿加 `reader.HasValueSequence ? GetString 回退 : ValueSpan` 分支（v33/v34 只补了 FormatException→JsonException，未覆盖多段）。
- **验证**：探针——构造跨段的 `ReadOnlySequence<byte>` reader 调 converter。⚠ 可达性需探针
- **涉及文件**：`src/PalDDD.Core.SourceGen/IdentityGenerator.cs`

### [ ] ITM-630 · tutorial 的 `AppOutboxDbContext` 示例缺抽象成员，照抄编译失败 · 可信度 ✅
- **维度**：文档一致性 / 示例可编译性
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`docs/tutorial.md:466-472` 示例 `sealed class AppOutboxDbContext : OutboxDbContext { public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>(); }` **未实现基类抽象成员** `LeasePendingMessagesAsync`（`src/PalDDD.Transactions.EFCore/OutboxDbContext.cs:90` 为 `public abstract`）→ CS0534 编译失败。
- **触发路径**：用户复制教程 4.6 节代码 → 编译失败；若绕过（空实现/退化）→ 失去原子租约语义（多实例重复发布，docs/pitfalls E2 场景）。
- **建议**：示例补 `LeasePendingMessagesAsync` 实现（或改为继承方言基类 `PostgreSqlOutboxDbContext`/`SqlServerOutboxDbContext` 等已实现具体方言）。
- **验证**：对照 `OutboxDbContext.cs:90` 的 abstract 声明。✅ 已实测
- **涉及文件**：`docs/tutorial.md`、`src/PalDDD.Transactions.EFCore/OutboxDbContext.cs`

### [ ] ITM-631 · `SqlServerOutboxDbContext` 标 `[Obsolete]` 未验证，但 docs 列为一级支持方言 · 可信度 ✅
- **维度**：发布面一致性 / AOT-免
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：`SqlServerOutboxDbContext.cs:11` 显式 `[Obsolete("零测试覆盖的未验证方言基类……v3.0 移除")]`，类注释自述"全仓无 SqlServer 测试容器/断言、无 src 消费"。但 `docs/architecture.md:305`（列 SQL Server 为四方言之一）、`docs/usage.md:215/217`、`docs/pitfalls.md:37/126`、`docs/tutorial.md`（6 处 `UseSqlServer`，教程唯一方言示例）均把 SQL Server 当一等支持面。
- **建议**：二选一——① 若 SQL Server 非支持面，docs 全面标注"未验证/实验性"并从教程移除（教程改用已验证方言）；② 若保留支持，补 SQL Server 测试（Testcontainers `mcr.microsoft.com/mssql/server`）并解除 Obsolete。
- **验证**：`grep -rn "UseSqlServer" docs/` 命中教程 6 处；读 `SqlServerOutboxDbContext.cs:11` Obsolete。✅ 已实测
- **涉及文件**：`src/PalDDD.Transactions.EFCore/SqlServerOutboxDbContext.cs`、`docs/architecture.md`、`docs/usage.md`、`docs/pitfalls.md`、`docs/tutorial.md`

### [ ] ITM-632 · `ProjectionCheckpointDbContext` 四写路径 OCE 逃逸不 Detach → 幽灵租约 · 可信度 ✅
- **维度**：资源流 / 错误流
- **优先级**：P2 · 危害: 高 · 复杂度: 中
- **问题**：四个写路径（:96-118, 136-155, 185-206, 277-297）的 try 只捕获 `DbUpdateConcurrencyException`/`DbUpdateException`，**未捕获 OCE 及其他异常**。实体在进入 try 前已被 `MarkProcessing/MarkCompleted/MarkFailed` 变异（内存已改），异常逃逸时既不 Detach 也不回滚内存值 → 滞留 ChangeTracker → 同 DbContext 后续任意 `SaveChangesAsync` 把"从未成功获取的幽灵租约/幽灵终态"落库。
- **触发路径**：长驻 scope 调 `TryStartAsync(..., cancelledToken)` → OCE 逃逸（EF 在 DetectChanges 前 `ThrowIfCancellationRequested`）→ checkpoint 仍 tracked 且内存为 Processing/LeaseUntil → 后续无关 `SaveChangesAsync` 触发 DetectChanges → UPDATE 落库（Revision 尚为 DB 原值故恒匹配）→ 投影位被锁死至 LeaseDuration。
- **建议**：`catch` 扩到 `Exception`（OCE 也 Detach/回滚内存），或 `finally` 中按"保存未成功则还原实体状态 + Detach"。姊妹 `IdempotencyDbContext`（:170-190 等三处）、`SagaStateDbContext`（:133-157）同型，需一并核对。
- **验证**：探针——SQLite:memory + 已取消 token 调 `TryStartAsync`，再调 `SaveChangesAsync()`，断言行存在且 Status=Processing 即成立。⚠ 修复前先补探针
- **涉及文件**：`src/PalDDD.Projections.EFCore/ProjectionCheckpointDbContext.cs`、`src/PalDDD.Idempotency.EFCore/IdempotencyDbContext.cs`、`src/PalDDD.Transactions.EFCore/SagaStateDbContext.cs`

### [ ] ITM-633 · `DefaultSagaManager.ResumeAsync` 不落库——决策效果可能丢失 · 可信度 ✅（契约归属❓）
- **维度**：并发流 / 架构契约
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：`DefaultSagaManager.ResumeAsync`（:62-119）返回 `ValueTask`（无状态），决策派发经闭包 `ProcessEventAsync(current, decision, ct)` 作用于**中断时捕获的实例**；默认实现不做持久化，返回值无状态可供调用方保存。`ISagaManager` 文档未声明"恢复后须由调用方自行保存该实例"。
- **触发路径**：应用 `saga.ProcessEventAsync(loaded, evt, ct)` 产生 AWD 并存库 → 之后 `manager.ResumeAsync(sagaId, decision, ct)` 在闭包捕获的 loaded 上推进 → 返回值无状态可保存 → 若未额外持有该实例引用再存一次，store 中该 Saga 仍为 AWD，决策效果丢失。另：中断条目为进程内字典，多实例部署下决策须路由到中断发生的实例。
- **建议**：明确契约——`ResumeAsync` 应返回恢复后的状态/或内部持久化；至少在 XML doc 显式声明"调用方负责保存"，并在测试锁定。
- **验证**：探针——内存 SagaManager + InMemory/EfCore store，Resume 后 `store.GetByIdAsync` 断言 Status。⚠ 契约归属需主线程裁决
- **涉及文件**：`src/PalDDD.Transactions/Saga/DefaultSagaManager.cs`、`src/PalDDD.Transactions/Saga/ISagaManager.cs`

### [ ] ITM-634 · `DapperOutboxStore.created_at` 用 Store 时钟——跨栈语义分叉 · 可信度 ✅
- **维度**：生成语义流 / 多实现契约一致性
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`DapperOutboxStore.cs:197/219/230` 的 `created_at` 用 Store 时钟覆盖（单条 `_timeProvider.GetUtcNow()`、批量批次起始 `now`）；PalORM（`OutboxMessageRow.FromDomain`）、EFCore（`OutboxMessages.Add(message)`）、InMemory（列表直存）均保留领域赋值的 `OutboxMessage.CreatedAt`。同消息跨栈落库 `created_at` 不同 → `ORDER BY created_at` 投递序与"CreatedAt 往返"断言分叉。
- **建议**：Dapper 栈改为持久化 `message.CreatedAt`（对齐三栈），或反向统一（需 ADR）。
- **验证**：探针——构造 `CreatedAt=T0` 的消息经 Dapper 栈落库，读回断言 == T0。⚠ 修复前先补探针
- **涉及文件**：`src/PalDDD.Dapper/DapperOutboxStore.cs`

### C 组 · 多实现/双管线对称（4 条）

### [ ] ITM-635 · `PostgreSqlMultiHost` 零副本分支绕过全部 Host 列表校验 · 可信度 ✅
- **维度**：错误流 / 姊妹对称
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`AddPalNpgsqlDataSourceWithReadWriteSplit`（:210-228）的零副本分支（`replicaConnectionStrings.Length == 0`）直接 `soloBuilder.Build()`，**未调用**同文件的 `EnsureNoBlankHostEntries`（:343）/`EnsureNoDuplicateHost`（:359）。而语义等价的两处单主机入口（`PostgreSqlServiceCollectionExtensions.cs:63/65/111/113`）已 fail-fast。
- **触发路径**：`AddPalNpgsqlDataSourceWithReadWriteSplit("Host=pg1,,pg2;Database=pal", [])` → 空段成为"参与轮询的死节点，故障转移静默失败"（框架自身注释定性）。同串传 `AddPalNpgsqlDataSource` 却抛异常 → 同配置两入口行为分叉。
- **建议**：零副本分支接入 `EnsureNoBlankHostEntries` + `EnsureNoDuplicateHost`。
- **验证**：`grep -n "EnsureNoBlankHostEntries\|EnsureNoDuplicateHost" src/PalDDD.Dapper.PostgreSql/PostgreSqlMultiHost.cs` → 确认零副本分支无调用。✅ 已实测（PD17 姊妹枚举）
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlMultiHost.cs`

### [ ] ITM-636 · `PostgreSqlOutboxNotifier` LISTEN/NOTIFY 未加引号——通道名大小写折叠 · 可信度 ✅
- **维度**：错误流 / SQL 安全
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`PostgreSqlOutboxNotifier.cs:138/248` 用 `$"LISTEN {_channelName}"` / `$"NOTIFY {_channelName}"` 拼接**未加引号标识符**；PG 对未引号标识符小写折叠，而 `pg_notify(text,...)` 通道参数是大小写敏感 text。`ValidateChannelName`（:103-115）明确允许大写字母（`IsIdentifierStart` 含 `A-Z`）。
- **触发路径**：注册 `channelName: "OutboxChannel"`（构造不拦大写）→ `LISTEN OutboxChannel` 折叠为 `outboxchannel`；用户按文档写触发器 `pg_notify('OutboxChannel', ...)` → 消息落通道 `OutboxChannel`，LISTEN 侧永收不到 → `WaitAsync` 一直阻塞，唤醒全靠 PeriodicTimer 兜底（未注册 OutboxProcessor 时 Outbox 停摆）。
- **建议**：标识符加双引号（`LISTEN "{_channelName}"`），或 `ValidateChannelName` 拒绝大写（限定小写+下划线）。
- **验证**：读 :103-115 白名单（含 A-Z）+ :138/248 无引号拼接。✅ 已实测
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlOutboxNotifier.cs`

### [ ] ITM-637 · `PostgreSqlReadWriteRouter` 用实例注册，容器不释放 → 连接池泄漏 · 可信度 ✅
- **维度**：资源流
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`PostgreSqlReadWriteRouter.cs:263-267` 的 XML doc 声称"DI 注册为 Singleton 时容器自动调用 Dispose……否则连接泄漏"，但注册用 `services.AddSingleton(router)`/`AddSingleton(writer)`（**ImplementationInstance**）。MS.DI **不释放容器未创建的对象** → `DisposeAsync` 永不执行，writer/reader 的 `NpgsqlDataSource` 连接池在宿主 Dispose 时静默泄漏。
- **触发路径**：`AddPalReadWriteRouter(primary, replicas)` → `provider.DisposeAsync()` → router/WRITER/READER 的 `DisposeAsync` 均不被调用（子代理探针实测 `Disposed=False`；改工厂重载 `AddSingleton<T>(_ => new T())` 则被调用）。
- **建议**：改为工厂注册 `AddSingleton(sp => new PostgreSqlReadWriteRouter(...))`；或注册 `IHostedService`/`IAsyncDisposable` 显式 dispose。
- **验证**：探针——`AddSingleton(instance)` 后 `provider.DisposeAsync()` 断言 `Disposed` 标志。✅ 子代理已实测
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlReadWriteRouter.cs`

### [ ] ITM-638 · Inbox/Checkpoint `MarkFailed` 截断常量分叉（2040 vs 2000） · 可信度 ✅
- **维度**：多实现契约一致性 / 双管线姊妹（PD24）
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：Dapper 侧 `DapperInboxStore.cs:171`、`DapperProjectionCheckpointStore.cs:179` 用 `FailureReason.Truncate(..., 2040)`；PalORM/EFCore 侧 `PalOrmInboxStore.cs:216`、`PalOrmProjectionCheckpointStore.cs:159`、`ProjectionCheckpointDbContext.cs:184`、`InboxDbContext.cs:191` 用 `FailureReason.Normalize(...)`（截 2000 + 空白归一）。`error` 列 `HasMaxLength(2048)`，两值均在界内（无超列风险），但**同输入跨栈存储值不同**。Outbox 族已统一 2040（`sibling-map.md:31` 种子表登记的未收敛实例）。
- **建议**：Inbox/Checkpoint 族统一为 `FailureReason.Normalize`（或统一 2040），消除跨栈观测差异。
- **验证**：`git grep -n "Truncate.*2040\|Normalize" -- 'src/**/*Store.cs' 'src/**/*DbContext.cs'`。✅ 已实测（PD24 种子表命中）
- **涉及文件**：`src/PalDDD.Dapper/DapperInboxStore.cs`、`src/PalDDD.Dapper/DapperProjectionCheckpointStore.cs`、`src/PalDDD.PalORM/Stores/*`、`src/PalDDD.Transactions.EFCore/InboxDbContext.cs`、`src/PalDDD.Projections.EFCore/ProjectionCheckpointDbContext.cs`

### D 组 · 其余生产代码 P2（3 条）

### [ ] ITM-639 · `RabbitMqBroker` mandatory 依赖调用方启用 confirms——消息可能静默丢失 · 可信度 ⚠
- **维度**：错误流
- **优先级**：P2 · 危害: 高 · 复杂度: 中
- **问题**：`RabbitMqBroker.cs:105-115` 用 `mandatory:true`，但依赖调用方在注入的 `IChannel` 上启用 PublisherConfirmation tracking（:107-108 注释已自认）；本包无 DI/工厂入口替调用方启用。未启用时 `BasicPublishAsync` 不等 basic.return 即返回成功 → 发布到无绑定队列的 exchange → `OutboxBatchProcessor` MarkProcessed → 消息永久丢失且标记已处理。
- **建议**：在 broker 侧显式启用 confirms（或文档强制要求 + 构造校验 `channel` 能力）。
- **验证**：探针——注入未启用 confirms 的 channel 发布到无绑定 exchange，观察是否静默成功。⚠ 修复前先补探针
- **涉及文件**：`src/PalDDD.Messaging.RabbitMQ/RabbitMqBroker.cs`

### [ ] ITM-640 · `OutboxDomainEventInterceptor` 在 `AddDbContextPool` 下作用域捕获失效 · 可信度 ❓
- **维度**：并发流
- **优先级**：P2 · 危害: 高 · 复杂度: 中
- **问题**：拦截器为 Scoped 且持有非线程安全可变状态（`_pending`/`_injectedOutboxIds`），类注释称"Scoped 保证单请求独占"。该前提仅在**非池化** `AddDbContext` 下成立；若用 `AddDbContextPool`，`sp` 解析出的首个 scoped 实例被烘焙进池化 context 的 options → 所有 context 共享同一实例 → 并发请求交叉读写 → 领域事件漏写/重复写、失败路径误 Detach 他请求注入的行。
- **建议**：文档明确"禁止 `AddDbContextPool` + `UsePalOutboxInterceptor(sp)` 组合"，或在拦截器内改用 context 级状态（`ChangeTracker`/`DbContext` 关联）替代实例字段。
- **验证**：探针——pool 下双 scope 并发断言 `PendingEvents` 隔离与 outbox 行数。⚠ 修复前先补探针（PD32 同类）
- **涉及文件**：`src/PalDDD.Repository.EFCore/OutboxDomainEventInterceptor.cs`

### [ ] ITM-641 · `PostgreSqlReportHelper` `DateOnly`/`TimeOnly`/数组 落 `Convert.ToString` · 可信度 ✅
- **维度**：生成语义流
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`PostgreSqlReportHelper.cs:243-291` 的 `WriteJsonValue`/`FormatCsvValue` switch 未含 `DateOnly`/`TimeOnly`/数组（PG `date`/`time`/`text[]` 默认映射），落入 `Convert.ToString(value, InvariantCulture)`。该转换对未实现 `IConvertible` 的类型**忽略 provider 回退 `value.ToString()`（当前区域性）**——`DateOnly` 在 zh-CN 宿主输出 `2025/1/2`（跨宿主不确定），数组输出 `System.String[]`（ITM-114 同类漏网）。
- **建议**：switch 补 `DateOnly`（`yyyy-MM-dd`）、`TimeOnly`、`IEnumerable`（元素递归）分支。
- **验证**：探针——`ExportCsvAsync("SELECT recorded_on, tags FROM …")` 观察单元格值。✅ 已读完整方法 + 核实 Npgsql 10 类型映射
- **涉及文件**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlReportHelper.cs`

### E 组 · 测试与防线（6 条）

### [ ] ITM-642 · `OutboxRequeueTests` 只测 InMemory——三栈 `RequeueDeadAsync` 零行为测试 · 可信度 ✅
- **维度**：测试覆盖
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**：`OutboxRequeueTests.cs:4-12` 文件头声称覆盖 `IPalOutboxStore.RequeueDeadAsync` 的 ADR-011 语义，实际只测 `InMemoryOutboxStore`。`DapperOutboxStore.RequeueDeadAsync`（:304）、`PalOrmOutboxStore.RequeueDeadAsync`（:317）、`OutboxDbContext.RequeueDeadAsync`（:293）**零行为测试**。删掉 Dapper 版 Status 守卫（允许 Processed→Pending）本套件全绿。
- **建议**：补三栈的 `RequeueDeadAsync` 测试（Dead→Pending、非 Dead 拒绝、RetryCount 保留、Error 审计串、retriedBy 空白抛）。
- **验证**：`git grep -n "RequeueDeadAsync" -- 'test/**/*.cs'` → 仅 InMemory 断言。✅ 已实测
- **涉及文件**：`test/PalDDD.Transactions.Tests/OutboxRequeueTests.cs`、`test/PalDDD.Integration.Tests/`、`test/PalDDD.PalORM.Tests/`

### [ ] ITM-643 · `ArchitectureBoundaryTests` 守卫正则恒不匹配（死代码） · 可信度 ✅
- **维度**：质量门禁 / 生成语义流
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`ArchitectureBoundaryTests.cs:891` 的 `DependencyInjectionMethods_MustStartWithAddPalPrefix` 正则 `@"public static .* IServiceCollection ([A-Za-z]+)\("` 对合法 C# 恒不匹配——合法签名 `public static IServiceCollection AddPalDDD(this IServiceCollection services)` 中 `([A-Za-z]+)\(` 只能匹到 `services)`，`\(` 失配。实测该文件命中数 `grep -c` = **0**（BRE/ERE/PCRE 三口径均 0），全项目 `PalDDD.DependencyInjection` 同 0。守卫体从未执行。把 `AddPalDDD` 改名 → 测试仍绿。
- **建议**：修正正则为 `@"public static IServiceCollection ([A-Za-z_][A-Za-z0-9_]*)\s*\("`（捕获方法名），并补一条负向自证测试（镜像同文件 `TestMethodPattern_Detects…` 形态，确保守卫能失败）。
- **验证**：`grep`/regex 实测 0 命中。✅ 已实测（Python re 复现）
- **涉及文件**：`test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`

### [ ] ITM-644 · `PalOrmSagaMultiDialectTests` JSON 案例缺 MySQL 方言 · 可信度 ✅
- **维度**：测试覆盖
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`PalOrmSagaMultiDialectTests.cs:161-186` 的 `Test_WithJsonTypeInfo_PreservesBusinessFields` 只有 SQLite + PostgreSQL 两个 `[Test]`，**缺 MySQL**；同类其余矩阵（InsertNew/GetActiveSagas/LeaseActiveSagas/StaleVersion）三方言齐全，`CreateMySqlStoreAsync()` 工厂已存在可用。MySQL `saga_data` JSON 列业务字段往返回归无测试。
- **建议**：补 MySQL `[Test]` 用例。
- **验证**：读该文件 :161-186 仅 2 个 `[Test]`。✅ 已实测
- **涉及文件**：`test/PalDDD.PalORM.Tests/PalOrmSagaMultiDialectTests.cs`

### [ ] ITM-645 · 测试名实不符批量（≥8 处名声称 ≠ 实断言） · 可信度 ✅
- **维度**：测试真伪（PD29 近亲）
- **优先级**：P2 · 危害: 中 · 复杂度: 中
- **问题**（逐项实测，名声称 A 实断言 B）：
  1. `PalOrmUnitOfWorkTests.cs:64-71` `ExecuteInTransactionAsync_CommitsOnSuccess` 只断言 `executed=true`——从未验证事务真提交（改为"不开事务直接调委托"仍绿）。
  2. `PalOrmResilienceWiringTests.cs:79-94` `..._CallbackDisposesOpenedSession` 只断言 `Throws<InvalidOperationException>`——注释自认"释放无法直接断言"（删 `session.DisposeAsync()` 仍绿）。
  3. `ServiceRegistrationTests.cs:89-101` 名"不清除用户已配置 Provider"只断言 `ILoggerFactory != null`（改回 `ClearProviders()` 仍绿）。
  4. `ServiceRegistrationTests.cs:177-189` 名含 "AsScoped" 只断言解析类型（改 Singleton 仍绿）；命令版有跨 scope 对照，查询版不对称。
  5. `ArchitectureBoundaryTests.cs:1049` `PerformanceContract_FrozenDictionaryAndPipelineStateMachineAndRefStruct` 全文无 FrozenDictionary 断言（改 `.ToDictionary()` 仍绿）。
  6. `ArchitectureBoundaryTests.cs:1002-1042` 名 "Triple" 实断言 `underscoreCount < 1`（仅 ≥1）。
  7. `SourceGeneratorDirectTests.cs:471-494` `..._BothGenerate` 唯一断言是"无 PALID004"（未验证两个 FooId 都产出）。
  8. `SourceGeneratorDirectTests.cs:377-388` 第二语法树缺 `using`（CS0246），通过原因非"两树干净编译"。
- **建议**：逐条把断言收紧到与名称一致的行为（如提交测试加"委托内写行 + 调用后断言行数"；FrozenDictionary 断言加 `FrozenDictionary` 类型/行为检查）；或按实际断言**改名**（诚实命名）。
- **验证**：各条已读完整方法体。✅ 已实测
- **涉及文件**：`test/PalDDD.PalORM.Tests/PalOrmUnitOfWorkTests.cs`、`test/PalDDD.PalORM.Tests/PalOrmResilienceWiringTests.cs`、`test/PalDDD.DependencyInjection.Tests/ServiceRegistrationTests.cs`、`test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs`、`test/PalDDD.Core.Tests/SourceGeneratorDirectTests.cs`

### [ ] ITM-646 · `BrokerIntegrationTests` 断言 `count == 5` 与 at-least-once 语义冲突 · 可信度 ⚠
- **维度**：测试 flaky
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`BrokerIntegrationTests.cs:452/637` 多消息测试断言 `snapshot.Count == 5`（Kafka/Rabbit 两处），broker 为 at-least-once；`done` 在首次达 5 时触发，其后到快照前若有重投递，计数变 6 → 假红。
- **建议**：改 `Count >= 5`（或去重后判唯一消息集）。
- **验证**：探针——N=50 次循环观察是否出现 count>5。⚠ 修复前先补探针
- **涉及文件**：`test/PalDDD.Messaging.Integration.Tests/BrokerIntegrationTests.cs`

### [ ] ITM-647 · `EventLogTests` 无 `[NotInParallel]` 但用进程级 Meter/Activity 监听器 · 可信度 ⚠
- **维度**：测试污染 / 验证器自欺
- **优先级**：P2 · 危害: 中 · 复杂度: 易
- **问题**：`EventLogTests.cs:8/37-67/213-233` 无 `[NotInParallel]`（仓库同型指标断言类均加，如 `MessagingTests.cs:114`），却用进程级全局 `RecordingActivityListener`/`RecordingMeterListener`；同程序集 `EventLogEfCoreTests` 也发同名 activity（`event_count=2`）与同指标值 2 且同样无隔离。`listener.Measurements).Contains(2)` 可能由并行测试的发射满足。
- **建议**：加 `[NotInParallel]`（无 key，全序列化）——对齐 `ProjectionTests.cs:9` 的仓库正确范式。
- **验证**：`grep -n "NotInParallel" test/PalDDD.EventLog.Tests/EventLogTests.cs` → 0。⚠ 修复前先补探针
- **涉及文件**：`test/PalDDD.EventLog.Tests/EventLogTests.cs`

---

## ⚪ P3 / 评估 — 汇总（34 条，进 `action-items-p3-backlog.md`）

> 低危害项，30 天老化升 P2。逐条详述见报告附录 4.4；此处仅列名目与位置。

**代码健壮性（P3）**
1. `Saga.Compensation` 首用快照策略——运行期改 `CompensationPolicy` 被静默忽略（`Saga.cs:80-84`）
2. `SagaManager` XML doc 称"可由 DI 注入"但全仓无 `ISagaManager` 注册（`Saga.cs:159/171`）
3. `SqliteJsonExtensions` 文档漏方括号约束（`:39-45`）
4. `PostgreSqlJsonbExtensions` `EscapeLiteral` 未处理反斜杠（`standard_conforming_strings=off` 下不安全，默认安全）
5. `PostgreSqlPipeline.Add` 无 null/空守卫（`:56-62`）
6. `ProjectionCheckpointDbContext.ExecuteDeleteAsync` 绕过 ChangeTracker（`:221-232`，与 ITM-632 同源）
7. `AddPalPostgreSqlOutboxNotifier` 无去重（重复 `IHostedService`，`PostgreSqlServiceCollectionExtensions.cs:176`）
8. `PalOrmSagaStateStore` Error 截断在保存结果未知前变异调用方对象（`:193`，姊妹已修）
9. ThreadStatic 池非重入（`JsonMessageSerializer.cs:40-44`，已声明限制）
10. `NpgsqlDataSource` 追加注册不释放（`PostgreSqlServiceCollectionExtensions.cs:80/88`）
11. `PALENUM001` 文案未含类型过滤条件（`EnumGenerator.cs:24-34`）
12. `events.metadata` 可空列无 NULL 兜底（`StoredEvent.cs:113` / `PalOrmEventLog.cs:270`）
13. `SqlErrorClassifier` AOT-true 项目内反射（已 `UnconditionalSuppressMessage` 安全降级）
14. `ISagaManager` 含 internal 成员——程序集外不可实现（`:53-59`）
15. `EventData` 注释称"不可变"但 `StoredEvent.From` 共享 byte[] 别名（三方一致）
16. `Dispatcher.Register` XML doc 称"Freeze 后抛 ODE"但空表 Freeze 不置冻结位（`Dispatcher.cs:88-90`）
17. `HandlerEntry.HandlerType/ResponseType` 零读取、`Register` 参数静默忽略（`Dispatcher.cs:94-115`）
18. `docs/sql/*/000_schema.sql` `projection_checkpoints.status` 注释与枚举错位（注释 Completed=2，代码 Completed=1）
19. `PostgreSqlSoftDelete.Escape` 不支持 schema 限定名（`:103`）
20. `UlidStringConverter.FromProvider` 严格 Parse 抛 FormatException（姊妹 TryParse 降级，`:21`）
21. `InMemorySagaStateStore.GetActiveSagasAsync` 返回活引用绕过 Version fencing（`:33-38`）
22. `PostgreSqlReadWriteRouter.DisposeAsync` catch 排 OCE 跳过 Reader 释放（`:78-92`）
23. `KafkaBroker` Dispose 与 Publish 竞态（`:65` vs `:359-389`，注释自认）
24. `FanOutStep` `_executor` 自身抛非 linked OCE 逃逸（`:163-175`）
25. `PostgreSqlSharding.ShardedDataSourceManager.DisposeAsync` 吞 OCE（`:238-254`）
26. `EndpointExtensions.MapQuery` null query 无守卫（`:226/241`，与 MapCommand 不对称）
27. `MySqlPerformanceOptimizer` `SET SESSION` 对池连接不持久（`:52-58`，`ConnectionReset=true`）
28. `MySqlMultiHost` SslMode 未纳入一致性校验（`:69-76/469-480`）
29. `OutboxDbContext` catch `InvalidOperationException` 过宽（`:116-149`）
30. `SqliteRowFactory`/`SqliteTypeHandlers` `new DateTimeOffset(dt, Zero)` 对 `Kind=Local` 抛（`:86`/`:89`）
31. `MemoryPackMessageSerializer` 类摘要声称"AOT 兼容"与项目 `IsAotCompatible=false` 矛盾（`:17`）
32. `ProjectionCheckpoint` 三变更方法不校验负 timeout（`:78-103`，守卫在 Store 层）

**测试与文档 P3**
33. `DiagnosticCoverageGateTests` 形态判定偏宽（`.Id` 成员访问即算覆盖，`:119-146`）；`PublicApiSnapshotTests` 金标自更新变量泄漏风险（`:49-53`）；`EventLogPositionReserverTests` 用伪异常类（`:136-172`）
34. 文档 P3 批：`docs/testing.md:485` 方法数、`docs/release.md` Compression 分层、`scripts/changelog-check.sh:103` 硬编码、`ci-coverage.sh:65` set -e、`scripts/check-all.sh:28` ERROR_COUNT 未用、`docs/migration/net11.md:49` 失效路径、`docs/tutorial.md:677` OrderId 载体、`CHANGELOG.md:491` release.yml 段落、`Directory.Build.props:40` NoWarn 23 vs CHANGELOG 21、`docs/usage.md:86` Dispatcher 前提、`docs/test-coverage-baseline.md` 850 vs 1202、`MessagingTests.cs:98/139` 拼写 Dispatches

---

## 修复顺序建议（按"止损优先"）

| 序 | ITM | 理由 |
|:--:|-----|------|
| 1 | ITM-616 / ITM-615 | 安全声明失实 + CI 零防线——最易被"假绿"掩盖 |
| 2 | ITM-618 | 双副本漂移会随下次"同步"静默删修复 |
| 3 | ITM-619/620/621/622 | 门禁假绿四连——修完前，其它绿信不可全信 |
| 4 | ITM-625/626/627 | 生成/可观测面零守护——影响面广 |
| 5 | ITM-628/632/637/639/640 | 运行时缺陷（NRE/幽灵写/泄漏/丢消息） |
| 6 | ITM-617/624/623/630/631 | 文档一致性批 |
| 7 | ITM-629/633/634/635/636/638/641 | 其余 P2 |
| 8 | ITM-642～647 | 测试防线补强 |
| 9 | P3 汇总 | 进 backlog，30 天老化 |

## 验收标准（本轮修复完成后）

- [ ] 四道门禁（fix-completeness / G23 / G24 / tech-debt#20）均通过"已知坏输入"验证能失败（mutation probe 三步）
- [ ] `grep` 旧事实值（21 ADR / 212 源文件 / 33 方法 / 40 包 / 22 继承 / 143+/179/443 处）全仓库零残留
- [ ] 两副本 `dialect-probe.sh` 除 ROOT 行外 `diff` 为空
- [ ] 新增 P1/P2 修复均带复现测试（修复门两问①）
- [ ] 修复后三轴复跑：机械轴（gate+verify-ai+tech-debt 全绿）+ 静态轴（地毯零新 P0-P2）+ 实测轴（方言探针真跑或 CI）
