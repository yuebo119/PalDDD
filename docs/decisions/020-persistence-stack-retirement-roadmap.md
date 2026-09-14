# ADR 020：三栈并行策略与 Dapper 栈退役路线

> 状态：**退役延后（2026-09-13 维护者裁决）**——原"已采纳（2026-08-26：v3.0 Obsolete / v4.0 移除）"时间表取消,Dapper 栈转为**能力平等栈**（见下方状态更新）
> 日期：2026-08-26（草案）/ 2026-08-26（采纳）/ **2026-09-13（退役延后修订）**
> 关联：README"Dapper 持久化"、ADR-012（方言项目粒度）、二轮架构评审"三栈并行的姊妹维护成本"、experiment/dapper-aot-full 实验（合并 bffd2c2）

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

---

## 状态更新（2026-09-13：退役延后,Dapper 栈转为能力平等栈）

**触发**：experiment/dapper-aot-full 实验（2026-09-13,合并 bffd2c2）推翻本 ADR 的关键前提,维护者裁决退役延后。

**被推翻的前提**（原"理由 1"引用第 28 轮裁决）：

| 原否决依据（第 28 轮） | 2026-09-13 探针实证 |
|----------------------|-------------------|
| "30 调用点迁移"成本过高 | 34 调用点机械改造一次完成（剥壳脚本） |
| "SQL 模板运行时拼串与 AOT 编译期常量要求冲突" | const 直引/实例属性/switch 选常量三形状全部被编译期常量追踪并生成拦截器——方言分支形状正是被支持的 |
| Dapper.AOT 启用被否决 | 启用后三方言（SQLite/PG 18.4/MySQL 8.4.11 真库）JIT + NativeAOT 二进制各 13/13 全过 |

**新决策**：

1. **退役时间表取消**——v3.0 `[Obsolete]` / v4.0 移除五包不再执行；Dapper 栈转为与 PalORM/EFCore 平等的第三栈。
2. **能力边界声明**（对外文档口径,详见 docs/review/dapper-aot-experiment-2026-09-13.md 抑制声明审计）：Dapper 栈封装 API 面（六接口 × 34 调用点）AOT 实测通过;Dapper.dll 库级非 AOT 干净（上游 46 警告,与调用点路径正交）,绕过封装直用 Dapper 原生 API 在 AOT 下不受支持。
3. **行为变化记录**：SQL 执行层 ct 不可中断（直接重载无 ct 参数,连接超时兜底）;接口签名与连接层 ct 保留。
4. **三栈定位重述**：PalORM（AOT 主线,库级源生成零反射,纯度最高）/ Dapper（手写 SQL,调用点级 AOT,极致性能与控制力）/ EF Core（生态兼容线）。三栈姊妹维护成本照旧,由既有姊妹同步防线（OCE 三族/诊断覆盖门禁）承载。
5. 原决策 2 中"v3.0 窗口合并 IPalOutboxStore 异步化与契约统一"不受影响——仍按 major 窗口推进,仅与退役解绑。
