// ─────────────────────────────────────────────────────────────
// 🔄 MessageEvolutionPipeline — 消息版本升级链（FrozenDictionary O(1)）
// ─────────────────────────────────────────────────────────────
using System.Collections.Frozen;

namespace PalDDD.Serialization.Evolution;

// ─────────────────────────────────────────────────────────────
// 消息版本升级管道
// ─────────────────────────────────────────────────────────────

public sealed class MessageEvolutionPipeline
{
    private readonly FrozenDictionary<Key, MessageUpgradeStep> _steps;

    internal MessageEvolutionPipeline(IEnumerable<MessageUpgradeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        _steps = steps.ToFrozenDictionary(
            step => new Key(step.SourceDescriptor.Name, step.SourceDescriptor.SchemaVersion));
    }

    public object? Upgrade(
        ReadOnlySpan<byte> payload,
        MessageDescriptor sourceDescriptor,
        MessageDescriptor targetDescriptor,
        IMessageSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(targetDescriptor);
        ArgumentNullException.ThrowIfNull(serializer);

        ValidateDescriptors(sourceDescriptor, targetDescriptor);

        var currentDescriptor = sourceDescriptor;
        var current = serializer.Deserialize(payload, currentDescriptor);
        while (currentDescriptor.SchemaVersion < targetDescriptor.SchemaVersion)
        {
            if (current is null)
                return null;

            var step = GetNextStep(currentDescriptor, targetDescriptor);
            current = step.Convert(current);
            currentDescriptor = step.TargetDescriptor;
        }

        return current;
    }

    public void ValidatePath(MessageDescriptor sourceDescriptor, MessageDescriptor targetDescriptor)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(targetDescriptor);

        ValidateDescriptors(sourceDescriptor, targetDescriptor);

        var currentDescriptor = sourceDescriptor;
        while (currentDescriptor.SchemaVersion < targetDescriptor.SchemaVersion)
        {
            currentDescriptor = GetNextStep(currentDescriptor, targetDescriptor).TargetDescriptor;
        }
    }

    private MessageUpgradeStep GetNextStep(
        MessageDescriptor currentDescriptor,
        MessageDescriptor targetDescriptor)
    {
        var key = new Key(currentDescriptor.Name, currentDescriptor.SchemaVersion);
        if (!_steps.TryGetValue(key, out var step))
        {
            throw new MessageEvolutionException(
                $"Message evolution step missing: name '{key.Name}' from version {key.SchemaVersion}. Target version {targetDescriptor.SchemaVersion}.");
        }

        if (step.TargetDescriptor.SchemaVersion > targetDescriptor.SchemaVersion)
        {
            throw new MessageEvolutionException(
                $"Message evolution step overshot: name '{key.Name}' from version {key.SchemaVersion} jumped to version {step.TargetDescriptor.SchemaVersion}, expected target version {targetDescriptor.SchemaVersion}.");
        }

        return step;
    }

    private static void ValidateDescriptors(
        MessageDescriptor sourceDescriptor,
        MessageDescriptor targetDescriptor)
    {
        if (!StringComparer.Ordinal.Equals(sourceDescriptor.Name, targetDescriptor.Name))
            throw new InvalidOperationException("Message evolution requires matching stable wire names.");

        if (sourceDescriptor.SchemaVersion > targetDescriptor.SchemaVersion)
            throw new InvalidOperationException("Cannot evolve a message from a newer schema version to an older version.");
    }

    private readonly record struct Key(string Name, int SchemaVersion);
}
