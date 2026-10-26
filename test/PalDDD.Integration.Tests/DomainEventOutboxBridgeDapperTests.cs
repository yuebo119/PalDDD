using Microsoft.Data.Sqlite;
using PalDDD.Core;
using PalDDD.Dapper;
using PalDDD.Serialization;
using PalDDD.Transactions;
using System.Text.Json.Serialization;

namespace PalDDD.Integration.Tests;

/// <summary>
/// T9（基础库优化清单）：栈无关域事件桥在真实 Dapper SQLite 上的接法。
/// 核心断言是事务语义：同一 UoW 内写入随提交可见，回滚后 outbox 行不存在。
/// </summary>
// R11 F26：与 dapper-global 组串行（Dapper 全局类型映射为进程级状态）。
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class DomainEventOutboxBridgeDapperTests
{
    private const string OutboxDdl =
        "CREATE TABLE outbox_messages (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload BLOB NOT NULL, " +
        "content_type TEXT NOT NULL DEFAULT 'application/json', schema_version INTEGER NOT NULL DEFAULT 1, " +
        "status INTEGER NOT NULL DEFAULT 0, retry_count INTEGER NOT NULL DEFAULT 0, error TEXT, " +
        "created_at TEXT NOT NULL, processed_at TEXT, next_attempt_at TEXT, locked_by TEXT, locked_until TEXT, " +
        "correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)";

    private static readonly MessageDescriptor Descriptor = MessageDescriptor.Create(
        DapperBridgeJsonContext.Default.DapperBridgeEvent, "test.bridge.dapper.v1", schemaVersion: 1);

    [Test]
    public async Task PersistAsync_InsideUnitOfWork_CommitsOutboxRowAndClearsEvents(CancellationToken ct)
    {
        await using var connection = await CreateConnectionAsync(ct);
        var store = new DapperOutboxStore(connection, DapperDbType.Sqlite);
        await using var unitOfWork = new DapperUnitOfWork(connection);
        var bridge = CreateBridge();
        var aggregate = new DapperBridgeEntity();
        aggregate.AddEvent(new DapperBridgeEvent());

        await unitOfWork.BeginTransactionAsync(ct);
        var written = await bridge.PersistAsync([aggregate], store, ct);
        await unitOfWork.CommitAsync(ct);

        await Assert.That(written).IsEqualTo(1);
        await Assert.That(aggregate.HasDomainEvents).IsFalse();
        var pending = await store.GetPendingMessagesAsync(10, 10, ct);
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Type).IsEqualTo(Descriptor.Name);
    }

    [Test]
    public async Task PersistAsync_InsideRolledBackUnitOfWork_LeavesNoOutboxRow(CancellationToken ct)
    {
        await using var connection = await CreateConnectionAsync(ct);
        var store = new DapperOutboxStore(connection, DapperDbType.Sqlite);
        await using var unitOfWork = new DapperUnitOfWork(connection);
        var bridge = CreateBridge();
        var aggregate = new DapperBridgeEntity();
        aggregate.AddEvent(new DapperBridgeEvent());

        await unitOfWork.BeginTransactionAsync(ct);
        await bridge.PersistAsync([aggregate], store, ct);
        await unitOfWork.RollbackAsync(ct);

        var pending = await store.GetPendingMessagesAsync(10, 10, ct);
        await Assert.That(pending).Count().IsEqualTo(0);
    }

    private static DomainEventOutboxBridge CreateBridge()
        => new(new DapperBridgeCatalog(), new DapperBridgeSerializer());

    private static async Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = OutboxDdl;
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private sealed class DapperBridgeCatalog : IMessageCatalog
    {
        public IReadOnlyList<MessageDescriptor> Descriptors => [Descriptor];

        public MessageDescriptor? Find(string name) => name == Descriptor.Name ? Descriptor : null;

        public MessageDescriptor? Find(string name, int schemaVersion)
            => name == Descriptor.Name && schemaVersion == Descriptor.SchemaVersion ? Descriptor : null;

        public MessageDescriptor? Find(Type type) => type == typeof(DapperBridgeEvent) ? Descriptor : null;
    }

    private sealed class DapperBridgeSerializer : IMessageSerializer
    {
        public string ContentType => "application/json";

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null)
            => "payload"u8.ToArray();

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
            => "payload"u8.ToArray();

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => null;

        public T? Deserialize<T>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }
}

internal sealed class DapperBridgeEntity : Entity
{
    public void AddEvent(DomainEvent domainEvent) => RaiseEvent(domainEvent);
}

internal sealed class DapperBridgeEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "test.bridge.dapper.v1";
}

[JsonSerializable(typeof(DapperBridgeEvent))]
internal sealed partial class DapperBridgeJsonContext : JsonSerializerContext;
