using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core;

namespace PalDDD.CQRS.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ EPSL 地毯审计 2026-10-16 B 批 #9：ValidationBehavior 验证器枚举零分配
// 旧码 _validators 为接口型字段，foreach 经接口取枚举器（非空 validator 集实测
// 32B/请求——JIT 探针实证 interface=32B/iter vs array=0B/iter；零 validator 常见
// 场景 JIT 对空数组形态已消除故本就免费）。修复 = 构造期物化数组（Dispatcher.cs
// MaterializeBehaviors 同款先例）。
// 对照组增量设计：ValidationBehavior（1 个通过验证器）减 NoOp 行为——吸收
// Debug/Release 的 async 状态机配置基线，断言与运行配置无关。
// ═══════════════════════════════════════════════════════════════

internal sealed class AlwaysValidValidator : IPalValidator<CreateOrderCommand>
{
    public PalValidationResult Validate(CreateOrderCommand instance) => PalValidationResult.Success();
}

internal sealed class NoOpBehavior : IPipelineBehavior<CreateOrderCommand, Guid>
{
    public async ValueTask<Guid> HandleAsync(
        CreateOrderCommand request, CancellationToken ct, Func<ValueTask<Guid>> next)
        => await next().ConfigureAwait(false);
}

public sealed class ValidationBehaviorAllocationTests
{
    [Test]
    public async Task ValidationBehavior_WithValidator_NoEnumeratorAllocation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPalValidator<CreateOrderCommand>, AlwaysValidValidator>();
        var sp = services.BuildServiceProvider();

        var behavior = new ValidationBehavior<CreateOrderCommand, Guid>(
            sp.GetServices<IPalValidator<CreateOrderCommand>>());
        var noOp = new NoOpBehavior();
        var cmd = new CreateOrderCommand("Test", 100m);

        static ValueTask<Guid> Next() => new(Guid.NewGuid());

        // 预热（JIT 成型）
        for (var i = 0; i < 50; i++)
        {
            await behavior.HandleAsync(cmd, CancellationToken.None, Next);
            await noOp.HandleAsync(cmd, CancellationToken.None, Next);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int iterations = 10_000;
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            await behavior.HandleAsync(cmd, CancellationToken.None, Next);
        var withValidator = GC.GetAllocatedBytesForCurrentThread() - baseline;

        baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            await noOp.HandleAsync(cmd, CancellationToken.None, Next);
        var noOpAlloc = GC.GetAllocatedBytesForCurrentThread() - baseline;

        // 新码差 = 一次 Validate 调用（Success 路径零分配）≈ 0；旧码差 = +32B/请求
        // （接口枚举器）。预算 8B/迭代——旧码超预算判红，锁定的回归敏感点即验证器
        // 枚举路径接口枚举器分配的重引入。
        await Assert.That(withValidator - noOpAlloc).IsLessThanOrEqualTo(8L * iterations);
    }
}
