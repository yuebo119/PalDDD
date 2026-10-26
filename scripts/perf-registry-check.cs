// ============================================================================
// perf-registry-check.cs——性能车道注册表覆盖门禁（2026-10-23）
//
// 目的：把「注册表声明 → 实际可跑基准」做成机械对账，防三类静默退化：
//   1) 新增基准类但没进注册表：类数对得上，full 却漏跑（沿 eps-perf-check 的教训）。
//   2) 注册表把同一个类放进两条 full/standard 车道：重复计时，预算虚高。
//   3) profile 预算超限 / 空 profile / 未知 mode / 过滤器方法不存在：配置腐烂。
//
// 单一真源：bench/bench-registry.json。平坦清单由 bench 的 --list flat 给出
// （--from-flat 可读离线文件，供 gate-audit 隔离探针使用，无需构建 bench 工程）。
//
// 用法：
//   dotnet run scripts/perf-registry-check.cs
//   dotnet run scripts/perf-registry-check.cs -- --inventory
//   dotnet run scripts/perf-registry-check.cs -- --from-flat bench/flat.txt
//   dotnet run scripts/perf-registry-check.cs -- --from-flat bench/flat.txt --skip-cases   # gate-audit 隔离探针专用
//   dotnet run scripts/perf-registry-check.cs -- --selftest
//
// 退出码：0=全过；1=有覆盖/配置错误；2=自测失败或参数错误。
// ============================================================================

// Justification: 工具脚本非 AOT 发布物；注册表 JSON 用显式反射解析器（全局
// JsonSerializerIsReflectionEnabledByDefault=false 的例外，沿 eps-run 先例）。
#pragma warning disable CA1303, CA1812, IL2026, IL3050

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--selftest", StringComparer.Ordinal)) return SelfTest();

var root = FindRepoRoot();
var registryPath = Path.Combine(root, "bench", "bench-registry.json");
if (!File.Exists(registryPath))
{
    Console.Error.WriteLine($"FAIL 注册表不存在：{registryPath}");
    return 1;
}

Registry registry;
try
{
    registry = JsonSerializer.Deserialize<Registry>(File.ReadAllText(registryPath), JsonOptionsHolder.Options)
        ?? throw new InvalidOperationException("注册表反序列化为空");
}
catch (Exception ex) when (ex is JsonException or InvalidOperationException)
{
    Console.Error.WriteLine($"FAIL 注册表解析失败：{ex.Message}");
    return 1;
}

var fromFlat = ReadArg(args, "--from-flat");
var flat = ReadFlat(root, fromFlat);
if (flat.Count == 0)
{
    Console.Error.WriteLine("FAIL 平坦清单为空（--list flat 失败或 --from-flat 文件为空）");
    return 1;
}

var caseCounts = ParseCaseCounts(root, flat);
var skipCases = args.Contains("--skip-cases", StringComparer.Ordinal);
var errors = Validate(registry, flat, caseCounts, expectedCases: skipCases ? -1 : 173);
if (args.Contains("--inventory", StringComparer.Ordinal))
    PrintInventory(registry, flat, caseCounts);

if (errors.Count > 0)
{
    Console.Error.WriteLine($"FAIL perf-registry-check：{errors.Count} 项");
    foreach (var error in errors) Console.Error.WriteLine("  - " + error);
    return 1;
}

var totalCases = flat.GroupBy(f => f.Class, StringComparer.Ordinal)
    .Sum(g => g.Count() * (caseCounts.TryGetValue(g.Key, out var n) ? n : 1));
Console.WriteLine($"PASS perf-registry-check：{flat.Select(f => f.Class).Distinct().Count()} 类 / {flat.Count} 方法 / {totalCases} case / {registry.Lanes.Count} 车道");
return 0;

// ══════════════ 校验 ══════════════

