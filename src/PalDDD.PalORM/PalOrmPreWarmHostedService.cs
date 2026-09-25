using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using PalDDD.Core.Logging;
using PalORM;

namespace PalDDD.PalORM;

/// <summary>
/// 启动期连接池预热（<see cref="DataSession{TProvider}.PreWarmAsync"/> 的 hosted 包装，
/// 由 <c>AddPalOrm*</c> 在 <c>prewarmConnectionCount &gt; 0</c> 时注册，通常无需手动使用）。
/// </summary>
/// <remarks>
/// <para><b>语义</b>（上游 5.6.0 XML 契约）：逐条打开 <paramref name="connectionCount"/> 条连接
/// 随即归还池，使首批查询命中暖连接而非新建物理连接。物理连接池为 ADO.NET 进程级共享池
/// （按连接串键控）——预热所用 <paramref name="options"/> 必须与 DI 工厂最终生效的 options
/// 同连接串（<c>AddPalOrm*</c> 内部保证同一实例），否则预热落空。</para>
/// <para><b>失败语义</b>：预热是优化而非正确性前提——建连失败<b>降级启动</b>（记 Warning 后
/// 继续），首个请求走冷连接；不抛出以免"优化失败炸掉应用启动"。需要 fail-fast 的宿主应自行
/// 调用 <see cref="DataSession{TProvider}.PreWarmAsync"/> 并自行处置异常。</para>
/// <para><b>SQLite</b>：无连接池，上游契约直接返回（注册无害，预热为空操作）。</para>
/// <para><b>配套</b>：只预热不设 <c>DbOptions.MinPoolSize</c> 的话，空闲修剪到期后暖态仍会被
/// 清空——生产场景两者配合（上游 XML 明示）。</para>
/// </remarks>
/// <param name="options">与 DI 工厂同一实例的 DbOptions（同连接串=同池）。</param>
/// <param name="connectionCount">预热连接条数。</param>
/// <param name="logger">可选日志面（预热降级时记 Warning；默认空实现）。</param>
public sealed class PalOrmPreWarmHostedService<TProvider>(
    DbOptions options,
    int connectionCount,
    IPalLogger<PalOrmPreWarmHostedService<TProvider>>? logger = null) : IHostedService
    where TProvider : IDbProvider
{
    /// <inheritdoc />
    // CA1031 精确抑制（本仓惯例：不在全局 NoWarn）：预热降级语义要求捕获任意建连异常
    //（上游失败契约"建连异常原样抛出"，类型随 provider 而异），降级 + Warning 已显式
    // 声明于类 remarks——非静默吞异常
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "预热为优化而非正确性前提：上游失败契约'建连异常原样抛出'且类型随 provider 而异，此处按降级语义捕获后记 Warning 继续（类 remarks 显式声明），fail-fast 需求由调用方自调 PreWarmAsync 承载。")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DataSession<TProvider>.PreWarmAsync(options, connectionCount, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // 启动取消（host 停止/超时）：预热未完成即冷启动，非错误——记原因后放行
            (logger ?? NullPalLogger<PalOrmPreWarmHostedService<TProvider>>.Instance).Warning(
                $"PalORM 连接池预热被取消（{typeof(TProvider).Name}），首个请求将走冷连接：{ex.Message}");
        }
        // CA1031 见方法级 SuppressMessage 的 Justification（降级语义，非静默吞异常）
        catch (Exception ex)
        {
            // 预热是优化：失败降级启动（首个请求走冷连接），不炸应用启动——调用方若需
            // fail-fast 应自行调 PreWarmAsync（见类 remarks 失败语义声明）
            (logger ?? NullPalLogger<PalOrmPreWarmHostedService<TProvider>>.Instance).Warning(
                $"PalORM 连接池预热失败（{typeof(TProvider).Name}），降级为冷启动：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
