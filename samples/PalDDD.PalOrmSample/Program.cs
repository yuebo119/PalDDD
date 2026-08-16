// PalDDD.PalOrmSample — PalORM Sqlite AOT 发布验证入口
// ════════════════════════════════════════════════════════════
// 实际覆盖（与 csproj 注释一致，不夸大）：
//   - Outbox CRUD（AddMessage / GetPendingMessagesAsync / MarkProcessed）
//   - SqlitePalOrmUnitOfWork commit / rollback（SQLite 实库）
// 用 dotnet publish -r win-x64 /p:PublishAot=true 验证上述路径 AOT 兼容。
// 通过：编译期 0 警告 + 运行时 PASSED 输出。

using ByteAether.Ulid;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;

// 步骤 1：建表（手工 DDL，与 PalORM.Tests/MultiDialectSchema.cs 一致：枚举列存 int、payload TEXT。
// 注意：Dapper 栈的 docs/sql DDL 用字符串枚举/BLOB——两栈 DDL 不兼容，迁移见 docs/palorm-adapter.md §6）
const string DbPath = "palddd-palorm-sample.db";
if (File.Exists(DbPath)) File.Delete(DbPath);

await using var db = await DataSession<SqliteProvider>.CreateAsync(
    DbOptions.Development($"Data Source={DbPath}"));

await db.ExecuteAsync($"CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload TEXT NOT NULL, content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)");

// 步骤 2：Outbox CRUD
var outbox = new SqliteOutboxStore(db);
var id = Ulid.New();
var msg = new OutboxMessage
{
    Id = id,
    Type = "test.event.v1",
    Payload = System.Text.Encoding.UTF8.GetBytes("""{"hello":"world"}"""),
    ContentType = "application/json",
    SchemaVersion = 1,
};
outbox.AddMessage(msg);
Console.WriteLine($"[OK] Outbox.AddMessage: Id={msg.Id}, Status={msg.Status}");

var pending = await outbox.GetPendingMessagesAsync(batchSize: 10, maxRetryCount: 5, ct: default);
if (pending.Count != 1) throw new InvalidOperationException($"Expected 1 pending, got {pending.Count}");
Console.WriteLine($"[OK] Outbox.GetPending: Count={pending.Count}, Type={pending[0].Type}");

// 步骤 3：MarkProcessed
outbox.MarkProcessed(msg, DateTimeOffset.UtcNow);
var afterProcessed = await outbox.GetPendingMessagesAsync(10, 5, default);
if (afterProcessed.Count != 0) throw new InvalidOperationException($"Expected 0 pending after MarkProcessed, got {afterProcessed.Count}");
Console.WriteLine($"[OK] Outbox.MarkProcessed: Status={msg.Status}");

// 步骤 4：事务（UnitOfWork）
var uow = new SqlitePalOrmUnitOfWork(db);
await uow.BeginTransactionAsync();
var msg2 = new OutboxMessage { Id = Ulid.New(), Type = "tx.event.v1", Payload = System.Text.Encoding.UTF8.GetBytes("[]") };
outbox.AddMessage(msg2);
await uow.CommitAsync();

var allPending = await outbox.GetPendingMessagesAsync(10, 5, default);
if (allPending.Count != 1) throw new InvalidOperationException($"Expected 1 pending after tx commit, got {allPending.Count}");
Console.WriteLine($"[OK] Transaction commit: msg2 visible");

// 步骤 5：事务回滚
await uow.BeginTransactionAsync();
var msg3 = new OutboxMessage { Id = Ulid.New(), Type = "rollback.event.v1", Payload = System.Text.Encoding.UTF8.GetBytes("[]") };
outbox.AddMessage(msg3);
await uow.RollbackAsync();

var afterRollback = await outbox.GetPendingMessagesAsync(10, 5, default);
if (afterRollback.Count != 1) throw new InvalidOperationException($"Expected 1 pending after rollback (msg3 should be rolled back), got {afterRollback.Count}");
Console.WriteLine($"[OK] Transaction rollback: msg3 not visible");

Console.WriteLine();
Console.WriteLine("=== PalDDD PalORM AOT verification PASSED ===");
