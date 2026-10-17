using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core;

namespace PalDDD.CQRS.Tests;

// ═══════════════════════════════════════════════════════════════
// 📡 流式读测试 — IStreamQuery 契约 + handler 取消传播 + PalValidatorRunner（ADR-030）
// ═══════════════════════════════════════════════════════════════
// 覆盖 ADR-030 验证计划中 PalDDD.CQRS 侧两条：
//   ① handler 取消传播与 finally 清理（D5 契约）；
//   ② PalValidatorRunner 正例负例（D3 单一出处，含占位条目语义）。

public sealed record CountdownStreamQuery(int Count) : IStreamQuery<int>;

/// <summary>探针 handler——记录收到的令牌、产出项数与 finally 清理（D5 取消传播契约锁定）。</summary>
public sealed class CountdownStreamHandler : IStreamQueryHandler<CountdownStreamQuery, int>
{
    public bool FinallyRan { get; private set; }

    public CancellationToken? ReceivedToken { get; private set; }

    public int Yielded { get; private set; }

    public async IAsyncEnumerable<int> HandleAsync(
        CountdownStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ReceivedToken = ct;
        try
        {
            for (var i = 1; i <= query.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Yielded++;
                yield return i;
                // 消费者在两项之间获得取消窗口（探针延迟只为拉长窗口，生产 handler 是真实 IO）
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            FinallyRan = true;
        }
    }
}

// ─── 接口契约（D1 独立读形态的类型系统防线） ───

public sealed class StreamQueryContractTests
{
    private static readonly int[] ExpectedOneToThree = [1, 2, 3];

    [Test]
    public async Task StreamQuery_DoesNotImplementIBaseRequest()
    {
        // D1 防线：IStreamQuery 不继承 IBaseRequest——QueryAsync 无法接受流式查询，
        // "禁止经 dispatcher 路由流式查询"由编译器强制而非用户纪律（反射验证接口集）
        var interfaces = typeof(CountdownStreamQuery).GetInterfaces();
        await Assert.That(interfaces.Contains(typeof(IStreamQuery<int>))).IsTrue();
        await Assert.That(interfaces.Contains(typeof(IBaseRequest))).IsFalse();
        await Assert.That(interfaces.Contains(typeof(IRequest<int>))).IsFalse();
    }

    [Test]
    public async Task StreamHandler_NormalCompletion_RunsFinallyAndYieldsAll()
    {
        var handler = new CountdownStreamHandler();
        using var cts = new CancellationTokenSource();
        var items = new List<int>();

        await foreach (var item in handler.HandleAsync(new CountdownStreamQuery(3), cts.Token))
            items.Add(item);

        await Assert.That(items).IsEquivalentTo(ExpectedOneToThree);
        await Assert.That(handler.FinallyRan).IsTrue();
        // D5 令牌传递实证（同 ITM-285 CapturingCtHandler 口径——断言同一引用而非仅非空；
        // ReceivedToken 为 null 时下一行 .Value 即抛，无需冗余 IsNotNull 弱断言）
        await Assert.That(handler.ReceivedToken!.Value.Equals(cts.Token)).IsTrue();
    }

    [Test]
    public async Task StreamHandler_Cancellation_PropagatesToIteratorAndRunsFinally()
    {
        // D5 取消传播：消费方取消 → 迭代器 MoveNext 抛 OCE → finally 清理照常执行
        var handler = new CountdownStreamHandler();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in handler.HandleAsync(new CountdownStreamQuery(5), cts.Token))
            {
                // 收到首项后取消——迭代器在下一个 await（Task.Delay）处观测
                await cts.CancelAsync();
            }
        });

        await Assert.That(handler.Yielded).IsEqualTo(1);
        await Assert.That(handler.FinallyRan).IsTrue();
    }

    [Test]
    public async Task StreamHandler_WithCancellation_TokenPassedByConsumerWins()
    {
        // [EnumeratorCancellation] 语义：WithCancellation 传入的令牌优先于调用点令牌——
        // 端点依赖该语义兜底（忽略形参令牌的实现也会在 MoveNext 处观测取消）
        var handler = new CountdownStreamHandler();
        using var callSiteCts = new CancellationTokenSource();
        using var consumerCts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in handler.HandleAsync(new CountdownStreamQuery(5), callSiteCts.Token)
                .WithCancellation(consumerCts.Token))
            {
                await consumerCts.CancelAsync();
            }
        });

        await Assert.That(handler.Yielded).IsEqualTo(1);
        await Assert.That(handler.FinallyRan).IsTrue();
    }
}

// ─── PalValidatorRunner（D3 单一出处——ValidationBehavior 两路径与流式端点三处共用） ───

public sealed class PalValidatorRunnerTests
{
    public sealed class AlwaysValidValidator : IPalValidator<CountdownStreamQuery>
    {
        public PalValidationResult Validate(CountdownStreamQuery instance) => PalValidationResult.Success();
    }

