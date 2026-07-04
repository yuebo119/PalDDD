// ─────────────────────────────────────────────────────────────
// ✅ IPalValidator<T> — 领域验证抽象 + PalValidationResult 结果
// ─────────────────────────────────────────────────────────────
using System.Collections.Immutable;

namespace PalDDD.Core;

/// <summary>验证器接口 — 领域验证抽象，不依赖任何第三方库</summary>
/// <typeparam name="T">要验证的类型</typeparam>
public interface IPalValidator<in T>
{
    /// <summary>验证实例，返回验证结果</summary>
    PalValidationResult Validate(T instance);
}

/// <summary>验证结果 — 仅在验证失败时分配错误集合</summary>
public readonly struct PalValidationResult : IEquatable<PalValidationResult>
{
    /// <summary>是否通过验证</summary>
    public bool IsValid { get; }

    /// <summary>验证错误集合（仅在失败时有值）。使用 ImmutableArray 保证不可变语义。</summary>
    public ImmutableArray<PalValidationError> Errors { get; }

    private PalValidationResult(bool isValid, ImmutableArray<PalValidationError> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    /// <summary>验证通过 — 零分配空结果</summary>
    public static PalValidationResult Success() => new(true, ImmutableArray<PalValidationError>.Empty);

    /// <summary>验证失败，附带错误列表</summary>
    public static PalValidationResult Failed(ImmutableArray<PalValidationError> errors)
        => new(false, errors);

    /// <summary>验证失败，附带单个错误</summary>
    public static PalValidationResult Failed(string property, string message)
        => new(false, ImmutableArray.Create(new PalValidationError(property, message)));

    /// <inheritdoc/>
    public bool Equals(PalValidationResult other) => IsValid == other.IsValid;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PalValidationResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => IsValid.GetHashCode();

    /// <summary>相等比较</summary>
    public static bool operator ==(PalValidationResult left, PalValidationResult right) => left.Equals(right);

    /// <summary>不等比较</summary>
    public static bool operator !=(PalValidationResult left, PalValidationResult right) => !(left == right);
}

/// <summary>验证错误</summary>
/// <param name="PropertyName">属性名</param>
/// <param name="Message">错误消息</param>
public readonly record struct PalValidationError(string PropertyName, string Message);
