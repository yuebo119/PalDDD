# AI 质量系统自审计报告 2026-08-16

> 方式：三片独立审计（A=13 脚本逐行实读 / B=文档提示词一致性 / C=安装与实跑）+ 主线程定稿复核。
> 范围：`.ai/` 全量（README/engine/prompt×4/lessons/metrics/误判库/模板/13 脚本）+ 与仓库现实对照。
> 全部真实读取与实跑（Git Bash 全路径；dialect-probe 不运行——写真实 DB）。基线 b68bcfc（含本会话优化轮 a21f623 的改动）。
> 定稿结果：**P1×6，P2×19，P3×24**。本报告即分析轮产物，优化清单见文末（修复待下轮）。

## 一、系统健康总览

| 面 | 结论 |
|----|------|
| 实跑 | 13 个脚本全可解析；verify-ai 16/16、gate 22/22、tech-debt 22/22、doc-consistency 10/10、test-gate、refine、assertion 173、review-scope --all（338 文件/53392 行）全部 exit 0 |
| 根镜像 | verify-action-items / review-snapshot / refine-scan 三份与 `.ai/scripts` 逐字节一致 ✅ |
| 结构现实 | AOT 7/14 分层、PDDD 15、ADR 17、BoundaryTests 33、slnx 36 项目——与门禁名单吻合 ✅ |

**核心结论：系统"全绿"是真的，但有两个检查是 no-op（其中一个还是本会话刚加的），且安装/模板链路是 PalORM 旧版——绿的不是系统，是门禁本身。** 这正是本系统要抓的"no-op 门"缺陷，发生在了它自己身上。

## 二、P1 定稿（6 项，全部主线程亲验）

| # | 位置 | 缺陷 | 证据 |
|---|------|------|------|
| A-1 | `.ai/scripts/tech-debt-scan.sh:162-164` | **#13 方言 SQL 守卫检查整体 no-op**：`for m in re.finditer(...)` 缩进在 `if not found_any:` 块内、位于 `raise SystemExit(0)` 之后永不可达——两源文件存在时 `fams` 恒空，方言冲突安全守卫检查**永远 PASS** | 亲验行号+审计实测 `total fams: 0` |
| A-2 | `.ai/scripts/verify-ai-system.sh:195` | **V16 语法门 no-op**：`bash -n \| grep -q . && syntax_bad=... \|\| true` 在 `set -o pipefail` 下，bash -n 失败使管道非零 → `&&` 短路，语法错误永远不会被记入（本会话刚加的 V16 失效） | 审计用坏脚本复现：管道退出 2、bad 变量空 |
| C-1 | `.ai/system-template/tech-debt-scan.sh.template` | 模板仅 4 类检查（52 行/1976B），现行 22 类（319 行/19KB）——按安装提示"删 .ai 重装"即把方言/MTP/slnx 等 18 类防线全部降级删除 | 实测模板 4 个 check；现行 22 PASS |
| C-2 | `.ai/system-template/.ai-template/lessons.md` | 模板 v7.0 占位（101 行、缺陷表空），现行教训库 502 行/35KB——重装即降级覆盖 | 2300B vs 35425B；模板缺陷表 0 行 |
| C-3 | `system-template/INSTALL.md:5-12` + `install-ai-system.sh:4-5,68-82` | 文档化安装路径必失败：照 INSTALL.md 复制后三个 TEMPLATE_DIR 候选全 MISS → exit 1；脚本头让复制根 `scripts/install-ai-system.sh`，但根 scripts 下不存在该文件 | 路径模拟三候选 MISS；根 scripts 实测无此文件 |
| B-1 | `.ai/test/prompt.md:269-284` | /test 结果格式示例列 `PASS T1/T4/T7/T-DDD-1..3`——test-gate.sh 实际只查 T4/T6/T8/T9/T11/T12 + T-DEF-1/T-DEF-4，示例是脚本永远不会产出的输出 | test-gate.sh:2,14 实测 |

## 三、P2 定稿（19 项）

