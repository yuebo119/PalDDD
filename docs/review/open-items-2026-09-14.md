# 开放项清单（2026-09-14 全量收账）

> 来源：rule-placement-audit-2026-09-13（规则落位账本）· action-items-2026-09-13-bench（ITM 台账）·
> tech-debt.cs / gate-audit.cs 实测 · 近五轮会话遗留项收账。
> 状态口径：🔴 待裁决 · 🟡 排队（窗口/触发条件）· 🔵 上游/外部跟踪 · ⚪ 已裁决不做（防重提）

## A. 待你裁决/执行

| # | 项 | 现状 | 动作 |
|---|-----|------|------|
| A1 | ~~dev 推送~~ | ✅ **已完成**（2026-09-14）：`08ba7d6..080871c` 43 提交推送 origin（proxy 环境：需 `git -c http.proxy= -c https.proxy= -c http.version=HTTP/1.1 push` 组合，绕本机 127.0.0.1:50001 代理） | — |
| A2 | ~~ITM-673 Dapper.AOT ct 上游 issue~~ | ✅ **已完成**（2026-09-14）：issue #225 提交 → https://github.com/DapperLib/DapperAOT/issues/225（github MCP 通道，账号 yuebo119） | 跟踪上游回复 |
| A3 | ~~全局 AGENTS.md 规则承载物改造~~ | ✅ **已完成（索引层）**（2026-09-14）：文件头新增「规则承载物索引」表（20 规则区 → [脚本]/[类型]/[工具]/[人工]）+ 两条已知缺口注记；**正文压缩/移交 references 的深度重构保留为待办判断项**（索引中已注明） | 深度重构待后续裁决 |
| A4 | ~~encoding-gate E5 工作树行尾防线~~ | ✅ **已完成**（2026-09-14，`7b36c11`）：E5 判定+X 例外清单+自测 21/21+变异验证+gate-audit 探针 5/5 | — |
| A5 | ~~scripts/gate-audit.cs 未提交~~ | ✅ **实为假脏**（2026-09-14 勘正）：文件已跟踪已提交，"untracked"是过时快照；工作树 M 为行尾归一化（add 即净） | — |
| A6 | ~~单模块覆盖率降幅门禁~~ | ✅ **已完成**（2026-09-14，`080871c`）：coverage-baseline.json（15 项目）+ ci-coverage Step 6（容差 5pp，epsilon 修正）+ --update-baseline 入口 + 自测 18/18 + 变异 + 端到端数据验证 15/15 | — |

## B. 排队中（major 窗口 / 触发条件）

| # | 项 | 窗口/触发 |
|---|-----|----------|
| B1 | **ITM-672 EFCore Pooling 解锁**（拦截器状态迁 Context 派生字段） | v3.0 major 窗口（API 变化，与 B2 捆绑裁决维持）。**2026-09-19 优先级提升建议已实测撤回**：Pooling 对照实验（同日，%TEMP% spike 60 轮）——AddDbContext vs AddDbContextPool 同负载中位 10.68→10.21ms，**收益仅 4.4%（0.47ms）**；Lease 基准 11.70ms 的差距主体在 SaveChanges 管道（EF 上游，Pooling 不解决），"B1 是剩余差距主杠杆"的假设不成立。bench-baseline 09-13 的"部分改善"预期得到更弱确认 |
| B2 | **IPalOutboxStore 异步化 + 跨栈 fencing 契约统一**（吸收 PalORM 同步 DIM 的 GetResult 阻塞） | 同 v3.0 窗口（ADR-020 保留项） |
| B3 | **[Obsolete] 6 处移除**（Core Attributes ×2"框架零消费"、SqlServerOutboxDbContext 等，tech-debt WARN 项） | v3.0（移除计划已在注解内声明） |
| B4 | **覆盖率门禁阈值重校准** | CI accuracy 触发：coverage job 首跑产出含 Docker 的完整值后按其重校准（47c8c24 声明） |
| B5 | ~~54 处 docs 会话相对表述改写~~（"上一轮/本次"等） | ✅ **已完成**（2026-09-19，`40f7701`）：19 文件约 150 处改写为绝对表述（实测含误报由执行时甄别），NAMING §七对照表执行，mention 类保留 |
| B6 | **ITM-787 ChildSaga 车道测试从零补齐**（ProcessEventAsync 级表征：成功路径 / 重试耗尽补偿 / 补偿再失败嵌套 / 每 attempt 重建 state；含 v35 输入通道 public 化的回归——该区域缺陷曾因零测试掩盖数轮） | ✅ **已完成**（2026-09-19）：`SagaLaneCharacterizationTests.cs` 三车道 12 用例全绿（FanOut/ChildSaga/Dynamic 各 4），每 attempt 重建 + P3-SRC-603 观察者归因已锁定 |
| B7 | ~~ITM-804 推送 49 提交 + CI 全套验证~~ | ✅ **推送完成**（2026-09-19，`c908f40..7412390`，pre-push hook gate-lite 实跑通过）。CI 监控通道受限（gh 未认证）——CI 结果需维护者 GitHub 页面确认或 gh auth 后复查（aot-verify 对 Saga 骨架/EF 双下推的真发布验证是重点） |
| B8 | ~~ITM-805 B4 覆盖率阈值重校准~~ + **ITM-810 CI Format verify 首验矛盾消解** | ✅/✅ **已完成**（2026-09-19）：CI 首验三连失败（run 101/102/104）→ IDE0005 根因为 dotnet format 对源生成产物不加载的已知局限（format 判 unnecessary vs Build CS0246 互相矛盾），Core.Tests 项目级 NoWarn 压制根治（43ddc28）；**run 105 四 job 全绿**（dialect-probe/coverage/aot-verify/build-and-test 全 success）——aot-verify 对 Saga 骨架/EF 双下推/DapperAot 启用的真发布验证通过。B4 重校准待办：coverage job 已产出含 Docker 完整值，取数后与 0.70 阈值比对（须 CI 页面/artifact 取数，本会话 API 未见汇总数字） |
| B9 | ~~ITM-806 .NET 11 GA 迁移预案~~ | ✅ **已完成**（2026-09-19）：docs/migration/net11-ga-upgrade-plan.md（rc 包清单+五项 rc 特有行为复核表+六步升级顺序） |
| B10 | ~~ITM-807 三栈 Lease/GetPending 谓词对照测试~~ | ✅ **已完成**（2026-09-19）：CrossStackPredicateParityTests（SQLite 方言三栈归一化等价断言，ADR-024 白名单跳过 PG 锁子句） |
| B11 | ~~ITM-808 pre-push 同步提示 hook~~ | ✅ **已完成**（2026-09-19）：pre-push 追加段（落后警告+积压≥10 提示），推送实测触发 |
| B12 | ~~ITM-809 Dapper 2.1.86 升级~~ | ✅ **已完成**（2026-09-19）：CPM 升级，全量零回归 |

