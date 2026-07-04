// ─────────────────────────────────────────────────────────────
// 📋 SqlTemplates — Dapper Store SQL 模板集中管理
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要这个文件？
//   ｜ DapperOutboxStore / DapperInboxStore / DapperSagaStateStore / DapperEventLog
//   ｜ 各自内嵌了 SQL 语句。当需要切换数据库（PG→MySQL）或调整 SQL 时，
//   ｜ 需要修改分散在 4 个文件中的 SQL 字符串，容易遗漏和不一致。
//   ｜
//   ｜ SqlTemplates 将所有 SQL 集中到一个文件：
//   ｜   - 切换数据库 → 只需修改此文件
//   ｜   - 审查 SQL   → 只需看一个文件
//   ｜   - 保持业务逻辑代码不变
//   ｜
//   ｜ 使用场景：
//   ｜   DapperOutboxStore 引用 SqlTemplates.OutboxInsert
//   ｜   DapperInboxStore  引用 SqlTemplates.InboxSelect
//   ｜   DapperSagaStateStore 引用 SqlTemplates.SagaActive
//
// ✅ AOT 安全性：所有字段为 public const string，编译时常量，零运行时分配。
//
// 📐 DDD 位置：基础设施层 — Dapper 特定 SQL，不影响领域/应用层。
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Dapper;

/// <summary>
/// Dapper Store 的 SQL 模板集中管理。<br/>
/// 所有 SQL 语句为编译时常量（<c>const string</c>），零运行时开销。<br/>
/// 切换数据库时只需修改此文件，Store 的业务逻辑代码不变。
/// </summary>
/// <remarks>
/// 约定：
///   - 表名和列名使用 <c>snake_case</c>（PostgreSQL/MySQL 标准）
///   - 参数名使用 <c>PascalCase</c>（Dapper 自动映射到 <c>@ParamName</c>）
///   - PostgreSQL 专用语法（如 RETURNING）通过 Dapper 层的 DapperDbType switch 处理
///   - 状态值使用与 C# 枚举一致的名称（Pending/Processed/Dead/Active/Completed/DeadLettered）
///     避免 Outbox（字符串状态）和 Saga（数字状态）风格不一致。
/// </remarks>
public static class SqlTemplates
{
    // ═══════════════════════════════════════════════════════════
    // 📤 Outbox（发件箱）— 保证消息和业务数据在同一事务中持久化
    // ═══════════════════════════════════════════════════════════

    /// <summary>发件箱表名</summary>
    public const string Outbox = "outbox_messages";

    /// <summary>
    /// 插入一条新消息到发件箱。<br/>
    /// 💡 为什么包含 <c>id</c> 列？<br/>
    ///   ｜ PostgreSQL 的 UUID 和 SQLite 的 TEXT 都需要显式传入 id。<br/>
    ///   ｜ <c>OutboxMessage.Id</c> 在构造时已经 <c>Guid.NewGuid()</c>，直接传入即可。
    /// </summary>
    public const string OutboxInsert =
        "INSERT INTO outbox_messages (id,type,payload,content_type,schema_version,status,created_at) VALUES (@Id,@Type,@Payload,@ContentType,@SchemaVersion,'Pending',@CreatedAt)";

    /// <summary>
    /// 标记消息为"已处理"。<br/>
    /// 💡 同时清除 <c>locked_by</c> 和 <c>locked_until</c>——释放租约。
    /// </summary>
    public const string OutboxMarkProcessed =
        "UPDATE outbox_messages SET status='Processed',processed_at=@at,error=NULL,next_attempt_at=NULL,locked_by=NULL,locked_until=NULL WHERE id=@id";

    /// <summary>
    /// 标记消息为"死信"。<br/>
    /// 💡 死信意味着消息不可恢复——需要人工介入。
    /// </summary>
    public const string OutboxMarkDead =
        "UPDATE outbox_messages SET status='Dead',error=@reason,processed_at=@at,next_attempt_at=NULL,locked_by=NULL,locked_until=NULL WHERE id=@id";

    /// <summary>
    /// 释放租约并等待下次重试。<br/>
    /// 💡 <c>retry_count+1</c> 原子递增——当重试次数达到 10 时不再出现在 Pending 列表中。
    /// </summary>
    public const string OutboxReleaseForRetry =
        "UPDATE outbox_messages SET status='Pending',error=@reason,next_attempt_at=@next,retry_count=retry_count+1,locked_by=NULL,locked_until=NULL WHERE id=@id";

