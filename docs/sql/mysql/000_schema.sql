-- MySQL 建表脚本（InnoDB 引擎）
CREATE TABLE outbox_messages (
    id              CHAR(26) PRIMARY KEY,  -- Ulid 26 字符（代码侧始终显式提供，非自增）
    type            TEXT NOT NULL,
    payload         LONGBLOB NOT NULL,
    content_type    VARCHAR(255) NOT NULL DEFAULT 'application/json',
    schema_version  INT NOT NULL DEFAULT 1,
    status          INT NOT NULL DEFAULT 0,
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
    trace_state     VARCHAR(512),  -- 三十七轮 C2：对齐 EFCore MaxLength(512)
    -- 租约候选扫描按 created_at 排序；(status, created_at) 让扫描按索引序直接取满批次。
    -- 2026-10-20 真库实测：1k 待租/20 万行 12.2ms → 1.59ms。
    INDEX idx_outbox_due (status, created_at),
    -- 保留清理按 processed_at 分批删除终态行；(status, processed_at) 让扫描按索引序取满批次。
    INDEX idx_outbox_processed (status, processed_at),
    -- 租约回读谓词（locked_by + locked_until）覆盖索引。
    INDEX idx_outbox_lease_holder (locked_by, locked_until)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE inbox_messages (
    id                    BIGINT AUTO_INCREMENT PRIMARY KEY,
    message_id            VARCHAR(255) NOT NULL,
    consumer_name         VARCHAR(255) NOT NULL,
    status                INT NOT NULL DEFAULT 0,
    received_at           DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    processing_started_at DATETIME(6),
    processed_at          DATETIME(6),
    attempts              INT NOT NULL DEFAULT 1,
    last_error            TEXT,
    UNIQUE INDEX idx_inbox_unique (consumer_name, message_id),
    INDEX idx_inbox_processed (status, processed_at)
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
    next_check_at DATETIME(6),
    leased_by     VARCHAR(255),
    leased_until  DATETIME(6),
    INDEX idx_saga_status (status, created_at),
    INDEX idx_saga_lease (status, leased_until, created_at),
    INDEX idx_saga_due (status, next_check_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE events (
    global_position BIGINT AUTO_INCREMENT PRIMARY KEY,
    event_id        VARCHAR(255) NOT NULL,
    event_id_bin    BINARY(16) NULL,
    event_name      VARCHAR(255) NOT NULL,
    stream_name     VARCHAR(512) NOT NULL,  -- 三十七轮 C2：对齐 EFCore MaxLength(512)
    stream_version  BIGINT NOT NULL,
    schema_version  INT NOT NULL DEFAULT 1,
    content_type    VARCHAR(255) NOT NULL DEFAULT 'application/json',
    payload         LONGBLOB NOT NULL,
    metadata        LONGBLOB,
    recorded_at     DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    actor_id        VARCHAR(255),
    reason          VARCHAR(2048),  -- 三十七轮 C2：对齐 EFCore MaxLength(2048)
    correlation_id  CHAR(26),   -- 审计：关联 Ulid（26 字符）
    causation_id    CHAR(26),   -- 审计：因果 Ulid（26 字符）
    trace_parent    VARCHAR(255),
    trace_state     VARCHAR(512),  -- 三十七轮 C2：对齐 EFCore MaxLength(512)
    UNIQUE INDEX idx_events_event_id (event_id),
    INDEX idx_events_event_id_bin (event_id_bin),
    UNIQUE INDEX idx_events_stream (stream_name, stream_version)
    -- global_position 是主键，独立索引纯冗余（2026-10-20 移除）。
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
    response_payload  LONGBLOB,
    error             TEXT,
    revision          BIGINT NOT NULL DEFAULT 0,   -- 乐观并发令牌（单调递增，v53 P2；v54 修正：v53 脚本表尾定位漂移错插到 projection_checkpoints）
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
