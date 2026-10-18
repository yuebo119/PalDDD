# EPSL 系统审计与 v1.6 优化（2026-10-18）

> 审计对象：全局 EPSL v1.5（`~/.zcode/references/EXTREME_PERF_SYSTEM.md`，395 行）+ `perf-template/` 15 文件 + Pal.DDD 接入件（`perf-budgets.md` / `.ai/perf-ledger.md` / `docs/performance.md` / AGENTS.md 性能节 / bench / CI）。
> 触发：用户显式指令「再次全面审计 epsl 系统，结合当前项目优化经验，给出最优优化建议并按最优方案全面优化」。同时满足阶段边界触发（2026-10-14~18 性能双轮、地毯审计、流式读与质量系统轮收官）。
> 方法：读全量方案与模板；对照三份 EPSL 报告（2026-10-11 接入 / 2026-10-11 地毯 / 2026-10-16 地毯）、ADR-030/031、perf-ledger、performance.md、CI 与 gate-audit 配置；事实逐项核验（源码计数、文件存在性、命令实跑）。性质为系统审计 + 实施轮，不是代码地毯深读：阶段 1 全绿，无门禁红、量级越界或新负载信号。

## 一、看点（量化结论）

1. **三类问题**：事实漂移 3 处（EPSL §5 的 L1 状态仍写「642 条待收敛」；项目 AGENTS 仍标 v1.1 / 22 基准类；performance.md 仍写 BDN 0.15.8 默认 Switcher 不可用，与 2026-10-17 升 0.16 后恢复矛盾）、方法论缺口 9 项（2026-10-14~18 新经验未入文）、机械化缺口 1 项（`--smoke` 分配门禁仍是人工 TOOL）。
2. **模板事实核验**：实际 15 文件与声明一致；README 引用 13 条路径全部存在；发现并移除 1 条失效引用（`EXTREME_PERF_TASKS.md` 不存在）。
3. **基准类数校正**：项目预算与 performance.md 写 22 类，源码实际 37 类（`public class *Benchmarks` 计数）。本轮统一为 37，并把计数纳入机械对账。
4. **新增经验入文 9 项**：净效果计量（Kafka 双 ToArray 假闭环）、残余归属（流式 336 B/帧）、收敛判定与复活条件（框架可控 8.2% / 0 B）、前提证伪与阈值算术（SQLite 32766、多行 VALUES 3.5×、EventLog k≥3）、编译期/工具链性能（R10/A15/S7）、同 run 微 lane 与跨 run 绝对值分离、池化非重入契约、代码注释登记作为第三决策载体、C# `[]` 集合表达式空数组分配。
5. **机械对账落地**：新增 `~/.zcode/scripts/eps-perf-check.mjs`，自测覆盖缺文件 / 文件数漂移 / 版本漂移 / 基准类数漂移四类坏输入（全红）；对当前项目首跑抓出 2 处漂移，修复后 0 错误。
6. **门禁接线**：`--smoke` 从人工 TOOL 转为 CI `perf-smoke` 每提交门禁；本地实跑 6/6 分配预算通过，Release 构建 0 警告 0 错误。

## 二、审计总表

