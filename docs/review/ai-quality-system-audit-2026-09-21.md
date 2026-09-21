# AI 质量系统全面运行与分析报告（2026-09-21）

> **运行方式**：机械门禁全量实跑（`verify-ai` 23 项 + `gate-audit` 矩阵与 12 探针 + `verify-conventions` + `doc-consistency` + `secret-scan`）+ 四路并行人工深读 `.ai/` 全部 150 个文件（无缓存，逐文件打开）+ 6 组门禁变异实验（改坏→验证能红→还原）+ 主审人对 5 条关键结论独立复实证。
> **结论口径**：每条发现含 file:line 证据与 `[事实]`/`[判断]` 标注。本次为**分析与清单输出，未修改任何文件**。

---

## 一、执行摘要

机械层全绿（verify-ai 23/23、gate-audit 12/12 探针、矩阵 0 缺口），但**深层分析发现这套系统正处于"骨架完好、指针风化"的状态**：判定层仪器可信（4 项变异实验全部按预期变红且报错可定位），但汇总性声明与实际触发面严重不符。

三个最严重的问题：**① AI 质量系统的安装器已死**（`install-ai-system.sh:45` 的前置检查指向 MIG-012 已删除的 `gate-check.sh`，实测退出码 1，分发能力为 0）；**② 传感器台账 5+1 个传感器指向已删脚本而 V19 只验日期不验路径**，"传感器 OK"可能是对着空气 OK，且这是全库唯一"校验器自身数据"的项；**③ CI 的 `.ai` 分支恒假**（`.gitignore:68` 排除 `.ai`，fresh checkout 无 `.ai` → `ci.yml:152` 分支永不进入），6 个门禁在 CI 从不运行，而 gate-audit 矩阵因"注释提及即算接线"把它们显示为 WIRED=yes。

根因是同一个：**MIG-012 脚本 C# 化迁移后，指向旧 `.sh` 的指针层（安装器/台账/prompt/README/章程）整体没有同步，而守护这些指针的门禁恰好都设计成"永不因指针漂移而红"**——V9 显式把 `.ai/` 排除在扫描面外、V19 不查路径、V7 查下限不查等值、V13 只查子串、V14 抓第一个百分比。

---

## 二、机械门禁运行结果（真实）

| 门禁 | 结果 | 说明 |
|---|---|---|
| `dotnet run scripts/verify-ai.cs` | **23/23 通过** | V1-V23 全绿 |
| `dotnet run scripts/gate-audit.cs` | **12/12 探针通过**，矩阵 32 脚本：接线 17 · 工具 15 · 缺口 0 · 未归类 0 | 含 6 个 probedGates |
| `verify-conventions --quick` | 6 项全过 | V5/V8/V9/V10/V11/V12 |
| `doc-consistency` | D7 PASS | 薄壳，D1-D6/D8-D12 已下沉测试 |
| `secret-scan` | 579 文件 PASS | 扩展名已扩 |

**这五项全部真实运行通过**。但第三节说明：通过这些门禁不等于系统健康。

---

## 三、深层发现（按严重性）

### 严重（S）——系统能力已实际失效

**S1. AI 质量系统安装器死链，分发能力为 0** `[事实]`
`install-ai-system.sh:45` 的前置存在性检查要求 `$SOURCE_AI/scripts/gate-check.sh`，该文件已在 MIG-012 删除（`.ai/scripts/` 现只剩 `install-ai-system.sh` 与 `template-gate.sh`）。实测：在干净目标目录按文档用法 A 运行 → 退出码 1，报"找不到现行 AI 系统（lessons.md / scripts/gate-check.sh）"，零文件落地。连带失实：同文件 L60 宣传文案、L101-106 复制清单（4 份根镜像源不存在却打印 ✓）、L129-138 手动配置清单均已过期。

