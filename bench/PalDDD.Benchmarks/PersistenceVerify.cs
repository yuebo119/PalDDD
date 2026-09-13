// ─────────────────────────────────────────────────────────────
// 基准设施正确性验证(--verify-persist)——「验证验证者」(PD29)
// ─────────────────────────────────────────────────────────────
// PersistenceBenchmarks 是三栈优化的数字裁判,裁判自身的 fixture 不变量与
// 三栈行为语义此前只有基准运行时的间接验证(守卫抛异常),无直接断言。
// 本文件以正式断言钉住:任何对基准 fixture 的改动(连接串/表名/建库方式)
// 若破坏不变量,此处红而非基准数字悄悄失真。
//
// 验证面(与 PersistenceBenchmarks.cs 头注坑位清单一一对应):
//   V1 共享内存库多连接同库(keeper 语义——Cache=Shared 命名库)
//   V2 共享 schema 建表幂等(IF NOT EXISTS 重复执行合法)
//   V3 清表对空表合法(DELETE 幂等)
//   V4 EFCore DbSet 属性名表名(Events 非 StoredEvents——三轮实证坑)
//   V5 EFCore EnsureCreated 库级跳过(有表即整体跳过——三 context 须三独立库)
//   V6 Dapper 租约满额 + blob 往返 + 终态
//   V7 PalORM 租约满额 + blob 往返 + 幂等全链
//   V8 EFCore 租约满额 + blob 往返 + 幂等全链
//   V9 重灌守卫:不足额租约抛 InvalidOperationException(防 ITM-246 静默空转)
//
// 运行:dotnet run --project bench/PalDDD.Benchmarks -c Release -- --verify-persist
// 退出码:0=全绿,1=有红。

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Benchmarks;
using PalDDD.Core;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM;
using PalDDD.PalORM.Sqlite;
using PalORM;
using PalORM.Sqlite;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

// 入口分发在 Program.cs(--verify-persist 分支)——本文件纯类型,避免顶级语句冲突(CS8802)
namespace PalDDD.Benchmarks
{
    /// <summary>基准设施正确性验证执行器(--verify-persist 模式)。</summary>
    internal static class PersistenceVerifyRunner
    {
        private const int BatchSize = PersistenceBenchBase.PublicBatchSize;
        private static readonly List<(string Name, bool Pass, string Detail)> Results = [];

        public static async Task<int> RunAsync()
        {
            Console.WriteLine("═══ 基准设施正确性验证(--verify-persist)═══");

            await V1_SharedMemorySameDatabaseAsync();
            await V2_SchemaCreateIdempotentAsync();
            await V3_ClearEmptyTablesAsync();
            await V4_EfCoreDbSetTableNamesAsync();
            await V5_EnsureCreatedLibraryLevelSkipAsync();
            await V6_DapperBehaviorAsync();
            await V7_PalOrmBehaviorAsync();
            await V8_EfCoreBehaviorAsync();
            V9_ReseedGuard();

            Console.WriteLine();
            foreach (var (name, pass, detail) in Results)
                Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
            var failed = Results.Count(r => !r.Pass);
            Console.WriteLine($"═══ {Results.Count - failed}/{Results.Count} 通过 ═══");
            return failed == 0 ? 0 : 1;
        }

        private static void Check(string name, bool pass, string detail = "")
            => Results.Add((name, pass, detail));

        // ── V1:共享内存库多连接同库 ──
        private static async Task V1_SharedMemorySameDatabaseAsync()
        {
            try
            {
                await using var keeper = PersistenceBenchBase.OpenSharedMemory("verifyv1");
                await PersistenceBenchBase.CreateSharedSchemaAsync(keeper);
                await using var second = PersistenceBenchBase.OpenSharedMemory("verifyv1");
                // 第二连接必须看到第一连接建的表(cache=shared 命名库语义)
                var tables = (await global::Dapper.SqlMapper.QueryAsync<string>(second,
                    "SELECT name FROM sqlite_master WHERE type='table' AND name='outbox_messages'")).ToList();
                Check("V1 共享内存库多连接同库", tables.Count == 1,
                    tables.Count == 1 ? "" : "第二连接看不到 keeper 建的表");
            }
            catch (Exception ex)
            {
                Check("V1 共享内存库多连接同库", false, ex.Message);
            }
        }

