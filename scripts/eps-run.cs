// ============================================================================
// eps-run.cs——EPSL 快速/全量编排器（2026-10-19 全量运行经验沉淀）
//
// 为什么需要它：EPSL 的检查件（eps-perf-check）、L2 门禁（--smoke）与 16 条
// 基准车道此前靠人工逐条拼命令，2026-10-19 全量运行时踩到两类真实问题：
//   1) 全量与外部 build/test/bench 并发时，BDN InProcess 子进程会无异常 exit 1，
//      失败类随机漂移（SagaState/Compression/Configuration 都出现过）——单类或
//      独占环境复跑即通过。故全量预检要求工作树干净、打印并发 dotnet 进程数，
//      并对已知易受干扰的 --infra 默认失败重试一次（仍红才判失败）。
//   2) 类数对得上不代表类真的被入口引用（新增基准忘了接 Program.cs 会漏跑）。
//      该事实由 eps-perf-check 的“基准类接入”检查负责；本工具只负责运行编排。
//
// 用法：
//   dotnet run scripts/eps-run.cs -- --quick
//       跑 EPSL 检查件（selftest + 项目对账）+ --smoke 分配预算门禁。
//   dotnet run scripts/eps-run.cs -- --full [--strict] [--allow-dirty] [--include-remote] [--retry N]
//       独占全量：预检 → 构建 → 17 条本地车道 → 汇总/日志。
//   dotnet run scripts/eps-run.cs -- --lane <name> [--strict]
//       只跑一条车道（仍做工作树/构建预检）。
//   dotnet run scripts/eps-run.cs -- --list
//   dotnet run scripts/eps-run.cs -- --dry-run --full
//   dotnet run scripts/eps-run.cs -- --selftest
//
// 退出码：0=全过；1=有门禁/车道失败；2=参数/预检/环境失败。
// ============================================================================

// Justification: CA1303——固定中文协议行（人读/grep 消费），沿 ci-coverage.cs 先例。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Globalization;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal)) return SelfTest();
if (args.Contains("--list", StringComparer.Ordinal))
{
    PrintLanes();
    return 0;
}

// 本次运行起点：日志清理据此区分「本次创建的目录」与历史目录。
RunScope.StartedAtUtc = DateTime.UtcNow;

var (options, parseError) = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine($"ERROR: {parseError}");
    return 2;
}

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

if (options.DryRun)
{
    PrintDryRun(options);
    return 0;
}

if (options.Quick) return await RunQuickAsync(options);

using var runLock = AcquireRunLock(root);
if (runLock is null)
{
    Console.Error.WriteLine("ERROR: 另一个 eps-run 正在运行（bench/logs/.eps-run.lock 被占用）");
    return 2;
}

var preflight = await PreflightAsync(options);
if (preflight != 0) return preflight;

return options.Full
    ? await RunFullAsync(options)
    : await RunSingleAsync(options);

// ══════════════ 运行模式 ══════════════

static async Task<int> RunQuickAsync(Options options)
{
    var results = new List<LaneResult>();
    var checker = await RunCheckerAsync();
    results.Add(checker);
    if (checker.Status == "FAIL") return Finish(results, options);

    var smoke = await RunLaneAsync(LaneByName("smoke")!, options, attempts: 1);
    results.Add(smoke);
    return Finish(results, options);
}

static async Task<int> RunFullAsync(Options options)
{
    var checker = await RunCheckerAsync();
    var results = new List<LaneResult> { checker };
    if (checker.Status == "FAIL") return Finish(results, options);

    foreach (var lane in SelectLanes(includeRemote: options.IncludeRemote))
    {
        if (lane.Remote && !RemoteReady(lane))
        {
            results.Add(new LaneResult(lane.Name, "SKIP", TimeSpan.Zero, 0, "",
                $"缺少 Docker 或 {RemoteEnvName(lane)} 连接串"));
            Console.WriteLine($"SKIP {lane.Name}（{results[^1].Detail}）");
            continue;
        }

        var attempts = options.Retry + 1;
        var result = await RunLaneAsync(lane, options, attempts);
        results.Add(result);
    }

    return Finish(results, options);
}

