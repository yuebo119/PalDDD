using Npgsql;
using PalDDD.Dapper.PostgreSql;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔀 PostgreSqlMultiHost 端口编码测试 — ITM-132 回归
// ═══════════════════════════════════════════════════════════════
// Npgsql 的共享 Port 只对未内嵌端口的主机生效：主库 Port≠5432 时，
// 未编码 host 的副本/备机会错误继承主库 Port。EncodeHostEntry 保证：
//   - primary Port ≠ 5432：全部 Host 显式 host:port（含显式 5432）
//   - primary Port == 5432：仅非 5432 端口的 Host 编码（未编码继承 5432 语义正确）
// ═══════════════════════════════════════════════════════════════

public sealed class PostgreSqlMultiHostPortEncodingTests
{
    [Test]
    public async Task EncodeHostEntry_PrimaryDefaultPort_HostDefaultPort_ReturnsBareHost()
    {
        var host = new NpgsqlConnectionStringBuilder("Host=pg-replica;Port=5432");

        var encoded = PostgreSqlMultiHost.EncodeHostEntry(host, primaryPort: 5432);

        await Assert.That(encoded).IsEqualTo("pg-replica");
    }

    [Test]
    public async Task EncodeHostEntry_PrimaryDefaultPort_HostCustomPort_EncodesPort()
    {
        var host = new NpgsqlConnectionStringBuilder("Host=pg-replica;Port=5433");

        var encoded = PostgreSqlMultiHost.EncodeHostEntry(host, primaryPort: 5432);

        await Assert.That(encoded).IsEqualTo("pg-replica:5433");
    }

    [Test]
    public async Task EncodeHostEntry_PrimaryCustomPort_HostExplicitDefaultPort_EncodesExplicit5432()
    {
        // ITM-132 核心回归：primary Port=5433 时，显式 5432 的副本也必须编码，
        // 否则未编码 host 会继承 primary Port=5433，读流量/故障转移落到错误实例。
        var host = new NpgsqlConnectionStringBuilder("Host=pg-replica;Port=5432");

        var encoded = PostgreSqlMultiHost.EncodeHostEntry(host, primaryPort: 5433);

        await Assert.That(encoded).IsEqualTo("pg-replica:5432");
    }

    [Test]
    public async Task EncodeHostEntry_PrimaryCustomPort_HostCustomPort_EncodesPort()
    {
        var host = new NpgsqlConnectionStringBuilder("Host=pg-replica;Port=5434");

        var encoded = PostgreSqlMultiHost.EncodeHostEntry(host, primaryPort: 5433);

        await Assert.That(encoded).IsEqualTo("pg-replica:5434");
    }

    [Test]
    public async Task EncodeHostEntry_HostMissing_Throws()
    {
        // NpgsqlConnectionStringBuilder 在未指定 Host 时 Host 返回空字符串（Npgsql 10.0.3 实证）
        var host = new NpgsqlConnectionStringBuilder("Database=pal;Port=5432");

        await Assert.That(() => PostgreSqlMultiHost.EncodeHostEntry(host, 5432)).Throws<ArgumentException>();
    }
}
