using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core;
using PalDDD.CQRS;
using PalDDD.DependencyInjection;
using PalDDD.Transactions;

#pragma warning disable CA1812 // DI 实例化的内部类
#pragma warning disable CS9113 // 主构造器参数由属性自动捕获

// 运行
await ECommerceApp.RunAsync();

// ─── 领域模型 ───
readonly record struct Money(decimal Amount, string Currency) : IValueObject
{ public static Money CNY(decimal a) => new(a, "CNY"); public override string ToString() => $"{Amount:F2} {Currency}"; }

readonly partial record struct OrderId(Guid Value) : IPalIdentity<Guid>;

[BoundedContext("ecommerce")]
internal sealed class Order : AggregateRoot<OrderId>
{
    public string CustomerName { get; private set; }
    public Money TotalAmount { get; private set; } = Money.CNY(0);
    public string Status { get; private set; } = "pending";
    // SMP-102：Items 暴露只读接口——public List 可被外部持有者绕过聚合直接 Add/Remove 破坏不变量；
    // 构造内的 List 改私有字段 _items，经 IReadOnlyList 视图对外
    private readonly List<OrderItem> _items = [];
    public IReadOnlyList<OrderItem> Items => _items;

    public Order(OrderId id, string cn) : base(id) => CustomerName = cn;

    // SMP-103 守卫：参数校验（数量/单价/币种）先于状态机校验，fail-fast——
    // 非法参数在触碰任何聚合状态前拒绝
    public void AddItem(string name, int qty, Money price)
    {
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), qty, "数量必须为正整数");
        if (price.Amount < 0) throw new ArgumentOutOfRangeException(nameof(price), price.Amount, "单价不能为负数");
        if (price.Currency != TotalAmount.Currency) throw new ArgumentException($"币种不匹配：订单 {TotalAmount.Currency}，入参 {price.Currency}", nameof(price));
        if (Status == "confirmed") throw new InvalidOperationException("订单已确认，不能再添加商品");
        _items.Add(new OrderItem { Name = name, Qty = qty, Price = price }); TotalAmount = Money.CNY(TotalAmount.Amount + price.Amount * qty); RaiseEvent(new ItemAdded { OrderId = Id.Value, Name = name, Qty = qty, Price = price });
    }

    public void Confirm()
    { Status = "confirmed"; RaiseEvent(new OrderConfirmed { OrderId = Id.Value, Customer = CustomerName, Total = TotalAmount }); }
}

internal sealed class OrderItem
{ public string Name { get; init; } = ""; public int Qty { get; init; } public Money Price { get; init; } }

// ─── 领域事件 ───
internal sealed class ItemAdded : DomainEvent, IDomainEvent
{ public Guid OrderId { get; init; } public string Name { get; init; } = ""; public int Qty { get; init; } public Money Price { get; init; } static string IDomainEvent.EventName => "ordering.item-added.v1"; }

internal sealed class OrderConfirmed : DomainEvent, IDomainEvent
{ public Guid OrderId { get; init; } public string Customer { get; init; } = ""; public Money Total { get; init; } static string IDomainEvent.EventName => "ordering.confirmed.v1"; }

// ─── CQRS ───
sealed record AddItemCmd(OrderId OrderId, string Name, int Qty, Money Price) : ICommand;
sealed record ConfirmCmd(OrderId OrderId) : ICommand;
sealed record GetOrderQry(OrderId OrderId) : IQuery<OrderDto?>;
sealed record OrderDto(string Id, string Customer, string Status, decimal Amount, int Items);

// ─── 仓储 ───
internal sealed class OrderRepo

{ private readonly Dictionary<OrderId, Order> _s = []; public Order? Get(OrderId id) => _s.GetValueOrDefault(id); public void Add(Order o) => _s[o.Id] = o; }

// ─── 处理器 ───
internal sealed class AddItemH(OrderRepo r) : ICommandHandler<AddItemCmd, Unit>

{
    public ValueTask<Unit> HandleAsync(AddItemCmd c, CancellationToken ct)
    {
        // 示例从简：订单不存在时 Get! 会 NRE。生产代码应判 null 并抛领域 NotFound 异常（映射 404）。
        r.Get(c.OrderId)!.AddItem(c.Name, c.Qty, c.Price); return ValueTask.FromResult(new Unit());
    }
}

internal sealed class ConfirmH(OrderRepo r) : ICommandHandler<ConfirmCmd, Unit>

{
    public ValueTask<Unit> HandleAsync(ConfirmCmd c, CancellationToken ct)
    {
        // 示例从简：订单不存在时 Get! 会 NRE。生产代码应判 null 并抛领域 NotFound 异常（映射 404）。
        r.Get(c.OrderId)!.Confirm(); return ValueTask.FromResult(new Unit());
    }
}

internal sealed class GetOrderH(OrderRepo r) : IQueryHandler<GetOrderQry, OrderDto?>

{
    public ValueTask<OrderDto?> HandleAsync(GetOrderQry q, CancellationToken ct)
    {
        var o = r.Get(q.OrderId); return ValueTask.FromResult(o is null ? null : new OrderDto(o.Id.Value.ToString()[..8], o.CustomerName, o.Status, o.TotalAmount.Amount, o.Items.Count));
    }
}

// ─── Saga ───
internal sealed class FState : SagaState;

internal sealed class FSaga : Saga<FState>
{
    public FSaga()
    {
        When("Started", new SagaStep("Notify", async (s, e, ct) => { s.CurrentState = "Notified"; Console.WriteLine("  🏭 Saga: 通知仓库"); return s; }, compensate: async (s, ct) => Console.WriteLine("  ↩️ 补偿: 取消仓库通知")));
        When("Notified", new SagaStep("Ship", async (s, e, ct) => { s.Status = SagaStatus.Completed; s.CurrentState = "Done"; Console.WriteLine("  🚚 Saga: 安排发货"); return s; }, compensate: async (s, ct) => Console.WriteLine("  ↩️ 补偿: 取消物流")));
    }
}

