// ─────────────────────────────────────────────────────────────
// 🧪 断线续跑 run 生产者异常的 Error 日志出口（R18 审计发现缺陷 4）
// ─────────────────────────────────────────────────────────────
// 缺陷：StreamRun.FinishAsync 只在新级 Debug 记 failure.GetType().Name，异常对象
// （消息 + 堆栈）无任何 Error 出口；同文件 StreamToAsync 的 attach 失败与
// EndpointExtensions 的普通流式读都记 Error(ex)。修前默认 Information 级别下
// 生产者异常完全不可见，StreamFailed 计数器与 span status 只给「失败 N 次/类型名」。
//
// 为什么用源码级不变式：生产者异常由 IStreamQueryHandler.HandleAsync 枚举抛出，
// 行为探针需注入抛异常 handler 并断言 IPalLogger 收到含异常对象的 Error 日志（本仓
// 同族健康位接线用源码级形态）。判据逐行扫描并跳过 // 注释行防假绿。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Hosting.AspNetCore.Tests;

/// <summary>
/// 断线续跑 run 失败日志出口的源码级不变式（R18 缺陷 4 锁定）。
/// </summary>
public sealed class StreamRunFailureLoggingTests
{
    /// <summary>
    /// FinishAsync 在 Failed 态必须给异常对象一个 Error 级出口（_logger.Error(failure, ...)）；
    /// 修前只有 Debug 记类型名，本测试红。
    /// </summary>
    [Test]
    public async Task FinishAsync_FailedState_LogsExceptionObjectAtErrorLevel()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(
            FindRepoRoot(), "src", "PalDDD.Hosting.AspNetCore", "AspNetCore", "StreamRunRegistry.cs"));
        var code = string.Join('\n', lines.Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var start = code.IndexOf("private async ValueTask FinishAsync", StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThan(-1).Because("未定位 FinishAsync");

        var brace = code.IndexOf('{', start);
        var depth = 0;
        var end = code.Length;
        for (var i = brace; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) { end = i + 1; break; }
        }
        var body = code[brace..end];

        await Assert.That(body.Contains("_logger.Error(failure", StringComparison.Ordinal)).IsTrue()
            .Because("FinishAsync 必须给生产者异常一个 Error 级出口（修前只有 Debug 记类型名，默认级别零输出）");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }
}
