using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace PalDDD.Serialization.Evolution;

/// <summary>消息契约演化验证的 DI 注册。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册启动期对基于 catalog 的消息契约演化路径的验证。</summary>
    public static IServiceCollection AddPalMessageContractVerification(
        this IServiceCollection services,
        Action<MessageContractVerificationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<PalPlatformVerifier>();
        // P2 修复（二十一轮）：TryAdd → AddSingleton——与十七轮 P1（IMessageCatalog）同型：
        // TryAdd 先到先得使多模块各自调 AddPalMessageContractVerification(cfg) 时后续
        // configure 回调被静默丢弃。后注册者生效与 Serialization 两侧替换语义统一；
        // 自定义 pipeline 须在本扩展【之后】注册（同 catalog 顺序契约）。
        services.AddSingleton(_ =>
        {
            var builder = new MessageContractVerificationBuilder();
            configure?.Invoke(builder);
            return builder.BuildPipeline();
        });
        // ITM-167 修复：AddHostedService → TryAddEnumerable——pipeline 注册保留
        // AddSingleton（后注册者覆盖，多模块各自 configure 的语义见十七轮修复）；
        // hosted service 是验证执行器，多次调用本扩展不应重复启动验证（重复注册会
        // 跑多遍启动验证且 DI 中残留重复 IHostedService）。
        // R13 F24：改为工厂注册 + 内部标记去重——catalog 在注册本扩展时可能尚未注册
        // （顺序无关），不能作为强制构造依赖；catalog 缺失改由 StartAsync 抛可行动
        // 异常（原实现让 DI 在解析 hosted service 时抛通用 "Unable to resolve"）。
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(PalPlatformVerificationMarker)))
        {
            services.AddSingleton<PalPlatformVerificationMarker>();
            services.AddSingleton<IHostedService>(static sp => new PalPlatformVerificationHostedService(
                sp.GetService<IMessageCatalog>(),
                sp.GetRequiredService<MessageEvolutionPipeline>(),
                sp.GetRequiredService<PalPlatformVerifier>()));
        }

        return services;
    }
}

/// <summary>R13 F24：契约验证 hosted service 的幂等标记（工厂注册下 TryAddEnumerable 不适用）。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "DI 容器经 AddSingleton<PalPlatformVerificationMarker>() 实例化，仅作注册幂等标记。")]
internal sealed class PalPlatformVerificationMarker;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Hosted service 由 DI 容器经工厂注册实例化，宿主启动时按 IHostedService 服务类型解析。")]
internal sealed class PalPlatformVerificationHostedService : IHostedService
{
    private readonly IMessageCatalog? _messageCatalog;
    private readonly MessageEvolutionPipeline _messageEvolutionPipeline;
    private readonly PalPlatformVerifier _verifier;

    public PalPlatformVerificationHostedService(
        IMessageCatalog? messageCatalog,
        MessageEvolutionPipeline messageEvolutionPipeline,
        PalPlatformVerifier verifier)
    {
        _messageCatalog = messageCatalog;
        _messageEvolutionPipeline = messageEvolutionPipeline;
        _verifier = verifier;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // R13 F24：catalog 未注册时给出可行动诊断（点名注册入口与顺序约束），
        // 而不是 DI 的通用 "Unable to resolve service for type IMessageCatalog"。
        var catalog = _messageCatalog
            ?? throw new InvalidOperationException(
                "Message contract verification is enabled (AddPalMessageContractVerification) but IMessageCatalog is not registered. "
                + "Register the catalog with AddPalJsonSerialization(...) or AddPalMemoryPackSerialization(...) before building the host, "
                + "or remove AddPalMessageContractVerification().");

        var manifest = MessageContractManifest.Create(catalog);
        _verifier.ValidateMessageContractManifest(_messageEvolutionPipeline, manifest);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
