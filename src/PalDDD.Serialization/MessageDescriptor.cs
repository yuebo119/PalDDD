// ─────────────────────────────────────────────────────────────
// 🏷️ MessageDescriptor — 消息元数据（Name/Schema/ContentType/JsonTypeInfo）
// ─────────────────────────────────────────────────────────────
using System.Text.Json.Serialization.Metadata;

namespace PalDDD.Serialization;

// ─────────────────────────────────────────────────────────────
// 消息描述符
// ─────────────────────────────────────────────────────────────

/// <summary>描述消息类型及其序列化元数据。</summary>
/// <remarks>
/// 这是一个 sealed class 而非 record，以防止 <c>with</c> 表达式
/// 绕过构造函数中强制执行的 <c>jsonTypeInfo.Type == clrType</c> 不变式。
/// 相等性基于 <see cref="Name"/> 和 <see cref="SchemaVersion"/>。
/// </remarks>
public sealed class MessageDescriptor : IEquatable<MessageDescriptor>
{
    public string Name { get; }
    public Type ClrType { get; }
    public JsonTypeInfo JsonTypeInfo { get; }
    public int SchemaVersion { get; }
    public string ContentType { get; }

    public MessageDescriptor(
        string name,
        Type clrType,
        JsonTypeInfo jsonTypeInfo,
        int schemaVersion = 1,
        string contentType = ContentTypes.Json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);

        if (jsonTypeInfo.Type != clrType)
        {
            throw new ArgumentException(
                $"JsonTypeInfo describes '{GetTypeName(jsonTypeInfo.Type)}', but descriptor CLR type is '{GetTypeName(clrType)}'.",
                nameof(jsonTypeInfo));
        }

        Name = name;
        ClrType = clrType;
        JsonTypeInfo = jsonTypeInfo;
        SchemaVersion = schemaVersion;
        ContentType = contentType;
    }

    /// <summary>为 <typeparamref name="TMessage"/> 创建描述符。</summary>
    public static MessageDescriptor Create<TMessage>(
        JsonTypeInfo<TMessage> jsonTypeInfo,
        string? name = null,
        int schemaVersion = 1,
        string contentType = ContentTypes.Json)
        => new(name ?? typeof(TMessage).Name, typeof(TMessage), jsonTypeInfo, schemaVersion, contentType);

    // ── Equality by (Name, SchemaVersion) ──

    /// <summary>
    /// 基于 <see cref="Name"/> 和 <see cref="SchemaVersion"/> 的相等比较器。<br/>
    /// 使用 .NET 11 的 <c>EqualityComparer&lt;T&gt;.Create()</c> 构造，
    /// 避免自定义 <c>IEquatable</c> 和 <c>GetHashCode</c> 的虚方法分派开销。
    /// </summary>
    public static EqualityComparer<MessageDescriptor> NameAndVersionComparer { get; } =
        EqualityComparer<MessageDescriptor>.Create(
            (a, b) => a is not null && b is not null
                && a.Name == b.Name && a.SchemaVersion == b.SchemaVersion,
            d => d is null ? 0 : HashCode.Combine(d.Name, d.SchemaVersion));

    public bool Equals(MessageDescriptor? other)
        => other is not null
           && Name == other.Name
           && SchemaVersion == other.SchemaVersion;

    public override bool Equals(object? obj) => obj is MessageDescriptor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Name, SchemaVersion);

    public static bool operator ==(MessageDescriptor? left, MessageDescriptor? right)
        => left?.Equals(right) ?? right is null;

    public static bool operator !=(MessageDescriptor? left, MessageDescriptor? right) => !(left == right);

    private static string GetTypeName(Type type) => type.FullName ?? type.Name;
}
