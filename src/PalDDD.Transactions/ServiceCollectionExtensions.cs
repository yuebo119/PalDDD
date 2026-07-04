// ─────────────────────────────────────────────────────────────
// 🔧 DI 注册 — AddPalOutbox / AddPalInbox / AddPalSaga 等
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

/// <summary>事务基础设施注册。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册发件箱发布器。调用方还需注册 <see cref="Serialization.IMessageSerializer"/>、<see cref="Serialization.IMessageCatalog"/> 和 <see cref="IPalOutboxStore"/>。</summary>
    public static IServiceCollection AddPalOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OutboxOptions>()
            .Validate(static options => options.BatchSize > 0, "Outbox batch size must be greater than zero.")
            .Validate(static options => options.LeaseDuration > TimeSpan.Zero, "Outbox lease duration must be greater than zero.")
            .Validate(static options => options.PollInterval > TimeSpan.Zero, "Outbox poll interval must be greater than zero.")
            .Validate(static options => options.MaxRetryCount > 0, "Outbox max retry count must be greater than zero.")
            .Validate(static options => options.MaxRetryDelay > TimeSpan.Zero, "Outbox max retry delay must be greater than zero.")
            .ValidateOnStart();
        services.TryAddScoped<OutboxBatchProcessor>();
        services.AddHostedService<OutboxProcessor>();
        return services;
    }

    /// <summary>注册收件箱幂等性处理器。</summary>
    public static IServiceCollection AddPalInbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<InboxOptions>()
            .Validate(static options => !string.IsNullOrWhiteSpace(options.DefaultConsumerName), "Inbox default consumer name is required.")
            .Validate(static options => options.ProcessingTimeout > TimeSpan.Zero, "Inbox processing timeout must be greater than zero.")
            .ValidateOnStart();
        services.TryAddScoped<InboxProcessor>();
        return services;
    }

    /// <summary>注册 Saga 编排器和超时处理器。</summary>
    public static IServiceCollection AddPalSaga<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.NonPublicConstructors
            | DynamicallyAccessedMemberTypes.PublicFields
            | DynamicallyAccessedMemberTypes.NonPublicFields
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.Interfaces)]
    TState,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TOrchestrator>(
        this IServiceCollection services)
        where TState : SagaState, new()
        where TOrchestrator : Saga<TState>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SagaProcessorOptions>()
            .Validate(static options => options.PollInterval > TimeSpan.Zero, "Saga poll interval must be greater than zero.")
            .Validate(static options => options.TimeoutScanBatchSize > 0, "Saga timeout scan batch size must be greater than zero.")
            .ValidateOnStart();
        services.TryAddSingleton<TOrchestrator>();
        services.TryAddSingleton<Saga<TState>>(sp => sp.GetRequiredService<TOrchestrator>());
        services.TryAddScoped<SagaTimeoutProcessor<TState>>();
        services.AddHostedService<SagaProcessor<TState>>();
        return services;
    }
}
