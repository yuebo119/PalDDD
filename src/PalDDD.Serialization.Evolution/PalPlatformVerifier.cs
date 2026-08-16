// ─────────────────────────────────────────────────────────────
// ✅ PalPlatformVerifier — 启动期消息契约验证
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Serialization.Evolution;

// ─────────────────────────────────────────────────────────────
// 平台配置启动校验
// ─────────────────────────────────────────────────────────────

/// <summary>校验平台配置——必须在宿主开始服务前验证通过。</summary>
public sealed class PalPlatformVerifier
{
    /// <summary>校验所有必需的消息演化路径。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "验证器有意设为实例化，以便宿主包通过 DI 注册并扩展校验项而不破坏公共 API。")]
    public void ValidateMessageEvolutionPaths(
        MessageEvolutionPipeline pipeline,
        IEnumerable<MessageEvolutionPathRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(requirements);

        List<PalPlatformVerificationError>? errors = null;
        foreach (var requirement in requirements)
        {
            ArgumentNullException.ThrowIfNull(requirement);

            try
            {
                pipeline.ValidatePath(requirement.SourceDescriptor, requirement.TargetDescriptor);
            }
            // ITM-093 修复：补 OCE 过滤——pipeline.ValidatePath 实现内部可能抛出 OCE，
            // 不应在聚合阶段误收集；不吞 OutOfMemoryException 语义保持不变。
            // 此处聚合所有验证错误统一抛出；OutOfMemoryException 直接向上传播不收集。
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                errors ??= [];
                errors.Add(new PalPlatformVerificationError(requirement, exception));
            }
        }

        if (errors is { Count: > 0 })
            throw new PalPlatformVerificationException(errors);
    }

    /// <summary>校验契约清单所需的所有消息演化路径。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "验证器有意设为实例化，以便宿主包通过 DI 注册并扩展校验项而不破坏公共 API。")]
    public void ValidateMessageContractManifest(
        MessageEvolutionPipeline pipeline,
        MessageContractManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        ValidateMessageEvolutionPaths(pipeline, manifest.EvolutionRequirements);
    }
}
