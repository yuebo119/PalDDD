# 租约回读索引升级（2026-09-17 审计 P-2）

> 适用对象：**存量部署**的数据库。新建库直接用 `docs/sql/{postgresql,mysql,sqlite}/000_schema.sql`
> 即已包含本索引（3c887c7 起）。EF Core 栈由 `OutboxDbContext` 的 `HasIndex` 承担，
> 走 EF 迁移的部署无需手工执行。

## 为什么

Outbox 租约回读谓词 `WHERE locked_by = … AND locked_until < …`（及各栈对应的
重租/回收查询）此前无覆盖索引，全表扫描随 outbox_messages 增长线性劣化。
`(locked_by, locked_until)` 复合索引覆盖该谓词（审计 2026-09-17 P-2）。

## 存量库升级语句

### PostgreSQL / SQLite

两者均支持幂等写法，可直接重复执行：

```sql
CREATE INDEX IF NOT EXISTS idx_outbox_lease_holder
    ON outbox_messages(locked_by, locked_until);
```

### MySQL

MySQL 8 的 `CREATE INDEX` **不支持** `IF NOT EXISTS`（MariaDB 支持）。二选一：

```sql
-- 方式一（推荐）：先查后建
SET @idx_exists = (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema = DATABASE()
      AND table_name = 'outbox_messages'
      AND index_name = 'idx_outbox_lease_holder');
SET @ddl = IF(@idx_exists = 0,
    'ALTER TABLE outbox_messages ADD INDEX idx_outbox_lease_holder (locked_by, locked_until)',
    'SELECT ''index already exists''');
PREPARE stmt FROM @ddl;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- 方式二（人工确认不存在后直执）
ALTER TABLE outbox_messages ADD INDEX idx_outbox_lease_holder (locked_by, locked_until);
```

## 验证

```sql
-- 三方言通用：确认索引存在
-- PG / MySQL
SELECT indexname FROM pg_indexes WHERE indexname = 'idx_outbox_lease_holder';        -- PG
SHOW INDEX FROM outbox_messages WHERE Key_name = 'idx_outbox_lease_holder';          -- MySQL
-- SQLite
PRAGMA index_list('outbox_messages');                                                -- 应列出 idx_outbox_lease_holder
```
