namespace PalDDD.Transactions.Tests;

/// <summary>
/// 验证实体时间戳默认值不依赖可变静态状态（static TimeProvider Clock 反模式已移除）。
/// 构造实体时 CreatedAt/ReceivedAt 应反映系统当前时间，而非被全局可变 Clock 污染。
/// </summary>
public sealed class TimestampDefaultsTests
{
    [Test]
    public async Task OutboxMessage_CreatedAt_ReflectsSystemTime()
    {
        var before = DateTimeOffset.UtcNow;
        var message = new OutboxMessage();
        var after = DateTimeOffset.UtcNow;

        await Assert.That(message.CreatedAt >= before).IsTrue();
        await Assert.That(message.CreatedAt <= after).IsTrue();
    }

    [Test]
    public async Task InboxMessage_ReceivedAt_DefaultIsUnset()
    {
        // InboxMessage 的 ReceivedAt 由 store 在插入时显式赋值（InboxDbContext.TryStartProcessingAsync），
        // 实体默认值不再调用静态 Clock —— 默认应为 default(DateTimeOffset)。
        var message = new InboxMessage();
        await Assert.That(message.ReceivedAt).IsEqualTo(default(DateTimeOffset));
    }

    [Test]
    public async Task SagaState_CreatedAt_ReflectsSystemTime()
    {
        var before = DateTimeOffset.UtcNow;
        var state = new TestSagaState();
        var after = DateTimeOffset.UtcNow;

        await Assert.That(state.CreatedAt >= before).IsTrue();
        await Assert.That(state.CreatedAt <= after).IsTrue();
    }

    [Test]
    public async Task NoMutableStaticClock_OnEntities()
    {
        // 反模式 static TimeProvider Clock 已移除：三个实体类型都不应再暴露 internal static Clock。
        await Assert.That(typeof(OutboxMessage).GetProperty("Clock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)).IsNull();
        await Assert.That(typeof(InboxMessage).GetProperty("Clock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)).IsNull();
        await Assert.That(typeof(SagaState).GetProperty("Clock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)).IsNull();
    }

    private sealed class TestSagaState : SagaState;
}