**S2. 传感器台账指向已删脚本，而 V19 只验日期不验路径** `[事实]`
`sensor-ledger.md:12-17` 五个传感器指向 `.ai/scripts/{verify-ai-system,doc-consistency-check,tech-debt-scan,gate-check,test-gate}.sh`——全部不存在（真身在根 `scripts/*.cs`）。`:35` 非阻断扫描器整节同样全是已删 `.sh`。变异实验实证：把某传感器行改成指向 `verify-ai-system-PROBE-NONEXISTENT.sh`，verify-ai 仍 23/23 全过。**基线状态下台账已在失实状态而门禁全绿**——"每类错误由哪个传感器负责看见"的真源指向空气。

**S3. CI 的 `.ai` 分支恒假，6 个门禁在 CI 从不运行** `[事实]`
`.gitignore:68` 为 `/.ai`（`.ai` 是独立 git 仓库，不被主仓跟踪，`git ls-files .ai` 为空）；`ci.yml:152` 的全部 `.ai` 自检包在 `if [ -d .ai/scripts ]` 内 → CI fresh checkout 恒无 `.ai` → 分支恒假。恒假分支内的 verify-ai / gate / doc-consistency / tech-debt / test-gate / encoding-gate(.ai 循环) **无任何自动触发面**，只剩手动运行。`ci.yml:122-124` 的注释自认此设计（"本地装有 .ai 才跑全量自检"），但 gate-audit 矩阵因此高估了 CI 覆盖面。

### 高（H）

**H1. gate-audit 假阳性接线：注释提及即算 WIRED** `[事实]`
`scripts/gate-audit.cs:448-476` 的 `CollectWiredNames` 把 `.githooks/*` 与 `.github/workflows/*.yml` 全文拼成语料库，对每个脚本名做 `text.Contains` 子串匹配——**注释里提到名字即计数**。实例：`gate-audit` 自身仅因 `ci.yml:71` 一句注释（原文写"TOOL 手工调用"）即判 WIRED=yes，矩阵结论与它读取的注释自相矛盾。

**H2. PD 模式计数 11 处口径互相矛盾，V7 只查下限** `[事实]`
实际 PD1-PD39 + 通用 1-8 = **47 模式**（实测最大号 PD39，`known-false-positives.md:62`）。但同一文件内 `:3`/`:22` 写"至 PD37，共 45 模式"，而 `:60` 就是 PD37、`:62` 就是 PD39——**文件内部自相矛盾**。跨文件还有 33/37/39/45 四种口径（engine.md:226、README:51/173、charter:167/216、v51:90/92/138/241、metrics:115、sensor-ledger:31）。`verify-ai.cs:176` 的 V7 判据是 `kbFull>=37`——**下限不是等值**，PD 再涨 10 条全部 prose 计数静默过期而门禁全绿。

**H3. test/prompt.md 覆盖率阈值已被判定失效却仍在用** `[事实]`
`.ai/test/prompt.md:125-126` 写"全局行覆盖率不低于 65%（基线 67.9%，2026-07-30）"，与 `docs/test-coverage-baseline.md:29-30`（阈值 **0.70**、基线 **72.98%**、2026-09-14 复校准）直接矛盾；基线文档 `:64` 明言旧阈值 0.65"失去早期预警意义"。后果：执行 `/test` 的 agent 会按 65% 放行低于 70% 门禁的代码。V14 更抓文档中第一个百分比，门禁输出自己显示"覆盖率基线存在（67.9%）"。

**H4. 协议命令面全面失实，协议不可机械执行** `[事实]`
`engine.md:13/16/135/347/358` 与 `fix-protocol.md:21/35/38/75`、`review-charter-v2.md:188-191` 引用的 `bash scripts/*.cs`（file-based app 不能用 bash 跑）、`sister-axis-scan.sh`/`fix-completeness-check.sh`/`post-fix-check.sh`（前两者已迁 `.cs`，第三者已删并下沉 TestGateGuardTests）均不存在。照协议原文执行必失败。

**H5. 三条被当 `[事实]` 引用的核心声明已过期或证伪** `[事实]`
- `unified-quality-system.md:37` 的"P1 曲线近五轮 ≤1"：写作时窗口内就有 P1=4（metrics:53）、P1=8（:40）、P1=4（:38）的轮次；现在恰好变成真，但无复审机制
- 同文件"姊妹类为唯一持续 P1 产地"：其后 13 个有 P1 的轮次中约 12 个非姊妹类（mojibake×2、Kafka 泄漏、SQLite 翻译缺口、SQL 行内注释等），而现行 `sibling-map.md:4` 仍把它当 `[事实]` 引用
- `docs/design/ai-system-optimization-plan-2026-08-20.md:19-20` 同款声明

