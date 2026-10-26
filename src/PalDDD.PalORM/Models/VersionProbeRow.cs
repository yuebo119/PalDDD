// ─────────────────────────────────────────────────────────────
// MySQL 版本探测行（ADR-025）——已退役，保留一个 Minor 窗口的二进制兼容
// ─────────────────────────────────────────────────────────────
// 2026-10-21 双评审修复轮：MySqlSupportsSkipLockedAsync 已改为读取
// DbConnection.ServerVersion（握手包，零往返），本类型在全仓 0 引用。
// 直接删除会让已编译消费方在加载时找不到类型；按兼容策略先标 [Obsolete]，
// 下一个 Major（v5.0.0）移除。
// ─────────────────────────────────────────────────────────────
using PalORM;

namespace PalDDD.PalORM.Models;

/// <summary>版本探测行——已被 <c>DbConnection.ServerVersion</c> 取代，计划在 v5.0.0 移除。</summary>
[Obsolete("VersionProbeRow is no longer used: MySqlSupportsSkipLockedAsync reads DbConnection.ServerVersion. Kept for binary compatibility until v5.0.0.")]
[Table("sys_version_probe")]
public sealed partial class VersionProbeRow
{
    /// <summary>服务器版本串（历史探针列，兼作占位键列）。</summary>
    [Key(AutoIncrement = false)]
    [Column("v")]
    public string? V { get; set; }

    /// <summary>第二版本列（PALORM024 要求的可更新非键列占位）。</summary>
    [Column("v2")]
    public string? V2 { get; set; }
}
