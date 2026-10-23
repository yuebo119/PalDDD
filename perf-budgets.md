# perf-budgets.md（Pal.DDD 性能预算契约 · EPSL L0 接入件）

> 接入：2026-10-11（EPSL 存量改造补件，Pal.DDD 此前漏列）｜ 本文件是既有基准体系的契约层声明，不改变任何现有阈值。
> 单一执行真源：smoke 分配断言在 `bench/PalDDD.Benchmarks/Program.cs`（退出码执法，变异验红已实证）；测量记录在 `docs/performance.md`。

## 1. 量级声明

| 维度 | 现状 | 说明 |
| --- | --- | --- |
| 基准覆盖 | **38 类**（2026-10-21 口径：Framework 5 + App 2 + Feature 5 + Infra 7 + Persistence 4 + SagaLane 1 + Concurrency 2 + Remote 5 + Delivery 4 + Comparison 2 + Stream 1） | 单一真源 = `bench/PalDDD.Benchmarks/*.cs` 的 `*Benchmarks` 类；入口路由见 `Program.cs`。特性面缺口见 performance.md「未完成项」 |
| 真库车道 | PG / MySQL（wslc 外部实例）/ SQLite（单写者） | 并发形态分化已知：PG 真并行 ~2.8× / MySQL N≥2 死锁（ADR-024 实证）/ SQLite busy 25× |

## 2. SLO 预算（分配为判定层，计时只作趋势层——两轮跨 SDK 实证）

| 指标 | 预算 | 口径 | 执行 |
| --- | --- | --- | --- |
| Validation.Success 分配 | ≤ 1 B/op | `--smoke` 断言（实测 0.000） | Program.cs 退出码执法 |
| Validation.Failed 分配 | ≤ 50 B/op | 同上（实测 40.000） | 同上 |
| Entity.RaiseEvent 分配 | ≤ 140 B/op | 同上（实测 128.001） | 同上 |
| Start* 遥测入口（无 listener）分配 | ≤ 1 B/op | 同上（R1 轮 2026-10-14 增：实测 0.000/0.002；旧码 24.000/24.002 B/op 装箱判红实证；断言落 bench 进程——测试宿主订阅全局 ActivitySource 使 HasListeners 恒真，零分配断言在测试宿主结构性不可行） | 同上 |
| 流式读默认/富化帧（序列化+framing）分配 | ≤ 1 B/item | `StreamFrameSerializer.SerializeAndFrame_Pooled` 与 `WriteItemFrame` 双零分配（2026-10-18 P1 实测 0.000；旧码 56 B/项判红实证） | `StreamFrameAllocationTests` 三件 + `--stream` 微 lane |
| 流式读端点框架可控成本占比 | ≤ 10%（趋势级，不作判定层） | 2026-10-18 实测 1.04 μs / 8.2%，可控分配 0 B；残余 336 B/帧归属基准侧替身与 BDN（同步 sink 三形态 0 B 探针） | `--stream` 端点 lane + 收敛声明（§8） |
| 框架热路径水位 | AddSingleEvent 116.8ns/72B、Create 3.70ns/56B 等（2026-10-08 Medium 首轮） | MediumRun + InProcess + MemoryDiagnoser；跨轮差 <5% 实证 | `--framework` 复跑对照 |
| 统计有效性 | Error/Mean <5% 判定可用；>15% 统计无效（现有 2 项 SmartEnum ns 级，判定以零分配事实为准） | performance.md §自评 | 引用计时前先查自评表 |

## 2.1 profile 与采样档（2026-10-23）

车道、profile、超时与每类采样档的单一真源是 `bench/bench-registry.json`。`Program.cs` 用它选择 BDN job，`scripts/eps-run.cs` 用它编排 profile，`scripts/perf-registry-check.cs` 对账类/方法/case/profile/预算。

| profile | 内容 | 预算 |
| --- | --- | ---: |
| gate | smoke 6 项 + 注册表检查 | ≤10 秒 |
| standard | app/framework/infra/coverage/concurrency/saga/persist/stream + smoke（9 条；persist-mark 留在 full 与聚焦车道） | ≤10 分钟（2026-10-23 实测 592 秒） |
| full | 16 条执行车道全部本地 case；persist-medium 保留为聚焦别名，不在 full 重复跑子集 | ≤45 分钟 |
| remote-smoke | 每方言 2 个代表 case | ≤5 分钟 |
| remote-full | 4 条真库车道 | ≤90 分钟（含本地） |
| strict | 40 迭代 / 20 预热 / 3 launch 参考档 | 按类 |

