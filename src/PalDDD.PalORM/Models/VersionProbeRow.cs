// ─────────────────────────────────────────────────────────────
// MySQL 版本探测行（ADR-025）——PalOrmOutboxStore.MySqlSupportsSkipLockedAsync 专用
// 形态约束（PALORM015/045 实证）：必须是顶层 + [Table] + partial + 公共无参构造 +
// [Key]——嵌套/无键类型被 PalORM.SourceGen 跳过（RowFactory 不生成 → QueryAsync 运行时
// 'not registered' → 探测被 fail-safe 吞掉恒回退无锁模板，2026-10-09 复跑实证）。
// 表名/键均为占位：探测走 raw SELECT VERSION() AS v，不经 CRUD 实体路径，v 兼作键列。
// ─────────────────────────────────────────────────────────────
using PalORM;

namespace PalDDD.PalORM.Models;

/// <summary>版本探测行——SELECT VERSION() AS v, VERSION() AS v2 的单行物化载体。</summary>
[Table("sys_version_probe")]
public sealed partial class VersionProbeRow
{
    /// <summary>服务器版本串（如 "8.4.11"），兼作实体键列（探测只读，永不插入）。
    /// 属性名 V/v2 大写起头循 IDE1006 命名规则（CI format-verify 实证，2026-10-11）；
    /// SQL 列别名仍为小写 v/v2，经 [Column] 映射（探测 SQL：SELECT VERSION() AS v）。</summary>
    [Key(AutoIncrement = false)]
    [Column("v")]
    public string? V { get; set; }

    /// <summary>第二版本列——PALORM024 要求实体至少一个可更新非键列（探测只读，仅占位）。</summary>
    [Column("v2")]
    public string? V2 { get; set; }
}
