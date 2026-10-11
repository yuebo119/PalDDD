namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════
// MySqlLeasePolicy 单元测试（ADR-025）——版本边界与 fail-safe 回退。
// 正例负例成对：8.0.17/8.0.18 是判定线的两侧，MariaDB/垃圾串/空值锁回退路径。
// ═══════════════════════════════════════════════════════════

public sealed class MySqlLeasePolicyTests
{
    [Test]
    public async Task Boundary_8_0_17_FallsBack()
        => await Assert.That(MySqlLeasePolicy.UseSkipLocked("8.0.17")).IsFalse();

    [Test]
    public async Task Boundary_8_0_18_Enables()
        => await Assert.That(MySqlLeasePolicy.UseSkipLocked("8.0.18")).IsTrue();

    [Test]
    public async Task Version_8_4_11_Enables()
        => await Assert.That(MySqlLeasePolicy.UseSkipLocked("8.4.11")).IsTrue();

    [Test]
    public async Task Version_9_0_Enables()
        => await Assert.That(MySqlLeasePolicy.UseSkipLocked("9.0.0")).IsTrue();

    [Test]
    public async Task VersionWithSuffix_Enables()
        => await Assert.That(MySqlLeasePolicy.UseSkipLocked("8.0.18-commercial")).IsTrue();

    [Test]
    public async Task MariaDB_AlwaysFallsBack()
    {
        // 派生表锁语义独立矩阵，未验证——即使高版本也回退
        await Assert.That(MySqlLeasePolicy.UseSkipLocked("11.4.3-MariaDB")).IsFalse();
        await Assert.That(MySqlLeasePolicy.UseSkipLocked("mariadb.org binary distribution")).IsFalse();
    }

    [Test]
    public async Task GarbageOrNull_FallsBack()
    {
        await Assert.That(MySqlLeasePolicy.UseSkipLocked(null)).IsFalse();
        await Assert.That(MySqlLeasePolicy.UseSkipLocked("")).IsFalse();
        await Assert.That(MySqlLeasePolicy.UseSkipLocked("abc")).IsFalse();
        await Assert.That(MySqlLeasePolicy.UseSkipLocked("8")).IsFalse();
        await Assert.That(MySqlLeasePolicy.UseSkipLocked("x.y.z")).IsFalse();
    }

    [Test]
    public async Task MinimumVersionConstant_MatchesBoundary()
        => await Assert.That(MySqlLeasePolicy.MinimumSkipLockedVersion).IsEqualTo("8.0.18");
}