static async Task<int> RunSingleAsync(Options options)
{
    var lane = LaneByName(options.Lane!);
    if (lane is null)
    {
        Console.Error.WriteLine($"ERROR: 未知车道「{options.Lane}」；--list 查看清单");
        return 2;
    }
    if (lane.Remote && !RemoteReady(lane))
    {
        Console.Error.WriteLine($"SKIP {lane.Name}：缺少 Docker 或 {RemoteEnvName(lane)} 连接串");
        return 0;
    }

    var result = await RunLaneAsync(lane, options, options.Retry + 1);
    return Finish([result], options);
}

static async Task<LaneResult> RunLaneAsync(Lane lane, Options options, int attempts)
{
    var logDir = Path.Combine("bench", "logs", "eps-run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(logDir);
    var logPath = Path.GetFullPath(Path.Combine(logDir, lane.Name + ".log"));
    var psi = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("--configuration");
    psi.ArgumentList.Add("Release");
    psi.ArgumentList.Add("--project");
    psi.ArgumentList.Add(Path.Combine("bench", "PalDDD.Benchmarks", "PalDDD.Benchmarks.csproj"));
    psi.ArgumentList.Add("--no-build");
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add("--" + lane.Flag);
    if (options.Strict) psi.ArgumentList.Add("--strict");

    var total = Stopwatch.StartNew();
    var lastOutput = "";
    var actualAttempts = 0;
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        actualAttempts = attempt;
        Console.WriteLine($">> lane {lane.Name}（attempt {attempt}/{attempts}）…");
        var (exitCode, output) = await RunCapturedAsync(psi, LaneTimeout(lane), logPath);
        lastOutput = output;
        if (exitCode == 0)
        {
            total.Stop();
            Console.WriteLine($"PASS {lane.Name}（{total.Elapsed.TotalSeconds:F0}s，log {logPath}）");
            return new LaneResult(lane.Name, "PASS", total.Elapsed, actualAttempts, logPath, "");
        }

        Console.Error.WriteLine($"FAIL {lane.Name}（exit={exitCode}，attempt {attempt}/{attempts}）");
        PrintTail(lastOutput, 20);
        if (attempt < attempts)
        {
            Console.WriteLine("   30 秒后重试（全量基准要求独占；确认没有并发 build/test/bench）…");
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    }

    total.Stop();
    return new LaneResult(lane.Name, "FAIL", total.Elapsed, actualAttempts, logPath, $"exit≠0（{actualAttempts} 次）");
}

static async Task<LaneResult> RunCheckerAsync()
{
    var logDir = Path.Combine("bench", "logs", "eps-run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(logDir);
    var logPath = Path.GetFullPath(Path.Combine(logDir, "eps-perf-check.log"));
    var checker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zcode", "scripts", "eps-perf-check.mjs");
    if (!File.Exists(checker))
    {
        return new LaneResult("eps-perf-check", "FAIL", TimeSpan.Zero, 1, "", $"检查器不存在：{checker}");
    }

    var output = new StringBuilder();
    var total = Stopwatch.StartNew();
    foreach (var invocation in new[] { new[] { "--selftest" }, new[] { "--project", "." } })
    {
        var psi = new ProcessStartInfo("node") { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        psi.ArgumentList.Add(checker);
        foreach (var argument in invocation) psi.ArgumentList.Add(argument);
        var (exitCode, text) = await RunCapturedAsync(psi, TimeSpan.FromMinutes(2), logPath + "." + invocation[0].TrimStart('-'));
        output.AppendLine(text);
        if (exitCode != 0)
        {
            total.Stop();
            File.WriteAllText(logPath, output.ToString(), new UTF8Encoding(false));
            Console.Error.WriteLine($"FAIL eps-perf-check（{invocation[0]}，exit={exitCode}）");
            PrintTail(text, 20);
            return new LaneResult("eps-perf-check", "FAIL", total.Elapsed, 1, logPath, $"{invocation[0]} exit≠0");
        }
    }
    total.Stop();
    File.WriteAllText(logPath, output.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"PASS eps-perf-check（{total.Elapsed.TotalSeconds:F0}s）");
    return new LaneResult("eps-perf-check", "PASS", total.Elapsed, 1, logPath, "");
}

// ══════════════ 预检 ══════════════

static async Task<int> PreflightAsync(Options options)
{
    var head = Git("rev-parse", "--short", "HEAD");
    var branch = Git("rev-parse", "--abbrev-ref", "HEAD");
    var status = Git("status", "--porcelain");
    Console.WriteLine($"== EPSL 预检 == HEAD {head} / branch {branch}");
    if (status.Length > 0 && !options.AllowDirty)
    {
        Console.Error.WriteLine("ERROR: 工作树不干净，基准数字不可复核。提交/暂存后重试，或显式 --allow-dirty。");
        Console.Error.WriteLine("   " + status.Replace("\n", "\n   ", StringComparison.Ordinal));
        return 2;
    }
    if (status.Length > 0)
        Console.WriteLine("WARN 工作树不干净（--allow-dirty 已指定），结果只作趋势参考。");

    var dotnetCount = Math.Max(0, Process.GetProcessesByName("dotnet").Length - 1);
    Console.WriteLine($">> 并发提示：检测到 {dotnetCount} 个其他 dotnet 进程；全量基准要求独占。");
    if (dotnetCount > 0)
        Console.WriteLine("   若出现无异常 exit 1，先确认没有并发 build/test/bench，再复跑该车道。");

    if (!options.SkipBuild)
    {
        var build = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        build.ArgumentList.Add("build");
        build.ArgumentList.Add("PalDDD.slnx");
        build.ArgumentList.Add("-c");
        build.ArgumentList.Add("Release");
        build.ArgumentList.Add("-v");
        build.ArgumentList.Add("minimal");
        var (exitCode, _) = await RunCapturedAsync(build, TimeSpan.FromMinutes(15), Path.Combine(Path.GetTempPath(), "eps-run-build.log"));
        if (exitCode != 0)
        {
            Console.Error.WriteLine("ERROR: Release 构建失败，未启动基准。");
            return 2;
        }
    }
    return 0;
}

// 远程真库车道单次可超过本地量级：2026-10-23 persist-mysql 实测约 56 分钟，
// 45 分钟上限连续两轮被截断（exit=-1），只能手工单跑补齐。本地保持 45 分钟。
static TimeSpan LaneTimeout(Lane lane)
    => lane.Remote ? TimeSpan.FromMinutes(90) : TimeSpan.FromMinutes(45);

// ══════════════ 汇总/日志 ══════════════

static int Finish(IReadOnlyList<LaneResult> results, Options options)
{
    var logRoot = Path.Combine("bench", "logs");
    Directory.CreateDirectory(logRoot);
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var summaryPath = Path.GetFullPath(Path.Combine(logRoot, $"eps-run-{stamp}-summary.md"));
    var head = Git("rev-parse", "--short", "HEAD");
    var failed = results.Count(r => r.Status == "FAIL");
    var skipped = results.Count(r => r.Status == "SKIP");

    var sb = new StringBuilder();
    sb.AppendLine("# EPSL run summary");
    sb.AppendLine();
    sb.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
    sb.AppendLine($"- HEAD：{head}");
    sb.AppendLine($"- dotnet：{DotnetVersion()}");
    sb.AppendLine($"- 结果：{results.Count(r => r.Status == "PASS")} PASS / {failed} FAIL / {skipped} SKIP");
    sb.AppendLine();
    sb.AppendLine("| 车道 | 结果 | 耗时 | 尝试 | 日志/备注 |");
    sb.AppendLine("| --- | --- | ---: | ---: | --- |");
    foreach (var r in results)
    {
        var detail = string.IsNullOrEmpty(r.LogPath) ? r.Detail : r.LogPath;
        sb.AppendLine($"| {r.Name} | {r.Status} | {r.Duration.TotalSeconds:F0}s | {r.Attempts} | {detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
    }
    File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine();
    Console.WriteLine($"== 汇总：{results.Count(r => r.Status == "PASS")} PASS / {failed} FAIL / {skipped} SKIP ==");
    foreach (var r in results)
        Console.WriteLine($"  {r.Status,-4} {r.Name,-16} {r.Duration.TotalSeconds,5:F0}s  {r.Detail}");
    Console.WriteLine($"summary: {summaryPath}");
    PruneOldLogs(logRoot);
    return failed == 0 ? 0 : 1;
}

static void PruneOldLogs(string logRoot)
{
    try
    {
        var stale = SelectStaleLogDirs(
            Directory.GetDirectories(logRoot, "eps-run-*"),
            Directory.GetLastWriteTimeUtc,
            RunScope.StartedAtUtc,
            keep: 20);
        foreach (var dir in stale) Directory.Delete(dir, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"WARN 旧日志清理失败（不影响本次结果）：{ex.Message}");
    }
}

// 选出可删除的日志目录：本次运行创建的目录一律保留，历史目录按最后写入时间保留最新 keep 份。
// 2026-10-23 实证：一次 --full 创建 20+ 个车道目录，旧的「全局保留 20 份」会在运行结束时
// 删掉本次最早的 smoke/app 明细，事后回溯只能重跑。
static string[] SelectStaleLogDirs(
    IReadOnlyList<string> dirs,
    Func<string, DateTime> lastWriteUtc,
    DateTime runStartedAtUtc,
    int keep)
    => dirs
        .Where(dir => lastWriteUtc(dir) < runStartedAtUtc)
        .OrderByDescending(lastWriteUtc)
        .Skip(keep)
        .ToArray();

// ══════════════ 车道清单/参数 ══════════════

static Lane[] SelectLanes(bool includeRemote)
{
    var lanes = new List<Lane>
    {
        LaneByName("smoke")!,
        LaneByName("app")!,
        LaneByName("framework")!,
        LaneByName("verify-persist")!,
        LaneByName("infra")!,
        LaneByName("coverage")!,
        LaneByName("concurrency")!,
        LaneByName("saga")!,
        LaneByName("compression")!,
        LaneByName("persist")!,
        LaneByName("persist-medium")!,
        LaneByName("persist-mark")!,
        LaneByName("stream")!,
        LaneByName("delivery")!,
        LaneByName("qps")!,
        LaneByName("compare")!,
        LaneByName("sqlgen")!,
    };
    if (includeRemote)
    {
        lanes.Add(LaneByName("persist-pg")!);
        lanes.Add(LaneByName("persist-mysql")!);
        lanes.Add(LaneByName("delivery-pg")!);
        lanes.Add(LaneByName("delivery-mysql")!);
    }
    return [.. lanes];
}

static Lane? LaneByName(string name) => Lanes().FirstOrDefault(l => l.Name == name);

static Lane[] Lanes() =>
[
    new("smoke", "smoke", "L2 分配预算门禁", false),
    new("app", "app", "CQRS 分发 + 序列化", false),
    new("framework", "framework", "领域事件/值对象/SmartEnum/验证/创建", false),
    new("verify-persist", "verify-persist", "持久化探针自检", false),
    new("infra", "infra", "Outbox/Inbox/EventLog/SagaState/SqlGen/Configuration/Compression", false),
    new("coverage", "coverage", "Projection/演化/Specification/Hosting 端到端", false),
    new("concurrency", "concurrency", "Outbox 并发租约", false),
    new("saga", "saga", "Saga 编排", false),
    new("compression", "compression", "五算法压缩", false),
    new("persist", "persist", "SQLite 持久化", false),
    new("persist-medium", "persist-medium", "SQLite 租约锚", false),
    new("persist-mark", "persist-mark", "标记段成本", false),
    new("stream", "stream", "SSE 流式读", false),
    new("delivery", "delivery", "投递排空/节奏", false),
    new("qps", "qps", "绝对 QPS harness", false),
    new("compare", "compare", "MediatR/CAP 对比", false),
    new("sqlgen", "sqlgen", "SQLite FTS/JSON SQL 生成", false),
    new("persist-pg", "persist-pg", "PG 真库车道", true),
    new("persist-mysql", "persist-mysql", "MySQL 真库车道", true),
    new("delivery-pg", "delivery-pg", "PG 远程投递", true),
    new("delivery-mysql", "delivery-mysql", "MySQL 远程投递", true),
];

static bool RemoteReady(Lane lane)
{
    var env = RemoteEnvName(lane);
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env))) return true;
    try
    {
        var psi = new ProcessStartInfo("docker", "info")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        if (!p.WaitForExit(3000))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
            {
                // 进程已退出/无权限/平台不支持——按不可用处理
            }
            return false;
        }
        return p.ExitCode == 0;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return false;
    }
}