static List<string> Validate(
    Registry registry,
    IReadOnlyList<FlatMethod> flat,
    IReadOnlyDictionary<string, int> caseCounts,
    int expectedCases)
{
    var errors = new List<string>();
    var flatClasses = flat.Select(f => f.Class).Distinct(StringComparer.Ordinal).ToList();

    if (registry.Version <= 0) errors.Add("registry.version 必须 >0");
    if (registry.Lanes.Count == 0) errors.Add("registry.lanes 为空");

    var laneNames = registry.Lanes.Select(l => l.Name).ToList();
    foreach (var duplicate in laneNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        errors.Add($"车道名重复：{duplicate.Key}");
    foreach (var duplicate in registry.Lanes.Select(l => l.Flag).GroupBy(f => f, StringComparer.Ordinal).Where(g => g.Count() > 1))
        errors.Add($"车道 flag 重复：{duplicate.Key}");

    foreach (var lane in registry.Lanes)
    {
        if (string.IsNullOrWhiteSpace(lane.Name)) errors.Add("存在空车道名");
        if (lane.Mode != "n/a" && !registry.Modes.ContainsKey(lane.Mode))
            errors.Add($"车道 {lane.Name} mode「{lane.Mode}」不在 modes 中");
        foreach (var pair in lane.Overrides)
        {
            if (!flatClasses.Contains(pair.Key, StringComparer.Ordinal))
                errors.Add($"车道 {lane.Name} override 指向未发现的类 {pair.Key}");
            if (!registry.Modes.ContainsKey(pair.Value))
                errors.Add($"车道 {lane.Name} override {pair.Key} 指向未知 mode「{pair.Value}」");
        }
        if (lane.TimeoutMinutes <= 0) errors.Add($"车道 {lane.Name} timeoutMinutes 必须 >0");
        if (lane.ExpectedSeconds <= 0) errors.Add($"车道 {lane.Name} expectedSeconds 必须 >0");
        if (lane.Remote && lane.TimeoutMinutes < 10) errors.Add($"远程车道 {lane.Name} 超时 <10 分钟");
        if (lane.Profiles.Count == 0 && lane.Name is not ("persist-medium")) errors.Add($"车道 {lane.Name} 不属于任何 profile（只允许 persist-medium 为 tool）");
        foreach (var className in lane.Classes)
            if (!flatClasses.Contains(className, StringComparer.Ordinal))
                errors.Add($"车道 {lane.Name} 引用了未发现的类 {className}");
        foreach (var filter in lane.Filter)
            if (!lane.Classes.Any(c => flat.Where(f => f.Class == c).Any(f => f.Method == filter)))
                errors.Add($"车道 {lane.Name} 的过滤器方法 {filter} 不在任何类中");
    }

    // 每个基准类至少有一条车道归属（防新增类漏注册）
    var laneClasses = registry.Lanes.SelectMany(l => l.Classes).ToHashSet(StringComparer.Ordinal);
    foreach (var className in flatClasses.Where(c => !laneClasses.Contains(c)))
        errors.Add($"类 {className} 不在任何车道中（新增类必须登记注册表）");

    // 每个 profile 非空且预算不超
    var targetMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gate"] = "gate",
        ["standard"] = "standard",
        ["full"] = "full",
        ["remote-smoke"] = "remoteSmoke",
        ["remote-full"] = "remoteFull",
        ["strict"] = "strict",
    };
    foreach (var profile in targetMap.Keys)
    {
        var lanes = registry.Lanes.Where(l => l.Profiles.Contains(profile, StringComparer.Ordinal)).ToList();
        if (lanes.Count == 0) { errors.Add($"profile {profile} 没有车道"); continue; }
        if (!registry.TargetsSeconds.TryGetValue(targetMap[profile], out var target))
        {
            errors.Add($"targetsSeconds 缺少 {targetMap[profile]}");
            continue;
        }
        var budget = lanes.Sum(l => l.ExpectedSeconds);
        if (budget > target) errors.Add($"profile {profile} 预算 {budget}s > 目标 {target}s");
    }

    // 本地 full/standard 不得重复跑同一个类
    foreach (var profile in new[] { "gate", "standard", "full", "strict" })
    {
        var duplicates = registry.Lanes
            .Where(l => l.Profiles.Contains(profile, StringComparer.Ordinal))
            .SelectMany(l => l.Classes.Select(c => (Lane: l.Name, Class: c)))
            .GroupBy(x => x.Class, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var group in duplicates)
            errors.Add($"profile {profile} 重复跑类 {group.Key}：{string.Join(", ", group.Select(g => g.Lane))}");
    }

    var remoteClasses = registry.Lanes.Where(l => l.Remote).SelectMany(l => l.Classes).ToHashSet(StringComparer.Ordinal);
    var fullLanes = registry.Lanes.Where(l => l.Profiles.Contains("full", StringComparer.Ordinal)).ToList();
    var fullClasses = fullLanes.SelectMany(l => l.Classes).ToHashSet(StringComparer.Ordinal);
    foreach (var className in flatClasses.Where(c => !remoteClasses.Contains(c)))
        if (!fullClasses.Contains(className))
            errors.Add($"full profile 漏跑本地类 {className}");
    foreach (var className in remoteClasses.Where(c => fullClasses.Contains(c)))
        errors.Add($"full profile 混入远程类 {className}");

    // 每个方法都必须能被某个 lane 的 filter 覆盖（无 filter 即全类）
    foreach (var method in flat)
    {
        var covered = registry.Lanes
            .Where(l => l.Classes.Contains(method.Class, StringComparer.Ordinal))
            .Any(l => l.Filter.Count == 0 || l.Filter.Contains(method.Method, StringComparer.Ordinal));
        if (!covered) errors.Add($"方法 {method.Class}.{method.Method} 不被任何车道覆盖（filter 误杀）");
    }

    // case 数：方法数 × Params 笛卡尔积（--skip-cases 供 gate-audit 隔离探针使用，
    // 隔离目录没有真实 bench 源文件；真实仓库运行不允许跳过）。
    if (expectedCases >= 0)
    {
        var actualCases = 0;
        foreach (var group in flat.GroupBy(f => f.Class, StringComparer.Ordinal))
        {
            if (!caseCounts.TryGetValue(group.Key, out var perMethod) || perMethod <= 0)
            {
                errors.Add($"类 {group.Key} 的 [Params] 无法解析（fail-closed）");
                continue;
            }
            actualCases += group.Count() * perMethod;
        }
        if (actualCases != expectedCases)
            errors.Add($"case 总数 {actualCases} != {expectedCases}（方法数 × Params 展开；新增参数须同步报告与预算）");
    }

    return errors;
}