## C. 上游/外部跟踪

| # | 项 | 现状 |
|---|-----|------|
| C1 | Dapper.AOT ct 调用点入口（=A2 的 issue 对象） | 管道已支持 ct，仅缺调用点语法；草稿就绪 |
| C2 | EF Core 11 GA（2026-11）AOT 警告复查 | 当前 RC 仍 experimental；GA 后重查"零警告"是否达成 |
| C3 | 三栈包版本 | Dapper 2.1.79 / Dapper.AOT 1.1.0 / EF Core 11 rc.1 均已最新（2026-09-14 核实） |

## D. 环境依赖（非代码问题）

| # | 项 | 现状 |
|---|-----|------|
| D1 | PalORM.Tests 46 项多方言测试 | 本机无 Docker（Container 守卫 fail-closed）；CI 容器环境全跑 |
| D2 | <INTERNAL_TEST_HOST> 服务稳定性 | **状态不稳定，使用前先实测**（2026-09-14 四次变动：早间"全部实测通过" → 13 时半坏（TCP 通 AMQP 无响应 + Kafka 不可达，Messaging 5 失败，ITM-675）→ 约 14 时恢复（8/8）→ 21 时再现半坏（TCP 11ms 可达但 Kafka GetMetadata/Rabbit AMQP 握手均失败，8/8 快速跳过））；半坏特征 = TCP 开、协议无响应。**注**：全跳过时 MTP 退出码为 **8**（非失败码 2）——本地循环判定须区分"环境跳过"（exit 8 且失败=0）与"测试失败"（exit 2） |
| D3 | 工作树 29 个"假脏"M | 行尾归一化 stat 缓存（git diff 全空，零实质改动）；无害，提交时自然消失 |

## E. 已裁决不做（记录在案，勿重提）

- MySQL 三栈租约互斥统一（**ITM-794，2026-09-19 用户裁决「显式接受」**——ADR-024 在案：两侧取舍各有成立面（Dapper/PalORM 保 8.0.18 以下兼容矩阵、EF 保互斥），fencing + at-least-once 幂等分层兜底；消费方指引见 ADR-024 §消费方指引）
- AGPL-3.0-or-later 商业双轨（**2026-09-19 用户裁决「社区项目」**——项目定位社区框架，AGPL 维持，不引入商业授权双轨）
- RequestHash 请求指纹（第 28 轮"先不做"，备忘方案已存 cortex）
- Kafka Produce 批量回调 / ZLogger 结构化门面（第 28 轮裁决，理由在案）
- ORM 优化 11 项不做清单（orm-optimization-deep-analysis §五：EFCore Compiled Models / PalORM configureResilience / MySQL 预备语句 / ~~批量 lease~~（**2026-09-18 翻案，2026-09-19 已实施** `637e73d`：decision-2026-09-17 裁决并落地 EF SQLite 租约单语句批量化——旧结论针对「削弱 fencing」形态，新方案资格检查语句内重估不削弱；验收按「Lease 本体达标」改判，详见 decision 锚点表）/ 序列化等，各有反过度优化理由）

---

**统计**（2026-09-19 任务清单实施完毕）：待裁决 0 · 排队 **2**（B1-B3 major 窗口 + B4 待 CI 数据；**B5-B12 与 ITM-794~803 全部完成**——除 ITM-805 需 CI 结果可见外，2026-09-19 全清单实施闭环，详见各 B 行完成注记与 docs/review/audit-2026-09-19-*.md 系列）· 跟踪 3 · 环境 3 · 已裁决不做 3 类（MySQL 互斥/AGPL 双轨/翻案实施均在案）。
**最近一轮已闭环**：X1 基准（ITM-674）· 覆盖率门禁接线+glob 缺陷修复（47c8c24+277bc34）·
退役延后（ADR-020）· 真库三方言 AOT 实测 · Agent 脚本 C# 规则（3b31cc0）。
