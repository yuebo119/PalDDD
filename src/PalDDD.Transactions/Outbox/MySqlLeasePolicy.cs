// ─────────────────────────────────────────────────────────────
// MySQL 租约 SQL 形态版本策略（ADR-025）——Dapper/PalORM 双栈共用
// 背景：ADR-024 显式接受「无锁子句 last-writer-wins」分叉；2026-10-08/09 基准车道
// 实证其并发后果为 InnoDB 死锁异常（Dapper/PalORM 双栈 N≥2 全死锁，8 处实录），
// 而 JOIN 派生表 FOR UPDATE SKIP LOCKED 形态同库同负载零死锁+互斥+1.75× 加速
//（MySqlSkipLockedShapeBenchmarks）。ADR-025 裁定：MySQL ≥8.0.18（与 EF 栈声明
// 对齐）启用 SKIP LOCKED 模板，旧版/MariaDB/解析失败回退现模板（行为逐字不变）。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// MySQL 租约 SQL 形态的版本判定（ADR-025）。纯函数，Dapper/PalORM 两栈共用。
/// </summary>
public static class MySqlLeasePolicy
{
    /// <summary>启用 JOIN 派生表 FOR UPDATE SKIP LOCKED 的最低版本（与 EF 栈 MySqlOutboxDbContext 声明对齐）。</summary>
    public const string MinimumSkipLockedVersion = "8.0.18";

    /// <summary>
    /// 判定给定 MySQL ServerVersion 是否启用 SKIP LOCKED 租约模板。<br/>
    /// 规则（fail-safe：任何不确定都回退现模板，其行为在全部版本已知）：
    /// <list type="bullet">
    /// <item>仅认 MySQL——含 "-MariaDB" 后缀的发行版一律 false（派生表锁语义独立矩阵，未验证）；</item>
    /// <item>主/次版本解析失败 false；补丁段取前导数字（"8.4.11-log" → 11），非数字段按 0；</item>
    /// <item>≥ (8, 0, 18) 才 true。</item>
    /// </list>
    /// </summary>
    /// <param name="serverVersion">连接报告的服务器版本串（如 "8.4.11"、"8.0.18-commercial"、"11.4.3-MariaDB"）。</param>
    public static bool UseSkipLocked(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
            return false;
        if (serverVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = serverVersion.Split('.');
        if (parts.Length < 2)
            return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return false;

        var patch = 0;
        if (parts.Length >= 3)
        {
            var patchText = parts[2];
            var digitEnd = 0;
            while (digitEnd < patchText.Length && char.IsDigit(patchText[digitEnd]))
                digitEnd++;
            // 非数字补丁段（如 "8.4.x"）按 0 计——主/次版本已满足判定语义
            if (!int.TryParse(patchText[..digitEnd], out patch))
                patch = 0;
        }

        return major > 8 || (major == 8 && (minor > 0 || patch >= 18));
    }
}
