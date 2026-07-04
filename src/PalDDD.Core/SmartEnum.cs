using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Core;

// ─────────────────────────────────────────────────────────────
// 智能枚举 — DDD 战术模式 + FrozenDictionary O(1) 查找 + AOT 安全
// 💡 保留理由：DDD 枚举语义 + FrozenDictionary 零反射查找 + 源码生成器。
//    详见 docs/decisions/004-core-type-retention.md
// ─────────────────────────────────────────────────────────────
//
// 线程安全：Interlocked.CompareExchange 确保多模块初始化器
//    并发注册时不会互相覆盖。Volatile.Read 确保读取线程看到最新值。

/// <summary>
/// 智能枚举基类。FrozenDictionary O(1) 查找，源码生成器注册值。
/// </summary>
/// <remarks>
/// 源码生成器在 <c>[ModuleInitializer]</c> 中调用 <c>RegisterValues</c> 注入编译时已知的值。
/// 使用 <c>Interlocked.CompareExchange</c> + <c>Volatile.Read</c> 确保弱内存模型下的线程安全，
/// 同时防止 <c>[ModuleInitializer]</c> 在多模块场景下被多次调用时重复注册覆盖正确数据。
/// </remarks>
[SuppressMessage("Sonar", "S4035", Justification = "SmartEnum 是抽象基类，设计用于被继承而非密封。")]
public abstract class SmartEnum<TSelf, TValue> : IEquatable<TSelf>
    where TSelf : SmartEnum<TSelf, TValue>
    where TValue : notnull, IEquatable<TValue>
{
    private static FrozenDictionary<TValue, TSelf>? s_values;

    public TValue Value { get; }
    public string Name { get; }

    protected SmartEnum(TValue value, string? name = null)
    {
        Value = value;
        Name = name ?? value.ToString()!;
    }

    /// <summary>所有枚举值 — O(1) 查找</summary>
    public static IReadOnlyCollection<TSelf> All => Dictionary.Values;

    /// <summary>根据值获取枚举项</summary>
    public static TSelf FromValue(TValue value) =>
        Dictionary.TryGetValue(value, out var item)
            ? item
            : throw new KeyNotFoundException($"No {typeof(TSelf).Name} with value {value}");

    /// <summary>尝试根据值获取枚举项</summary>
    public static bool TryFromValue(TValue value, [NotNullWhen(true)] out TSelf? result) =>
        Dictionary.TryGetValue(value, out result);

    // ═══════════════════════════════════════════════════════════════
    // AOT 安全注入点 — 源码生成器在静态构造函数中调用
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 编译时值注册 — 由源码生成器在 <c>[ModuleInitializer]</c> 中调用。
    /// AOT 安全：所有 typeof(TSelf) 引用均为编译时常量。
    /// 使用 <c>Interlocked.CompareExchange</c> 防止重复注册覆盖。
    /// </summary>
    protected static void RegisterValues(IEnumerable<TSelf> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        // 委托给零分配版（内部转为数组仅此一次，启动期可接受）
        RegisterValues(values.ToArray());
    }

    /// <summary>
    /// 编译时值注册（零分配版）— 接受 span / 数组 / 集合表达式，无堆分配。
    /// 使用 <c>Interlocked.CompareExchange</c> 防止重复注册覆盖。
    /// </summary>
    protected static void RegisterValues(ReadOnlySpan<TSelf> values)
    {
        var dict = new Dictionary<TValue, TSelf>(values.Length);
        foreach (var item in values)
            dict[item.Value] = item;
        Interlocked.CompareExchange(ref s_values, dict.ToFrozenDictionary(), null);
    }

    // ═══════════════════════════════════════════════════════════════
    // 字典访问 — 只接受源码生成器或显式模块初始化器注册
    // ═══════════════════════════════════════════════════════════════

    private static FrozenDictionary<TValue, TSelf> Dictionary
    {
        get
        {
            var values = Volatile.Read(ref s_values);
            if (values is not null) return values;

            throw new InvalidOperationException(
                $"SmartEnum<{typeof(TSelf).Name}, {typeof(TValue).Name}> 未注册任何值。请使用 [GenerateEnum] 源码生成器，或在模块初始化器中调用 RegisterValues。");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 相等性判断
    // ═══════════════════════════════════════════════════════════════

    public bool Equals(TSelf? other) => other is not null && EqualityComparer<TValue>.Default.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is TSelf other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Name;

    public static bool operator ==(SmartEnum<TSelf, TValue>? left, TSelf? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(SmartEnum<TSelf, TValue>? left, TSelf? right) => !(left == right);
}
