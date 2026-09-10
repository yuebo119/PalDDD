// ─────────────────────────────────────────────────────────────
// TestEnvironment - 统一测试环境配置
// ─────────────────────────────────────────────────────────────
// 解析优先级：环境变量 > appsettings.test.local.json > appsettings.test.json > 默认值(Testcontainers)
// 配置文件存在但读取或解析失败时 fail-closed，不回退默认值。
// PG/MySQL Fixture 只允许 Testcontainers；外部数据库清理路径已禁用。

using System.Data.Common;
using System.Text.Json;

namespace PalDDD.Testing;

/// <summary>统一测试环境配置 - 全项目所有测试桶共用。</summary>
public static class TestEnvironment
{
    private static readonly TestConfig _config = Load();

    /// <summary>PG 连接串（环境变量 PALDDD_TEST_PG 覆盖）。仅供非 Fixture 测试配置读取。</summary>
    public static string PostgreSqlConnectionString =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_PG") ?? _config.PostgreSql?.ConnectionString ?? DefaultPg;

    /// <summary>是否应由 Fixture 启动 PostgreSQL Testcontainers。</summary>
    public static bool UsePostgreSqlTestcontainers =>
        ResolveUseTestcontainers(_config.PostgreSql?.UseTestcontainers, Environment.GetEnvironmentVariable("PALDDD_TEST_PG"));

    /// <summary>PostgreSQL Testcontainers 镜像。</summary>
    public static string PostgreSqlImage => _config.PostgreSql?.Image ?? "postgres:18-alpine";

    /// <summary>MySQL 连接串（环境变量 PALDDD_TEST_MYSQL 覆盖）。仅供非 Fixture 测试配置读取。</summary>
    public static string MySqlConnectionString =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_MYSQL") ?? _config.MySql?.ConnectionString ?? DefaultMySql;

    /// <summary>是否应由 Fixture 启动 MySQL Testcontainers。</summary>
    public static bool UseMySqlTestcontainers =>
        ResolveUseTestcontainers(_config.MySql?.UseTestcontainers, Environment.GetEnvironmentVariable("PALDDD_TEST_MYSQL"));

    /// <summary>MySQL Testcontainers 镜像。</summary>
    public static string MySqlImage => _config.MySql?.Image ?? "mysql:8.4";

    /// <summary>Kafka BootstrapServers（环境变量 PALDDD_TEST_KAFKA 覆盖）。</summary>
    public static string KafkaBootstrapServers =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_KAFKA") ?? _config.Kafka?.BootstrapServers ?? DefaultKafka;

    /// <summary>RabbitMQ Host（环境变量 PALDDD_TEST_RABBIT_HOST 覆盖）。</summary>
    public static string RabbitMqHost =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_HOST") ?? _config.RabbitMq?.Host ?? DefaultRabbitHost;

    /// <summary>RabbitMQ Port（环境变量 PALDDD_TEST_RABBIT_PORT 覆盖）。
    /// fail-closed（TST-105b）：环境变量存在但不是有效整数时抛异常，不静默回退 5672——
    /// typo（如 5673u）静默连错端口比显式失败更难排查。</summary>
    public static int RabbitMqPort
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_PORT");
            if (raw is null) return _config.RabbitMq?.Port ?? 5672;
            if (!int.TryParse(raw, out var port))
                throw new InvalidOperationException(
                    $"环境变量 PALDDD_TEST_RABBIT_PORT 的值 '{raw}' 不是有效整数端口（fail-closed，不回退默认值 5672）。");
            return port;
        }
    }

    /// <summary>RabbitMQ 用户名（环境变量 PALDDD_TEST_RABBIT_USER 覆盖）。</summary>
    public static string RabbitMqUsername =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_USER") ?? _config.RabbitMq?.Username ?? "guest";

    /// <summary>RabbitMQ 密码（环境变量 PALDDD_TEST_RABBIT_PASS 覆盖）。</summary>
    public static string RabbitMqPassword =>
        Environment.GetEnvironmentVariable("PALDDD_TEST_RABBIT_PASS") ?? _config.RabbitMq?.Password ?? "guest";

    /// <summary>
    /// 从连接串读取唯一数据库别名。Database、Initial Catalog、Catalog 只能出现一个，
    /// 即使多个别名的值相同也拒绝，避免不同 provider 对别名解析不一致。
    /// </summary>
    public static bool TryGetUniqueDatabaseName(string connectionString, out string? database)
    {
        database = null;
        try
        {
            var aliases = GetDatabaseAliases(connectionString);
            if (aliases.Count != 1) return false;

            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (!builder.TryGetValue(aliases[0], out var value)
                || value is not string name || string.IsNullOrWhiteSpace(name))
                return false;

            database = name.Trim();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>校验生成数据库名是否属于本次进程，并且只有在创建成功后才允许补偿清理。</summary>
    public static bool IsStrictGeneratedDatabaseName(string database, string prefix, bool databaseCreated)
    {
        if (!databaseCreated || string.IsNullOrWhiteSpace(prefix)
            || !database.StartsWith(prefix, StringComparison.Ordinal)
            || database.Length <= prefix.Length)
            return false;

        foreach (var character in database)
        {
            if ((character is < 'a' or > 'z') && (character is < '0' or > '9') && character != '_')
                return false;
        }

        return true;
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
            Image = ReadString(db, "Image")
        };
    }

    private static string? ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.GetString() ?? throw new InvalidOperationException($"配置字段 {name} 不能为 null。");
    }

    private static bool ReadBoolean(JsonElement parent, string name, bool defaultValue)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue;
        try
        {
            return value.GetBoolean();
        }
        catch (InvalidOperationException ex)
        {
            // P3 修复：值类型不匹配（如 "UseTestcontainers": "true"）时 S.T.J 抛的 IOE
            // 无字段指向，且被 Parse 的 catch(InvalidOperationException){throw;} 原样穿透
            //——包装为带字段名与实际类型的指向性配置错误（fail-closed 口径同 RabbitMqPort）
            throw new InvalidOperationException($"配置字段 {name} 应为布尔类型，实际为 {value.ValueKind}。", ex);
        }
    }

    private static int ReadInt32(JsonElement parent, string name, int defaultValue)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue;
        try
        {
            return value.GetInt32();
        }
        catch (InvalidOperationException ex)
        {
            // P3 修复：同 ReadBoolean——"Port": "5672" 这类字符串值须报出字段名，
            // 而非 S.T.J 内部 "requires an element of type 'Number'" 的无指向穿透
            throw new InvalidOperationException($"配置字段 {name} 应为整数类型，实际为 {value.ValueKind}。", ex);
        }
    }

    private static List<string> GetDatabaseAliases(string connectionString)
    {
        var aliases = new List<string>();
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0) continue;
            var key = segment[..separator].Trim();
            if (IsDatabaseAlias(key)) aliases.Add(key);
        }
        return aliases;
    }

    private static bool IsDatabaseAlias(string key) =>
        key.Equals("Database", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Catalog", StringComparison.OrdinalIgnoreCase);

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
