-- MySQL 建表脚本（InnoDB 引擎）
CREATE TABLE outbox_messages (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    type            TEXT NOT NULL,
    payload         MEDIUMBLOB NOT NULL,
    content_type    VARCHAR(255) NOT NULL DEFAULT 'application/json',
    schema_version  INT NOT NULL DEFAULT 1,
    status          VARCHAR(20) NOT NULL DEFAULT 'Pending',
    retry_count     INT NOT NULL DEFAULT 0,
    error           TEXT,
    created_at      DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    processed_at    DATETIME(3),
    next_attempt_at DATETIME(3),
    locked_by       VARCHAR(255),
    locked_until    DATETIME(3),
    correlation_id  CHAR(36),
    causation_id    CHAR(36),
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
    received_at           DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    processing_started_at DATETIME(3),
    processed_at          DATETIME(3),
    attempts              INT NOT NULL DEFAULT 1,
    last_error            TEXT,
    UNIQUE INDEX idx_inbox_unique (consumer_name, message_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE saga_states (
    saga_id       CHAR(36) PRIMARY KEY,
    current_state TEXT NOT NULL,
    status        INT NOT NULL DEFAULT 0,
    created_at    DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    completed_at  DATETIME(3),
    error         TEXT,
    error_at      DATETIME(3),
    version       INT NOT NULL DEFAULT 0,
    saga_data     JSON,
    leased_by     VARCHAR(255),
    leased_until  DATETIME(3),
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
    recorded_at     DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    actor_id        VARCHAR(255),
    reason          VARCHAR(255),
    UNIQUE INDEX idx_events_stream (stream_name, stream_version),
    INDEX idx_events_global (global_position)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
