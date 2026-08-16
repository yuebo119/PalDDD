namespace PalDDD.Integration.Tests;

using PalDDD.Dapper.PostgreSql;

// ═══════════════════════════════════════════════════════════════
// 🧪 PostgreSqlSharding 测试 — 分片路由策略构造守卫与路由契约
// ═══════════════════════════════════════════════════════════════
// ITM-136 回归：ConsistentHashSharding virtualNodes <= 0 构造即抛，
// 不再延迟到 GetShardId 时空环取模 0（DivideByZero）。
// ═══════════════════════════════════════════════════════════════

public sealed class PostgreSqlShardingTests
{
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-100)]
    public async Task ConsistentHashSharding_NonPositiveVirtualNodes_Throws(int virtualNodes)
    {
        await Assert.That(() => new ConsistentHashSharding(2, virtualNodes)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ConsistentHashSharding_ValidVirtualNodes_RoutesWithinShardRange()
    {
        var sharding = new ConsistentHashSharding(4, virtualNodes: 16);

        for (var i = 0; i < 100; i++)
        {
            var shard = sharding.GetShardId(Guid.NewGuid());
            await Assert.That(shard).IsGreaterThanOrEqualTo(0).And.IsLessThan(4);
        }
    }
}