### 中（M）

**M1. 章程与修复规范是孤儿文档** `[事实]`：`review-charter-v2.md` 与 `fix-protocol.md` 仅被 `.ai/README.md:54-55` 与 `known-false-positives.md:514` 提及，单入口 `prompt.md`→`engine.md` 全文零引用。按 prompt.md 执行的评审代理永远看不到轮次分级制度与修复完备性规范。

**M2. 三套收束判据数值互相打架** `[事实]`：engine.md:33-35（gate 22/22 + 静态轴连续零 + 方言 PASS）、charter:222-225（P0/P1/P2 连续 **5 轮**零 + P3 ≤3/轮）、KFP:514（连续 **3 轮** ≤5 项且 P1/P2=0）——"何时可以收束"有三个答案，且 KFP:514 自称来源包含 charter 却与 charter 数值不一致。

**M3. 修复门两问的埋点死亡** `[事实]`：`engine.md:38` 要求"每轮收口在 metrics 记修复门拦截数与事后正当率"，`metrics.md:167-173` 台账自 2026-09-09 建位仅 1 行（"0 拦截"），其后 v76-v91 共 16 轮零记录；`:38` 自带的"连续两轮零正当拦截即降级观察"从未执行。两问协议目前无任何合规性数据。

**M4. 约 50 个轮次有账本行无报告，编号跳号** `[事实]`：v12/v14/v15/v17/v24 及 08-27..09-09 区间在 `.ai/review/history/reports` 与主仓 `docs/review` 两地均无报告文件（`.ai` git log 证实 history/reports 是一次性归档提交 `cf124ad` 后再无新增）。R34/R38/R46 被引用但无报告。V17 只校验 metrics 表格内日期，一个都发现不了。

**M5. perspective-stats 零数据却被指为数据源** `[事实]`：`perspective-stats.md:20-28` 七流发现率全为 `_待积累_`，自 2026-07-19 立档至今零数据；而 `engine.md:301` 把五指标之一"按流发现密度"的数据源指定为它。该指标结构上不可采集。

**M6. sibling-map 增长规则停摆 + 快照过期** `[事实]`：轴 B 表 7 行自 2026-08-20 起零增长，而 metrics.md:452-455（v88-v91）连续记录姊妹类复发；IIdempotencyStore 文档写 6（:20）实测 7（漏 PalORM 基类）。文件自称"本表是防线真源"（:4）的表已在最新一轮复发的当口停止维护。

**M7. 勾销状态双轨表示** `[事实]`：`action-items-2026-08-17.md` 18 个头全部 `### [ ]`，但"完成回填"节 13 个 `[x]` + 1 证伪 + 3 项"维持现状"——机械读数 15 未清 vs 事实 2 暂缓，差 13 条。叠加 p3-backlog（历史池 230 条全清）与 ITM-794~803（现行池）双池并存，V21 的"无超期"只覆盖前者。

**M8. README 的脚本清单整体失效 + 三处计数失实** `[事实]`：README:66/140 声称".ai/scripts/（20 个）"并列 20 个 `.sh`，实测该目录只有 2 个文件；:12"八个高频防线"中 6 个 `.sh` 不存在。ADR 数 `:98/127/170` 写 22，实测 24 份；lessons 章数 `:46` 写 I-XVI，实际 I-XVIII。

**M9. V13 名不副实** `[事实]`：`verify-ai.cs:248` 只判 `text.Contains("SPD-") && text.Contains("误判")`——一个 "SPD-" 子串即 PASS，不校验 SPD-1..6 齐全、不校验内容、更不校验是否机械化。而抽验显示 SPD 系列 6 条中 5 条仍停留文字层（无门禁强制），恰是 V13 声称覆盖的对象。

