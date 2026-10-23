// ============================================================================
// eps-run.cs——EPSL 快速/全量编排器（2026-10-19 全量运行经验沉淀；2026-10-23 注册表化）
//
// 为什么需要它：EPSL 的检查件（eps-perf-check）、L2 门禁（--smoke）与 16 条
// 基准车道此前靠人工逐条拼命令，2026-10-19 全量运行时踩到两类真实问题：
//   1) 全量与外部 build/test/bench 并发时，BDN InProcess 子进程会无异常 exit 1，
//      失败类随机漂移（SagaState/Compression/Configuration 都出现过）——单类或
//      独占环境复跑即通过。故全量预检要求工作树干净、打印并发 dotnet 进程数，
//      并对易受干扰的车道默认失败重试一次（仍红才判失败）。
//   2) 类数对得上不代表类真的被入口引用（新增基准忘了接 Program.cs 会漏跑）。
//      该事实由 eps-perf-check 的“基准类接入”检查负责；本工具只负责运行编排。
//
// 2026-10-23：车道、profile、超时、远程与路径触发迁到 bench/bench-registry.json
// 单一真源。本工具的 --list 与 bench 的 --list-lanes 读同一份文件；覆盖对账由
// scripts/perf-registry-check.cs 负责。
//
// 用法：
//   dotnet run scripts/eps-run.cs -- --quick
//       跑 EPSL 检查件（selftest + 项目对账）+ --smoke 分配预算门禁。
//   dotnet run scripts/eps-run.cs -- --full [--strict] [--allow-dirty] [--include-remote] [--retry N]
//       独占全量：预检 → 构建 → full profile 16 条执行车道（persist-medium 为聚焦别名）。
//   dotnet run scripts/eps-run.cs -- --profile standard [--changed] [--changed-base <ref>]
//       常规档：10 条车道，≤10 分钟；--changed 按注册表 paths 过滤，未知路径回退全量 standard。
//   dotnet run scripts/eps-run.cs -- --profile remote-smoke|remote-full
//       远程冒烟 / 远程全量。
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
// Justification: 本工具是开发者编排脚本，不是 AOT 发布物；注册表与 summary.json 的
// 反射式 JSON 序列化在工具进程内成立。AOT 发布物是 src/ 与 samples/，不进本脚本。
// CA1812：Lane/RegistryFile 由 JSON 反序列化创建，分析器看不到构造函数调用。
#pragma warning disable IL2026, IL3050, CA1812

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

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

return options.Lane is not null
    ? await RunSingleAsync(options)
    : await RunProfileAsync(options);

// ══════════════ 运行模式 ══════════════

static async Task<int> RunQuickAsync(Options options)
{
    var results = new List<LaneResult>();
    var checker = await RunCheckerOrSkipAsync(options);
    results.Add(checker);
    if (checker.Status == "FAIL") return Finish(results, options);

    var smoke = await RunLaneAsync(Registry.Lane("smoke"), options, attempts: 1);
    results.Add(smoke);
    return Finish(results, options);
}

