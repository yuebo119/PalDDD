// ─────────────────────────────────────────────────────────────
// 🔔 PostgreSqlOutboxNotifier — LISTEN/NOTIFY 替代轮询
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ Npgsql NOTIFY 协议 — PostgreSQL 原生功能，ADO.NET 标准事件回调。
//   ✅ 零反射 — Notification 事件通过 C# event 传递，无 IL 生成。
//   ✅ IsAotCompatible=true（此项目 csproj）。
//
// 架构设计（DDD/Clean Architecture 友好）：
//   此服务是 PostgreSQL 专属的 IHostedService 扩展。
//   - 不做侵入式修改：默认 OutboxProcessor（PeriodicTimer 轮询）保持不变。
//   - 通过 DI opt-in 注册：AddPalPostgreSqlOutboxNotifier()。
//   - 收到 NOTIFY 后触发 OutboxBatchProcessor.ProcessBatchAsync() — 复用现有批处理逻辑。
//
// PostgreSQL 端配置：
//   CREATE OR REPLACE FUNCTION notify_outbox() RETURNS trigger AS $$
//   BEGIN
//     PERFORM pg_notify('outbox_channel', 'new_message');
//     RETURN NEW;
//   END;
//   $$ LANGUAGE plpgsql;
//
//   CREATE TRIGGER outbox_notify
//     AFTER INSERT OR UPDATE ON outbox_messages
//     FOR EACH ROW WHEN (NEW.status = 'Pending')
//     EXECUTE FUNCTION notify_outbox();
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalDDD.Core.Logging;
using Npgsql;
using System.Diagnostics.CodeAnalysis;

using PalDDD.Transactions;
namespace PalDDD.Dapper.PostgreSql;

