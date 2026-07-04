namespace PalDDD.Serialization.Evolution;

// ─────────────────────────────────────────────────────────────
// 单步版本升级
// ─────────────────────────────────────────────────────────────

public sealed class MessageUpgradeStep
{
    private readonly Func<object, object> _convert;

    public MessageUpgradeStep(
        MessageDescriptor sourceDescriptor,
        MessageDescriptor targetDescriptor,
        Func<object, object> convert)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(targetDescriptor);
        ArgumentNullException.ThrowIfNull(convert);

        if (!StringComparer.Ordinal.Equals(sourceDescriptor.Name, targetDescriptor.Name))
            throw new ArgumentException("Upgrade steps must keep the same stable wire name.", nameof(targetDescriptor));

        if (targetDescriptor.SchemaVersion <= sourceDescriptor.SchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(targetDescriptor), "Target schema version must be greater than source schema version.");

        SourceDescriptor = sourceDescriptor;
        TargetDescriptor = targetDescriptor;
        _convert = convert;
    }

    public MessageDescriptor SourceDescriptor { get; }

    public MessageDescriptor TargetDescriptor { get; }

    public object Convert(object message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return _convert(message);
    }
}
