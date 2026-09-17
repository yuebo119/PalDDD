// ─────────────────────────────────────────────────────────────
// 🧪 Dapper DI 注册完整性测试 — 审计 2026-09-17 A-3
// ─────────────────────────────────────────────────────────────
// 验证 AddPalDapperTransactions 注册的三个补齐 store 可从容器解析：
//   IEventLog → DapperEventLog
//   IProjectionCheckpointStore → DapperProjectionCheckpointStore
//   IUnitOfWork → DapperUnitOfWork
// 使用 SQLite :memory: — 零外部依赖。

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core.Repository;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Projections;
using PalDDD.Transactions;
using System.Data.Common;

namespace PalDDD.Integration.Tests;

/// <summary>Dapper DI 注册面完整性测试（M2-2 / 审计 A-3）。</summary>
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class DapperDiRegistrationTests
{
    [Test]
    public async Task AddPalDapperTransactions_ResolvesEventLog_ProjectionCheckpoint_UnitOfWork()
    {
        var services = new ServiceCollection();
        services.AddPalDapperTransactions(DapperDbType.Sqlite, "Data Source=:memory:");

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // 三个补齐的 store 均可解析（构造依赖 DbConnection + DapperDbType 已注册）
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLog>();
        await Assert.That(eventLog).IsTypeOf<DapperEventLog>();

        var checkpointStore = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        await Assert.That(checkpointStore).IsTypeOf<DapperProjectionCheckpointStore>();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await Assert.That(unitOfWork).IsTypeOf<DapperUnitOfWork>();
    }

    [Test]
    public async Task AddPalDapperTransactions_ResolvesExistingStores_AlsoStillWorks()
    {
        // 回归：原有注册面（Outbox/Inbox/Idempotency）不被新增注册破坏
        var services = new ServiceCollection();
        services.AddPalDapperTransactions(DapperDbType.Sqlite, "Data Source=:memory:");

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var outbox = scope.ServiceProvider.GetRequiredService<IPalOutboxStore>();
        await Assert.That(outbox).IsTypeOf<DapperOutboxStore>();

        var inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        await Assert.That(inbox).IsTypeOf<DapperInboxStore>();
    }

    [Test]
    public async Task AddPalDapperTransactions_ResolvesStores_SameScopedConnection()
    {
        // 同一 scope 内所有 store 共享同一 DbConnection（Scoped 生命周期）
        var services = new ServiceCollection();
        services.AddPalDapperTransactions(DapperDbType.Sqlite, "Data Source=:memory:");

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var conn1 = scope.ServiceProvider.GetRequiredService<DbConnection>();
        var conn2 = scope.ServiceProvider.GetRequiredService<DbConnection>();
        await Assert.That(ReferenceEquals(conn1, conn2)).IsTrue();
    }
}
