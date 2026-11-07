// ─────────────────────────────────────────────────────────────
// 🛡️ SagaStatusGuard — 状态列读写的唯一定义点（批次 B，2026-11-04）
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要：持久化层的 status 是 INT 列，四栈水合点此前一律 `(SagaStatus)row.Status`
//   ｜ 强转。强转对未定义值不抛错——库里存 4 会读成一个「不是任何已知状态」的枚举实例，
//   ｜ switch 全部落空、终态判定失效、运维查询列不出来，故障表现是「Saga 卡住但不报错」。
//
// ✅ AOT 安全：Enum.IsDefined<TEnum>(TEnum) 是泛型专用化，零反射、零装箱。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>Saga 状态列的强类型守卫 —— 四栈水合与状态过滤的唯一校验点。</summary>
internal static class SagaStatusGuard
{
    /// <summary>
    /// 把数据库里的整型状态值映射为 <see cref="SagaStatus"/>；未定义值抛可行动异常。
    /// </summary>
    /// <param name="rawValue">status 列原值。</param>
    /// <param name="sagaId">所属 Saga 标识（用于定位行；未知时传 <c>null</c>）。</param>
    /// <exception cref="InvalidOperationException">值不属于任何已定义的 <see cref="SagaStatus"/>。</exception>
    public static SagaStatus FromPersisted(int rawValue, object? sagaId)
    {
        var status = (SagaStatus)rawValue;
        if (Enum.IsDefined(status))
            return status;

        throw new InvalidOperationException(
            $"Saga '{sagaId}' 的 status 列值为 {rawValue}，不属于任何已定义的 SagaStatus"
            + "（合法值 0、1、2、3、5；4 已随 DeadLettered 删除，是永久空洞）。"
            + "升级前请执行「SELECT COUNT(*) FROM saga_states WHERE status = 4」核对存量，"
            + "把 4 修正为实际语义（补偿失败用 CompensationFailed=3）后再启动。"
            + "拒绝把未定义值静默读成非法枚举实例——那会让终态判定与 switch 全部落空。");
    }

    /// <summary>校验一个已水合的状态值（供 ORM 自动物化栈在读取后复核）。</summary>
    /// <param name="status">已物化的状态。</param>
    /// <param name="sagaId">所属 Saga 标识（用于定位行）。</param>
    /// <exception cref="InvalidOperationException">状态不属于任何已定义值。</exception>
    public static void EnsureDefined(SagaStatus status, object? sagaId)
    {
        if (!Enum.IsDefined(status))
            _ = FromPersisted((int)status, sagaId); // 复用同一可行动异常文案
    }
}
