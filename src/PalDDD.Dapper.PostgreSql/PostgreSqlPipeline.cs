// ─────────────────────────────────────────────────────────────
// ⚡ PostgreSqlPipeline — Npgsql 管道批量执行（绕过 ADO.NET）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ Add(NpgsqlParameter[]) — 显式参数，零反射，完全 AOT 安全。
//
// 性能对比：
//   传统 ADO.NET 逐条执行：N 条 SQL → N 次网络往返
//   Pipelining 批量执行：  N 条 SQL → 1 次网络往返
//
// 原理：
//   Npgsql Pipelining 将多个 SQL 命令打包在一个 TCP 帧中发送，
//   PostgreSQL 服务端按顺序执行并返回结果。不需要 Extended Query 协议
//   的 Sync 消息，避免了逐条等待的网络延迟。
//
// 适用场景：
//   - Outbox 批处理（ReleaseForRetry → INSERT → MarkProcessed）
//   - 有序事件插入（AppendEvents：乐观并发检查 + 批量 INSERT）
//   - Saga 批量更新（SaveAsync + 关联事件插入）
//
// 使用示例：
//   // AOT 安全方式（推荐）
//   await using var pipe = new PostgreSqlPipeline(conn);   // P3 修复（二十一轮）：删 .ConfigureAwait(false)——
//   pipe.Add("UPDATE outbox SET status='Pending' WHERE id=@id",   // await using 的构造表达式非 Task，该调用不合法
//            new NpgsqlParameter("@id", 42));
//   await pipe.ExecuteAsync().ConfigureAwait(false);
// ─────────────────────────────────────────────────────────────

using Npgsql;
using System.Data;
using System.Data.Common;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>Npgsql 管道批量执行 — 单次网络往返执行多条 SQL</summary>
public sealed class PostgreSqlPipeline : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlBatch _batch;

    /// <summary>创建管道（需要有效的 NpgsqlConnection）</summary>
    public PostgreSqlPipeline(DbConnection connection)
    {
        _connection = (connection as NpgsqlConnection)
            ?? throw new ArgumentException("PostgreSqlPipeline 需要 NpgsqlConnection。", nameof(connection));
        _batch = _connection.CreateBatch();
    }

    // ── AOT 安全方式：显式传入 NpgsqlParameter ──

    /// <summary>添加参数化 SQL 到管道（AOT 安全，零反射）</summary>
    public void Add(string sql, params NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlBatchCommand(sql);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        _batch.BatchCommands.Add(cmd);
    }

    // ── 执行 ──

    /// <summary>执行管道中所有命令（单次网络往返）</summary>
    /// <returns>受影响总行数（跨全部语句聚合，Npgsql 文档语义）</returns>
    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        if (_batch.BatchCommands.Count == 0) return 0;

        // P3 修复（八轮评审）：幂等守卫——连接已打开时跳过 OpenAsync（对已 Open 连接重复
        // Open 抛 InvalidOperationException；调用方复用共享连接时本管道不强制拥有开连接职责）
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        await using var reader = await _batch.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // 排空全部结果集（读循环不可省略——未读完不能 NextResult）
        // ITM-166 修复：补 ConfigureAwait(false)（对齐本方法其余 await）。
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) { }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false))
        {
            // ITM-218 修复（三十二轮）：内层 ReadAsync 补齐——ITM-166 修复不完整（:75 注释已声称对齐）
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) { }
        }

        // 三轮评审修正：Npgsql 的 RecordsAffected 是跨全部语句的聚合值——
        // 不得在多结果集循环内累加（重复计数）。排空后读一次即总受影响行数。
        return reader.RecordsAffected >= 0 ? reader.RecordsAffected : 0;
    }

    /// <summary>清空已添加的命令，重用管道</summary>
    public void Clear() => _batch.BatchCommands.Clear();

    public async ValueTask DisposeAsync()
    {
        await _batch.DisposeAsync().ConfigureAwait(false);
    }
}
