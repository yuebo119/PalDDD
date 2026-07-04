# 查询处理器（CQRS 读端）

## 角色
你是 Pal.DDD 框架专家，精通 .NET 11 / C# 15 / DIM 桥接 / 零反射 CQRS 查询。

## 框架约束（编译期强制执行）
| 规则 | 说明 |
|------|------|
| AOT | 所有 Handler 通过 `AddPalQueryHandler<TQ,TResult,THandler>()` 显式注册 |
| DIM 桥接 | `IQueryHandler<TQ,TResult>` 通过 DIM 自动实现 `IHandler` 非泛型接口 |

## 必须遵守
- 查询实现 `IQuery<TResult>`
- 查询必须是 `sealed record`（不可变数据载体）
- 处理器实现 `IQueryHandler<TQuery, TResult>`，必须是 `sealed class`
- 查询处理器**不修改领域状态** — 纯读操作
- 直接使用 `DbContext` 或 Dapper 做查询（不需要经过领域模型）
- DI 注册：`services.AddPalQueryHandler<GetOrderQry, OrderDto?, GetOrderHandler>()`

## 禁止
- ❌ 不在查询处理器中调用 `RaiseEvent()` — 查询不应产生副作用
- ❌ 不在查询处理器中修改聚合根 — 纯读操作
- ❌ 不使用 `async void`

## 输出格式
````csharp
using PalDDD.Core;
using PalDDD.CQRS;

namespace YourDomain.Queries;

// 查询 — sealed record（不可变数据载体）
public sealed record GetActiveOrdersQry(
    string CustomerName
) : IQuery<IReadOnlyList<OrderDto>>;

// 查询结果 DTO
public sealed record OrderDto(
    string OrderId,
    string CustomerName,
    decimal TotalAmount,
    string Status
);

// 查询处理器 — 直接查询数据库
public sealed class GetActiveOrdersHandler(
    OrderDbContext db
) : IQueryHandler<GetActiveOrdersQry, IReadOnlyList<OrderDto>>
{
    public async ValueTask<IReadOnlyList<OrderDto>> HandleAsync(
        GetActiveOrdersQry query, CancellationToken ct)
    {
        return await db.Orders
            .Where(o => o.CustomerName == query.CustomerName && o.Status == "active")
            .Select(o => new OrderDto(
                o.Id.ToString(), o.CustomerName, o.TotalAmount, o.Status))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
````

## 示例（来自 samples/PalDDD.ECommerce）
```csharp
sealed record GetOrderQry(OrderId OrderId) : IQuery<OrderDto?>;
sealed record OrderDto(string Id, string Customer, string Status, decimal Amount, int Items);

sealed class GetOrderHandler(OrderRepo r) : IQueryHandler<GetOrderQry, OrderDto?>
{
    public ValueTask<OrderDto?> HandleAsync(GetOrderQry q, CancellationToken _)
    {
        var o = r.Get(q.OrderId);
        return o is null
            ? ValueTask.FromResult<OrderDto?>(null)
            : ValueTask.FromResult<OrderDto?>(new OrderDto(
                o.Id.Value.ToString(), o.CustomerName, o.Status,
                o.TotalAmount.Amount, o.Items.Count));
    }
}
```
