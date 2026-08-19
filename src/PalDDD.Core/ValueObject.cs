using System.Numerics;

namespace PalDDD.Core;

// ─────────────────────────────────────────────────────────────
// 💰 IValueObject — 值对象标记接口（DDD Ubiquitous Language）
// ─────────────────────────────────────────────────────────────
// 💡 保留理由：DDD 语义标记 + 源码生成器契约 + 框架扩展性。
//    详见 docs/decisions/003-ivalueobject-retention.md
// ─────────────────────────────────────────────────────────────

/// <summary>值对象标记接口 — DDD Ubiquitous Language 的一部分，标识类型为值对象</summary>
public interface IValueObject
{ }

// ─────────────────────────────────────────────────────────────
// 💰 ValueObject<T>（值对象） — 基于 readonly record struct 的零分配数值包装
// ─────────────────────────────────────────────────────────────
//
// 💡 通俗解释 —— 什么是值对象？
//   ｜ 值对象是没有独立身份的对象，它的"值"就是它的身份。
//   ｜ 两张 100 元的人民币是完全等价的（面额相同）—— 你不在乎具体哪张钞票，只在乎金额。
//   ｜ 对比实体：你和你的克隆人即使 DNA 相同，也是两个不同的人（因为 Id 不同）。
//
// 💡 为什么用 readonly record struct？
//   ｜ 1. 栈分配 —— 零堆压力，GC 几乎无负担
//   ｜ 2. 值相等性 —— 编译器自动生成 Equals/GetHashCode
//   ｜ 3. 不可变性 —— readonly 保证创建后不可修改
//   ｜ 4. AOT 友好 —— struct 泛型在编译期完全特化，无需运行时装箱

