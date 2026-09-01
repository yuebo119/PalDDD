using System.Collections.Immutable;

// ─────────────────────────────────────────────────────────────
// ⚠️ PalValidationException — 验证失败异常（DTO 400）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.CQRS;

// ─────────────────────────────────────────────────────────────
// 验证异常
// ─────────────────────────────────────────────────────────────

/// <summary>命令/查询验证异常 — 管道验证失败时抛出</summary>
public sealed class PalValidationException : Exception
{
    /// <summary>验证错误集合 — 使用 ImmutableArray 保证不可变语义</summary>
    public ImmutableArray<Core.PalValidationError> Errors { get; }

    /// <summary>创建空验证异常</summary>
    public PalValidationException() : base("Validation failed.") => Errors = ImmutableArray<Core.PalValidationError>.Empty;

    /// <summary>创建验证异常（含自定义消息）</summary>
    public PalValidationException(string message) : base(message) => Errors = ImmutableArray<Core.PalValidationError>.Empty;

    /// <summary>创建验证异常（含内部异常）</summary>
    public PalValidationException(string message, Exception inner) : base(message, inner) => Errors = ImmutableArray<Core.PalValidationError>.Empty;

    /// <summary>创建验证异常（含错误集合）</summary>
    public PalValidationException(ImmutableArray<Core.PalValidationError> errors)
        : base(CreateMessage(errors))
    {
        // v39 P2 根因修：default(ImmutableArray) 归一为空数组——39 轮修复家族的三消费方
        // （ValidationBehavior 38 轮/CreateMessage 38 轮/EndpointExtensions:268 探针实证 NRE）
        // 全部因消费 default Errors 抛 NullReferenceException；构造层归一后整个家族闭环
        if (errors.IsDefault)
            errors = ImmutableArray<Core.PalValidationError>.Empty;
        Errors = errors;
    }

    /// <summary>创建单错误验证异常</summary>
    public PalValidationException(string property, string message)
        : this(ImmutableArray.Create(new Core.PalValidationError(property, message))) { }

    private static string CreateMessage(ImmutableArray<Core.PalValidationError> errors)
    {
        // errors.IsDefault 表示从未初始化；errors.IsEmpty 表示初始化后为空
        if (errors.IsDefaultOrEmpty)
            return "Validation failed.";

        return $"Validation failed with {errors.Length} error(s): {string.Join("; ", errors)}";
    }
}
