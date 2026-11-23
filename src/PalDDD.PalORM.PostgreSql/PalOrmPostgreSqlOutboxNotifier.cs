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

        _shutdownCts = new CancellationTokenSource();

        // 构造不建连；StartAsync 后才连接并 LISTEN（上游 XML 契约）。
        // 订阅者异常被上游隔离（不会终止重连循环），此处仍 catch-log 防未观察异常。
        _listener = new PgNotificationListener(connectionString, channelName);
        _listener.OnNotification += OnNotificationReceived;
        await _listener.StartAsync(cancellationToken).ConfigureAwait(false);
        logger.Information($"PalORM PostgreSQL LISTEN started on channel '{channelName}'");
    }

    private void OnNotificationReceived(object? sender, PgNotificationEventArgs e)
    {
        logger.Debug($"PostgreSQL NOTIFY received on channel '{e.Channel}'");
        // 非阻塞 try-enter：gate 被占用时本次 NOTIFY 被丢弃——at-least-once 由兜底保证
        //（PeriodicTimer 轮询 + 上游 listener 心跳保活的后续通知），对齐 Dapper 版语义。
        if (_processGate.Wait(0))
        {
            // 不传宿主 token：传入时若调度前取消，任务不执行、已获取的 gate 永不释放
            //（Dapper 版 P2 修复同款）；关停协作取消经 _shutdownCts（StopAsync 先取消），
            // gate 释放由 FireBatchProcessAsync 的 finally 兜底。
#pragma warning disable S8949 // 刻意不向 Task.Run 传 token（调度前取消会使 gate 永不释放）
            _ = Task.Run(() => FireBatchProcessAsync());
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "PostgreSQL NOTIFY batch process failed (PalORM stack)");
        }
        finally
        {
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
        // 先取消 in-flight 批处理（协作收尾），再停监听——顺序反了会让最后一批
        // 在监听已停、轮询器同停的窗口内无人等待其完成。
        if (_shutdownCts is not null)
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            _shutdownCts.Dispose();
            _shutdownCts = null;
        }

        if (_listener is not null)
        {
            _listener.OnNotification -= OnNotificationReceived;
            await _listener.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>释放 gate 与 listener。</summary>
    public async ValueTask DisposeAsync()
    {
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
