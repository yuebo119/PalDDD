// ─────────────────────────────────────────────────────────────
// 🧪 Kafka 消费链路健康位接线门禁 — R16 修复（2026-11-24，F1）
// ─────────────────────────────────────────────────────────────
// 缺陷：worker 循环的两条不可恢复终止 catch 只记 Error，不置 _consumeFaultReason。
// worker 死后 finally 取消 cts，消费线程走「token 已取消」分支静默退出（不置位），
// 订阅句柄仍有效 → IsConsumingHealthy 恒 true → /health 保持 Healthy，本实例已停止消费。
//
// 为什么用源码扫描而非行为测试：两条 catch 分别要求「循环骨架非关停 OCE」与
// 「channel.ReadAsync / Task.WhenAny / await completed 抛非 OCE 异常」——前者需在
// 循环骨架注入不可控的 token，后者需真 broker + 真 librdkafka。两者都不是能在
// 单元测试里稳定构造的形态。本仓既有同族防线（ArchitectureBoundaryTests 的源码扫描）
// 证明该形态可接受：断言的是「每个不可恢复终止点都置健康位」这条不变式。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Messaging.Integration.Tests;

/// <summary>
/// Kafka 消费链路健康位接线的源码级不变式。
/// </summary>
public sealed class KafkaConsumeHealthWiringTests
{
    /// <summary>
    /// 两条不可恢复终止 catch（消费线程 + worker）都必须给 <c>_consumeFaultReason</c> 赋值。
    /// 修前 worker 那条只记日志（R14 修复只覆盖了消费线程），本测试在修前会红。
    /// <para>
    /// 判据：块内出现 <c>_logger.Error</c> 且措辞含 <c>terminated</c>——这是「不可恢复终止」
    /// 的现有文案约定（两条 catch 都符合）。含 <c>continue</c> 的重试型 catch（消费线程的
    /// ConsumeException 分支）不属终止面，天然不命中。
    /// </para>
    /// </summary>
    [Test]
    public async Task EveryConsumeLoopTerminationCatch_SetsFaultReason()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "PalDDD.Messaging.Kafka", "KafkaBroker.cs");
        var lines = await File.ReadAllLinesAsync(path);

        var catches = new List<(int Line, string Header, bool SetsFault)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("catch (", StringComparison.Ordinal))
                continue;

            // 块边界：catch 头所在行的起始缩进。遇到缩进 <= 该缩进的 '}' 即块结束。
            var headerIndent = lines[i].Length - trimmed.Length;
            var depth = 0;
            var seenBrace = false;
            var setsFault = false;
            var logsTermination = false;
            var retries = false;
            for (var j = i; j < lines.Length; j++)
            {
                var text = lines[j];
                foreach (var ch in text)
                {
                    if (ch == '{') { depth++; seenBrace = true; }
                    else if (ch == '}') depth--;
                }
                var code = text.TrimStart();
                if (!code.StartsWith("//", StringComparison.Ordinal))
                {
                    if (text.Contains("_consumeFaultReason", StringComparison.Ordinal)
                        && (text.Contains("??=", StringComparison.Ordinal)
                            || System.Text.RegularExpressions.Regex.IsMatch(text, @"_consumeFaultReason\s*=")))
                    {
                        setsFault = true;
                    }
                    if (text.Contains("terminated", StringComparison.Ordinal)
                        && text.Contains("_logger.Error", StringComparison.Ordinal))
                    {
                        logsTermination = true;
                    }
                    if (code.StartsWith("continue;", StringComparison.Ordinal))
                        retries = true;
                }
                // 块闭合：已进入块体且深度回到 0
                if (seenBrace && depth <= 0)
                    break;
            }

            if (logsTermination && !retries)
                catches.Add((i + 1, trimmed, setsFault));
        }

        // 三条：消费专用线程终止（:633，R14 已修）+ worker 的非关停 OCE + worker 兜底 Exception
        await Assert.That(catches.Count).IsEqualTo(3)
            .Because($"应有 3 条不可恢复终止 catch（消费线程 + worker×2），实际 {catches.Count} 条：{string.Join("; ", catches.Select(c => $"{c.Line}: {c.Header}"))}");

        var missing = catches.Where(c => !c.SetsFault).ToArray();
        await Assert.That(missing.Length).IsEqualTo(0)
            .Because($"以下终止 catch 未置 _consumeFaultReason（worker 死而 /health 恒绿）：{string.Join("; ", missing.Select(c => $"KafkaBroker.cs:{c.Line} {c.Header}"))}");
    }

    /// <summary>
    /// R17 审计发现 D8 锁定：消费线程故障终止后重新 SubscribeAsync 恢复消费时，
    /// 必须清除粘滞的 <c>_consumeFaultReason</c>——否则消费已恢复而 /health 永久 Unhealthy，
    /// 实例被摘流量直到 broker 重启（Rabbit FIX-020 同族已修恢复清位，Kafka 未同步）。
    /// <para>
    /// 行为探针需真 broker 注入「消费线程异常终止 → 重订阅」，同既有测试的取舍用源码级
    /// 不变式：SubscribeAsync 体内必须有清零，且位于 SetConsumeTask（消费线程启动成功）
    /// 之后——清理点过早会在启动失败时误报健康。
    /// </para>
    /// </summary>
    [Test]
    public async Task SubscribeAsync_ClearsFaultReasonAfterConsumeTaskStarted()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "PalDDD.Messaging.Kafka", "KafkaBroker.cs");
        var text = await File.ReadAllTextAsync(path);

        var subscribeStart = text.IndexOf("public override ValueTask<IAsyncDisposable> SubscribeAsync", StringComparison.Ordinal);
        await Assert.That(subscribeStart).IsGreaterThan(-1).Because("未定位 SubscribeAsync");

        var setTask = text.IndexOf("subscription.SetConsumeTask(consumeTask)", subscribeStart, StringComparison.Ordinal);
        await Assert.That(setTask).IsGreaterThan(subscribeStart).Because("未定位消费线程启动点");

        var clear = text.IndexOf("_consumeFaultReason = null", subscribeStart, StringComparison.Ordinal);
        await Assert.That(clear).IsGreaterThan(setTask)
            .Because($"SubscribeAsync 必须在消费线程启动成功后清除粘滞故障位（修前全文零清除，/health 永久 Unhealthy）；实际 clear={clear}/setTask={setTask}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }
}
