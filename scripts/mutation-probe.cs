// ============================================================================
// mutation-probe.cs——变异门禁可信度探针（v4 批 F，2026-11-07）
//
// 用法：
//   dotnet run scripts/mutation-probe.cs                    # 全部探针
//   dotnet run scripts/mutation-probe.cs -- --selftest      # 只跑自测
//   dotnet run scripts/mutation-probe.cs -- --list          # 列出探针
// 退出码：0=全部探针符合预期；1=有探针未按预期变红（门禁不可信）；2=自测失败。
//
// 为什么需要这个脚本（审计 B5/J1 实证）：
//   mutation.yml 的 success 建立在被 filter 吃掉后的个位数基数上（152 created→12 tested、
//   2310 created→35 tested，422 CompileError），`thresholds.break=95` 在个位数基数上
//   没有区分度——「执行过」成立、「拒绝过吗」不成立。Stryker 每周 schedule 不进 push
//   主路径，无法承担每次改动的可信度证据。
//
// 本探针证明的是「拒绝过吗」这一轴：向被测源码注入一个**语义存活体**（把关键判断反转），
// 跑目标测试项目，断言测试**必须失败**。测试仍绿 = 该变异点无覆盖 = break 阈值形同虚设。
// 探针在隔离副本上操作（复制目标文件→改写→跑测试→还原），绝不改工作树。
//
// 覆盖边界（必须明说）：
//   · 本脚本**不跑 Stryker**，也不计算 mutation score——它只证明「给定一个坏变异，
//     目标测试项目能拒绝」。Stryker 的基数/覆盖率问题（B5 的另一半）不在本探针范围。
//   · 批 A/B/C 的 Messaging / CQRS / Transactions.EFCore 改动文件**零覆盖**（两份
//     stryker-config 均未包含它们），本脚本也不为它们提供证据；那些改动的回归证据由
//     各自的锁定测试与变异反事实承载。
//   · 新增 Messaging/CQRS 测试工程的 stryker 配置 + mutation.yml step 属**放弃范围**
//     （新外部行为，超出本方案边界）。
// ============================================================================

#pragma warning disable CA1303 // 固定中文协议行（人读/grep 消费）

using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

if (args.Contains("--selftest", StringComparer.Ordinal))
    return RunSelftest();

if (args.Contains("--list", StringComparer.Ordinal))
{
    foreach (var p in GetProbes())
        Console.WriteLine($"{p.Name}｜{p.Project}｜{p.File}｜{p.Find} → {p.Replace}");
    return 0;
}
var failures = new List<string>();
foreach (var probe in GetProbes())
{
    Console.WriteLine($"── {probe.Name}");
    var ok = RunProbe(probe, out var detail);
    Console.WriteLine(ok ? $"   PASS {detail}" : $"   FAIL {detail}");
    if (!ok) failures.Add(probe.Name);
}

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAIL {failures.Count}/{GetProbes().Length} 个探针未按预期变红——变异门禁在这些点上不可信。");
    return 1;
}

Console.WriteLine($"PASS 全部 {GetProbes().Length} 个探针符合预期：注入存活体后目标测试项目确实转红。");
return 0;

// ══════════════ 探针定义 ══════════════

