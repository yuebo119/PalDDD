using PalDDD.Testing;

namespace PalDDD.PalORM.Tests;

/// <summary>ITM-208 纯逻辑安全回归：不连接数据库，不执行 DROP。</summary>
public sealed class MultiDialectSafetyTests
{
    [Test]
    public async Task SingleAlias_ValidConnection_IsAccepted()
    {
        var valid = TestEnvironment.TryGetUniqueDatabaseName(
            "Host=127.0.0.1;Database=palddd_test_run_123",
            out var database);

        await Assert.That(valid).IsTrue();
        await Assert.That(database).IsEqualTo("palddd_test_run_123");
    }

    [Test]
    public async Task DuplicateAlias_SameKey_IsRejected()
    {
        var valid = TestEnvironment.TryGetUniqueDatabaseName(
            "Host=127.0.0.1;Database=palddd_test_one;Database=palddd_test_two",
            out _);

        await Assert.That(valid).IsFalse();
    }

    [Test]
    public async Task ConflictingAliases_DifferentKeys_IsRejected()
    {
        var valid = TestEnvironment.TryGetUniqueDatabaseName(
            "Server=127.0.0.1;Database=palddd_test_one;Initial Catalog=palddd_test_two",
            out _);

        await Assert.That(valid).IsFalse();
    }

    [Test]
    public async Task CatalogAlias_BypassAttempt_IsRejected()
    {
        var valid = TestEnvironment.TryGetUniqueDatabaseName(
            "Server=127.0.0.1;Database=palddd_test_one;Catalog=production",
            out _);

        await Assert.That(valid).IsFalse();
    }

    [Test]
    public async Task ExternalFixture_DisabledTestcontainers_IsRejected()
    {
        await Assert.That(() => MultiDialectFixture.EnsureTestcontainersRequired(false, "PostgreSQL"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task GeneratedDatabase_CreatedStateAndStrictName_Required()
    {
        await Assert.That(TestEnvironment.IsStrictGeneratedDatabaseName(
            "palddd_probe_pg_a1b2c3", "palddd_probe_pg_", databaseCreated: true)).IsTrue();
        await Assert.That(TestEnvironment.IsStrictGeneratedDatabaseName(
            "palddd_probe_pg_a1b2c3", "palddd_probe_pg_", databaseCreated: false)).IsFalse();
        await Assert.That(TestEnvironment.IsStrictGeneratedDatabaseName(
            "production", "palddd_probe_pg_", databaseCreated: true)).IsFalse();
    }

    [Test]
    public async Task Dispose_PrimaryFails_SecondaryStillDisposed()
    {
        var primary = new RecordingAsyncDisposable(new InvalidOperationException("primary"));
        var secondary = new RecordingAsyncDisposable();

        await Assert.That(async () => await AsyncResourceDisposer.DisposeAsync(primary, secondary))
            .Throws<InvalidOperationException>();
        await Assert.That(primary.DisposeCount).IsEqualTo(1);
        await Assert.That(secondary.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task Dispose_DualFailures_Aggregated()
    {
        var primary = new RecordingAsyncDisposable(new InvalidOperationException("primary"));
        var secondary = new RecordingAsyncDisposable(new IOException("secondary"));

        var exception = await Assert.That(async () => await AsyncResourceDisposer.DisposeAsync(primary, secondary))
            .Throws<AggregateException>();
        await Assert.That(exception!.InnerExceptions).Count().IsEqualTo(2);
        await Assert.That(primary.DisposeCount).IsEqualTo(1);
        await Assert.That(secondary.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task MalformedConfiguration_FailsClosed()
    {
        await Assert.That(() => TestEnvironment.ValidateConfigurationJson("{\"TestEnvironment\":"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task MissingTestEnvironmentSection_FailsClosed()
    {
        await Assert.That(() => TestEnvironment.ValidateConfigurationJson("{}"))
            .Throws<InvalidOperationException>();
    }

    private sealed class RecordingAsyncDisposable(Exception? exception = null) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return exception is null ? ValueTask.CompletedTask : ValueTask.FromException(exception);
        }
    }
}
