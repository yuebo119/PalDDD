using PalDDD.Core;
using PalDDD.CQRS;
using System.Text.Json.Serialization;

#pragma warning disable CA1812, CA1515, CA1050, CA1062

namespace PalDDD.MinimalApi
{
    readonly record struct Money(decimal Amount, string Currency) : IValueObject
    {
        public static Money CNY(decimal a) => new(a, "CNY");
    }
    readonly partial record struct OrderId(Guid Value) : IPalIdentity<Guid>;

        internal sealed class Order : AggregateRoot<OrderId>
    {
        public string CustomerName { get; private set; } = null!; public Money TotalAmount { get; private set; } = Money.CNY(0); public string Status { get; private set; } = "pending";
        // SMP-102：Items 暴露只读接口（同 ECommerce 示例）——public List 可被外部持有者
        // 绕过聚合直接 Add/Remove；构造内 List 改私有字段 _items，经 IReadOnlyList 视图对外
        private readonly List<OrderItem> _items = [];
        public IReadOnlyList<OrderItem> Items => _items;

        public Order(OrderId id, string cn) : base(id) => CustomerName = cn;

        // SMP-103 守卫：参数校验（数量/单价/币种）先于状态机校验，fail-fast。
        // 本示例无 Confirm 转换方法，状态机守卫以 Status != "pending" 表达——
        // 非 pending（已确认/已取消等）一律拒改
        public void AddItem(string n, int q, Money p)
        {
            if (q <= 0) throw new ArgumentOutOfRangeException(nameof(q), q, "数量必须为正整数");
            if (p.Amount < 0) throw new ArgumentOutOfRangeException(nameof(p), p.Amount, "单价不能为负数");
            if (p.Currency != TotalAmount.Currency) throw new ArgumentException($"币种不匹配：订单 {TotalAmount.Currency}，入参 {p.Currency}", nameof(p));
            if (Status != "pending") throw new InvalidOperationException("订单已不在待定状态，不能再添加商品");
            _items.Add(new OrderItem { Name = n, Qty = q, Price = p }); TotalAmount = Money.CNY(TotalAmount.Amount + p.Amount * q);
        }
    }

    internal sealed class OrderItem
    {
        public string Name { get; init; } = "";
        public int Qty { get; init; }
        public Money Price { get; init; }
    }

    sealed record CreateOrderCmd(string CustomerName) : ICommand<OrderId>;
    sealed record AddItemCmd(OrderId OrderId, string Name, int Qty, Money Price) : ICommand;
    sealed record GetOrderQry(OrderId OrderId) : IQuery<OrderDto?>;
    sealed record OrderDto(string Id, string Customer, string Status, decimal Amount, int Items);

    internal sealed class OrderRepo
    { private readonly Dictionary<OrderId, Order> _s = []; public Order? Get(OrderId id) => _s.GetValueOrDefault(id); public void Add(Order o) => _s[o.Id] = o; }

    internal sealed class CreateOrderH(OrderRepo r) : ICommandHandler<CreateOrderCmd, OrderId>
    {
        public ValueTask<OrderId> HandleAsync(CreateOrderCmd c, CancellationToken ct)
        {
            var id = new OrderId(Guid.NewGuid()); r.Add(new Order(id, c.CustomerName)); return ValueTask.FromResult(id);
        }
    }

    internal sealed class AddItemH(OrderRepo r) : ICommandHandler<AddItemCmd, Unit>
    {
        public ValueTask<Unit> HandleAsync(AddItemCmd c, CancellationToken ct)
        {
            // 示例从简：订单不存在时 Get! 会 NRE → 全局异常中间件 500。
            // 生产代码应改为：r.Get 判 null 并抛领域 NotFound 异常（映射 404）或返回失败结果。
            r.Get(c.OrderId)!.AddItem(c.Name, c.Qty, c.Price); return ValueTask.FromResult(new Unit());
        }
    }

    internal sealed class GetOrderH(OrderRepo r) : IQueryHandler<GetOrderQry, OrderDto?>
    {
        public ValueTask<OrderDto?> HandleAsync(GetOrderQry q, CancellationToken ct)
        {
            var o = r.Get(q.OrderId); return ValueTask.FromResult(o is null ? null : new OrderDto(o.Id.Value.ToString()[..8], o.CustomerName, o.Status, o.TotalAmount.Amount, o.Items.Count));
        }
    }

    [JsonSerializable(typeof(CreateOrderCmd))]
    [JsonSerializable(typeof(AddItemCmd))]
    [JsonSerializable(typeof(OrderId))]
    [JsonSerializable(typeof(OrderDto))]
    internal sealed partial class AppJsonContext : JsonSerializerContext;
}
