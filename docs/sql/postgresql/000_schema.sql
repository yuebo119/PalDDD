-- PostgreSQL 建表脚本（DDD Clean Architecture 适配）
CREATE TABLE outbox_messages (
    id              BIGSERIAL PRIMARY KEY,
    type            TEXT NOT NULL,
    payload         BYTEA NOT NULL,
    content_type    TEXT NOT NULL DEFAULT 'application/json',
    schema_version  INTEGER NOT NULL DEFAULT 1,
    status          TEXT NOT NULL DEFAULT 'Pending',
    retry_count     INTEGER NOT NULL DEFAULT 0,
    error           TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at    TIMESTAMPTZ,
    next_attempt_at TIMESTAMPTZ,
    locked_by       TEXT,
    locked_until    TIMESTAMPTZ,
    correlation_id  UUID,
    causation_id    UUID,
    trace_parent    TEXT,
    trace_state     TEXT
);
CREATE INDEX idx_outbox_status ON outbox_messages(status, next_attempt_at, locked_until) WHERE status = 'Pending';
CREATE INDEX idx_outbox_created ON outbox_messages(created_at);

CREATE TABLE inbox_messages (
    id                    BIGSERIAL PRIMARY KEY,
    message_id            TEXT NOT NULL,
    consumer_name         TEXT NOT NULL,
    status                TEXT NOT NULL DEFAULT 'Processing',
    received_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processing_started_at TIMESTAMPTZ,
    processed_at          TIMESTAMPTZ,
    attempts              INTEGER NOT NULL DEFAULT 1,
    last_error            TEXT
);
CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id);

CREATE TABLE saga_states (
    saga_id       UUID PRIMARY KEY,
    current_state TEXT NOT NULL,
    status        INTEGER NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at  TIMESTAMPTZ,
    error         TEXT,
    error_at      TIMESTAMPTZ,
    version       INTEGER NOT NULL DEFAULT 0,
    saga_data     JSONB,
    leased_by     TEXT,
    leased_until  TIMESTAMPTZ
);
CREATE INDEX idx_saga_status ON saga_states(status, created_at);
CREATE INDEX idx_saga_lease ON saga_states(status, leased_until, created_at);

CREATE TABLE events (
    global_position BIGSERIAL PRIMARY KEY,
    event_id        TEXT NOT NULL,
    event_name      TEXT NOT NULL,
    stream_name     TEXT NOT NULL,
    stream_version  BIGINT NOT NULL,
    schema_version  INTEGER NOT NULL DEFAULT 1,
    content_type    TEXT NOT NULL DEFAULT 'application/json',
    payload         BYTEA NOT NULL,
    metadata        BYTEA,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    actor_id        TEXT,
    reason          TEXT
);
CREATE UNIQUE INDEX idx_events_stream ON events(stream_name, stream_version);
CREATE INDEX idx_events_global ON events(global_position);
