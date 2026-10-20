# 迁移：删除三处写放大索引（2026-10-21）

> 适用：已有部署按本文件执行一次；新建部署直接使用更新后的 `000_schema.sql`。
> 目标：减少 outbox、saga、events 三张高写入表的索引维护成本，不改任何查询语义。

## 1. 依据

| 索引 | 现状 | 代码侧事实 |
| --- | --- | --- |
| `idx_events_event_id_bin` | 每次事件追加维护一份 B-tree | 全仓没有以 `event_id_bin` 为过滤条件的查询；该列只被 SELECT 回传 |
| `idx_outbox_lease_holder` | 每次租约、标记、重试都维护 | PG/SQLite 租约走 `UPDATE ... RETURNING`，终态写按主键 `id` 过滤；只有 MySQL 两步回读按 `(locked_by, locked_until)` 匹配 |
| `idx_saga_lease_holder` | 每次租约、释放、保存都维护 | PG/SQLite 租约与回读为单语句 `RETURNING`；只有 MySQL 两步回读需要 |

MySQL 保留两张 `*_lease_holder` 索引，不要删除。三个方言都可以删除 `idx_events_event_id_bin`。

## 2. 执行前核对（PostgreSQL）

```sql
SELECT relname AS table_name, indexrelname AS index_name, idx_scan
FROM pg_stat_user_indexes
WHERE indexrelname IN (
    'idx_events_event_id_bin',
    'idx_outbox_lease_holder',
    'idx_saga_lease_holder'
);
```

`idx_scan` 为 0 或接近 0 才执行删除。统计自上次 `pg_stat_reset` 起累计；重启或统计重置后需重新观察一个完整业务周期。

SQLite 可用 `EXPLAIN QUERY PLAN` 核对目标查询未使用这些索引；MySQL 用 `EXPLAIN` 核对。

## 3. PostgreSQL

`DROP INDEX CONCURRENTLY` 不能放在事务块内，需逐条执行。

```sql
DROP INDEX CONCURRENTLY IF EXISTS idx_events_event_id_bin;
DROP INDEX CONCURRENTLY IF EXISTS idx_outbox_lease_holder;
DROP INDEX CONCURRENTLY IF EXISTS idx_saga_lease_holder;
```

## 4. SQLite

```sql
DROP INDEX IF EXISTS idx_events_event_id_bin;
DROP INDEX IF EXISTS idx_outbox_lease_holder;
DROP INDEX IF EXISTS idx_saga_lease_holder;
```

SQLite 的 `DROP INDEX` 会短暂持有写锁；在 WAL 模式下对在线读影响有限，建议放在低峰执行。

## 5. MySQL

只删除事件表这一处；两张租约索引保留。

```sql
ALTER TABLE events DROP INDEX idx_events_event_id_bin;
```

## 6. 回滚

需要恢复时按原定义重建：

```sql
-- PostgreSQL / SQLite
CREATE INDEX idx_events_event_id_bin ON events(event_id_bin);
CREATE INDEX idx_outbox_lease_holder ON outbox_messages(locked_by, locked_until);
CREATE INDEX idx_saga_lease_holder ON saga_states(leased_by, leased_until);
```

PostgreSQL 的租约索引原带 `WHERE locked_by IS NOT NULL`（saga 为 `WHERE leased_by IS NOT NULL`）部分谓词，回滚时补上。

## 7. 与 EF Core 模型的关系

`EventLogDbContext` 模型已移除 `EventIdBin` 索引，`EnsureCreated` 新建的库不会再创建它。旧库由本迁移删除。`OutboxDbContext` 与 `SagaStateDbContext` 的租约索引保留在模型中，因为 MySQL provider 仍需要它们；PG/SQLite 由脚本建库的部署可安全删除。
