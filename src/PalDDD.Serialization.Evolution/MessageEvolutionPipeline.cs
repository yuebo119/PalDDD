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
    private readonly FrozenDictionary<MessageVersionKey, MessageUpgradeStep> _steps;

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

        // P3-SRC-304 修复：重复键抛 MessageEvolutionException——原 ToFrozenDictionary 对重复
        // (Name, SourceSchemaVersion) 键抛 BCL ArgumentException（"An item with the same key..."），
        // 与本构造器其余校验（严格递增/ClrType 衔接）的异常类型分叉，消费者无法单点 catch
        // MessageEvolutionException 覆盖全部注册期错误。显式检测后带键信息抛出。
        var seed = new Dictionary<MessageVersionKey, MessageUpgradeStep>(stepList.Length);
        foreach (var step in stepList)
        {
            var key = new MessageVersionKey(step.SourceDescriptor.Name, step.SourceDescriptor.SchemaVersion);
            if (!seed.TryAdd(key, step))
                throw new MessageEvolutionException(
                    $"Duplicate message evolution step: name '{key.Name}' from version {key.SchemaVersion} is registered more than once.");
        }
        _steps = seed.ToFrozenDictionary();

        // P3 修复（二十一轮）：相邻步 ClrType 衔接校验（构造期 fail-fast）——升级链按
        // (Name, SourceSchemaVersion) 键衔接，相邻两步 A→B 要求 A.TargetDescriptor.ClrType
        // 与 B.SourceDescriptor.ClrType 一致：断裂链（同版本由不同 CLR 类型接棒）此前仅在
        // Upgrade 执行期以 Convert 内的 InvalidCastException（或静默错误转换）暴露。
        // _steps 字典内信息已充分（后继步可按 A 的 target 键查得），构造期即校验；
        // 末步（target 无后继）不参与本检查。
        foreach (var step in _steps.Values)
        {
            var targetKey = new MessageVersionKey(step.TargetDescriptor.Name, step.TargetDescriptor.SchemaVersion);
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
            // v30 P3：source 端类型哨兵（镜像 v18 D2 target 端形态）——descriptor 等价性按
            // (Name,Version) 判定，调用方传入的 sourceDescriptor 可与注册第一步同名同版本但
            // ClrType 不同：Deserialize 按传入 descriptor 的 JsonTypeInfo 产出对象，而
            // step.Convert 期望注册 SourceClrType 的实例——错配静默传到 Convert 的强转才炸
            // InvalidCastException（或更糟：结构兼容时静默错误转换）。仅第一步需要本哨兵：
            // 后续步 currentDescriptor 来自 step.TargetDescriptor（注册对象），与下一步
            // Source 的衔接已由构造期校验覆盖。ReferenceEquals 判定第一步（循环从
            // sourceDescriptor 起步，首轮必真；后续轮 currentDescriptor 已被改写为注册
            // TargetDescriptor——即便与传入 sourceDescriptor 同引用，ClrType 相等性由构造期
            // 校验保证，哨兵无假阳性）
            if (ReferenceEquals(currentDescriptor, sourceDescriptor)
                && step.SourceDescriptor.ClrType != sourceDescriptor.ClrType)
                throw new MessageEvolutionException(
                    $"Source descriptor ClrType mismatch: expected {step.SourceDescriptor.ClrType}, got {sourceDescriptor.ClrType}.");
            current = step.Convert(current);
            // v40 P3：中间步 converter 返回 null 禁止静默传播——原实现 null 流入下一轮
            // 循环头的 `if (current is null) return null`，与"首轮 payload 反序列化为
            // null"（空/null JSON 输入，合法空结果）的语义混流，converter 缺陷（如条件
            // 分支漏 return）被吞成静默 null 返回。走到本行的 current 必非 null（循环头
            // 哨兵已拦截首轮），null 只能来自 step.Convert 本身 → fail-fast
            if (current is null)
                throw new MessageEvolutionException(
                    $"Message evolution step returned null: name '{step.TargetDescriptor.Name}' "
                    + $"version {step.TargetDescriptor.SchemaVersion}.");
            currentDescriptor = step.TargetDescriptor;
        }

        // v18 D2：末步产出类型哨兵——descriptor 等价性按 (Name,Version) 判定，ClrType 不同的
        // 描述符可互换传入使产出对象静默错配、下游 cast 才炸。构造期衔接校验只覆盖 _steps
        // 内部相邻步，此处补齐"调用方传入 targetDescriptor"这一入口。
        if (targetDescriptor.ClrType != currentDescriptor.ClrType)
            throw new MessageEvolutionException(
                $"Target descriptor ClrType mismatch: expected {currentDescriptor.ClrType}, got {targetDescriptor.ClrType}.");

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
        var key = new MessageVersionKey(currentDescriptor.Name, currentDescriptor.SchemaVersion);
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
        // v18 D1：运行期校验统一 MessageEvolutionException（原裸 InvalidOperationException 因
        // 不继承前者使 catch(MessageEvolutionException) 的"单点 catch"意图在运行期断裂）
        if (!StringComparer.Ordinal.Equals(sourceDescriptor.Name, targetDescriptor.Name))
            throw new MessageEvolutionException("Message evolution requires matching stable wire names.");

        if (sourceDescriptor.SchemaVersion > targetDescriptor.SchemaVersion)
            throw new MessageEvolutionException("Cannot evolve a message from a newer schema version to an older version.");
    }

    private static string GetTypeName(Type type) => type.FullName ?? type.Name;


}

/// <summary>消息版本标识（Name, SchemaVersion）——Pipeline 与 Builder 原各自嵌套同型 MessageVersionKey
/// 双拷（精炼提取 2026-08-26），包内共享。</summary>
internal readonly record struct MessageVersionKey(string Name, int SchemaVersion);