    /// <summary>
    /// 将死信消息重置为 Pending（ops 重投递入口）。<br/>
    /// 💡 仅作用于 <c>status='Dead'</c> 的消息，避免越权把已 Processed/Pending 重置；<c>retry_count</c> 保留失败历史。<br/>
    /// <c>processed_at</c> 清 NULL、<c>error</c> 写入操作审计串。<br/>
    /// ⚠️ 幂等前提由调用方保证（详见 ADR-011）。
    /// </summary>
    public const string OutboxRequeueDead =
        "UPDATE outbox_messages SET status='Pending',processed_at=NULL,error=@audit,next_attempt_at=@next,locked_by=NULL,locked_until=NULL WHERE id=@id AND status='Dead'";

    /// <summary>原子租约获取 — UPDATE 子句</summary>
    public const string OutboxLeaseUpdate =
        "UPDATE outbox_messages SET locked_by=@owner, locked_until=@until WHERE id IN ";

    /// <summary>按 ID 批量查询（用于 PG RETURING * 替代路径）</summary>
    public const string OutboxSelectById =
        "SELECT * FROM outbox_messages WHERE id IN ";

    /// <summary>
    /// 查询待处理消息（Pending + 未达重试上限 + 可重试/无租约/租约过期）。<br/>
    /// 💡 供 <c>GetPendingMessagesAsync</c> 使用，与 <c>LeasePendingMessagesAsync</c> 的子查询条件一致。
    /// </summary>
    public const string OutboxSelectPending =
        "SELECT * FROM outbox_messages WHERE status=@status AND retry_count<@maxRetryCount AND (next_attempt_at IS NULL OR next_attempt_at<=@now) AND (locked_until IS NULL OR locked_until<=@now) ORDER BY created_at LIMIT @n";

    /// <summary>
    /// 按租约标识回读刚锁定的消息。<br/>
    /// 💡 解决非 PG 路径的并发 Bug：子查询在 UPDATE 后重新评估会把锁定行排除。<br/>
    /// 改为按 <c>locked_by=@owner AND locked_until=@until</c> 回读，精确匹配本次租约。
    /// </summary>
    public const string OutboxSelectByLease =
        "SELECT * FROM outbox_messages WHERE locked_by=@owner AND locked_until=@until";

    // ═══════════════════════════════════════════════════════════
    // 📥 Inbox（收件箱）— 保障每个消费者的每条消息只处理一次
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 查询特定消费者的特定消息。<br/>
    /// 💡 联合唯一约束 <c>(consumer_name, message_id)</c> 保证幂等性。
    /// </summary>
    public const string InboxSelect =
        "SELECT * FROM inbox_messages WHERE consumer_name=@c AND message_id=@m";

    /// <summary>
    /// 开始处理消息 — 状态改为 Processing，尝试次数 +1。<br/>
    /// 💡 原子操作：UPDATE 在同一个 SQL 中完成状态变更和计数递增。
    /// </summary>
    public const string InboxStartProcessing =
        "UPDATE inbox_messages SET status='Processing',attempts=attempts+1,processing_started_at=@now WHERE id=@id AND status<>'Processed'";

    /// <summary>标记消息处理成功</summary>
    public const string InboxMarkProcessed =
        "UPDATE inbox_messages SET status='Processed',processed_at=@at WHERE id=@id AND status='Processing'";

    /// <summary>
    /// 标记消息处理失败。<br/>
    /// 💡 保留 <c>last_error</c> 以便排查问题，消息可以重试。
    /// </summary>
    public const string InboxMarkFailed =
        "UPDATE inbox_messages SET status='Failed',last_error=@err WHERE id=@id AND status='Processing'";

    /// <summary>
    /// PostgreSQL conflict-safe INSERT 语法。<br/>
    /// 💡 <b>单语句原子幂等：</b><c>ON CONFLICT ... DO NOTHING RETURNING id</c> 在同一条 SQL 中完成"已存在则跳过、新插入则返回 id"，
    /// 消除 SQLite/MySQL 路径的 TOCTOU 窗口，生产推荐路径。
    /// </summary>
    public const string InboxInsertPG =
        "INSERT INTO inbox_messages (consumer_name,message_id,status,received_at,processing_started_at,attempts) VALUES (@c,@m,'Processing',@now,@now,1) ON CONFLICT (consumer_name,message_id) DO NOTHING RETURNING id";

