using PalORM;
using PalORM.Sqlite;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM.Stores;
using PalDDD.Projections;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Sqlite;

// ─────────────────────────────────────────────────────────────
// SQLite 方言固化中间类
// ════════════════════════════════════════════════════════════
// PalORM 的 IDbProvider 是纯 static abstract 接口（无实例成员）——
// DI 容器解析的是"实例"，PalORM 需要的是"类型参数"，两者本质不兼容。
// 此处的中间类把 TProvider 固化为 SqliteProvider，让 DI 容器只需关闭 TState 一个参数。
// ─────────────────────────────────────────────────────────────

/// <summary>SQLite 方言 Outbox Store。</summary>
public sealed class SqliteOutboxStore : PalOrmOutboxStore<SqliteProvider>
{
    public SqliteOutboxStore(DataSession<SqliteProvider> session, TimeProvider? clock = null)
        : base(session, clock) { }
}

/// <summary>SQLite 方言 Inbox Store。</summary>
public sealed class SqliteInboxStore : PalOrmInboxStore<SqliteProvider>
{
    public SqliteInboxStore(DataSession<SqliteProvider> session) : base(session) { }
}

/// <summary>SQLite 方言 Saga State Store（开放泛型 TState，单参数可注册 DI）。</summary>
public sealed class SqliteSagaStateStore<TState> : PalOrmSagaStateStore<SqliteProvider, TState>
    where TState : SagaState, new()
{
    public SqliteSagaStateStore(DataSession<SqliteProvider> session,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState>? jsonTypeInfo = null,
        TimeProvider? clock = null)
        : base(session, jsonTypeInfo, clock) { }  // P3（十八轮验证轮 F1）：透传 clock——便捷注册解析容器 TimeProvider
}

/// <summary>SQLite 方言 EventLog Store。</summary>
public sealed class SqliteEventLog : PalOrmEventLog<SqliteProvider>
{
    public SqliteEventLog(DataSession<SqliteProvider> session, TimeProvider? clock = null)
        : base(session, clock) { }
}

/// <summary>SQLite 方言 Projection Checkpoint Store。</summary>
public sealed class SqliteProjectionCheckpointStore : PalOrmProjectionCheckpointStore<SqliteProvider>
{
    public SqliteProjectionCheckpointStore(DataSession<SqliteProvider> session) : base(session) { }
}

/// <summary>SQLite 方言 Idempotency Store。</summary>
public sealed class SqliteIdempotencyStore : PalOrmIdempotencyStore<SqliteProvider>
{
    public SqliteIdempotencyStore(DataSession<SqliteProvider> session) : base(session) { }
}

/// <summary>SQLite 方言 UnitOfWork。</summary>
public sealed class SqlitePalOrmUnitOfWork : PalOrmUnitOfWork<SqliteProvider>
{
    public SqlitePalOrmUnitOfWork(DataSession<SqliteProvider> session) : base(session) { }
}