// ══════════════ 平坦清单与 Params ══════════════

static string? ReadArg(string[] arguments, string name)
{
    for (var i = 0; i < arguments.Length - 1; i++)
        if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
            return arguments[i + 1];
    return null;
}

static List<FlatMethod> ReadFlat(string root, string? fromFlat)
{
    if (fromFlat is not null)
    {
        if (!File.Exists(fromFlat))
        {
            Console.Error.WriteLine($"FAIL --from-flat 文件不存在：{fromFlat}");
            return [];
        }
        return ParseFlat(File.ReadAllLines(fromFlat));
    }

    var project = Path.Combine(root, "bench", "PalDDD.Benchmarks", "PalDDD.Benchmarks.csproj");
    var psi = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = root,
    };
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("--configuration");
    psi.ArgumentList.Add("Release");
    psi.ArgumentList.Add("--project");
    psi.ArgumentList.Add(project);
    psi.ArgumentList.Add("--no-build");
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add("--list");
    psi.ArgumentList.Add("flat");
    using var process = Process.Start(psi);
    if (process is null) return [];
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? ParseFlat(output.Split('\n')) : [];
}

static List<FlatMethod> ParseFlat(IEnumerable<string> lines)
{
    var methods = new List<FlatMethod>();
    foreach (var raw in lines)
    {
        var line = raw.Trim().TrimEnd('\r');
        if (!line.StartsWith("PalDDD.Benchmarks.", StringComparison.Ordinal)) continue;
        var parts = line.Split('.');
        if (parts.Length < 4) continue;
        methods.Add(new FlatMethod(parts[2], parts[3]));
    }
    return methods;
}

