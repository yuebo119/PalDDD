using PalDDD.Testing;
using TUnit.Assertions;
using TUnit.Core;

namespace PalDDD.PalORM.Tests;

/// <summary>ITM-208 纯逻辑安全回归：不连接数据库，不执行 DROP。</summary>
public sealed class MultiDialectSafetyTests
{
    [Test]
    public async Task ExternalCleanupWithoutExplicitConfirmationIsRejected()
    {
        var allowed = TestEnvironment.CanCleanExternalDatabase(
            "Host=127.0.0.1;Database=palddd_test_run_123",
            "palddd_test_",
            explicitConfirmation: false);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task ExternalCleanupForNonTestDatabaseIsRejected()
    {
        var allowed = TestEnvironment.CanCleanExternalDatabase(
            "Server=127.0.0.1;Database=production",
            "palddd_test_",
            explicitConfirmation: true);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task UniqueTestDatabaseWithExplicitConfirmationIsAccepted()
    {
        var allowed = TestEnvironment.CanCleanExternalDatabase(
            "Host=127.0.0.1;Port=5432;Database=palddd_test_run_20260819_001",
            "palddd_test_",
            explicitConfirmation: true);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task MalformedConfigurationFailsClosed()
    {
        await Assert.That(() => TestEnvironment.ValidateConfigurationJson("{\"TestEnvironment\":"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ConfigurationWithoutTestEnvironmentFailsClosed()
    {
        await Assert.That(() => TestEnvironment.ValidateConfigurationJson("{}"))
            .Throws<InvalidOperationException>();
    }
}
