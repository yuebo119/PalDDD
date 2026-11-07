// ─────────────────────────────────────────────────────────────
// 🛡️ StatusFilterGuard — 状态过滤值的定义域校验（批次 C，2026-11-04）
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要：Outbox/Inbox/Saga 的读侧 API 都按状态过滤。传一个未定义的枚举值
//   ｜ （如历史遗留编号、或写错的强制转换）时，静默返回空列表会让调用方误判
//   ｜ 「该状态没有数据」——这正是本轮要消灭的假 API 形态（GetInterruptedSagasAsync）。
//   ｜ 让过滤条件本身可见失败，与 SagaStatusGuard 同口径。
//
// ✅ AOT 安全：Enum.IsDefined<TEnum> 泛型专用化，零反射。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>状态过滤值的定义域守卫——Outbox/Inbox 读侧 API 共用。</summary>
internal static class StatusFilterGuard
{
    /// <summary>校验状态过滤值是已定义枚举成员，否则抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    /// <typeparam name="TStatus">状态枚举类型。</typeparam>
    /// <param name="status">待校验的过滤值。</param>
    /// <param name="enumName">枚举类型名（异常文案用，如 <c>OutboxStatus</c>）。</param>
    public static void EnsureDefined<TStatus>(TStatus status, string enumName)
        where TStatus : struct, Enum
    {
        if (Enum.IsDefined(status))
            return;

        throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            $"{enumName} 的过滤值 {Convert.ToInt32(status, System.Globalization.CultureInfo.InvariantCulture)}"
            + " 不是已定义枚举成员——请修正调用方。静默返回空列表会让调用方误判"
            + "「该状态没有数据」。");
    }
}
