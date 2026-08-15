# ADR 017：Saga 租约并发策略 — 维持乐观并发，不引入跨方言 SKIP LOCKED

> 状态：已采纳
> 日期：2026-08-15
> 关联：ITM-066（SagaStateDbContext 冲突优雅降级）、ADR-013

## 背景

全量评审（2026-08-15）发现 `SagaStateDbContext.LeaseActiveSagasAsync` 是"读-改-写"三步：先 LINQ 查询候选、内存改租约字段、SaveChanges 提交。多实例并发租约同批 Saga 时，Version 乐观令牌使第二个实例抛 `DbUpdateConcurrencyException`。同库 Outbox 的 PostgreSQL 版（PostgreSqlOutboxDbContext）已用 `FOR UPDATE SKIP LOCKED` 单语句原子租约，两侧机制不一致。

ITM-066 已实现最小修复：捕获并发冲突视为"本轮未获取租约"，下轮重试。

## 决策

**维持乐观并发 + 冲突静默重试，不引入 Saga 侧跨方言 SKIP LOCKED。**

## 理由

1. **冲突代价可接受**：租约时长为分钟级，tick 间隔通常秒级——两实例同 tick 抢同批的概率低，冲突方损失一轮空转（已静默处理），不丢数据、不损租约正确性（Version 令牌保证只有一个赢家）。
2. **SQLite 不支持**：`FOR UPDATE SKIP LOCKED` 在 SQLite（单写者模型）不可用。引入需按方言分叉租约 SQL + 双路径测试矩阵，复杂度/收益比不成立——Saga 租约查询带 Status/LeasedUntil 复合条件，无法像 Outbox 那样用一条简单原生 SQL 统一表达。
3. **EF Core LINQ 无法表达**：SKIP LOCKED 需 FromSqlRaw 原生 SQL per dialect，绕过 LINQ 组合能力，Saga 侧复合查询条件翻译维护成本高。

## 触发重评条件

多实例部署实测出现租约轮盘退化（日志中 DbUpdateConcurrencyException 频率与 tick 数同量级）时，为 PostgreSQL/MySQL 覆写原生租约 SQL（参照 PostgreSqlOutboxDbContext 模式），SQLite 维持乐观并发。

## 后果

- ITM-066 的 catch 降级成为正式契约（本 ADR 背书）。
- 多实例 Saga 吞吐上限由"每 tick 至多一个实例获得租约批次"约束；单实例无影响。
