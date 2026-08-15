-- ============================================================
-- Pal.DDD 数据库建表脚本（通用 ANSI SQL）
-- ============================================================
-- 使用说明：
--   1. 根据数据库类型选择对应的注释块
--   2. PostgreSQL 用户：直接执行此脚本
--   3. SQLite/MySQL 用户：按注释调整语法
--   4. Dapper 适配器 + Dapper.AOT SG 已自动处理列名映射
-- ============================================================

-- ── Outbox 发件箱消息表 ──
CREATE TABLE outbox_messages (
    id              TEXT    PRIMARY KEY,   -- Ulid 26 字符（代码侧始终显式提供，非自增）；MySQL: CHAR(26)
    type            TEXT    NOT NULL,
    payload         TEXT    NOT NULL,
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
CREATE INDEX idx_events_global ON events(global_position);

-- ── Idempotency 幂等记录表（P3-010 补充）──
CREATE TABLE idempotency_records (
    operation_name    TEXT    NOT NULL,
    key               TEXT    NOT NULL,
    status            INTEGER NOT NULL DEFAULT 0,  -- 0:Started 1:Completed 2:Failed
    locked_until      TIMESTAMP,
    expires_at        TIMESTAMP NOT NULL,
    updated_at        TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    response_payload  TEXT,                         -- 成功响应快照（Base64 for PalORM）
    error             TEXT
);

CREATE UNIQUE INDEX idx_idempotency_unique ON idempotency_records(operation_name, key);
CREATE INDEX idx_idempotency_expires ON idempotency_records(expires_at);

-- ── Projection Checkpoint 投影检查点表（P3-010 补充）──
CREATE TABLE projection_checkpoints (
    projection_name   TEXT    NOT NULL,
    source_name       TEXT    NOT NULL,
    position          TEXT    NOT NULL DEFAULT '',  -- 流位置（ULID/数字，按 source 类型）
    status            INTEGER NOT NULL DEFAULT 0,   -- 0:Idle 1:Processing 2:Completed 3:Failed
    updated_at        TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lease_until       TIMESTAMP,
    revision          INTEGER NOT NULL DEFAULT 0,   -- 乐观并发控制令牌
    error             TEXT
);

CREATE UNIQUE INDEX idx_checkpoint_unique ON projection_checkpoints(projection_name, source_name);

-- ── SQL Server 用户：请使用 EF Core 适配器 + DbContext.OnModelCreating ──
