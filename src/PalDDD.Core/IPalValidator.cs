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

    private readonly ImmutableArray<PalValidationError> _errors;

    /// <summary>验证错误集合（仅在失败时有值）。使用 ImmutableArray 保证不可变语义。</summary>
    // v47 P3：getter 归一——default(Struct) 实例（用户验证器 return default，不经过
    // Failed 工厂）的 Errors 枚举不再 NRE（v39/v40 消费方防御的源头闭环）。
    // _errors 为 default 时返回 Empty（零分配——Empty 是 ImmutableArray 的单例形态）
    public ImmutableArray<PalValidationError> Errors => _errors.IsDefault ? ImmutableArray<PalValidationError>.Empty : _errors;

    private PalValidationResult(bool isValid, ImmutableArray<PalValidationError> errors)
    {
        IsValid = isValid;
        _errors = errors;
    }

    /// <summary>验证通过 — 零分配空结果</summary>
    public static PalValidationResult Success() => new(true, ImmutableArray<PalValidationError>.Empty);

    /// <summary>验证失败，附带错误列表</summary>
    /// <remarks>v40 P2：default(ImmutableArray) 归一为空数组——用户验证器 return default
    /// 的现实可达形态（38 轮注释自证）下 Errors 可枚举性契约与 v39 PalValidationException
    /// 构造归一同族闭环（default ImmutableArray 枚举抛 NRE）。</remarks>
    public static PalValidationResult Failed(ImmutableArray<PalValidationError> errors)
    {
        if (errors.IsDefault)
            errors = ImmutableArray<PalValidationError>.Empty;
        return new(false, errors);
    }

    /// <summary>验证失败，附带单个错误</summary>
    public static PalValidationResult Failed(string property, string message)
        => new(false, ImmutableArray.Create(new PalValidationError(property, message)));

    /// <summary>
    /// 相等比较——<b>仅按 <see cref="IsValid"/> 判等，不比较 <see cref="Errors"/> 集合</b>
    /// （八轮评审声明：避免逐元素比较错误集合的分配与语义歧义）。
    /// </summary>
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
