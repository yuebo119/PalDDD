# Saga 到期索引迁移（M1）

> 日期：2026-10-19
> 关联：`docs/review/perf-migration-plan-2026-10-19.md` M1、ADR-033 开放决策点

## 变更

`saga_states` 增加 `next_check_at` 列与 `(status, next_check_at)` 索引。应用侧
`SagaState.NextCheckAt` 由 `Saga<TState>` 在步骤开始/完成时维护；终态写 NULL。
`SagaProcessorOptions.EnableDueTimeFilter` 默认 `true` 且 store 实现
`IDueTimeSagaStore<TState>` 时，后台扫描只租约
`next_check_at IS NULL OR next_check_at <= now` 的行。

活动 Saga 的 `next_check_at` 恒有值（2026-10-21）：

| 状态 | 写入值 |
| --- | --- |
| 有带 Timeout 且已开始的步骤 | 最早 `StepStartedAt + Timeout` |
| 无待检查超时步骤 | 哨兵 `SagaState.NoPendingTimeout` = 9999-12-31 00:00:00 UTC |
| 终态（Completed/Compensated/...） | NULL |

不用 `DateTimeOffset.MaxValue` 作哨兵：其 7 位小数秒加时区偏移超出 MySQL
`DATETIME(6)` 范围，写入即错。NULL 只剩一种来源——M1 迁移前的存量行，到期谓词把
NULL 视为到期，首次扫描后由 `SagaProcessor` 回填，因此**不需要独立的回填阶段**。

## DDL（expand）

PostgreSQL：

```sql
ALTER TABLE saga_states ADD COLUMN IF NOT EXISTS next_check_at TIMESTAMPTZ;
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_saga_due ON saga_states(status, next_check_at);
```

MySQL：

```sql
ALTER TABLE saga_states ADD COLUMN next_check_at DATETIME(6) NULL;
CREATE INDEX idx_saga_due ON saga_states(status, next_check_at);
```

SQLite：

```sql
ALTER TABLE saga_states ADD COLUMN next_check_at TEXT;
CREATE INDEX idx_saga_due ON saga_states(status, next_check_at);
```

## 回填

`NextCheckAt` 依赖代码中的步骤 Timeout 定义，不能只靠 SQL 回填。2026-10-21 起不需要
手工回填阶段：

1. 部署本版本即可（`EnableDueTimeFilter` 默认 `true`）。
2. 存量行的 `next_check_at` 为 NULL，到期谓词把它视为到期；该行被租到后
   `SagaProcessor` 重算并回填（无待检查超时写哨兵），每行只付一次。
3. 观察收敛：`SELECT COUNT(*) FROM saga_states WHERE status IN (0,5) AND next_check_at IS NULL`
   会随扫描轮次降到 0。存量行超过 `TimeoutScanBatchSize` 时按批逐轮收敛，不影响正确性。
4. 回退：置 `EnableDueTimeFilter=false` 恢复全量扫描；索引与列可保留。

SQL 侧核对（可选）：

```sql
SELECT COUNT(*) FILTER (WHERE next_check_at IS NULL) AS legacy_rows,
       COUNT(*) FILTER (WHERE next_check_at = TIMESTAMPTZ '9999-12-31 00:00:00+00') AS no_pending
FROM saga_states WHERE status IN (0, 5);
```

## 验收

- 10k 活跃 Saga、`TimeoutScanBatchSize=256`：单 tick 写量不超过批量；尾部超时不超过
  2 个轮询周期。
- 三方言：SQLite/PG/MySQL 的 `LeaseDueSagasAsync` 返回行与 `IsTimedOut` 判定一致；
  `NextCheckAt` 回读无时区漂移。
- 旧路径回归：`EnableDueTimeFilter=false` 时现有全量扫描测试全绿。

## 已知边界

- `NextCheckAt` 为 NULL 只代表迁移前存量行，到期路径把它当作到期并懒回填；哨兵
  9999-12-31 表示“当前状态无待检查超时”，到期路径自然跳过。
- 步骤重入不刷新 `StepStartedAt`（v17 既有语义），`NextCheckAt` 随之保持首次开始时间。
- 2026-10-20：到期租约的排序从 `created_at` 改为 `next_check_at, created_at`
  （Dapper/PalORM/EF 到期路径同步），最早到期者先被租；到期但未超时的行在处理后
  重算并推进 `NextCheckAt`，避免同一批行每 tick 重复被租、饿死后续 Saga。
  2026-10-21：该推进写哨兵而非 NULL，`EnableDueTimeFilter` 默认改为 `true`。

## 真库演练（2026-10-19）

命令（库名须 `palddd_bench` 前缀）：

```bash
# PALDDD_BENCH_PG_CONN / PALDDD_BENCH_MYSQL_CONN 由环境注入（库名须 palddd_bench 前缀）
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m1-probe-pg
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m1-probe-mysql
```

探针在旧 schema 上执行 `ALTER TABLE ... ADD next_check_at` 与 `idx_saga_due`，播种
due/future/NULL 三条 Saga，然后调用 `LeaseDueSagasAsync` 与 `LeaseActiveSagasAsync`。
两方言结果一致：`M1 PASS: due=1, active-non-due=2, index=idx_saga_due`。

## 10k 规模演练（2026-10-20）

```bash
# PALDDD_BENCH_PG_CONN / PALDDD_BENCH_MYSQL_CONN 由环境注入（库名须 palddd_bench 前缀）
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m1-load-pg
dotnet run -c Release --project bench/PalDDD.Benchmarks -- --m1-load-mysql
```

口径：10,000 条 Active Saga，`NextCheckAt` 在 100s 内均匀分布；轮询 2s、批量 256
（相对默认 30s 轮询做时间轴压缩，容量比相同：每 tick 约 200 条到期，批量上限 256）；
5s 播种宽限不计入延迟。`maxPerTick` 断言不超过 256；每行租约延迟
（租到时刻减 `NextCheckAt`）断言不超过 2 个轮询周期（4s）。

| 方言 | leased | ticks | maxPerTick | p50 | p99 | max | 阈值 |
|---|---|---|---|---|---|---|---|
| PostgreSQL | 10,000 | 53 | 202 | 1011ms | 1996ms | 2101ms | 4000ms |
| MySQL | 10,000 | 52 | 202 | 1023ms | 2003ms | 2034ms | 4000ms |

两方言均通过：尾部最大约 2.1s，约 1.05 个轮询周期。该结果覆盖到期索引与租约循环；
存量行回填由运行时懒回填完成（2026-10-21 起无需独立回填阶段）。