| # | 位置 | 缺陷 |
|---|------|------|
| A-3 | `verify-action-items.sh` | 无根目录发现——从子目录运行，真实存在的路径被误报 FAIL（实测 test/ 下运行 exit 1） |
| A-4 | `tech-debt-scan.sh:62` | #3 的 bin/ 排除失效：`grep -v 'obj/\|bin/ \|\| true'` 正则拼接错误，bin 路径不被过滤 |
| A-5 | `test-gate.sh:148` | T-DEF-4 的 job 计数恒为 1：`grep -c` 数字再经 `wc -l` 恒得 1——CI 实际 2 job，缺 timeout 也报 PASS |
| A-6 | `probe-template.sh:22,59` | 生成 csproj 含 MSYS 路径 `/c/ai/...`，Windows dotnet 不识别——模板探针工程无法 build（dialect-probe 自身专门 cygpath 转换，模板漏了） |
| A-7 | `review-snapshot.sh:51` | AOT_FALSE 计数 15 vs 实际 14：`IsAotCompatible.*false` 命中 Extension csproj 的 Description 散文（本会话 ITM-128 描述引入） |
| B-2 | `.ai/README.md`×4 | refine "24 项"（实 27）、误判库 "PD1-PD9"（实 PD29/37）、verify-ai "15 项"（实 16） |
| B-3 | `.ai/review/engine.md:148,196` | "现行至 PD23 共 31 模式"、"tech-debt #1-17"（实 PD29/37、#1-22） |
| B-4 | `.ai/review/prompt.md:98,104` | "architecture 18 项决策 + ADR 16"——同文件 11 行已写 ADR 001-017，自相矛盾 |
| B-5 | `.ai/lessons.md`×4 | ADR 16 / 18 项决策 / refine 24 项过期 |
| B-6 | `.ai/test/prompt.md:24` | TUnit 1.58.0（实 1.65.0） |
| B-7 | `.ai/gate/prompt.md:55,60` | G6 "14 tokens"（脚本 11 分支）；G11 缺 PalORM sync-over-async 豁免描述 |
| B-8 | `.ai/review/known-false-positives.md:22` | 速版标题 PD1-PD29，条目只列到 PD26 |
| B-9 | `.ai/review/metrics.md:19,80` | 称"ORM 记录已随仓库分离清除"，history/ 仍保留大量 PalORM 时代文件 |
| B-10 | `verify-ai-system.sh:110-125` | V7 注释 "≥26 模式"（实 37）；V8 只匹配 6 流，漏"生成语义流"却守护七流账本 |
| C-4 | `install-ai-system.sh:45-48,96,104` | 宣称复制 prompts / 17 类技术债 / 14 缺陷——模板实际只有 lessons.md、4 类、0 缺陷 |
| C-5 | `install-ai-system.sh:33-37,99-112` | 幂等守卫不覆盖 scripts/.github 既有文件——同名脚本/PR 模板被直接覆盖 |
| C-6 | `install-ai-system.sh` vs `system-template/install-ai-system.sh` | 两份安装脚本漂移（通用 17 类 vs PalORM 12 类），无源标注 |

## 四、P3（24 项，分组）

- **门禁口径**：G13 声称禁 IRepository/EventBus 但脚本不查（脚本有 ArchitectureBoundaryTests 覆盖 IRepository、EventBus 全仓无检查）；G4 声称"仅 ByteAether.Ulid"但只查数量 ≤1；G17b 不计入汇总（恒 22）；tech-debt allow/WARN 计入 PASS（"22 全绿"需注明口径）；doc-consistency D1 只查存在不查非空。
- **可移植/健壮性**：tech-debt #19 BRE `\s` 注释过滤失效（主线程亲验，GNU 扩展下才生效）；test-gate `\s` 同类；gate-check G11 `sed -n` 负地址无下界钳制（命中 1/2 行报错）；gate-check G12/G18/G19/G21 perl 无前置硬门（缺 perl 静默全 PASS，对比 tech-debt 对 python3 的前置硬门）；dialect-probe 双方言不可达 exit 0（调用方需自行判定 SKIP）；assertion-strength `--max-weak` 缺参时 `set -u` 崩溃；review-snapshot AOT_TRUE 口径过窄（只数显式 8，未计继承全局 true）。
- **文档卫生**：根 README 全文无 `.ai` 字样（fresh clone 不可发现系统存在）；history 里 action-items 放错目录 1 个；metrics 多轮无对应归档；perspective-stats 七流顺序与 engine 不一致；prompt 说"9 种探针形态"（engine 实 10）；doc-consistency 注释 "13 编号章节"（实 14）；`_impl_total` 死变量。

