using PalORM;
using PalORM.PostgreSql;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM.Stores;
using PalDDD.Projections;
using PalDDD.Transactions;

namespace PalDDD.PalORM.PostgreSql;

// ─────────────────────────────────────────────────────────────
// PostgreSQL 方言固化中间类
// ════════════════════════════════════════════════════════════
// PG 支持 RETURNING 子句 —— TProvider.SupportsReturningClause=true。
// Outbox/Inbox/Idempotency/Projection 的 INSERT 走 ON CONFLICT DO NOTHING RETURNING 单语句原子路径。
// ─────────────────────────────────────────────────────────────

/// <summary>PostgreSQL 方言 Outbox Store。</summary>
public sealed class PostgreSqlOutboxStore : PalOrmOutboxStore<PostgreSqlProvider>
{
    public PostgreSqlOutboxStore(DataSession<PostgreSqlProvider> session, TimeProvider? clock = null)
        : base(session, clock) { }
}

/// <summary>PostgreSQL 方言 Inbox Store。</summary>
public sealed class PostgreSqlInboxStore : PalOrmInboxStore<PostgreSqlProvider>
{
    public PostgreSqlInboxStore(DataSession<PostgreSqlProvider> session) : base(session) { }
}

/// <summary>PostgreSQL 方言 Saga State Store。</summary>
public sealed class PostgreSqlSagaStateStore<TState> : PalOrmSagaStateStore<PostgreSqlProvider, TState>
    where TState : SagaState, new()
{
    public PostgreSqlSagaStateStore(DataSession<PostgreSqlProvider> session,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState>? jsonTypeInfo = null)
        : base(session, jsonTypeInfo) { }
}

/// <summary>PostgreSQL 方言 EventLog Store。</summary>
public sealed class PostgreSqlEventLog : PalOrmEventLog<PostgreSqlProvider>
{
    public PostgreSqlEventLog(DataSession<PostgreSqlProvider> session, TimeProvider? clock = null)
        : base(session, clock) { }
}

/// <summary>PostgreSQL 方言 Projection Checkpoint Store。</summary>
public sealed class PostgreSqlProjectionCheckpointStore : PalOrmProjectionCheckpointStore<PostgreSqlProvider>
{
    public PostgreSqlProjectionCheckpointStore(DataSession<PostgreSqlProvider> session) : base(session) { }
}

/// <summary>PostgreSQL 方言 Idempotency Store。</summary>
public sealed class PostgreSqlIdempotencyStore : PalOrmIdempotencyStore<PostgreSqlProvider>
{
    public PostgreSqlIdempotencyStore(DataSession<PostgreSqlProvider> session) : base(session) { }
}

/// <summary>PostgreSQL 方言 UnitOfWork。</summary>
public sealed class PostgreSqlPalOrmUnitOfWork : PalOrmUnitOfWork<PostgreSqlProvider>
{
    public PostgreSqlPalOrmUnitOfWork(DataSession<PostgreSqlProvider> session) : base(session) { }
}
