using System.Collections.ObjectModel;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>Saga 状态基类 — 持久化到业务数据库，与领域事件在同一事务中</summary>
public abstract class SagaState
{
    /// <summary>框架内置状态名（<see cref="CurrentState"/> 协议值）— 新建 Saga 的初始状态。</summary>
    public const string InitialStateName = "Initial";

    /// <summary>框架内置状态名（<see cref="CurrentState"/> 协议值）— SagaTimeoutProcessor 补偿成功的终态。</summary>
    public const string CompensatedStateName = "Compensated";

    /// <summary>框架内置状态名（<see cref="CurrentState"/> 协议值）— SagaTimeoutProcessor 补偿失败的终态。</summary>
    public const string CompensationFailedStateName = "CompensationFailed";

    /// <summary>Saga 唯一标识</summary>
    public PalUlid SagaId { get; init; } = PalUlid.New();

    private string _currentState = InitialStateName;

    /// <summary>
    /// 当前状态名称。
    /// <para>
    /// ⚠️ <b>约束：</b>状态名不能包含 <c>|</c> 字符（<c>|</c> 用作 Saga key 分隔符）。
    /// 违反此约束会抛出 <see cref="ArgumentException"/>。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">设置的值包含 <c>|</c> 字符时抛出</exception>
    public string CurrentState
    {
        get => _currentState;
        set
        {
            // ITM-168 修复：补 null 守卫——原 value.Contains 对 null 抛 NRE，
            // 失败点与异常类型（NullReferenceException）均不符合框架公共 setter 惯例。
            // 仅拒绝 null，空串语义保持原样（空状态名由业务层自行约束）。
            ArgumentNullException.ThrowIfNull(value);
            if (value.Contains('|'))
                throw new ArgumentException(
                    $"Saga 状态名不能包含 '|' 字符（当前值：\"{value}\"），因为 '|' 用作 key 分隔符。请使用 PascalCase 或 kebab-case。",
                    nameof(value));
            _currentState = value;
        }
    }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.GetUtcNow();

    /// <summary>
    /// 框架级时间提供者（P3 修复：时钟双轨清零）——与 DomainEvent/OutboxMessage 同模式：
    /// internal AsyncLocal，测试经 InternalsVisibleTo 注入 FakeTimeProvider。
    /// </summary>
    internal static TimeProvider TimeProvider
    {
        get => s_timeProvider.Value ?? System.TimeProvider.System;
        set => s_timeProvider.Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static readonly AsyncLocal<TimeProvider?> s_timeProvider = new();

    /// <summary>完成时间</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>生命周期状态 — 替代分散的 IsCompleted / IsDeadLettered / CompensationError</summary>
    public SagaStatus Status { get; set; }

    /// <summary>版本号（乐观并发）</summary>
    public int Version { get; set; }

    /// <summary>各步骤开始执行的时间戳（用于精确超时计算，而非从 Saga 创建时间起算）</summary>
    public Dictionary<string, DateTimeOffset> StepStartedAt { get; init; } = [];

    /// <summary>补偿失败或死信时的错误信息 — 非空表示补偿未成功完成</summary>
    public string? Error { get; set; }

    /// <summary>进入死信或补偿失败状态的时间。</summary>
    public DateTimeOffset? ErrorAt { get; set; }

    /// <summary>当前租约持有者；非空表示后台扫描器正在处理此 Saga。</summary>
    public string? LeasedBy { get; set; }

    /// <summary>租约过期时间；过期后其他后台扫描器可重新获取。</summary>
    public DateTimeOffset? LeasedUntil { get; set; }

    /// <summary>已成功执行的步骤 Key 列表（按执行顺序）— 补偿顺序的唯一依据：
    /// <see cref="CompensationPolicy.Backward"/> 逆序遍历（最后执行的先回滚）、
    /// <see cref="CompensationPolicy.Forward"/> 正序遍历。v39 P3 勘正声明：补偿按执行序
    /// 而非注册序（注册顺序的 List 供超时检测使用，与本列表无关）</summary>
    public Collection<string> ExecutedStepKeys { get; init; } = [];

    /// <summary>中断原因 — HITL 中断时记录等待人工决策的原因</summary>
    public string? InterruptReason { get; set; }

    /// <summary>
    /// 创建当前状态的成员级浅拷贝（v25 P3 行为族 B1）——供 <see cref="InMemorySagaStateStore{TState}"/>
    /// 的 successor 租约替换使用（对齐 InMemoryOutboxStore 的 ITM-174 模式）。
    /// <see cref="object.MemberwiseClone"/> 为 CLR 内在方法（非反射，AOT 安全），拷贝全部
    /// 实例字段（含子类字段）。⚠️ v26 P3 勘正：<see cref="StepStartedAt"/>/<see cref="ExecutedStepKeys"/>
    /// 为 init-only 属性，浅拷贝后新旧实例共享集合容器——<b>并发写安全未保障</b>（旧持有者僵尸
    /// 与新持有者并发执行时对共享容器的并发读写可抛，如 Dictionary 枚举中修改）；
    /// <b>Version fencing 语义不受影响</b>（标量字段 Status/Version/LeasedBy 等已隔离，
    /// 僵尸的 SaveChangesAsync 因 Version 落后返回 0）；<b>深隔离需后续破坏性变更</b>
    /// （拷贝容器会改变既有共享语义，须随主版本演进）。当前共享语义下，步骤执行轨迹
    /// （集合内容）属"事实记录"而非租约保护状态，旧持有者的步骤记录反映到 successor
    /// 语义可接受（其确实执行过该步骤）。
    /// </summary>
    internal SagaState CloneForLease() => (SagaState)MemberwiseClone();
}
