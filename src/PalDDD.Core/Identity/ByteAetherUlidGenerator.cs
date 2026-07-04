// ─────────────────────────────────────────────────────────────
// 🆔 ByteAetherUlidGenerator — ByteAether.Ulid 实现
// ─────────────────────────────────────────────────────────────

using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Core.Identity;

/// <summary>ByteAether.Ulid 实现——RFC 9562 合规，AOT 兼容。</summary>
internal sealed class ByteAetherUlidGenerator : IPalIdGenerator
{
    /// <inheritdoc/>
    public PalUlid NewId() => PalUlid.New();

    /// <inheritdoc/>
    public PalUlid NewId(DateTimeOffset timestamp) => PalUlid.New(timestamp);
}