**M10. V9 显式排除 `.ai/`，整栈失实无守护** `[事实]`：`verify-conventions` 的 V9 只扫主仓 `docs/`（排除 review/）、`.github/`、根级文档，且显式 `continue` 掉 `.ai/` 路径（理由：.ai 独立仓库不随主仓分发）。上述 S1/S2/H2/H4/M1/M2/M8 全部位于 `.ai/` 内，**结构性地不在任何机械门禁视野里**；`.ai` 子仓自身无 hooks（`core.hooksPath` 未配置）、无 CI，其提交零守卫。

**M11. verify-conventions 探针只覆盖 V11** `[事实]`：gate-audit 中 verify-conventions 的唯一探针只测 V11（决策文档结构）。其 V9（文档命令引用的脚本路径必须存在——正是 MIG-011/012 迁移后二次回归的断链类）零隔离探针；V9 的 `--selftest` 只测 `ExtractScriptRefs`/`IsHistoricalMention` 纯函数，不测扫描面。若 V9 的 glob 或排除列表被改坏，12/12 探针全绿、无任何信号。

**M12. system-template 旧版分叉 + python3 硬依赖** `[事实]`：`.ai/system-template/install-ai-system.sh` 与现行安装器已分叉，仍是修复前旧版（含 D3 永久短路缺陷与 ITM-622 read 缺陷两个真 bug 的活样本）；`tech-debt-scan.sh.template:16` 硬依赖 python3（缺失即 exit 1），与项目已全 C# 化（0 个 `.py`、2026-09-14 用户裁决禁 python 面）冲突。

**M13. 报告层自发演出协议未定义的 P4 级** `[事实]`：`review-2026-08-22-full-carpet-v3..v7.md:12` 与 `v16.md:33` 大量使用"P4×~15/20/25"，但 engine.md:261-267 与 prompt.md:192-198 的优先级体系只有 P0-P3，metrics 账本无 P4 列——P4 的定级/处置规则完全空白。

**M14. lessons.md 版本号与内容脱节** `[事实]`：文件头自称"版本 v1.0"（:6）而内容已到 XVIII（2026-09-11），README 系统版本是 v2.3（README:164）。另 XIII 章题标注"（2026-07-30）"（:461）但 XIII.6/7/8 是 2026-08-18/20（:504/:512/:521），晚于章题三周。

### 低（L）

**L1. 83 份报告中 2 对字节级重复归档** `[事实]`：`audit-2026-08-16.md` 与 `-4e5437f.md` md5 相同，`audit-2026-08-16-r2.md` 与 `-r2-d502b75.md` md5 相同——83 份实为 81 份独立文档，重复归档虚增计数。

**L2. v51 五处计数失实** `[事实]`：`lessons-learned-v51.md:126/180/214/237-238` 称"gate-check 24 项 / verify-ai 21 项 / 24/24 + 21/21"，实测 gate 保留 3 项 + verify-ai 23 项；`:240`"弱断言 157/173" vs 实际 MaxWeak=200；内部三处清偿总数口径不一（:3"~320" vs :308"238" vs :311"450+"）。

**L3. PD10/PD11 数字漂移** `[事实]`：KFP 中"4 处 `.GetAwaiter().GetResult()`"，实测 7 处。

**L4. 验证轮文化是全系统最真实的部分（记录为优势）** `[事实]`：v9-verification.md:24-30 抓出 P1-1 并给三重拦截缺口分析；v10-verification.md:33-38 抓出"readonly 失实链"跨轮传播；08-20-v2.md:13 量化"上轮 13 项 P2 修复 4 项被验证轮抓出（31% 修复缺陷率）"，-v3.md:29 记录降至 8%。深读范围内未发现验证轮照抄被验证报告结论。

---

## 四、门禁自身盲区（变异实验实证）

这是"验证验证者"原则的直接落实。6 组实验结果：