        // ── V2:建表幂等 ──
        private static async Task V2_SchemaCreateIdempotentAsync()
        {
            try
            {
                await using var conn = PersistenceBenchBase.OpenSharedMemory("verifyv2");
                await PersistenceBenchBase.CreateSharedSchemaAsync(conn);
                await PersistenceBenchBase.CreateSharedSchemaAsync(conn); // 第二次不得抛
                Check("V2 共享 schema 建表幂等(IF NOT EXISTS)", true);
            }
            catch (Exception ex)
            {
                Check("V2 共享 schema 建表幂等(IF NOT EXISTS)", false, ex.Message);
            }
        }

        // ── V3:清表对空表合法 ──
        private static async Task V3_ClearEmptyTablesAsync()
        {
            try
            {
                await using var conn = PersistenceBenchBase.OpenSharedMemory("verifyv3");
                await PersistenceBenchBase.CreateSharedSchemaAsync(conn);
                await PersistenceBenchBase.ClearSharedSchemaAsync(conn);
                await PersistenceBenchBase.ClearSharedSchemaAsync(conn); // 空表 DELETE 幂等
                Check("V3 清表对空表合法(DELETE 幂等)", true);
            }
            catch (Exception ex)
            {
                Check("V3 清表对空表合法(DELETE 幂等)", false, ex.Message);
            }
        }

        // ── V4:EFCore DbSet 属性名表名 ──
        private static async Task V4_EfCoreDbSetTableNamesAsync()
        {
            try
            {
                await using var conn = PersistenceBenchBase.OpenSharedMemory("verifyv4");
                var options = new DbContextOptionsBuilder<BenchEventLogDbContext>().UseSqlite(conn).Options;
                await using (var db = new BenchEventLogDbContext(options))
                    await db.Database.EnsureCreatedAsync();
                var tables = (await global::Dapper.SqlMapper.QueryAsync<string>(conn,
                    "SELECT name FROM sqlite_master WHERE type='table'")).ToList();
                // DbSet 属性名(Events/GlobalPositionAllocators)而非 CLR 类名(StoredEvents/...)
                Check("V4 EFCore 表名=DbSet 属性名(Events 非 StoredEvents)",
                    tables.Contains("Events") && tables.Contains("GlobalPositionAllocators") && !tables.Contains("StoredEvents"),
                    string.Join(",", tables));
            }
            catch (Exception ex)
            {
                Check("V4 EFCore 表名=DbSet 属性名(Events 非 StoredEvents)", false, ex.Message);
            }
        }

        // ── V5:EnsureCreated 库级跳过 ──
        private static async Task V5_EnsureCreatedLibraryLevelSkipAsync()
        {
            try
            {
                await using var conn = PersistenceBenchBase.OpenSharedMemory("verifyv5");
                // 第一个 context 建表后,第二个不同模型的 context 对同一库 EnsureCreated 必须返回 false
                var outboxOptions = new DbContextOptionsBuilder<BenchOutboxDbContext>().UseSqlite(conn).Options;
                var eventLogOptions = new DbContextOptionsBuilder<BenchEventLogDbContext>().UseSqlite(conn).Options;
                bool firstCreated, secondCreated;
                await using (var db = new BenchOutboxDbContext(outboxOptions))
                    firstCreated = await db.Database.EnsureCreatedAsync();
                await using (var db = new BenchEventLogDbContext(eventLogOptions))
                    secondCreated = await db.Database.EnsureCreatedAsync();
                Check("V5 EnsureCreated 库级跳过(第二 context 对同库返回 false)",
                    firstCreated && !secondCreated,
                    $"first={firstCreated}, second={secondCreated}(true=库级跳过语义失效,基准须三独立库的前提被破坏)");
            }
            catch (Exception ex)
            {
                Check("V5 EnsureCreated 库级跳过(第二 context 对同库返回 false)", false, ex.Message);
            }
        }

