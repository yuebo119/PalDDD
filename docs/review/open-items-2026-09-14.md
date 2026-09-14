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
| B1 | **ITM-672 EFCore Pooling 解锁**（拦截器状态迁 Context 派生字段；基准佐证慢主体在 EF 管道非构造，预期"部分改善"） | v3.0 major 窗口（API 变化） |
| B2 | **IPalOutboxStore 异步化 + 跨栈 fencing 契约统一**（吸收 PalORM 同步 DIM 的 GetResult 阻塞） | 同 v3.0 窗口（ADR-020 保留项） |
| B3 | **[Obsolete] 6 处移除**（Core Attributes ×2"框架零消费"、SqlServerOutboxDbContext 等，tech-debt WARN 项） | v3.0（移除计划已在注解内声明） |
| B4 | **覆盖率门禁阈值重校准** | CI accuracy 触发：coverage job 首跑产出含 Docker 的完整值后按其重校准（47c8c24 声明） |
| B5 | **54 处 docs 会话相对表述改写**（"上一轮/本次"等） | docs 归档整理时按 NAMING.md §七 对照表执行 |

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
| D2 | 192.168.200.120 服务稳定性 | **状态不稳定，使用前先实测**（2026-09-14 三次变动：早间"全部实测通过" → 13 时 RabbitMQ/TCP 通 AMQP 无响应 + Kafka 不可达，Messaging 集成 5 失败（ITM-675）→ 约 14 时恢复，8/8 实测通过）；半坏特征 = TCP 开、协议无响应 |
| D3 | 工作树 29 个"假脏"M | 行尾归一化 stat 缓存（git diff 全空，零实质改动）；无害，提交时自然消失 |

## E. 已裁决不做（记录在案，勿重提）

- RequestHash 请求指纹（第 28 轮"先不做"，备忘方案已存 cortex）
- Kafka Produce 批量回调 / ZLogger 结构化门面（第 28 轮裁决，理由在案）
- ORM 优化 11 项不做清单（orm-optimization-deep-analysis §五：EFCore Compiled Models / PalORM configureResilience / MySQL 预备语句 / 批量 lease / 序列化等，各有反过度优化理由）

---

**统计**：待裁决 6 · 排队 5 · 跟踪 3 · 环境 3 · 已裁决不做 3 类。
**最近一轮已闭环**：X1 基准（ITM-674）· 覆盖率门禁接线+glob 缺陷修复（47c8c24+277bc34）·
退役延后（ADR-020）· 真库三方言 AOT 实测 · Agent 脚本 C# 规则（3b31cc0）。
