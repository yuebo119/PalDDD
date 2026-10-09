# ADR 025：MySQL 租约双模板 — 版本探测启用 SKIP LOCKED（修订 ADR-024）

> 状态：**已采纳（2026-10-09 用户裁决——方案选型与探测线 8.0.18 均用户确认）**
> 日期：2026-10-09
> 关联：ADR-024（被修订——其「不统一」决策对 MySQL ≥8.0.18 部分失效，<8.0.18 行为不变）、
> ADR-020（三栈并行策略）、`docs/performance.md`（死锁实证与 SKIP LOCKED 形态实测）、
> `bench/PalDDD.Benchmarks/DapperRemoteOutboxBenchmarks.cs`（回归车道）

## 背景

ADR-024（2026-09-19）显式接受 Dapper/PalORM MySQL 租约 JOIN 无锁子句与 EF 栈 SKIP LOCKED
的分叉，理由两条：MySQL <8.0.18 派生表锁语义差异（统一 = 收窄部署面）；三栈回归成本
高于收益（当时**不存在多实例并发基准**）。

2026-10-08/09 性能系统补齐并发车道后获得两条新事实：

1. **无锁形态的并发后果是死锁异常**：Dapper/PalORM MySQL N≥2 并发租约全部
   InnoDB 死锁（`Deadlock found`，两轮 8 处实录）——比 ADR-024 记录的「重复投递窗口」
   更严重（异常中断先于重复投递发生）。
2. **SKIP LOCKED 形态已实测且成本反转**：EF 栈步骤 1 SQL（JOIN 派生表 FOR UPDATE
   SKIP LOCKED）在 MySQL 8.4.11 同库同负载零死锁、互斥不变量全波通过、串行→8W 加速
   1.75×（`MySqlSkipLockedShapeBenchmarks`）；且并发车道现成，回归成本断崖式下降。

## 决策

**版本探测双模板**：Dapper/PalORM 的 MySQL 租约在运行时探测服务器版本——
MySQL ≥8.0.18（与 EF 栈声明对齐）使用派生表 `FOR UPDATE SKIP LOCKED` 模板；
旧版 / MariaDB（派生表锁语义独立矩阵，未验证）/ 探测失败一律回退原无锁 JOIN 模板
（**行为与 ADR-024 接受的现状逐字一致**）。

- 判定逻辑单点：`PalDDD.Transactions.MySqlLeasePolicy`（纯函数，双栈共用，fail-safe：
  任何不确定回退无锁模板）；
- Dapper 栈：连接 ServerVersion 属性（零往返）；PalORM 栈：首次 MySQL 租约
  `SELECT VERSION()` 一次并缓存于 store 实例（DataSession 不暴露连接版本属性）；
- 探测线 **8.0.18**（用户裁决，与 EF 栈 `MySqlOutboxDbContext` 声明对齐——MySQL 8.0
  小版本有派生表锁 bug 史，不更保守地放宽）。

## 消费方指引（本决策的对外契约）

- **MySQL ≥8.0.18**：Dapper/PalORM 多实例并发租约零死锁且互斥（实测口径）——
  幂等消费仍是 Outbox 契约要求，但 ADR-024 补记的「死锁重试」实际要求在此版本段解除；
- **MySQL <8.0.18 / MariaDB**：行为与 ADR-024 现状一致（幂等消费 + 死锁重试照旧），
  需要强互斥时按 ADR-024 原指引升级版本或选 EF 栈；
- 运维核对：`SELECT VERSION()` 确认 ≥8.0.18 且非 MariaDB 发行版，即已在新模板保护内。

## 被否决方案

- **无条件 SKIP LOCKED**（收窄 <8.0.18 部署面，ADR-024 原否决选项 A 的复刻）——
  版本探测使其不再必要；
- **GET_LOCK 互斥**：全版本可用但串行化损失并行收益（实测 SKIP LOCKED 1.75× 加速），
  且有断连持锁风险；
- **调用方死锁重试 / READ COMMITTED 隔离**：只消死锁不消双租窗口，不改善正确性
  （READ COMMITTED 另有实测待办，见后续动作）。

## 后续动作

1. ✅ 已随本决策完成：策略纯函数单测（边界 8.0.17/8.0.18、MariaDB、垃圾串 fail-safe）；
   Dapper/PalORM 双栈接线；wslc MySQL 并发车道复跑验证（Dapper+PalORM N≥2 零死锁，
   数字入 `docs/performance.md`）。
2. ⬜ 旧版回归：`mysql:8.0.17` 镜像在并发车道复跑，确认回退模板行为与现状逐字一致
   （需拉取旧版镜像，登记于 `docs/performance.md` 未完成项）。
3. ⬜ READ COMMITTED 对照实验（被否决方案的留档验证：连接串一参数复跑车道，
   量化「只消死锁」路线的实际效果，供未来同类决策参考）。
4. CI 方言探针（dialect-probe，MySQL 8.4.11）自动覆盖新模板路径——现有 Outbox 族
   断言即回归面，无需新增探针。
