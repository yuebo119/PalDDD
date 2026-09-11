// flaky-parse.cs — TUnit 报告跨跑比对（MIG-011c 姊妹件：flaky-gate 的 python 核心 C# 化，2026-09-11）
// 用法：
//   dotnet run scripts/flaky-parse.cs -- <报告根目录> <跑数>          # 分析模式（run_N/**/*.tunit-report.json）
//   dotnet run scripts/flaky-parse.cs --gen-selftest <目录>          # 生成合成双跑报告（self-test 数据源）
//   dotnet run scripts/flaky-parse.cs -- --run <csproj> [--runs N]   # 真跑模式（MIG-012-B2：
//                                                                    #   dotnet build -c Release + N 次 dotnet test
//                                                                    #   --results-directory 后进入分析，
//                                                                    #   等价 .ai/scripts/flaky-gate.sh 真跑段）
// 退出码：0=无代码性 flaky；1=检出 code-flaky 或零报告守卫触发；2=用法错误。
// --run 模式下 build 失败透传 dotnet build 退出码（等价 bash set -e 行为）。
// 等价口径（与原 python 逐条对照）：
//   P=passed / S=skipped（三十七轮 P2-1：条件性 skip 非 fail）/ F=失败且异常不匹配环境模式 /
//   E=失败但异常含环境指纹（Npgsql/MySql/Socket/连接拒绝/broker 等 T-DDD-6 口径）；
//   同一测试跨跑 P/F 任意组合时该跑取最差态（F>E>P）；跨跑 distinct>1 才报；
//   distinct ⊆ {P,E,S} → env_mixed（WARN）；含 F → code_flaky（FAIL）；
//   零报告 = FAIL（三十七轮 P2-1：runner 崩溃不得静默通过）。
#pragma warning disable CA1303 // 诊断输出为 CI 控制台英文关键字（FAIL/WARN/SUMMARY 被 bash grep 消费），字面量必要

using System.Diagnostics;
using System.Text.Json;

var args2 = args.ToList();
if (args2.Count == 2 && args2[0] == "--gen-selftest")
{
    GenSelftest(args2[1]);
    return 0;
}
// MIG-012-B2：--run <csproj> [--runs N] 真跑模式（flaky-gate.sh 真跑段等价），
// 必须在旧两分支之前判（--run 不是 <报告根> <跑数> 形态）
if (args2.Count >= 2 && args2[0] == "--run")
{
    var proj = args2[1];
    var runs = 2; // 默认与 flaky-gate.sh 一致
    var i = 2;
    while (i < args2.Count)
    {
        if (args2[i] == "--runs" && i + 1 < args2.Count && int.TryParse(args2[i + 1], out var n))
        {
            runs = n;
            i += 2; // 跳过已消费的 N 值
        }
        else
        {
            Console.Error.WriteLine("用法: flaky-parse.cs --run <csproj> [--runs N] | <报告根> <跑数> | --gen-selftest <目录>");
            return 2;
        }
    }
    if (!File.Exists(proj))
    {
        Console.Error.WriteLine($"错误：项目文件不存在：{proj}");
        return 2;
    }
    return RunGate(proj, runs);
}
if (args2.Count != 2 || !int.TryParse(args2[1], out var runs2))
{
    Console.Error.WriteLine("用法: flaky-parse.cs --run <csproj> [--runs N] | <报告根> <跑数> | --gen-selftest <目录>");
    return 2;
}
return Analyze(args2[0], runs2);

// ─── 真跑模式（等价 flaky-gate.sh 主流程：build + N 次 test + analyze）───

static int RunGate(string proj, int runs)
{
    // 临时工作目录（等价 mktemp -d /tmp/palddd-flaky.XXXXXX + trap rm -rf EXIT——
    // finally 保证异常路径同样清理）
    var work = Path.Combine(Path.GetTempPath(), $"palddd-flaky.{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        Console.WriteLine($"═══════ flaky-gate：{proj} × {runs} 跑 ═══════");
        var buildRc = Dotnet(["build", proj, "-c", "Release", "--verbosity", "quiet"], redirect: false);
        if (buildRc != 0) return buildRc; // 等价 set -e：build 失败即退出并透传退出码
        for (var i = 1; i <= runs; i++)
        {
            Console.WriteLine($"── 第 {i}/{runs} 跑 ──");
            // 测试有失败不中断——flaky 检测正需要观察失败（输出丢弃，等价 >/dev/null 2>&1）
            _ = Dotnet(["test", proj, "--no-build", "-c", "Release", "--verbosity", "quiet",
                "--results-directory", Path.Combine(work, $"run_{i}")], redirect: true);
        }
        return Analyze(work, runs);
    }
    finally
    {
        Directory.Delete(work, recursive: true);
    }
}

static int Dotnet(string[] arguments, bool redirect)
{
    var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    if (redirect)
    {
        // 重定向时必须排空两流，防管道缓冲写满子进程阻塞
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        var drainOut = p.StandardOutput.ReadToEndAsync();
        var drainErr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return p.ExitCode;
    }
    using var plain = Process.Start(psi)!;
    plain.WaitForExit();
    return plain.ExitCode;
}