| # | 实验 | 预期 | 实际 | 结论 |
|---|---|---|---|---|
| 1a | sensor-ledger 定标日改 200 天前 | V19 红 | exit 1，精确报"定标超期 200 天" | **能失败** |
| 1b | 传感器行改指向不存在路径 | 应有 V 项红 | exit 0，**23/23 全过** | **盲区实证**（V19 只查日期不查路径） |
| 2a | 速版最大 PD 号改小 | V7 红 | exit 1，双源口径报错 | **能失败** |
| 2b | 完整版加 PD40、速版不加 | V7 红 | exit 1，断链可检出 | **能失败** |
| 3 | metrics 相邻行日期对调 | V17 红 | exit 1，定位到行 | **能失败** |
| 4 | P3 账本加未勾销超 30 天条目 | V21 红 | exit 1，豁免逻辑未误判 | **能失败** |

**判定**：verify-ai 的**日期类/结构类判定可信**（4/4 能失败且报错可定位）；但**存在性与数值类判定有实证盲区**（V19 不查路径、V7 查下限不查等值、V13 查子串、V14 抓第一个百分比）。每个实验后均已还原，两仓 `git status` 干净，终态 verify-ai 回到 23/23。

---

## 五、修复清单与任务进度

> 进度口径：**本次为分析与清单输出，未修改任何文件，全部任务待修复（0/N）**。
> 优先级：P0=系统能力失效 / P1=数据与指针失实 / P2=协议与覆盖缺口 / P3=卫生。

### P0 · 系统能力失效（3 项）

| ID | 任务 | 位置 | 验收标准 | 工作量 | 风险 | 进度 |
|---|---|---|---|---|---|---|
| F-01 | 修安装器死链：前置检查改挂现行文件（`lessons.md` + 根 `scripts/verify-ai.cs`）或直接删该检查；同步 L60/L101-106/L129-138 文案与复制清单 | `.ai/scripts/install-ai-system.sh:45,60,101-106,129-138` | 干净目标目录按用法 A 跑通，退出码 0 且落盘预期文件集 | S | 低 | 待修复 |
| F-02 | 传感器台账路径校准：L12-17 五个传感器改指根 `scripts/*.cs`，`:35` 非阻断扫描器整节同步；删首行 `PROBE-NONEXISTENT` 残留 | `.ai/gate/sensor-ledger.md:12-17,35` | 台账每行"传感器"列指向的文件真实存在（可机械校验） | S | 低 | 待修复 |
| F-03 | 给 V19 或新 V 项加**传感器路径存在性校验**；同步 `gate/prompt.md:35,36,44,114,117,120,127` 的已删脚本与 `bash scripts/gate.cs` 错误命令形态 | `scripts/verify-ai.cs`（V19）、`.ai/gate/prompt.md` | 变异实验：把台账某行改成不存在路径 → verify-ai 必红；基线当前 6 处失实从红起步 | M | 中（V19 逻辑变更须同步 selftest） | 待修复 |

### P1 · 数据与指针失实（6 项）

| ID | 任务 | 位置 | 验收标准 | 工作量 | 风险 | 进度 |
|---|---|---|---|---|---|---|
| F-04 | CI `.ai` 分支处置：要么把 6 个门禁真正接入 CI（`.ai` 随分发或改判"本地专用"从矩阵摘除），要么修 `CollectWiredNames` 排除注释行、要求出现于可执行位置 | `.github/workflows/ci.yml:152`、`scripts/gate-audit.cs:448-476` | 矩阵 WIRED 列与实际触发面一致；gate-audit 自身的假阳性接线消失 | M | 中 | 待修复 |
| F-05 | PD 计数收敛：删全部 prose 计数只留门禁输出，或把 V7 从"下限"改为"prose 声称 == 实测"；统一 KFP 头/README/engine/charter/v51/metrics/sensor-ledger 七处口径 | `scripts/verify-ai.cs:176` + `.ai/` 内 11 处 | 任意一处 prose 计数与实测不符 → verify-ai 必红 | M | 低 | 待修复 |
| F-06 | 同步 test/prompt.md 覆盖率阈值到 0.70/72.98%，V14 改校验"门禁阈值"行而非任意百分比 | `.ai/test/prompt.md:125-126`、`scripts/verify-ai.cs:261` | prompt 阈值与基线文档一致；V14 输出现行阈值 | S | 低 | 待修复 |
| F-07 | 协议命令面校准：`bash scripts/*.cs` → `dotnet run`；删/改已迁移已删脚本引用；同步 charter 四件套登记 | `.ai/review/engine.md:13,16,135,347,358`、`fix-protocol.md:21,35,38,75`、`review-charter-v2.md:188-191` | 协议中每条命令都可执行（可机械校验命令形态） | M | 低 | 待修复 |
| F-08 | 三条过期"事实"声明复审并改标注：P1 曲线近五轮 ≤1、姊妹类唯一持续 P1 产地、覆盖率 65%/67.9% | `docs/design/unified-quality-system.md:37`、`.ai/review/sibling-map.md:4`、`.ai/test/prompt.md:125` | 三条声明或改标注、或补复审机制（如"每 5 轮重验一次"） | S | 低 | 待修复 |
| F-09 | README 脚本清单与三处计数勘正：`.ai/scripts/` 20 个 → 2 个；ADR 22 → 24；lessons I-XVI → I-XVIII；版本号 v1.0 → 实际 | `.ai/README.md:12,24,46,51,66,98,127,140,164,170,173`、`lessons.md:6` | README 文件地图与实际目录逐一吻合 | S | 低 | 待修复 |