    /// <summary>MySQL conflict-safe INSERT 语法</summary>
    public const string InboxInsertMySql =
        "INSERT IGNORE INTO inbox_messages (consumer_name,message_id,status,received_at,processing_started_at,attempts) VALUES (@c,@m,'Processing',@now,@now,1); SELECT LAST_INSERT_ID();";

    /// <summary>
    /// SQLite conflict-safe INSERT 语法。<br/>
    /// ⚠️ <b>语义弱保证 / TOCTOU 窗口：</b>SQLite 路径由 <c>INSERT OR IGNORE</c> + <c>SELECT last_insert_rowid() WHERE changes() &gt; 0</c>
    /// 两步组成——在并发消费者场景下，理论上存在"消费者 A 已 INSERT 但尚未提交、消费者 B 的 <c>INSERT OR IGNORE</c> 静默忽略、随后的 <c>SELECT</c> 也读不到 A 行"的窗口，导致 B 误判为"无主"并发发起处理。
    /// 单语句原子性由 PostgreSQL 的 <see cref="InboxInsertPG"/>（<c>ON CONFLICT ... RETURNING</c>）消除，生产推荐 PostgreSQL 路径。
    /// SQLite 路径适合单实例/低并发/测试场景，仅依赖 <c>(consumer_name, message_id)</c> 唯一约束避免重复记录，不保证强幂等。
    /// </summary>
    public const string InboxInsertSqlite =
        "INSERT OR IGNORE INTO inbox_messages (consumer_name,message_id,status,received_at,processing_started_at,attempts) VALUES (@c,@m,'Processing',@now,@now,1); SELECT last_insert_rowid() WHERE changes() > 0;";

    // ═══════════════════════════════════════════════════════════
    // 💾 Saga（长事务编排）— 持久化 Saga 状态，支持补偿
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 查询活跃的 Saga 状态。<br/>
    /// 💡 只返回 Active=0 的 Saga；Completed、Compensated、CompensationFailed、DeadLettered 都是终态或人工介入态。<br/>
    /// 注：Saga 状态使用 int 枚举值存储（SagaStatus 底层为 int），与 Outbox 的字符串状态不同。
    /// 这是有意选择：Saga 状态需要版本号乐观并发控制，int 比较比字符串更快。
    /// </summary>
    public const string SagaActive =
        "SELECT * FROM saga_states WHERE status = 0 ORDER BY created_at LIMIT @n";

    /// <summary>租约获取活跃 Saga，避免多 worker 重复处理。</summary>
    public const string SagaLeaseActive =
        "UPDATE saga_states SET leased_by=@owner, leased_until=@until WHERE saga_id IN (SELECT saga_id FROM saga_states WHERE status = 0 AND (leased_until IS NULL OR leased_until <= @now) ORDER BY created_at LIMIT @n)";

    /// <summary>按本次租约回读刚获取的 Saga。</summary>
    public const string SagaSelectByLease =
        "SELECT * FROM saga_states WHERE leased_by=@owner AND leased_until=@until";

    /// <summary>按 SagaId 查找指定 Saga</summary>
    public const string SagaById =
        "SELECT * FROM saga_states WHERE saga_id=@id";

    /// <summary>
    /// 更新 Saga 状态（乐观并发控制）。<br/>
    /// 💡 <c>version=version+1</c> + <c>WHERE version=@v</c> 防止并发覆盖。
    /// </summary>
    public const string SagaUpdate =
        "UPDATE saga_states SET current_state=@cs,status=@st,completed_at=@ca,version=version+1,error=@err,error_at=@ea,saga_data=@data,leased_by=@leasedBy,leased_until=@leasedUntil WHERE saga_id=@id AND version=@v";

    /// <summary>插入新的 Saga 状态</summary>
    public const string SagaInsert =
        "INSERT INTO saga_states(saga_id,current_state,status,created_at,completed_at,error,error_at,saga_data,leased_by,leased_until) VALUES(@id,@cs,@st,@ca,@completedAt,@err,@ea,@data,@leasedBy,@leasedUntil)";
}
