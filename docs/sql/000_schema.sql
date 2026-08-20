-- ============================================================
-- Pal.DDD 数据库建表脚本（通用 ANSI 参考模板）
-- ============================================================
-- 使用说明：
--   1. 本文件是跨方言参考模板（AUTOINCREMENT/TIMESTAMP 为示意语法，非任一方言可直接执行）
--   2. 实际部署请使用 docs/sql/{postgresql,mysql,sqlite}/000_schema.sql 方言脚本
--   3. Dapper 适配器 + Dapper.AOT SG 已自动处理列名映射
-- ============================================================

-- ── Outbox 发件箱消息表 ──
CREATE TABLE outbox_messages (
    id              TEXT    PRIMARY KEY,   -- Ulid 26 字符（代码侧始终显式提供，非自增）；MySQL: CHAR(26)
    type            TEXT    NOT NULL,
    payload         BLOB    NOT NULL,  -- 代码侧 byte[]；PG: BYTEA / MySQL: MEDIUMBLOB / SQLite: BLOB
    content_type    TEXT    NOT NULL DEFAULT 'application/json',
    schema_version  INTEGER NOT NULL DEFAULT 1,
    status          TEXT    NOT NULL DEFAULT 'Pending',  -- Pending | Processing | Processed | Dead
    retry_count     INTEGER NOT NULL DEFAULT 0,
    error           TEXT,
    created_at      TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    processed_at    TIMESTAMP,
    next_attempt_at TIMESTAMP,
    locked_by       TEXT,
    locked_until    TIMESTAMP,
    correlation_id  TEXT,   -- 审计：关联 Ulid（26 字符）
    causation_id    TEXT,   -- 审计：因果 Ulid（26 字符）
    trace_parent    TEXT,   -- 审计：W3C traceparent
    trace_state     TEXT    -- 审计：W3C tracestate
);

CREATE INDEX idx_outbox_status ON outbox_messages(status, next_attempt_at, locked_until);
CREATE INDEX idx_outbox_created ON outbox_messages(created_at);

-- ── Inbox 收件箱幂等消费表 ──
CREATE TABLE inbox_messages (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id            TEXT    NOT NULL,  -- 全局消息 ID
    consumer_name         TEXT    NOT NULL,  -- 消费者标识
    status                TEXT    NOT NULL DEFAULT 'Processing',  -- Processing | Processed | Failed
    received_at           TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    processing_started_at TIMESTAMP,
    processed_at          TIMESTAMP,
    attempts              INTEGER NOT NULL DEFAULT 1,
    last_error            TEXT
);

CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id);

-- ── Saga 状态持久化表 ──
CREATE TABLE saga_states (
    saga_id       TEXT    PRIMARY KEY,  -- Ulid 26 字符（代码传字符串，非 UUID）；MySQL: CHAR(26)
    current_state TEXT    NOT NULL,
    status        INTEGER NOT NULL DEFAULT 0,  -- 0:Active 1:Completed 2:Compensated 3:CompensationFailed 4:DeadLettered
    created_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at  TIMESTAMP,
    error         TEXT,
    error_at      TIMESTAMP,
    version       INTEGER NOT NULL DEFAULT 0,  -- 乐观并发控制
    saga_data     TEXT,                        -- 完整 Saga 状态快照（provider schema 可用 JSON/JSONB）
    leased_by     TEXT,
    leased_until  TIMESTAMP
);

CREATE INDEX idx_saga_status ON saga_states(status, created_at);
CREATE INDEX idx_saga_lease ON saga_states(status, leased_until, created_at);

-- ── Event Log 事件流水表 ──
CREATE TABLE events (
    global_position BIGINT PRIMARY KEY AUTOINCREMENT,  -- PG: BIGSERIAL
    event_id        TEXT    NOT NULL,
    event_name      TEXT    NOT NULL,
    stream_name     TEXT    NOT NULL,
    stream_version  BIGINT  NOT NULL,
    schema_version  INTEGER NOT NULL DEFAULT 1,
    content_type    TEXT    NOT NULL DEFAULT 'application/json',
    payload         BLOB    NOT NULL,  -- 零拷贝：MEMORY/COPY 列读取
    metadata        BLOB,
    recorded_at     TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    actor_id        TEXT,
    reason          TEXT,
    correlation_id  TEXT,   -- 审计：关联 Ulid（26 字符）
    causation_id    TEXT,   -- 审计：因果 Ulid（26 字符）
    trace_parent    TEXT,   -- 审计：W3C traceparent
    trace_state     TEXT    -- 审计：W3C tracestate
);

CREATE UNIQUE INDEX idx_events_stream ON events(stream_name, stream_version);
CREATE UNIQUE INDEX IF NOT EXISTS idx_events_event_id ON events(event_id);
CREATE INDEX idx_events_global ON events(global_position);

-- ── Idempotency 幂等记录表（P3-010 补充）──
-- P2 修复（十轮）：列名 key → idempotency_key、唯一索引 → 复合主键——对齐
-- PalOrmIdempotencyStore 全部 DML 与测试 schema（此前按本脚本部署首条 DML 即报列不存在）
CREATE TABLE idempotency_records (
    operation_name    TEXT    NOT NULL,
    idempotency_key   TEXT    NOT NULL,
    status            INTEGER NOT NULL DEFAULT 0,  -- 0:Started 1:Completed 2:Failed
    locked_until      TIMESTAMP NOT NULL,
    expires_at        TIMESTAMP NOT NULL,
    updated_at        TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    response_payload  TEXT,                         -- 成功响应快照（Base64 for PalORM）
    error             TEXT,
    PRIMARY KEY (operation_name, idempotency_key)
);

CREATE INDEX idx_idempotency_expires ON idempotency_records(expires_at);

-- ── Projection Checkpoint 投影检查点表（P3-010 补充）──
-- P2 修复（九轮评审）：主键改为 (projection_name, source_name, position) 三列复合——
-- 代码侧全部 DML 以三列为键（DapperProjectionCheckpointStore），此前两列唯一索引
-- 会让同 (projection,source) 的第二个 position 被 ON CONFLICT 静默吞掉（位置互相覆盖）。
CREATE TABLE projection_checkpoints (
    projection_name   TEXT    NOT NULL,
    source_name       TEXT    NOT NULL,
    position          TEXT    NOT NULL,             -- 流位置（ULID/数字，按 source 类型；复合主键成员，无默认值）
    status            INTEGER NOT NULL DEFAULT 0,   -- 0:Idle 1:Processing 2:Completed 3:Failed
    updated_at        TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lease_until       TIMESTAMP,
    revision          INTEGER NOT NULL DEFAULT 0,   -- 乐观并发控制令牌
    error             TEXT,
    PRIMARY KEY (projection_name, source_name, position)
);

CREATE INDEX idx_checkpoint_status ON projection_checkpoints(projection_name, source_name, status);

-- ── SQL Server 用户：请使用 EF Core 适配器 + DbContext.OnModelCreating ──