### P2 · 协议与覆盖缺口（8 项）

| ID | 任务 | 位置 | 验收标准 | 工作量 | 风险 | 进度 |
|---|---|---|---|---|---|---|
| F-10 | 章程/修复规范接入入口：prompt.md 或 engine.md 增加对二者的引用，使其不再是孤儿文档 | `.ai/review/prompt.md`、`engine.md` | 按 prompt.md 执行的代理能 reach 轮次分级与修复完备性规范 | S | 低 | 待修复 |
| F-11 | 收束判据统一：三套数值（gate 22/22 / 连续 5 轮零 / 连续 3 轮 ≤5）收敛为一个，其余标为历史版本 | `.ai/review/engine.md:33-35`、`review-charter-v2.md:222-225`、`known-false-positives.md:514` | "何时可以收束"只有一个答案 | S | 低 | 待修复 |
| F-12 | 修复门两问埋点恢复：metrics 台账补 v76-v91 共 16 轮记录，或明确废止该台账并删 engine.md:38 要求 | `.ai/review/metrics.md:167-173,440-455`、`engine.md:38` | 台账有数据或要求已删（不留空头声明） | S | 低 | 待修复 |
| F-13 | 轮次号↔报告文件存在性纳入机械校验（新 V 项或扩 V17）：v12/v14/v15/v17/v24 及 08-27..09-09 约 50 轮补报告或销号 | `scripts/verify-ai.cs`、`.ai/review/history/reports/` | metrics 每个轮次号都有对应报告文件（或显式豁免） | M | 中 | 待修复 |
| F-14 | perspective-stats 处置：要么开始采集（评审报告收口时按流计数），要么从 engine.md:301 的指标清单摘除 | `.ai/review/perspective-stats.md:20-28`、`engine.md:301` | 不留"结构上不可采集却被指为数据源"的指标 | S | 低 | 待修复 |
| F-15 | sibling-map 恢复维护：补 IIdempotencyStore 第 7 实现、ICompressor/IMessageSerializer 轴 B 条目；v88-v91 姊妹类复发按规则入表 | `.ai/review/sibling-map.md:15-20,28-36,84-95` | 实跑 `sibling-map.cs` 输出与快照一致；增长规则有最新留痕 | S | 低 | 待修复 |
| F-16 | P4 级正式化或禁用：在 engine.md/prompt.md 的优先级体系补 P4 定义与处置规则，或把报告层 P4 改写为 P3 | `.ai/review/engine.md:261-267`、`prompt.md:192-198`、`review-2026-08-22-full-carpet-v3..v7.md:12`、`v16.md:33` | 优先级体系与报告层用词一致 | S | 低 | 待修复 |
| F-17 | 勾销状态单轨化：修 action-items-2026-08-17 的 18 个 `### [ ]` 头与回填节矛盾；明确 p3-backlog 与 ITM-794~803 双池的状态真源 | `.ai/review/history/action-items/action-items-2026-08-17.md`、`action-items-p3-backlog.md` | 机械读数与事实一致；V21 覆盖现行池 | S | 低 | 待修复 |

