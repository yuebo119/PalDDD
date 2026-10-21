using System.Reflection;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.Projections;
using PalDDD.Transactions;

namespace PalDDD.Integration.Tests;

/// <summary>
/// 契约格子清单（2026-10-21 契约盘点 R2 后续项 P1）。
/// <para>
/// 61 格 = 组件 × 栈 × 方言。每格登记：实现类型、代表测试文件、覆盖状态。
/// 本测试做三件事：① 每格的实现类型在引用程序集中存在并实现对应抽象；
/// ② 新增实现（未登记的根实现）立即失败；③ Local/CiOnly 格必须有含
/// [Test] 与 Assert.That 的测试文件，AcceptedGap 必须带原因且集合冻结。
/// 覆盖状态三态：Local（本地可跑）、CiOnly（需 Testcontainers，CI dialect-probe 跑）、
/// AcceptedGap（显式接受缺口，原因必填）。
/// </para>
/// </summary>
public sealed class ContractCellManifestTests
{
    private enum CellStatus { Local, CiOnly, AcceptedGap }

    private sealed record Cell(
        string Component,
        string Stack,
        string Dialect,
        string ImplementationType,
        string TestFile,
        CellStatus Status,
        string? Reason = null);

    private const string TransactionsTests = "test/PalDDD.Transactions.Tests/";
    private const string IntegrationTests = "test/PalDDD.Integration.Tests/";
    private const string PalOrmTests = "test/PalDDD.PalORM.Tests/";
    private const string EventLogTests = "test/PalDDD.EventLog.Tests/";

