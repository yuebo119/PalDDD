# Outbox 可用时刻折叠迁移（2026-10-21）

## 1. 变更

Outbox 的租约资格从两列 OR 谓词：

```sql
status = 0
AND (next_attempt_at IS NULL OR next_attempt_at <= now)
AND (locked_until IS NULL OR locked_until <= now)
```

改为单列范围谓词：

```sql
status = 0
AND next_attempt_at <= now
ORDER BY next_attempt_at, created_at
LIMIT n
```

`next_attempt_at` 成为唯一的“可用时刻”：新建消息写存储层写入时刻（显式调度时保留
调用方值），租约时改写为 `locked_until`，重试/重新入队写新的退避时间，终态清空。
`locked_until` 继续用于 fencing 和持租回读，不再参与到期判断。

## 2. 为什么不用新增 `due_at` 生成列

- SQLite 同时存在 Dapper `"O"` 文本和 EF/PalORM provider 空格文本，生成列的比较语义
  会随写入栈分叉。
- PG/MySQL 的 stored generated column 需要表重写或在线 DDL，且 EF 模型要增加
  provider 特定 shadow property。
- 复用 `next_attempt_at` 不新增列、不改公共模型，索引可直接建在现有列上。

## 3. 迁移顺序（必须先回填再升级查询）

### 3.1 停写或排空租约

升级前停止 Outbox worker，或至少停止旧版本实例。旧版本租约只写 `locked_until`、
不写 `next_attempt_at`，与新查询混跑会让旧活跃租约被新实例视为不可见或误判。
滚动升级若无法停写，必须在升级窗口内循环执行 3.2 的回填直到零违例。

### 3.2 回填存量行

PG / MySQL：

```sql
UPDATE outbox_messages
SET next_attempt_at = GREATEST(
    COALESCE(next_attempt_at, created_at),
    COALESCE(locked_until, created_at))
WHERE status = 0;
```

SQLite 在应用侧逐行读取 `next_attempt_at`、`locked_until`、`created_at`，按
`DateTimeOffset` 计算最大值后写回。不要在 SQLite 里直接 `MAX()` 文本列，Dapper 与
EF/PalORM 的时间文本格式不同。

### 3.3 校验零违例

```sql
SELECT COUNT(*) FROM outbox_messages
WHERE status = 0
  AND (next_attempt_at IS NULL
       OR (locked_until IS NOT NULL AND next_attempt_at < locked_until));
```

必须为 0。第一条覆盖未回填行，第二条覆盖旧租约行的可用时刻早于租约到期。

### 3.4 建新索引

```sql
-- PostgreSQL
CREATE INDEX CONCURRENTLY idx_outbox_due_next
ON outbox_messages(next_attempt_at, created_at) WHERE status = 0;

-- SQLite
CREATE INDEX idx_outbox_due_next ON outbox_messages(next_attempt_at, created_at)
WHERE status = 0;

-- MySQL
ALTER TABLE outbox_messages
ADD INDEX idx_outbox_due_next (status, next_attempt_at, created_at),
ALGORITHM=INPLACE, LOCK=NONE;

-- SQL Server（如使用）
CREATE INDEX idx_outbox_due_next ON outbox_messages(status, next_attempt_at, created_at);
```

`EXPLAIN` / `EXPLAIN QUERY PLAN` 必须显示按 `next_attempt_at` 范围扫描，且无额外
`Sort`（索引已同时提供 `next_attempt_at, created_at` 顺序）。

### 3.5 部署新代码

新版本所有插入路径写 `next_attempt_at = COALESCE(显式值, 存储层写入时刻)`，所有租约路径在
同一 UPDATE 中写 `next_attempt_at = until`。部署后重新开启 worker。

### 3.6 收缩

稳定运行一个发布周期后：

```sql
DROP INDEX idx_outbox_due; -- 旧 (status, created_at) 索引
ALTER TABLE outbox_messages RENAME INDEX idx_outbox_due_next TO idx_outbox_due; -- MySQL
-- PG/SQLite 用 ALTER INDEX / DROP + CREATE 等价操作
```

可选契约：待所有写入方升级后，增加 `CHECK (status <> 0 OR next_attempt_at IS NOT NULL)`，
防止绕过 Store 的直写重新引入 NULL。加约束前先跑 3.3 的零违例查询。

## 4. 回滚

代码回滚到旧查询即可，旧谓词仍能处理新行（新行 `next_attempt_at` 非空，旧谓词
`next_attempt_at <= now` 分支命中）。索引回滚：

```sql
DROP INDEX idx_outbox_due_next;
CREATE INDEX idx_outbox_due ON outbox_messages(created_at) WHERE status = 0; -- PG/SQLite
```

MySQL 恢复 `(status, created_at)` 索引。

## 5. 实测（2026-10-21，wslc PG 18.6 / MySQL 8.4.11）

`dotnet run --project samples/PalDDD.DapperAotProbe -c Release -- --provider <Pg|MySql> --outbox-migration`

探针播种 100,000 行混合积压：90,000 未来重试行（位于 `created_at` 头部）、9,000 无
`next_attempt_at` 的到期行、1,000 旧活跃租约（`locked_until` 未来、`next_attempt_at` NULL）。
流程为旧索引 + 旧谓词量测 → 回填 → 零违例校验 → 新索引 + 新谓词量测，5 次中位数。

| 方言 | 旧谓词 + 旧索引 | 新谓词 + 新索引 | 比值 | 回填违例 | EXPLAIN |
|---|---:|---:|---:|---:|---|
| PostgreSQL | 6.95ms | 0.34ms | 20.15× | 0 | 命中 `idx_outbox_due`，无全表扫描 |
| MySQL | 196.91ms | 0.61ms | 324.82× | 0 | 命中 `idx_outbox_due`，无全表扫描 |

口径说明：两组同时切换谓词与索引，是“迁移前/迁移后”组合收益，不是单谓词微基准。
探针只用于迁移验收，不进入生产路径。

## 6. 验收

- 四栈：新建消息 `NextAttemptAt == CreatedAt`；租约后 `NextAttemptAt == LockedUntil`；
  租约有效期内二次租约为空；到期后可再租。
- `GetPendingMessagesAsync` 不返回未来 `next_attempt_at` 行。
- PG/MySQL `EXPLAIN` 走新索引；SQLite `EXPLAIN QUERY PLAN` 无临时 B-tree 排序。
- 回填后 3.3 查询为 0；回滚演练通过。