static Dictionary<string, int> ParseCaseCounts(string root, IReadOnlyList<FlatMethod> flat)
{
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    var benchDir = Path.Combine(root, "bench", "PalDDD.Benchmarks");
    if (!Directory.Exists(benchDir)) return counts;
    var files = Directory.EnumerateFiles(benchDir, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Select(File.ReadAllText)
        .ToList();

    foreach (var className in flat.Select(f => f.Class).Distinct(StringComparer.Ordinal))
    {
        var block = files.Select(text => ExtractClassBlock(text, className)).FirstOrDefault(b => b is not null);
        if (block is null)
        {
            counts[className] = 0;
            continue;
        }
        var product = 1;
        foreach (Match match in Regex.Matches(block, @"\[Params\((?<args>[^)]*)\)\]", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            var values = match.Groups["args"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0) product = 0;
            else product *= values.Length;
        }
        counts[className] = product;
    }
    return counts;
}

static string? ExtractClassBlock(string text, string className)
{
    var start = Regex.Match(text, $@"public\s+class\s+{Regex.Escape(className)}\b", RegexOptions.None, TimeSpan.FromSeconds(1));
    if (!start.Success) return null;
    var next = Regex.Match(text[(start.Index + start.Length)..], @"public\s+class\s+\w+", RegexOptions.None, TimeSpan.FromSeconds(1));
    var length = next.Success ? next.Index : text.Length - start.Index - start.Length;
    return text.Substring(start.Index, start.Length + Math.Max(0, length));
}

// ══════════════ 输出 ══════════════

static void PrintInventory(Registry registry, IReadOnlyList<FlatMethod> flat, IReadOnlyDictionary<string, int> caseCounts)
{
    Console.WriteLine("=== 性能车道矩阵 ===");
    foreach (var lane in registry.Lanes)
    {
        var profiles = lane.Profiles.Count == 0 ? "tool" : string.Join("/", lane.Profiles);
        var cases = lane.Classes.Sum(c => flat.Count(f => f.Class == c) * (caseCounts.TryGetValue(c, out var n) ? n : 0));
        Console.WriteLine($"{lane.Name,-18} {profiles,-24} mode={lane.Mode,-12} {lane.TimeoutMinutes,2}m {lane.ExpectedSeconds,5}s classes={lane.Classes.Count} cases={cases}{(lane.Remote ? " [remote]" : "")}");
    }
    Console.WriteLine();
    Console.WriteLine("=== 类 / 方法 / case ===");
    foreach (var group in flat.GroupBy(f => f.Class, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        var perMethod = caseCounts.TryGetValue(group.Key, out var n) ? n : 0;
        Console.WriteLine($"{group.Key,-48} methods={group.Count(),2} cases={group.Count() * perMethod,3}");
    }
}

static int SelfTest()
{
    var failures = new List<string>();
    void Case(string name, bool ok)
    {
        if (ok) Console.WriteLine($"PASS ST-REGISTRY {name}");
        else failures.Add(name);
    }

    var valid = new Registry
    {
        Version = 1,
        TargetsSeconds = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["gate"] = 10, ["standard"] = 100, ["full"] = 200,
            ["remoteSmoke"] = 100, ["remoteFull"] = 200, ["strict"] = 100,
        },
        Modes = new Dictionary<string, Mode>(StringComparer.Ordinal)
        {
            ["alloc"] = new() { Warmup = 2, Iterations = 3, Launch = 1, Timing = "none" },
        },
        Lanes =
        [
            LaneOf("smoke", ["gate", "standard", "full"], "n/a", [], 1, null),
            LaneOf("a", ["standard", "full"], "alloc", ["A"], 10, null),
            LaneOf("r", ["remote-full"], "alloc", ["R"], 10, null, remote: true),
            LaneOf("rs", ["remote-smoke"], "alloc", ["R"], 10, ["M2"], remote: true),
            LaneOf("s", ["strict"], "alloc", ["A"], 10, null),
        ],
    };
    var flat = new List<FlatMethod> { new("A", "M1"), new("R", "M2") };
    var cases = new Dictionary<string, int>(StringComparer.Ordinal) { ["A"] = 1, ["R"] = 1 };

    var validErrors = Validate(valid, flat, cases, 2);
    Case("有效注册表通过", validErrors.Count == 0);
    foreach (var error in validErrors) Console.WriteLine("  DEBUG " + error);

    var missing = Clone(valid);
    missing.Lanes.RemoveAll(l => l.Name is "a" or "s");
    Case("漏注册类必红", Validate(missing, flat, cases, 2).Any(e => e.Contains("不在任何车道")));

    var duplicate = Clone(valid);
    duplicate.Lanes.Add(LaneOf("a2", ["full"], "alloc", ["A"], 10, null));
    Case("full 重复类必红", Validate(duplicate, flat, cases, 2).Any(e => e.Contains("重复跑类")));

    var overBudget = Clone(valid);
    overBudget.Lanes.First(l => l.Name == "a").ExpectedSeconds = 500;
    Case("profile 预算超限必红", Validate(overBudget, flat, cases, 2).Any(e => e.Contains("预算")));

    var noProfile = Clone(valid);
    noProfile.Lanes.First(l => l.Name == "a").Profiles = [];
    Case("标准车道无 profile 必红", Validate(noProfile, flat, cases, 2).Any(e => e.Contains("不属于任何 profile")));

    var badMode = Clone(valid);
    badMode.Lanes.First(l => l.Name == "a").Mode = "nope";
    Case("未知 mode 必红", Validate(badMode, flat, cases, 2).Any(e => e.Contains("不在 modes")));

    var badFilter = Clone(valid);
    badFilter.Lanes.First(l => l.Name == "a").Filter = ["Nope"];
    Case("过滤器方法不存在必红", Validate(badFilter, flat, cases, 2).Any(e => e.Contains("过滤器方法")));

    Console.WriteLine($"═══════ PERF-REGISTRY SELFTEST：{(failures.Count == 0 ? "全部通过" : $"{failures.Count} 例失败")} ═══════");
    foreach (var failure in failures) Console.WriteLine("  FAIL " + failure);
    return failures.Count == 0 ? 0 : 2;
}

static Registry Clone(Registry source)
    => JsonSerializer.Deserialize<Registry>(JsonSerializer.Serialize(source, JsonOptionsHolder.Options), JsonOptionsHolder.Options)!;

static Lane LaneOf(
    string name, List<string> profiles, string mode, List<string> classes,
    int expectedSeconds, List<string>? filter, bool remote = false) => new()
    {
        Name = name,
        Flag = name,
        Profiles = profiles,
        Mode = mode,
        Classes = classes,
        ExpectedSeconds = expectedSeconds,
        TimeoutMinutes = 10,
        Remote = remote,
        Filter = filter ?? [],
    };

static string FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = start;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "PalDDD.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
    Console.Error.WriteLine("ERROR: 未定位到仓库根（无 PalDDD.slnx）");
    Environment.Exit(2);
    return "";
}

// 顶层类型：注册表（只取校验需要的字段）
internal sealed class Registry
{
    public int Version { get; init; }

    public Dictionary<string, int> TargetsSeconds { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, Mode> Modes { get; init; } = new(StringComparer.Ordinal);

    public List<Lane> Lanes { get; init; } = [];
}

internal sealed class Mode
{
    public int Warmup { get; init; }

    public int? Iterations { get; init; }

    public int Launch { get; init; }

    public int? Invocation { get; init; }

    public int? IterationTimeMs { get; init; }

    public string Timing { get; init; } = "trend";
}

internal sealed class Lane
{
    public string Name { get; init; } = "";

    public string Flag { get; init; } = "";

    public List<string> Profiles { get; set; } = [];

    public string Mode { get; set; } = "n/a";

    public Dictionary<string, string> Overrides { get; init; } = new(StringComparer.Ordinal);

    public List<string> Filter { get; set; } = [];

    public int TimeoutMinutes { get; init; }

    public bool Remote { get; init; }

    public int ExpectedSeconds { get; set; }

    public List<string> Classes { get; init; } = [];
}

internal sealed record FlatMethod(string Class, string Method);

internal static class JsonOptionsHolder
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}
