// ─────────────────────────────────────────────────────────────
// SagaLaneBenchmarks —— Saga 车道基准锚（perf-run-2026-09-20 P1）
// ─────────────────────────────────────────────────────────────
// 背景：骨架收敛（f5947f5）后 Saga 四车道行为有 12 个表征测试锁定，
// 性能零锚——本文件给 Normal/FanOut 两车道补 before 基线，供未来
// v3.0 子项粒度改造（ADR-020 窗口）与骨架优化做回归比对。
//
// 口径：InProcess + MemoryDiagnoser；ProcessEventAsync 为纯内存编排
//（无 IO），重点观测编排开销（重试循环/观察者分发/轨迹写入）。
// executor 为最小工作（计数器自增），测的是框架税而非业务成本。
//
// 用法：dotnet run --project bench/PalDDD.Benchmarks -c Release -- --saga
// ─────────────────────────────────────────────────────────────
using BenchmarkDotNet.Attributes;
using PalDDD.Benchmarks;
using PalDDD.Transactions;

[MemoryDiagnoser]
[InProcess]
public class SagaLaneBenchmarks
{
    private NormalLaneSaga _normal = null!;
    private FanOutLaneSaga _fanOut = null!;
    private NormalLaneState _normalState = null!;
    private FanOutLaneState _fanOutState = null!;

    [GlobalSetup]
    public void Setup()
    {
        _normal = new NormalLaneSaga();
        _fanOut = new FanOutLaneSaga();
        _normalState = new NormalLaneState { CurrentState = "Start" };
        _fanOutState = new FanOutLaneState { CurrentState = "Start" };
    }

    /// <summary>Normal 车道单事件往返：MakeKey 查找 + SafeObserve 族 + RecordExecutedStep
    /// + executor 分派——骨架收敛后的单步编排总开销（无重试无补偿路径）。</summary>
    [Benchmark(Baseline = true)]
    public async ValueTask<NormalLaneState> ProcessEvent_NormalLane()
    {
        _normalState.CurrentState = "Start"; // 幂等回置（executor 不改状态，回置 CurrentState 防 Key 漂移）
        return await _normal.ProcessEventAsync(_normalState, BenchEvent.Instance);
    }

    /// <summary>FanOut 车道单事件往返（items=4）：selector 展开 + semaphore 并发调度
    /// + 四子任务 executor + FanOutResult 聚合——整批 attempt 粒度的完整路径。</summary>
    [Benchmark]
    public async ValueTask<FanOutLaneState> ProcessEvent_FanOutLane()
    {
        return await _fanOut.ProcessEventAsync(_fanOutState, BenchEvent.Instance);
    }
}

// ── 车道测试 saga（最小 executor：只计数不改状态，隔离框架开销）──

public sealed class NormalLaneState : SagaState;
public sealed class FanOutLaneState : SagaState;

internal sealed class BenchEvent
{
    public static readonly BenchEvent Instance = new();
    private BenchEvent() { }
}

public sealed class NormalLaneSaga : Saga<NormalLaneState>
{
    public NormalLaneSaga()
    {
        When("Start", new SagaStep("step",
            execute: static (s, _, _) => ValueTask.FromResult(s)));
    }
}

public sealed class FanOutLaneSaga : Saga<FanOutLaneState>
{
    public FanOutLaneSaga()
    {
        When("Start", new FanOutStep<int, int>(
            "fan",
            selector: static _ => [1, 2, 3, 4],
            executor: static (item, _) => ValueTask.FromResult(item)));
    }
}
