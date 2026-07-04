using System.Runtime.InteropServices;

namespace PalDDD.Core.Tests;

/// <summary>
/// 验证 .NET 11 迁移后的运行时环境和编译配置状态。
/// 这些测试在 net10.0 上会失败（红灯），在 net11.0 上应全部通过（绿灯）。
/// </summary>
public sealed class DotNet11MigrationTests
{
    [Test]
    public async Task RuntimeVersion_IsDotNet11OrHigher()
    {
        // 验收：运行时版本 >= 11.0
        // 在 net10.0 上 Environment.Version.Major == 10 → 失败（红灯）
        // 在 net11.0 上 Environment.Version.Major >= 11 → 通过（绿灯）
        await Assert.That(Environment.Version.Major >= 11).IsTrue();
    }

    [Test]
    public async Task RuntimeFrameworkDescription_ContainsNet11()
    {
        // 验收：框架描述包含 .NET 11
        var desc = RuntimeInformation.FrameworkDescription;
        await Assert.That(desc).Contains(".NET 11");
    }

    [Test]
    public async Task TargetFramework_IsNet11()
    {
        // 验收：编译目标为 net11.0（通过编译时常量检测）
        // 在 net10.0 上 NET11_0_OR_GREATER 未定义 → 失败
        // 在 net11.0 上 NET11_0_OR_GREATER 已定义 → 通过
#if NET11_0_OR_GREATER
        var frameworkIsNet11 = true;
        await Assert.That(frameworkIsNet11).IsTrue();
#else
        Assert.Fail("Target framework is not .NET 11. NET11_0_OR_GREATER is not defined.");
#endif
    }

    [Test]
    public async Task AotCompatibility_AnalyzerEnabled()
    {
        // 验收：AOT 兼容性分析器已启用（IsAotCompatible=true）
        // 通过检查编译时常量验证（AOT 分析器在构建时运行，零警告）
        // 此测试在构建时验证：所有 AOT 相关配置在 Directory.Build.props 中启用
        // 运行时验证：JsonSerializer.IsReflectionEnabledByDefault == false
        await Assert.That(System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault).IsFalse();
    }
}
