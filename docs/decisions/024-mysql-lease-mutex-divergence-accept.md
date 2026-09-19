# ADR 024：MySQL 三栈租约互斥分叉 — 显式接受现状

> 状态：**已采纳（2026-09-19 用户裁决「显式接受」）**
> 日期：2026-09-19
> 关联：ADR-020（三栈并行策略）、ADR-017（Saga 租约乐观并发）、ITM-794（首席审计 A-1，
> 论证：`docs/review/audit-2026-09-19-findings-confirmation.md`）

## 背景

MySQL 方言的 Outbox 租约获取在三栈上存在互斥语义分叉（首席审计 A-1，三处 SQL 逐行亲证）：

- **EF 栈**（`MySqlOutboxDbContext.cs:116-127`）：JOIN 派生表内 `FOR UPDATE SKIP LOCKED`——
  并发双 worker 败者租他行，互斥强保证。
- **Dapper 栈**（`SqlTemplates.cs:127-131`）与 **PalORM 栈**（`PalOrmOutboxStore.cs:126-135`）：
  同 JOIN 形态**无锁子句**——语义为 last-writer-wins，且 v43 声明补充：(A写,A读,B写,B读)
  交错时序下后写者覆盖先写者租约并回读整批，构成**双 worker 同批双执行**（重复投递）。

分叉根源是两侧**有意的取舍**（亲证补充，PalORM:129-130）：Dapper/PalORM 弃行锁以避开
MySQL 派生表锁的版本兼容矩阵（8.0.18 以下行为差异）；EF 栈弃兼容面保互斥。

## 决策

**显式接受现状分叉，不统一。** 理由：

1. **两侧取舍各有成立面**：Dapper/PalORM 的兼容矩阵理由真实（8.0.18 以下部署存在），
   统一 SKIP LOCKED 意味着收窄这两栈的部署面——为消解一个已兜底的窗口违反部署承诺。
2. **正确性兜底已分层在位**：fencing token（(locked_by, locked_until) 终态守卫）防双终态写；
   重复投递在 at-least-once 投递契约内，由下游幂等消费兜底（Outbox 契约本身要求消费方幂等）。
3. **统一成本不对称**：三栈行为变更 + 多实例并发回归 + 版本兼容矩阵变更，换取的是
   触发条件受限（同批同 tick 交错）且已有兜底的窗口收窄——成本高于收益。

## 消费方指引（本决策的对外契约）

- **多实例 Dapper/PalORM MySQL 部署**：重复投递率结构性高于 EF 部署——消费方**必须**
  实现幂等消费（本框架 Outbox 契约的既有要求，非新增负担；本决策明确该要求在
  Dapper/PalORM MySQL 场景下不是理论项而是实际项）。
- **需要强互斥的 MySQL 场景**：选 EF 栈（SKIP LOCKED），或升级至 MySQL 8.0.18+ 后
  自行在 Dapper/PalORM 的 SqlTemplates 基础上添加派生表锁（模板是 public const，可复制改造）。
- SQLite/PG 方言不受本决策影响（SQLite 单写者天然互斥；PG 三栈一致 SKIP LOCKED）。

## 被否决方案

- **统一为 SKIP LOCKED**（ITM-794 原始选项 A）：收窄 Dapper/PalORM 的 MySQL 版本
  兼容面 + 三栈行为变更回归成本——被"部署承诺优先"否决。
- **Dapper/PalORM 加可选锁子句参数**：配置面爆炸（三栈 × 有锁/无锁 × 方言），
  且默认值无论怎么选都改变现有消费者行为——被复杂度否决。

## 后续动作

- ITM-794 关闭（本 ADR 即裁决）；ITM-807（三栈谓词对照测试）仍执行——对照测试固化
  **现状**形态（含本分叉作为白名单项），防的是"无意的"漂移而非本决策接受的分叉。
- PalORM/Dapper 的 v13/v43 分叉声明注释与本 ADR 互为印证，无需修改。
