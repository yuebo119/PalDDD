# 性能索引迁移（2026-10-20）

本轮把 Outbox 租约查询与 EventLog 追加路径的索引形状对齐到实际谓词。全部步骤可在线执行
（PG 使用 `CONCURRENTLY`，MySQL 使用 InnoDB Online DDL）。改前请先在影子库执行
`EXPLAIN (ANALYZE, BUFFERS)` 确认计划。

## 1. Outbox 租约索引

查询形态：

```sql
SELECT ... FROM outbox_messages
WHERE status = 0 AND retry_count < @maxRetryCount
  AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
  AND (locked_until IS NULL OR locked_until <= @now)
ORDER BY created_at LIMIT @n;
```

旧索引 `(status, next_attempt_at, locked_until)` 不含排序列，优化器需要扫完待租集再排序，
成本随待租积压增长。2026-10-20 真库实测（20 万行、1000 条待租）：PG 0.279ms → 0.061ms，
MySQL 12.2ms → 1.59ms。

### PostgreSQL

```sql
CREATE INDEX CONCURRENTLY idx_outbox_due
    ON outbox_messages(created_at) WHERE status = 0;

DROP INDEX CONCURRENTLY IF EXISTS idx_outbox_status;

-- 只索引持租行，避免历史 NULL 行把索引撑大
CREATE INDEX CONCURRENTLY idx_outbox_lease_holder_v2
    ON outbox_messages(locked_by, locked_until) WHERE locked_by IS NOT NULL;
DROP INDEX CONCURRENTLY IF EXISTS idx_outbox_lease_holder;
ALTER INDEX idx_outbox_lease_holder_v2 RENAME TO idx_outbox_lease_holder;
```

`idx_outbox_created` 暂时保留给保留清理任务使用；启用 Retention 并按 `processed_at` 清理后，
可评估删除它。

### MySQL

```sql
ALTER TABLE outbox_messages
    ADD INDEX idx_outbox_due (status, created_at),
    DROP INDEX idx_outbox_status;
```

MySQL 无部分索引，`(status, created_at)` 用 status 前缀固定扫描范围，按索引序直接取满批次。
`idx_outbox_created` 同样保留给清理任务。

### SQLite

```sql
CREATE INDEX IF NOT EXISTS idx_outbox_due
    ON outbox_messages(created_at) WHERE status = 0;
DROP INDEX IF EXISTS idx_outbox_status;
DROP INDEX IF EXISTS idx_outbox_lease_holder;
CREATE INDEX IF NOT EXISTS idx_outbox_lease_holder
    ON outbox_messages(locked_by, locked_until) WHERE locked_by IS NOT NULL;
```

### EF Core 用户

`OutboxDbContext.OnModelCreating` 的索引已同步为 `(Status, CreatedAt)`。使用 EF 迁移的项目
需生成并审查一个新迁移；使用手写 DDL 的项目按上述脚本执行。

## 2. EventLog 冗余索引

`global_position` 是主键（PG `BIGSERIAL`、MySQL `AUTO_INCREMENT`、SQLite rowid），
独立的 `idx_events_global` 纯冗余，移除后不影响 `ReadAllAsync` 的范围扫描与排序。

```sql
-- PostgreSQL
DROP INDEX CONCURRENTLY IF EXISTS idx_events_global;

-- MySQL
ALTER TABLE events DROP INDEX idx_events_global;

-- SQLite
DROP INDEX IF EXISTS idx_events_global;
```

## 3. M4 event_id 收缩（有条件）

M4 的 expand 步骤已同时写入 `event_id` 文本列与 `event_id_bin` 二进制列。收缩必须先确认回填
完整，否则重复事件 ID 检测会失效。

前置条件：

```sql
SELECT COUNT(*) FROM events WHERE event_id_bin IS NULL;
-- 必须为 0
```

步骤：

```sql
-- 1. 二进制列唯一化（PG 在线；MySQL/SQLite 相应调整）
CREATE UNIQUE INDEX CONCURRENTLY ux_events_event_id_bin ON events(event_id_bin);

-- 2. 应用侧置 DapperEventLog.UseBinaryEventId / PalOrmEventLog.UseBinaryEventId = true，
--    并在影子库验证 ReadStream/ReadAll 与重复 ID 冲突分类。

-- 3. 删除文本唯一索引（保留文本列作为审计/兼容读列）
DROP INDEX CONCURRENTLY IF EXISTS idx_events_event_id;

-- 4. 删除被唯一索引取代的非唯一二进制索引
DROP INDEX CONCURRENTLY IF EXISTS idx_events_event_id_bin;
```

回滚：重新创建 `idx_events_event_id` 唯一索引并把 `UseBinaryEventId` 置回 `false`。文本列在
收缩期间始终双写，回滚不丢数据。

## 4. 验收

- `EXPLAIN (ANALYZE, BUFFERS)` 中租约查询不再出现 `Sort`。
- 1 万与 10 万待租场景的租约耗时不超过 1 千待租的 2 倍。
- EventLog 追加的索引数量从 5 降到 4（完成 M4 收缩后为 3）。
- 迁移失败时按各节回滚语句恢复，索引创建失败不会影响既有查询。
