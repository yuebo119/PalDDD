# Inbox 状态索引迁移（2026-11-04，批次 C 读侧补齐）

## 1. 背景

`IInboxStore` 新增 `GetByStatusAsync(InboxStatus status, int maxCount, CancellationToken ct)`，
供运维列举失败列表与滞留处理中的消息。四栈统一语义为
`WHERE status = @status ORDER BY id LIMIT @maxCount`。

索引核查结论（对四个方言逐一核对现有 DDL）：

| 方言 | Inbox 现状 | 是否覆盖 |
|---|---|---|
| EF Core | `(status, processing_started_at)` + `(status, processed_at)` | 覆盖（前导列 status） |
| MySQL | `(status, processed_at)` | 覆盖 |
| SQLite | `(status, processed_at)` | 覆盖 |
| PostgreSQL | 仅 `(processed_at) WHERE status = 2` | **只覆盖 Processed(2)**，Pending(0)/Processing(1)/Failed(3) 顺序扫描 |

Outbox 无需新增：PG 的两个互补部分索引
`idx_outbox_due (next_attempt_at, created_at) WHERE status = 0` 与
`idx_outbox_processed (processed_at) WHERE status <> 0` 已覆盖全部三个已定义状态
（Pending=0 / Processed=1 / Dead=2）。

## 2. 变更

PostgreSQL 新增一个索引：

```sql
CREATE INDEX idx_inbox_status ON inbox_messages(status);
```

## 3. 为什么不是三个部分索引

低基数列（4 个枚举值）上的 btree 写成本可忽略，而三个部分索引要维护三份索引结构，
收益为零。单一 `(status)` 索引同时覆盖四种状态的查询，且对保留清理
（`idx_inbox_processed`）无影响。

## 4. 迁移顺序

1. 停写非必须：这是纯新增索引，不改变任何查询语义，滚动升级安全。
2. 执行 DDL（大表建议 `CREATE INDEX CONCURRENTLY`，避免阻塞写入）：

   ```sql
   CREATE INDEX CONCURRENTLY idx_inbox_status ON inbox_messages(status);
   ```

3. 若 `CONCURRENTLY` 失败（事务块内不可用），回退普通 `CREATE INDEX` 并在低峰执行。

## 5. 回滚

```sql
DROP INDEX idx_inbox_status;
```

读侧 API 在索引缺失时仍正确（退化为顺序扫描），故回滚无功能影响、只影响性能。
