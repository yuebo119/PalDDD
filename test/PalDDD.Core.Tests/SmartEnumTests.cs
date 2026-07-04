namespace PalDDD.Core.Tests;

[GenerateEnum]
public sealed partial class OrderStatus : SmartEnum<OrderStatus, string>
{
    public static readonly OrderStatus Pending = new("pending", "待处理");
    public static readonly OrderStatus Shipped = new("shipped", "已发货");
    public static readonly OrderStatus Delivered = new("delivered", "已送达");

    private OrderStatus(string value, string displayName) : base(value, displayName)
    {
    }
}

public sealed class SmartEnumTests
{
    [Test]
    public async Task FromValue_ReturnsCorrectItem()
    {
        var status = OrderStatus.FromValue("shipped");
        await Assert.That(status.Value).IsEqualTo("shipped");
        await Assert.That(status.Name).IsEqualTo("已发货");
    }

    [Test]
    public async Task FromValue_Invalid_ThrowsKeyNotFound()
    {
        await Assert.That(() => OrderStatus.FromValue("invalid")).Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task TryFromValue_Valid_ReturnsTrue()
    {
        await Assert.That(OrderStatus.TryFromValue("delivered", out var status)).IsTrue();
        await Assert.That(status!.Value).IsEqualTo("delivered");
    }

    [Test]
    public async Task TryFromValue_Invalid_ReturnsFalse()
    {
        await Assert.That(OrderStatus.TryFromValue("nonexistent", out var status)).IsFalse();
        await Assert.That(status).IsNull();
    }

    [Test]
    public async Task All_ReturnsAllValues()
    {
        var all = OrderStatus.All;
        await Assert.That(all.Count).IsEqualTo(3);
        await Assert.That(all).Contains(OrderStatus.Pending);
        await Assert.That(all).Contains(OrderStatus.Shipped);
        await Assert.That(all).Contains(OrderStatus.Delivered);
    }

    [Test]
    public async Task Equals_ByValue()
    {
        await Assert.That(OrderStatus.Pending.Equals(OrderStatus.FromValue("pending"))).IsTrue();
        await Assert.That(OrderStatus.Pending.Equals(OrderStatus.Shipped)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_ByValue()
    {
        await Assert.That(OrderStatus.Pending.GetHashCode()).IsEqualTo(OrderStatus.FromValue("pending").GetHashCode());
    }

    [Test]
    public async Task ToString_ReturnsName()
    {
        await Assert.That(OrderStatus.Pending.ToString()).IsEqualTo("待处理");
    }

    [Test]
    public async Task EqualityOperator_HandlesSameAndDifferentValues()
    {
        await Assert.That(OrderStatus.Pending == OrderStatus.FromValue("pending")).IsTrue();
        await Assert.That(OrderStatus.Pending != OrderStatus.Shipped).IsTrue();
        await Assert.That(OrderStatus.Pending == null).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    // 并发安全性测试 — Interlocked.CompareExchange + Volatile.Read
    // ═══════════════════════════════════════════════════════════════

    /// <summary>多线程并发读取 All 属性 — 初始化后 FrozenDictionary 是不可变的</summary>
    [Test]
    public async Task ConcurrentReads_All_ReturnsConsistentResults()
    {
        var barrier = new Barrier(4);
        var results = new List<IReadOnlyCollection<OrderStatus>>[4];
        for (var i = 0; i < 4; i++) results[i] = [];

        var tasks = new Task[4];
        for (var t = 0; t < 4; t++)
        {
            var idx = t;
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var j = 0; j < 100; j++)
                    results[idx].Add(OrderStatus.All);
            });
        }

        await Task.WhenAll(tasks);

        // 所有线程读取的结果应一致（3 个值，相同引用）
        foreach (var resultList in results)
        {
            foreach (var all in resultList)
                await Assert.That(all.Count).IsEqualTo(3);
        }
    }

    /// <summary>多线程并发 TryFromValue — FrozenDictionary 读操作线程安全</summary>
    [Test]
    public async Task ConcurrentTryFromValue_AllThreadsReturnCorrectValue()
    {
        var barrier = new Barrier(8);
        var successCount = 0;

        var tasks = new Task[8];
        for (var t = 0; t < 8; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < 100; i++)
                {
                    if (OrderStatus.TryFromValue("shipped", out var result))
                    {
                        await Assert.That(result!.Value).IsEqualTo("shipped");
                        Interlocked.Increment(ref successCount);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        await Assert.That(successCount).IsEqualTo(8 * 100);
    }

    /// <summary>
    /// 重复调用 RegisterValues — Interlocked.CompareExchange 防止第二次调用覆盖第一次。
    /// 验证第二次调用不破坏已注册的值。
    /// </summary>
    [Test]
    public async Task RegisterValues_CalledTwice_SecondCallDoesNotOverwrite()
    {
        // 注册第一组值
        DuplicateRegSmartEnum.RegisterFirst();

        var v1 = DuplicateRegSmartEnum.FromValue(1);
        await Assert.That(v1.Name).IsEqualTo("第一组");

        // 第二次注册尝试（不同值）—— 不应覆盖
        DuplicateRegSmartEnum.RegisterSecond();

        // 仍返回第一组的值
        v1 = DuplicateRegSmartEnum.FromValue(1);
        await Assert.That(v1.Name).IsEqualTo("第一组");

        // 第二组的值不会存在
        await Assert.That(DuplicateRegSmartEnum.TryFromValue(100, out _)).IsFalse();
    }
}

[GenerateEnum]
public sealed partial class DuplicateRegSmartEnum : SmartEnum<DuplicateRegSmartEnum, int>
{
    public static readonly DuplicateRegSmartEnum FirstValue = new(1, "第一组");
    public static readonly DuplicateRegSmartEnum FirstOther = new(2, "第一组-其它");

    private DuplicateRegSmartEnum(int value, string name) : base(value, name)
    {
    }

    public static void RegisterFirst()
        => RegisterValues(new[] { FirstValue, FirstOther });

    public static void RegisterSecond()
        => RegisterValues(new[] { new DuplicateRegSmartEnum(100, "第二组"), new DuplicateRegSmartEnum(200, "第二组-其它") });
}

[GenerateEnum]
public sealed partial class NumericSmartEnum : SmartEnum<NumericSmartEnum, int>
{
    public static readonly NumericSmartEnum Low = new(1, "低");
    public static readonly NumericSmartEnum High = new(10, "高");

    private NumericSmartEnum(int value, string name) : base(value, name)
    {
    }
}

public sealed class NumericSmartEnumTests
{
    [Test]
    public async Task FromValue_Int()
    {
        await Assert.That(NumericSmartEnum.FromValue(1).Name).IsEqualTo("低");
        await Assert.That(NumericSmartEnum.FromValue(10).Name).IsEqualTo("高");
    }
}
