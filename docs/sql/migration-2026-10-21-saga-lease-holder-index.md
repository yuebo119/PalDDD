# Saga 租约回读索引迁移（2026-10-21）

## 背景

Saga 租约回读谓词为 `leased_by = @owner AND leased_until = @until`（EF PG/MySQL/SQLite/SQL Server 的 `LeaseVia*Async` 回读、Dapper/PalORM MySQL 两步回读）。此前 `saga_states` 只有 `(status, leased_until, created_at)` 与 `(status, next_check_at)`，没有以 `leased_by` 开头的索引，回读随终态历史增长退化为全表扫描。

## PostgreSQL

```sql
CREATE INDEX CONCURRENTLY idx_saga_lease_holder
ON saga_states(leased_by, leased_until)
WHERE leased_by IS NOT NULL;
```

回滚：

```sql
DROP INDEX CONCURRENTLY IF EXISTS idx_saga_lease_holder;
```

## MySQL

```sql
ALTER TABLE saga_states
  ADD INDEX idx_saga_lease_holder (leased_by, leased_until);
```

回滚：

```sql
ALTER TABLE saga_states DROP INDEX idx_saga_lease_holder;
```

## SQLite

```sql
CREATE INDEX IF NOT EXISTS idx_saga_lease_holder
ON saga_states(leased_by, leased_until)
WHERE leased_by IS NOT NULL;
```

回滚：

```sql
DROP INDEX IF EXISTS idx_saga_lease_holder;
```

## SQL Server

EF 模型已声明该索引，使用 EF Migrations 的部署会随模型生成：

```sql
CREATE INDEX idx_saga_lease_holder
ON saga_states(leased_by, leased_until)
WHERE leased_by IS NOT NULL;
```

回滚：

```sql
DROP INDEX IF EXISTS idx_saga_lease_holder ON saga_states;
```

## 验证

- 加索引前后对租约回读执行 `EXPLAIN`：应出现 `Index Scan` / `ref` 访问，不再出现 `Seq Scan` / `ALL`。
- 该索引与 `idx_saga_lease (status, leased_until, created_at)` 用途不同：前者服务回读等值对，后者服务租约资格扫描；两者都保留。