## 五、优化清单（按收益排序，修复轮执行）

1. **修 2 个 no-op 门（P1）**：tech-debt #13 的 Python 缩进重排 + 用真实 SQL 家族回归验证它能红；V16 改为捕获式 `if ! bash -n "$s" 2>/tmp/e; then syntax_bad+=...`（并对坏脚本做红测）。
2. **重做安装/模板链路（P1×3 + P2×3）**：模板改为从现行 `.ai` 生成（或删模板、安装脚本改为打包当前 .ai 目录）；INSTALL.md 路径与脚本候选一致；幂等守卫覆盖 scripts/.github；两脚本合一；宣称与实际一致。
3. **test prompt 输出示例对齐 test-gate 真实输出（P1）**。
4. **计数漂移清零（P2×10 + 相关 P3）**：README 四计数、engine 两处、prompt/lessons 三处、KFP 速版补 PD27-29、verify-ai V7/V8、gate prompt G4/G6/G11/G13 描述与脚本校准。
5. **脚本级修复（P2×5）**：verify-action-items 加 `_ai_root_find`；tech-debt #3 bin 过滤正则修复；test-gate T-DEF-4 改 `grep -c` 直读；probe-template 加 cygpath 转换；review-snapshot AOT_FALSE 只匹配属性行 + AOT_TRUE 口径注明。
6. **门禁语义收紧（P3）**：tech-debt 汇总分 PASS/ALLOW/WARN；gate-check G17b 入汇总、G11 sed 钳制、perl 前置硬门；doc-consistency D1 加非空。
7. **可发现性**：根 README 增 `.ai` 系统一节（独立仓库、获取方式、CI 降级）。
8. **卫生**：history 归位/补档、七流顺序统一、README 系统计数统一。
9. **回归网**：给 no-op 门补"变异探针"式负向自检（坏脚本/坏 SQL 必须让 V16/#13 红）——防再犯。

---

## 六、修复轮状态（2026-08-16 完成）

> 全部 9 项优化清单已执行完毕（[x]）。关键返工与证据：
> - **#13 no-op 门**：修复了双重缺陷（循环缩进在 raise 之后永不可达 + 家族正则 `\$` 匹配字面 $）——合成坏 SQL 红测输出 `InboxInsertMySql: missing guard`，真库复跑 22 项通过。
> - **V16 no-op 门**：重写为 `if ! bash -n`——坏脚本红测 `FAIL V16`，移除后 16/16 绿。
> - **安装链路 v2**：安装器以自身所在 `.ai` 为源复制现行系统；临时目录安装红测产出完整系统（lessons 35KB + tech-debt 19.5KB + 全 prompts/scripts）；幂等守卫覆盖 AGENTS.md/.ai/scripts/.github 四落点；system-template 快照同步刷新；INSTALL.md 重写。
> - **test prompt**：结果格式示例改为 test-gate 真实八类输出并注明机械子集口径。
> - **脚本级**：verify-action-items 根发现、tech-debt #3 bin 过滤、test-gate T-DEF-4 计数（实测 2 个）、probe-template Windows 路径（生成 Include="C:\ai\... 实测）、review-snapshot AOT 8/14 口径、gate perl 硬门 + G11 sed 钳制 + G17b 入 WARNED、doc D1 非空、assertion 参数解析、tech-debt #19 可移植正则、allow/WARN 汇总透明化（20 PASS / 2 ALLOW / 0 FAIL）、dialect SKIP 语义注明。
> - **文档漂移**：14 项全部清零（README/engine/prompt/lessons/test/gate 提示词、KFP 速版补 PD27-29、verify-ai V7/V8、metrics、perspective-stats、根 README 新增 .ai 系统一节、history 归档归位）。
> - **终验**：verify-ai 16/16 · tech-debt 20P/2A/0F · gate 22/22（提交后）· doc-consistency 10/10 · test-gate · assertion 173 · verify-action-items 11/0 · review-snapshot AOT 口径 8/14。
