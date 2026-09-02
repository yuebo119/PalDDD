using Microsoft.Extensions.DependencyInjection;
using PalDDD.Dapper.MySql;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔀 MySqlMultiHost 入口校验测试 — v48 P3（v47/v48 修复的回归网）
// ═══════════════════════════════════════════════════════════════
// 覆盖 LoadBalance / LeastConnections 两入口的 Server 配置 fail-fast
//（均在 Build 前抛出，零网络 IO）：
//   - Server 列表内部重复（v48 P2 查重，"Server=db1,db1" 静默 Build 出同机
//     双份条目 → RoundRobin 权重倾斜 / LeastConnections 同机双池）
//   - Server 空串（v47 空校验，静默 Build 出 localhost 默认池）
// 镜像同目录 PostgreSqlMultiHostPortEncodingTests 形态
//（MySqlMultiHost 原先全仓零测试，本文件为首份入口级回归网）。
// ═══════════════════════════════════════════════════════════════

public sealed class MySqlMultiHostTests
{
    // ── v48 P3（V3）：Server 列表内部重复 — v48 P2 查重的回归网 ──

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_DuplicateServer_ThrowsArgumentException()
    {
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLoadBalance("Server=db1,db1;Database=pal"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLeastConnections_DuplicateServer_ThrowsArgumentException()
    {
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLeastConnections("Server=db1,db1;Database=pal"))
            .Throws<ArgumentException>();
    }

    // ── v48 P3（V4）：Server 空串 — v47 空校验的回归网 ──

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_BlankServer_ThrowsArgumentException()
    {
        // 空白串守卫放行 "Server=" 后 Server 属性为空串——空校验 fail-fast，
        // 不静默 Build 出 localhost 默认池
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLoadBalance("Server=;Database=pal"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLeastConnections_BlankServer_ThrowsArgumentException()
    {
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLeastConnections("Server=;Database=pal"))
            .Throws<ArgumentException>();
    }
}
