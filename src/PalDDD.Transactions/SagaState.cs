using System.Collections.ObjectModel;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>Saga 状态基类 — 持久化到业务数据库，与领域事件在同一事务中</summary>
public abstract class SagaState
{
    /// <summary>Saga 唯一标识</summary>
    public PalUlid SagaId { get; init; } = PalUlid.New();

    private string _currentState = "Initial";

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
            if (value.Contains('|'))
                throw new ArgumentException(
                    $"Saga 状态名不能包含 '|' 字符（当前值：\"{value}\"），因为 '|' 用作 key 分隔符。请使用 PascalCase 或 kebab-case。",
                    nameof(value));
            _currentState = value;
        }
    }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.System.GetUtcNow();

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

    /// <summary>已成功执行的步骤 Key 列表（按执行顺序）— 用于精确补偿已执行步骤</summary>
    public Collection<string> ExecutedStepKeys { get; init; } = [];

    /// <summary>中断原因 — HITL 中断时记录等待人工决策的原因</summary>
    public string? InterruptReason { get; set; }
}
