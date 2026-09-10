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

    // ── v53 P1：冒号判定形态 — v50 F3 误改 Contains(':') 全拦（IPv6 全形态死路）的回归网 ──

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_BareIpv6_DoesNotThrow()
    {
        // 裸 IPv6（多冒号）是合法字面量——Dns.GetHostAddresses 可解析，必须放行；
        // 校验与 Build 均零网络 IO（Build 仅构造 MySqlConnection 对象）
        var services = new ServiceCollection()
            .AddPalMySqlDataSourceWithLoadBalance("Server=::1;Database=pal;Port=3306");
        await Assert.That(services.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_EmbeddedPort_ThrowsArgumentException()
    {
        // 唯一冒号（host:port 内嵌语法）——MySqlConnector 不支持，照拦
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLoadBalance("Server=db1:3306;Database=pal"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_BareIpv6ListWithDuplicate_ThrowsArgumentException()
    {
        // 裸 IPv6 多主机 + 重复查重兼容——::1 归一化后查重仍生效
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLoadBalance("Server=::1,::1;Database=pal;Port=3306"))
            .Throws<ArgumentException>();
    }

    // ── v54/v55 守卫回归网：多冒号 IPAddress.TryParse 收窄（"a:b:c" 等垃圾多冒号配置期定位）──

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_GarbageMultiColon_Throws()
    {
        // 多冒号但非合法 IPv6——TryParse 失败，配置期 fail-fast（原延迟到建连期 SocketException）
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLoadBalance("Server=a:b:c;Database=pal"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_ZoneIdScopedIpv6_DoesNotThrow()
    {
        // v55 .NET 11 实测勘正（推翻 v55 B 片 PowerShell 5.1 结论）：IPAddress.TryParse 对
        // scoped IPv6（fe80::1%eth0 / %25 编码 / %3 数字 zone）在 .NET Core 3.0+ 全部解析成功
        // ——均属合法 IPv6 字面量，配置层放行（Windows PowerShell 5.1 的 False 是旧运行时行为）
        var services = new ServiceCollection()
            .AddPalMySqlDataSourceWithLoadBalance("Server=fe80::1%eth0;Database=pal");
        await Assert.That(services.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task AddPalMySqlDataSource_BlankEntryInServerList_Throws()
    {
        // v53 单主机空段（B-P2-2 第五姊妹回归网）
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSource("Server=db1,,db2;Database=pal"))
            .Throws<ArgumentException>();
    }

    // ── v74 P3（F1-2 收口）：LoadBalance 显式冲突 fail-fast 的回归网 ──
    // 原三入口无条件覆盖串内显式值（静默改策略零警告）；显式矛盾现 fail-fast，
    // 显式同值（合法冗余）与未显式（方法赋策略）放行

    [Test]
    public async Task AddPalMySqlDataSourceWithFailover_ExplicitLoadBalanceConflict_Throws()
    {
        // 串内显式 LeastConnections 调 WithFailover——被静默改 FailOver 的原触发形态
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithFailover(
                    "Server=db1;Database=pal;LoadBalance=LeastConnections",
                    "Server=db2;Database=pal"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_ExplicitLoadBalanceConflict_Throws()
    {
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLoadBalance("Server=db1,db2;Database=pal;LoadBalance=LeastConnections"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLeastConnections_ExplicitLoadBalanceConflict_Throws()
    {
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithLeastConnections("Server=db1,db2;Database=pal;LoadBalance=RoundRobin"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_ExplicitMatchingStrategy_DoesNotThrow()
    {
        // 显式同值（合法冗余）放行——策略与方法名一致，非矛盾（同时锁定 TryGetValue 值形态
        // 比较口径：显式 RoundRobin 与方法策略 ToString 归一相等）
        var services = new ServiceCollection()
            .AddPalMySqlDataSourceWithLoadBalance("Server=db1,db2;Database=pal;LoadBalance=RoundRobin");
        await Assert.That(services.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithLoadBalance_NoExplicitStrategy_DoesNotThrow()
    {
        // 未显式（默认场景）放行——策略由方法名决定，不因默认值 FailOver ≠ RoundRobin 误抛
        var services = new ServiceCollection()
            .AddPalMySqlDataSourceWithLoadBalance("Server=db1,db2;Database=pal");
        await Assert.That(services.Count).IsGreaterThan(0);
    }

    // ── P3#7：standby 侧 LoadBalance 冲突 fail-fast（v74 F1-2 的 standby 侧缺口收口）──
    // 合并只取 primaryBuilder——standby 串显式 LoadBalance 值原被静默丢弃零警告

    // ── P3#20：MySQL SslMode 冲突校验行为测试（v65 P3 F1-3 修复的姊妹测试收口）──
    // SslMode 属"主机列表内统一生效"的共享参数，显式异值 = TLS 配置静默降级，v65 已
    // 纳入 ThrowIfCredentialsMismatch 校验集——此前零行为测试锁定（验证轮 P3#20 命中）

    [Test]
    public async Task AddPalMySqlDataSourceWithFailover_SslModeMismatch_Throws()
    {
        // standby 与 primary SslMode 显式不一致（Required vs None）→ ArgumentException
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithFailover(
                    "Server=db1;Database=pal;User Id=root;Password=p;SslMode=Required",
                    "Server=db2;Database=pal;User Id=root;Password=p;SslMode=None"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithFailover_SslModeExplicitSame_DoesNotThrow()
    {
        // 显式同值（合法冗余）放行——两侧均显式 Required，非矛盾
        var services = new ServiceCollection()
            .AddPalMySqlDataSourceWithFailover(
                "Server=db1;Database=pal;User Id=root;Password=p;SslMode=Required",
                "Server=db2;Database=pal;User Id=root;Password=p;SslMode=Required");
        await Assert.That(services.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithFailover_ExplicitLoadBalanceConflictOnStandby_Throws()
    {
        // primary 无 LoadBalance + standby 显式 RoundRobin——原被静默丢弃（方法仍 FailOver），
        // 现 standby 侧同款 fail-fast（守卫族双侧对称：SslMode/凭据/Port 比对均两侧同参）
        await Assert.That(() => new ServiceCollection()
                .AddPalMySqlDataSourceWithFailover(
                    "Server=db1;Database=pal",
                    "Server=db2;Database=pal;LoadBalance=RoundRobin"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddPalMySqlDataSourceWithFailover_ExplicitLoadBalanceSameOnStandby_DoesNotThrow()
    {
        // standby 显式 FailOver 同值（合法冗余）放行——非矛盾，锁定现状
        var services = new ServiceCollection()
            .AddPalMySqlDataSourceWithFailover(
                "Server=db1;Database=pal",
                "Server=db2;Database=pal;LoadBalance=FailOver");
        await Assert.That(services.Count).IsGreaterThan(0);
    }
}
