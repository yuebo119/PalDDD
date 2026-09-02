namespace PalDDD.Serialization.Evolution;

/// <summary>描述单条平台启动验证失败信息。</summary>
public sealed record PalPlatformVerificationError
{
    public PalPlatformVerificationError(MessageEvolutionPathRequirement requirement, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(exception);
        Requirement = requirement;
        Exception = exception;
    }

    public MessageEvolutionPathRequirement Requirement { get; }
    public Exception Exception { get; }
}

/// <summary>平台启动验证检测到无效配置时抛出。</summary>
public sealed class PalPlatformVerificationException : InvalidOperationException
{
    /// <summary>创建空的平台验证异常。</summary>
    /// <remarks>v54 P3 契约声明：无参/单参构造为序列化兼容预留——Errors 可为空集合（四参构造
    /// 强制至少一错），框架内唯一调用方仅用四参构造，空 Errors 路径无缺陷场景。</remarks>
    public PalPlatformVerificationException()
    {
        Errors = [];
    }

    /// <summary>创建带消息的平台验证异常。</summary>
    public PalPlatformVerificationException(string message)
        : base(message)
    {
        Errors = [];
    }

    /// <summary>创建带消息和内部异常的平台验证异常。</summary>
    public PalPlatformVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    /// <summary>创建平台验证异常。</summary>
    public PalPlatformVerificationException(IReadOnlyList<PalPlatformVerificationError> errors)
        : base(CreateMessage(errors))
    {
        // P3 修复（十七轮）：null 校验收敛——base 调用的 <see cref="CreateMessage"/>
        // 先于构造体执行且已含 ThrowIfNull，构造体到达此处时 errors 必非 null，
        // 此前的重复 ThrowIfNull 为不可达死代码，删除（单一校验点）。
        if (errors.Count == 0)
            throw new ArgumentException("Platform verification exception requires at least one error.", nameof(errors));

        // v44 P3：防御拷贝——异常对象快照语义，外部调用方持可变 List 变异后
        // 不漂移 Errors（内部唯一调用方传入后不再改，公共构造器外部使用受影响）
        Errors = [.. errors];
    }

    /// <summary>单次验证中发现的所有启动验证错误。</summary>
    public IReadOnlyList<PalPlatformVerificationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<PalPlatformVerificationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors); // 唯一 null 校验点（base 参数求值先于构造体执行）

        return $"Pal platform verification failed with {errors.Count} error(s).";
    }
}