    public sealed class AlwaysInvalidValidator : IPalValidator<CountdownStreamQuery>
    {
        public PalValidationResult Validate(CountdownStreamQuery instance)
            => PalValidationResult.Failed("Count", "Must be positive");
    }

    public sealed class DefaultReturningValidator : IPalValidator<CountdownStreamQuery>
    {
        // v25 P2-2 场景：病态但真实的验证器（逻辑分支遗漏 return 值）——
        // default(PalValidationResult) = IsValid false + Errors default
        public PalValidationResult Validate(CountdownStreamQuery instance) => default;
    }

    private static CountdownStreamQuery Query => new(3);

    [Test]
    public async Task Validate_AllValid_ReturnsNull()
    {
        var validators = new IPalValidator<CountdownStreamQuery>[]
        {
            new AlwaysValidValidator(),
            new AlwaysValidValidator()
        };

        var errors = PalValidatorRunner.Validate(validators, Query);

        // 无失败返回 null（调用方按 errors is { Count: > 0 } 判定——无失败时零堆分配）
        await Assert.That(errors).IsNull();
    }

    [Test]
    public async Task Validate_EmptyValidators_ReturnsNull()
    {
        var errors = PalValidatorRunner.Validate(
            Array.Empty<IPalValidator<CountdownStreamQuery>>(), Query);

        await Assert.That(errors).IsNull();
    }

    [Test]
    public async Task Validate_InvalidRequest_ReturnsErrors()
    {
        var validators = new IPalValidator<CountdownStreamQuery>[]
        {
            new AlwaysValidValidator(),
            new AlwaysInvalidValidator()
        };

        var errors = PalValidatorRunner.Validate(validators, Query);

        await Assert.That(errors).Count().IsEqualTo(1);
        await Assert.That(errors![0].PropertyName).IsEqualTo("Count");
        await Assert.That(errors[0].Message).IsEqualTo("Must be positive");
    }

    [Test]
    public async Task Validate_MultipleFailures_AccumulatesAllErrors()
    {
        var validators = new IPalValidator<CountdownStreamQuery>[]
        {
            new AlwaysInvalidValidator(),
            new AlwaysInvalidValidator()
        };

        var errors = PalValidatorRunner.Validate(validators, Query);

        await Assert.That(errors).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Validate_DefaultResult_ConstructsPlaceholderError()
    {
        // v25 P2-2 / v48 P3 定案语义：IsValid=false 且 Errors 为空时构造占位条目——
        // 直接 continue 会吞掉失败事实静默放行（验证拦截被绕过）；该语义三处共用，
        // 只允许 PalValidatorRunner 一处定义
        var validators = new IPalValidator<CountdownStreamQuery>[] { new DefaultReturningValidator() };

        var errors = PalValidatorRunner.Validate(validators, Query);

        await Assert.That(errors).Count().IsEqualTo(1);
        await Assert.That(errors![0].PropertyName).IsEqualTo(string.Empty);
        await Assert.That(errors[0].Message).Contains(nameof(DefaultReturningValidator));
    }

    [Test]
    public async Task Validate_SharedByValidationBehavior_GenericAndNonGenericPathsAgree()
    {
        // D3 重构不变量：ValidationBehavior 泛型路径与非泛型（DIM 桥）路径对同一输入
        // 产生同一验证判定（重构前两条路径各自持有 Validate 副本——防分叉锁定）
        var services = new ServiceCollection();
        services.AddSingleton<IPalValidator<CreateOrderCommand>, AlwaysInvalidOrderValidator>();
        var sp = services.BuildServiceProvider();

        var behavior = new ValidationBehavior<CreateOrderCommand, Guid>(
            sp.GetServices<IPalValidator<CreateOrderCommand>>());
        var cmd = new CreateOrderCommand("Test", 100m);
        var nextCalled = false;

        var genericEx = await Assert.ThrowsAsync<PalValidationException>(async () =>
            await behavior.HandleAsync(cmd, CancellationToken.None,
                () => { nextCalled = true; return new ValueTask<Guid>(Guid.NewGuid()); }));

        var nonGenericEx = await Assert.ThrowsAsync<PalValidationException>(async () =>
            await ((IPipelineBehavior)behavior).HandleAsync(cmd, CancellationToken.None,
                () => new ValueTask<object?>(Guid.NewGuid())));

        await Assert.That(nextCalled).IsFalse();
        await Assert.That(genericEx!.Errors).Count().IsEqualTo(1);
        await Assert.That(nonGenericEx!.Errors).Count().IsEqualTo(1);
        await Assert.That(genericEx.Errors[0].PropertyName).IsEqualTo(nonGenericEx.Errors[0].PropertyName);
    }

    public sealed class AlwaysInvalidOrderValidator : IPalValidator<CreateOrderCommand>
    {
        public PalValidationResult Validate(CreateOrderCommand instance)
            => PalValidationResult.Failed("CustomerName", "Required");
    }
}
