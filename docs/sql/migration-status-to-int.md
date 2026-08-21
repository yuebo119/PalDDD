-- ============================================================
-- 存量数据库迁移：outbox/inbox status 列 字符串 → INT（三十八轮统一）
-- ============================================================
-- 适用对象：使用 Pal.DDD ≤ 1.1.0（Dapper 栈字符串状态语义）建立的数据库。
-- 新部署直接使用 docs/sql/{postgresql,mysql,sqlite}/000_schema.sql，无需本脚本。
--
-- 背景（三十八轮统一，2026-08-21）：
--   outbox_messages / inbox_messages 的 status 列从字符串枚举（'Pending' 等）
--   统一为 INT 数值——对齐 Saga/Checkpoint/Idempotency 三表的既定 int 语义
--   与 PalORM/EFCore 适配层的映射。五表状态列自此全部 INT。
--
-- 枚举数值契约：
--   OutboxStatus: Pending=0, Processed=1, Dead=2
--   InboxStatus:  Pending=0, Processing=1, Processed=2, Failed=3
--
-- ⚠️ 执行顺序：先停写 → 跑数据转换 → 改列类型 → 升级应用。列类型修改后
--    旧版本应用（按字符串比较）将无法正确读写。
-- ============================================================

-- ─────────────── PostgreSQL ───────────────

UPDATE outbox_messages SET status = CASE status
    WHEN 'Pending' THEN '0'
    WHEN 'Processed' THEN '1'
    WHEN 'Dead' THEN '2'
    ELSE '0'
END;
UPDATE inbox_messages SET status = CASE status
    WHEN 'Pending' THEN '0'
    WHEN 'Processing' THEN '1'
    WHEN 'Processed' THEN '2'
    WHEN 'Failed' THEN '3'
    ELSE '0'
END;
ALTER TABLE outbox_messages ALTER COLUMN status TYPE INTEGER USING status::INTEGER;
ALTER TABLE inbox_messages ALTER COLUMN status TYPE INTEGER USING status::INTEGER;

-- ─────────────── MySQL ───────────────

UPDATE outbox_messages SET status = CASE status
    WHEN 'Pending' THEN '0'
    WHEN 'Processed' THEN '1'
    WHEN 'Dead' THEN '2'
    ELSE '0'
END;
UPDATE inbox_messages SET status = CASE status
    WHEN 'Pending' THEN '0'
    WHEN 'Processing' THEN '1'
    WHEN 'Processed' THEN '2'
    WHEN 'Failed' THEN '3'
    ELSE '0'
END;
ALTER TABLE outbox_messages MODIFY COLUMN status INT NOT NULL DEFAULT 0;
ALTER TABLE inbox_messages MODIFY COLUMN status INT NOT NULL DEFAULT 0;

-- ─────────────── SQLite（不支持 ALTER COLUMN TYPE，需重建列）───────────────
-- 以 outbox_messages 为例（inbox_messages 同型；建议整库重建时直接用现行 DDL）：

-- CREATE TABLE outbox_messages_new (... status INTEGER NOT NULL DEFAULT 0, ...);
-- INSERT INTO outbox_messages_new SELECT id, type, ..., CASE status
--     WHEN 'Pending' THEN 0 WHEN 'Processed' THEN 1 WHEN 'Dead' THEN 2 ELSE 0 END, ...
-- FROM outbox_messages;
-- DROP TABLE outbox_messages;
-- ALTER TABLE outbox_messages_new RENAME TO outbox_messages;