static int Analyze(string root, int runs)
{
    // 环境性失败指纹（T-DDD-6 口径，小写比较——与原 python 逐字一致）
    string[] envPatterns = ["Npgsql", "MySql", "Socket", "Connection refused", "connect timeout",
        "9092", "broker", "No such host", "connection closed"];

    var perRun = new List<Dictionary<string, char>>();
    var totalReports = 0;
    for (var i = 1; i <= runs; i++)
    {
        var map = new Dictionary<string, char>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, $"run_{i}"),
                     "*.tunit-report.json", SearchOption.AllDirectories))
        {
            totalReports++;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; } // 坏报告跳过（与原容错一致）
            foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
            {
                if (!g.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array) continue;
                foreach (var t in tests.EnumerateArray())
                {
                    var key = $"{Str(t, "className", "?")}.{Str(t, "methodName", "?")}";
                    var s = StatusOf(t, envPatterns);
                    map.TryGetValue(key, out var prev); // prev 默认 '\0' 视作 P
                    map[key] = Worst(s, prev is '\0' or 'P' ? 'P' : prev);
                }
            }
        }
        perRun.Add(map);
    }

    if (totalReports == 0)
    {
        Console.WriteLine($"::error ::FLAKY-GATE: No test reports found (runner crash or config error?) — treating as FAIL");
        Console.WriteLine($"SUMMARY runs={runs} code_flaky=0 env_mixed=0 reports=0 FAIL");
        return 1;
    }

    var codeFlaky = new List<string>();
    var envMixed = new List<string>();
    foreach (var key in perRun.SelectMany(m => m.Keys).Distinct())
    {
        var seq = perRun.Select(m => m.TryGetValue(key, out var s) ? s : 'P').ToArray();
        var distinct = seq.Distinct().ToHashSet();
        if (distinct.Count == 1) continue;
        var joined = string.Join("/", seq);
        if (distinct.IsSubsetOf(['P', 'E', 'S']))
            envMixed.Add($"{key}：{joined}（环境性/条件性不稳定——查环境或 skip 条件）");
        else
            codeFlaky.Add($"{key}：{joined}（代码性 flaky——重跑阈值内不稳定）");
    }

    foreach (var c in codeFlaky.OrderBy(x => x, StringComparer.Ordinal)) Console.WriteLine($"FAIL FLAKY {c}");
    foreach (var e in envMixed.OrderBy(x => x, StringComparer.Ordinal)) Console.WriteLine($"WARN ENVFLAKY {e}");
    Console.WriteLine($"SUMMARY runs={runs} code_flaky={codeFlaky.Count} env_mixed={envMixed.Count}");
    return codeFlaky.Count > 0 ? 1 : 0;
}

static char Worst(char a, char b) => a == 'F' || b == 'F' ? 'F' : a == 'E' || b == 'E' ? 'E' : 'P';

static char StatusOf(JsonElement t, string[] envPatterns)
{
    var status = Str(t, "status", "");
    if (status == "passed") return 'P';
    if (status == "skipped") return 'S';
    var ex = t.TryGetProperty("exception", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
    var msg = $"{Str(ex, "type", "")} {Str(ex, "message", "")}";
    return envPatterns.Any(p => msg.Contains(p, StringComparison.OrdinalIgnoreCase)) ? 'E' : 'F';
}

static string Str(JsonElement el, string prop, string fallback)
    => el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String
        ? fallback : v.GetString() ?? fallback;

static void GenSelftest(string dir)
{
    // 合成双跑报告（与原 python self-test 逐字段一致）：稳定 P/P、coin-flip P/F、环境 P/E(Npgsql)
    Directory.CreateDirectory(dir);
    Directory.CreateDirectory(Path.Combine(dir, "run_1"));
    Directory.CreateDirectory(Path.Combine(dir, "run_2"));
    var run1 = Rep([T("Stable_Passes", "passed", null), T("Flaky_CoinFlip", "passed", null), T("Env_Toggle", "passed", null)]);
    var run2 = Rep([T("Stable_Passes", "passed", null),
        T("Flaky_CoinFlip", "failed", ("AssertionException", "Expected 0")),
        T("Env_Toggle", "failed", ("NpgsqlException", "Connection refused"))]);
    File.WriteAllText(Path.Combine(dir, "run_1", "r.tunit-report.json"), run1);
    File.WriteAllText(Path.Combine(dir, "run_2", "r.tunit-report.json"), run2);

    static string Rep((string Name, string Status, (string Type, string Msg)? Ex)[] tests)
    {
        // 手工拼接（raw string 结尾引号与定界符咬合丢字符——ITM-658 同型教训，selftest 实证）
        var sb = new System.Text.StringBuilder("{\"schemaVersion\":1,\"groups\":[{\"tests\":[");
        foreach (var t in tests)
        {
            sb.Append("{\"className\":\"T\",\"methodName\":\"").Append(t.Name)
              .Append("\",\"status\":\"").Append(t.Status).Append('"');
            if (t.Ex is { } ex)
                sb.Append(",\"exception\":{\"type\":\"").Append(ex.Type)
                  .Append("\",\"message\":\"").Append(ex.Msg).Append("\"}");
            sb.Append("},");
        }
        if (tests.Length > 0) sb.Length--; // 去尾逗号
        sb.Append("]}]}");
        return sb.ToString();
    }
    static (string, string, (string, string)?) T(string n, string s, (string, string)? ex) => (n, s, ex);
}
