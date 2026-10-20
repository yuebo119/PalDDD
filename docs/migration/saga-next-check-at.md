# Saga 到期索引迁移（M1）

> 日期：2026-10-19
> 关联：`docs/review/perf-migration-plan-2026-10-19.md` M1、ADR-033 开放决策点

## 变更

`saga_states` 增加 `next_check_at` 列与 `(status, next_check_at)` 索引。应用侧
`SagaState.NextCheckAt` 由 `Saga<TState>` 在步骤开始/完成时维护；终态写 NULL。
`SagaProcessorOptions.EnableDueTimeFilter=true` 且 store 实现 `IDueTimeSagaStore<TState>`
时，后台扫描只租约 `next_check_at IS NOT NULL AND next_check_at <= now` 的行。

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

`NextCheckAt` 依赖代码中的步骤 Timeout 定义，不能只靠 SQL 回填。切换步骤：

1. 部署本版本，保持 `EnableDueTimeFilter=false`（默认）。
2. 旧全量扫描路径会为租到的存量行计算 `NextCheckAt` 并保存；正常事件处理也会为
   新状态写入该值。
3. 观察活跃行：`SELECT COUNT(*) FROM saga_states WHERE status IN (0,5) AND next_check_at IS NULL`
   会包含“当前状态没有待检查超时步骤”的合法行；只对配置了 Timeout 且已开始步骤的
   Saga 需要非 NULL。确认目标集合后置 `EnableDueTimeFilter=true`。
4. 回退：置回 false；索引与列可保留（不影响旧路径）。

## 验收

- 10k 活跃 Saga、`TimeoutScanBatchSize=256`：单 tick 写量不超过批量；尾部超时不超过
  2 个轮询周期。
- 三方言：SQLite/PG/MySQL 的 `LeaseDueSagasAsync` 返回行与 `IsTimedOut` 判定一致；
  `NextCheckAt` 回读无时区漂移。
- 旧路径回归：`EnableDueTimeFilter=false` 时现有全量扫描测试全绿。

## 已知边界

- `NextCheckAt` 为 NULL 表示“当前状态无待检查超时”，到期路径会跳过；回填完成前不要
  直接开启过滤。
- 步骤重入不刷新 `StepStartedAt`（v17 既有语义），`NextCheckAt` 随之保持首次开始时间。

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

两方言均通过：尾部最大约 2.1s，约 1.05 个轮询周期。该结果覆盖到期索引与租约循环，
不覆盖生产库的回填完整性；生产开启 `EnableDueTimeFilter=true` 前仍须完成活跃行回填。

