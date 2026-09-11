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
        // 反模式 static TimeProvider Clock 已移除：三个实体类型都不得再持有可变全局静态时钟。
        // P3 收宽扫描口径：字段腿扫**全部** static TimeProvider 字段（任意命名——裸静态字段
        // 与 settable 静态属性的编译器后备字段 <X>k__BackingField 均在此现形，原按名 "Clock"
        // 扫描对改名回退形态失明）；属性腿仅按历史名 Clock 扫——不扫全部 TimeProvider 属性，
        // 因 OutboxMessage/SagaState 的 internal static TimeProvider 属性是 AsyncLocal 测试
        // 注入门面（P2 定案：时间控制双轨统一），其存储字段类型为 AsyncLocal<TimeProvider?>，
        // 不会被字段腿误伤。
        await Assert.That(FindMutableStaticClockMember(typeof(OutboxMessage))).IsNull();
        await Assert.That(FindMutableStaticClockMember(typeof(InboxMessage))).IsNull();
        await Assert.That(FindMutableStaticClockMember(typeof(SagaState))).IsNull();
    }

    private static System.Reflection.MemberInfo? FindMutableStaticClockMember(Type type)
    {
        // 字段腿豁免只读形态（P3 批假红向量修复）：static readonly（IsInitOnly）/ const
        //（IsLiteral）的 TimeProvider 字段是不可变共享默认时钟——不是"可变全局静态时钟"
        // 反模式的命中目标，计入即假红。
        var field = type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(member => member.FieldType == typeof(TimeProvider)
                && !member.IsInitOnly
                && !member.IsLiteral);
        if (field is not null)
            return field;

        return type.GetMember("Clock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault();
    }

    private sealed class TestSagaState : SagaState;
}
