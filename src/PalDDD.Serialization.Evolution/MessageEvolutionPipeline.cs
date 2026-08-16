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

        // ITM-166 修复：steps 先物化一次再校验/建字典——原 foreach + ToFrozenDictionary
        // 对同一 IEnumerable 枚举两次：单次序列（如生成器）第二次枚举为空/抛错，
        // 且两次枚举间若序列内容变化（如延迟求值依赖外部状态）会校验一套、建字典另一套。
        var stepList = steps.ToArray();

        // P2 修复：构造期校验升级链严格递增——v1→v2 与 v2→v1 之类的回环注册
        // 会让 Upgrade/ValidatePath 的 while 循环无限乒乓（挂死而非异常）
        foreach (var step in stepList)
        {
            if (step.TargetDescriptor.SchemaVersion <= step.SourceDescriptor.SchemaVersion)
                throw new MessageEvolutionException(
                    $"升级步骤 {step.SourceDescriptor.Name} v{step.SourceDescriptor.SchemaVersion}→v{step.TargetDescriptor.SchemaVersion} "
                    + "必须严格递增：回环/退化注册会导致升级死循环。");
        }

        _steps = stepList.ToFrozenDictionary(
            step => new Key(step.SourceDescriptor.Name, step.SourceDescriptor.SchemaVersion));

        // P3 修复（二十一轮）：相邻步 ClrType 衔接校验（构造期 fail-fast）——升级链按
        // (Name, SourceSchemaVersion) 键衔接，相邻两步 A→B 要求 A.TargetDescriptor.ClrType
        // 与 B.SourceDescriptor.ClrType 一致：断裂链（同版本由不同 CLR 类型接棒）此前仅在
        // Upgrade 执行期以 Convert 内的 InvalidCastException（或静默错误转换）暴露。
        // _steps 字典内信息已充分（后继步可按 A 的 target 键查得），构造期即校验；
        // 末步（target 无后继）不参与本检查。
        foreach (var step in _steps.Values)
        {
            var targetKey = new Key(step.TargetDescriptor.Name, step.TargetDescriptor.SchemaVersion);
            if (_steps.TryGetValue(targetKey, out var next)
                && next.SourceDescriptor.ClrType != step.TargetDescriptor.ClrType)
            {
                throw new MessageEvolutionException(
                    $"Message evolution chain broken: name '{targetKey.Name}' version {targetKey.SchemaVersion} "
                    + $"is produced as CLR type '{GetTypeName(step.TargetDescriptor.ClrType)}' "
                    + $"but consumed by the next step as '{GetTypeName(next.SourceDescriptor.ClrType)}'.");
            }
        }
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

    private static string GetTypeName(Type type) => type.FullName ?? type.Name;

    private readonly record struct Key(string Name, int SchemaVersion);
}
