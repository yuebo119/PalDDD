// ─────────────────────────────────────────────────────────────
// TestEnvironment - 统一测试环境配置
// ─────────────────────────────────────────────────────────────
// 解析优先级：环境变量 > appsettings.test.local.json > appsettings.test.json > 默认值(Testcontainers)
//
// 配置文件查找路径：从 AppContext.BaseDirectory 向上回溯最多 6 层找 appsettings.test*.json。
// CI 环境（GitHub Actions）不配 appsettings.test*.json -> 走 Testcontainers 默认。
// 本地外部数据库必须显式设置 UseTestcontainers=false，并使用唯一 palddd_test_ 前缀。
// 需要清理外部数据库时还必须设置 PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP=1。

using System.Data.Common;
using System.Text.Json;

namespace PalDDD.Testing;

/// <summary>统一测试环境配置 - 全项目所有测试桶共用。</summary>
public static class TestEnvironment
{
    private static readonly TestConfig _config = Load();

    /// <summary>PG 连接串（环境变量 PALDDD_TEST_PG 覆盖）。</summary>
    public static string PostgreSqlConnectionString =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_PG") ?? _config.PostgreSql?.ConnectionString ?? DefaultPg;

    /// <summary>是否应由 Fixture 启动 PostgreSQL Testcontainers。</summary>
    public static bool UsePostgreSqlTestcontainers =>
        ResolveUseTestcontainers(_config.PostgreSql?.UseTestcontainers, Environment.GetEnvironmentVariable("PALDDD_TEST_PG"));

    /// <summary>PostgreSQL Testcontainers 镜像。</summary>
    public static string PostgreSqlImage => _config.PostgreSql?.Image ?? "postgres:18-alpine";

    /// <summary>PostgreSQL 外部测试数据库名前缀。</summary>
    public static string PostgreSqlIsolationDatabasePrefix =>
        _config.PostgreSql?.IsolationDatabasePrefix ?? "palddd_test_";

    /// <summary>MySQL 连接串（环境变量 PALDDD_TEST_MYSQL 覆盖）。</summary>
    public static string MySqlConnectionString =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_MYSQL") ?? _config.MySql?.ConnectionString ?? DefaultMySql;

    /// <summary>是否应由 Fixture 启动 MySQL Testcontainers。</summary>
    public static bool UseMySqlTestcontainers =>
        ResolveUseTestcontainers(_config.MySql?.UseTestcontainers, Environment.GetEnvironmentVariable("PALDDD_TEST_MYSQL"));

    /// <summary>MySQL Testcontainers 镜像。</summary>
    public static string MySqlImage => _config.MySql?.Image ?? "mysql:8.4";

    /// <summary>MySQL 外部测试数据库名前缀。</summary>
    public static string MySqlIsolationDatabasePrefix =>
        _config.MySql?.IsolationDatabasePrefix ?? "palddd_test_";

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

    /// <summary>RabbitMQ 用户名（环境变量 PALDDD_TEST_RABBIT_USER 覆盖）。</summary>
    public static string RabbitMqUsername =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_USER") ?? _config.RabbitMq?.Username ?? "guest";

    /// <summary>RabbitMQ 密码（环境变量 PALDDD_TEST_RABBIT_PASS 覆盖）。</summary>
    public static string RabbitMqPassword =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_PASS") ?? _config.RabbitMq?.Password ?? "guest";

    /// <summary>外部数据库清理所需的显式环境变量确认。</summary>
    public static bool ExternalDatabaseCleanupConfirmed =>
        string.Equals(Environment.GetEnvironmentVariable("PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP"), "1", StringComparison.Ordinal);

