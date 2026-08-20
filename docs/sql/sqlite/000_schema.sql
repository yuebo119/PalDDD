-- SQLite 建表脚本（WAL 模式推荐）
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE outbox_messages (
    id              TEXT PRIMARY KEY,  -- Ulid 26 字符（代码侧始终显式提供，非自增）
    type            TEXT NOT NULL,
    payload         BLOB NOT NULL,
    content_type    TEXT NOT NULL DEFAULT 'application/json',
    schema_version  INTEGER NOT NULL DEFAULT 1,
    status          TEXT NOT NULL DEFAULT 'Pending',
    retry_count     INTEGER NOT NULL DEFAULT 0,
    error           TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    processed_at    TEXT,
    next_attempt_at TEXT,
    locked_by       TEXT,
    locked_until    TEXT,
    correlation_id  TEXT,
    causation_id    TEXT,
    trace_parent    TEXT,
    trace_state     TEXT
);
CREATE INDEX idx_outbox_status ON outbox_messages(status, next_attempt_at, locked_until);
CREATE INDEX idx_outbox_created ON outbox_messages(created_at);

CREATE TABLE inbox_messages (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id            TEXT NOT NULL,
    consumer_name         TEXT NOT NULL,
    status                TEXT NOT NULL DEFAULT 'Processing',
    received_at           TEXT NOT NULL DEFAULT (datetime('now')),
    processing_started_at TEXT,
    processed_at          TEXT,
    attempts              INTEGER NOT NULL DEFAULT 1,
    last_error            TEXT
);
CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id);

CREATE TABLE saga_states (
    saga_id       TEXT PRIMARY KEY,
    current_state TEXT NOT NULL,
    status        INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at  TEXT,
    error         TEXT,
    error_at      TEXT,
    version       INTEGER NOT NULL DEFAULT 0,
    saga_data     TEXT,
    leased_by     TEXT,
    leased_until  TEXT
);
CREATE INDEX idx_saga_status ON saga_states(status, created_at);
CREATE INDEX idx_saga_lease ON saga_states(status, leased_until, created_at);

CREATE TABLE events (
    global_position INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id        TEXT NOT NULL,
    event_name      TEXT NOT NULL,
    stream_name     TEXT NOT NULL,
    stream_version  INTEGER NOT NULL,
    schema_version  INTEGER NOT NULL DEFAULT 1,
    content_type    TEXT NOT NULL DEFAULT 'application/json',
    payload         BLOB NOT NULL,
    metadata        BLOB,
    recorded_at     TEXT NOT NULL DEFAULT (datetime('now')),
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

-- P3 修复（九轮评审）：补齐通用脚本已有而方言脚本缺失的两张表
-- ── Idempotency 幂等记录表 ──
CREATE TABLE idempotency_records (
    operation_name    TEXT NOT NULL,
    idempotency_key   TEXT NOT NULL,
    status            INTEGER NOT NULL DEFAULT 0,
    locked_until      TEXT NOT NULL,
    expires_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL DEFAULT (datetime('now')),
    response_payload  TEXT,
    error             TEXT,
    PRIMARY KEY (operation_name, idempotency_key)
);
CREATE INDEX idx_idempotency_expires ON idempotency_records(expires_at);

-- ── Projection Checkpoint 投影检查点表（三列复合主键，对齐代码 DML）──
CREATE TABLE projection_checkpoints (
    projection_name   TEXT    NOT NULL,
    source_name       TEXT    NOT NULL,
    position          TEXT    NOT NULL,
    status            INTEGER NOT NULL DEFAULT 0,
    updated_at        TEXT    NOT NULL DEFAULT (datetime('now')),
    lease_until       TEXT,
    revision          INTEGER NOT NULL DEFAULT 0,
    error             TEXT,
    PRIMARY KEY (projection_name, source_name, position)
);
CREATE INDEX idx_checkpoint_status ON projection_checkpoints(projection_name, source_name, status);
