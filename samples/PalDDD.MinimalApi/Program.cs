using PalDDD.Core;
using PalDDD.CQRS;
using PalDDD.DependencyInjection;
using PalDDD.Hosting.AspNetCore;
using PalDDD.MinimalApi;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddLogging();
        builder.Services.AddPalDDD();
        builder.Services.AddPalPipelineBehaviors();
        builder.Services.AddSingleton<OrderRepo>();
        builder.Services.AddSingleton<CreateOrderH>(); builder.Services.AddSingleton<ICommandHandler<CreateOrderCmd, OrderId>, CreateOrderH>();
        builder.Services.AddSingleton<AddItemH>(); builder.Services.AddSingleton<ICommandHandler<AddItemCmd, Unit>, AddItemH>();
        builder.Services.AddSingleton<GetOrderH>(); builder.Services.AddSingleton<IQueryHandler<GetOrderQry, OrderDto?>, GetOrderH>();

        var app = builder.Build();
        app.UsePalExceptionHandler();
        app.MapPalHealthChecks();

        var d = app.Services.GetRequiredService<Dispatcher>();
        d.Register<CreateOrderCmd, OrderId, CreateOrderH>();
        d.Register<AddItemCmd, Unit, AddItemH>();
        d.Register<GetOrderQry, OrderDto?, GetOrderH>();

        app.MapCommand<CreateOrderCmd, OrderId>("/orders", AppJsonContext.Default.CreateOrderCmd, AppJsonContext.Default.OrderId);
        app.MapCommand<AddItemCmd>("/orders/{id}/items", AppJsonContext.Default.AddItemCmd);
        app.MapQuery<GetOrderQry, OrderDto?>("/orders/{id}", ctx => new GetOrderQry(new OrderId(Guid.Parse((string)ctx.Request.RouteValues["id"]!))), AppJsonContext.Default.OrderDto);
        app.MapGet("/", () => Results.Ok(new { App = "Pal.DDD Minimal API", Routes = new[] { "POST /orders", "POST /orders/{id}/items", "GET /orders/{id}", "GET /health" } }));

        Console.WriteLine("🚀 Pal.DDD Minimal API → http://localhost:5000");
        app.Run();
    }
}
