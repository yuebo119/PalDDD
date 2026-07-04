// ─────────────────────────────────────────────────────────────
// 🔢 ExpectedStreamVersion — 乐观并发控制（4 种合法状态）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 乐观并发版本检查 — DDD 值对象 + 事件溯源模式
// 💡 保留理由：事件溯源乐观并发控制核心抽象 · 4 种策略封装为值类型。
//    详见 docs/decisions/004-core-type-retention.md
// ─────────────────────────────────────────────────────────────
//
// 封装价值：Exact(long version) 工厂方法包含
//    ArgumentOutOfRangeException.ThrowIfLessThan(version, 0) 验证。
//    如果改为 public 构造函数，调用方可以创建 version=-1 的 Exact
//    （语义上与 NoStream 冲突的非法状态）。工厂方法不可删除。

/// <summary>乐观并发检查的期望流版本。</summary>
public readonly record struct ExpectedStreamVersion
{
    private ExpectedStreamVersion(ExpectedStreamVersionKind kind, long version)
    {
        Kind = kind;
        Version = version;
    }

    /// <summary>仅当流不存在时追加。</summary>
    public static ExpectedStreamVersion NoStream { get; } = new(ExpectedStreamVersionKind.NoStream, -1);

    /// <summary>无论当前流版本如何都追加。</summary>
    public static ExpectedStreamVersion Any { get; } = new(ExpectedStreamVersionKind.Any, -1);

    /// <summary>仅当流已存在时追加。</summary>
    public static ExpectedStreamVersion StreamExists { get; } = new(ExpectedStreamVersionKind.StreamExists, -1);

    /// <summary>仅当流最后版本等于 <paramref name="version"/> 时追加。</summary>
    public static ExpectedStreamVersion Exact(long version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 0);

        return new ExpectedStreamVersion(ExpectedStreamVersionKind.Exact, version);
    }

    internal ExpectedStreamVersionKind Kind { get; }

    /// <summary>当 <see cref="Kind"/> 为 Exact 时的精确期望流版本。</summary>
    public long Version { get; }

    /// <summary>返回期望版本是否匹配实际流版本。</summary>
    public bool Matches(long actualVersion)
        => Kind switch
        {
            ExpectedStreamVersionKind.Any => true,
            ExpectedStreamVersionKind.NoStream => actualVersion == -1,
            ExpectedStreamVersionKind.StreamExists => actualVersion >= 0,
            ExpectedStreamVersionKind.Exact => actualVersion == Version,
            _ => false
        };
}

internal enum ExpectedStreamVersionKind
{
    Any,
    NoStream,
    StreamExists,
    Exact
}
