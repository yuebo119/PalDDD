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
        services.TryAddSingleton(_ =>
        {
            var builder = new MessageContractVerificationBuilder();
            configure?.Invoke(builder);
            return builder.BuildPipeline();
        });
        services.AddHostedService<PalPlatformVerificationHostedService>();
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