/// <summary>
/// PostgreSQL LISTEN/NOTIFY 后台服务。
/// 替换默认的 PeriodicTimer 轮询，实现零延迟 Outbox 处理。
/// </summary>
/// <remarks>
/// 注册方式（在 Program.cs 中）：
///   services.AddPalNpgsqlDataSource(connStr);
///   services.AddPalPostgreSqlOutboxNotifier();
///
/// ⚠️ 必须已在 DI 中注册 <see cref="OutboxProcessor"/>（默认轮询器）。
///   此 Notifier 将替换其轮询延迟效果——收到 NOTIFY 后立即触发批处理。
/// </remarks>
public sealed class PostgreSqlOutboxNotifier : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _channelName;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPalLogger<PostgreSqlOutboxNotifier> _logger;
    private readonly TimeProvider _timeProvider;
    private int _reconnectAttempts;

    // 🟡 P1 修复 (2026-06-21): SemaphoreSlim 背压控制
    // 原实现 _ = Task.Run(...) 在 NOTIFY 风暴时启动数千个并发 Task 压垮 DB。
    // SemaphoreSlim(1,1) 保证同时最多只有一个批处理在执行，多个 NOTIFY 被合并。
    // OutboxBatchProcessor 本身会处理所有 pending 消息，一次批处理足以覆盖多次 NOTIFY。
    private readonly SemaphoreSlim _processGate = new(1, 1);

    // P2-1 修复 (2026-07-28): 自通知节流——避免高积压场景的 NOTIFY 风暴
    private DateTimeOffset _lastSelfNotify = DateTimeOffset.MinValue;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);

    /// <summary>释放 SemaphoreSlim</summary>
    public override void Dispose()
    {
        _processGate.Dispose();
        base.Dispose();
    }

    /// <param name="dataSource">Npgsql 数据源（由 <c>AddPalNpgsqlDataSource</c> 注册，Singletion）</param>
    /// <param name="scopeFactory">用于创建 Scoped OutboxBatchProcessor</param>
    /// <param name="channelName">PostgreSQL 通知通道名（默认 "outbox_channel"）</param>
    /// <param name="logger">日志</param>
    /// <param name="timeProvider">时间提供者（测试可注入 FakeTimeProvider）</param>
    public PostgreSqlOutboxNotifier(
        NpgsqlDataSource dataSource,
        IServiceScopeFactory scopeFactory,
        IPalLogger<PostgreSqlOutboxNotifier> logger,
        TimeProvider? timeProvider = null,
        string channelName = "outbox_channel")
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channelName = channelName ?? throw new ArgumentNullException(nameof(channelName));
        ValidateChannelName(_channelName);
    }

    /// <summary>
    /// 校验 LISTEN/NOTIFY 通道名为合法 PostgreSQL 标识符。
    /// LISTEN/NOTIFY 通道名不可参数化（Npgsql 协议限制），必须字符串拼接，
    /// 故需在入口处做白名单校验防止 SQL 注入（与 PostgreSqlAuditor.QuoteIdentifier 同级防护）。
    /// </summary>
    private static void ValidateChannelName(string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName, nameof(channelName));
        if (!IsIdentifierStart(channelName[0]))
            throw new ArgumentException(
                "PostgreSQL LISTEN/NOTIFY 通道名必须以字母或下划线开头。", nameof(channelName));
        for (int i = 1; i < channelName.Length; i++)
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception",
        Justification = "LISTEN/NOTIFY 监听循环必须隔离任意异常以支持断线重连退避；OperationCanceledException 已由前一 catch 分支处理。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 🔴 P1 修复 (2026-06-21): 使用 NpgsqlDataSource.CreateConnection()
                // 替代 new NpgsqlConnection(connectionString)，复用连接池配置、类型映射器和 ApplicationName
                await using var conn = _dataSource.CreateConnection();
                await conn.OpenAsync(stoppingToken).ConfigureAwait(false);

                // 注册 LISTEN
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"LISTEN {_channelName}";
                await cmd.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);

                _logger.Information($"PostgreSQL LISTEN started on channel '{_channelName}'");
                _reconnectAttempts = 0; // 连接成功，重置退避计数

                // 阻塞等待 NOTIFY — 零 CPU 消耗
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken).ConfigureAwait(false);

                    // NOTIFY 到达 → 有背压触发批处理
                    // 🟡 P1 修复 (2026-06-21): 使用 SemaphoreSlim 防止 NOTIFY 风暴。
                    // 多个 NOTIFY 只需要一次批处理（一次处理拿走所有 pending），
                    // 多余的并发请求只会竞争 DB 连接池。
                    _logger.Debug($"PostgreSQL NOTIFY received on channel '{_channelName}'");
                    if (await _processGate.WaitAsync(0, stoppingToken).ConfigureAwait(false))
                    {
                        // P2 修复：Task.Run 不再传 stoppingToken——传入时若调度前取消，
                        // 任务不执行、已获取的 gate 永不释放。去掉 token 后任务必然运行，
                        // 由 FireBatchProcessAsync 内部协作取消 + finally 兜底释放。
                        _ = Task.Run(() => FireBatchProcessAsync(stoppingToken));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "PostgreSQL NOTIFY connection lost, reconnecting...");
                // 指数退避：1s → 2s → 4s → 8s → 最大 30s
                var delay = Math.Min(30_000, 1_000 * (int)Math.Pow(2, _reconnectAttempts));
                _reconnectAttempts++;
                await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception",
        Justification = "后台批处理触发任务必须隔离任意异常，避免未处理异常终止后台服务；失败已通过 LogBatchProcessFailed 记录。")]
    private async Task FireBatchProcessAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
            await processor.ProcessBatchAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "PostgreSQL NOTIFY batch process failed");
        }
        finally
        {
            // P2 修复：Dispose 已释放 gate 时 in-flight 批处理的 Release 会抛
            // ObjectDisposedException 且无人观察——吞掉（停机路径，gate 已无意义）
            try { _processGate.Release(); }
            catch (ObjectDisposedException) { }

            // 🔴 P1 修复 (2026-07-28): 批处理完成后主动发送一次 NOTIFY 自唤醒。
            // 必要性：消息可能在批处理执行期间（_processGate 被占用，新的 NOTIFY 被 TryEnter(0) 丢弃）
            // 或在批处理刚结束、监听线程尚未重新进入 conn.WaitAsync() 之前被插入。
            // 通过此处主动自通知，唤醒主循环重新进入 WaitAsync，保证 at-least-once 语义。
            //
            // P2-1 修复 (2026-07-28): 用时间戳节流避免高积压场景的 NOTIFY 风暴——
            // 距上次自通知不足 _pollInterval 则跳过（此时 PeriodicTimer 会兜底触发）。
            // 失败可忽略——最坏情况下次外部 NOTIFY 或 PeriodicTimer 仍会触发；不影响正确性。
            var now = _timeProvider.GetUtcNow();
            if (now - _lastSelfNotify >= _pollInterval)
            {
                _lastSelfNotify = now;
                try
                {
                    await using var conn = _dataSource.CreateConnection();
                    if (conn.State != System.Data.ConnectionState.Open)
                        await conn.OpenAsync(ct).ConfigureAwait(false);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"NOTIFY {_channelName}";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // 自唤醒失败不影响主流程；外部 NOTIFY 或下次插入仍会触发。
                }
            }
        }
    }
}
