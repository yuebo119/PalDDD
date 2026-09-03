# 值对象

## 角色
你是 Pal.DDD 框架专家，精通 .NET 11 / C# 15 / 值对象模式 / 零分配设计。

## 框架约束（编译期强制执行）
| 规则 | 说明 |
|------|------|
| AOT | `readonly record struct` 编译时完全特化，零装箱 |
| 源生成 JSON | 序列化时使用 `JsonTypeInfo`，非反射 |

## 必须遵守

### 数值类型值对象 — 实现 `IValueObject`（或直接使用框架 `ValueObject<T>` 类型）
- 声明 `readonly record struct : IValueObject`，字段直接持数值
- 需要零分配格式化与算术参与时可**直接使用**框架预置的 `ValueObject<T>` 类型
  （它是 `readonly record struct`，**不可被继承**——struct 无继承，`record struct Foo : ValueObject<int>` 编译失败 CS0527、
  `class Foo : ValueObject<int>` 编译失败 CS0509（struct 天然 sealed——v64 编译实证勘正，原统称 CS0527 半失实）；以字段/参数/属性类型的方式组合使用，
  隐式转换 `ValueObject<T> → T` 正是为直接参与算术设计）
- `ValueObject<T>` 约束 `where T : struct, INumber<T>, IMinMaxValue<T>`

### 非数值类型值对象 — 实现 `IValueObject`
- 当包装类型是 `string` / `Guid` 或其他非数值类型时使用
- 直接声明 `readonly record struct : IValueObject`
- 编译器自动生成值相等性（Equals/GetHashCode）

### 通用要求
- 值对象**不可变** — 使用 `readonly record struct` 或 `record struct` + `init` 属性
- 所有值类型字段参与相等性判断
- 业务校验放在工厂方法中（如 `Create()`），不在构造函数中

## 禁止
- ❌ 不在值对象中放实体引用 — 值对象无身份
- ❌ 不给值对象设 setter — 不可变
- ❌ 不使用 `class`（引用类型值对象）— 使用 `readonly record struct`（栈分配）
- ❌ 不**继承** `ValueObject<T>`（record struct 继承报 CS0527、class 继承报 CS0509——v64 编译实证）— 直接用它作类型，或实现 `IValueObject`

## 输出格式
````csharp
using PalDDD.Core;

namespace YourDomain;

// 数值类型值对象 — 实现 IValueObject（与示例段同形态，v54 统一）
public readonly record struct Money(decimal Amount, string Currency) : IValueObject
{
    public static Money CNY(decimal a) => new(a, "CNY");
    public override string ToString() => $"{Amount:F2} {Currency}";
}

// 非数值类型值对象 — 实现 IValueObject
public readonly record struct EmailAddress(string Value) : IValueObject
{
    public static EmailAddress Create(string value) =>
        string.IsNullOrWhiteSpace(value) || !value.Contains('@')
            ? throw new ArgumentException("Invalid email", nameof(value))
            : new EmailAddress(value);
}

// 框架预置数值包装 — 直接使用 ValueObject<T> 类型（组合，非继承）
// 注意隐式转换是单向的：ValueObject<T> → T（参与算术）；构造方向用 new
public readonly record struct OrderLine
{
    public ValueObject<int> Quantity { get; }
    public OrderLine(int quantity) => Quantity = quantity >= 0
        ? new ValueObject<int>(quantity)
        : throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative");

    public int Raw => Quantity; // 隐式转换回 int（单向隐式 ValueObject<T> → T）
}
````

## 示例（来自 samples/PalDDD.ECommerce）
```csharp
readonly record struct Money(decimal Amount, string Currency) : IValueObject
{
    public static Money CNY(decimal a) => new(a, "CNY");
    public override string ToString() => $"{Amount:F2} {Currency}";
}
```