static string RemoteEnvName(Lane lane) => lane.Name.Contains("mysql", StringComparison.Ordinal)
    ? "PALDDD_BENCH_MYSQL_CONN"
    : "PALDDD_BENCH_PG_CONN";

static (Options? Options, string? Error) ParseArgs(string[] arguments)
{
    var quick = false;
    var full = false;
    var strict = false;
    var allowDirty = false;
    var includeRemote = false;
    var dryRun = false;
    var skipBuild = false;
    string? lane = null;
    var retry = -1;

    for (var i = 0; i < arguments.Length; i++)
    {
        var arg = arguments[i];
        switch (arg)
        {
            case "--quick": quick = true; break;
            case "--full": full = true; break;
            case "--strict": strict = true; break;
            case "--allow-dirty": allowDirty = true; break;
            case "--include-remote": includeRemote = true; break;
            case "--dry-run": dryRun = true; break;
            case "--skip-build": skipBuild = true; break;
            case "--lane":
                if (++i >= arguments.Length) return (null, "--lane 需要名称");
                lane = arguments[i];
                break;
            case "--retry":
                if (++i >= arguments.Length || !int.TryParse(arguments[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out retry) || retry is < 0 or > 3)
                    return (null, "--retry 需要 0..3 的整数");
                break;
            default:
                return (null, $"未知参数：{arg}");
        }
    }

    var modeCount = (quick ? 1 : 0) + (full ? 1 : 0) + (lane is not null ? 1 : 0);
    if (modeCount != 1) return (null, "必须且只能指定 --quick / --full / --lane <name> 之一");
    if (lane is not null && LaneByName(lane) is null)
        return (null, $"未知车道「{lane}」；--list 查看清单");
    if (retry < 0) retry = full ? 1 : 0;
    return (new Options(quick, full, lane, strict, allowDirty, includeRemote, dryRun, skipBuild, retry), null);
}

static void PrintLanes()
{
    Console.WriteLine("EPSL 车道：");
    foreach (var lane in Lanes())
    {
        var remote = lane.Remote ? " [remote]" : "";
        Console.WriteLine($"  {lane.Name,-16} --{lane.Flag,-16} {lane.Description}{remote}");
    }
    Console.WriteLine();
    Console.WriteLine("--quick = eps-perf-check（selftest + 项目对账）+ --smoke");
    Console.WriteLine("--full  = 以上 + 17 条本地车道；--include-remote 追加 4 条真库车道");
}

static void PrintDryRun(Options options)
{
    if (options.Quick)
    {
        Console.WriteLine("node ~/.zcode/scripts/eps-perf-check.mjs --selftest");
        Console.WriteLine("node ~/.zcode/scripts/eps-perf-check.mjs --project .");
        Console.WriteLine("dotnet run -c Release --project bench/PalDDD.Benchmarks --no-build -- --smoke");
        return;
    }

    var lanes = options.Full
        ? SelectLanes(options.IncludeRemote)
        : [LaneByName(options.Lane!)!];
    foreach (var lane in lanes)
        Console.WriteLine($"dotnet run -c Release --project bench/PalDDD.Benchmarks --no-build -- --{lane.Flag}{(options.Strict ? " --strict" : "")}");
}

// ══════════════ 基础设施 ══════════════

static async Task<(int ExitCode, string Output)> RunCapturedAsync(ProcessStartInfo psi, TimeSpan timeout, string logPath)
{
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;
    using var process = Process.Start(psi);
    if (process is null) return (-1, "进程启动失败");

    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var cts = new CancellationTokenSource(timeout);
    var timedOut = false;
    try
    {
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        timedOut = true;
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
            // 进程已退出/无权限/平台不支持
        }
    }

    var output = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    await File.WriteAllTextAsync(logPath, output, new UTF8Encoding(false)).ConfigureAwait(false);
    return (timedOut ? -1 : process.ExitCode, output);
}