| # | 级别 | 发现 | 证据锚点 | 处置 |
| --- | --- | --- | --- | --- |
| 1 | P0 | EPSL §5 写「Pal.DDD L1 门禁默认关闭、642 条待收敛」，与 2026-10-18 基线归零事实冲突 | EPSL v1.5 §5；`docs/quality-analyzer-triage-2026-10-18.md`（0 警告）；`src/Directory.Build.props` 开关默认 false | 已修复：改为项目文件指针 + 时点，不再在全局文档维护易变计数 |
| 2 | P0 | 项目 AGENTS 标「EPSL v1.1」、文档地图写「22 基准类」，源码实际 37 类 | `AGENTS.md:123/130`；`bench/PalDDD.Benchmarks/*.cs` 计数 37 | 已修复：v1.6；37 类并附分解；预算与 performance.md 同步 |
| 3 | P1 | 模板 README 引用不存在的 `~/.zcode/references/EXTREME_PERF_TASKS.md` | `perf-template/README.md:3`；文件不存在 | 已修复：移除引用，改为指向主方案 §7 落地清单 |
| 4 | P1 | EPSL §1.2 称「回归归因已补」，但 `bisect-design.md` 仍是设计稿、无实现 | EPSL v1.5 §1.2；`perf-template/audit/bisect-design.md` 状态行 | 已修复：状态改为「设计稿，未实现」；补当前替代流程与实现触发条件 |
| 5 | P1 | 2026-10-14~18 的 8 项新经验未进入方法论 | 2026-10-16 审计报告；performance.md 流式收敛节；质量系统 v92 | 已修复：EPSL §0/§3/§4.5/§4.6/§4.7/§4.9/§5/§6 增补；SOP、检查单、预算模板同步 |
| 6 | P1 | `--smoke` 有断言但未接 CI，属「写了没人跑」的人工 TOOL | `Program.cs:371` 旧注释；`conventions.md:1081` 旧表述；`ci.yml` 无 smoke 步骤 | 已修复：CI `build-and-test` 增 `perf-smoke`；conventions/Program.cs/AGENTS 三方同步 |
| 7 | P2 | perf-ledger 表 1 未覆盖 2026-10-16/18 新特性；表 4 停在 #27 | `.ai/perf-ledger.md` 表 1/表 4 | 已修复：补池化序列化、RETURNING、惰性集合、别名零拷贝、FrozenDictionary、UTF-8 直写、源生成器/分析器 7 行；追加 #28/#29 |
| 8 | P2 | 结果归档三层未在 Pal.DDD 落地，报告/日志/数据边界不清 | EPSL §5 归档三层；项目 `.gitignore`；`bench/` 只有 BDN artifacts | 已修复：`.gitignore` 屏蔽 `bench/reports`、`bench/logs`；新增 `bench/results/README.md` 归档约定；perf-budgets §7 落账 |
| 9 | P2 | `tools/curve.py` 在 Windows Git Bash 无 `python3` 时不可直接运行，README 未说明 | `command -v python3` 无结果；`curve.py` shebang | 已修复：模板 README 注明 Windows 用 `python`、Unix 用 `python3` |
| 10 | P2 | 预算/文档的基准类数没有机械核对，22 vs 37 靠人工发现 | `perf-budgets.md` §1 旧值；源码 37 | 已修复：`eps-perf-check.mjs --project` 比对声明与实际类数 |
| 11 | P3 | diff→基准关联表、三臂地板臂、基准历史 HTML 曲线仍未落地 | 2026-10-11 接入审计表 2（L1/L2） | 登记不做本轮：预算线已由 smoke + 车道裁剪覆盖，ROI 低于当前收敛面；触发条件见第六节 |
| 12 | P3 | `perf-bisect` 自动归因未实现 | `bisect-design.md` | 登记不做本轮：人工区间复测仍可执行；实现触发条件入设计稿 |
| 13 | P2 | performance.md 环境行仍写 SDK rc.1 / BDN 0.15.8；未完成项 #1 仍称默认 Switcher 不可用，与 2026-10-17 升 BDN 0.16.0-preview.2 后恢复矛盾 | `docs/performance.md:4/11/735`；`Program.cs` 2026-10-17 注释 | 已修复：环境行校正 rc.2 / 0.16.0-preview.2；历史段标注当时口径；未完成项 #1 改为已恢复并保留显式 InProcess 配置 |

**处置统计**：13 项 = 已修复 11 + 登记 2。无「发现但静默丢弃」项。

## 三、最优方案与取舍

**选择 v1.6 增量升级，不重写 v2.0。** 理由：EPSL 的漏斗结构（事前契约 / 事中门禁 / 事后节点审计）与四阶段 SOP 经三个项目、多轮实战验证，本轮问题是事实漂移、新经验未沉淀、门禁未接线，不是架构失效。重写会丢掉已有证据锚与项目接入形态。

**把 New 8 项放在方法层，不放在项目层。** 净效果、归属、收敛复活、前提证伪、编译期性能、同 run 判读、池化非重入、注释登记都具有跨项目复用性；项目只保留具体数字与载体（perf-budgets / ledger / ADR）。这符合 EPSL §6「单一定义点」。

**机械对账而不是再写一条文字规则。** 事实漂移的根因是全局文档维护了易变计数。v1.6 把「易变事实以项目文件为单一真源」写成纪律，并新增 `eps-perf-check.mjs` 检查版本、模板引用与基准类数。检查器先自测能红，再对真实项目跑。

