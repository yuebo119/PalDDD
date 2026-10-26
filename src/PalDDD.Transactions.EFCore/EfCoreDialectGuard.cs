using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PalDDD.Transactions;

/// <summary>
/// EF 方言上下文的 provider 一致性守卫（2026-10-21 契约盘点 R2 后续项）。
/// <para>
/// 方言上下文（<c>SqliteOutboxDbContext</c> / <c>PostgreSqlOutboxDbContext</c> /
/// <c>MySqlOutboxDbContext</c>）用原生 SQL 实现租约；provider 与上下文方言不一致时，
/// 现有实现会在首次查询抛 provider 原生语法错误（如 SQLite 上的 <c>near "FOR"</c>），
/// 不指出真正的问题。本守卫在租约入口按 <see cref="DatabaseFacade.ProviderName"/>
/// 判定并给出可行动消息。
/// </para>
/// </summary>
internal static class EfCoreDialectGuard
{
    /// <summary>校验当前 provider 与上下文声称的方言一致。</summary>
    /// <exception cref="InvalidOperationException">provider 缺失或不匹配。</exception>
    internal static void EnsureProvider(
        DatabaseFacade database,
        string expectedProviderFragment,
        string expectedDialect,
        string contextName)
    {
        var provider = database.ProviderName;
        if (provider is not null
            && provider.Contains(expectedProviderFragment, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{contextName} 需要 {expectedDialect} EF Core provider，但当前 provider 为 '{provider ?? "<未配置>"}'。"
            + $"请在 DbContextOptions 中改用 Use{expectedDialect}，或改用与 provider 匹配的 *OutboxDbContext 子类；"
            + "provider 与方言错配会在后续查询以 provider 语法错误的形式失败。");
    }
}
