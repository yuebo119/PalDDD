using Microsoft.Extensions.DependencyInjection;
using PalDDD.CQRS;

namespace PalDDD.DependencyInjection.Tests;

// ═══════════════════════════════════════════════════════════════
// 📡 流式查询处理器注册测试 — AddPalStreamQueryHandler（ADR-030 D7）
// ═══════════════════════════════════════════════════════════════
// 验证计划中 DependencyInjection 侧：注册解析、重复注册快速失败（ITM-220 同族裁决）。

public sealed class StreamQueryRegistrationTests
{
    [Test]
    public async Task AddPalStreamQueryHandler_RegistersResolvableScopedHandler()
    {
        var services = new ServiceCollection();
        services.AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<IStreamQueryHandler<StreamTestQuery, string>>();
        await Assert.That(handler).IsTypeOf<StreamTestHandler>();
        // 具体类型同可解析（端点按接口取，具体注册供直接消费）
        await Assert.That(scope.ServiceProvider.GetRequiredService<StreamTestHandler>())
            .IsTypeOf<StreamTestHandler>();
    }

    [Test]
    public async Task AddPalStreamQueryHandler_WithoutAddPalDDD_AutoEnsuresCoreRegistration()
    {
        // 评审 P2-1 同族：漏调 AddPalDDD 时注册期补齐（幂等）
        var services = new ServiceCollection();
        services.AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>();

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<CQRS.Dispatcher>();
        // HandlerRegistrar hosted service 已注册（internal 类型，按名断言——对齐
        // AddPalCommandHandler 同族测试的行为断言口径，不用 IsNotNull 弱断言）
        await Assert.That(services.Any(sd =>
            sd.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && sd.ImplementationType?.Name == "HandlerRegistrar")).IsTrue();
    }

    [Test]
    public async Task AddPalStreamQueryHandler_ScopedLifetime_DifferentInstancesPerScope()
    {
        var services = new ServiceCollection();
        services.AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>();

        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IStreamQueryHandler<StreamTestQuery, string>>();
        var second = secondScope.ServiceProvider.GetRequiredService<IStreamQueryHandler<StreamTestQuery, string>>();
        await Assert.That(first).IsNotSameReferenceAs(second);
    }

    [Test]
    public async Task AddPalStreamQueryHandler_DifferentHandlersForSameStreamQuery_Throws()
    {
        // ITM-220 同族裁决：同一 (TQuery, TItem) 的两个不同 THandler 属配置错误——
        // 静默覆盖会让后注册者顶替先注册者且无诊断
        var services = new ServiceCollection();
        services.AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>();

        var ex = await Assert.That(() =>
            services.AddPalStreamQueryHandler<StreamTestQuery, string, OtherStreamTestHandler>())
            .Throws<InvalidOperationException>();

        // 消息含两个 Handler 类型名（排障指向性，对齐 AddPalCommandHandler 同族断言）
        await Assert.That(ex?.Message ?? "").Contains(nameof(StreamTestHandler));
        await Assert.That(ex?.Message ?? "").Contains(nameof(OtherStreamTestHandler));
    }

    [Test]
    public async Task AddPalStreamQueryHandler_SameHandlerTwice_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>();

        // 同 Handler 同查询重复注册幂等（防重语义，不抛）
        services.AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IStreamQueryHandler<StreamTestQuery, string>>();
        await Assert.That(handlers.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task AddPalStreamQueryHandler_ManualRegistrationThenDifferentHandler_Throws()
    {
        // 检测面宽于命令/查询的 Marker 哨兵：手工 AddScoped 注册的接口描述符也在扫描面内
        //（Marker 方案只能看到经显式 API 注册的处理器）
        var services = new ServiceCollection();
        services.AddScoped<IStreamQueryHandler<StreamTestQuery, string>, OtherStreamTestHandler>();

        await Assert.That(() =>
            services.AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AddPalStreamQueryHandler_NullServices_Throws()
    {
        await Assert.That(() =>
            ((IServiceCollection)null!).AddPalStreamQueryHandler<StreamTestQuery, string, StreamTestHandler>())
            .Throws<ArgumentNullException>();
    }

    private sealed class StreamTestQuery : IStreamQuery<string>;

    private sealed class StreamTestHandler : IStreamQueryHandler<StreamTestQuery, string>
    {
        public async IAsyncEnumerable<string> HandleAsync(
            StreamTestQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return "a";
            await Task.Yield();
            yield return "b";
        }
    }

    private sealed class OtherStreamTestHandler : IStreamQueryHandler<StreamTestQuery, string>
    {
        public async IAsyncEnumerable<string> HandleAsync(
            StreamTestQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return "other";
            await Task.Yield();
        }
    }
}
