-- PostgreSQL 建表脚本（DDD Clean Architecture 适配）
CREATE TABLE outbox_messages (
    id              TEXT PRIMARY KEY,  -- Ulid 26 字符（代码侧始终显式提供，非自增）
    type            TEXT NOT NULL,
    payload         BYTEA NOT NULL,
    content_type    TEXT NOT NULL DEFAULT 'application/json',
    schema_version  INTEGER NOT NULL DEFAULT 1,
    status          INTEGER NOT NULL DEFAULT 0,
    retry_count     INTEGER NOT NULL DEFAULT 0,
    error           TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at    TIMESTAMPTZ,
    next_attempt_at TIMESTAMPTZ,
    locked_by       TEXT,
    locked_until    TIMESTAMPTZ,
    correlation_id  TEXT,   -- 审计：关联 Ulid（26 字符，非 UUID 格式）
    causation_id    TEXT,   -- 审计：因果 Ulid（26 字符，非 UUID 格式）
    trace_parent    TEXT,
    trace_state     TEXT
);
-- 租约候选扫描按 created_at 排序。部分索引把扫描固定为 O(批次)，
-- 不再随待租积压或已处理历史增长（2026-10-20 真库实测：1k 待租/20 万行 0.279ms → 0.061ms）。
CREATE INDEX idx_outbox_due ON outbox_messages(created_at) WHERE status = 0;
-- 保留清理按 processed_at 分批删除；部分索引只覆盖终态行（Processed/Dead）。
CREATE INDEX idx_outbox_processed ON outbox_messages(processed_at) WHERE status <> 0;
-- 租约回读谓词（locked_by + locked_until）覆盖索引；仅索引持租行，避免历史 NULL 行膨胀。
CREATE INDEX idx_outbox_lease_holder ON outbox_messages(locked_by, locked_until) WHERE locked_by IS NOT NULL;

CREATE TABLE inbox_messages (
    id                    BIGSERIAL PRIMARY KEY,
    message_id            TEXT NOT NULL,
    consumer_name         TEXT NOT NULL,
    status                INTEGER NOT NULL DEFAULT 0,
    received_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processing_started_at TIMESTAMPTZ,
    processed_at          TIMESTAMPTZ,
    attempts              INTEGER NOT NULL DEFAULT 1,
    last_error            TEXT
);
CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id);
-- 保留清理按 processed_at 分批删除已处理行；部分索引只覆盖 Processed。
CREATE INDEX idx_inbox_processed ON inbox_messages(processed_at) WHERE status = 2;

CREATE TABLE saga_states (
    saga_id       TEXT PRIMARY KEY,  -- Ulid 26 字符（代码传字符串，非 UUID 格式）
    current_state TEXT NOT NULL,
    status        INTEGER NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at  TIMESTAMPTZ,
    error         TEXT,
    error_at      TIMESTAMPTZ,
    version       INTEGER NOT NULL DEFAULT 0,
    saga_data     JSONB,
    next_check_at TIMESTAMPTZ,
    leased_by     TEXT,
    leased_until  TIMESTAMPTZ
);
CREATE INDEX idx_saga_status ON saga_states(status, created_at);
CREATE INDEX idx_saga_lease ON saga_states(status, leased_until, created_at);
CREATE INDEX idx_saga_due ON saga_states(status, next_check_at);

CREATE TABLE events (
    global_position BIGSERIAL PRIMARY KEY,
    event_id        TEXT NOT NULL,
    event_id_bin    BYTEA,
    event_name      TEXT NOT NULL,
    stream_name     TEXT NOT NULL,
    stream_version  BIGINT NOT NULL,
    schema_version  INTEGER NOT NULL DEFAULT 1,
    content_type    TEXT NOT NULL DEFAULT 'application/json',
    payload         BYTEA NOT NULL,
    metadata        BYTEA,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    actor_id        TEXT,
    reason          TEXT,
    correlation_id  TEXT,   -- 审计：关联 Ulid（26 字符，非 UUID 格式）
    causation_id    TEXT,   -- 审计：因果 Ulid（26 字符，非 UUID 格式）
    trace_parent    TEXT,   -- 审计：W3C traceparent
    trace_state     TEXT    -- 审计：W3C tracestate
);
CREATE UNIQUE INDEX idx_events_stream ON events(stream_name, stream_version);
CREATE UNIQUE INDEX IF NOT EXISTS idx_events_event_id ON events(event_id);
CREATE INDEX IF NOT EXISTS idx_events_event_id_bin ON events(event_id_bin);
-- global_position 是主键，PK 索引已覆盖 ReadAll 的范围扫描与排序；独立索引纯冗余（2026-10-20 移除）。

-- P3 修复（九轮评审）：补齐通用脚本已有而方言脚本缺失的两张表
-- ── Idempotency 幂等记录表 ──
CREATE TABLE idempotency_records (
    operation_name    TEXT    NOT NULL,
    idempotency_key   TEXT    NOT NULL,
    status            INTEGER NOT NULL DEFAULT 0,  -- 0:Processing 1:Completed 2:Failed
    locked_until      TIMESTAMPTZ NOT NULL,
    expires_at        TIMESTAMPTZ NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    response_payload  BYTEA,
    error             TEXT,
        revision         BIGINT NOT NULL DEFAULT 0,                     -- 乐观并发令牌（单调递增，v53 P2）
    PRIMARY KEY (operation_name, idempotency_key)
);
CREATE INDEX idx_idempotency_expires ON idempotency_records(expires_at);

-- ── Projection Checkpoint 投影检查点表（三列复合主键，对齐代码 DML）──
CREATE TABLE projection_checkpoints (
    projection_name   TEXT    NOT NULL,
    source_name       TEXT    NOT NULL,
    position          TEXT    NOT NULL,
    status            INTEGER NOT NULL DEFAULT 0,  -- 0:Processing 1:Completed 2:Failed（枚举 ProjectionCheckpointStatus）
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    lease_until       TIMESTAMPTZ,
    revision          INTEGER NOT NULL DEFAULT 0,
    error             TEXT,
    PRIMARY KEY (projection_name, source_name, position)
);
CREATE INDEX idx_checkpoint_status ON projection_checkpoints(projection_name, source_name, status);