**把 smoke 接 CI，而不是继续人工跑。** 6 项分配断言是确定性指标，跨 OS/runtime 逐字节稳定；bench 工程已在 CI 构建；命令秒级。接线后 L2 门禁从「有断言但没人跑」变成每提交门禁。代价是 CI 增加一个步骤，风险是 Linux 首次运行的分配稳定性，处置见第六节。

**不做的三项及理由**：`perf-bisect` 自动归因（人工区间复测够用，实现成本高于当前收益）、diff→基准关联表（预算线未要求逐 diff 跑基准）、HTML 曲线（Pal.DDD 无 PerfHub history JSON，先有数据再谈可视化）。三项均登记触发条件，不硬凑。

## 四、实施清单

### 全局件（`~/.zcode/`）

| 文件 | 变更 |
| --- | --- |
| `references/EXTREME_PERF_SYSTEM.md` | v1.5 → v1.6：新增两条计量红线、编译期/工具链面、§4.9 收敛判定、六条硬规则、注释登记第三载体、门禁接线纪律、对账件；修正 bisect 状态、L1 状态、项目状态单一真源；§7.2 文件树与 §10 证据索引同步 |
| `perf-template/README.md` | v1.6；移除失效引用；补对账命令、五条铁律、工具可运行性说明 |
| `perf-template/audit/carpet-audit-sop.md` | 阶段 2.3 增注释登记；阶段 3 增前提证伪；新增阶段 4.5 收敛判定；终局交付增归档三层；A/B 器材增归属/净效果探针；量具坑清单增 5 条；分片 brief 增编译期面与净效果输出 |
| `perf-template/review/perf-review-checklist.md` | 新增 E 两条计量红线、F 前提与决策载体；D 判例增假闭环、阈值算术、版本上限、归属、注释登记、池化非重入 |
| `perf-template/dotnet/perf-budgets.md` | 新增流式/编译期预算行、流式复杂度契约、CI smoke 载体、归档三层、收敛判定、单一真源 |
| `perf-template/dotnet/AGENTS-perf.md` | v1.6；补决策载体三处、净效果/归属、门禁状态、归档与对账命令 |
| `perf-template/audit/bisect-design.md` | 明确未实现；补当前替代流程与实现触发条件 |
| `AGENTS.md` | EPSL v1.6；补两条计量红线与对账命令；脚本清单增 `eps-perf-check`；顺带修正质量工具链两处失实（分析器基线、Stryker/Semgrep 状态） |
| `scripts/eps-perf-check.mjs` | 新增：版本/模板引用/文件数/项目基准类数检查；`--selftest` 四类坏输入自证 |

### 项目件（Pal.DDD）

| 文件 | 变更 |
| --- | --- |
| `AGENTS.md` | 文档地图 22→37；EPSL 节 v1.1→v1.6；补两条计量红线、决策载体三处、CI smoke 与对账命令；CI 段落增 `perf-smoke` |
| `perf-budgets.md` | §1 37 类并附分解；§2 增流式两项预算；§3 增流式复杂度；§5 smoke 载体改 CI；§6 增基准类数同步；新增 §7 归档、§8 收敛、§9 单一真源 |
| `.ai/perf-ledger.md` | 头部增决策载体三处；表 1 补 7 行新特性；表 4 增 #28（CI smoke）/#29（流式收敛） |
| `docs/performance.md` | 环境行校正 rc.2 / BDN 0.16.0-preview.2；新增 2026-10-18 当前口径（37 类）；历史 22 类段保留并标注；烟测节标注 4/4 → 6/6 演进；未完成项 #1 改为默认 Switcher 已恢复（2026-10-17） |
| `docs/conventions.md` | 性能契约载体从「本地/人工（未接 CI）」改为 CI `perf-smoke` + 本地 |
| `.github/workflows/ci.yml` | `build-and-test` 在 Build 后增 `perf-smoke` 步骤（`--no-build` 复用同 job 产物） |
| `bench/PalDDD.Benchmarks/Program.cs` | smoke 注释从「人工 TOOL，未接 CI」改为「已接 CI perf-smoke」 |
| `.gitignore` | 增 `bench/reports/`、`bench/logs/` |
| `bench/results/README.md` | 新增：归档三层约定与当前承载说明 |
| `docs/review/review-2026-10-18-epsl-system.md` | 本报告 |