    private static readonly Cell[] Cells =
    [
        // ── Outbox（11 格）──
        new("Outbox", "InMemory", "-", "PalDDD.Transactions.InMemoryOutboxStore", TransactionsTests + "InMemoryStoreTests.cs", CellStatus.Local),
        new("Outbox", "Dapper", "SQLite", "PalDDD.Dapper.DapperOutboxStore", IntegrationTests + "CrossStackContractMatrixTests.cs", CellStatus.Local),
        new("Outbox", "Dapper", "PostgreSQL", "PalDDD.Dapper.DapperOutboxStore", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("Outbox", "Dapper", "MySQL", "PalDDD.Dapper.DapperOutboxStore", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("Outbox", "PalORM", "SQLite", "PalDDD.PalORM.Stores.PalOrmOutboxStore`1", PalOrmTests + "PalOrmOutboxStoreTests.cs", CellStatus.Local),
        new("Outbox", "PalORM", "PostgreSQL", "PalDDD.PalORM.Stores.PalOrmOutboxStore`1", PalOrmTests + "PalOrmOutboxMultiDialectTests.cs", CellStatus.CiOnly),
        new("Outbox", "PalORM", "MySQL", "PalDDD.PalORM.Stores.PalOrmOutboxStore`1", PalOrmTests + "PalOrmOutboxMultiDialectTests.cs", CellStatus.CiOnly),
        new("Outbox", "EFCore", "SQLite", "PalDDD.Transactions.SqliteOutboxDbContext", IntegrationTests + "OutboxEfCoreTests.cs", CellStatus.Local),
        new("Outbox", "EFCore", "PostgreSQL", "PalDDD.Transactions.PostgreSqlOutboxDbContext", IntegrationTests + "TechDebtGuardTests.cs", CellStatus.AcceptedGap, "TechDebtGuardTests 白名单：无关系型用例（2026-09-22 起登记）"),
        new("Outbox", "EFCore", "MySQL", "PalDDD.Transactions.MySqlOutboxDbContext", IntegrationTests + "TechDebtGuardTests.cs", CellStatus.AcceptedGap, "TechDebtGuardTests 白名单：无关系型用例（2026-09-22 起登记）"),
        new("Outbox", "EFCore", "SQLServer", "PalDDD.Transactions.SqlServerOutboxDbContext", IntegrationTests + "TechDebtGuardTests.cs", CellStatus.AcceptedGap, "Obsolete 实验方言，零测试且无 SQL Server 容器"),

        // ── Inbox（10 格）──
        new("Inbox", "InMemory", "-", "PalDDD.Transactions.InMemoryInboxStore", TransactionsTests + "InMemoryStoreTests.cs", CellStatus.Local),
        new("Inbox", "Dapper", "SQLite", "PalDDD.Dapper.DapperInboxStore", IntegrationTests + "DapperStoreTests.cs", CellStatus.Local),
        new("Inbox", "Dapper", "PostgreSQL", "PalDDD.Dapper.DapperInboxStore", IntegrationTests + "InboxTimestampTokenPrecisionProbeTests.cs", CellStatus.CiOnly),
        new("Inbox", "Dapper", "MySQL", "PalDDD.Dapper.DapperInboxStore", IntegrationTests + "InboxTimestampTokenPrecisionProbeTests.cs", CellStatus.CiOnly),
        new("Inbox", "PalORM", "SQLite", "PalDDD.PalORM.Stores.PalOrmInboxStore`1", PalOrmTests + "PalOrmInboxStoreTests.cs", CellStatus.Local),
        new("Inbox", "PalORM", "PostgreSQL", "PalDDD.PalORM.Stores.PalOrmInboxStore`1", PalOrmTests + "PalOrmInboxMultiDialectTests.cs", CellStatus.CiOnly),
        new("Inbox", "PalORM", "MySQL", "PalDDD.PalORM.Stores.PalOrmInboxStore`1", PalOrmTests + "PalOrmInboxMultiDialectTests.cs", CellStatus.CiOnly),
        new("Inbox", "EFCore", "SQLite", "PalDDD.Transactions.InboxDbContext", IntegrationTests + "InboxEfCoreTests.cs", CellStatus.Local),
        new("Inbox", "EFCore", "PostgreSQL", "PalDDD.Transactions.InboxDbContext", IntegrationTests + "DialectProbeTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例；PG/MySQL 分支无逐上下文测试"),
        new("Inbox", "EFCore", "MySQL", "PalDDD.Transactions.InboxDbContext", IntegrationTests + "DialectProbeTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例；PG/MySQL 分支无逐上下文测试"),

        // ── Idempotency（10 格）──
        new("Idempotency", "InMemory", "-", "PalDDD.Idempotency.InMemoryIdempotencyStore", IntegrationTests + "IdempotencyTests.cs", CellStatus.Local),
        new("Idempotency", "Dapper", "SQLite", "PalDDD.Dapper.DapperIdempotencyStore", IntegrationTests + "DapperIdempotencyStoreTests.cs", CellStatus.Local),
        new("Idempotency", "Dapper", "PostgreSQL", "PalDDD.Dapper.DapperIdempotencyStore", IntegrationTests + "CrossStackIdempotencyMatrixTests.cs", CellStatus.AcceptedGap, "PG/MySQL 幂等路径无 Testcontainers 用例"),
        new("Idempotency", "Dapper", "MySQL", "PalDDD.Dapper.DapperIdempotencyStore", IntegrationTests + "CrossStackIdempotencyMatrixTests.cs", CellStatus.AcceptedGap, "PG/MySQL 幂等路径无 Testcontainers 用例"),
        new("Idempotency", "PalORM", "SQLite", "PalDDD.PalORM.Stores.PalOrmIdempotencyStore`1", PalOrmTests + "PalOrmIdempotencyStoreTests.cs", CellStatus.Local),
        new("Idempotency", "PalORM", "PostgreSQL", "PalDDD.PalORM.Stores.PalOrmIdempotencyStore`1", PalOrmTests + "PalOrmIdempotencyMultiDialectTests.cs", CellStatus.CiOnly),
        new("Idempotency", "PalORM", "MySQL", "PalDDD.PalORM.Stores.PalOrmIdempotencyStore`1", PalOrmTests + "PalOrmIdempotencyMultiDialectTests.cs", CellStatus.CiOnly),
        new("Idempotency", "EFCore", "SQLite", "PalDDD.Idempotency.IdempotencyDbContext", IntegrationTests + "IdempotencyEfCoreTests.cs", CellStatus.Local),
        new("Idempotency", "EFCore", "PostgreSQL", "PalDDD.Idempotency.IdempotencyDbContext", IntegrationTests + "IdempotencyEfCoreTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),
        new("Idempotency", "EFCore", "MySQL", "PalDDD.Idempotency.IdempotencyDbContext", IntegrationTests + "IdempotencyEfCoreTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),

        // ── Saga（10 格）──
        new("Saga", "InMemory", "-", "PalDDD.Transactions.InMemorySagaStateStore`1", TransactionsTests + "InMemoryStoreTests.cs", CellStatus.Local),
        new("Saga", "Dapper", "SQLite", "PalDDD.Dapper.DapperSagaStateStore`1", IntegrationTests + "DapperStoreTests.cs", CellStatus.Local),
        new("Saga", "Dapper", "PostgreSQL", "PalDDD.Dapper.DapperSagaStateStore`1", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("Saga", "Dapper", "MySQL", "PalDDD.Dapper.DapperSagaStateStore`1", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("Saga", "PalORM", "SQLite", "PalDDD.PalORM.Stores.PalOrmSagaStateStore`2", PalOrmTests + "PalOrmSagaStateStoreTests.cs", CellStatus.Local),
        new("Saga", "PalORM", "PostgreSQL", "PalDDD.PalORM.Stores.PalOrmSagaStateStore`2", PalOrmTests + "PalOrmSagaMultiDialectTests.cs", CellStatus.CiOnly),
        new("Saga", "PalORM", "MySQL", "PalDDD.PalORM.Stores.PalOrmSagaStateStore`2", PalOrmTests + "PalOrmSagaMultiDialectTests.cs", CellStatus.CiOnly),
        new("Saga", "EFCore", "SQLite", "PalDDD.Transactions.SagaStateDbContext`1", IntegrationTests + "SagaEfCoreConcurrencyTests.cs", CellStatus.Local),
        new("Saga", "EFCore", "PostgreSQL", "PalDDD.Transactions.SagaStateDbContext`1", IntegrationTests + "SagaStateEfCoreLeasePushdownTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),
        new("Saga", "EFCore", "MySQL", "PalDDD.Transactions.SagaStateDbContext`1", IntegrationTests + "SagaStateEfCoreLeasePushdownTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),

        // ── Projections（10 格）──
        new("Projections", "InMemory", "-", "PalDDD.Projections.InMemoryProjectionCheckpointStore", IntegrationTests + "ProjectionTests.cs", CellStatus.Local),
        new("Projections", "Dapper", "SQLite", "PalDDD.Dapper.DapperProjectionCheckpointStore", IntegrationTests + "ProjectionResumeTests.cs", CellStatus.Local),
        new("Projections", "Dapper", "PostgreSQL", "PalDDD.Dapper.DapperProjectionCheckpointStore", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("Projections", "Dapper", "MySQL", "PalDDD.Dapper.DapperProjectionCheckpointStore", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("Projections", "PalORM", "SQLite", "PalDDD.PalORM.Stores.PalOrmProjectionCheckpointStore`1", PalOrmTests + "PalOrmProjectionCheckpointStoreTests.cs", CellStatus.Local),
        new("Projections", "PalORM", "PostgreSQL", "PalDDD.PalORM.Stores.PalOrmProjectionCheckpointStore`1", PalOrmTests + "PalOrmProjectionMultiDialectTests.cs", CellStatus.CiOnly),
        new("Projections", "PalORM", "MySQL", "PalDDD.PalORM.Stores.PalOrmProjectionCheckpointStore`1", PalOrmTests + "PalOrmProjectionMultiDialectTests.cs", CellStatus.CiOnly),
        new("Projections", "EFCore", "SQLite", "PalDDD.Projections.ProjectionCheckpointDbContext", IntegrationTests + "ProjectionCheckpointEfCoreTests.cs", CellStatus.Local),
        new("Projections", "EFCore", "PostgreSQL", "PalDDD.Projections.ProjectionCheckpointDbContext", IntegrationTests + "ProjectionCheckpointEfCoreTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),
        new("Projections", "EFCore", "MySQL", "PalDDD.Projections.ProjectionCheckpointDbContext", IntegrationTests + "ProjectionCheckpointEfCoreTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),

        // ── EventLog（10 格）──
        new("EventLog", "InMemory", "-", "PalDDD.EventLog.InMemoryEventLog", EventLogTests + "EventLogTests.cs", CellStatus.Local),
        new("EventLog", "Dapper", "SQLite", "PalDDD.Dapper.DapperEventLog", IntegrationTests + "DapperStoreTests.cs", CellStatus.Local),
        new("EventLog", "Dapper", "PostgreSQL", "PalDDD.Dapper.DapperEventLog", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("EventLog", "Dapper", "MySQL", "PalDDD.Dapper.DapperEventLog", IntegrationTests + "DialectProbeTests.cs", CellStatus.CiOnly),
        new("EventLog", "PalORM", "SQLite", "PalDDD.PalORM.Stores.PalOrmEventLog`1", PalOrmTests + "PalOrmEventLogMultiDialectTests.cs", CellStatus.Local),
        new("EventLog", "PalORM", "PostgreSQL", "PalDDD.PalORM.Stores.PalOrmEventLog`1", PalOrmTests + "PalOrmEventLogMultiDialectTests.cs", CellStatus.CiOnly),
        new("EventLog", "PalORM", "MySQL", "PalDDD.PalORM.Stores.PalOrmEventLog`1", PalOrmTests + "PalOrmEventLogMultiDialectTests.cs", CellStatus.CiOnly),
        new("EventLog", "EFCore", "SQLite", "PalDDD.EventLog.EventLogDbContext", EventLogTests + "EventLogEfCoreTests.cs", CellStatus.Local),
        new("EventLog", "EFCore", "PostgreSQL", "PalDDD.EventLog.EventLogDbContext", EventLogTests + "EventLogEfCoreTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),
        new("EventLog", "EFCore", "MySQL", "PalDDD.EventLog.EventLogDbContext", EventLogTests + "EventLogEfCoreTests.cs", CellStatus.AcceptedGap, "EF 上下文仅 SQLite 关系型用例"),
    ];

    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PalDDD.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"自 {AppContext.BaseDirectory} 向上未找到 PalDDD.slnx。");
    }

    private static readonly Assembly[] AnchorAssemblies =
    [
        typeof(InMemoryOutboxStore).Assembly,
        typeof(InMemoryEventLog).Assembly,
        typeof(InMemoryIdempotencyStore).Assembly,
        typeof(InMemoryProjectionCheckpointStore).Assembly,
        typeof(DapperOutboxStore).Assembly,
        typeof(PalDDD.PalORM.Stores.PalOrmOutboxStore<>).Assembly,
        typeof(OutboxDbContext).Assembly,
        typeof(EventLogDbContext).Assembly,
        typeof(IdempotencyDbContext).Assembly,
        typeof(ProjectionCheckpointDbContext).Assembly,
    ];

    [Test]
    public async Task Manifest_CoversAllCells_AndEveryRowIsWired()
    {
        await Assert.That(Cells.Length).IsEqualTo(61);

        // 多个程序集存在同全名类型（如 ServiceCollectionExtensions），用集合而非字典。
        var types = AnchorAssemblies
            .SelectMany(a => a.GetTypes())
            .GroupBy(t => t.FullName ?? t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var failures = new List<string>();

        foreach (var cell in Cells)
        {
            if (!types.TryGetValue(cell.ImplementationType, out var type))
            {
                failures.Add($"格子 {cell.Component}/{cell.Stack}/{cell.Dialect} 的实现类型不存在：{cell.ImplementationType}");
                continue;
            }

            if (!ImplementsComponent(type, cell.Component))
                failures.Add($"格子 {cell.Component}/{cell.Stack}/{cell.Dialect} 的类型未实现该组件抽象：{type.FullName}");

            if (cell.Status == CellStatus.AcceptedGap)
            {
                if (string.IsNullOrWhiteSpace(cell.Reason))
                    failures.Add($"AcceptedGap 缺原因：{cell.Component}/{cell.Stack}/{cell.Dialect}");
                continue;
            }

            var testPath = Path.Combine(Root, cell.TestFile);
            if (!File.Exists(testPath))
            {
                failures.Add($"格子 {cell.Component}/{cell.Stack}/{cell.Dialect} 的测试文件不存在：{cell.TestFile}");
                continue;
            }

            var text = File.ReadAllText(testPath);
            if (!text.Contains("[Test]", StringComparison.Ordinal) || !text.Contains("Assert.That", StringComparison.Ordinal))
                failures.Add($"格子 {cell.Component}/{cell.Stack}/{cell.Dialect} 的测试文件缺少 [Test] 或 Assert.That：{cell.TestFile}");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>新增根实现未登记即红：排除派生的方言包装（Sqlite*/MySql*/PostgreSql*）与 EF 方言子类。</summary>
    [Test]
    public async Task Manifest_DetectsNewUnregisteredImplementations()
    {
        var manifestTypes = Cells.Select(c => c.ImplementationType).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var assembly in AnchorAssemblies.Distinct())
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsInterface || type.Name.StartsWith('<'))
                    continue;
                // OutboxDbContext 是四方言上下文的公共基类（抽象基础设施，不是格子）。
                if (type == typeof(OutboxDbContext)) continue;
                if (!ImplementsAnyComponent(type)) continue;

                var name = type.Name;
                if (name.StartsWith("Sqlite", StringComparison.Ordinal)
                    || name.StartsWith("MySql", StringComparison.Ordinal)
                    || name.StartsWith("PostgreSql", StringComparison.Ordinal)
                    || name.EndsWith("OutboxDbContext", StringComparison.Ordinal) && name != "OutboxDbContext")
                    continue;

                var fullName = type.FullName ?? type.Name;
                if (!manifestTypes.Contains(fullName))
                    failures.Add($"新实现未登记：{fullName}（{string.Join(",", type.GetInterfaces().Where(IsComponentInterface).Select(i => i.Name))}）");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Manifest_PrintsTriStateSummary()
    {
        var local = Cells.Count(c => c.Status == CellStatus.Local);
        var ciOnly = Cells.Count(c => c.Status == CellStatus.CiOnly);
        var gaps = Cells.Count(c => c.Status == CellStatus.AcceptedGap);
        Console.WriteLine($"契约格子：总计 {Cells.Length}｜verified-local {local}｜verified-ci-only {ciOnly}｜accepted-gap {gaps}");

        await Assert.That(local + ciOnly + gaps).IsEqualTo(Cells.Length);
        await Assert.That(gaps).IsEqualTo(15);
    }

    private static bool ImplementsComponent(Type type, string component)
    {
        return component switch
        {
            "Outbox" => typeof(IPalOutboxStore).IsAssignableFrom(type) || type.GetInterfaces().Contains(typeof(IPalOutboxStore)),
            "Inbox" => typeof(IInboxStore).IsAssignableFrom(type) || type.GetInterfaces().Contains(typeof(IInboxStore)),
            "Idempotency" => typeof(IIdempotencyStore).IsAssignableFrom(type) || type.GetInterfaces().Contains(typeof(IIdempotencyStore)),
            "Saga" => ImplementsOpenInterface(type, typeof(ISagaStateStore<>)),
            "Projections" => typeof(IProjectionCheckpointStore).IsAssignableFrom(type) || type.GetInterfaces().Contains(typeof(IProjectionCheckpointStore)),
            "EventLog" => typeof(IEventLog).IsAssignableFrom(type) || type.GetInterfaces().Contains(typeof(IEventLog)),
            _ => false,
        };
    }

    private static bool ImplementsAnyComponent(Type type)
    {
        return typeof(IPalOutboxStore).IsAssignableFrom(type)
            || typeof(IInboxStore).IsAssignableFrom(type)
            || typeof(IIdempotencyStore).IsAssignableFrom(type)
            || ImplementsOpenInterface(type, typeof(ISagaStateStore<>))
            || typeof(IProjectionCheckpointStore).IsAssignableFrom(type)
            || typeof(IEventLog).IsAssignableFrom(type);
    }

    private static bool ImplementsOpenInterface(Type type, Type openGeneric)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric) return true;
        foreach (var i in type.GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric) return true;
        return false;
    }

    private static bool IsComponentInterface(Type i)
        => i == typeof(IPalOutboxStore) || i == typeof(IInboxStore) || i == typeof(IIdempotencyStore)
        || i == typeof(IProjectionCheckpointStore) || i == typeof(IEventLog)
        || i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISagaStateStore<>);
}
