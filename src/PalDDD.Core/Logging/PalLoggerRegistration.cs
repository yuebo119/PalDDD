// ─────────────────────────────────────────────────────────────
// 📝 PalLoggerRegistration — IPalLogger 门面的单一注册点
// ─────────────────────────────────────────────────────────────
// 2026-10-21 契约盘点 R7：门面映射只在此处定义，供 DI 包（AddPalDDD/AddPalPipelineBehaviors/
// AddPalLogging）与 Transactions 包（AddPalOutbox/AddPalInbox/AddPalSaga）共同调用。
// 本方法只登记门面本身，不调用 AddLogging——宿主的 ILogger<T> 由调用方或标准宿主提供
// （与既有「依赖宿主 ILogger<T>，无 Provider 时零输出」契约一致）；DI 包在自身入口里
// 额外 AddLogging() 以保持 AddPalCoreStack 在裸 ServiceCollection 上的既有行为。

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PalDDD.Core.Logging;

internal static class PalLoggerRegistration
{
    /// <summary>登记 <see cref="IPalLogger{T}"/> → <see cref="PalLogger{T}"/>（TryAdd，幂等）。</summary>
    internal static IServiceCollection EnsurePalLoggerFacade(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(typeof(IPalLogger<>), typeof(PalLogger<>));
        return services;
    }
}