### P3 · 卫生（5 项）

| ID | 任务 | 位置 | 验收标准 | 工作量 | 风险 | 进度 |
|---|---|---|---|---|---|---|
| F-18 | 删 2 对字节级重复归档报告 | `.ai/review/history/reports/audit-2026-08-16{,-4e5437f}.md`、`audit-2026-08-16-r2{,-d502b75}.md` | 83 → 81 份，无 md5 重复 | S | 低 | 待修复 |
| F-19 | v51 五处计数勘正（gate 3 项/verify-ai 23 项、MaxWeak 200、清偿总数统一、测试数 1358） | `.ai/review/lessons-learned-v51.md:3,126,180,214,237-241,308,311` | 与实测一致 | S | 低 | 待修复 |
| F-20 | KFP 数字漂移勘正（PD10/PD11 的"4 处" → 7 处）；tech-debt #14-#20 映射表改指 TechDebtGuardTests | `.ai/review/known-false-positives.md:72-92` 及 PD10/PD11 条目 | 与实测一致 | S | 低 | 待修复 |
| F-21 | 删或重做 system-template 旧版分叉副本与 python3 硬依赖模板 | `.ai/system-template/install-ai-system.sh`、`tech-debt-scan.sh.template:2,16`、`INSTALL.md:29,48-50,56,61-63`、`AGENTS.md.template:11,30` | 模板与现行安装器一致；无 python3 依赖 | M | 低 | 待修复 |
| F-22 | lessons.md 章题日期与版本号勘正（XIII 章题 2026-07-30 vs 内容至 08-20；版本 v1.0 vs v2.3） | `.ai/lessons.md:6,461` | 版本号与章题日期反映实际内容 | S | 低 | 待修复 |

### 进度汇总

| 优先级 | 任务数 | 已完成 | 进行中 | 待修复 |
|---|---|---|---|---|
| P0 系统能力失效 | 3 | 0 | 0 | 3 |
| P1 数据与指针失实 | 6 | 0 | 0 | 6 |
| P2 协议与覆盖缺口 | 8 | 0 | 0 | 8 |
| P3 卫生 | 5 | 0 | 0 | 5 |
| **合计** | **22** | **0** | **0** | **22** |

**建议执行顺序**：F-01（安装器，S 级且解除分发阻断）→ F-02+F-03（台账与 V19 存在性，一组）→ F-05+F-06（计数与阈值，机械可验）→ F-04（CI/矩阵，需设计决策）→ 其余按优先级。F-03 与 F-13 是仅有的两个"中"风险项（改 V 项逻辑须同步 `--selftest` 红绿矩阵）。

---

## 六、开放问题（需人类决定）

1. **`.ai` 的 CI 定位**：F-04 的二选一取决于产品意图——`.ai` 是打算随仓库分发（则需入版本库 + 接 CI）还是明确为"本地开发工具"（则从矩阵摘除、README 明说）？当前状态（gitignore 排除 + CI 恒假分支 + 矩阵显示 WIRED）是三者中最坏的组合。
2. **prose 计数的存废**：F-05 的"删全部 prose 计数"会改变 `.ai` 文档的可读性（数字有助于人类快速理解规模），但与 V7 下限判据天然冲突。需决定要"人类可读的数字"还是"机械一致的零计数"。
3. **P4 级**：报告层已自发使用 P4 约 15-25 项/轮，是实际需要（P3 老化规则太粗）还是噪声放大？这决定 F-16 的方向（正式化 vs 禁用）。
4. **历史轮次报告**：约 50 个轮次有账本行无报告。补报告（成本高、价值是证据可回放）还是销号（承认那些轮次的细节不可回放）？

---

*本报告基于 2026-09-21 实跑与实读。所有 file:line 可复现；变异实验均已还原，两仓 `git status` 干净，终态 verify-ai 23/23。本次未修改任何文件。*
