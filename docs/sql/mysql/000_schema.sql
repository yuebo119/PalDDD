-- MySQL 建表脚本（InnoDB 引擎）
CREATE TABLE outbox_messages (
    id              CHAR(26) PRIMARY KEY,  -- Ulid 26 字符（代码侧始终显式提供，非自增）
    type            TEXT NOT NULL,
    payload         MEDIUMBLOB NOT NULL,
    content_type    VARCHAR(255) NOT NULL DEFAULT 'application/json',
    schema_version  INT NOT NULL DEFAULT 1,
    status          VARCHAR(20) NOT NULL DEFAULT 'Pending',
    retry_count     INT NOT NULL DEFAULT 0,
    error           TEXT,
    created_at      DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    processed_at    DATETIME(6),
    next_attempt_at DATETIME(6),
    locked_by       VARCHAR(255),
    locked_until    DATETIME(6),
    correlation_id  CHAR(26),  -- 审计：关联 Ulid（26 字符）
    causation_id    CHAR(26),  -- 审计：因果 Ulid（26 字符）
    trace_parent    VARCHAR(255),
    trace_state     VARCHAR(255),
    INDEX idx_outbox_status (status, next_attempt_at, locked_until),
    INDEX idx_outbox_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE inbox_messages (
    id                    BIGINT AUTO_INCREMENT PRIMARY KEY,
    message_id            VARCHAR(255) NOT NULL,
    consumer_name         VARCHAR(255) NOT NULL,
    status                VARCHAR(20) NOT NULL DEFAULT 'Processing',
    received_at           DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    processing_started_at DATETIME(6),
    processed_at          DATETIME(6),
    attempts              INT NOT NULL DEFAULT 1,
    last_error            TEXT,
    UNIQUE INDEX idx_inbox_unique (consumer_name, message_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE saga_states (
    saga_id       CHAR(26) PRIMARY KEY,  -- Ulid 26 字符
    current_state TEXT NOT NULL,
    status        INT NOT NULL DEFAULT 0,
    created_at    DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    completed_at  DATETIME(6),
    error         TEXT,
    error_at      DATETIME(6),
    version       INT NOT NULL DEFAULT 0,
    saga_data     JSON,
    leased_by     VARCHAR(255),
    leased_until  DATETIME(6),
    INDEX idx_saga_status (status, created_at),
    INDEX idx_saga_lease (status, leased_until, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE events (
    global_position BIGINT AUTO_INCREMENT PRIMARY KEY,
    event_id        VARCHAR(255) NOT NULL,
    event_name      VARCHAR(255) NOT NULL,
    stream_name     VARCHAR(255) NOT NULL,
    stream_version  BIGINT NOT NULL,
    schema_version  INT NOT NULL DEFAULT 1,
    content_type    VARCHAR(255) NOT NULL DEFAULT 'application/json',
    payload         MEDIUMBLOB NOT NULL,
    metadata        MEDIUMBLOB,
    recorded_at     DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    actor_id        VARCHAR(255),
    reason          VARCHAR(255),
    correlation_id  CHAR(26),   -- 审计：关联 Ulid（26 字符）
    causation_id    CHAR(26),   -- 审计：因果 Ulid（26 字符）
    trace_parent    VARCHAR(255),
    trace_state     VARCHAR(255),
    UNIQUE INDEX idx_events_event_id (event_id),
    UNIQUE INDEX idx_events_stream (stream_name, stream_version),
    INDEX idx_events_global (global_position)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- P3 修复（九轮评审）：补齐通用脚本已有而方言脚本缺失的两张表
-- ── Idempotency 幂等记录表 ──
CREATE TABLE idempotency_records (
    operation_name    VARCHAR(128) NOT NULL,
    idempotency_key   VARCHAR(255) NOT NULL,
    status            INT NOT NULL DEFAULT 0,
    locked_until      DATETIME(6) NOT NULL,
    expires_at        DATETIME(6) NOT NULL,
    updated_at        DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    response_payload  TEXT,
    error             TEXT,
    PRIMARY KEY (operation_name, idempotency_key),
    INDEX idx_idempotency_expires (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── Projection Checkpoint 投影检查点表（三列复合主键，对齐代码 DML）──
CREATE TABLE projection_checkpoints (
    projection_name   VARCHAR(255) NOT NULL,
    source_name       VARCHAR(255) NOT NULL,
    position          VARCHAR(255) NOT NULL,
    status            INT NOT NULL DEFAULT 0,
    updated_at        DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    lease_until       DATETIME(6),
    revision          INT NOT NULL DEFAULT 0,
    error             TEXT,
    PRIMARY KEY (projection_name, source_name, position),
    INDEX idx_checkpoint_status (projection_name, source_name, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
