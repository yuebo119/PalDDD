# Inbox 批量认领令牌列（claim_token，MySQL）

> 日期：2026-10-22
> 影响面：仅 MySQL 方言的 `inbox_messages` 表与 Dapper MySQL 批量认领路径
> （`DapperInboxStore.TryStartProcessingManyAsync`）；PG/SQLite 与 EF/PalORM 栈不变。

## 变更

新增可空列 `claim_token CHAR(26)`（`NULL` = 未被批量认领过）。批量认领由「SELECT 既有行 →
插入缺失行 → 回读本次插入 → 既有可重入行逐条 CAS」改为「单语句 UPSERT + 按令牌回读」：
每 400 行块固定 2 次往返；原形态在可重入失败行上为每行 2 到 3 次（400 行重投约 800 次）。

归属身份从时间戳换成令牌的原因：MySQL 无 `UPDATE ... RETURNING`，用
`processing_started_at=@now` 回读在同一 now 的并发认领下会把他人的行认成自己的
（R14 F4 实证，真库用例 `Inbox_MySql_BatchClaim_SameTimestamp_DoesNotClaimTwice` 锁定）。
令牌每次调用新生成（ULID），回读按 `(consumer_name, claim_token, message_id IN 本块)`。

## 升级

```sql
ALTER TABLE inbox_messages ADD COLUMN claim_token CHAR(26) NULL AFTER last_error;
```

不建索引：回读的 `message_id IN (...)` 走既有唯一索引 `idx_inbox_unique`，令牌只是过滤条件。
升级前的存量行 `claim_token` 为 NULL，与新建行同义（首次批量认领写入）。

## 行为

- 可重入（Pending / Failed / 租约过期 Processing）行被接管：`attempts+1`、
  `processing_started_at=@now`、`last_error=NULL`、`claim_token=新令牌`。
- Processed 与活跃 Processing 行保持原状（令牌不变），不会被本次回读命中。
- 终态标记（`MarkProcessedAsync`/`MarkFailedAsync`）不清理令牌：令牌全局唯一，残留值不影响后续认领。
- 单条认领路径（`TryStartProcessingAsync`）不使用该列，行为逐字不变。

## 验证

真库（MySQL 8.4.11，本机容器）SQL 级探针：5 行种子（Pending / Processed / 活跃 Processing /
过期 Processing / Failed）经一次 UPSERT 后回读到 4 行（不含 Processed 与活跃 Processing）；
同一 now 的第二次 UPSERT 回读 0 行；租约过期后（now+6min、超时 5min）5 行全部可接管且
`attempts` 各 +1。

未覆盖：C# 端到端需要 Testcontainers/Docker，由 CI 的 `dialect-probe` 与
`Inbox_MySql_BatchClaim_SameTimestamp_DoesNotClaimTwice` 覆盖。
