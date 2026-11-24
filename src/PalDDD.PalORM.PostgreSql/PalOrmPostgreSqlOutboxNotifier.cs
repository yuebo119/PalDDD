// ─────────────────────────────────────────────────────────────
// 🔔 PalOrmPostgreSqlOutboxNotifier — PalORM 栈的 LISTEN/NOTIFY Outbox 唤醒
// ─────────────────────────────────────────────────────────────
//
// 💡 2026-11-23 性能轮（审计在册候选 #3 收口）：PG 跨进程唤醒此前只存在于 Dapper 栈
//   ｜（PostgreSqlOutboxNotifier 手写 WaitAsync + 重连退避）；PalORM 栈缺同能力，
//   ｜ Outbox 端到端延迟受 PeriodicTimer 轮询周期钳制。本服务用 PalORM 6.9.0 的
//   ｜ PgNotificationListener（30s 心跳保活 + 线性递增全量抖动重连，上限 30s——A5 设计）
//   ｜ 承载监听循环，NOTIFY 到达即触发 OutboxBatchProcessor（复用既有批处理与
//   ｜ SemaphoreSlim 背压语义），消除轮询延迟。
//   ｜ 通道契约与 Dapper 版完全一致（outbox_channel + 过渡表触发器，建表 SQL 见
//   ｜ PostgreSqlOutboxNotifier 头注释）——同一库两栈共用同一触发器面。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.Core.Logging;
using PalDDD.Transactions;
using PalORM.PostgreSql;

namespace PalDDD.PalORM.PostgreSql;

