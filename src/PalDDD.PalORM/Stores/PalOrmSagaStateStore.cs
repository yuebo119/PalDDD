using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PalORM;
using PalDDD.Core;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// SagaState Store 的 PalORM 实现 —— 双泛型核心基类（TProvider 方言 + TState Saga 状态）。
/// <para>
/// <b>SagaStateRow 未注册为实体</b>（开放泛型 TState 无法 [Table] 注册）——
/// 查询路径用 <see cref="DbDataReader"/> 手动映射（QueryAsync/QueryFirstAsync 对未注册类型返回空对象）。
/// </para>
/// <para>
/// <b>Saga 快照</b>：开放泛型 TState 在编译期未知，<c>[OwnedJson]</c> 无法静态绑定 ——
/// 保留手写 <see cref="JsonSerializer"/>.Serialize(state, <see cref="_jsonTypeInfo"/>) 序列化整 TState 到 <c>saga_data</c> 列。
/// </para>
/// <para>
/// <b>乐观锁</b>：<c>version</c> 列（int）—— UPDATE 时手写 <c>WHERE version = @expected</c>，
/// 0 行返回视为冲突；不走 UpdateAsync（SagaStateRow 非注册实体，无 [ConcurrencyCheck]）。
/// </para>
/// <para>
/// <b>时间戳读取（ITM-242）</b>：MySQL DATETIME 回读的 DateTime Kind=Unspecified，
/// C# 隐式 DateTime→DateTimeOffset 转换对 Unspecified 套<b>本地时区偏移</b>（探针实测 -8h 累积回拨）。
/// <see cref="ReadSagaRow"/> 统一 <see cref="DateTime.SpecifyKind"/>(Utc) 使隐式转换套 offset 0
///（Npgsql timestamptz / SQLite 带偏移文本均返回 Kind=Utc，幂等无害）。
/// </para>
/// <para>
/// <b>raw command 事务（ITM-243）</b>：手动 reader 查询经 <see cref="CreateRawCommand"/> 创建命令并挂接
/// <c>PalOrmAmbientTransaction</c>（IUnitOfWork 事务边界 Session 键控传导）——MySQL 活动事务下
/// 未挂 cmd.Transaction 的命令抛 InvalidOperationException（MySqlConnector 严格校验）。
/// </para>
/// </summary>
public class PalOrmSagaStateStore<TProvider, TState> : ISagaStateStore<TState>, ISagaLeaseBatchStore<TState>, ISagaScanCursorStore<TState>, IDueTimeSagaStore<TState>, ISagaBatchStateStore<TState>
    where TProvider : IDbProvider
    where TState : SagaState, new()
{
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需直接访问 Session。")]
    protected readonly DataSession<TProvider> Session;

    private readonly JsonTypeInfo<TState>? _jsonTypeInfo;
    private readonly TimeProvider _clock;

    /// <summary>
    /// ITM-127（PG jsonb 42804）：PG 的 saga_data 为 jsonb 列，text 参数无隐式赋值转换——
    /// PG 方言派生类覆写为 true 后，INSERT/UPDATE 对快照参数加 CAST(... AS jsonb)
    /// （对齐 Dapper SqlTemplates.SagaInsertPG/SagaUpdatePG）。SQLite/MySQL 列类型收 text 无此需要。
    /// </summary>
    protected virtual bool RequiresJsonbCast => false;

    /// <summary>构造 Saga Store。</summary>
    /// <param name="session">Scoped 数据库会话。</param>
    /// <param name="jsonTypeInfo">可选 STJ 源生成上下文。</param>
    /// <param name="clock">可选时间提供者（默认 System）；用于租约时间一致性（P1-8 修复）。</param>
    public PalOrmSagaStateStore(DataSession<TProvider> session, JsonTypeInfo<TState>? jsonTypeInfo = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(session); // v22 C-1
        Session = session;
        _jsonTypeInfo = jsonTypeInfo;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        // P3 修复（八轮）：与 Dapper/EFCore 姊妹实现对齐——batchSize 非正直接拒绝
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // SagaStateRow 未注册 —— 用 GetRawConnection + 手动 reader（ITM-243：经 CreateRawCommand 挂接环境事务）
        // 三十四轮（中断态超时兜底）：观测查询与 Lease 同步纳入 AwaitingHumanDecision（5）
        await using var cmd = CreateRawCommand();
        cmd.CommandText = "SELECT saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until, next_check_at FROM saga_states WHERE status IN (@p0, @p1) ORDER BY created_at LIMIT @p2";
        AddParam(cmd, "@p0", (int)SagaStatus.Active);
        AddParam(cmd, "@p1", (int)SagaStatus.AwaitingHumanDecision);
        AddParam(cmd, "@p2", batchSize);
        return await ReadSagasAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner, TimeSpan leaseDuration, int batchSize, CancellationToken ct)
        => LeaseCoreAsync(owner, leaseDuration, batchSize, dueOnly: false, dueNow: null, ct);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TState>> LeaseDueSagasAsync(
        string owner, TimeSpan leaseDuration, int batchSize, DateTimeOffset now, CancellationToken ct)
        => LeaseCoreAsync(owner, leaseDuration, batchSize, dueOnly: true, dueNow: now, ct);

    private async ValueTask<IReadOnlyList<TState>> LeaseCoreAsync(
        string owner, TimeSpan leaseDuration, int batchSize, bool dueOnly, DateTimeOffset? dueNow, CancellationToken ct)
    {
        // ITM-163 修复：补 owner 空白守卫（对齐 InMemorySagaStateStore/SagaStateDbContext/DapperSagaStateStore）
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        // P3 修复（八轮）：与 Dapper/EFCore 姊妹实现对齐——batchSize 非正直接拒绝
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // v25 P3 守卫族：leaseDuration 边界守卫——对照 EFCore 四方言 LeasePendingMessagesAsync
        //（MySql/PG/Sqlite，ITM-167/216 对齐系列）同型漏网——leaseDuration 非正时租约
        // 即刻过期/永不过期语义错乱；TotalSeconds 超过 int.MaxValue 时
        // until = now + leaseDuration 的秒数语义溢出。Options 层已校验正数，
        // 此处是 Store 直调路径的防御性 fail-fast（与同文件姊妹 PalOrmOutboxStore 同步补齐）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LeasedUntil value.");

        var now = dueNow ?? _clock.GetUtcNow();  // P1-8：用注入的 Clock 替代硬编码 UtcNow；到期路径用调用方 now
        var until = now + leaseDuration;

        // Saga 租约：UPDATE 子查询
        // MySQL 不支持 UPDATE...WHERE id IN (SELECT...LIMIT) —— 用 JOIN 替代
        // EPSL 地毯审计 2026-10-16 B 批 #3：PG/SQLite 改单语句 UPDATE ... RETURNING
        // （v35 P3 勘正的正解——上方 SupportsReturningClause 分支的 UPDATE 原无 RETURNING，
        // 三方言均两步回读）：UPDATE 与回读收为一次命令，按行锁语义返回刚锁定行，
        // 两步回读按 (leased_by, leased_until) 匹配的同 tick 混批窗口（八轮 P3 声明）
        // 在此形态下不存在（对照 PalOrmOutboxStore.LeasePendingMessagesAsync:95-104 真
        // RETURNING 先例）。实现走 raw command：RETURNING 列序与 ReadSagaRow 序号访问
        // （0-10）逐列对齐；@p 参数复用回读同款 AddParam 绑定（until/now 经 AddParam 的
        // DateTimeOffset 编码三方言正确——ADR-027 until 编码纪律，禁简化为 UtcDateTime）。
        // RETURNING 无排序保证，物化后按 CreatedAt 客户端排序，保留原回读
        // ORDER BY created_at 契约（LINQ OrderBy 稳定排序）。
        if (TProvider.SupportsReturningClause)
        {
            var dueClause = dueOnly ? " AND next_check_at IS NOT NULL AND next_check_at <= @p4" : string.Empty;
            var orderClause = dueOnly
                ? " ORDER BY next_check_at, created_at"
                : TProvider.Dialect == global::PalORM.SqlDialect.PostgreSql
                    ? " ORDER BY leased_until NULLS FIRST, created_at"
                    : " ORDER BY (leased_until IS NULL) DESC, leased_until, created_at";
            await using var leaseCmd = CreateRawCommand();  // ITM-243：挂接 IUnitOfWork 环境事务
            leaseCmd.CommandText = TProvider.Dialect == global::PalORM.SqlDialect.PostgreSql
                ? "UPDATE saga_states SET leased_by = @p0, leased_until = @p1, version = version + 1 WHERE saga_id IN (SELECT saga_id FROM saga_states WHERE status IN (@p2, @p3) AND (leased_until IS NULL OR leased_until <= @p4)" + dueClause + orderClause + " LIMIT @p5 FOR UPDATE SKIP LOCKED) RETURNING saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until, next_check_at"
                : "UPDATE saga_states SET leased_by = @p0, leased_until = @p1, version = version + 1 WHERE saga_id IN (SELECT saga_id FROM saga_states WHERE status IN (@p2, @p3) AND (leased_until IS NULL OR leased_until <= @p4)" + dueClause + orderClause + " LIMIT @p5) RETURNING saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until, next_check_at";
            AddParam(leaseCmd, "@p0", owner);
            AddParam(leaseCmd, "@p1", until);
            AddParam(leaseCmd, "@p2", (int)SagaStatus.Active);
            AddParam(leaseCmd, "@p3", (int)SagaStatus.AwaitingHumanDecision);
            AddParam(leaseCmd, "@p4", now);
            AddParam(leaseCmd, "@p5", batchSize);
            var leased = await ReadSagasAsync(leaseCmd, ct).ConfigureAwait(false);
            return leased.OrderBy(static s => s.CreatedAt).ToList();
        }

        {
            // MySQL 特化路径：JOIN 子查询。M1 到期过滤复用 now 参数。
            FormattableString mysqlSql = dueOnly
                ? (FormattableString)$"UPDATE saga_states t JOIN (SELECT saga_id FROM saga_states WHERE status IN ({(int)SagaStatus.Active}, {(int)SagaStatus.AwaitingHumanDecision}) AND (leased_until IS NULL OR leased_until <= {now}) AND next_check_at IS NOT NULL AND next_check_at <= {now} ORDER BY next_check_at, created_at LIMIT {batchSize}) AS sub ON t.saga_id = sub.saga_id SET t.leased_by = {owner}, t.leased_until = {until}, t.version = t.version + 1"
                : (FormattableString)$"UPDATE saga_states t JOIN (SELECT saga_id FROM saga_states WHERE status IN ({(int)SagaStatus.Active}, {(int)SagaStatus.AwaitingHumanDecision}) AND (leased_until IS NULL OR leased_until <= {now}) ORDER BY (leased_until IS NULL) DESC, leased_until, created_at LIMIT {batchSize}) AS sub ON t.saga_id = sub.saga_id SET t.leased_by = {owner}, t.leased_until = {until}, t.version = t.version + 1";
            await Session.ExecuteAsync(
                // v53 P2：租约即换代（version+1），见 PG 分支注释
                mysqlSql,
                ct).ConfigureAwait(false);
        }

        // 按 lease 标识回读（手动 reader）——仅 MySQL 两步路径（PG/SQLite 已走上方
        // RETURNING 单语句，v35 P3 勘正的「三方言均两步」状态由此解除）。
        // ⚠️ 已知限制（八轮评审 P3，声明不修）：MySQL 两步路径回读按 (leased_by, leased_until) 匹配——
        // 同一 owner 在同一 tick（until 完全相等，如 FakeTimeProvider 冻结时间）发起两次租约时，
        // 第二次回读会混入第一次已锁定的批次。生产触发条件近乎为零（DATETIME(6) 微秒精度 + 单 owner
        // 串行租约）。候选 id 预取需 IN 列表参数化，PalORM 的 FormattableString 路径不支持
        // （详见 PalOrmOutboxStore.LeasePendingMessagesAsync 同款声明）。
        // ITM-076 实测结论（2026-08-16 双连接探针，5 轮）：跨 owner 并发 UPDATE...JOIN 时
        // derived table 按语句开始快照物化，后到者会覆盖先到者的 leased_by（last-writer-wins），
        // 但"双 worker 同批回读"窗口未复现——被覆盖方的回读按 (owner, until) 已不再匹配。
        // 残余窗口由 SaveChangesAsync 的 version 乐观锁兜底（与 Dapper SagaLeaseActiveMySql
        // 声明的"由 SagaUpdate 的 version 乐观锁兜底"对齐）——重复保存被版本冲突拒绝。
        // 回读补状态守卫：已被处理方标记终态（Completed 等）的租约残留行不再混入
        //（三十四轮：守卫集与租约集同步扩为 Active + AwaitingHumanDecision）。
        await using var cmd = CreateRawCommand();  // ITM-243：挂接 IUnitOfWork 环境事务
        cmd.CommandText = "SELECT saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until, next_check_at FROM saga_states WHERE leased_by = @p0 AND leased_until = @p1 AND status IN (@p2, @p3) ORDER BY created_at";
        AddParam(cmd, "@p0", owner);
        AddParam(cmd, "@p1", until);
        AddParam(cmd, "@p2", (int)SagaStatus.Active);
        AddParam(cmd, "@p3", (int)SagaStatus.AwaitingHumanDecision);
        return await ReadSagasAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        await using var cmd = CreateRawCommand();  // ITM-243：挂接 IUnitOfWork 环境事务
        cmd.CommandText = "SELECT saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until, next_check_at FROM saga_states WHERE saga_id = @p0";
        AddParam(cmd, "@p0", sagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return Materialize(ReadSagaRow(reader));
    }

    /// <inheritdoc />
    public async ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        // ITM-163 修复：补 state null 守卫（对齐 InMemorySagaStateStore/DapperSagaStateStore/
        // SagaStateDbContext——三方对齐于 2026-09-19 第五十四轮 F2 补齐：EF 侧此前缺失本守卫，
        // 原注释的三方声明在当时是失实的）
        ArgumentNullException.ThrowIfNull(state);

        // v29 P3（S9，镜像 v28 DapperSagaStateStore 的 Q1 形态）：存储层截断兜底——error 列
        // 上限 2048（EFCore 侧 DDL 共表基准），调用层 SagaProcessor 保存前已
        // FailureReason.Normalize（2000）截断，此处防直调路径（未经 SagaProcessor 的
        // SaveChangesAsync）超长 Error 直传 SQL，跨栈共用表场景 EFCore 侧 2048 列写入失败。
        // 经 FailureReason.Truncate（2040 截断 + UTF-16 代理对守卫）——仅截断不归一空白，
        // Error=null 是"未出错"语义，Normalize 会把 null 归一为 "(no message)" 破坏该语义
        //（INSERT/UPDATE 两处 {truncatedError} 赋值点共用此收口）。
        // v65 P3：截断值先算局部变量、保存结果确认后才赋回 state.Error——对齐 DapperSagaStateStore
        // v38（原实现在保存结果未知前就变异调用方对象：UPDATE 版本冲突 affected=0 时 DB 未变
        // 而调用方 Error 已被截断；INSERT 路径同理）。
        // ITM-649 修复（v65 回归勘正）：v65 重构时 UPDATE 两条 SQL 的 error 绑定点误留
        // {state.Error}（INSERT 两条已改 {truncatedError}）——直调路径超长 Error 经 UPDATE
        // 原文入库（SQLite TEXT 不限长不报错；MySQL/EFCore 共表侧 2048 列写入失败），且
        // affected>0 后内存回写截断值造成内存/DB 分叉。两路径绑定统一回 {truncatedError}
        //（对齐 DapperSagaStateStore :176/:209 的 err 赋值点）。
        var truncatedError = FailureReason.Truncate(state.Error, 2040);

        // ITM-228 修复（三十二轮）：JsonTypeInfo null 时 saga_data 写 NULL——
        // 业务字段（CustomerId 等）全部丢失。fail-fast 比静默丢数据更诚实。
        if (_jsonTypeInfo is null)
            throw new InvalidOperationException(
                $"PalOrmSagaStateStore<TProvider,TState> requires JsonTypeInfo<TState> to persist saga_data. " +
                "Register with AddPalSagaStore<TState>(jsonTypeInfo) or pass it to the constructor. " +
                "Without it, SaveChangesAsync silently drops all business fields (ITM-228).");

        // EPSL 地毯审计 2026-10-11 B-3：UPDATE 先行——原「GetByIdAsync 全行 SELECT + 全量
        // 反序列化做存在性判断」把 O(快照大小) 的传输与 JSON 成本花在只做 null 判断的结果上
        //（与 DapperSagaStateStore 同款改造）。0 行时轻量存在性探测（COUNT raw command）
        // 区分「版本冲突」（行在 → 返回 0）与「新行」（INSERT 路径）；行为三态等价。
        // saga_data：STJ 手写序列化（开放泛型 TState，[OwnedJson] 不可用）
        var jsonData = JsonSerializer.Serialize(state, _jsonTypeInfo);

        // P2 修复（乐观锁快照）：expectedVersion 取调用方加载时的 state.Version（内存快照），
        // 而非本次新读的 existing.Version（DB 最新快照）——后者使"加载后被他人改过"的窗口
        // 检测失效（本次读到的已是 bump 后版本，比较恒过，退化为最后写者胜）。
        // 与 EFCore 版 Version concurrency token（内存快照比对）语义对齐。
        var expectedVersion = state.Version;
        var affected = await Session.ExecuteAsync(
            RequiresJsonbCast
                ? (FormattableString)$"UPDATE saga_states SET current_state = {state.CurrentState}, status = {(int)state.Status}, completed_at = {state.CompletedAt}, version = version + 1, error = {truncatedError}, error_at = {state.ErrorAt}, saga_data = CAST({jsonData} AS jsonb), next_check_at = {state.NextCheckAt}, leased_by = {state.LeasedBy}, leased_until = {state.LeasedUntil} WHERE saga_id = {state.SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture)} AND version = {expectedVersion}"
                : (FormattableString)$"UPDATE saga_states SET current_state = {state.CurrentState}, status = {(int)state.Status}, completed_at = {state.CompletedAt}, version = version + 1, error = {truncatedError}, error_at = {state.ErrorAt}, saga_data = {jsonData}, next_check_at = {state.NextCheckAt}, leased_by = {state.LeasedBy}, leased_until = {state.LeasedUntil} WHERE saga_id = {state.SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture)} AND version = {expectedVersion}",
            ct).ConfigureAwait(false);
        if (affected > 0)
        {
            state.Version++;
            state.Error = truncatedError; // v65 P3：rows>0（DB 已更新）才回写调用方对象
            return affected;
        }

        // 2026-10-19 性能实施轮：UPDATE 0 行后直接尝试 INSERT，唯一冲突即行已存在
        //（版本冲突或并发创建），返回 0 走乐观冲突契约。原实现先发 COUNT 探测再 INSERT，
        // 新 Saga 路径固定 3 次往返；去掉探测后成功新建降为 2 次，已有行更新维持 1 次。
        int inserted;
        try
        {
            inserted = await Session.ExecuteAsync(
                RequiresJsonbCast
                    ? (FormattableString)$"INSERT INTO saga_states (saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, next_check_at, leased_by, leased_until) VALUES ({state.SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture)}, {state.CurrentState}, {(int)state.Status}, {state.CreatedAt}, {state.CompletedAt}, {truncatedError}, {state.ErrorAt}, {state.Version}, CAST({jsonData} AS jsonb), {state.NextCheckAt}, {state.LeasedBy}, {state.LeasedUntil})"
                    : (FormattableString)$"INSERT INTO saga_states (saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, next_check_at, leased_by, leased_until) VALUES ({state.SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture)}, {state.CurrentState}, {(int)state.Status}, {state.CreatedAt}, {state.CompletedAt}, {truncatedError}, {state.ErrorAt}, {state.Version}, {jsonData}, {state.NextCheckAt}, {state.LeasedBy}, {state.LeasedUntil})",
                ct).ConfigureAwait(false);
        }
        catch (DbException ex) when (SqlErrorClassifier.IsUniqueKeyViolation(ex))
        {
            // 行已存在（版本冲突或并发创建同一新 Saga）——统一返回 0（乐观冲突契约）。
            return 0;
        }
        // v65 P3：INSERT 已落库才回写调用方对象（对齐 DapperSagaStateStore v38 形态）
        if (inserted > 0)
            state.Error = truncatedError;
        return inserted;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// EPSL 地毯审计 2026-10-11 B-1：raw command 单语句批量释放（手动 @pN IN 列表——
    /// PalORM FormattableString 路径不支持集合参数化，见 LeaseActiveSagasAsync 已知限制声明；
    /// ITM-243 挂接环境事务）。批量上限 = 扫描 batchSize（默认 256）+ 1 个 owner 参数，
    /// 三方言占位符上限（SQLite 999 / MySQL、PG 65535）内。
    /// </remarks>
    public async ValueTask<int> ReleaseLeasesAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        await using var cmd = CreateRawCommand();
        var sb = new StringBuilder("UPDATE saga_states SET leased_by = NULL, leased_until = NULL, version = version + 1 WHERE leased_by = @p0 AND saga_id IN (@p1");
        AddParam(cmd, "@p0", owner);
        AddParam(cmd, "@p1", sagas[0].SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 1; i < sagas.Count; i++)
        {
            var name = $"@p{i + 1}";
            sb.Append(", ").Append(name);
            AddParam(cmd, name, sagas[i].SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(')');
        cmd.CommandText = sb.ToString();
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>扫描轮转：leased_until 写成本次扫描时刻，legacy 租约按 leased_until NULLS FIRST
    /// 排序，已扫描行退到未扫描行之后。</remarks>
    public async ValueTask<int> ReleaseLeasesAsync(
        string owner,
        IReadOnlyList<TState> sagas,
        DateTimeOffset checkedAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        await using var cmd = CreateRawCommand();
        var sb = new StringBuilder("UPDATE saga_states SET leased_by = NULL, leased_until = @p1, version = version + 1 WHERE leased_by = @p0 AND saga_id IN (@p2");
        AddParam(cmd, "@p0", owner);
        AddParam(cmd, "@p1", checkedAt);
        AddParam(cmd, "@p2", sagas[0].SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 1; i < sagas.Count; i++)
        {
            var name = $"@p{i + 2}";
            sb.Append(", ").Append(name);
            AddParam(cmd, name, sagas[i].SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(')');
        cmd.CommandText = sb.ToString();
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 到期推进只写 next_check_at + version：无 ambient 事务时由本方法开一个事务，
    /// 整批提交一次；ambient 事务存在时并入调用方边界（ITM-243 raw command 挂接）。
    /// </remarks>
    public async ValueTask<int> AdvanceNextCheckAtAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        var ambient = PalOrmAmbientTransaction.TryGet(Session);
        DbTransaction? ownTx = null;
        if (ambient is null)
        {
            ownTx = await Session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
            PalOrmAmbientTransaction.Set(Session, ownTx);
        }

        try
        {
            // SQLite 单语句参数上限 999：每行 3 参数，按 300 行分块。
            const int chunkRows = 300;
            var affected = 0;
            var fullyApplied = true;
            for (var start = 0; start < sagas.Count; start += chunkRows)
            {
                var count = Math.Min(chunkRows, sagas.Count - start);
                await using var cmd = CreateRawCommand();
                cmd.CommandText = BuildAdvanceNextCheckBatch(cmd, owner, sagas, start, count);
                var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                affected += rows;
                if (rows != count)
                    fullyApplied = false;
            }

            if (ownTx is not null)
                await ownTx.CommitAsync(ct).ConfigureAwait(false);
            if (fullyApplied)
            {
                foreach (var saga in sagas)
                    saga.Version++;
            }
            return affected;
        }
        finally
        {
            if (ownTx is not null)
            {
                try { await ownTx.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or DbException) { /* 事务已失效/已释放 */ }
                Session.UseTransaction(null);
                PalOrmAmbientTransaction.Set(Session, null);
            }
        }
    }

    /// <summary>单语句多行 NextCheckAt 推进（每块最多 300 行）。</summary>
    private static string BuildAdvanceNextCheckBatch(
        DbCommand cmd,
        string owner,
        IReadOnlyList<TState> sagas,
        int start,
        int count)
    {
        var sb = new StringBuilder(64 + count * 64);
        AddParam(cmd, "@p0", owner);

        if (TProvider.Dialect == global::PalORM.SqlDialect.MySql)
        {
            sb.Append("UPDATE saga_states s JOIN (");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(" UNION ALL ");
                sb.Append("SELECT @id").Append(i).Append(" AS saga_id, @next").Append(i)
                  .Append(" AS next_check_at, @v").Append(i).Append(" AS version");
                AddAdvanceParams(cmd, sagas[start + i], i);
            }
            sb.Append(") v ON s.saga_id = v.saga_id SET s.next_check_at = v.next_check_at, s.version = s.version + 1 WHERE s.version = v.version AND s.leased_by = @p0");
        }
        else if (TProvider.Dialect == global::PalORM.SqlDialect.PostgreSql)
        {
            sb.Append("UPDATE saga_states AS s SET next_check_at = v.next_check_at, version = s.version + 1 FROM (VALUES ");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("(CAST(@id").Append(i).Append(" AS text), CAST(@next").Append(i)
                  .Append(" AS timestamptz), CAST(@v").Append(i).Append(" AS integer))");
                AddAdvanceParams(cmd, sagas[start + i], i);
            }
            sb.Append(") AS v(saga_id, next_check_at, version) WHERE s.saga_id = v.saga_id AND s.version = v.version AND s.leased_by = @p0");
        }
        else
        {
            // SQLite UPDATE ... FROM：用 SELECT/UNION ALL 派生表（SQLite 不接受
            // FROM (VALUES ...) AS v(col1,col2) 的列别名列表）。
            sb.Append("UPDATE saga_states SET next_check_at = v.next_check_at, version = saga_states.version + 1 FROM (");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(" UNION ALL ");
                sb.Append("SELECT @id").Append(i).Append(" AS saga_id, @next").Append(i)
                  .Append(" AS next_check_at, @v").Append(i).Append(" AS version");
                AddAdvanceParams(cmd, sagas[start + i], i);
            }
            sb.Append(") AS v WHERE saga_states.saga_id = v.saga_id AND saga_states.version = v.version AND saga_states.leased_by = @p0");
        }

        return sb.ToString();
    }

    private static void AddAdvanceParams(DbCommand cmd, TState saga, int index)
    {
        AddParam(cmd, $"@id{index}", saga.SagaId.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        AddParam(cmd, $"@next{index}", saga.NextCheckAt.HasValue ? saga.NextCheckAt.Value : (object)DBNull.Value);
        AddParam(cmd, $"@v{index}", saga.Version);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<int>> SaveManyAsync(IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return [];

        var ambient = PalOrmAmbientTransaction.TryGet(Session);
        DbTransaction? ownTx = null;
        if (ambient is null)
        {
            ownTx = await Session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
            PalOrmAmbientTransaction.Set(Session, ownTx);
        }

        try
        {
            var results = new int[sagas.Count];
            for (var i = 0; i < sagas.Count; i++)
                results[i] = await SaveChangesAsync(sagas[i], ct).ConfigureAwait(false);

            if (ownTx is not null)
                await ownTx.CommitAsync(ct).ConfigureAwait(false);
            return results;
        }
        finally
        {
            if (ownTx is not null)
            {
                try { await ownTx.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or DbException) { /* 事务已失效/已释放 */ }
                Session.UseTransaction(null);
                PalOrmAmbientTransaction.Set(Session, null);
            }
        }
    }

    /// <summary>从 DbDataReader 读取多行 Saga。</summary>
    private async Task<List<TState>> ReadSagasAsync(DbCommand cmd, CancellationToken ct)
    {
        var result = new List<TState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Materialize(ReadSagaRow(reader)));
        }
        return result;
    }

    /// <summary>从 reader 当前行读取 SagaStateRow（手动映射，避免 QueryAsync 对未注册类型返回空）。</summary>
    private static SagaStateRow ReadSagaRow(DbDataReader reader) => new()
    {
        SagaId = reader.GetString(0),
        CurrentState = reader.GetString(1),
        Status = reader.GetInt32(2),
        CreatedAt = GetUtc(reader, 3),
        CompletedAt = reader.IsDBNull(4) ? null : GetUtc(reader, 4),
        Error = reader.IsDBNull(5) ? null : reader.GetString(5),
        ErrorAt = reader.IsDBNull(6) ? null : GetUtc(reader, 6),
        Version = reader.GetInt32(7),
        SagaData = reader.IsDBNull(8) ? null : reader.GetString(8),
        LeasedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
        LeasedUntil = reader.IsDBNull(10) ? null : GetUtc(reader, 10),
        NextCheckAt = reader.IsDBNull(11) ? null : GetUtc(reader, 11),
    };

    /// <summary>
    /// ITM-242：读回时间戳并标记 UTC。MySQL DATETIME 回读的 DateTime Kind=Unspecified，
    /// 隐式 DateTime→DateTimeOffset 转换对 Unspecified 套本地时区偏移（探针实测 -8h 累积回拨）；
    /// SpecifyKind(Utc) 使隐式转换套 offset 0。Npgsql timestamptz 与 SQLite 带偏移文本
    /// 均返回 Kind=Utc（探针实测幂等）。
    /// </summary>
    private static DateTimeOffset GetUtc(DbDataReader reader, int ordinal)
        => new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>
    /// ITM-243：创建 raw command 并挂接 IUnitOfWork 活动事务。GetRawConnection().CreateCommand()
    /// 的手动命令不自动 enlist（PalORM 仅 ExecuteAsync 路径自动挂 GetActiveTransaction()），
    /// MySQL 活动事务下未挂接抛 InvalidOperationException（MySqlConnector 严格校验，探针实测）。
    /// </summary>
    private DbCommand CreateRawCommand()
    {
        var cmd = Session.GetRawConnection().CreateCommand();
        var tx = PalOrmAmbientTransaction.TryGet(Session);
        if (tx is not null) cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>SagaStateRow → TState（JSON 反序列化 + 元数据覆盖）。
    /// <para>当 <c>_jsonTypeInfo</c> 为 null 或 SagaData 为空时，从数据库列恢复 SagaId/CreatedAt（init-only 通过 object initializer 赋值）。
    /// 其余可变属性在下方逐行覆盖。与 DapperSagaStateStore.Materialize 行为对齐。</para>
    /// </summary>
    private TState Materialize(SagaStateRow row)
    {
        var sagaId = PalUlid.Parse(row.SagaId);
        TState state;
        if (_jsonTypeInfo is not null && !string.IsNullOrEmpty(row.SagaData))
        {
            // 对齐 ITM-228 哲学（fail-fast 比静默丢数据更诚实）：saga_data 为字面 "null"
            // JSON 文本（行损坏/手工写入）时 Deserialize 返回 null——原 `?? new TState` 兜底
            // 会静默丢弃全部业务字段（CustomerId 等），调用方继续推进后 SaveChangesAsync
            // 以空状态覆写 DB，损坏从一行扩散到整条 saga；改抛InvalidOperationException
            // 与同文件 SaveChangesAsync 的 ITM-228 同款。注意：下方 else 分支的 new TState
            // 兜底是合法语义（saga_data 为空 = 无 JSON 数据可恢复，仅元数据兜底，不丢已存
            // 数据），保持不变。姊妹 DapperSagaStateStore.Materialize 已同步收口（v3 轮
            // ITM-659 批，同款 fail-fast——v4 轮审计勘正本注释：原文声称"未同步"已过期）。
            state = JsonSerializer.Deserialize(row.SagaData, _jsonTypeInfo)
                ?? throw new InvalidOperationException(
                    $"Saga {row.SagaId} 的 saga_data 列损坏（值为 JSON 字面 'null' 文本），无法恢复业务状态——"
                    + "降级为空状态会在下次保存时静默清空业务字段，请人工修复该行数据（对齐 ITM-228 口径）。");
        }
        else
        {
            state = new TState { SagaId = sagaId, CreatedAt = row.CreatedAt };
        }

        state.CurrentState = row.CurrentState;
        state.Status = (SagaStatus)row.Status;
        state.Version = row.Version;
        state.CompletedAt = row.CompletedAt;
        state.Error = row.Error;
        state.ErrorAt = row.ErrorAt;
        state.LeasedBy = row.LeasedBy;
        state.LeasedUntil = row.LeasedUntil;
        state.NextCheckAt = row.NextCheckAt;
        return state;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }


    /// <summary>临时 SagaStateRow（内部用，不注册为实体）。</summary>
    private sealed class SagaStateRow
    {
        public string SagaId { get; set; } = "";
        // SagaState.InitialStateName 而非字面量（二十四轮）：与领域状态机协议值单一来源
        public string CurrentState { get; set; } = SagaState.InitialStateName;
        public int Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? ErrorAt { get; set; }
        public int Version { get; set; }
        public string? SagaData { get; set; }
        public string? LeasedBy { get; set; }
        public DateTimeOffset? LeasedUntil { get; set; }
        public DateTimeOffset? NextCheckAt { get; set; }
    }
}
