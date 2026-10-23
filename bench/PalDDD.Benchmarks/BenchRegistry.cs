// ============================================================================
// BenchRegistry.cs——性能车道与采样档的单一真源加载器
//
// 数据文件：bench/bench-registry.json。Program.cs 用它选择每类的测量档与覆盖；
// scripts/eps-run.cs 用同一份数据选择 profile、超时、远程与路径触发。
// scripts/perf-registry-check.cs 做覆盖对账（类/方法/case/profile/预算）。
//
// 查找顺序：CWD 向上找 bench/bench-registry.json，再 AppContext.BaseDirectory
// 向上找。这样 dotnet run（CWD=仓库根）、BDN 子进程（BaseDirectory=bin）与
// 隔离探针都能定位。文件缺失即抛异常，不静默退回旧硬编码。
// ============================================================================

using System.Text.Json;

namespace PalDDD.Benchmarks;

internal sealed class BenchRegistry
{
    private static readonly Lazy<BenchRegistry> s_current = new(LoadCore);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BenchRegistry Current => s_current.Value;

    public int Version { get; init; }

    public Dictionary<string, int> TargetsSeconds { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, ModeDef> Modes { get; init; } = new(StringComparer.Ordinal);

    public List<LaneDef> Lanes { get; init; } = [];

    public LaneDef Lane(string name)
        => Lanes.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"注册表中不存在车道「{name}」");

    public IEnumerable<LaneDef> Profile(string profile)
        => Lanes.Where(l => l.Profiles.Contains(profile, StringComparer.Ordinal));

    private static BenchRegistry LoadCore()
    {
        var path = FindRegistryPath();
        var json = File.ReadAllText(path);
        var registry = JsonSerializer.Deserialize<BenchRegistry>(json, s_jsonOptions)
            ?? throw new InvalidOperationException($"注册表反序列化为空：{path}");

        if (registry.Version <= 0)
            throw new InvalidOperationException($"注册表 version 无效：{path}");
        if (registry.Lanes.Count == 0)
            throw new InvalidOperationException($"注册表 lanes 为空：{path}");
        foreach (var lane in registry.Lanes)
        {
            if (string.IsNullOrWhiteSpace(lane.Name))
                throw new InvalidOperationException($"注册表存在空车道名：{path}");
            if (lane.Mode != "n/a" && !registry.Modes.ContainsKey(lane.Mode))
                throw new InvalidOperationException($"车道 {lane.Name} 的 mode「{lane.Mode}」不在 modes 中");
            foreach (var pair in lane.Overrides)
                if (!registry.Modes.ContainsKey(pair.Value))
                    throw new InvalidOperationException($"车道 {lane.Name} 的 override「{pair.Key}」指向未知 mode「{pair.Value}」");
        }
        if (registry.Lanes.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count() != registry.Lanes.Count)
            throw new InvalidOperationException($"注册表存在重复车道名：{path}");

        return registry;
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

internal sealed class LaneDef
{
    public string Name { get; init; } = "";

    public string Flag { get; init; } = "";

    public string Purpose { get; init; } = "";

    public List<string> Profiles { get; init; } = [];

    public string Mode { get; init; } = "n/a";

    public Dictionary<string, string> Overrides { get; init; } = new(StringComparer.Ordinal);

    public List<string> Filter { get; init; } = [];

    public int TimeoutMinutes { get; init; }

    public bool Remote { get; init; }

    public int ExpectedSeconds { get; init; }

    public List<string> Classes { get; init; } = [];

    public List<string> Paths { get; init; } = [];
}

internal sealed class ModeDef
{
    public int Warmup { get; init; }

    public int? Iterations { get; init; }

    public int Launch { get; init; }

    public int? Invocation { get; init; }

    public int? IterationTimeMs { get; init; }

    public int? MinIterations { get; init; }

    public int? MaxIterations { get; init; }

    public string Timing { get; init; } = "trend";
}
