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
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
            throw new ArgumentException("Platform verification exception requires at least one error.", nameof(errors));

        Errors = errors;
    }

    /// <summary>单次验证中发现的所有启动验证错误。</summary>
    public IReadOnlyList<PalPlatformVerificationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<PalPlatformVerificationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return $"Pal platform verification failed with {errors.Count} error(s).";
    }
}
