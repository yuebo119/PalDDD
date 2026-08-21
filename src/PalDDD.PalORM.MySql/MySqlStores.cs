using PalORM;
using PalORM.MySql;
using PalDDD.PalORM.Stores;
using PalDDD.Transactions;

namespace PalDDD.PalORM.MySql;

// ─────────────────────────────────────────────────────────────
// MySQL 方言固化中间类
// ════════════════════════════════════════════════════════════
// MySQL 不支持 RETURNING —— TProvider.SupportsReturningClause=false。
// Outbox LeasePending 走两步 UPDATE+SELECT 回读路径（避免重跑子查询）。
// Inbox TryStart 走普通 INSERT + 唯一约束冲突异常捕获路径（三十八轮 P1 回归修复，
// 弃用 ON DUPLICATE KEY UPDATE——MySqlConnector 默认 found rows 使冲突误判为新插入）。
// ─────────────────────────────────────────────────────────────

/// <summary>MySQL 方言 Outbox Store。</summary>
public sealed class MySqlOutboxStore : PalOrmOutboxStore<MySqlProvider>
{
    public MySqlOutboxStore(DataSession<MySqlProvider> session, TimeProvider? clock = null)
        : base(session, clock) { }
}

/// <summary>MySQL 方言 Inbox Store。</summary>
public sealed class MySqlInboxStore : PalOrmInboxStore<MySqlProvider>
{
    public MySqlInboxStore(DataSession<MySqlProvider> session) : base(session) { }
}

/// <summary>MySQL 方言 Saga State Store。</summary>
public sealed class MySqlSagaStateStore<TState> : PalOrmSagaStateStore<MySqlProvider, TState>
    where TState : SagaState, new()
{
    public MySqlSagaStateStore(DataSession<MySqlProvider> session,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState>? jsonTypeInfo = null,
        TimeProvider? clock = null)
        : base(session, jsonTypeInfo, clock) { }  // P3（十八轮验证轮 F1）：透传 clock——便捷注册解析容器 TimeProvider
}

/// <summary>MySQL 方言 EventLog Store。</summary>
public sealed class MySqlEventLog : PalOrmEventLog<MySqlProvider>
{
    public MySqlEventLog(DataSession<MySqlProvider> session, TimeProvider? clock = null)
        : base(session, clock) { }
}

/// <summary>MySQL 方言 Projection Checkpoint Store。</summary>
public sealed class MySqlProjectionCheckpointStore : PalOrmProjectionCheckpointStore<MySqlProvider>
{
    public MySqlProjectionCheckpointStore(DataSession<MySqlProvider> session) : base(session) { }
}

/// <summary>MySQL 方言 Idempotency Store。</summary>
public sealed class MySqlIdempotencyStore : PalOrmIdempotencyStore<MySqlProvider>
{
    public MySqlIdempotencyStore(DataSession<MySqlProvider> session) : base(session) { }
}

/// <summary>MySQL 方言 UnitOfWork。</summary>
public sealed class MySqlPalOrmUnitOfWork : PalOrmUnitOfWork<MySqlProvider>
{
    public MySqlPalOrmUnitOfWork(DataSession<MySqlProvider> session) : base(session) { }
}
