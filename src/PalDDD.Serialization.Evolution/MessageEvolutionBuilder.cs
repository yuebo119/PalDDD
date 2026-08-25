namespace PalDDD.Serialization.Evolution;

// ─────────────────────────────────────────────────────────────
// 升级规则构建器
// ─────────────────────────────────────────────────────────────

public sealed class MessageEvolutionBuilder
{
    private readonly Dictionary<MessageVersionKey, MessageUpgradeStep> _steps = [];

    public MessageEvolutionBuilder Add<TSource, TTarget>(
        MessageDescriptor sourceDescriptor,
        MessageDescriptor targetDescriptor,
        Func<TSource, TTarget> convert)
        where TSource : notnull
        where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(targetDescriptor);
        ArgumentNullException.ThrowIfNull(convert);

        if (sourceDescriptor.ClrType != typeof(TSource))
            throw new ArgumentException("Source descriptor CLR type does not match converter source type.", nameof(sourceDescriptor));

        if (targetDescriptor.ClrType != typeof(TTarget))
            throw new ArgumentException("Target descriptor CLR type does not match converter target type.", nameof(targetDescriptor));

        Add(new MessageUpgradeStep(
            sourceDescriptor,
            targetDescriptor,
            message => convert((TSource)message)!));
        return this;
    }

    public MessageEvolutionBuilder Add(MessageUpgradeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var key = new MessageVersionKey(step.SourceDescriptor.Name, step.SourceDescriptor.SchemaVersion);
        if (!_steps.TryAdd(key, step))
        {
            // ITM-280（R44）：对齐 MessageEvolutionPipeline 的重复键异常类型——原抛 InvalidOperationException
            // 使消费者无法单点 catch MessageEvolutionException 覆盖全部注册期错误（Builder 是链式主入口）
            throw new MessageEvolutionException(
                $"Message upgrade step '{key.Name}' v{key.SchemaVersion} is already registered.");
        }

        return this;
    }

    public MessageEvolutionPipeline Build() => new(_steps.Values);


}
