# EPSL v1.1 接入审计（2026-10-11）

> 审计对象：本项目对《极致性能优化系统 EPSL v1.1》（2026-10-11 定稿，`C:\Users\Andy\ZCodeProject\extreme-performance-system.md`）的接入状态与既有性能纪律合规度。
> 触发登记：阶段边界（优化实施轮收尾 4e1af12 同日）+ EPSL 当日定稿后的首次对账——§4.1 固定节点合法触发。
> 性质声明：本轮为**接入/合规审计**（对象 = EPSL 系统），非代码地毯深读。深读未启动的依据：阶段 1 闸口全绿 + 优化实施轮（探针→实施→S3 反向验证）当日收尾 + 无门禁红/量级越界/新负载类型信号（§4.1 反触发纪律）；SOP 双出口取「接入缺口清单」出口，不硬凑产出。

## 一、看点（量化结论）

1. **阶段 1 两闸口新鲜数全绿**：全解决方案 Release 构建 exit 0 / 0 警告 0 错误（15.04s）；`--smoke` 4/4 分配预算内，且分配列与账本跨轮逐字节一致（88 B / 40,000,040 B / 128.001 B/op）——量具自检与账本双通过。
2. **EPSL 六件套接入 3/6 缺失**：perf-budgets.md、.ai/perf-ledger.md 两件不存在；AGENTS.md 无 EPSL 性能节，且文档地图零处引用 `docs/performance.md` / `docs/deployment-performance.md`——路线一「会话启动自动注入」生效机制在本项目不生效。
3. **既有性能纪律超出 EPSL 基线**：S3 反向验证全程在案、勘正链制度、Error 专项治理三轮（83 基准 strict 档除多峰族外全部 <5%）、负结论带数据、继承论断独立复验（T0.1 参照提交本尊复跑推翻二手读数）——四红线抽查零违规。
4. **防重开承载物分散五处**（performance.md 留档段 / ADR-024~026 / 源码勘误注 / cortex 跨会话记忆 / 历史提交），无单一台账入口；28 轮三项「不做」裁决（Kafka 批量回调、ZLogger 门面、Dapper.AOT）现仅见于跨会话记忆——EPSL「提性能建议前必查 ledger」无落点，防重提案靠检索不靠制度。
5. **量具面两小缺口**：无 diff→基准增量关联表（testing.md 矩阵是「特性×车道」覆盖表，非路径关联）；全量基准 ~105 分钟（2026-10-09 晚 13 模式 15 运行），无「常规集 ≤15min / 节点集」分层声明。

## 二、四组表

### 表 1 · EPSL 六件套接入状态（EPSL §6 按位）

| EPSL 件 | 状态 | 证据 / 等价物 |
| --- | --- | --- |
| budget 进规范真源表 | ❌ 缺 | perf-budgets.md 不存在（受跟踪面与目录双查零命中） |
| 门禁进启动清单 | ❌ 缺 | AGENTS.md 无性能节；启动清单无「读预算/门禁命令/勿重开」三件确认 |
| ledger 进 .ai/ | ❌ 缺 | .ai/ 无 perf-ledger.md；等价物分散五处（看点 4） |
| 性能缺陷续 lessons 统一编号 | ◐ 部分 | 无性能编号段；否决/勘正记 performance.md 轮次段与 ADR |
| 汇报对齐五段式 | ◐ 部分 | performance.md 自有轮次格式（表格+口径注+勘正链），要素覆盖五段式但非其形 |
| 审计 SOP 挂 review 路由 | ◐ 部分 | 有审计轮先例（docs/review/perf-*、bench-baseline-*），未挂 EPSL SOP 路由 |

### 表 2 · L0-L3 部件对照（EPSL §5 总表 × 本仓）

| 层 | EPSL 件 | 状态 |
| --- | --- | --- |
| L0 | perf-budgets（延迟+分配预算、量级声明、热路径复杂度清单） | ❌；运行时等价物存在（smoke 分配断言、分发 <500B 契约、测试行为守卫）但无成文契约 |
| L0 | csproj 性能段 / analyzer 机械层 | ✅（TreatWarningsAsErrors + AnalysisLevel latest-all + OptimizationPreference Speed；NoWarn 21 条逐条 Justification = 基线制雏形，超出 EPSL 基线要求） |
| L1 | BDN 增量基准（diff 关联） | ◐ bench 工程成熟（10+ 模式、83+ 基准 strict 化、--delivery/--qps/--compare 多维）；无关联表、未挂提交流程 |
| L2 | 三臂对照 + 地板健全性门禁 | ❌（--compare 是竞品对己方，无地板臂概念） |
| L2 | 基准历史 HTML 曲线 | ❌（github.md 报告在 artifacts-medium，无曲线件） |
| L2 | 基准集 ≤15min 上限 | ◐（smoke 秒级、单车道 medium 4~7 分钟事实存在；全量 105 分钟无分层声明） |
| L3 | 触发深挖协议 | ✅ 实质合规（探针前置/单变量/配对 A/B/S3 反向验证全程在案） |

### 表 3 · 阶段 1 闸口新鲜数（本轮实测，2026-10-11）

