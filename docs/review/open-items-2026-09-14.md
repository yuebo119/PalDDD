# 开放项清单（2026-09-14 全量收账）

> 来源：rule-placement-audit-2026-09-13（规则落位账本）· action-items-2026-09-13-bench（ITM 台账）·
> tech-debt.cs / gate-audit.cs 实测 · 近五轮会话遗留项收账。
> 状态口径：🔴 待裁决 · 🟡 排队（窗口/触发条件）· 🔵 上游/外部跟踪 · ⚪ 已裁决不做（防重提）

## A. 待你裁决/执行

| # | 项 | 现状 | 动作 |
|---|-----|------|------|
| A1 | **dev 推送** | 4+ 提交未推 origin（你此前"暂缓推送"裁决） | 说一声即推（proxy bypass 方式已验证） |
| A2 | **ITM-673 Dapper.AOT ct 上游 issue** | 英文草稿就绪（docs/review/dapper-aot-ct-issue-draft.md） | 需你的 GitHub 账号提交（或授权我用 gh） |
| A3 | **全局 AGENTS.md 规则承载物改造** | 账本 §六建议：每条规则标注 `[脚本]`/`[类型]`/`[人工]`，脚本类压缩为指针 | 判断项，账本明示"未改动全局文件待裁决" |
| A4 | **encoding-gate E5 工作树行尾防线** | 现状：.md/.csproj/.targets 行尾漂移无机械检测（git autocrlf 自愈，仓库层无危害） | 加则按门禁规程（自测+变异+接 hook）半天可交付 |
| A5 | **scripts/gate-audit.cs 未提交** | 工作树 untracked（你会话的门禁可信度矩阵工具，矩阵实测全绿：UNWIRED/REVIEW=0） | 决定：提交 + 按账本建议"按需手跑或并入 CI 前段" |
| A6 | **单模块覆盖率降幅门禁** | 账本 §四 ⏳ 未实现（现只有全局 0.70 阈值）；可行路径已明：ci-coverage 已逐项目产 cobertura，解析与基线表比对 | 立项裁决 |

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
| D2 | 192.168.200.120 服务稳定性 | 曾两度变动（PG/MySQL/Kafka/RabbitMQ 同时 TCP 开、协议无响应）；当前就绪，全部实测通过 |
| D3 | 工作树 29 个"假脏"M | 行尾归一化 stat 缓存（git diff 全空，零实质改动）；无害，提交时自然消失 |

## E. 已裁决不做（记录在案，勿重提）

- RequestHash 请求指纹（第 28 轮"先不做"，备忘方案已存 cortex）
- Kafka Produce 批量回调 / ZLogger 结构化门面（第 28 轮裁决，理由在案）
- ORM 优化 11 项不做清单（orm-optimization-deep-analysis §五：EFCore Compiled Models / PalORM configureResilience / MySQL 预备语句 / 批量 lease / 序列化等，各有反过度优化理由）

---

**统计**：待裁决 6 · 排队 5 · 跟踪 3 · 环境 3 · 已裁决不做 3 类。
**最近一轮已闭环**：X1 基准（ITM-674）· 覆盖率门禁接线+glob 缺陷修复（47c8c24+277bc34）·
退役延后（ADR-020）· 真库三方言 AOT 实测 · Agent 脚本 C# 规则（3b31cc0）。
