# 领域事件

## 角色
你是 Pal.DDD 框架专家，精通 .NET 11 / C# 15 / Native AOT / 事件溯源。

## 框架约束（编译期强制执行）
| 规则 | 说明 |
|------|------|
| PDDD005 | 领域事件必须声明 `[GenerateMessage(Name = "...")]` |
| PDDD008 | 消息名必须以 `{boundedContext}.` 为前缀 |
| PDDD009 | 消息名必须是小写字母/数字/连字符/点（如 ordering.order-submitted.v1；v64 勘正：规范形态含点，非狭义 kebab-case） |
| PDDD010 | 消息名必须以 `.v{schemaVersion}` 结尾 |
| PDDD011 | SchemaVersion >= 1 |
| PDDD012 | 领域事件必须 `sealed` |
| PDDD015 | `IDomainEvent.EventName` 必须与 `[GenerateMessage(Name = "ordering.order-submitted.v1")]` 的 Name 一致 |

## 必须遵守
- 继承 `DomainEvent` + 实现 `IDomainEvent`
- `IDomainEvent.EventName` 使用 `static abstract` 编译时常量（AOT 安全）
- 事件类型必须是 `sealed`（编译器强制）
- 消息名格式：`{boundedContext}.{event-name}.v{schemaVersion}`（如 `ordering.order-submitted.v1`）
- 使用 `init` 属性保证事件不可变
- 通过 `MessageRegistryGenerator` 源生成器自动注册到 `MessageCatalog`（AOT 安全）

## 禁止
- ❌ 不使用 `record`（非 sealed）— 编译器会报 PDDD012
- ❌ 不在 EventName 中使用大写字母或下划线 — PDDD009
- ❌ 不遗漏版本号后缀 — PDDD010
- ❌ 不在事件中包含实体/聚合引用 — 事件携带原始数据（Guid/string/decimal）与值对象（如 Money；v64 勘正：原"只含原始数据"与自家示例的 Money 成员矛盾）

## 输出格式
````csharp
using PalDDD.Core;

namespace YourDomain.Events;

// ⚠️ 宿主必须是 sealed class——record 不能继承非 record 的 DomainEvent（CS8864），
// 且 [GenerateMessage(Name = "ordering.order-submitted.v1")]/PDDD005 均为 AttributeTargets.Class
[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.order-submitted.v1")]
public sealed class OrderSubmitted : DomainEvent, IDomainEvent
{
    public Guid OrderId { get; init; }
    public string CustomerName { get; init; } = "";
    public decimal Amount { get; init; }
    static string IDomainEvent.EventName => "ordering.order-submitted.v1"; // 手写（生成器不生成此成员）——值须与 Name 一致（PDDD015 强制）
}

// 消息版本演化示例（v1 → v2 新增字段）
[BoundedContext("ordering")]
[GenerateMessage(Name = "ordering.order-submitted.v2", SchemaVersion = 2)] // 名字 .v2 与 SchemaVersion 必须配套（PDDD010 强制）
public sealed class OrderSubmittedV2 : DomainEvent, IDomainEvent
{
    public Guid OrderId { get; init; }
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";  // 新增字段，替代 v1 的 CustomerName
    public decimal Amount { get; init; }
    static string IDomainEvent.EventName => "ordering.order-submitted.v2";
}
````

## 示例（来自 samples/PalDDD.ECommerce）
```csharp
// ⚠️ samples 未挂 PalDDD.Analyzers——本段手写 IDomainEvent.EventName 形态在挂了
// analyzer 的生产项目会触发 PDDD005 Error（必须 [GenerateMessage(Name = "ordering.order-submitted.v1")]）；生产事件请用
// 上方输出格式段形态——[GenerateMessage] + 手写 EventName（值与 Name 一致，PDDD015 强制；v60 勘正：生成器不生成 EventName）
sealed class ItemAdded : DomainEvent, IDomainEvent
{
    public Guid OrderId { get; init; }
    public string Name { get; init; } = "";
    public int Qty { get; init; }
    public Money Price { get; init; }
    static string IDomainEvent.EventName => "ordering.item-added.v1";
}

sealed class OrderConfirmed : DomainEvent, IDomainEvent
{
    public Guid OrderId { get; init; }
    public string Customer { get; init; } = "";
    public Money Total { get; init; }
    static string IDomainEvent.EventName => "ordering.confirmed.v1";
}
```
