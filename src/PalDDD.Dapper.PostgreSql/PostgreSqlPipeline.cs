// ─────────────────────────────────────────────────────────────
// ⚡ PostgreSqlPipeline — Npgsql 管道批量执行（绕过 ADO.NET）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ Add(NpgsqlParameter[]) — 显式参数，零反射，完全 AOT 安全。
//   ⚠️ Add(sql, object) — 便捷重载使用反射，AOT 下需 [DynamicallyAccessedMembers]。
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
//   await using var pipe = new PostgreSqlPipeline(conn).ConfigureAwait(false);
//   pipe.Add("UPDATE outbox SET status='Pending' WHERE id=@id",
//            new NpgsqlParameter("@id", 42));
//   await pipe.ExecuteAsync().ConfigureAwait(false);
// ─────────────────────────────────────────────────────────────

using Npgsql;
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
    /// <returns>受影响总行数</returns>
    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        if (_batch.BatchCommands.Count == 0) return 0;

        await _connection.OpenAsync(ct).ConfigureAwait(false);
        var reader = await _batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int total = 0;

        do
        {
            while (await reader.ReadAsync(ct))
                total++;
        }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false));

        return total;
    }

    /// <summary>清空已添加的命令，重用管道</summary>
    public void Clear() => _batch.BatchCommands.Clear();

    public async ValueTask DisposeAsync()
    {
        await _batch.DisposeAsync().ConfigureAwait(false);
    }
}
