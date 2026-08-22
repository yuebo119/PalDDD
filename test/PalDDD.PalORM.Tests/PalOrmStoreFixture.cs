using PalORM;
using PalORM.Sqlite;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// PalORM 测试共享 Fixture —— 替代 DapperStoreTests 的 [Before(Class)] 全局静态状态。
/// <para>
/// <b>关键差异（vs DapperStoreTests）</b>：
/// <list type="bullet">
/// <item>无 [Before(Class)] 注册 TypeHandler / 设 MatchNamesWithUnderscores（PalORM 无全局静态状态）。</item>
/// <item>每测试用独立 SQLite :memory: DataSession（天然隔离，无交叉污染）。</item>
/// <item>建表 DDL 引用 <see cref="MultiDialectSchema.Sqlite"/> 单一真源（ITM-259 四副本收敛）。</item>
/// </list>
/// </para>
/// </summary>
public static class PalOrmStoreFixture
{
    /// <summary>创建并初始化测试用 DataSession（:memory: SQLite + 建表）。</summary>
    /// <remarks>
    /// ITM-259：建表 DDL 收敛到 <see cref="MultiDialectSchema.Sqlite"/> 单一真源——
    /// 单 store 测试与多方言测试共用同一 schema，消除四份手写副本漂移。
    /// 收敛后与本 fixture 原 DDL 的已知差异：
    /// <list type="bullet">
    /// <item>events 表获得 correlation_id/causation_id/trace_parent/trace_state 4 列（超集，
    /// PalOrmEventLog 的 INSERT 已写入这些列，单 store 测试无感）。</item>
    /// <item>不再建 idx_outbox_status 索引（单一真源不含；:memory: 测试无性能依赖）。</item>
    /// </list>
    /// 保留差异：WAL pragma 为本 fixture 历史行为（对 :memory: 实际无效果，不扩散进单一真源）。
    /// </remarks>
    public static async Task<DataSession<SqliteProvider>> CreateAsync(CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct);
        // TST-306：PRAGMA/建表中途失败时 session 泄漏——对齐 MultiDialectFixture.CreateSqliteAsync
        // 的 catch-dispose 形态（失败释放已创建 session 后原样上抛）
        try
        {
            await session.ExecuteAsync($"PRAGMA journal_mode=WAL", ct);
            await MultiDialectFixture.ApplySchemaAsync(session, MultiDialectSchema.Sqlite, ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }
}