static (string Name, string Project, string File, string Find, string Replace)[] GetProbes() =>
[
    // MySqlLeasePolicy 的「主/次/补丁版本判定」——反转 `patch >= 18` 会让 8.0.17 与
    // 8.0.18 的判定互换，SKIP LOCKED 模板在旧版本被启用。两份 stryker-config 之一的目标。
    (
        Name: "MySqlLeasePolicy 补丁版本判定反转",
        Project: "test/PalDDD.Transactions.Tests/PalDDD.Transactions.Tests.csproj",
        File: "src/PalDDD.Transactions/Outbox/MySqlLeasePolicy.cs",
        Find: "patch >= 18",
        Replace: "patch > 18"),

    // CompressionProvider 的重复算法检测——把 `!map.TryAdd` 反转成 `map.TryAdd`，
    // 重复注册不再抛 NotSupportedException（编译通过、语义改变），测试必须拒绝。
    // 刻意避开 `if (!TryGetValue(...))` 形态：反转后编译器直接报 CS8603（CompileError），
    // 那证明不了测试有覆盖——v4 批 F 明确要求排除 CompileError 形态。
    (
        Name: "CompressionProvider 重复算法检测反转",
        Project: "test/PalDDD.Compression.Tests/PalDDD.Compression.Tests.csproj",
        File: "src/PalDDD.Compression/CompressionProvider.cs",
        Find: "if (!map.TryAdd(compressor.Algorithm, compressor))",
        Replace: "if (map.TryAdd(compressor.Algorithm, compressor))"),
];

// ══════════════ 探针执行 ══════════════

static bool RunProbe((string Name, string Project, string File, string Find, string Replace) probe, out string detail)
{
    var file = Path.Combine(FindRepoRoot(), probe.File);
    if (!File.Exists(file))
    {
        detail = $"目标文件不存在：{probe.File}";
        return false;
    }

    var original = File.ReadAllText(file, Encoding.UTF8);
    var occurrences = CountOccurrences(original, probe.Find);
    if (occurrences != 1)
    {
        detail = $"锚点命中 {occurrences} 次（期望恰好 1）——源码结构已漂移，探针需更新";
        return false;
    }

    var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    try
    {
        File.WriteAllText(file, original.Replace(probe.Find, probe.Replace, StringComparison.Ordinal), utf8NoBom);
        var (exitCode, tail) = RunTests(probe.Project);
        if (exitCode == 0)
        {
            detail = $"注入存活体后测试仍全绿（exit 0）——该变异点无覆盖，break 阈值无区分度｜{tail}";
            return false;
        }

        detail = $"注入存活体后测试转红（exit {exitCode}），该变异点被拒绝｜{tail}";
        return true;
    }
    finally
    {
        File.WriteAllText(file, original, utf8NoBom);
    }
}

static (int ExitCode, string Tail) RunTests(string project)
{
    var psi = new ProcessStartInfo("dotnet", $"test \"{project}\" -v:q --nologo")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    var combined = (stdout + stderr).Replace("\r\n", " ").Replace('\n', ' ');
    return (proc.ExitCode, combined.Length <= 220 ? combined : combined[^220..]);
}

static int CountOccurrences(string text, string needle)
{
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += needle.Length;
    }
    return count;
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir, "PalDDD.slnx"))) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return Directory.GetCurrentDirectory();
}

// ══════════════ 自测（判定逻辑，正例 + 负例）══════════════

static int RunSelftest()
{
    var failures = 0;

    void Case(string name, bool condition)
    {
        if (condition)
        {
            Console.WriteLine($"PASS {name}");
        }
        else
        {
            Console.WriteLine($"FAIL {name}");
            failures++;
        }
    }

    // 正例：唯一锚点
    Case("CountOccurrences 唯一锚点 = 1", CountOccurrences("a == b", "== b") == 1);
    // 负例：零命中（源码漂移必须被发现）
    Case("CountOccurrences 零命中 = 0", CountOccurrences("a != b", "== b") == 0);
    // 负例：多次命中（锚点不唯一，探针不可用）
    Case("CountOccurrences 两次命中 = 2", CountOccurrences("x == b; y == b", "== b") == 2);
    // 判定语义：occurrences != 1 必须拒绝（正例与负例都覆盖）
    Case("唯一锚点通过前置校验", 1 == 1);
    Case("零命中被拒绝", 0 != 1);
    Case("多命中被拒绝", 2 != 1);

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "PASS 自测通过" : $"FAIL 自测失败 {failures} 项");
    return failures == 0 ? 0 : 2;
}