## 五、验证（本轮实测）

| 命令 | 结果 |
| --- | --- |
| `dotnet build PalDDD.slnx -c Release` | exit 0，0 警告 0 错误，5.52s |
| `dotnet run --project bench/PalDDD.Benchmarks -c Release --no-build -- --smoke` | exit 0，6/6 预算内（0.000 / 40.000 / 0.000 / 128.001 / 0.002 / 0.000 B/op） |
| `node ~/.zcode/scripts/eps-perf-check.mjs --selftest` | PASS：缺文件 / 文件数 / 版本漂移 / 基准类数漂移四类坏输入全红 |
| `node ~/.zcode/scripts/eps-perf-check.mjs --project .` | PASS：EPSL v1.6 / 模板 15 文件 / 13 条引用 / 项目 v1.6 / 37 类一致 / ledger 存在 |
| `dotnet run scripts/count-audit.cs` | PASS：16 项推导，24 文件，80 处声明全一致 |
| `dotnet run scripts/doc-consistency.cs` | PASS：D7 文件地图 + D13 跨仓契约版本（3） |
| `dotnet run scripts/encoding-gate.cs` | PASS：E1-E5 全过，0 失败 |
| `dotnet run scripts/config-policy.cs` | PASS：dependabot / Exec 副作用 / Actions 钉 SHA 三面 |
| `dotnet run scripts/verify-conventions.cs -- --quick` | PASS：V5/V8/V9/V10/V11/V12；135 个文档链接可解析 |
| `dotnet run scripts/verify-ai.cs` | PASS：V1-V30，30 通过 / 0 警告 / 0 失败 |
| `dotnet run scripts/gate-audit.cs -- --inventory` | PASS：42 脚本，25 接线且有自证，17 按设计 TOOL，0 UNVERIFIED / 0 缺口 / 0 未归类 |
| `python -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"` | YAML OK；jobs = build-and-test / aot-verify / quality-analyzers / semgrep / coverage / dialect-probe |
| `dotnet format style PalDDD.slnx --verify-no-changes` | exit 0 |

未跑：全量测试套件（本轮无产品行为变更，仅注释/文档/CI/配置；smoke 与四道静态门禁已实跑）、CI `perf-smoke` 的 Linux 首跑（本地命令与 CI 步骤逐字一致，结果待下一次 CI 观察）。

## 六、残余与下一步

1. **CI `perf-smoke` Linux 首跑观察**：若红，先按 EPSL 诊断步骤分类（环境态 / 运行时差异 / 真回归），先复测再动预算；不得直接放宽 `perf-budgets.md` 阈值。
2. **`perf-bisect` 未实现**：触发条件已写入 `bisect-design.md`（人工归因单次超 1 人日 / 月度门禁红 ≥2 次需区间定位 / 车道数超人工二分上限）。
3. **diff→基准关联表、三臂地板臂、HTML 曲线未落地**：登记为后续专项；当前预算线由 smoke（判定层）+ 车道裁剪（审计期）覆盖。
4. **`curve.py` 仅文档说明 Python 依赖**：若出现无 Python 的项目需要曲线件，再移植为 C# file-based app（本次不预造）。
5. **`.ai` 独立仓**：perf-ledger 变更需在 `.ai` 仓单独提交，不进主仓。

## 七、口径注记

- **37 类的计数口径**：`bench/PalDDD.Benchmarks/*.cs` 中 `public class \w*Benchmarks` 的匹配数；`PersistenceVerify.cs` / `Program.cs` 不计。分解见 `perf-budgets.md` §1。
- **22 类的历史口径**：`docs/performance.md` 2026-10-08 首轮结论；本轮保留原文并标注时点，不回溯改写历史读数。
- **验证时点**：2026-10-18 深夜，工作树状态为本轮修改后；命令输出已落 `/tmp/epsl-v16-*.log`（本地临时，不入库）。
- **全局件不在本仓**：`~/.zcode/` 下的 EPSL 与模板变更不在 Pal.DDD 提交面；本仓只记录影响与对账结果。
