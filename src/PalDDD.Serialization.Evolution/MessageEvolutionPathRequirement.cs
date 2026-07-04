namespace PalDDD.Serialization.Evolution;

/// <summary>声明某条消息 Schema 演进路径在启动时必须可用。</summary>
public sealed record MessageEvolutionPathRequirement
{
    /// <summary>创建一条必需的消息演进路径。</summary>
    public MessageEvolutionPathRequirement(
        MessageDescriptor sourceDescriptor,
        MessageDescriptor targetDescriptor)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(targetDescriptor);

        SourceDescriptor = sourceDescriptor;
        TargetDescriptor = targetDescriptor;
    }

    /// <summary>必须可升级的源 Schema 描述符。</summary>
    public MessageDescriptor SourceDescriptor { get; }

    /// <summary>必须可从源 Schema 到达的目标 Schema 描述符。</summary>
    public MessageDescriptor TargetDescriptor { get; }
}