        // ── V6:Dapper 行为语义 ──
        private static async Task V6_DapperBehaviorAsync()
        {
            try
            {
                await using var conn = PersistenceBenchBase.OpenSharedMemory("verifyv6");
                await PersistenceBenchBase.CreateSharedSchemaAsync(conn);
                var outbox = new DapperOutboxStore(conn, DapperDbType.Sqlite);
                var payload = "verify-payload"u8.ToArray();
                var msg = NewMessage(payload);
                outbox.AddMessage(msg);

                var leased = await outbox.LeasePendingMessagesAsync(10, "verify",
                    TimeSpan.FromMinutes(2), 10, default);
                bool leaseOk = leased.Count == 1 && leased[0].Payload.SequenceEqual(payload);

                outbox.MarkProcessed(leased[0], TimeProvider.System.GetUtcNow());
                var pending = await outbox.GetPendingMessagesAsync(10, 10, default);
                Check("V6 Dapper 租约满额+blob 往返+终态", leaseOk && pending.Count == 0,
                    $"lease={leaseOk}, pendingAfterTerminal={pending.Count}");
            }
            catch (Exception ex)
            {
                Check("V6 Dapper 租约满额+blob 往返+终态", false, ex.Message);
            }
        }

        // ── V7:PalORM 行为语义 ──
        private static async Task V7_PalOrmBehaviorAsync()
        {
            try
            {
                var keeper = PersistenceBenchBase.OpenSharedMemory("verifyv7");
                try
                {
                    await PersistenceBenchBase.CreateSharedSchemaAsync(keeper);
                    var opts = DbOptions.Development("Data Source=verifyv7;Mode=Memory;Cache=Shared");
                    await using var session = await DataSession<SqliteProvider>.CreateAsync(opts, default);
                    var outbox = new SqliteOutboxStore(session);
                    var payload = "verify-payload"u8.ToArray();
                    await outbox.AddMessagesAsync([NewMessage(payload)]);

                    var leased = await outbox.LeasePendingMessagesAsync(10, "verify",
                        TimeSpan.FromMinutes(2), 10, default);
                    bool leaseOk = leased.Count == 1 && leased[0].Payload.SequenceEqual(payload);

                    outbox.MarkProcessed(leased[0], TimeProvider.System.GetUtcNow());
                    var pending = await outbox.GetPendingMessagesAsync(10, 10, default);

                    var idem = new SqliteIdempotencyStore(session);
                    var now = DateTimeOffset.UtcNow;
                    var record = await idem.TryStartAsync("verify-op", PalUlid.New().ToString(), now, IdempotencyPolicy.Default, default);
                    await idem.MarkCompletedAsync(record!, "resp"u8.ToArray(), now.AddSeconds(1));
                    var loaded = await idem.GetAsync("verify-op", record!.Key, now.AddSeconds(1), default);
                    bool idemOk = loaded?.Status == IdempotencyRecordStatus.Completed
                        && loaded.ResponsePayload is not null
                        && loaded.ResponsePayload.Value.Span.SequenceEqual("resp"u8.ToArray());

                    Check("V7 PalORM 租约+blob 往返+终态+幂等全链", leaseOk && pending.Count == 0 && idemOk,
                        $"lease={leaseOk}, pending={pending.Count}, idem={idemOk}");
                }
                finally
                {
                    keeper.Dispose();
                }
            }
            catch (Exception ex)
            {
                Check("V7 PalORM 租约+blob 往返+终态+幂等全链", false, ex.Message);
            }
        }

