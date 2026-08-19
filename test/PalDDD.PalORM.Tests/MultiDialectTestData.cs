using ByteAether.Ulid;
using PalDDD.EventLog;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// 跨方言测试数据工厂 —— 共享的测试夹具构造方法。
/// </summary>
internal static class MultiDialectTestData
{
    internal static OutboxMessage CreateOutboxMessage(string type = "test.event") => new()
    {
        Id = Ulid.New(),
        Type = type,
        Payload = System.Text.Encoding.UTF8.GetBytes("""{"v":1}"""),
        ContentType = "application/json",
        SchemaVersion = 1,
    };

    internal static EventData CreateEventData(string name = "test.event.v1") => new(
        eventId: Ulid.New(),
        eventName: name,
        schemaVersion: 1,
        contentType: "application/json",
        payload: new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("""{"v":1}""")),
        metadata: new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("{}")),
        audit: EventAuditMetadata.Empty);
}