static async Task<int> RunProfileAsync(Options options)
{
    var checker = await RunCheckerOrSkipAsync(options);
    var results = new List<LaneResult> { checker };
    if (checker.Status == "FAIL") return Finish(results, options);

    var profile = options.Profile ?? "full";
    var lanes = Registry.Profile(profile).ToList();
    if (options.IncludeRemote)
        lanes.AddRange(Registry.Profile("remote-full"));

    if (options.Changed)
    {
        if (profile != "standard")
        {
            Console.Error.WriteLine("ERROR: --changed 只适用于 standard profile（发布/参考全量永不过滤）");
            return 2;
        }
        lanes = SelectChangedLanes(lanes, options.ChangedBase);
    }

    foreach (var lane in lanes)
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
    var lane = Registry.Lane(options.Lane!);
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
    psi.ArgumentList.Add("--lane");
    psi.ArgumentList.Add(lane.Name);
    if (options.Strict) psi.ArgumentList.Add("--strict");

    var total = Stopwatch.StartNew();
    var lastOutput = "";
    var actualAttempts = 0;
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        actualAttempts = attempt;
        Console.WriteLine($">> lane {lane.Name}（{lane.Mode}，attempt {attempt}/{attempts}）…");
        var (exitCode, output) = await RunCapturedAsync(psi, TimeSpan.FromMinutes(lane.TimeoutMinutes), logPath);
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

static async Task<LaneResult> RunCheckerOrSkipAsync(Options options)
{
    if (!options.NoChecker) return await RunCheckerAsync();
    Console.WriteLine("SKIP eps-perf-check（--no-checker；CI 由 perf-registry-check 与 perf-smoke 承担）");
    return new LaneResult("eps-perf-check", "SKIP", TimeSpan.Zero, 0, "", "--no-checker");
}

static async Task<LaneResult> RunCheckerAsync()
{    var logDir = Path.Combine("bench", "logs", "eps-run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
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
    else
    {
        var stale = CheckBuildFreshness(Environment.CurrentDirectory);
        if (stale is not null)
        {
            Console.Error.WriteLine($"ERROR: --skip-build 但基准程序集不新鲜：{stale}");
            Console.Error.WriteLine("   先跑 dotnet build PalDDD.slnx -c Release，或去掉 --skip-build。");
            return 2;
        }
    }
    return 0;
}

// 二进制新鲜度守卫：--skip-build 只允许在程序集晚于全部编译输入（.cs/.csproj）时使用。
// 注册表是运行时读取的数据文件，不参与编译，故不列入（改 JSON 不需要重建 DLL）。
// 用时间戳而非哈希：构建是单点动作，时间戳足以拦截「改了源文件却用旧 DLL 验证」。
static string? CheckBuildFreshness(string root)
{
    var dll = Path.Combine(root, "bench", "PalDDD.Benchmarks", "bin", "Release", "net11.0", "PalDDD.Benchmarks.dll");
    if (!File.Exists(dll)) return "基准程序集不存在";
    var dllTime = File.GetLastWriteTimeUtc(dll);
    var inputs = new List<string>();
    foreach (var dir in new[] { Path.Combine(root, "bench"), Path.Combine(root, "src") })
    {
        if (!Directory.Exists(dir)) continue;
        inputs.AddRange(Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
    }
    foreach (var file in new[] { Path.Combine(root, "Directory.Build.props"), Path.Combine(root, "Directory.Packages.props") })
        if (File.Exists(file)) inputs.Add(file);

    var newest = inputs.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
    return newest > dllTime
        ? $"最新输入 {newest:yyyy-MM-dd HH:mm:ss}Z 晚于程序集 {dllTime:yyyy-MM-dd HH:mm:ss}Z"
        : null;
}

// ══════════════ 汇总/日志 ══════════════

static int Finish(IReadOnlyList<LaneResult> results, Options options)
{
    var logRoot = Path.Combine("bench", "logs");
    Directory.CreateDirectory(logRoot);
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var summaryPath = Path.GetFullPath(Path.Combine(logRoot, $"eps-run-{stamp}-summary.md"));
    var jsonPath = Path.GetFullPath(Path.Combine(logRoot, $"eps-run-{stamp}-summary.json"));
    var head = Git("rev-parse", "--short", "HEAD");
    var failed = results.Count(r => r.Status == "FAIL");
    var skipped = results.Count(r => r.Status == "SKIP");

    var sb = new StringBuilder();
    sb.AppendLine("# EPSL run summary");
    sb.AppendLine();
    sb.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
    sb.AppendLine($"- HEAD：{head}");
    sb.AppendLine($"- profile：{(options.Quick ? "quick" : options.Lane is not null ? "lane" : options.Profile)}");
    sb.AppendLine($"- dotnet：{DotnetVersion()}");
    sb.AppendLine($"- 结果：{results.Count(r => r.Status == "PASS")} PASS / {failed} FAIL / {skipped} SKIP");
    sb.AppendLine();
    sb.AppendLine("| 车道 | 结果 | 耗时 | 尝试 | mode | 日志/备注 |");
    sb.AppendLine("| --- | --- | ---: | ---: | --- | --- |");
    foreach (var r in results)
    {
        var lane = Registry.Lanes.FirstOrDefault(l => l.Name == r.Name);
        var detail = string.IsNullOrEmpty(r.LogPath) ? r.Detail : r.LogPath;
        sb.AppendLine($"| {r.Name} | {r.Status} | {r.Duration.TotalSeconds:F0}s | {r.Attempts} | {lane?.Mode ?? "n/a"} | {detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
    }
    File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(false));

    var json = JsonSerializer.Serialize(new
    {
        timestamp = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
        head,
        dotnet = DotnetVersion(),
        profile = options.Quick ? "quick" : options.Lane is not null ? "lane" : options.Profile,
        results = results.Select(r => new
        {
            lane = r.Name,
            mode = Registry.Lanes.FirstOrDefault(l => l.Name == r.Name)?.Mode ?? "n/a",
            status = r.Status,
            seconds = Math.Round(r.Duration.TotalSeconds, 1),
            attempts = r.Attempts,
            log = r.LogPath,
            detail = r.Detail,
        }),
    }, SummaryJson.Options);
    File.WriteAllText(jsonPath, json, new UTF8Encoding(false));

    Console.WriteLine();
    Console.WriteLine($"== 汇总：{results.Count(r => r.Status == "PASS")} PASS / {failed} FAIL / {skipped} SKIP ==");
    foreach (var r in results)
        Console.WriteLine($"  {r.Status,-4} {r.Name,-16} {r.Duration.TotalSeconds,5:F0}s  {r.Detail}");
    Console.WriteLine($"summary: {summaryPath}");
    Console.WriteLine($"summary.json: {jsonPath}");
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

// ══════════════ 路径触发 ══════════════

static List<Lane> SelectChangedLanes(IReadOnlyList<Lane> standardLanes, string baseRef)
{
    var changed = ChangedFiles(baseRef);
    if (changed.Count == 0)
    {
        Console.WriteLine(">> --changed：未检测到变更文件，回退全部 standard 车道");
        return [.. standardLanes];
    }

    var matched = standardLanes
        .Where(l => l.Name != "smoke" && l.Paths.Any(p => changed.Any(f => PathMatches(p, f))))
        .ToList();
    if (matched.Count == 0)
    {
        Console.WriteLine(">> --changed：变更路径未命中任何 paths，回退全部 standard 车道");
        return [.. standardLanes];
    }

    var smoke = standardLanes.FirstOrDefault(l => l.Name == "smoke");
    if (smoke is not null) matched.Insert(0, smoke);
    Console.WriteLine(">> --changed 命中：" + string.Join(", ", matched.Select(l => l.Name)));
    return matched;
}

static List<string> ChangedFiles(string baseRef)
{
    var output = Git("diff", "--name-only", baseRef);
    if (output.Length == 0) output = Git("diff", "--name-only");
    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

static bool PathMatches(string pattern, string path)
{
    var regex = "^" + Regex.Escape(pattern.Replace('\\', '/'))
        .Replace("\\*\\*", ".*", StringComparison.Ordinal)
        .Replace("\\*", "[^/]*", StringComparison.Ordinal) + "$";
    return Regex.IsMatch(path.Replace('\\', '/'), regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
}

// ══════════════ 车道清单/参数 ══════════════

static void PrintLanes()
{
    Console.WriteLine("EPSL 车道（bench/bench-registry.json）：");
    foreach (var lane in Registry.Lanes)
    {
        var profiles = lane.Profiles.Count == 0 ? "tool" : string.Join("/", lane.Profiles);
        var remote = lane.Remote ? " [remote]" : "";
        Console.WriteLine($"  {lane.Name,-18} --{lane.Flag,-18} {profiles,-24} mode={lane.Mode,-12} {lane.TimeoutMinutes,2}m {lane.ExpectedSeconds,5}s{remote}");
    }
    Console.WriteLine();
    Console.WriteLine("--quick   = eps-perf-check（selftest + 项目对账）+ --smoke");
    Console.WriteLine("--full    = 以上 + full profile（16 条执行车道；persist-medium 为聚焦别名）");
    Console.WriteLine("--profile standard|remote-smoke|remote-full|strict");
    Console.WriteLine("--include-remote 追加 4 条真库车道");
}

static void PrintDryRun(Options options)
{
    if (options.Quick)
    {
        Console.WriteLine("node ~/.zcode/scripts/eps-perf-check.mjs --selftest");
        Console.WriteLine("node ~/.zcode/scripts/eps-perf-check.mjs --project .");
        Console.WriteLine("dotnet run -c Release --project bench/PalDDD.Benchmarks --no-build -- --lane smoke");
        return;
    }

    List<Lane> lanes = options.Lane is not null
        ? [Registry.Lane(options.Lane)]
        : [.. Registry.Profile(options.Profile ?? "full").Concat(options.IncludeRemote ? Registry.Profile("remote-full") : [])];
    if (options.Changed) lanes = SelectChangedLanes(lanes, options.ChangedBase);
    foreach (var lane in lanes)
        Console.WriteLine($"dotnet run -c Release --project bench/PalDDD.Benchmarks --no-build -- --lane {lane.Name}{(options.Strict ? " --strict" : "")}");
}

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
    var profile = (string?)null;
    var strict = false;
    var allowDirty = false;
    var includeRemote = false;
    var dryRun = false;
    var skipBuild = false;
    var noChecker = false;
    var changed = false;
    var changedBase = "HEAD~1";
    string? lane = null;
    var retry = -1;

    for (var i = 0; i < arguments.Length; i++)
    {
        var arg = arguments[i];
        switch (arg)
        {
            case "--quick": quick = true; break;
            case "--full": profile = "full"; break;
            case "--profile":
                if (++i >= arguments.Length) return (null, "--profile 需要名称");
                profile = arguments[i];
                break;
            case "--strict": strict = true; break;
            case "--allow-dirty": allowDirty = true; break;
            case "--include-remote": includeRemote = true; break;
            case "--dry-run": dryRun = true; break;
            case "--skip-build": skipBuild = true; break;
            case "--no-checker": noChecker = true; break;
            case "--changed": changed = true; break;
            case "--changed-base":
                if (++i >= arguments.Length) return (null, "--changed-base 需要 git ref");
                changedBase = arguments[i];
                break;
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

    var modeCount = (quick ? 1 : 0) + (profile is not null ? 1 : 0) + (lane is not null ? 1 : 0);
    if (modeCount != 1) return (null, "必须且只能指定 --quick / --full / --profile / --lane <name> 之一");
    if (lane is not null && !Registry.Lanes.Any(l => l.Name == lane))
        return (null, $"未知车道「{lane}」；--list 查看清单");
    if (profile is not null && !Registry.Lanes.Any(l => l.Profiles.Contains(profile, StringComparer.Ordinal)))
        return (null, $"未知 profile「{profile}」；可用：{string.Join(", ", Registry.Lanes.SelectMany(l => l.Profiles).Distinct())}");
    if (includeRemote && profile != "full")
        return (null, "--include-remote 只适用于 --full（远程专用 profile 用 --profile remote-full）");
    if (changed && profile != "standard")
        return (null, "--changed 只适用于 --profile standard");
    if (retry < 0) retry = profile is "full" ? 1 : 0;
    return (new Options(quick, profile, lane, strict, allowDirty, includeRemote, dryRun, skipBuild, noChecker, changed, changedBase, retry), null);
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

// ══════════════ 自测（纯参数/清单/路径逻辑，不跑基准） ══════════════

static int SelfTest()
{
    var failures = new List<string>();
    void Case(string name, bool ok)
    {
        if (ok) Console.WriteLine($"PASS ST-EPSRUN {name}");
        else failures.Add(name);
    }

    var (full, fullError) = ParseArgs(["--full"]);
    Case("--full 解析", fullError is null && full is { Profile: "full", Quick: false, Lane: null, Retry: 1 });
    var (standard, standardError) = ParseArgs(["--profile", "standard"]);
    Case("--profile standard 解析", standardError is null && standard is { Profile: "standard", Retry: 0 });
    var (quick, quickError) = ParseArgs(["--quick"]);
    Case("--quick 解析", quickError is null && quick is { Quick: true, Retry: 0 });
    var (lane, laneError) = ParseArgs(["--lane", "infra", "--strict", "--retry", "2"]);
    Case("--lane 解析 + strict/retry", laneError is null && lane is { Lane: "infra", Strict: true, Retry: 2 });
    Case("模式冲突必红", ParseArgs(["--quick", "--full"]).Error is not null);
    Case("未知参数必红", ParseArgs(["--nope"]).Error is not null);
    Case("--retry 越界必红", ParseArgs(["--full", "--retry", "9"]).Error is not null);
    Case("--lane 未知名称必红（含 --dry-run）", ParseArgs(["--lane", "nope"]).Error is not null && ParseArgs(["--dry-run", "--lane", "nope"]).Error is not null);
    Case("--profile 未知名称必红", ParseArgs(["--profile", "nope"]).Error is not null);
    Case("--changed 只允许 standard", ParseArgs(["--full", "--changed"]).Error is not null);
    Case("--include-remote 只允许 full", ParseArgs(["--profile", "standard", "--include-remote"]).Error is not null);
    Case("full profile 16 条执行车道", Registry.Profile("full").Count() == 16);
    Case("standard profile 9 条车道", Registry.Profile("standard").Count() == 9);
    Case("remote-full 4 条", Registry.Profile("remote-full").Count() == 4);
    Case("remote-smoke 2 条", Registry.Profile("remote-smoke").Count() == 2);
    Case("gate 只有 smoke", Registry.Profile("gate").Select(l => l.Name).SequenceEqual(["smoke"]));
    Case("strict 只有 strict-core", Registry.Profile("strict").Select(l => l.Name).SequenceEqual(["strict-core"]));
    Case("每条车道 flag 唯一", Registry.Lanes.Select(l => l.Flag).Distinct(StringComparer.Ordinal).Count() == Registry.Lanes.Count);
    Case("远程车道超时来自注册表", Registry.Lane("persist-mysql").TimeoutMinutes == 90 && Registry.Lane("persist").TimeoutMinutes == 45);
    Case("路径匹配 core", PathMatches("src/PalDDD.Core/**", "src/PalDDD.Core/Entity.cs"));
    Case("路径匹配不误伤", !PathMatches("src/PalDDD.Core/**", "src/PalDDD.Dapper/Store.cs"));
    Case("路径匹配单星号", PathMatches("src/PalDDD.*/x.cs", "src/PalDDD.Core/x.cs") && !PathMatches("src/PalDDD.*/x.cs", "src/PalDDD.Core/sub/x.cs"));

    var changed = SelectChangedLanesForTest(Registry.Profile("standard").ToList(), []);
    Case("--changed 无变更回退全部 standard", changed.Count == 9);
    var changedMatched = SelectChangedLanesForTest(Registry.Profile("standard").ToList(), ["src/PalDDD.Core/Entity.cs"]);
    Case("--changed 命中核心路径", changedMatched.Any(l => l.Name == "framework") && changedMatched.Any(l => l.Name == "smoke") && !changedMatched.Any(l => l.Name == "delivery"));
    var changedUnknown = SelectChangedLanesForTest(Registry.Profile("standard").ToList(), ["docs/unknown.md"]);
    Case("--changed 未知路径回退全部 standard", changedUnknown.Count == 9);

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

static List<Lane> SelectChangedLanesForTest(IReadOnlyList<Lane> standardLanes, IReadOnlyList<string> changed)
{
    var matched = standardLanes
        .Where(l => l.Name != "smoke" && l.Paths.Any(p => changed.Any(f => PathMatches(p, f))))
        .ToList();
    if (matched.Count == 0) return [.. standardLanes];
    var smoke = standardLanes.FirstOrDefault(l => l.Name == "smoke");
    if (smoke is not null) matched.Insert(0, smoke);
    return matched;
}

// 顶层类型声明（top-level program 要求类型声明位于所有语句之后）
internal sealed record Lane(
    string Name, string Flag, string Purpose, List<string> Profiles, string Mode,
    int TimeoutMinutes, bool Remote, int ExpectedSeconds, List<string> Paths);
internal sealed record Options(
    bool Quick, string? Profile, string? Lane, bool Strict, bool AllowDirty,
    bool IncludeRemote, bool DryRun, bool SkipBuild, bool NoChecker, bool Changed, string ChangedBase, int Retry);
internal sealed record LaneResult(string Name, string Status, TimeSpan Duration, int Attempts, string LogPath, string Detail);

/// <summary>本次进程的运行起点（日志清理据此区分本次目录与历史目录）。</summary>
internal static class RunScope
{
    public static DateTime StartedAtUtc { get; set; }
}

/// <summary>summary.json 的序列化选项（工具进程显式启用反射解析器）。</summary>
internal static class SummaryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}

/// <summary>bench/bench-registry.json 的编排视图（只读本工具需要的字段）。</summary>
internal sealed class RegistryFile
{
    public List<Lane> Lanes { get; init; } = [];
}

internal static class Registry
{
    private static readonly Lazy<RegistryFile> s_current = new(Load);
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static List<Lane> Lanes => s_current.Value.Lanes;

    public static Lane Lane(string name)
        => Lanes.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"注册表中不存在车道「{name}」");

    public static IEnumerable<Lane> Profile(string profile)
        => Lanes.Where(l => l.Profiles.Contains(profile, StringComparer.Ordinal));

    private static RegistryFile Load()
    {
        var path = FindRegistryPath();
        var file = JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(path), s_options)
            ?? throw new InvalidOperationException($"注册表反序列化为空：{path}");
        if (file.Lanes.Count == 0) throw new InvalidOperationException($"注册表 lanes 为空：{path}");
        if (file.Lanes.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count() != file.Lanes.Count)
            throw new InvalidOperationException($"注册表存在重复车道名：{path}");
        return file;
    }

    private static string FindRegistryPath()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "bench", "bench-registry.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }
        throw new FileNotFoundException("未找到 bench/bench-registry.json（从 CWD 与 BaseDirectory 向上查找）");
    }
}
