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
-- 租约候选扫描按 next_attempt_at（可用时刻）过滤并按 (next_attempt_at, created_at) 排序；
-- 部分索引把扫描固定为 O(批次)，不再随待租积压或已处理历史增长。
CREATE INDEX idx_outbox_due ON outbox_messages(next_attempt_at, created_at) WHERE status = 0;
-- 保留清理按 processed_at 分批删除；部分索引只覆盖终态行（Processed/Dead）。
CREATE INDEX idx_outbox_processed ON outbox_messages(processed_at) WHERE status <> 0;
-- 2026-10-21：不建 locked_by/locked_until 索引。PG 租约路径为 UPDATE ... RETURNING，
-- 终态写按主键 id 过滤，没有按租约列回读的查询；该索引只增加每次租约与标记的写成本。
-- MySQL 保留同名索引（其两步回读按 locked_by/locked_until 匹配）。

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
-- 2026-10-21：同 outbox，不建 leased_by/leased_until 索引。PG 租约与回读均为单语句
-- RETURNING，扫描释放按 saga_id 主键；MySQL 的两步回读仍需要该索引，保留在 MySQL 脚本。

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
-- 2026-10-21：不建 event_id_bin 索引。该列只被 SELECT 回传，没有任何查询用它过滤；
-- 每次事件追加却要维护一份 B-tree。文本 event_id 的唯一索引保留（唯一性守卫）。
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

-- ── 序列缓存（2026-10-21）──
-- BIGSERIAL 背后的序列默认 CACHE 1：每次 nextval 推进序列页并落 WAL，高写入下成为
-- 单点争用。这两列只作代理键/全局排序（EventLog 位置预留器本就按块跳号），允许进程
-- 崩溃后最多 100 的跳号，故把持久化开销按 100 次摊薄。已有库执行同一 ALTER 即可，
-- 见 docs/sql/migration-2026-10-21-pg-sequence-cache.md。
ALTER SEQUENCE inbox_messages_id_seq CACHE 100;
ALTER SEQUENCE events_global_position_seq CACHE 100;
