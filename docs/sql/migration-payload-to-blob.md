-- ============================================================
-- 存量数据库迁移：payload 列 Base64 TEXT → 原生二进制（PalORM 栈）
-- ============================================================
-- 适用对象：使用 Pal.DDD PalORM 适配器 + Base64 TEXT 负载列建立的数据库
--   （PalORM ≤ 5.2 时代 byte[] 经 ByteArrayBase64Converter/Base64 编码存储）。
-- 新部署直接使用 docs/sql/{postgresql,mysql,sqlite}/000_schema.sql（payload 列
--   已是 BYTEA/LONGBLOB/BLOB），无需本脚本。
-- Dapper / EFCore 栈用户的 payload 列本就是二进制列，同样无需本脚本。
--
-- 背景（2026-08-22，随 PalORM 5.3 升级）：
--   PalORM 5.3（ADR-G）原生支持 byte[] 二进制列后，PalORM 适配器已移除
--   ByteArrayBase64Converter——outbox_messages.payload、events.payload、
--   events.metadata、idempotency_records.response_payload 四列与 Dapper/EFCore
--   栈及 docs/sql DDL 统一为原生二进制列（省 33% 体积、免双向编解码、
--   消除 85KB 档 LOH 分配；基准见 PalORM ADR-G）。
--
-- 涉及列：
--   outbox_messages.payload            TEXT(Base64) → 二进制 NOT NULL
--   events.payload                     TEXT(Base64) → 二进制 NOT NULL
--   events.metadata                    TEXT(Base64) → 二进制 NULL
--   idempotency_records.response_payload TEXT(Base64) → 二进制 NULL
--
-- ⚠️ 执行顺序：先停写 → 跑数据转换 → 改列类型 → 升级应用。
--    MySQL 注意：直接 MODIFY TEXT→BLOB 不会解码 Base64（存的是编码文本的字节），
--    必须走下方"加列 → FROM_BASE64 回填 → 换列"三步。
-- ============================================================

-- ─────────────── PostgreSQL ───────────────

ALTER TABLE outbox_messages ALTER COLUMN payload TYPE BYTEA USING decode(payload, 'base64');
ALTER TABLE events ALTER COLUMN payload TYPE BYTEA USING decode(payload, 'base64');
ALTER TABLE events ALTER COLUMN metadata TYPE BYTEA USING decode(metadata, 'base64');
ALTER TABLE idempotency_records ALTER COLUMN response_payload TYPE BYTEA USING decode(response_payload, 'base64');
-- 注：metadata / response_payload 为可空列，decode(NULL) 保持 NULL，无需额外处理。

-- ─────────────── MySQL（三步法，以 outbox_messages 为例，其余三列同型）───────────────

-- 步骤 1：加新二进制列
ALTER TABLE outbox_messages ADD COLUMN payload_blob LONGBLOB;
-- 步骤 2：Base64 解码回填
UPDATE outbox_messages SET payload_blob = FROM_BASE64(payload);
-- 步骤 3：换列（先 DROP 旧列，再把新列 CHANGE 到原名并补 NOT NULL）
ALTER TABLE outbox_messages DROP COLUMN payload,
    CHANGE COLUMN payload_blob payload LONGBLOB NOT NULL;
-- 可空列（events.metadata / idempotency_records.response_payload）同型，
-- 仅步骤 3 的 NOT NULL 省略。

-- ─────────────── SQLite（无内置 unbase64，应用侧脚本读出解码写回）───────────────
-- 以 C# 为例（Microsoft.Data.Sqlite 参数化写回，解码走 Convert.FromBase64String）：
--   SELECT rowid, payload FROM outbox_messages;
--   → Convert.FromBase64String(reader.GetString(0)) →
--   UPDATE outbox_messages SET payload = @blob WHERE rowid = @rowid;
-- 完成后 SQLite 的动态类型即可直接存二进制（列声明改为 BLOB 建议整表重建时顺带做）。