internal static class ECommerceApp
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        // ITM-171：AddPalPipelineBehaviors 的 LoggingBehavior 依赖 IPalLogger 门面，
        // 只调 AddLogging 会在运行时解析管道行为时失败。
        services.AddPalLogging();
        services.AddPalDDD();
        services.AddPalPipelineBehaviors();
        services.AddSingleton<OrderRepo>();
        // ITM-171：处理器经框架显式注册 API（AOT 安全）注册进 DI。
        // 注意：本示例没有 Generic Host，HandlerRegistrar（IHostedService）不会自动运行，
        // 因此下方仍需显式 d.Register 把处理器登记到 Dispatcher（Web 宿主则无需；
        // 替代路径见 AotSample：手动 StartAsync IHostedService 触发自动注册）。
        services.AddPalCommandHandler<AddItemCmd, Unit, AddItemH>();
        services.AddPalCommandHandler<ConfirmCmd, Unit, ConfirmH>();
        services.AddPalQueryHandler<GetOrderQry, OrderDto?, GetOrderH>();
        var sp = services.BuildServiceProvider();
        var d = sp.GetRequiredService<Dispatcher>();
        d.Register<AddItemCmd, Unit, AddItemH>();
        d.Register<ConfirmCmd, Unit, ConfirmH>();
        d.Register<GetOrderQry, OrderDto?, GetOrderH>();

        var repo = sp.GetRequiredService<OrderRepo>();
        var oid = new OrderId(Guid.NewGuid());
        repo.Add(new Order(oid, "张三"));

        Console.WriteLine("═══════════════════════════════════");
        Console.WriteLine("  Pal.DDD E-Commerce 示例");
        Console.WriteLine("═══════════════════════════════════\n");
        Console.WriteLine($"✅ 创建订单: {oid.Value.ToString()[..8]} (客户: 张三)");

        Console.WriteLine("\n── CQRS: 添加商品 ──");
        await d.SendAsync(new AddItemCmd(oid, "机械键盘", 1, Money.CNY(299)));
        await d.SendAsync(new AddItemCmd(oid, "鼠标垫", 2, Money.CNY(49)));
        var o = repo.Get(oid)!;
        Console.WriteLine($"  商品: {o.Items.Count}, 总额: {o.TotalAmount}");

        Console.WriteLine("\n── CQRS: 确认订单 ──");
        await d.SendAsync(new ConfirmCmd(oid));
        Console.WriteLine($"  状态: {o.Status}");

        Console.WriteLine("\n── 领域守卫演示（SMP-103）──");
        try { o.AddItem("零数量", 0, Money.CNY(9)); } catch (ArgumentOutOfRangeException ex) { Console.WriteLine($"  ✅ 数量守卫: {ex.Message.Split('\n')[0]}"); }
        try { o.AddItem("负单价", 1, Money.CNY(-1)); } catch (ArgumentOutOfRangeException ex) { Console.WriteLine($"  ✅ 单价守卫: {ex.Message.Split('\n')[0]}"); }
        try { o.AddItem("美元支付", 1, new Money(1, "USD")); } catch (ArgumentException ex) { Console.WriteLine($"  ✅ 币种守卫: {ex.Message}"); }
        try { o.AddItem("确认后追加", 1, Money.CNY(9)); } catch (InvalidOperationException ex) { Console.WriteLine($"  ✅ 状态机守卫: {ex.Message}"); }

        Console.WriteLine("\n── CQRS: 查询订单 ──");
        var dto = await d.QueryAsync(new GetOrderQry(oid));
        Console.WriteLine($"  客户: {dto!.Customer}, 金额: {dto.Amount:F2}, 商品: {dto.Items} 件");

        Console.WriteLine("\n── Outbox 租约发布 ──");
        var outbox = new InMemoryOutboxStore();
        outbox.AddMessage(new OutboxMessage { Type = "ordering.confirmed.v1", Payload = [1, 2, 3], ContentType = "application/json", SchemaVersion = 1 });
        var msgs = await outbox.LeasePendingMessagesAsync(10, "ecom", TimeSpan.FromMinutes(2), new OutboxOptions().MaxRetryCount, default);
        outbox.MarkProcessed(msgs[0], DateTimeOffset.UtcNow);
        Console.WriteLine($"  待发布: {msgs.Count}, 状态: {msgs[0].Status}");

        Console.WriteLine("\n── Inbox 幂等消费 ──");
        var inbox = new InMemoryInboxStore();
        var now = DateTimeOffset.UtcNow;
        var r1 = await inbox.TryStartProcessingAsync("proj", "msg-1", now, TimeSpan.FromMinutes(5), default);
        var r2 = await inbox.TryStartProcessingAsync("proj", "msg-1", now, TimeSpan.FromMinutes(5), default);
        Console.WriteLine($"  第1次: {(r1 is not null ? "✅ 处理" : "跳过")}  第2次(重复): {(r2 is not null ? "处理" : "✅ 跳过（幂等）")}");

        Console.WriteLine("\n── Saga 履约编排 ──");
        var saga = new FSaga();
        var state = await saga.ProcessEventAsync(new FState { CurrentState = "Started" }, new OrderConfirmed { OrderId = oid.Value, Customer = "张三", Total = Money.CNY(100) }, default);
        Console.WriteLine($"  状态: {state.Status}, 步骤: {state.CurrentState}");

        Console.WriteLine("\n═══════════════════════════════════");
        Console.WriteLine("  演示完成 ✅");
        Console.WriteLine("═══════════════════════════════════");
    }
}
