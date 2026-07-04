namespace PalDDD.Serialization.Evolution;

/// <summary>启动期消息合约验证规则构建器。</summary>
public sealed class MessageContractVerificationBuilder
{
    private readonly MessageEvolutionBuilder _evolutionBuilder = new();

    /// <summary>使用源生成 JSON 元数据添加 Schema 演进步骤。</summary>
    public MessageContractVerificationBuilder Add<TSource, TTarget>(
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TSource> sourceJsonTypeInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TTarget> targetJsonTypeInfo,
        Func<TSource, TTarget> convert,
        string? name = null,
        int sourceSchemaVersion = 1,
        int targetSchemaVersion = 2,
        string contentType = ContentTypes.Json)
        where TSource : notnull
        where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(sourceJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(targetJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(convert);

        var sourceDescriptor = MessageDescriptor.Create(
            sourceJsonTypeInfo,
            name,
            sourceSchemaVersion,
            contentType);
        var targetDescriptor = MessageDescriptor.Create(
            targetJsonTypeInfo,
            name ?? sourceDescriptor.Name,
            targetSchemaVersion,
            contentType);

        return Add(sourceDescriptor, targetDescriptor, convert);
    }

    /// <summary>使用显式描述符添加 Schema 演进步骤。</summary>
    public MessageContractVerificationBuilder Add<TSource, TTarget>(
        MessageDescriptor sourceDescriptor,
        MessageDescriptor targetDescriptor,
        Func<TSource, TTarget> convert)
        where TSource : notnull
        where TTarget : notnull
    {
        _evolutionBuilder.Add(sourceDescriptor, targetDescriptor, convert);
        return this;
    }

    internal MessageEvolutionPipeline BuildPipeline() => _evolutionBuilder.Build();
}
