using PalDDD.Core;
using PalDDD.DependencyInjection;
using PalDDD.Hosting.AspNetCore;
using PalDDD.MinimalApi;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        // ITM-171：AddPalPipelineBehaviors 的 LoggingBehavior 依赖 IPalLogger 门面——
        // 只调 AddLogging 会在运行时解析管道行为时失败。
        builder.Services.AddPalLogging();
        builder.Services.AddPalDDD();
        builder.Services.AddPalPipelineBehaviors();
        // ITM-155：MapPalHealthChecks 前置要求先注册，否则 /health 返回 500
        builder.Services.AddPalHealthChecks();
        builder.Services.AddSingleton<OrderRepo>();
        // ITM-171：处理器经框架显式注册 API（AOT 安全），HandlerRegistrar 会在宿主启动时
        // 自动把 HandlerMarker 注册进 Dispatcher——无需再手写 d.Register。
        builder.Services.AddPalCommandHandler<CreateOrderCmd, OrderId, CreateOrderH>();
        builder.Services.AddPalCommandHandler<AddItemCmd, Unit, AddItemH>();
        builder.Services.AddPalQueryHandler<GetOrderQry, OrderDto?, GetOrderH>();

        var app = builder.Build();
        app.UsePalExceptionHandler();
        app.MapPalHealthChecks();

        app.MapCommand<CreateOrderCmd, OrderId>("/orders", AppJsonContext.Default.CreateOrderCmd, AppJsonContext.Default.OrderId);
        // ITM-156：/orders/{id}/items 的 {id} 从未被读取——AddItemCmd 的 OrderId 来自 body；
        // 路由改为 /orders/items（body 传 OrderId），路由清单同步。
        app.MapCommand<AddItemCmd>("/orders/items", AppJsonContext.Default.AddItemCmd);
        // 示例从路由值直接 Parse：非法 Guid 会抛 FormatException 并经全局异常中间件映射为 500。
        // 生产代码应 Guid.TryParse 并在失败时返回 400（或走 RouteValues 绑定 + 验证管道）。
        app.MapQuery<GetOrderQry, OrderDto?>("/orders/{id}", ctx => new GetOrderQry(new OrderId(Guid.Parse((string)ctx.Request.RouteValues["id"]!))), AppJsonContext.Default.OrderDto);
        app.MapGet("/", () => Results.Ok(new { App = "Pal.DDD Minimal API", Routes = new[] { "POST /orders", "POST /orders/items", "GET /orders/{id}", "GET /health" } }));

        Console.WriteLine("🚀 Pal.DDD Minimal API 已启动（实际监听地址见 launchSettings.json 或下方 ASP.NET 日志）");
        app.Run();
    }
}