/// <summary>
/// 泛型数学值对象。基于 readonly record struct，零堆分配、值相等性、编译时数值运算。
/// <para>
/// 💡 <b>性能优化：</b>实现 IUtf8SpanFormattable 接口，格式化输出时直接写入 UTF-8 buffer，
/// 避免字符串分配 —— 这对高频日志和序列化场景至关重要。
/// </para>
/// </summary>
/// <remarks>
/// 此基类型不做隐式值域约束 —— <typeparamref name="T"/> 的 <c>MinValue</c>/<c>MaxValue</c>
/// 即类型的完整范围，对它们 <c>Clamp</c> 是恒等操作。业务值域约束应由派生类型自行实现。
/// <para>
/// 💡 <b>为什么仅支持数值类型？</b><br/>
/// string / Guid 等非数值类型可直接声明 <c>readonly record struct : IValueObject</c>，
/// 编译器自动生成值相等性，无需基类的 TryFormat / implicit operator 支持。
/// <c>ValueObject&lt;T&gt;</c> 专为数值类型提供 IUtf8SpanFormattable 和隐式转换。
/// </para>
/// <para>
/// 💡 <b>非数值类型示例：</b><br/>
/// <code>
/// // EmailAddress 值对象 — string 包装，无需继承 ValueObject&lt;T&gt;
/// public readonly record struct EmailAddress(string Value) : IValueObject
/// {
///     public static EmailAddress Create(string value) =>
///         string.IsNullOrWhiteSpace(value) || !value.Contains('@')
///             ? throw new ArgumentException("Invalid email address", nameof(value))
///             : new EmailAddress(value);
/// }
///
/// // UserId 值对象 — Guid 包装，用于强类型 ID
/// public readonly record struct UserId(Guid Value) : IValueObject;
/// </code>
/// </para>
/// </remarks>
public readonly record struct ValueObject<T> : IValueObject, IUtf8SpanFormattable
    where T : struct, INumber<T>, IMinMaxValue<T>
{
    /// <summary>值对象中包装的原始数值</summary>
    public T Value { get; }

    public ValueObject(T value) { Value = value; }

    /// <summary>隐式转换为底层数值类型 —— 允许直接参与算术运算</summary>
    public static implicit operator T(ValueObject<T> vo) => vo.Value;
    public override string ToString() => Value.ToString()!;

    /// <inheritdoc/>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        // 装箱修复（benchmark 实证旧实现每次调用分配 24B——接口 is-匹配对 struct 装箱）：
        // INumberBase<T> 已含 ISpanFormattable，受约束调用经 JIT 静态分派零装箱，
        // 先格式化到栈上 UTF16 缓冲再经 Utf8.FromUtf16 转码，全程零堆分配。
        Span<char> utf16 = stackalloc char[128];
        if (Value.TryFormat(utf16, out int charsWritten, format, provider))
            return WriteUtf8(utf16[..charsWritten], utf8Destination, out bytesWritten);

        // P3 修复（八轮评审）：utf16 中间缓冲不足（如超长格式输出）时回退 ToString 堆路径
        // ——保证"返回 false 仅因 utf8Destination 不足"的 TryFormat 契约，而非因内部
        // 128 字符缓冲不足而误报失败。（span 不能跨分支存入同一局部——ref 安全分析
        // CS8352，故两条路径各自直达转码辅助方法。）
        return WriteUtf8(Value.ToString(format.IsEmpty ? null : format.ToString(), provider),
            utf8Destination, out bytesWritten);
    }

    private static bool WriteUtf8(ReadOnlySpan<char> source, Span<byte> destination, out int bytesWritten)
    {
        var status = System.Text.Unicode.Utf8.FromUtf16(source, destination,
            out _, out bytesWritten);
        if (status != System.Buffers.OperationStatus.Done)
        {
            // 保持 .NET TryFormat 契约：失败时不报告部分写入（与原 IUtf8SpanFormattable 直写行为一致）
            bytesWritten = 0;
            return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => Value.ToString(format, formatProvider);
}

// ─────────────────────────────────────────────────────────────
// 📐 轻量值类型集合 — 软删除 + 时间戳 + 乐观并发（行版本）
// ─────────────────────────────────────────────────────────────
//
// 💡 设计原则：
//   ｜ 所有这些类型都是 readonly record struct —— 零堆分配、值相等性、不可变。
//   ｜ 每个类型都提供 implicit operator，可以与原始类型无缝交互。
//   ｜ 所有类型都使用 DomainEvent.TimeProvider 获取时间，确保测试确定性。

/// <summary>
/// 软删除标记 —— 配合 EF Core QueryFilter 自动过滤已删除记录。
/// <para>
/// 💡 通俗解释 —— 什么是软删除？<br/>
/// 软删除不真正删除数据库记录，只是标记为"已删除"。<br/>
/// 好处是可以恢复误删数据、保留审计历史、维护外键完整性。
/// </para>
/// </summary>
public readonly record struct Deleted(bool Value = false)
{
    public static readonly Deleted No = new(false);
    public static readonly Deleted Yes = new(true);
    public static implicit operator bool(Deleted d) => d.Value;
    public static implicit operator Deleted(bool b) => new(b);
    public override string ToString() => Value ? "deleted" : "active";
}

/// <summary>
/// 软删除时间 —— 与 Deleted 配对使用，记录软删除发生的时刻。
/// <para>💡 Value 为 null 表示从未被删除。使用 DomainEvent.TimeProvider 获取时间。</para>
/// </summary>
public readonly record struct DeletedTime(DateTimeOffset? Value)
{
    public static DeletedTime Now() => new(DomainEvent.TimeProvider.GetUtcNow());
    public static readonly DeletedTime Never = new(null);
    public override string ToString() => Value?.ToString("O") ?? "never";
}

/// <summary>
/// 最后修改时间戳。
/// <para>💡 每次实体修改时更新，用于审计追踪、乐观并发检测、缓存失效判断。</para>
/// </summary>
public readonly record struct UpdateTime(DateTimeOffset Value)
{
    public static UpdateTime Now() => new(DomainEvent.TimeProvider.GetUtcNow());
    public static implicit operator DateTimeOffset(UpdateTime t) => t.Value;
    public override string ToString() => Value.ToString("O");
}

/// <summary>
/// 乐观并发行版本号 —— readonly record struct，零堆分配。
/// <para>
/// 💡 通俗解释 —— 什么是乐观并发控制？<br/>
/// 不锁数据库行，而是在更新时检查版本号是否变化：<br/>
/// UPDATE ... SET Version = 2 WHERE Version = 1<br/>
/// 如果 Version 已经变成 2（被其他人改过），更新影响 0 行，说明冲突。<br/>
/// 这种方法比悲观锁（SELECT ... FOR UPDATE）性能更高，适合读多写少的场景。
/// </para>
/// </summary>
public readonly record struct RowVersion(int Value)
{
    /// <summary>递增版本号 —— 返回新实例，不修改原实例</summary>
    /// <exception cref="OverflowException">Value == int.MaxValue 时——与 IdentityGenerator（ITM-099）的 checked 语义一致，
    /// 溢出显式失败优于静默回绕（回绕产生"旧版本"乐观锁令牌，并发判定失真）。</exception>
    public RowVersion Next() => new(checked(Value + 1));
    public static implicit operator int(RowVersion v) => v.Value;
    public override string ToString() => Value.ToString();
}
