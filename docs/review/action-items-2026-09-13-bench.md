# 行动项台账 — 2026-09-13(X1 基准轮)

> 来源:orm-optimization-deep-analysis-2026-09-13.md 裁决汇总的执行落地
> 前序台账:action-items-2026-09-13-v5.md(全部闭环)

## ITM-672|E1:EFCore DbContext Pooling 解锁(major 窗口)

- **类别**:性能(major 窗口,API 变化)
- **现状**:`OutboxDomainEventInterceptor` 的 `_pending`/`_injectedOutboxIds` 为实例级可变状态,与 `AddDbContextPool` 互斥(ITM-640 三十八轮"声明不修")——本仓 EFCore 栈每请求重建 DbContext。
- **基准佐证**(bench-baseline-2026-09-13):EFCore 栈 Lease 15,669us(5.1× Dapper)、分配 3.65MB(11.4×);Pool 解锁预期"部分改善"而非"显著改善"(慢的主体在 EF 管道而非构造)。
- **方案**:拦截器状态迁 DbContext 派生类字段(拦截器经 `eventData.Context` 取用;EF 官方 per-context 拦截器状态模式,池化 ResetState 天然安全)。代价:消费者 TContext 须继承 `OutboxEventCapableContext` 基类(或实现接口)——破坏性变更。
- **裁决**:与 IPalOutboxStore 异步化同批(major 窗口);落地后跑 `--persist` 基准对比,Ratio >10% 改善则 PalORM/EFCore 差距收窄可期。

## ITM-673|D5:Dapper.AOT ct 调用点入口请愿(上游跟踪)

- **类别**:功能恢复(上游 issue)
- **现状**:Dapper 经典直接重载无 ct 参数(CS1739 实证),`CommandDefinition` 被 DAP057 拒绝——SQL 执行层不可中断(连接超时兜底)。而 Dapper.AOT 1.1.0 内部管道 `Command<T>.QueryBufferedAsync/ExecuteAsync(args, CancellationToken)` **全系数支持 ct**(源码实证),只缺调用点语法入口。
- **行动**:向 DapperLib/DapperAOT 提 issue:请求为拦截器生成提供带 ct 的调用点重载(如 `QueryAsync<T>(sql, param, transaction, commandTimeout, commandType, cancellationToken)` 或 `CommandDefinition` 的 AOT 可读变体)。落地后本仓 34 调用点改形状即恢复。
- **issue 草稿**:docs/review/dapper-aot-ct-issue-draft.md
- **裁决**:跟踪上游;本仓不动。

## ITM-674|X1:三栈持久化基准建立 ✅(本轮完成)

- bench/PalDDD.Benchmarks/PersistenceBenchmarks.cs(三栈 × 5 基准)
- 首批数字:docs/review/bench-baseline-2026-09-13.md
- 后续纪律:三栈改动跑同口径对比;裁决级决策用 --job medium 复测。

## 附带清偿(本轮)

- DapperOutboxStore/DapperInboxStore 三处"AOT 拦截未启用"实验后残留注释(见 ab30dd6)。
