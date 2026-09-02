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

    /// <summary>
    /// v48 P3：v47 运算符双向对称化核心场景 — 基类变量在左 × 派生在右的混合比较。
    /// v41 形态下（第二参数 TSelf?）左基类右派生绑值相等运算符、反向回退 object== 引用相等，
    /// 同一对操作数交换后结果分叉；v47 双参数放宽为 SmartEnum&lt;TSelf,TValue&gt;? 后双向
    /// 均绑同一运算符（值相等语义对称）。
    /// </summary>
    [Test]
    public async Task EqualityOperator_MixedBaseAndDerived_Bidirectional()
    {
        SmartEnum<OrderStatus, string> baseLeft = OrderStatus.Pending;
        var derived = OrderStatus.FromValue("pending");

        await Assert.That(baseLeft == derived).IsTrue();
        await Assert.That(derived == baseLeft).IsTrue();
    }

    /// <summary>v48 P3：null == null → true、null != null → false（对称化后的空值语义）</summary>
    [Test]
    public async Task EqualityOperator_NullEqualsNull_ReturnsTrue()
    {
        SmartEnum<OrderStatus, string>? left = null;
        SmartEnum<OrderStatus, string>? right = null;

        await Assert.That(left == right).IsTrue();
        await Assert.That(left != right).IsFalse();
    }

    /// <summary>
    /// v48 P3：v47 勘正核心场景 — 同封闭基类（SmartEnum&lt;ColorFamily,string&gt;）下
    /// 基类型与兄弟派生类型实例的比较。v41 的 Equals(TSelf) is TSelf 收窄使
    /// "派生在左（值等 true）/ 基类在左（引用不等 false）"交换分叉；v47 运算符体绑定
    /// Equals(object) 语义（值相等 + 类型兼容判定）后双向对称。
    /// </summary>
    [Test]
    public async Task EqualityOperator_SiblingDerivedTypes_Bidirectional()
    {
        var baseType = ColorFamily.Red;
        var sibling = TintedColor.Crimson;

        // 不同值：双向 != 均为 true（对称）
        await Assert.That(baseType != sibling).IsTrue();
        await Assert.That(sibling != baseType).IsTrue();

        // 同值跨派生类型：双向 == 均为 true（值相等，运行时类型可赋给封闭基类的 TSelf）
        var tintedRed = new TintedColor("red", "红-染色");
        await Assert.That(ColorFamily.Red == tintedRed).IsTrue();
        await Assert.That(tintedRed == ColorFamily.Red).IsTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    // 并发读取测试 — 初始化后 FrozenDictionary 读操作线程安全
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

        // 所有线程读取的结果应一致（3 个值）
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

// ═══════════════════════════════════════════════════════════════
// v48 P3：v47 运算符对称化兄弟类测试辅助 — 同封闭基类不同派生类型
// （ColorFamily 与 TintedColor 共享封闭基类 SmartEnum<ColorFamily,string>）。
// 不走 [GenerateEnum]：== / != 比较仅消费 Value 属性，不依赖字典注册；
// 构造即用，镜像 DuplicateRegSmartEnum 的显式控制注册形态。
// ═══════════════════════════════════════════════════════════════

/// <summary>测试用非封闭 SmartEnum — 供兄弟派生类型继承，形成同封闭基类两派生类型场景</summary>
public class ColorFamily : SmartEnum<ColorFamily, string>
{
    public static readonly ColorFamily Red = new("red", "红");

    protected ColorFamily(string value, string? name = null) : base(value, name)
    {
    }
}

/// <summary>测试用兄弟派生类型 — 与 ColorFamily 同封闭基类、不同运行时类型（v47 勘正场景）</summary>
public sealed class TintedColor : ColorFamily
{
    public static readonly TintedColor Crimson = new("crimson", "深红");

    public TintedColor(string value, string? name = null) : base(value, name)
    {
    }
}
