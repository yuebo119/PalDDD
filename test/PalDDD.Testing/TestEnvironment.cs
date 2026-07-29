// ─────────────────────────────────────────────────────────────
// 🧪 TestEnvironment — 统一测试环境配置
// ─────────────────────────────────────────────────────────────
// 解析优先级：环境变量 > appsettings.test.local.json > appsettings.test.json > 默认值(Testcontainers)
//
// 配置文件查找路径：从 AppContext.BaseDirectory 向上回溯最多 6 层找 appsettings.test*.json。
// CI 环境（GitHub Actions）不配 appsettings.test*.json → 走 Testcontainers 默认。
// 本地开发复制 appsettings.test.json → appsettings.test.local.json 并修改连接串。
//

using System.Text.Json;

namespace PalDDD.Testing;

/// <summary>统一测试环境配置 —— 全项目所有测试桶共用。</summary>
public static class TestEnvironment
{
    private static readonly TestConfig _config = Load();

    /// <summary>PG 连接串（环境变量 PALDDD_TEST_PG 覆盖）。</summary>
    public static string PostgreSqlConnectionString =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_PG") ?? _config.PostgreSql?.ConnectionString ?? DefaultPg;

    /// <summary>MySQL 连接串（环境变量 PALDDD_TEST_MYSQL 覆盖）。</summary>
    public static string MySqlConnectionString =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_MYSQL") ?? _config.MySql?.ConnectionString ?? DefaultMySql;

    /// <summary>Kafka BootstrapServers（环境变量 PALDDD_TEST_KAFKA 覆盖）。</summary>
    public static string KafkaBootstrapServers =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_KAFKA") ?? _config.Kafka?.BootstrapServers ?? DefaultKafka;

    /// <summary>RabbitMQ Host（环境变量 PALDDD_TEST_RABBIT_HOST 覆盖）。</summary>
    public static string RabbitMqHost =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_HOST") ?? _config.RabbitMq?.Host ?? DefaultRabbitHost;

    /// <summary>RabbitMQ Port。</summary>
    public static int RabbitMqPort =>
        int.TryParse(Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_PORT"), out var p)
            ? p : _config.RabbitMq?.Port ?? 5672;

    // ── 默认值（Testcontainers 本地启动时使用）──
    private const string DefaultPg = "Host=localhost;Port=5432;Username=test;Password=test;Database=palddd_test";
    private const string DefaultMySql = "Server=localhost;Port=3306;UserID=root;Password=test;Database=palddd_test";
    private const string DefaultKafka = "localhost:9092";
    private const string DefaultRabbitHost = "localhost";

    private static TestConfig Load()
    {
        // 优先读 local 版本（gitignore），回退到模板
        var json = FindAndRead("appsettings.test.local.json")
                   ?? FindAndRead("appsettings.test.json");
        if (json is null) return new();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("TestEnvironment", out var env))
            {
                return new TestConfig
                {
                    PostgreSql = env.TryGetProperty("PostgreSql", out var pg) && pg.TryGetProperty("ConnectionString", out var pgCs) ? new() { ConnectionString = pgCs.GetString() } : null,
                    MySql = env.TryGetProperty("MySql", out var my) && my.TryGetProperty("ConnectionString", out var myCs) ? new() { ConnectionString = myCs.GetString() } : null,
                    Kafka = env.TryGetProperty("Kafka", out var k) && k.TryGetProperty("BootstrapServers", out var bs) ? new() { BootstrapServers = bs.GetString() } : null,
                    RabbitMq = env.TryGetProperty("RabbitMq", out var r) ? new()
                    {
                        Host = r.TryGetProperty("Host", out var h) ? h.GetString() : null,
                        Port = r.TryGetProperty("Port", out var p) && p.TryGetInt32(out var port) ? port : 5672
                    } : null
                };
            }
        }
        catch (JsonException) { /* 配置文件格式错误 → 用默认值 */ }
        catch (IOException) { /* 配置文件读取错误 → 用默认值 */ }

        return new();
    }

    private static string? FindAndRead(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var path = Path.Combine(dir.FullName, fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class TestConfig
    {
        public DbConfig? PostgreSql { get; init; }
        public DbConfig? MySql { get; init; }
        public KafkaConfig? Kafka { get; init; }
        public RabbitConfig? RabbitMq { get; init; }
    }

    private sealed class DbConfig
    {
        public string? ConnectionString { get; init; }
    }

    private sealed class KafkaConfig
    {
        public string? BootstrapServers { get; init; }
    }

    private sealed class RabbitConfig
    {
        public string? Host { get; init; }
        public int Port { get; init; } = 5672;
    }
}
