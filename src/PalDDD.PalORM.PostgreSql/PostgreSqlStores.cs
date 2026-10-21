using PalORM;
using PalORM.PostgreSql;
using PalDDD.PalORM.Stores;
using PalDDD.Transactions;

namespace PalDDD.PalORM.PostgreSql;

// ─────────────────────────────────────────────────────────────
// PostgreSQL 方言固化中间类
// ════════════════════════════════════════════════════════════
// PG 支持 RETURNING 子句 —— TProvider.SupportsReturningClause=true。
// 四个 Store 的实际形态（2026-10-21 评审勘正）：
//   Inbox        —— ON CONFLICT DO NOTHING ... RETURNING（单语句原子路径）
//   Idempotency  —— ON CONFLICT DO NOTHING，无 RETURNING（按 affected 判断）
//   Projection   —— ON CONFLICT DO NOTHING，无 RETURNING（按 affected 判断）
//   Outbox       —— 普通 INSERT（无 ON CONFLICT）；租约路径用 UPDATE ... RETURNING
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
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState>? jsonTypeInfo = null,
        TimeProvider? clock = null)
        : base(session, jsonTypeInfo, clock) { }  // P3（十八轮验证轮 F1）：透传 clock——便捷注册解析容器 TimeProvider

    // ITM-127 修复：PG saga_data 为 jsonb 列，text 参数无隐式赋值转换（42804）——
    // 基类 INSERT/UPDATE 对快照参数加 CAST(... AS jsonb)（对齐 Dapper SagaInsertPG/SagaUpdatePG）
    protected override bool RequiresJsonbCast => true;
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
