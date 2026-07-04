using System.Collections.Frozen;
using System.Text.Json.Serialization.Metadata;

namespace PalDDD.Serialization;

// ─────────────────────────────────────────────────────────────
// 只读消息元数据目录
// ─────────────────────────────────────────────────────────────

/// <summary>只读、AOT 友好的消息元数据目录。</summary>
public interface IMessageCatalog
{
    /// <summary>此目录中注册的全部描述符，按插入顺序排列。</summary>
    IReadOnlyList<MessageDescriptor> Descriptors { get; }

    /// <summary>按稳定线传输名称查找描述符。</summary>
    MessageDescriptor? Find(string name);

    /// <summary>按稳定线传输名称和 Schema 版本查找描述符。</summary>
    MessageDescriptor? Find(string name, int schemaVersion);

    /// <summary>按 CLR 类型查找描述符。</summary>
    MessageDescriptor? Find(Type type);
}

/// <summary>不可变、AOT 友好的消息元数据目录。</summary>
/// <remarks>
/// 内部使用 <c>OrderedDictionary</c> 在构建期保持注册顺序（Phase B1），<br/>
/// 运行时通过三个 <see cref="FrozenDictionary"/> 实现 O(1) 查找。
/// </remarks>
public sealed class MessageCatalog : IMessageCatalog
{
    internal readonly struct NameVersionKey(string name, int schemaVersion)
        : IEquatable<NameVersionKey>
    {
        public string Name { get; } = name;
        public int SchemaVersion { get; } = schemaVersion;

        public readonly bool Equals(NameVersionKey other)
            => Name == other.Name && SchemaVersion == other.SchemaVersion;

        public override readonly bool Equals(object? obj)
            => obj is NameVersionKey other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(Name, SchemaVersion);

        public static bool operator ==(NameVersionKey left, NameVersionKey right) => left.Equals(right);

        public static bool operator !=(NameVersionKey left, NameVersionKey right) => !left.Equals(right);
    }

    private readonly FrozenDictionary<NameVersionKey, MessageDescriptor> _byNameAndVersion;
    private readonly FrozenDictionary<string, MessageDescriptor> _latestByName;
    private readonly FrozenDictionary<Type, MessageDescriptor> _byType;

    /// <summary>空目录，供仅使用显式描述符的应用使用。</summary>
    public static MessageCatalog Empty { get; } = new([]);

    internal MessageCatalog(IReadOnlyList<MessageDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        // 保留传入顺序（由 MessageCatalogBuilder 通过 OrderedDictionary 维护）
        Descriptors = descriptors;
        _byNameAndVersion = descriptors.ToFrozenDictionary(
            d => new NameVersionKey(d.Name, d.SchemaVersion));
        _latestByName = descriptors
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => group.MaxBy(d => d.SchemaVersion)!,
                StringComparer.Ordinal);
        _byType = descriptors.ToFrozenDictionary(d => d.ClrType);
    }

    /// <inheritdoc />
    public IReadOnlyList<MessageDescriptor> Descriptors { get; }

    /// <inheritdoc />
    public MessageDescriptor? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _latestByName.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public MessageDescriptor? Find(string name, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        return _byNameAndVersion.GetValueOrDefault(new NameVersionKey(name, schemaVersion));
    }

    /// <inheritdoc />
    public MessageDescriptor? Find(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _byType.GetValueOrDefault(type);
    }
}

/// <summary>启动期不可变消息目录构建器。</summary>
/// <remarks>
/// 使用 <c>OrderedDictionary&lt;TKey, TValue&gt;</c>（.NET 11 Preview 4）维护注册顺序，<br/>
/// 确保 <see cref="Build"/> 后的 <see cref="IMessageCatalog.Descriptors"/> 按 Add 调用顺序枚举。
/// </remarks>
public sealed class MessageCatalogBuilder
{
    // OrderedDictionary 保持注册顺序（Phase B1）
    private readonly OrderedDictionary<MessageCatalog.NameVersionKey, MessageDescriptor> _byNameAndVersion = [];

    private readonly Dictionary<Type, MessageDescriptor> _byType = [];

    /// <summary>使用源生成 JSON 元数据添加消息类型。</summary>
    public MessageDescriptor Add<TMessage>(
        JsonTypeInfo<TMessage> jsonTypeInfo,
        string? name = null,
        int schemaVersion = 1,
        string contentType = ContentTypes.Json)
    {
        var descriptor = MessageDescriptor.Create(jsonTypeInfo, name, schemaVersion, contentType);
        Add(descriptor);
        return descriptor;
    }

    /// <summary>将描述符添加到正在构建的目录中。</summary>
    public MessageCatalogBuilder Add(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var key = new MessageCatalog.NameVersionKey(descriptor.Name, descriptor.SchemaVersion);
        if (!_byNameAndVersion.TryAdd(key, descriptor))
        {
            throw new InvalidOperationException(
                $"Message name '{descriptor.Name}' with schema version {descriptor.SchemaVersion} is already registered.");
        }

        if (!_byType.TryAdd(descriptor.ClrType, descriptor))
        {
            throw new InvalidOperationException(
                $"Message CLR type '{GetTypeName(descriptor.ClrType)}' is already registered.");
        }

        return this;
    }

    /// <summary>构建不可变运行时目录，保留注册顺序。</summary>
    public MessageCatalog Build()
    {
        List<MessageDescriptor> ordered = new(_byNameAndVersion.Count);
        foreach (var kv in _byNameAndVersion)
            ordered.Add(kv.Value);
        return new MessageCatalog(ordered.AsReadOnly());
    }

    private static string GetTypeName(Type type) => type.FullName ?? type.Name;
}
