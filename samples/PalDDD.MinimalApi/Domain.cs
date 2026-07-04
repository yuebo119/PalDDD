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

    [AggregateName("Order")]
    internal sealed class Order : AggregateRoot<OrderId>
    {
        public string CustomerName { get; private set; } = null!; public Money TotalAmount { get; private set; } = Money.CNY(0); public string Status { get; private set; } = "pending"; public List<OrderItem> Items { get; } = [];

        public Order(OrderId id, string cn) : base(id) => CustomerName = cn;

        public void AddItem(string n, int q, Money p)
        { Items.Add(new OrderItem { Name = n, Qty = q, Price = p }); TotalAmount = Money.CNY(TotalAmount.Amount + p.Amount * q); }
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