/// <summary>
/// PalORM PostgreSQL 栈的 LISTEN/NOTIFY Outbox 唤醒服务（IHostedService）。
/// </summary>
/// <remarks>
/// 与 Dapper 栈的 <c>PostgreSqlOutboxNotifier</c> 语义对齐（NOTIFY → 立即触发
/// <see cref="OutboxBatchProcessor"/>，消除 PeriodicTimer 轮询延迟；默认轮询器保留作兜底），
/// 差异仅在监听载体：本服务用 PalORM 自带的 <see cref="PgNotificationListener"/>——
/// 断线自动重连（线性递增 + 全量抖动退避）、30 秒心跳保活、订阅者异常隔离，
/// 免去手写 WaitAsync 循环与重连退避的维护面（Dapper 版五轮 P1/P3 修复的全部分支）。
/// <para>
/// ⚠️ <b>重连上限差异（2026-11-24 R15 审计发现 1 接线后的行为声明）</b>：上游
/// <c>PgNotificationListener</c> 的断线重连有<b>次数上限（5 次）</b>，耗尽后或遇非瞬态
/// 故障时后台循环终止且不再自愈——终止原因经 <c>OnError</c> 记入本组件 Error 日志
/// （NOTIFY 通路中断，Outbox 退回轮询兜底，消息不丢但延迟回升）。Dapper 版为无限重连
/// （外层 while），同类故障下自愈形态不同；长断线场景恢复手段是重启进程，或依赖轮询器
/// 继续投递。
/// </para>
/// <para>
/// 注册：<c>services.AddPalOrmPostgreSql(...).AddPalOrmPostgreSqlOutboxNotifier(connectionString, "outbox_channel");</c>
/// ⚠️ 连接串必须与 <see cref="PostgreSqlPalOrmExtensions.AddPalOrmPostgreSql"/> 一致——
/// listener 持有独立长连接（LISTEN 不可与业务会话共用），不走 PalORM 会话池。
/// <c>OutboxBatchProcessor</c> 由 <c>AddPalOutbox()</c> 注册（Scoped），未调用时首个
/// 通知会在作用域解析阶段抛缺服务异常（与 Dapper 版行为一致）。
/// </para>
/// <para>
/// 建表侧（触发器/通道）与 Dapper 版共用，见 <c>PostgreSqlOutboxNotifier</c> 头注释的
/// DDL——本服务零表结构要求。
/// </para>
/// </remarks>
public sealed class PalOrmPostgreSqlOutboxNotifier(
    string connectionString,
    IServiceScopeFactory scopeFactory,
    IPalLogger<PalOrmPostgreSqlOutboxNotifier> logger,
    string channelName = "outbox_channel") : IHostedService, IAsyncDisposable
{
    // 背压控制（对齐 Dapper 版 P1 修复语义）：SemaphoreSlim(1,1) 保证同时最多一个批处理
    // 在执行；NOTIFY 风暴被合并（一次批处理拿走全部 pending，多余的并发只竞争连接池）。
    private readonly SemaphoreSlim _processGate = new(1, 1);

    // 2026-11-23 审查修复（缺陷 1）：关停协作取消——FireBatchProcessAsync 原传
    // CancellationToken.None，宿主关停时 in-flight 批处理不可协作收尾，进程退出硬杀
    // 批（Outbox 租约悬挂到超时）。对齐 Dapper 版 stoppingToken 语义：StopAsync 先
    // 取消本 CTS，ProcessBatchAsync 感知取消后按其既有协作语义收尾（Dapper 版
    // FireBatchProcessAsync(stoppingToken) 同款）。
    private CancellationTokenSource? _shutdownCts;

    private PgNotificationListener? _listener;

    // 2026-11-24 勘正（R15 审计发现 2）：fire 任务引用持有——原注释（654f4da）声称
    // 「Task.Run 每通知一分配 → 每批处理一分配」的缓存收益不存在：字段零读方，
    // Task.Run 调用次数与修前 `_ = Task.Run(...)` 逐位一致（in-flight 时 NOTIFY 被
    // Wait(0) 拒掉的串行性质在修前同样成立）。
    // R16 修复（2026-11-24）：本字段自本轮起有读方——StopAsync 等待它收尾（见该处注释）。
    private Task? _fireTask;

    /// <summary>启动监听（建立连接并 LISTEN）。</summary>
    /// <exception cref="InvalidOperationException">重复启动（IHostedService 契约下 StartAsync 只应调用一次）。</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        // 2026-11-23 审查修复（缺陷 2）：幂等守卫——重复 StartAsync 会 new 第二个 listener，
        // 旧实例的订阅挂着无人释放（构造 + 订阅成功但 Stop/Dispose 走不到）。fail-fast 抛。
        if (_listener is not null)
            throw new InvalidOperationException("PalOrmPostgreSqlOutboxNotifier has already been started.");
        // 通道名白名单校验与 Dapper 版同款（LISTEN 不可参数化，拼接前须防注入；
        // PG 未引号标识符小写折叠，上游 listener 以双引号包裹通道名传递——大小写口径一致）。
        ValidateChannelName(channelName);

        var shutdownCts = new CancellationTokenSource();

        // 构造不建连；StartAsync 后才连接并 LISTEN（上游 XML 契约）。
        // 订阅者异常被上游隔离（不会终止重连循环），此处仍 catch-log 防未观察异常。
        var listener = new PgNotificationListener(connectionString, channelName);
        listener.OnNotification += OnNotificationReceived;
        // 2026-11-24 修复（R15 审计发现 1）：监听死亡可观测接线。上游断线重连有次数上限
        //（反编译实证 _maxReconnectAttempts=5），耗尽后 RunAsync 终止、RaiseError 走
        // LogListenerTerminated(Logger ?? NullLogger)——本组件此前三通道全空（Logger 属性
        // 未赋值、OnError 零订阅、LastError 零读取），Error 级终止原因落到 NullLogger
        // 零输出，通知器静默死亡。OnNotificationReceived 的注释「上游 listener 心跳保活
        // 的后续通知」仅在重连窗口内成立，死亡后 NOTIFY 通路中断，Outbox 退回 500ms
        // 轮询兜底。此处订阅 OnError 把终止原因转入本组件日志（Error 级）；未再触发
        // 自动重启——与 Dapper 版「无限重连」的行为差异保留声明（见类 remarks 差异段），
        // 恢复手段是重启进程或修复网络后由轮询器继续投递。
        listener.OnError += OnListenerTerminated;

        // R16 修复（2026-11-24，F3）：字段赋值后移到最后——修前先置 _listener/_shutdownCts
        // 再 await 上游 StartAsync，首连失败时对象停在「声称已启动」态：_listener 非 null
        // 使重试命中 :86 的幂等守卫（实际从未启动），且已创建的 CTS 无人释放。
        // 现语义：首连成功才落字段；失败路径退订 + 释放局部对象后重抛，对象回到「未启动」
        // 可重入态（对齐上游「StopAsync 之后可再次启动」契约）。
        try
        {
            await listener.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            listener.OnNotification -= OnNotificationReceived;
            listener.OnError -= OnListenerTerminated;
            await listener.DisposeAsync().ConfigureAwait(false);
            shutdownCts.Dispose();
            throw;
        }

        _listener = listener;
        _shutdownCts = shutdownCts;
        logger.Information($"PalORM PostgreSQL LISTEN started on channel '{channelName}'");
    }

    // 2026-11-24 修复（R15 审计发现 1）：上游终止回调——首次 LISTEN 成功后，重连耗尽
    // 或非瞬态故障均经上游 RaiseError 触发本事件；关停路径的取消不触发（RunAsync 的
    // OCE-when-cancel 分支先行退出）。只记日志不重抛：事件在后台任务上触发，异常会
    // 打断上游的事件分发循环。
    private void OnListenerTerminated(object? sender, PgNotificationErrorEventArgs e)
        => logger.Error(e.Exception, $"PalORM PostgreSQL LISTEN terminated (non-transient failure or reconnect attempts exhausted); NOTIFY delivery is down, Outbox falls back to polling, channel '{channelName}': {e.Exception.Message}");

    private void OnNotificationReceived(object? sender, PgNotificationEventArgs e)
    {
        logger.Debug($"PostgreSQL NOTIFY received on channel '{e.Channel}'");
        // 非阻塞 try-enter：gate 被占用时本次 NOTIFY 被丢弃——at-least-once 由兜底保证
        //（PeriodicTimer 轮询 + 上游 listener 心跳保活的后续通知），对齐 Dapper 版语义。
        // 刻意不传 token：传入时若调度前取消，任务不执行、已获取的 gate 永不释放。
#pragma warning disable S8949 // Wait(0) 刻意不传 token——取消时放弃获取而非带取消等待
        if (_processGate.Wait(0))
#pragma warning restore S8949
        {
            // 不传宿主 token：传入时若调度前取消，任务不执行、已获取的 gate 永不释放
            //（Dapper 版 P2 修复同款）；关停协作取消经 _shutdownCts（StopAsync 先取消），
            // gate 释放由 FireBatchProcessAsync 的 finally 兜底。
            // fire 任务引用存入 _fireTask（仅持引用语义，见字段勘正注释）。
#pragma warning disable S8949 // Task.Run 刻意不传 token：调度前取消会让已获取的 gate 永不释放
            _fireTask = Task.Run(FireBatchProcessAsync);
#pragma warning restore S8949
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "后台批处理触发任务必须隔离任意异常，避免未处理异常终止后台服务；失败已通过 logger.Error 记录。")]
    private async Task FireBatchProcessAsync()
    {
        // 关停协作取消（审查修复 1）：批处理执行期间 StopAsync 可取消本 token，让
        // ProcessBatchAsync 按既有协作语义收尾（对齐 Dapper 版 stoppingToken 形态）；
        // null = StartAsync 从未成功，无批处理可言（防御，正常不可达）。
        var ct = _shutdownCts?.Token ?? CancellationToken.None;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
            await processor.ProcessBatchAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 宿主关停：批处理按协作语义收尾，记录后静默（对齐 Dapper 版 ITM-094 语义）
            logger.Information("PostgreSQL NOTIFY batch process canceled during shutdown (PalORM stack)");
        }
        catch (OperationCanceledException oce)
        {
            // R18 审计缺陷 2（v18 B1 / PD24 孪生对称，对齐 Dapper 版 :303-308）：
            // 非关停 OCE（ProcessBatchAsync 内部超时转换等）按 Error 记录。修前两分支形态
            // 把 OCE 排除在通用 Exception 之外，异常逃出 async Task 成未观察、零 Error 日志，
            // 关停期还会被上层 catch(OCE) 当正常关停吞掉，排障误定性。
            logger.Error(oce, "PostgreSQL NOTIFY batch process cancelled without stop request (internal timeout/conversion) (PalORM stack)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "PostgreSQL NOTIFY batch process failed (PalORM stack)");
        }
        finally
        {
            // 清引用（仅持引用语义，见字段勘正注释）；finally 先于 gate 释放执行。
            _fireTask = null;
            try { _processGate.Release(); }
            catch (ObjectDisposedException)
            {
                // 停机路径：gate 已释放，in-flight 批处理的 Release 无意义
            }
        }
    }

    /// <summary>停止监听并释放后台任务（断线由上游 StopAsync 幂等处理）。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 顺序：① 取消 in-flight 批处理（协作收尾）→ ② 退订并停监听（阻止新回调）→
        // ③ 等待在飞 fire 任务 → ④ 释放 CTS。等待点必须在停监听之后——放在之前会漏掉
        // 「已进入 OnNotificationReceived、正在通过 gate 并赋值 _fireTask」的在途回调。
        //
        // R16 修复（2026-11-24，F2）：修前只 Cancel 不 await——StopAsync 返回即被宿主
        // 视为已收尾，随后容器释放 _processGate，在飞批处理只能靠 finally 的
        // ObjectDisposedException 兜底（标记可能未落库，租约悬挂至超时重投）；
        // 注释所称的「协作收尾」不成立。
        // 等待不用宿主传入的 token：关停已在推进，宿主给 StopAsync 的 token 常用于限制
        // 等待时长，沿用它会在限时取消时退回「不等」的旧语义。批处理内部已收到取消信号，
        // 其收尾时长受 ProcessBatchAsync 自身协作语义约束。
        if (_shutdownCts is not null)
            await _shutdownCts.CancelAsync().ConfigureAwait(false);

        if (_listener is not null)
        {
            _listener.OnNotification -= OnNotificationReceived;
            _listener.OnError -= OnListenerTerminated;
            await _listener.StopAsync().ConfigureAwait(false);
        }

        var inFlight = _fireTask;
        if (inFlight is not null)
        {
            try
            {
                await inFlight.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FireBatchProcessAsync 已自行 catch-log 全部异常，此处仅为防御：
                // 关停路径不得因批处理异常中断后续释放。
                logger.Warning($"PalORM PostgreSQL LISTEN in-flight batch faulted during shutdown: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // 批处理按协作语义取消收尾，属预期路径。
            }
        }

        if (_shutdownCts is not null)
        {
            _shutdownCts.Dispose();
            _shutdownCts = null;
        }
    }

    /// <summary>释放 gate 与 listener。</summary>
    public async ValueTask DisposeAsync()
    {
        // R16 修复（2026-11-24，F2 姊妹）：释放 gate 前先等在飞 fire 任务收尾。
        // 修前直接 Dispose gate——若宿主未调 StopAsync（启动失败、DI 提前释放等路径），
        // 在飞批处理仍在用 gate 与 scope，Release 只能靠 ObjectDisposedException 兜底。
        var inFlight = _fireTask;
        if (inFlight is not null)
        {
            try
            {
                await inFlight.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FireBatchProcessAsync 已自行 catch-log；释放路径不得因它中断。
                logger.Warning($"PalORM PostgreSQL LISTEN in-flight batch faulted during dispose: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // 协作取消收尾，属预期路径。
            }
        }

        _processGate.Dispose();
        if (_shutdownCts is not null)
        {
            _shutdownCts.Dispose();
            _shutdownCts = null;
        }
        if (_listener is not null)
            await _listener.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 校验通道名为合法 PG 标识符（与 Dapper 版 PostgreSqlOutboxNotifier 同款白名单：
    /// [A-Za-z0-9_]，首字符字母/下划线——LISTEN 不可参数化，拼接前防注入）。
    /// </summary>
    private static void ValidateChannelName(string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName, nameof(channelName));
        if (!IsIdentifierStart(channelName[0]))
            throw new ArgumentException(
                "PostgreSQL LISTEN/NOTIFY 通道名必须以字母或下划线开头。", nameof(channelName));
        for (var i = 1; i < channelName.Length; i++)
        {
            if (!IsIdentifierPart(channelName[i]))
                throw new ArgumentException(
                    "PostgreSQL LISTEN/NOTIFY 通道名只能包含字母、数字或下划线。", nameof(channelName));
        }
    }

    private static bool IsIdentifierStart(char c)
        => c is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char c)
        => IsIdentifierStart(c) || c is >= '0' and <= '9';
}