| mode | 参数 | 用途 | 计时地位 |
| --- | --- | --- | --- |
| alloc | 2 预热 / 3 迭代 / 1 launch / InvocationCount 1 | 分配判定 | 不作计时引用 |
| fixed | 3 / 9 / 1，BDN 自放大调用数 | 趋势 | 趋势级，需 Error 门禁 |
| fixed-short | 2 / 5 / 1，自放大 | 并发趋势 | 趋势级，需比值门禁 |
| auto | 3 / 200 ms / 3 到 5 / 1 | 趋势 | 趋势级，需 Error 门禁 |
| medium-lite | 3 / 5 / 1 | 投递与远程趋势 | 趋势级 |
| tiny | 2 / 3 / 1 | 远程 Auto 慢格 | 趋势级 |
| medium | 10 / 15 / 2 | 参考趋势（原 MediumRun） | 参考 |
| strict | 20 / 40 / 3 | 参考数字 | 参考 |

边界：`alloc` 只做分配判定，计时不进入任何账本；这不是把 `[ShortRunJob]` 重新引入计时基线。趋势档引用前按 §2 的 Error 自评规则判断；参考数字只来自 `medium` 与 `strict`，跨档不比较绝对值。



## 3. 复杂度契约

库形态：热路径 = 领域事件管线（Add/Iterate，已零分配化）与分发链。既有锁定测试承载；新增公共 API 随行为测试补契约断言。

流式读（ADR-030/031）：每项 O(1) 写帧；断线续跑环形缓冲有界（默认 1024 项，溢出丢最旧并发 `event: gap`）；`MaxRunDuration`/`MaxItems`/`DetachGrace` 三硬边界锁定测试在案。禁止无界缓冲与逐 token 持久化。

## 4. 明确不做（引用既有裁决，防重复提案）

| 项 | 出处 |
| --- | --- |
| span decoder 改造 | 2026-09-25 否决留档（performance.md 对照段：GZip 33,704B 回滚基线保持） |
| 类级 [ShortRunJob] 回归 | 已全移除（53% 跨轮差教训，Medium 裁决实证） |

## 5. 门禁接线表

| 预算项 | 执行载体 | 触发 |
| --- | --- | --- |
| smoke 分配断言 | `-- --smoke`（退出码 1 即红）；CI `build-and-test` 的 `perf-smoke` 步骤 + 本地跑测 | 每提交 / 每次跑测（2026-10-18 从人工 TOOL 转每提交门禁） |
| EPSL 快速/全量编排 | `scripts/eps-run.cs -- --quick`（检查件 + smoke）/ `-- --full`（预检 + 16 条执行车道 + 汇总；persist-medium 为聚焦别名）/ `-- --profile standard`（≤10 分钟）/ `-- --profile remote-smoke|remote-full`；全量要求独占，失败默认重试 1 次；`--skip-build` 带二进制新鲜度检查；profile 实测超过 targetsSeconds 时打印 WARN 并写入 summary（不阻断，静态门禁仍由 perf-registry-check 执行） | 节点审计 / 常规夜间 / 全量复跑 |
| 流式零分配锁定 | `StreamFrameAllocationTests` + `--stream` 微 lane | 每次测试 / 节点审计 |
| Medium 水位对照 | `--framework` / `--app` / `--infra` / `--sqlgen` 复跑 | 节点审计 |
| 回归自动归因 | EPSL perf-bisect（**设计稿，未实现**：~/.zcode/perf-template/audit/bisect-design.md）；当前走人工 commit 区间 + 交替复测 | 门禁红且复测确认非环境态 |

## 6. 修订触发

新增基准车道、真库口径变化、SLO 重谈时，本文件与 Program.cs 断言阈值同批次更新。**基准类数/车道清单变化时同步 §1 与 `docs/performance.md` 当前结论**，并跑 `node ~/.zcode/scripts/eps-perf-check.mjs --project .`（声明与实际类数一致性）。

## 7. 结果归档三层（EPSL v1.7）

| 层 | 位置 | 入库 |
| --- | --- | --- |
| 结论报告 | `bench/reports/`（当前由 BDN artifacts 与 performance.md 轮次段承载） | 否 |
| 过程日志 | `bench/logs/`（保留最近 20 份） | 否 |
| 数据真源 | `bench/results/`（入库；归档约定见 `bench/results/README.md`）；BDN report-github.md 在 bin 下，不入库、可复跑再生 | 是（仅 `bench/results/`） |

每次 `eps-run` 还写 `bench/logs/eps-run-<stamp>-summary.json`（profile、lane、mode、退出码、耗时、尝试次数），供跨轮比较；它不替代 `bench/results/` 的入库数据。

## 8. 收敛判定与复活条件

流式读链路已收口（2026-10-18）：框架可控 1.04 μs / 8.2%、可控分配 0 B、92% 属传输层；残余 336 B/帧归属基准侧替身与 BDN 机械。复活条件：框架占比回升 ≥20% / 消费方持续 >5k 项每秒或 10k 并发流 / 生产 flush 占端到端 <50% / 出现流级横切需求。其他链路收口按 EPSL §4.9 模板执行。

## 9. 单一真源

本文件是门禁阈值唯一事实源；基准类数、门禁基线、测试计数等易变事实以源码/项目文件为真源，本文件引用带时点。EPSL 全局版本与模板引用由 `eps-perf-check.mjs` 对账。
