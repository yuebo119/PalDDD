using Microsoft.Extensions.DependencyInjection;
using PalDDD.Dapper.PostgreSql;

namespace PalDDD.Integration.Tests;

/// <summary>读写分离路由器注册扩展的字符串级测试——不依赖 PG 服务器（异常在任何连接建立前抛出）。</summary>
[TUnit.Core.NotInParallel("dapper-global")]
public class PostgreSqlReadWriteRouterTests
{
    /// <summary>ITM-262（P0）：副本缺 Host 的异常消息不得内嵌连接串原文——凭据段（Password 等）
    /// 进入异常消息即随宿主日志泄漏。触发条件注意：副本凭据须与主库一致才到达 Host 检查
    ///（前置 ThrowIfCredentialsMismatch 先拦截差异凭据——探针 v1 证伪记录）。</summary>
    [Test]
    public async Task AddPalReadWriteRouter_HostlessReplica_ErrorDoesNotEmbedConnectionString()
    {
        const string secret = "Sup3rS3cret!";
        var primary = $"Host=127.0.0.1;Port=5432;Database=probe;Username=probe;Password={secret}";
        // 凭据与主库完全一致、仅缺 Host——直达 Host 检查分支
        var replica = $"Port=5433;Database=probe;Username=probe;Password={secret}";

        var ex = await Assert.That(() =>
        {
            var services = new ServiceCollection();
            services.AddPalReadWriteRouter(primary, [replica]);
        }).Throws<ArgumentException>();

        await Assert.That(ex!.Message.Contains(secret)).IsFalse();
        await Assert.That(ex!.Message.Contains("index 0")).IsTrue();
        await Assert.That(ex!.ParamName).IsEqualTo("replicaConnectionStrings");
    }

    /// <summary>姊妹回归：差异凭据的前置校验消息同样不含连接串原文（既有形态锁定）。</summary>
    [Test]
    public async Task AddPalReadWriteRouter_CredentialMismatch_ErrorDoesNotEmbedConnectionString()
    {
        const string secret = "ReplicaOnlyPass!";
        var primary = "Host=127.0.0.1;Port=5432;Database=probe;Username=probe;Password=primary-pass";
        var replica = $"Host=replica-1;Database=other;Username=other;Password={secret}";

        var ex = await Assert.That(() =>
        {
            var services = new ServiceCollection();
            services.AddPalReadWriteRouter(primary, [replica]);
        }).Throws<ArgumentException>();

        await Assert.That(ex!.Message.Contains(secret)).IsFalse();
    }
}
