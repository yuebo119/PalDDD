namespace PalDDD.Serialization.Evolution;

/// <summary>已注册消息合约及其所需演进路径的不可变启动清单。</summary>
public sealed class MessageContractManifest
{
    private MessageContractManifest(
        IReadOnlyList<MessageDescriptor> descriptors,
        IReadOnlyList<MessageEvolutionPathRequirement> evolutionRequirements)
    {
        Descriptors = descriptors;
        EvolutionRequirements = evolutionRequirements;
    }

    /// <summary>清单中包含的所有消息描述符。</summary>
    public IReadOnlyList<MessageDescriptor> Descriptors { get; }

    /// <summary>启动完成前必须验证有效的相邻 Schema 演进路径。</summary>
    public IReadOnlyList<MessageEvolutionPathRequirement> EvolutionRequirements { get; }

    /// <summary>从不可变消息目录创建合约清单。</summary>
    public static MessageContractManifest Create(IMessageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var descriptors = catalog.Descriptors
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.SchemaVersion)
            .ToArray();

        List<MessageEvolutionPathRequirement>? requirements = null;
        foreach (var group in descriptors.GroupBy(descriptor => descriptor.Name, StringComparer.Ordinal))
        {
            MessageDescriptor? previous = null;
            foreach (var descriptor in group)
            {
                if (previous is not null)
                {
                    requirements ??= [];
                    requirements.Add(new MessageEvolutionPathRequirement(previous, descriptor));
                }

                previous = descriptor;
            }
        }

        return new MessageContractManifest(descriptors, requirements ?? []);
    }
}