    /// <summary>
    /// 校验外部数据库是否为当前测试专用目标。数据库名必须以唯一测试前缀开头，且必须有显式清理确认。
    /// </summary>
    public static bool CanCleanExternalDatabase(string connectionString, string requiredPrefix, bool explicitConfirmation)
    {
        if (!explicitConfirmation || string.IsNullOrWhiteSpace(requiredPrefix)) return false;

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var database = GetDatabaseName(builder);
            return database is not null
                && database.StartsWith(requiredPrefix, StringComparison.Ordinal)
                && database.Length > requiredPrefix.Length;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>按显式配置、连接串覆盖和默认值决定是否启动 Testcontainers。</summary>
    public static bool ResolveUseTestcontainers(bool? configuredValue, string? connectionStringOverride)
    {
        return configuredValue ?? string.IsNullOrWhiteSpace(connectionStringOverride);
    }

    /// <summary>验证配置文件可读取且 JSON 结构有效；供纯逻辑测试使用。</summary>
    public static void ValidateConfigurationFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"测试环境配置无法读取：{Path.GetFileName(path)}。", ex);
        }

        _ = Parse(json, Path.GetFileName(path));
    }

    /// <summary>校验配置 JSON；供纯逻辑测试确认损坏配置不会回退默认值。</summary>
    public static void ValidateConfigurationJson(string json)
    {
        _ = Parse(json, "test-input");
    }

    // ── 默认值（没有配置文件时使用 Testcontainers）──
    private const string DefaultPg = "Host=localhost;Port=5432;Username=test;Password=test;Database=palddd_test";
    private const string DefaultMySql = "Server=localhost;Port=3306;UserID=root;Password=test;Database=palddd_test";
    private const string DefaultKafka = "localhost:9092";
    private const string DefaultRabbitHost = "localhost";

    private static TestConfig Load()
    {
        // 文件一旦存在，任何读取/解析错误都必须失败；只有文件不存在才允许默认容器模式。
        var path = FindPath("appsettings.test.local.json") ?? FindPath("appsettings.test.json");
        if (path is null) return new();

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"测试环境配置无法读取：{Path.GetFileName(path)}。", ex);
        }

        return Parse(json, Path.GetFileName(path));
    }

    private static TestConfig Parse(string json, string sourceName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("TestEnvironment", out var env)
                || env.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"测试环境配置缺少 TestEnvironment 对象：{sourceName}。");
            }

            return new TestConfig
            {
                PostgreSql = ParseDbConfig(env, "PostgreSql"),
                MySql = ParseDbConfig(env, "MySql"),
                Kafka = env.TryGetProperty("Kafka", out var k) ? new() { BootstrapServers = ReadString(k, "BootstrapServers") } : null,
                RabbitMq = env.TryGetProperty("RabbitMq", out var r) ? new()
                {
                    Host = ReadString(r, "Host"),
                    Port = ReadInt32(r, "Port", 5672),
                    Username = ReadString(r, "Username"),
                    Password = ReadString(r, "Password")
                } : null
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"测试环境配置 JSON 无效：{sourceName}。", ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException)
        {
            throw new InvalidOperationException($"测试环境配置字段无效：{sourceName}。", ex);
        }
    }

    private static DbConfig? ParseDbConfig(JsonElement env, string name)
    {
        if (!env.TryGetProperty(name, out var db)) return null;
        return new DbConfig
        {
            ConnectionString = ReadString(db, "ConnectionString"),
            UseTestcontainers = ReadBoolean(db, "UseTestcontainers", true),
            Image = ReadString(db, "Image"),
            IsolationDatabasePrefix = ReadString(db, "IsolationDatabasePrefix")
        };
    }

    private static string? ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.GetString() ?? throw new InvalidOperationException($"配置字段 {name} 不能为 null。");
    }

    private static bool ReadBoolean(JsonElement parent, string name, bool defaultValue)
    {
        return !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? defaultValue
            : value.GetBoolean();
    }

    private static int ReadInt32(JsonElement parent, string name, int defaultValue)
    {
        return !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? defaultValue
            : value.GetInt32();
    }

    private static string? GetDatabaseName(DbConnectionStringBuilder builder)
    {
        foreach (var key in new[] { "Database", "Initial Catalog", "Catalog" })
        {
            if (builder.TryGetValue(key, out var value) && value is string database && !string.IsNullOrWhiteSpace(database))
                return database;
        }

        return null;
    }

    private static string? FindPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var path = Path.Combine(dir.FullName, fileName);
            try
            {
                _ = File.GetAttributes(path);
                return path;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 当前层不存在配置文件，继续向父目录查找。
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"测试环境配置路径无法访问：{fileName}。", ex);
            }
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
        public bool UseTestcontainers { get; init; } = true;
        public string? Image { get; init; }
        public string? IsolationDatabasePrefix { get; init; }
    }

    private sealed class KafkaConfig
    {
        public string? BootstrapServers { get; init; }
    }

    private sealed class RabbitConfig
    {
        public string? Host { get; init; }
        public int Port { get; init; } = 5672;
        public string? Username { get; init; }
        public string? Password { get; init; }
    }
}