| 闸口 | 命令 | 结果 |
| --- | --- | --- |
| Release 构建（含 analyzer 全量） | `dotnet build PalDDD.slnx -c Release` | exit 0 / 0 警告 0 错误 / 15.04s |
| smoke 量具自检 | `dotnet run -c Release --project bench/PalDDD.Benchmarks -- --smoke` | exit 0 / 4/4 预算内 / 分配逐字节 == 账本 |
| 基准矩阵全量 | 引用不重跑（口径注 ①） | 2026-10-10 晚 12 车道 strict 全 exit 0（cbf68c5）+ 2026-10-11 三次增量真跑（T0.3 / T0.2 / T3.1，均 24h 内） |

### 表 4 · 四红线抽查（近三轮性能论断）

| 红线 | 抽查对象 | 判定 |
| --- | --- | --- |
| 抽查绝对化论断 | 「smoke 4/4 预算内」本轮独立复现一致；「死锁零复现」有全日志 grep 台账 | ✓ |
| 负结论必须带数据 | T2.2 否决（快路径 E10.8% 读数 + 回退留档）；span decoder 否决（分配 +34/35% 数据）；PG 旋钮 strict A/B 定论（历史 -55% 判欠采样伪影并勘正） | ✓ |
| ADR / 勿重开比对 | T2.2 / P7 / P8 留档防重提；Dispatcher 勘误注防重提 | ✓（载体分散见表 1 第 3 行） |
| 继承论断独立复验 | T0.1 检出参照提交 33f4dba 本尊复跑，推翻二手读数 1,022/1,425μs，定性「参照系勘正非回归」 | ✓ 范本级执行 |

## 三、行读法归因

- 两件 ❌ 新件（budget / ledger）**非质量退化而是时序**：EPSL v1.1 当日定稿，其 §7.4 存量改造首批仅列 Pal.ORM（本项目作为四个证据源之一被引用但未列入改造清单，属方案遗漏）——故定级「尚未接入」，非「接入后失守」。
- AGENTS.md 文档地图零引用 performance.md 是接入优先级最高的一件：它是路线一生效机制的断点（每次会话自动注入预算与热路径上下文，依赖项目 AGENTS.md 承载），修复成本约 10 行。
- 顺带发现（与性能无关、同类文档失真）：AGENTS.md 文档地图写「24 份 ADR」，实际 docs/decisions/ 为 26 份（ADR-025 2026-10-09、ADR-026 2026-10-10 落地后未同步）。
- 表 2 的 L2 两 ❌（地板健全性、HTML 曲线）为 EPSL「最小件」定位，缺失不影响判定链完整性（分配确定性 + strict Error 治理已覆盖量具可信面），列后续可选项。

## 四、口径注记

① 基准矩阵未重跑：全量 strict 12 车道（2026-10-10 晚）与三次增量真跑（2026-10-11）均在本审计 24h 内，同日重跑不产生新信号且违反「昂贵命令只跑一次」纪律；本审计以构建 + smoke 为新鲜闸口，基准面引用账本既有读数。
② 本轮不启动阶段 2 地毯深读（见页首性质声明）；如需代码级深读须另满足 §4.1 触发条件。
③ 库项目预算传导（EPSL §9 未定义边界）：Pal.DDD 是类库，若起草 perf-budgets，SLO 段应改写为「基准锚」形态（per-op 分配/计时预算锚定基准读数，非部署 SLO——部署旋钮类结论已在 deployment-performance.md）——EPSL 预案即「首个类库项目遇到时补 ADR」，本项目接入时应随附该 ADR。
④ AGENTS.md ADR 计数（24）与实际（26）不一致的核对口径：`ls docs/decisions/ | grep -cE "^[0-9]{3}-"` = 26。

## 五、尾注（后续动作清单，按优先级）

1. **AGENTS.md 补 EPSL 接入节**（模板 perf-template/dotnet/AGENTS-perf.md 按本项目裁剪）+ 文档地图补 performance.md / deployment-performance.md 两行 + ADR 计数勘正 24→26。约 10 行，零代码。
2. **起草 perf-budgets.md**：量级声明、分配预算锚（从 performance.md 既有 strict 读数锚定）、热路径复杂度清单；随附「类库预算传导」ADR（口径注 ③）；agent 起草、人裁决定稿。
3. **.ai/perf-ledger.md 三表归集**：已充分利用特性表（IsCompletedSuccessfully 零堆路径 / ArrayPool 池化 / FrozenDictionary catalog 等）、明确不做表（span decoder、P7/P8、T2.2、旋钮、28 轮三不做——从五处分散载体收拢）、勿重开清单。
4. 可选项：diff→基准增量关联表（src 路径 → 模式映射，挂提交流程）；「常规集 ≤15min / 节点集」分层声明；基准历史 HTML 曲线件；地板健全性门禁（QPS harness 加地板臂）。

数据源：`%TEMP%/epsl-audit-build.log`、`%TEMP%/epsl-audit-smoke.log`（本轮命令台账）；性能账本 `docs/performance.md`（2026-10-11 状态，HEAD 4e1af12）。