        // ── V8:EFCore 行为语义 ──
        private static async Task V8_EfCoreBehaviorAsync()
        {
            try
            {
                await using var conn = PersistenceBenchBase.OpenSharedMemory("verifyv8");
                var outboxOptions = new DbContextOptionsBuilder<BenchOutboxDbContext>().UseSqlite(conn).Options;
                var idempotencyOptions = new DbContextOptionsBuilder<BenchIdempotencyDbContext>().UseSqlite(conn).Options;
                await using (var db = new BenchOutboxDbContext(outboxOptions))
                    await db.Database.EnsureCreatedAsync();
                // V5 已证同库第二 context 跳建——Idempotency 表须独立库
                await using var idemConn = PersistenceBenchBase.OpenSharedMemory("verifyv8idem");
                await using (var db = new BenchIdempotencyDbContext(new DbContextOptionsBuilder<BenchIdempotencyDbContext>().UseSqlite(idemConn).Options))
                    await db.Database.EnsureCreatedAsync();

                await using (var outbox = new BenchOutboxDbContext(outboxOptions))
                {
                    outbox.OutboxMessages.Add(NewMessage(payload: "verify-payload"u8.ToArray()));
                    await outbox.SaveChangesAsync();

                    var leased = await outbox.LeasePendingMessagesAsync(10, "verify",
                        TimeSpan.FromMinutes(2), 10, default);
                    bool leaseOk = leased.Count == 1 && leased[0].Payload.SequenceEqual("verify-payload"u8.ToArray());
                    outbox.MarkProcessed(leased[0], TimeProvider.System.GetUtcNow());
                    await outbox.SaveChangesAsync();
                    var pending = await outbox.GetPendingMessagesAsync(10, 10, default);

                    await using var idem = new BenchIdempotencyDbContext(new DbContextOptionsBuilder<BenchIdempotencyDbContext>().UseSqlite(idemConn).Options);
                    var now = DateTimeOffset.UtcNow;
                    var record = await idem.TryStartAsync("verify-op", PalUlid.New().ToString(), now, IdempotencyPolicy.Default, default);
                    await idem.MarkCompletedAsync(record!, "resp"u8.ToArray(), now.AddSeconds(1));
                    var loaded = await idem.GetAsync("verify-op", record!.Key, now.AddSeconds(1), default);
                    bool idemOk = loaded?.Status == IdempotencyRecordStatus.Completed;

                    Check("V8 EFCore 租约+blob 往返+终态+幂等(独立库)", leaseOk && pending.Count == 0 && idemOk,
                        $"lease={leaseOk}, pending={pending.Count}, idem={idemOk}");
                }
            }
            catch (Exception ex)
            {
                Check("V8 EFCore 租约+blob 往返+终态+幂等(独立库)", false, ex.Message);
            }
        }

        // ── V9:重灌守卫(不足额抛)──
        private static void V9_ReseedGuard()
        {
            try
            {
                // 少于 BatchSize 的租约结果必须抛(基准守卫语义)
                var shortList = new List<OutboxMessage>();
                for (int i = 0; i < PersistenceBenchBase.PublicBatchSize - 1; i++)
                    shortList.Add(NewMessage());
                try
                {
                    PersistenceBenchBase.EnsureFullLeasePublic(shortList);
                    Check("V9 重灌守卫不足额抛", false, "守卫未抛——静默空转回归(ITM-246 失效模式)");
                }
                catch (InvalidOperationException)
                {
                    Check("V9 重灌守卫不足额抛", true);
                }
            }
            catch (Exception ex)
            {
                Check("V9 重灌守卫不足额抛", false, ex.Message);
            }
        }

        private static OutboxMessage NewMessage(byte[]? payload = null)
            => new()
            {
                Type = "verify.event.v1",
                Payload = payload ?? "{}"u8.ToArray(),
                ContentType = "application/json",
                Status = OutboxStatus.Pending,
                CreatedAt = TimeProvider.System.GetUtcNow(),
            };
    }
}
