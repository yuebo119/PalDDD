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

        // v41 P3（ITM-280 收尾）：注册期异常统一为 MessageEvolutionException（单点 catch 契约）——
        // 原抛 ArgumentException/ArgumentOutOfRangeException 与 Builder 重复键腿分叉，消费者需
        // 多类型 catch 才能覆盖全部注册期错误；参数信息（原 nameof 参数名）保留在消息中
        if (!StringComparer.Ordinal.Equals(sourceDescriptor.Name, targetDescriptor.Name))
            throw new MessageEvolutionException(
                $"Upgrade steps must keep the same stable wire name (parameter 'targetDescriptor': source '{sourceDescriptor.Name}', target '{targetDescriptor.Name}').");

        if (targetDescriptor.SchemaVersion <= sourceDescriptor.SchemaVersion)
            throw new MessageEvolutionException(
                $"Target schema version must be greater than source schema version (parameter 'targetDescriptor': target v{targetDescriptor.SchemaVersion}, source v{sourceDescriptor.SchemaVersion}).");

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
