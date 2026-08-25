# ADR 020：三栈并行策略与 Dapper 栈退役路线

> 状态：已采纳（维护者裁决 2026-08-26：v3.0 Obsolete / v4.0 移除；IPalOutboxStore 契约统一+异步化并入 v3.0 窗口）
> 日期：2026-08-26（草案）/ 2026-08-26（采纳）
> 关联：README"Dapper 持久化 — ⚠️ 不支持 AOT，逐步弃用"、ADR-012（方言项目粒度）、二轮架构评审"三栈并行的姊妹维护成本"

## 背景

Outbox/Inbox/Saga/EventLog/Projection 持久化当前有**三栈并存**（加 InMemory 测试栈共四实现）：

| 栈 | 定位 | AOT | 维护成本信号 |
|----|------|:--:|------------|
| **PalORM 适配** | 战略栈：源生成零反射，完整链路 Native AOT | ✅ | 姊妹重复 ≥15 处（SQL 错误分类已收敛为 SqlErrorClassifier） |
| **EF Core 适配** | 战术栈：EF Core 生态兼容，Lazyloading/迁移用户群体 | ❌（适配器声明 false） | 与 PalORM 栈姊妹平行 |
| **Dapper 统一** | 退役中：手写 SQL，README 已标"逐步弃用" | ❌ | SqlTemplates 15+ 处方言分叉、四轮姊妹修复史 |

多轮审计的实证成本：fencing/契约修复平均需要三栈同步（"姊妹修复"在 40+ 轮提交中反复出现）；跨栈契约分歧（如 IPalOutboxStore 的 MarkProcessed 语义三栈不一致，OutboxStore.cs:42-48 Remarks 自认）是结构性的——同一抽象下安全级别不一致只有收敛栈数量才能根治。

## 决策

1. **战略方向：收敛到 PalORM（AOT 主线）+ EF Core（生态兼容线）双栈**，Dapper 栈进入退役轨道。
2. **退役节奏（维护者已裁决 2026-08-26，按建议节奏执行）**：
   - **现在起**：Dapper 栈只修缺陷不加特性；新特性（新 Store 能力、新表）仅落 PalORM/EFCore 双栈；README 与 docs 的推荐位序把 Dapper 移到"存量维护"段。
   - **v3.0（破坏性变更窗口）**：Dapper 栈标记 `[Obsolete]`（编译期警告 + 迁移指引）；同窗口合并 IPalOutboxStore 异步化与跨栈 fencing 契约统一（两项均已排队的破坏性变更，一次 major 窗口清偿）。
   - **v4.0**：移除 Dapper 五包（PalDDD.Dapper / .MySql / .PostgreSql / .Sqlite 及关联），保留迁移文档。
3. **保留 EF Core 栈的边界承诺**：EF Core 用户的迁移成本来自 DbContext 生态（Interceptor/迁移/LINQ），PalORM 无法覆盖该群体——EFCore 栈是长期共存项，不是退役候选。

## 理由

1. **AOT 定位倒逼**：框架把 Native AOT 作为一等公民（README 定位、CI 三 Provider AOT 验证矩阵），Dapper 栈的运行时路径与该定位根本冲突且无法修复（第 28 轮裁决：Dapper.AOT 启用被否决——30 调用点迁移、SQL 模板运行时拼串与 AOT 编译期常量要求冲突）。
2. **姊妹维护成本是已发生的现金流**（非假设风险）：40+ 轮审计中每轮的"四处同步"修复实证了三栈的边际成本；收敛到双栈直接减 1/3 的契约同步面。
3. **用户选择负担**：三选一的持久化决策对新用户是认知税，双栈各有清晰定位（AOT 性能线 vs EF 生态线）。
4. **渐进而非立即移除**：存量用户（含本仓库 dialect-probe 的 40 断言依赖）需要迁移窗口；Obsolete 先行 + 一个 major 周期的缓冲是 NuGet 生态惯例。

## 触发重评条件

- PalORM 上游（PalORM 5.x）出现重大不兼容演进或停更 → 重估 PalORM 主线风险（单一外部依赖风险已有独立评估义务）。
- EF Core 生态出现原生 AOT 持久化方案 → 双栈可能收敛为一栈。
- 用户反馈 Dapper 栈仍有不可替代场景（如超轻量嵌入式部署）→ 退役降级为"不推荐但不移除"。

## 后果

- 本 ADR 生效后：Dapper 栈的功能冻结原则进入 conventions（新 PR 涉及 Dapper 新特性默认拒绝）。
- 二轮评审 P2 项"IPalOutboxStore 跨栈 fencing 契约统一"并入 v3.0 窗口与本路线图执行。
- dialect-probe 的 Dapper 断言（含本轮新增 AmbientTxDapperSmoke）在 Dapper 包存续期间继续维护。
