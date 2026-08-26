namespace PalDDD.Transactions.Tests;

/// <summary>v12 能力实证盲区焊死：LeaseOwner 互异性——注入轮实证该值零测试覆盖（固定
/// owner 使同节点多实例 fencing 失效→僵尸标记/重复发布，154 测试全绿测不出）。本测试
/// 锁定"机器名:ULID"契约的互异语义：同节点多 Options 实例的 owner 必须互不相同。</summary>
public sealed class LeaseOwnerFactoryTests
{
    [Test]
    public async Task Create_DistinctCalls_ProduceDistinctOwners()
    {
        // internal 工厂经 InternalsVisibleTo 可达；两次构造模拟同节点多 Options 实例
        var first = LeaseOwnerFactory.Create();
        var second = LeaseOwnerFactory.Create();

        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(string.IsNullOrWhiteSpace(first)).IsFalse();
    }

    [Test]
    public async Task Create_IncludesMachineNamePrefix()
        => await Assert.That(LeaseOwnerFactory.Create().StartsWith(Environment.MachineName + ":", StringComparison.Ordinal)).IsTrue();
}
