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

        // v41 P3（ITM-280 收尾）：ClrType 失配腿对齐注册期异常统一——原抛 ArgumentException，
        // 与 MessageUpgradeStep 的 wire name/版本腿、Builder 重复键腿分叉，消费者无法单点
        // catch MessageEvolutionException 覆盖全部注册期错误；参数信息（原 nameof 参数名 +
        // 期望/实际类型）保留在消息中
        if (sourceDescriptor.ClrType != typeof(TSource))
            throw new MessageEvolutionException(
                $"Source descriptor CLR type does not match converter source type (parameter 'sourceDescriptor': expected {typeof(TSource).FullName}, actual {sourceDescriptor.ClrType.FullName}).");

        if (targetDescriptor.ClrType != typeof(TTarget))
            throw new MessageEvolutionException(
                $"Target descriptor CLR type does not match converter target type (parameter 'targetDescriptor': expected {typeof(TTarget).FullName}, actual {targetDescriptor.ClrType.FullName}).");

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
