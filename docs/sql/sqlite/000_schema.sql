-- SQLite 建表脚本（WAL 模式推荐）
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE outbox_messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
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
    reason          TEXT
);
CREATE UNIQUE INDEX idx_events_stream ON events(stream_name, stream_version);
CREATE INDEX idx_events_global ON events(global_position);
