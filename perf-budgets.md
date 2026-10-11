# perf-budgets.md（Pal.DDD 性能预算契约 · EPSL L0 接入件）

> 接入：2026-10-11（EPSL 存量改造补件，Pal.DDD 此前漏列）｜ 本文件是既有基准体系的契约层声明，不改变任何现有阈值。
> 单一执行真源：smoke 分配断言在 `bench/PalDDD.Benchmarks/Program.cs`（退出码执法，变异验红已实证）；测量记录在 `docs/performance.md`。

## 1. 量级声明

| 维度 | 现状 | 说明 |
| --- | --- | --- |
| 基准覆盖 | 22 类（Framework 5 + App 2 + Infra 7 + Persistence 3 + SagaLane 1 + Concurrency 2 + Remote 2） | 运行入口全覆盖，特性面缺口见 performance.md「未完成项」 |
| 真库车道 | PG / MySQL（wslc 外部实例）/ SQLite（单写者） | 并发形态分化已知：PG 真并行 ~2.8× / MySQL N≥2 死锁（ADR-024 实证）/ SQLite busy 25× |

## 2. SLO 预算（分配为判定层，计时只作趋势层——两轮跨 SDK 实证）

| 指标 | 预算 | 口径 | 执行 |
| --- | --- | --- | --- |
| Validation.Success 分配 | ≤ 1 B/op | `--smoke` 断言（实测 0.000） | Program.cs 退出码执法 |
| Validation.Failed 分配 | ≤ 50 B/op | 同上（实测 40.000） | 同上 |
| Entity.RaiseEvent 分配 | ≤ 140 B/op | 同上（实测 128.001） | 同上 |
| 框架热路径水位 | AddSingleEvent 116.8ns/72B、Create 3.70ns/56B 等（2026-10-08 Medium 首轮） | MediumRun + InProcess + MemoryDiagnoser；跨轮差 <5% 实证 | `--framework` 复跑对照 |
| 统计有效性 | Error/Mean <5% 判定可用；>15% 统计无效（现有 2 项 SmartEnum ns 级，判定以零分配事实为准） | performance.md §自评 | 引用计时前先查自评表 |

## 3. 复杂度契约

库形态：热路径 = 领域事件管线（Add/Iterate，已零分配化）与分发链。既有锁定测试承载；新增公共 API 随行为测试补契约断言。

## 4. 明确不做（引用既有裁决，防重复提案）

| 项 | 出处 |
| --- | --- |
| span decoder 改造 | 2026-09-25 否决留档（performance.md 对照段：GZip 33,704B 回滚基线保持） |
| 类级 [ShortRunJob] 回归 | 已全移除（53% 跨轮差教训，Medium 裁决实证） |

## 5. 门禁接线表

| 预算项 | 执行载体 | 触发 |
| --- | --- | --- |
| smoke 分配断言 | `-- --smoke`（退出码 1 即红） | 每次跑测 |
| Medium 水位对照 | `--framework` / `--app` / `--infra` 复跑 | 节点审计 |
| 回归自动归因 | EPSL perf-bisect（设计稿：~/.zcode/perf-template/audit/bisect-design.md） | 门禁红且复测确认非环境态 |

## 6. 修订触发

新增基准车道、真库口径变化、SLO 重谈时，本文件与 Program.cs 断言阈值同批次更新。
