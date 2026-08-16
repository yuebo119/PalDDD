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
        // 跑多遍启动验证且 DI 中残留重复 IHostedService）。TryAddEnumerable 按
        // ServiceType+ImplementationType 对去重，一次注册、幂等重入。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PalPlatformVerificationHostedService>());
        return services;
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Hosted service 由 Microsoft.Extensions.DependencyInjection 通过 AddHostedService 实例化。")]
internal sealed class PalPlatformVerificationHostedService : IHostedService
{
    private readonly IMessageCatalog _messageCatalog;
    private readonly MessageEvolutionPipeline _messageEvolutionPipeline;
    private readonly PalPlatformVerifier _verifier;

    public PalPlatformVerificationHostedService(
        IMessageCatalog messageCatalog,
        MessageEvolutionPipeline messageEvolutionPipeline,
        PalPlatformVerifier verifier)
    {
        _messageCatalog = messageCatalog;
        _messageEvolutionPipeline = messageEvolutionPipeline;
        _verifier = verifier;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var manifest = MessageContractManifest.Create(_messageCatalog);
        _verifier.ValidateMessageContractManifest(_messageEvolutionPipeline, manifest);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