static void PrintTail(string text, int lines)
{
    var tail = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
    foreach (var line in tail.Skip(Math.Max(0, tail.Length - lines)))
        Console.Error.WriteLine("   " + line);
}

static string Git(params string[] arguments)
{
    try
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi);
        if (p is null) return "";
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : "";
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return "";
    }
}

static string DotnetVersion()
{
    try
    {
        var psi = new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        return p is null ? "?" : p.StandardOutput.ReadToEnd().Trim();
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return "?";
    }
}

static FileStream? AcquireRunLock(string root)
{
    var dir = Path.Combine(root, "bench", "logs");
    Directory.CreateDirectory(dir);
    try
    {
        return new FileStream(Path.Combine(dir, ".eps-run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        return null;
    }
}

static string FindRepoRoot()
{
    foreach (var start in (string?[])[Environment.CurrentDirectory, AppContext.BaseDirectory])
    {
        var dir = start;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "PalDDD.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
    Console.Error.WriteLine("ERROR: 未定位到仓库根（无 PalDDD.slnx）");
    Environment.Exit(2);
    return "";
}

// ══════════════ 自测（纯参数/清单逻辑，不跑基准） ══════════════

static int SelfTest()
{
    var failures = new List<string>();
    void Case(string name, bool ok)
    {
        if (ok) Console.WriteLine($"PASS ST-EPSRUN {name}");
        else failures.Add(name);
    }

    var (full, fullError) = ParseArgs(["--full"]);
    Case("--full 解析", fullError is null && full is { Full: true, Quick: false, Lane: null, Retry: 1 });
    var (quick, quickError) = ParseArgs(["--quick"]);
    Case("--quick 解析", quickError is null && quick is { Quick: true, Retry: 0 });
    var (lane, laneError) = ParseArgs(["--lane", "infra", "--strict", "--retry", "2"]);
    Case("--lane 解析 + strict/retry", laneError is null && lane is { Lane: "infra", Strict: true, Retry: 2 });
    Case("模式冲突必红", ParseArgs(["--quick", "--full"]).Error is not null);
    Case("未知参数必红", ParseArgs(["--nope"]).Error is not null);
    Case("--retry 越界必红", ParseArgs(["--full", "--retry", "9"]).Error is not null);
    Case("--lane 未知名称必红（含 --dry-run）", ParseArgs(["--lane", "nope"]).Error is not null && ParseArgs(["--dry-run", "--lane", "nope"]).Error is not null);
    Case("本地全量不含 remote", SelectLanes(includeRemote: false).All(l => !l.Remote) && SelectLanes(false).Length == 17);
    Case("--include-remote 追加 4 条", SelectLanes(includeRemote: true).Length == SelectLanes(false).Length + 4);
    Case("每条车道 flag 唯一", Lanes().Select(l => l.Flag).Distinct(StringComparer.Ordinal).Count() == Lanes().Length);
    Case(
        "远程车道超时 90 分钟 / 本地 45 分钟",
        LaneTimeout(LaneByName("persist-mysql")!) == TimeSpan.FromMinutes(90)
        && LaneTimeout(LaneByName("persist")!) == TimeSpan.FromMinutes(45));

    var runStart = new DateTime(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);
    var beforeRun = runStart.AddHours(-2);
    var mtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal)
    {
        ["old1"] = beforeRun,
        ["old2"] = beforeRun.AddMinutes(1),
        ["old3"] = beforeRun.AddMinutes(2),
        ["cur1"] = runStart.AddMinutes(5),
        ["cur2"] = runStart.AddMinutes(6),
    };
    DateTime Mtime(string dir) => mtimes[dir];
    string[] allDirs = ["old1", "old2", "old3", "cur1", "cur2"];
    var stale = SelectStaleLogDirs(allDirs, Mtime, runStart, keep: 1);
    Case("日志清理保留本次运行目录", !stale.Contains("cur1") && !stale.Contains("cur2"));
    Case("日志清理按时间保留历史最新 keep 份", stale.Length == 2 && stale.Contains("old1") && stale.Contains("old2") && !stale.Contains("old3"));
    Case("历史目录不足 keep 不删", SelectStaleLogDirs(["old1"], Mtime, runStart, keep: 5).Length == 0);

    Console.WriteLine($"═══════ EPS-RUN SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var failure in failures) Console.WriteLine("  FAIL " + failure);
    return failures.Count == 0 ? 0 : 2;
}

// 顶层类型声明（top-level program 要求类型声明位于所有语句之后）
internal sealed record Lane(string Name, string Flag, string Description, bool Remote);
internal sealed record Options(
    bool Quick, bool Full, string? Lane, bool Strict, bool AllowDirty,
    bool IncludeRemote, bool DryRun, bool SkipBuild, int Retry);
internal sealed record LaneResult(string Name, string Status, TimeSpan Duration, int Attempts, string LogPath, string Detail);

/// <summary>本次进程的运行起点（日志清理据此区分本次目录与历史目录）。</summary>
internal static class RunScope
{
    public static DateTime StartedAtUtc { get; set; }
}
